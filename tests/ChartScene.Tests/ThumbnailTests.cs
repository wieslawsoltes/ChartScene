using System.Runtime.CompilerServices;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using ChartScene.Samples;
using SkiaSharp;

public static class ThumbnailTests
{
    private static ChartModel Model()
    {
        var model = new ChartModel();
        var data = new XyDataSeries(); data.AppendRange([0, 1, 2], [1, 3, 2]);
        model.RenderableSeries.Add(new FastLineRenderableSeries { DataSeries = data });
        return model;
    }
    public static void Register(TestSuite suite)
    {
        suite.Run("thumbnail warm draws reuse pixels without invoking factory", () =>
        {
            using var cache = new ChartThumbnailCache(); using var surface = SKSurface.Create(new SKImageInfo(400, 300));
            int calls = 0; Func<ChartModel> factory = () => { calls++; return Model(); };
            var initial = cache.Draw(surface.Canvas, factory, ChartTheme.Dark, new(0, 0, 400, 300));
            for (int i = 0; i < 20; i++) TestSuite.True(cache.Draw(surface.Canvas, factory, ChartTheme.Dark, new(0, 0, 200, 130)).CacheHit);
            TestSuite.Equal(calls, 1); TestSuite.Equal(initial.SourcePoints, 3L);
            TestSuite.Equal(cache.Statistics.Entries, 1); TestSuite.Equal(cache.Statistics.RetainedPixelBytes, 320L * 180 * 4);
        });
        suite.Run("thumbnail invalidation covers theme pixels revision and factory", () =>
        {
            using var cache = new ChartThumbnailCache(); using var surface = SKSurface.Create(new SKImageInfo(400, 300));
            Func<ChartModel> factory = Model; var r = new RectD(0, 0, 300, 180);
            cache.Draw(surface.Canvas, factory, ChartTheme.Dark, r);
            TestSuite.True(!cache.Draw(surface.Canvas, factory, ChartTheme.Light, r).CacheHit);
            TestSuite.True(!cache.Draw(surface.Canvas, factory, ChartTheme.Dark, r, 640, 360).CacheHit);
            TestSuite.True(!cache.Draw(surface.Canvas, factory, ChartTheme.Dark, r, contentRevision: 1).CacheHit);
            TestSuite.True(!cache.Draw(surface.Canvas, () => Model(), ChartTheme.Dark, r).CacheHit);
            var palette = ChartTheme.Dark.Palette.ToArray(); var mutable = ChartTheme.Dark with { Palette = palette };
            cache.Draw(surface.Canvas, factory, mutable, r); palette[0] = ColorRgba.White;
            TestSuite.True(!cache.Draw(surface.Canvas, factory, mutable, r).CacheHit);
        });
        suite.Run("thumbnail LRU evicts least recently drawn factory", () =>
        {
            using var cache = new ChartThumbnailCache(1024 * 1024, 2); using var surface = SKSurface.Create(new SKImageInfo(100, 100));
            Func<ChartModel> a = () => Model(), b = () => Model(), c = () => Model(); var r = new RectD(0, 0, 100, 100);
            cache.Draw(surface.Canvas, a, ChartTheme.Dark, r); cache.Draw(surface.Canvas, b, ChartTheme.Dark, r);
            TestSuite.True(cache.Draw(surface.Canvas, a, ChartTheme.Dark, r).CacheHit);
            cache.Draw(surface.Canvas, c, ChartTheme.Dark, r);
            TestSuite.Equal(cache.Statistics.Entries, 2); TestSuite.Equal(cache.Statistics.Evictions, 1L);
            TestSuite.True(cache.Draw(surface.Canvas, a, ChartTheme.Dark, r).CacheHit);
            TestSuite.True(!cache.Draw(surface.Canvas, b, ChartTheme.Dark, r).CacheHit);
        });
        suite.Run("thumbnail pixel budget and zero-budget bypass stay bounded", () =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(100, 100)); var r = new RectD(0, 0, 100, 100);
            foreach (long budget in new[] { 0L, 320 * 180 * 4L, 2 * 320 * 180 * 4L })
            {
                using var cache = new ChartThumbnailCache(budget);
                for (int i = 0; i < 6; i++)
                {
                    cache.Draw(surface.Canvas, Model, ChartTheme.Dark, r, contentRevision: i);
                    TestSuite.True(cache.Statistics.RetainedPixelBytes <= budget);
                }
                cache.Clear(); TestSuite.Equal(cache.Statistics.RetainedPixelBytes, 0L);
                TestSuite.Equal(cache.Statistics.Entries, 0);
            }
        });
        suite.Run("thumbnail factory failures preserve canvas and can recover", () =>
        {
            using var cache = new ChartThumbnailCache(); using var surface = SKSurface.Create(new SKImageInfo(100, 100));
            var r = new RectD(0, 0, 100, 100); int save = surface.Canvas.SaveCount;
            TestSuite.Throws<InvalidOperationException>(() => cache.Draw(surface.Canvas, () => throw new InvalidOperationException("test"), ChartTheme.Dark, r));
            TestSuite.Throws<InvalidOperationException>(() => cache.Draw(surface.Canvas, () => null!, ChartTheme.Dark, r));
            TestSuite.Throws<InvalidOperationException>(() => cache.Draw(surface.Canvas, () => { var m = Model(); m.XAxes.Clear(); return m; }, ChartTheme.Dark, r));
            TestSuite.Equal(cache.Statistics.Entries, 0); TestSuite.Equal(surface.Canvas.SaveCount, save);
            cache.Draw(surface.Canvas, Model, ChartTheme.Dark, r); TestSuite.Equal(cache.Statistics.Builds, 1L);
        });
        suite.Run("thumbnail rejects invalid configuration before factory or drawing", () =>
        {
            using var cache = new ChartThumbnailCache(); using var surface = SKSurface.Create(new SKImageInfo(100, 100));
            int calls = 0; Func<ChartModel> factory = () => { calls++; return Model(); };
            TestSuite.Throws<ArgumentOutOfRangeException>(() => cache.Draw(surface.Canvas, factory, ChartTheme.Dark, default));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => cache.Draw(surface.Canvas, factory, ChartTheme.Dark, new(0, 0, 20, 20), 5000));
            TestSuite.Throws<ArgumentException>(() => cache.Draw(surface.Canvas, factory, ChartTheme.Dark with { Palette = [] }, new(0, 0, 20, 20)));
            TestSuite.Equal(calls, 0);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => new ChartThumbnailCache(-1));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => new ChartThumbnailCache(entryLimit: 0));
        });
        suite.Run("thumbnail cache rejects reentrancy and does not resurrect after disposal", () =>
        {
            var cache = new ChartThumbnailCache(); using var surface = SKSurface.Create(new SKImageInfo(100, 100)); var r = new RectD(0, 0, 100, 100);
            TestSuite.Throws<InvalidOperationException>(() => cache.Draw(surface.Canvas, () => { cache.Clear(); return Model(); }, ChartTheme.Dark, r));
            cache.Draw(surface.Canvas, Model, ChartTheme.Dark, r); cache.Dispose(); cache.Dispose();
            TestSuite.Equal(cache.Statistics.Entries, 0); TestSuite.Equal(cache.Statistics.RetainedPixelBytes, 0L);
            TestSuite.Throws<ObjectDisposedException>(() => cache.Draw(surface.Canvas, Model, ChartTheme.Dark, r));
            surface.Canvas.Clear(SKColors.Red); // Host remains owned by caller.
        });
        suite.Run("thumbnail cache does not retain the created chart model", () =>
        {
            using var cache = new ChartThumbnailCache(); using var surface = SKSurface.Create(new SKImageInfo(100, 100));
            var weak = RenderCollectible(cache, surface.Canvas);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            TestSuite.True(!weak.TryGetTarget(out _));
        });
        foreach (var example in SampleCatalog.Examples)
            suite.Run("real thumbnail fixture " + example.Id, () =>
            {
                using var cache = new ChartThumbnailCache(); using var surface = SKSurface.Create(new SKImageInfo(320, 180));
                var result = cache.Draw(surface.Canvas, example.Create, ChartTheme.Dark, new(0, 0, 320, 180));
                TestSuite.True(result.SourcePoints > 0 && result.RenderedPoints > 0);
                using var image = surface.Snapshot(); using var bitmap = SKBitmap.FromImage(image);
                int colored = 0;
                for (int y = 0; y < 180; y++) for (int x = 0; x < 320; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    if (Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue)) > 45
                        && Math.Max(c.Red, Math.Max(c.Green, c.Blue)) > 100) colored++;
                }
                TestSuite.True(colored > 10);
                Directory.CreateDirectory("artifacts/thumbnails");
                using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes("artifacts/thumbnails/" + example.Id + ".png", png.ToArray());
                TestSuite.True(cache.Draw(surface.Canvas, example.Create, ChartTheme.Dark, new(0, 0, 320, 180)).CacheHit);
            });
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<ChartModel> RenderCollectible(ChartThumbnailCache cache, SKCanvas canvas)
    {
        WeakReference<ChartModel>? weak = null;
        cache.Draw(canvas, () => { var m = Model(); weak = new(m); return m; }, ChartTheme.Dark, new(0, 0, 100, 100));
        return weak!;
    }
}
