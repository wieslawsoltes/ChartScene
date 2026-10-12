using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Rendering;
using SkiaSharp;

namespace ChartScene.Rendering.Skia;

public readonly record struct ThumbnailCacheStatistics(long Builds, long Hits, long Evictions, int Entries, long RetainedPixelBytes);
public readonly record struct ThumbnailResult(bool CacheHit, long SourcePoints, long RenderedPoints);

/// <summary>
/// Render-thread-owned LRU of real chart thumbnails. Retains only raster images and factory keys,
/// never created chart models/data. A factory must return a new, exclusively owned ChartModel.
/// Increase contentRevision when a factory's output changes. Clear/dispose on the owner's unload.
/// </summary>
public sealed class ChartThumbnailCache : IDisposable
{
    private readonly record struct Key(Func<ChartModel> Factory, ChartTheme Theme, int Width, int Height, long Revision);
    private sealed record Entry(Key Key, SKImage Image, ColorRgba[] Palette, RenderStatistics Statistics);
    private readonly Dictionary<Key, LinkedListNode<Entry>> _cache = [];
    private readonly LinkedList<Entry> _lru = [];
    private readonly long _budget;
    private readonly int _entryLimit;
    private long _bytes, _builds, _hits, _evictions;
    private bool _disposed, _rendering;
    public ThumbnailCacheStatistics Statistics => new(_builds, _hits, _evictions, _cache.Count, _bytes);

    /// <param name="pixelByteBudget">Budget for retained width × height × 4 payload, not total process/native memory.</param>
    /// <param name="entryLimit">Additional bound on retained factory keys and images.</param>
    public ChartThumbnailCache(long pixelByteBudget = 8 * 1024 * 1024, int entryLimit = 32)
    {
        if (pixelByteBudget < 0) throw new ArgumentOutOfRangeException(nameof(pixelByteBudget));
        if (entryLimit < 1 || entryLimit > 4096) throw new ArgumentOutOfRangeException(nameof(entryLimit));
        _budget = pixelByteBudget; _entryLimit = entryLimit;
    }

    /// <summary>
    /// Draws an aspect-fitted miniature from the same chart renderer. The fresh model's title,
    /// subtitle, legend and axis titles are omitted to make the plotted data readable at small sizes.
    /// Unobservable factory changes require contentRevision; palette mutations are checked explicitly.
    /// Host save/transform/clip state is restored on every path. No host canvas is retained.
    /// </summary>
    public ThumbnailResult Draw(SKCanvas canvas, Func<ChartModel> factory, ChartTheme theme, RectD destination,
        int pixelWidth = 320, int pixelHeight = 180, long contentRevision = 0)
    {
        Check(); ArgumentNullException.ThrowIfNull(canvas); ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(theme);
        if (pixelWidth < 32 || pixelHeight < 32 || pixelWidth > 2048 || pixelHeight > 2048)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (destination.IsEmpty || !float.IsFinite((float)destination.X) || !float.IsFinite((float)destination.Y)
            || !float.IsFinite((float)destination.Right) || !float.IsFinite((float)destination.Bottom)
            || !float.IsFinite((float)destination.Width) || !float.IsFinite((float)destination.Height))
            throw new ArgumentOutOfRangeException(nameof(destination));
        if (theme.Palette is null || theme.Palette.Count == 0) throw new ArgumentException("A thumbnail theme requires a palette.", nameof(theme));
        var key = new Key(factory, theme, pixelWidth, pixelHeight, contentRevision);
        _rendering = true; SKImage? transient = null;
        try
        {
            bool hit = _cache.TryGetValue(key, out var node) && SamePalette(node.Value.Palette, theme.Palette);
            if (node is not null && !hit) Remove(node);
            Entry entry;
            if (hit)
            {
                entry = node!.Value; _lru.Remove(node); _lru.AddFirst(node); _hits++;
            }
            else
            {
                var palette = theme.Palette.ToArray();
                var frozenTheme = theme with { Palette = Array.AsReadOnly(palette) };
                using var model = factory() ?? throw new InvalidOperationException("The thumbnail factory returned no chart model.");
                var original = model.Theme;
                model.Title = model.Subtitle = ""; model.ShowLegend = false; model.Theme = frozenTheme;
                foreach (var axis in model.XAxes.Concat(model.YAxes)) axis.AxisTitle = "";
                ColorRgba Recolor(ColorRgba color)
                {
                    for (int i = 0; i < original.Palette.Count; i++)
                        if ((original.Palette[i] with { A = color.A }) == color) return palette[i % palette.Length].WithAlpha(color.A);
                    return color;
                }
                foreach (var series in model.RenderableSeries)
                {
                    series.Stroke = Recolor(series.Stroke); series.Fill = Recolor(series.Fill);
                    series.StrokeY1 = Recolor(series.StrokeY1); series.FillY1 = Recolor(series.FillY1);
                }
                using var surface = SKSurface.Create(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
                    ?? throw new InvalidOperationException("Could not allocate a chart thumbnail.");
                using var resources = new SkiaResources();
                var frame = new ChartRenderer().Render(new SkiaChartCanvas(surface.Canvas, resources), model, pixelWidth, pixelHeight);
                if (frame.Diagnostics.Count != 0) throw new InvalidOperationException(string.Join("\n", frame.Diagnostics));
                transient = surface.Snapshot();
                entry = new(key, transient, palette, frame.Statistics); _builds++;
                long bytes = (long)pixelWidth * pixelHeight * 4;
                if (bytes <= _budget)
                {
                    while (_cache.Count >= _entryLimit || _bytes > _budget - bytes) Remove(_lru.Last!);
                    var added = _lru.AddFirst(entry); _cache.Add(key, added); _bytes += bytes; transient = null;
                }
            }
            double scale = Math.Min(destination.Width / pixelWidth, destination.Height / pixelHeight);
            double width = pixelWidth * scale, height = pixelHeight * scale;
            var target = new SKRect((float)(destination.Center.X - width / 2), (float)(destination.Center.Y - height / 2),
                (float)(destination.Center.X + width / 2), (float)(destination.Center.Y + height / 2));
            int saved = canvas.Save();
            try
            {
                canvas.ClipRect(new((float)destination.X, (float)destination.Y, (float)destination.Right, (float)destination.Bottom));
                canvas.DrawImage(entry.Image, target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }
            finally { canvas.RestoreToCount(saved); }
            return new(hit, entry.Statistics.SourcePoints, entry.Statistics.RenderedPoints);
        }
        finally { transient?.Dispose(); _rendering = false; }
    }
    private static bool SamePalette(ColorRgba[] a, IReadOnlyList<ColorRgba> b)
    {
        if (a.Length != b.Count) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_rendering) throw new InvalidOperationException("A thumbnail cache cannot be reentered by its factory.");
    }
    private void Remove(LinkedListNode<Entry> node)
    {
        _cache.Remove(node.Value.Key); _lru.Remove(node);
        _bytes -= (long)node.Value.Key.Width * node.Value.Key.Height * 4;
        node.Value.Image.Dispose(); _evictions++;
    }
    public void Clear()
    {
        Check();
        while (_lru.Last is { } node) Remove(node);
    }
    public void Dispose()
    {
        if (_disposed) return;
        Clear(); _disposed = true;
    }
}
