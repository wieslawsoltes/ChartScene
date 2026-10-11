using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ChartScene.Data;
using ChartScene.Core;

namespace ChartScene.Charting;

/// <summary>Legacy independently owned writable output returned by Calculate.</summary>
public sealed record StackedValues(double[] Lower, double[] Upper);

/// <summary>Immutable cumulative values and the exact data snapshot from which they were built.</summary>
public sealed class StackedSeriesValues
{
    private sealed record Extent(DoubleRange? Value);
    private readonly Extent?[] _extents=new Extent[2];
    public SeriesSnapshot Snapshot { get; }
    public IReadOnlyList<double> Lower { get; }
    public IReadOnlyList<double> Upper { get; }
    internal StackedSeriesValues(SeriesSnapshot snapshot, double[] lower, double[] upper)
    { Snapshot = snapshot; Lower = Array.AsReadOnly(lower); Upper = Array.AsReadOnly(upper); }
    /// <summary>Cached full-column bounds; bounded queries filter the exact captured X snapshot.</summary>
    public DoubleRange? GetRange(DoubleRange? xRange=null,bool positiveOnly=false)
    {
        if(xRange is { IsValid:false }) throw new ArgumentOutOfRangeException(nameof(xRange));
        int slot=positiveOnly?1:0;
        if(xRange is null&&Volatile.Read(ref _extents[slot]) is { } cached) return cached.Value;
        var (start,end)=xRange is { } range?Snapshot.VisibleIndices(range,false):(0,Snapshot.Count);
        double min=double.PositiveInfinity,max=double.NegativeInfinity;
        void Include(double value)
        {
            if(!double.IsFinite(value)||positiveOnly&&value<=0) return;
            min=Math.Min(min,value); max=Math.Max(max,value);
        }
        for(int i=start;i<end;i++)
        {
            if(xRange is { } visible&&!visible.Contains(Snapshot[0,i])) continue;
            Include(Lower[i]); Include(Upper[i]);
        }
        DoubleRange? result=null;
        if(double.IsFinite(min))
        {
            if(positiveOnly&&min==max)
            {
                double lo=min*.95,hi=min*1.05;
                if(lo<=0) lo=min;
                if(!double.IsFinite(hi)) hi=min;
                if(hi==lo) hi=Math.BitIncrement(min);
                result=new(lo,hi);
            }
            else result=DoubleRange.FromExtents(min,max);
        }
        if(xRange is null) Interlocked.CompareExchange(ref _extents[slot],new Extent(result),null);
        return result;
    }
}

/// <summary>One immutable, shared generation. Camera, label, theme and cursor changes can reuse it.</summary>
public sealed class StackedFrame : IReadOnlyDictionary<RenderableSeries, StackedSeriesValues>
{
    private readonly ReadOnlyDictionary<RenderableSeries, StackedSeriesValues> _values;
    public long BuildNumber { get; }
    public long SourcePoints { get; }
    public long ArrayPayloadBytes => SourcePoints * sizeof(double) * 2;
    internal StackedFrame(Dictionary<RenderableSeries, StackedSeriesValues> values, long build, long points)
    { _values = new(values); BuildNumber = build; SourcePoints = points; }
    public int Count => _values.Count;
    public IEnumerable<RenderableSeries> Keys => _values.Keys;
    public IEnumerable<StackedSeriesValues> Values => _values.Values;
    public StackedSeriesValues this[RenderableSeries key] => _values[key];
    public bool ContainsKey(RenderableSeries key) => _values.ContainsKey(key);
    public bool TryGetValue(RenderableSeries key, [MaybeNullWhen(false)] out StackedSeriesValues value) => _values.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<RenderableSeries, StackedSeriesValues>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class StackCalculator
{
    private sealed record Input(RenderableSeries Series, IDataSeries Source, SeriesSnapshot Snapshot,
        string XAxis, string YAxis, string Group, StackMode Mode)
    {
        public bool Matches(RenderableSeries series) => ReferenceEquals(series, Series)
            && ReferenceEquals(series.DataSeries, Source) && series.XAxisId == XAxis && series.YAxisId == YAxis
            && series.StackedGroupId == Group && series.StackMode == Mode
            && ReferenceEquals(Source.Snapshot(), Snapshot);
    }
    private sealed class Cache
    {
        public readonly object Gate = new();
        public Input[] Inputs = [];
        public StackedFrame? Frame;
        public long Builds;
    }
    private struct SignedTotals
    {
        public double PositiveScale, PositiveSum, NegativeScale, NegativeSum;
        private static void Add(ref double scale, ref double sum, double magnitude)
        {
            if (magnitude == 0) return;
            if (magnitude > scale) { sum = sum * (scale / magnitude) + 1; scale = magnitude; }
            else sum += magnitude / scale;
        }
        public void Include(double value)
        {
            if (value >= 0) Add(ref PositiveScale, ref PositiveSum, value);
            else Add(ref NegativeScale, ref NegativeSum, -value);
        }
        public readonly double Percentage(double value)
        {
            double scale = value >= 0 ? PositiveScale : NegativeScale;
            double sum = value >= 0 ? PositiveSum : NegativeSum;
            return scale > 0 ? ((value / scale) / sum) * 100 : 0;
        }
    }
    private static readonly ConditionalWeakTable<ChartModel, Cache> Caches = new();
    private static bool Eligible(RenderableSeries series) => series.IsVisible && series.IsStacked && series.DataSeries is not null;

    /// <summary>
    /// Captures current stack inputs and shares read-only output across render/range/rollover clients.
    /// Reuse is keyed by ordered series identity, source snapshot, axes, group and stack mode, not
    /// the model's presentation revision. Model collection edits require the owning UI thread.
    /// Immutable input readers may share a capture concurrently. Expired models are weakly owned.
    /// </summary>
    public static StackedFrame Capture(ChartModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var cache = Caches.GetValue(model, static _ => new());
        lock (cache.Gate)
        {
            int position = 0; bool match = cache.Frame is not null;
            for (int index = 0; index < model.RenderableSeries.Count; index++)
            {
                var series = model.RenderableSeries[index];
                if (!Eligible(series)) continue;
                if (position >= cache.Inputs.Length || !cache.Inputs[position].Matches(series)) match = false;
                position++;
            }
            if (match && position == cache.Inputs.Length) return cache.Frame!;
            var inputs = new List<Input>(position);
            foreach (var series in model.RenderableSeries)
                if (Eligible(series))
                {
                    var source = series.DataSeries!;
                    inputs.Add(new(series, source, source.Snapshot(), series.XAxisId, series.YAxisId, series.StackedGroupId, series.StackMode));
                }
            // Build to completion before publishing; a diagnosed overflow never poisons the old generation.
            var frame = Build(inputs, checked(cache.Builds + 1));
            cache.Inputs = inputs.ToArray(); cache.Frame = frame; cache.Builds = frame.BuildNumber;
            return frame;
        }
    }
    /// <summary>Compatibility API: callers receive fresh writable copies, never cached internal arrays.</summary>
    public static Dictionary<RenderableSeries, StackedValues> Calculate(ChartModel model)
        => Capture(model).ToDictionary(pair => pair.Key, pair => new StackedValues(pair.Value.Lower.ToArray(), pair.Value.Upper.ToArray()));
    /// <summary>Releases retained stack snapshots, e.g. when a model is permanently disposed.</summary>
    public static void Release(ChartModel model)
    { ArgumentNullException.ThrowIfNull(model); Caches.Remove(model); }

    private static StackedFrame Build(List<Input> inputs, long buildNumber)
    {
        var result = new Dictionary<RenderableSeries, StackedSeriesValues>(); long points = 0;
        foreach (var group in inputs.GroupBy(input => (input.XAxis, input.YAxis, input.Group, input.Mode)))
        {
            Dictionary<double, SignedTotals>? total = null;
            if (group.Key.Mode == StackMode.Percent)
            {
                total = new();
                foreach (var input in group)
                {
                    var snapshot = input.Snapshot;
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        double x = snapshot[0, i], y = snapshot[1, i]; if (!double.IsFinite(y)) continue;
                        var sums = total.GetValueOrDefault(x); sums.Include(y); total[x] = sums;
                    }
                }
            }
            var running = new Dictionary<double, (double Positive, double Negative)>();
            foreach (var input in group)
            {
                var data = input.Snapshot; points += data.Count;
                var lower = new double[data.Count]; var upper = new double[data.Count];
                for (int i = 0; i < data.Count; i++)
                {
                    double x = data[0, i], y = data[1, i];
                    if (!double.IsFinite(y)) { lower[i] = upper[i] = double.NaN; continue; }
                    bool positive = y >= 0;
                    if (total is not null) y = total[x].Percentage(y);
                    var accumulated = running.GetValueOrDefault(x);
                    lower[i] = positive ? accumulated.Positive : accumulated.Negative;
                    double end = lower[i] + y;
                    if (!double.IsFinite(end)) throw new InvalidOperationException("Cumulative stack values exceed the finite double range. Rescale the data or use percent stacking.");
                    upper[i] = end;
                    running[x] = positive ? (end, accumulated.Negative) : (accumulated.Positive, end);
                }
                result.Add(input.Series, new(data, lower, upper));
            }
        }
        return new(result, buildNumber, points);
    }
}
