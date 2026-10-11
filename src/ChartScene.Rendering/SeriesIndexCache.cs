using System.Runtime.CompilerServices;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Numerics;

namespace ChartScene.Rendering;

public sealed partial class ChartRenderer
{
    private sealed class IndexCache
    {
        public WeakReference<SeriesSnapshot>? Snapshot;
        public DoubleRange Range;
        public int Pixels, Channel;
        public bool Logarithmic;
        public int[] Indices = [];
    }
    private readonly ConditionalWeakTable<RenderableSeries, IndexCache> _sampleCache = new();
    /// <summary>Number of private, unchanged viewport-index buffers reused in the last render.</summary>
    public int ResamplingCacheHits { get; private set; }
    /// <summary>Scalar samples visited by extrema queries in the last render; excludes one-time index construction.</summary>
    public long ResamplingScannedPoints { get; private set; }
    private void BeginResamplingFrame() { ResamplingCacheHits = 0; ResamplingScannedPoints = 0; }
    private int[] SampleIndices(RenderableSeries series, SeriesSnapshot data, DoubleRange range, int pixels, bool log)
    {
        var cache = _sampleCache.GetValue(series, static _ => new());
        if (cache.Snapshot is not null && cache.Snapshot.TryGetTarget(out var previous)
            && ReferenceEquals(previous, data) && cache.Range == range && cache.Pixels == pixels
            && cache.Channel == series.YChannel && cache.Logarithmic == log)
        {
            ResamplingCacheHits++; return cache.Indices;
        }
        var indices = IndexedResampling.MinMax(data, series.YChannel, range, pixels, out var work, log);
        ResamplingScannedPoints += work.ScannedPoints;
        if (cache.Snapshot is null) cache.Snapshot = new(data); else cache.Snapshot.SetTarget(data);
        cache.Range = range; cache.Pixels = pixels; cache.Channel = series.YChannel; cache.Logarithmic = log;
        // Never exposed to users. A changed data snapshot, X range, scale or viewport dimension
        // replaces the buffer. Weak ownership does not keep obsolete FIFO data or removed series alive.
        return cache.Indices = indices;
    }
}
