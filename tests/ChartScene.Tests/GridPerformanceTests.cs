using System.Diagnostics;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Rendering;

public static class GridPerformanceTests
{
    public static void Register(TestSuite suite)
    {
        suite.Run("grid visibility matches exhaustive inclusive cell bounds", () =>
        {
            var random = new Random(8915);
            double[] x = Enumerable.Range(0, 129).Select(i => Math.Pow(i, 1.4)).ToArray();
            double[] y = Enumerable.Range(0, 97).Select(i => Math.Pow(i, 1.7)).ToArray();
            var grid = new HeatmapGrid(x, y, new double[128 * 96]);
            for (int query = 0; query < 150; query++)
            {
                double a = random.NextDouble() * x[^1] * 1.3 - 50, b = a + random.NextDouble() * 200 + .1;
                double c = random.NextDouble() * y[^1] * 1.3 - 50, d = c + random.NextDouble() * 200 + .1;
                var rangeX = new DoubleRange(a, b); var rangeY = new DoubleRange(c, d);
                var window = GridVisibility.VisibleCells(grid, rangeX, rangeY);
                long count = 0;
                for (int row = 0; row < grid.Height; row++) for (int col = 0; col < grid.Width; col++)
                {
                    bool visible = x[col + 1] >= a && x[col] <= b && y[row + 1] >= c && y[row] <= d;
                    bool inside = col >= window.FirstColumn && col < window.EndColumn && row >= window.FirstRow && row < window.EndRow;
                    TestSuite.Equal(inside, visible); if (visible) count++;
                }
                TestSuite.Equal(window.CellCount, count);
            }
        });
        suite.Run("grid visibility retains exact boundary neighbors", () =>
        {
            var grid = new HeatmapGrid([0, 1, 4, 9], [0, 2, 3, 10], new double[9]);
            TestSuite.Equal(GridVisibility.VisibleCells(grid, new(1, 4), new(2, 3)).CellCount, 9L);
            TestSuite.Equal(GridVisibility.VisibleCells(grid, new(10, 11), new(1, 2)).CellCount, 0L);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => GridVisibility.VisibleCells(grid, default, new(0, 1)));
        });
        suite.Run("grid topology cache separates immutable inputs", () =>
        {
            var uniform = HeatmapGrid.Uniform(100, 100, 1, .1, 2, .25, (_, _) => 0);
            var nonuniform = new HeatmapGrid([0, 1, 4], [0, 1, 2], [1, 2, 3, 4]);
            for (int i = 0; i < 10; i++) { TestSuite.True(GridVisibility.IsUniform(uniform)); TestSuite.True(!GridVisibility.IsUniform(nonuniform)); }
        });
        suite.Run("million-cell nonuniform viewport avoids full-grid traversal", () =>
        {
            var grid = new HeatmapGrid(Enumerable.Range(0, 1001).Select(i => (double)i * i).ToArray(),
                Enumerable.Range(0, 1001).Select(i => (double)i * i).ToArray(), new double[1_000_000]);
            var window = GridVisibility.VisibleCells(grid, new(10000.5, 11000.5), new(10000.5, 11000.5));
            TestSuite.True(window.CellCount < 50);
            using var model = new ChartModel { ShowLegend = false };
            model.RenderableSeries.Add(new(SeriesKind.Heatmap) { HeatmapGrid = grid });
            model.XAxis.AutoRange = model.YAxis.AutoRange = AutoRange.Never;
            model.XAxis.VisibleRange = model.YAxis.VisibleRange = new(10000.5, 11000.5);
            var result = new ChartRenderer().Render(new RecordingCanvas(), model, 800, 500);
            TestSuite.Equal(result.Statistics.RenderedPoints, window.CellCount);
        });
        suite.Run("contour cache reuses geometry across equivalent level lists", () =>
        {
            var grid = HeatmapGrid.Uniform(80, 60, 0, 1, 0, 1, (x, y) => Math.Sin(x * .1) + Math.Cos(y * .1));
            double[] levels = [-.5, 0, .5];
            var first = ContourGeometryCache.Capture(grid, levels);
            TestSuite.True(ReferenceEquals(first, ContourGeometryCache.Capture(grid, [-.5, 0, .5])));
            TestSuite.True(first.SequenceEqual(Contours.MarchingSquares(grid, levels)));
            levels[0] = -.8; var modified = ContourGeometryCache.Capture(grid, levels);
            TestSuite.True(!ReferenceEquals(first, modified)); TestSuite.True(first.All(s => s.Level != -.8));
            TestSuite.True(ReferenceEquals(modified, ContourGeometryCache.Capture(grid, levels)));
        });
        suite.Run("contour cache rejects invalid levels and protects returned arrays", () =>
        {
            var grid = HeatmapGrid.Uniform(3, 3, 0, 1, 0, 1, (x, y) => x + y);
            var segments = ContourGeometryCache.Capture(grid, [1.5]);
            TestSuite.True(segments is not ContourSegment[]);
            TestSuite.Throws<NotSupportedException>(() => ((IList<ContourSegment>)segments)[0] = default);
            TestSuite.Throws<ArgumentException>(() => ContourGeometryCache.Capture(grid, [double.NaN]));
            TestSuite.Equal(ContourGeometryCache.Capture(grid, []).Count, 0);
        });
        suite.Run("contour cache concurrent captures agree", () =>
        {
            var grid = HeatmapGrid.Uniform(30, 30, 0, 1, 0, 1, (x, y) => x + y);
            IReadOnlyList<ContourSegment>?[] results = new IReadOnlyList<ContourSegment>[8];
            Parallel.For(0, 8, i => results[i] = ContourGeometryCache.Capture(grid, [5, 10, 15]));
            TestSuite.True(results.All(r => ReferenceEquals(r, results[0])));
        });
        suite.Run("contour topology all marching-squares cases", () =>
        {
            for (int mask = 0; mask < 16; mask++)
            {
                double At(int bit) => (mask & (1 << bit)) != 0 ? 1 : -1;
                var grid = new HeatmapGrid([0, 1, 2], [0, 1, 2], [At(0), At(1), At(3), At(2)]);
                var segments = Contours.MarchingSquares(grid, [0]).ToArray();
                TestSuite.Equal(segments.Length, mask is 0 or 15 ? 0 : mask is 5 or 10 ? 2 : 1);
                TestSuite.True(segments.All(s => s.A.IsFinite && s.B.IsFinite));
            }
        });
        suite.Run("large finite contour saddle does not overflow", () =>
        {
            var grid = new HeatmapGrid([0, 1, 2], [0, 1, 2], [1e308, -1e308, -1e308, 1e308]);
            var segments = Contours.MarchingSquares(grid, [0]).ToArray();
            TestSuite.Equal(segments.Length, 2);
            TestSuite.Equal(segments[0].A, new PointD(1, .5)); TestSuite.Equal(segments[0].B, new PointD(1.5, 1));
        });
        suite.Run("contour caching allocation and work evidence", () =>
        {
            var grid = HeatmapGrid.Uniform(160, 120, 0, 1, 0, 1, (x, y) => Math.Sin(x * .06) * Math.Cos(y * .065));
            double[] levels = Enumerable.Range(1, 10).Select(i => -1 + 2.0 * i / 11).ToArray();
            long before = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
            var fresh = ContourGeometryCache.Capture(grid, levels); double coldMs = timer.Elapsed.TotalMilliseconds;
            long coldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            var samples = new double[51]; before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < samples.Length; i++)
            {
                timer.Restart(); var hit = ContourGeometryCache.Capture(grid, levels); samples[i] = timer.Elapsed.TotalMilliseconds;
                TestSuite.True(ReferenceEquals(fresh, hit));
            }
            long warmBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / samples.Length;
            TestSuite.True(warmBytes < 2048); Array.Sort(samples);
            var report = new { width = grid.Width, height = grid.Height, levels = levels.Length, segments = fresh.Count,
                coldMilliseconds = coldMs, coldAllocatedBytes = coldBytes, cachedP50Milliseconds = samples[25], cachedP95Milliseconds = samples[48],
                cachedAllocatedBytesPerQuery = warmBytes, scope = "CPU geometry capture only, excluding drawing and presentation. Cached captures evaluate zero grid cells." };
            Directory.CreateDirectory("artifacts/performance");
            File.WriteAllText("artifacts/performance/contours.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
}
