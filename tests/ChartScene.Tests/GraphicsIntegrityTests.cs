using System.Diagnostics;
using System.Text.Json;
using ChartScene.Core;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using ChartScene.ThreeD;
using SkiaSharp;

public static class GraphicsIntegrityTests
{
    public static void Register(TestSuite suite)
    {
        suite.Run("Skia font cache prunes variable-size native resources between frames", () =>
        {
            using var resources = new SkiaResources(); using var surface = SKSurface.Create(new SKImageInfo(400, 200));
            var canvas = new SkiaChartCanvas(surface.Canvas, resources);
            for (int i = 0; i < 300; i++)
            {
                resources.BeginFrame(); canvas.DrawText("Font lifecycle Ω", new(10, 80), new(ColorRgba.White, 10 + i * .125));
                TestSuite.True(resources.CacheStatistics.Fonts <= 65);
            }
            resources.BeginFrame(); TestSuite.True(resources.CacheStatistics.Fonts <= 64);
            TestSuite.True(resources.CacheStatistics.TextBlobs <= 64);
            canvas.DrawText("Still renders", new(10, 100), new(ColorRgba.White, 16));
            using var image = surface.Snapshot(); TestSuite.Equal(image.Width, 400);
        });
        suite.Run("Skia disposal releases caches and cannot resurrect resources", () =>
        {
            var resources = new SkiaResources(); using var surface = SKSurface.Create(new SKImageInfo(100, 100));
            var canvas = new SkiaChartCanvas(surface.Canvas, resources);
            canvas.DrawText("cached", new(10, 20), new(ColorRgba.White, 12));
            canvas.DrawLine(new(0, 0), new(20, 20), new(ColorRgba.White));
            TestSuite.True(resources.CacheStatistics.Fonts > 0);
            resources.Dispose(); resources.Dispose(); TestSuite.Equal(resources.CacheStatistics, default(SkiaCacheStatistics));
            TestSuite.Throws<ObjectDisposedException>(() => resources.BeginFrame());
            TestSuite.Throws<ObjectDisposedException>(() => canvas.DrawText("cached", new(10, 20), new(ColorRgba.White, 12)));
            TestSuite.Throws<ObjectDisposedException>(() => canvas.DrawRectangle(new(0, 0, 10, 10), ColorRgba.White));
            TestSuite.Throws<ObjectDisposedException>(() => canvas.DrawLine(new(0, 0), new(20, 20), new(ColorRgba.White)));
            TestSuite.Equal(resources.CacheStatistics, default(SkiaCacheStatistics));
        });
        suite.Run("Skia image failure preserves host save stack", () =>
        {
            var resources = new SkiaResources(); using var surface = SKSurface.Create(new SKImageInfo(100, 100));
            var canvas = new SkiaChartCanvas(surface.Canvas, resources); var image = new RasterImage(1, 1, [255, 0, 0, 255]);
            int before = surface.Canvas.SaveCount; resources.Dispose();
            TestSuite.Throws<ObjectDisposedException>(() => canvas.DrawImage(image, new(0, 0, 10, 10), true, true));
            TestSuite.Equal(surface.Canvas.SaveCount, before);
        });
        suite.Run("Skia rejects nonfinite font and stroke sizes before native calls", () =>
        {
            using var resources = new SkiaResources(); using var surface = SKSurface.Create(new SKImageInfo(100, 100));
            var canvas = new SkiaChartCanvas(surface.Canvas, resources);
            foreach (double size in new[] { double.NaN, double.PositiveInfinity, -1, 0 })
                TestSuite.Throws<ArgumentOutOfRangeException>(() => canvas.MeasureText("text", new(ColorRgba.White, size)));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => canvas.DrawLine(default, new(1, 1), new(ColorRgba.White, double.PositiveInfinity)));
            TestSuite.Equal(resources.CacheStatistics.Fonts, 0);
        });
        suite.Run("3D surface indices reject row aliasing and invalid factories", () =>
        {
            var surface = new SurfaceData3D([0, 1], [0, 1], [10, 20, 30, 40]);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => { _ = surface[2, 0]; });
            TestSuite.Throws<ArgumentOutOfRangeException>(() => { _ = surface[-1, 1]; });
            TestSuite.Throws<ArgumentOutOfRangeException>(() => { _ = surface[0, 2]; });
            TestSuite.Near(surface[1, 1], 40);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => SurfaceData3D.Uniform(2, 2, 0, double.NaN, 0, 1, (_, _) => 0));
            TestSuite.Throws<ArgumentNullException>(() => SurfaceData3D.Uniform(2, 2, 0, 1, 0, 1, null!));
        });
        suite.Run("3D immutable transparency metadata survives caller-array changes", () =>
        {
            Vertex3D[] vertices = [new(Vector3D.Zero, Vector3D.UnitY, ColorRgba.White)];
            var opaque = new MeshBuffer3D(vertices, [], MeshTopology3D.Points); TestSuite.True(!opaque.HasTransparency);
            vertices[0] = vertices[0] with { Color = ColorRgba.White.WithAlpha(80) };
            var translucent = new MeshBuffer3D(vertices, [], MeshTopology3D.Points);
            TestSuite.True(!opaque.HasTransparency && translucent.HasTransparency);
            TestSuite.True(opaque.InterleavedVertices().All(float.IsFinite));
        });
        suite.Run("3D normalized buffers reject values that overflow GPU float coordinates", () =>
        {
            TestSuite.Throws<ArgumentException>(() => new MeshBuffer3D([new(new(1e200, 0, 0), Vector3D.UnitY, ColorRgba.White)], [], MeshTopology3D.Points));
            TestSuite.Throws<ArgumentException>(() => new MeshBuffer3D([new(Vector3D.Zero, new(0, 1e200, 0), ColorRgba.White)], [], MeshTopology3D.Points));
            TestSuite.Throws<ArgumentNullException>(() => new MeshBuffer3D(null!, [], MeshTopology3D.Points));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => new MeshBuffer3D([new(Vector3D.Zero, Vector3D.UnitY, ColorRgba.White)], [], (MeshTopology3D)99));
        });
        suite.Run("3D transparency-query constant-work evidence", () =>
        {
            var vertices = Enumerable.Range(0, 100000).Select(i => new Vertex3D(new(i * .00001, 0, 0), Vector3D.UnitY, ColorRgba.White)).ToArray();
            var mesh = new MeshBuffer3D(vertices, [], MeshTopology3D.Points);
            var timer = new Stopwatch(); long before = GC.GetAllocatedBytesForCurrentThread(); timer.Start();
            int transparent = 0; for (int i = 0; i < 100000; i++) if (mesh.HasTransparency) transparent++;
            double milliseconds = timer.Elapsed.TotalMilliseconds; long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            TestSuite.Equal(transparent, 0); TestSuite.True(bytes < 1024);
            var report = new { vertices = vertices.Length, queries = 100000, milliseconds, allocatedBytes = bytes,
                scope = "Immutable metadata reads only: HasTransparency is computed during construction, not by scanning vertices on each camera frame. Not a GPU/FPS benchmark." };
            Directory.CreateDirectory("artifacts/performance"); File.WriteAllText("artifacts/performance/mesh-metadata.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
}
