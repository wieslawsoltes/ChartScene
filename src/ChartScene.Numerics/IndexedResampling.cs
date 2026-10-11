using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ChartScene.Core;
using ChartScene.Data;

namespace ChartScene.Numerics;

/// <summary>Deterministic work counters, not a GPU timing or frame-rate measurement.</summary>
public readonly record struct ResamplingWork(int SourcePoints, int ScannedPoints, int VisitedNodes, int AcceptedNodes, int OutputPoints);

/// <summary>
/// Immutable, reusable extrema pyramid over an immutable data snapshot. Aligned blocks retain
/// the earliest min/max indices and whether a block contains a gap. Queries accept a whole
/// subtree only when all its samples occupy one screen bucket and contain no gaps. Everything
/// else is refined, preserving exactly the scan algorithm's first/min/max/last source indices.
/// Camera queries do not rebuild the index. Construction is O(N); storage is O(N / 32).
/// </summary>
public sealed class MinMaxIndex
{
    private const int BlockSize = 32;
    private readonly SeriesSnapshot _data;
    private readonly int _channel, _leaves;
    private readonly int[] _minimum, _maximum;
    private readonly bool[] _gaps;
    public int Count => _data.Count;
    public long StorageBytes => (long)_minimum.Length * (sizeof(int) * 2 + sizeof(bool));

    public MinMaxIndex(SeriesSnapshot data, int yChannel = 1)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!data.IsSortedAscending) throw new ArgumentException("An extrema index requires sorted X data.", nameof(data));
        if ((uint)yChannel >= (uint)data.ChannelCount) throw new ArgumentOutOfRangeException(nameof(yChannel));
        _data = data; _channel = yChannel;
        int blocks = (int)(((long)data.Count + BlockSize - 1) / BlockSize);
        _leaves = 1;
        while (_leaves < blocks) _leaves = checked(_leaves * 2);
        _minimum = new int[checked(_leaves * 2)]; _maximum = new int[_minimum.Length]; _gaps = new bool[_minimum.Length];
        Array.Fill(_minimum, -1); Array.Fill(_maximum, -1);
        var values = data.Channel(yChannel).Span;
        for (int block = 0; block < blocks; block++)
        {
            int node = _leaves + block, end = (int)Math.Min(data.Count, ((long)block + 1) * BlockSize);
            for (int i = block * BlockSize; i < end; i++)
            {
                if (!double.IsFinite(values[i])) { _gaps[node] = true; continue; }
                if (_minimum[node] < 0 || values[i] < values[_minimum[node]]) _minimum[node] = i;
                if (_maximum[node] < 0 || values[i] > values[_maximum[node]]) _maximum[node] = i;
            }
        }
        for (int node = _leaves - 1; node > 0; node--)
        {
            int a = node * 2, b = a + 1;
            _minimum[node] = Choose(_minimum[a], _minimum[b], minimum: true);
            _maximum[node] = Choose(_maximum[a], _maximum[b], minimum: false);
            _gaps[node] = _gaps[a] || _gaps[b];
        }
    }
    private int Choose(int a, int b, bool minimum)
    {
        if (a < 0) return b;
        if (b < 0) return a;
        double x = _data[_channel, a], y = _data[_channel, b];
        return minimum ? (x <= y ? a : b) : (x >= y ? a : b);
    }

    /// <summary>
    /// Uses data-space clipping and screen-space buckets. Logarithmic X buckets are uniform in
    /// logarithmic coordinates, not raw data. Every non-finite Y sample remains a path break.
    /// Returned arrays belong to the caller; cached index storage is never exposed.
    /// </summary>
    public int[] Sample(DoubleRange visible, int pixels, out ResamplingWork work, bool logarithmicX = false)
    {
        IndexedResampling.Validate(_data, _channel, visible, pixels, logarithmicX);
        var (start, end) = _data.VisibleIndices(visible);
        if (end - start <= (long)pixels * 4)
        {
            var all = IndexedResampling.All(start, end);
            work = new(end - start, 0, 0, 0, all.Length); return all;
        }
        var query = new Query(_data, _channel, visible, pixels, logarithmicX, end - start);
        int visited = 0, accepted = 0;
        void Visit(int node, int firstBlock, int blockCount)
        {
            int first = (int)Math.Min(_data.Count, (long)firstBlock * BlockSize);
            int last = (int)Math.Min(_data.Count, ((long)firstBlock + blockCount) * BlockSize);
            if (last <= start || first >= end || last <= first) return;
            visited++;
            if (first >= start && last <= end && !_gaps[node] && query.Bucket(first) == query.Bucket(last - 1))
            {
                query.Accept(first, last - 1, _minimum[node], _maximum[node]); accepted++; return;
            }
            if (blockCount == 1)
            { query.Scan(Math.Max(start, first), Math.Min(end, last)); return; }
            int half = blockCount / 2;
            Visit(node * 2, firstBlock, half); Visit(node * 2 + 1, firstBlock + half, half);
        }
        Visit(1, 0, _leaves);
        int[] result = query.Finish();
        work = new(end - start, query.Scanned, visited, accepted, result.Length);
        return result;
    }

    internal sealed class Query
    {
        private readonly SeriesSnapshot _data;
        private readonly int _channel, _pixels;
        private readonly DoubleRange _range;
        private readonly bool _log;
        private readonly double _logSize;
        private readonly List<int> _result;
        private int _bucket = int.MinValue, _first = -1, _last = -1, _min = -1, _max = -1;
        public int Scanned { get; private set; }
        public Query(SeriesSnapshot data, int channel, DoubleRange range, int pixels, bool log, int count)
        {
            _data = data; _channel = channel; _range = range; _pixels = pixels; _log = log;
            _logSize = log ? LogRatio(range.Max, range.Min) : 0;
            _result = new((int)Math.Min(count, (long)pixels * 4 + 8));
        }
        // LogP1 preserves adjacent-value ratios when subtracting logarithms would cancel.
        private static double LogRatio(double value, double minimum)
        {
            double relative = (value - minimum) / minimum;
            return relative < .5 ? double.LogP1(relative) : Math.Log(value) - Math.Log(minimum);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Bucket(int index)
        {
            double x = _data[0, index];
            if (x < _range.Min) return -1;
            if (x >= _range.Max) return _pixels;
            double fraction = _log ? LogRatio(x, _range.Min) / _logSize : _range.Fraction(x);
            return (int)Math.Clamp(Math.Floor(fraction * _pixels), -1, _pixels);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Accept(int first, int last, int minimum, int maximum)
        {
            int bucket = Bucket(first);
            if (bucket != _bucket) { Flush(); _bucket = bucket; }
            if (_first < 0) { _first = first; _min = minimum; _max = maximum; }
            else
            {
                if (_data[_channel, minimum] < _data[_channel, _min]) _min = minimum;
                if (_data[_channel, maximum] > _data[_channel, _max]) _max = maximum;
            }
            _last = last;
        }
        public void Scan(int start, int end)
        {
            Scanned += end - start;
            for (int i = start; i < end; i++)
            {
                if (!double.IsFinite(_data[_channel, i]))
                { Flush(); _result.Add(i); _bucket = int.MinValue; }
                else Accept(i, i, i, i);
            }
        }
        private void Flush()
        {
            if (_first < 0) return;
            Span<int> candidates = stackalloc int[] { _first, _min, _max, _last }; candidates.Sort();
            foreach (int index in candidates)
                if (_result.Count == 0 || _result[^1] != index) _result.Add(index);
            _first = _last = _min = _max = -1;
        }
        public int[] Finish() { Flush(); return _result.ToArray(); }
    }
}

/// <summary>
/// Automatic weakly owned per-snapshot/channel indexing. No global strong references retain
/// expired FIFO snapshots. Lazy construction prevents concurrent readers duplicating an index.
/// Small or unsorted inputs avoid index construction; unsorted data is never reordered.
/// </summary>
public static class IndexedResampling
{
    private sealed class Cache
    {
        public ConcurrentDictionary<int, Lazy<MinMaxIndex>> Channels { get; } = new();
    }
    private static readonly ConditionalWeakTable<SeriesSnapshot, Cache> Caches = new();
    public static int[] MinMax(SeriesSnapshot data, int yChannel, DoubleRange visible, int pixels,
        bool logarithmicX = false) => MinMax(data, yChannel, visible, pixels, out _, logarithmicX);
    public static int[] MinMax(SeriesSnapshot data, int yChannel, DoubleRange visible, int pixels,
        out ResamplingWork work, bool logarithmicX = false)
    {
        Validate(data, yChannel, visible, pixels, logarithmicX);
        var (start, end) = data.VisibleIndices(visible);
        if (!data.IsSortedAscending || end - start <= (long)pixels * 4)
        {
            var all = All(start, end); work = new(end - start, 0, 0, 0, all.Length); return all;
        }
        if (data.Count < 16384 || end - start < 16384)
        {
            var query = new MinMaxIndex.Query(data, yChannel, visible, pixels, logarithmicX, end - start);
            query.Scan(start, end); var result = query.Finish();
            work = new(end - start, end - start, 0, 0, result.Length); return result;
        }
        var cache = Caches.GetValue(data, static _ => new());
        var lazy = cache.Channels.GetOrAdd(yChannel, channel => new(() => new MinMaxIndex(data, channel), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value.Sample(visible, pixels, out work, logarithmicX);
    }
    internal static void Validate(SeriesSnapshot data, int channel, DoubleRange visible, int pixels, bool log)
    {
        ArgumentNullException.ThrowIfNull(data);
        if ((uint)channel >= (uint)data.ChannelCount) throw new ArgumentOutOfRangeException(nameof(channel));
        if (!visible.IsValid || log && visible.Min <= 0) throw new ArgumentOutOfRangeException(nameof(visible));
        if (pixels < 1) throw new ArgumentOutOfRangeException(nameof(pixels));
    }
    internal static int[] All(int start, int end)
    {
        int[] result = new int[end - start];
        for (int i = 0; i < result.Length; i++) result[i] = start + i;
        return result;
    }
}
