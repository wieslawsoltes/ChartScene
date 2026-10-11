using System.Diagnostics;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;
using ChartScene.Rendering;

public static class HitTestIntegrityTests
{
    private sealed class Fixture : IDisposable
    {
        public ChartModel Model { get; } = new() { ShowLegend = false };
        public XyDataSeries Data { get; } = new();
        public RenderableSeries Series { get; }
        public ChartHitTester Tester { get; } = new();
        public ChartFrame Frame { get; private set; } = null!;
        public Fixture(SeriesKind kind = SeriesKind.Line, bool rotated = false, bool flipped = false)
        {
            Model.XAxis.VisibleRange = Model.YAxis.VisibleRange = new(0, 10);
            Model.XAxis.AutoRange = Model.YAxis.AutoRange = AutoRange.Never;
            if (rotated) { Model.XAxis.AxisAlignment = AxisAlignment.Left; Model.YAxis.AxisAlignment = AxisAlignment.Bottom; }
            Model.XAxis.FlippedCoordinates = flipped;
            Data.AppendRange([1, 9], [1, 9]);
            Series = new(kind) { DataSeries = Data }; Model.RenderableSeries.Add(Series); Render();
        }
        public PointD Screen(double x, double y) => Frame.Layout.Projection(Model, Series).ToScreen(x, y);
        public void Render()
        { Frame = new ChartRenderer().Render(new RecordingCanvas(), Model, 900, 600); Tester.SetFrame(Frame); }
        public void Dispose() => Model.Dispose();
    }
    public static void Register(TestSuite suite)
    {
        suite.Run("line hit interpolation flag distinguishes points from segments", () =>
        {
            using var f = new Fixture(); var middle = f.Screen(5, 5);
            TestSuite.Equal(f.Tester.Query(f.Model, middle, 2).Count, 0);
            var interpolated = f.Tester.Query(f.Model, middle, 2, interpolate: true).Single();
            TestSuite.Near(interpolated.X, 5); TestSuite.Near(interpolated.Y, 5);
            TestSuite.Equal(f.Tester.Query(f.Model, f.Screen(1, 1), 0).Single().DataIndex, 0);
        });
        foreach (bool rotated in new[] { false, true }) foreach (bool flipped in new[] { false, true })
            suite.Run($"digital hit follows actual steps rotated={rotated} flipped={flipped}", () =>
            {
                using var f = new Fixture(SeriesKind.DigitalLine, rotated, flipped);
                TestSuite.Equal(f.Tester.Query(f.Model, f.Screen(5, 5), 2, interpolate: true).Count, 0);
                var horizontal = f.Tester.Query(f.Model, f.Screen(5, 1), 2, interpolate: true).Single();
                TestSuite.Near(horizontal.X, 5); TestSuite.Near(horizontal.Y, 1);
                var vertical = f.Tester.Query(f.Model, f.Screen(9, 5), 2, interpolate: true).Single();
                TestSuite.Near(vertical.X, 9); TestSuite.Near(vertical.Y, 5);
                var rollover = f.Tester.Query(f.Model, f.Screen(5, 8), nearestX: true, interpolate: true).Single();
                TestSuite.Near(rollover.Y, 1);
                var endpoint = f.Tester.Query(f.Model, f.Screen(9, 9), nearestX: true, interpolate: true).Single();
                TestSuite.Near(endpoint.Y, 9); TestSuite.Equal(endpoint.DataIndex, 1);
            });
        suite.Run("line logarithmic interpolation lies on rendered segment", () =>
        {
            using var f = new Fixture(); f.Data.ReplaceAll([1, 100], [1, 100]);
            f.Model.XAxis.Scale = f.Model.YAxis.Scale = AxisScale.Logarithmic;
            f.Model.XAxis.VisibleRange = f.Model.YAxis.VisibleRange = new(1, 100); f.Render();
            var pointer = f.Screen(10, 10);
            var hit = f.Tester.Query(f.Model, pointer, 3, true, true).Single();
            TestSuite.Near(hit.X, 10); TestSuite.Near(hit.Y, 10);
            TestSuite.Near(hit.Position.X, pointer.X); TestSuite.Near(hit.Position.Y, pointer.Y);
        });
        suite.Run("stacked rollover uses cumulative screen position and raw contribution", () =>
        {
            using var f = new Fixture(SeriesKind.StackedMountain); f.Data.ReplaceAll([1, 9], [10, 20]);
            var extra = new XyDataSeries(); extra.AppendRange([1, 9], [1, 3]);
            var top = new RenderableSeries(SeriesKind.StackedMountain) { DataSeries = extra };
            f.Model.RenderableSeries.Add(top); f.Model.YAxis.VisibleRange = new(0, 30); f.Render();
            var target = f.Screen(5, 17);
            var hit = f.Tester.Query(f.Model, target, 10, true, true).Single(h => ReferenceEquals(h.Series, top));
            TestSuite.Near(hit.Y, 2); TestSuite.Near(hit.Position.Y, target.Y);
        });
        suite.Run("rollover duplicate-X tie resolves closest screen point", () =>
        {
            using var f = new Fixture(SeriesKind.Scatter);
            f.Data.ReplaceAll(Enumerable.Repeat(5.0, 10).ToArray(), Enumerable.Range(0, 10).Select(i => (double)i).ToArray()); f.Render();
            var hit = f.Tester.Query(f.Model, f.Screen(5, 8), nearestX: true).Single();
            TestSuite.Equal(hit.DataIndex, 8); TestSuite.Near(hit.Y, 8);
        });
        suite.Run("line hit does not interpolate across NaN gaps", () =>
        {
            using var f = new Fixture(); f.Data.ReplaceAll([1, 5, 9], [1, double.NaN, 9]); f.Render();
            TestSuite.Equal(f.Tester.Query(f.Model, f.Screen(5, 5), 2, interpolate: true).Count, 0);
        });
        suite.Run("hit tester ignores hidden and removed frame regions", () =>
        {
            using var f = new Fixture(SeriesKind.Scatter); var point = f.Screen(1, 1);
            TestSuite.Equal(f.Tester.Query(f.Model, point).Count, 1);
            f.Series.IsVisible = false; TestSuite.Equal(f.Tester.Query(f.Model, point).Count, 0);
            f.Series.IsVisible = true; f.Render(); f.Model.RenderableSeries.Remove(f.Series);
            TestSuite.Equal(f.Tester.Query(f.Model, point).Count, 0); f.Series.Dispose();
        });
        suite.Run("hit tester does not pair old bounds with edited data", () =>
        {
            using var f = new Fixture(SeriesKind.Scatter); var oldPoint = f.Screen(1, 1);
            TestSuite.Equal(f.Tester.Query(f.Model, oldPoint, 2).Count, 1);
            f.Data.Update(0, 1, 8); TestSuite.Equal(f.Tester.Query(f.Model, oldPoint, 2).Count, 0);
            f.Render(); TestSuite.Equal(f.Tester.Query(f.Model, f.Screen(1, 8), 2).Count, 1);
        });
        suite.Run("hit tester source replacement cannot reuse equal-length stale regions", () =>
        {
            using var f = new Fixture(SeriesKind.Scatter); var oldPoint = f.Screen(1, 1);
            f.Tester.Query(f.Model, oldPoint);
            var other = new XyDataSeries(); other.AppendRange([1, 9], [8, 2]); f.Series.DataSeries = other;
            TestSuite.Equal(f.Tester.Query(f.Model, oldPoint, 2).Count, 0);
        });
        suite.Run("transparent log heatmap samples are not hover targets", () =>
        {
            using var model = new ChartModel { ShowLegend = false };
            var series = new RenderableSeries(SeriesKind.Heatmap)
            {
                HeatmapGrid = new([0, 1, 2], [0, 1, 2], [-1, 0, 2, double.NaN]),
                ColorScaleMode = HeatmapColorScaleMode.Logarithmic, ColorMapRange = new(1, 10)
            };
            model.RenderableSeries.Add(series); var frame = new ChartRenderer().Render(new RecordingCanvas(), model, 900, 600);
            var tester = new ChartHitTester(); tester.SetFrame(frame); var projection = frame.Layout.Projection(model, series);
            foreach (var point in new[] { new PointD(.5, .5), new PointD(1.5, .5), new PointD(1.5, 1.5) })
                TestSuite.Equal(tester.Query(model, projection.ToScreen(point.X, point.Y)).Count, 0);
            TestSuite.Equal(tester.Query(model, projection.ToScreen(.5, 1.5)).Single().DataIndex, 2);
        });
        suite.Run("hit spatial index is built lazily and reused only for the current frame", () =>
        {
            using var f = new Fixture(SeriesKind.Scatter); var pointer = f.Screen(1, 1);
            for (int i = 0; i < 100; i++)
            { f.Tester.SetFrame(f.Frame with { Statistics = f.Frame.Statistics with { CpuMilliseconds = i } }); f.Tester.Query(f.Model, pointer, nearestX: true); }
            TestSuite.Equal(f.Tester.HitRegionIndexBuildCount, 0L);
            f.Tester.Query(f.Model, pointer); f.Tester.Query(f.Model, pointer); TestSuite.Equal(f.Tester.HitRegionIndexBuildCount, 1L);
            f.Tester.SetFrame(f.Frame); f.Tester.Query(f.Model, pointer); TestSuite.Equal(f.Tester.HitRegionIndexBuildCount, 2L);
            f.Tester.SetFrame(f.Frame); f.Tester.Query(f.Model, pointer); TestSuite.Equal(f.Tester.HitRegionIndexBuildCount, 2L);
        });
        suite.Run("spatial radius and bounds cannot create unbounded empty-cell loops", () =>
        {
            using var f = new Fixture(SeriesKind.Scatter);
            var regions = new[] { new HitRegion(f.Series, 0, new(0, 0, 1, 1), new(.5, .5)), new HitRegion(f.Series, 1, new(-1e15, -1e15, 2e15, 2e15), new(0, 0)) };
            var index = new HitTestIndex(regions);
            TestSuite.Equal(index.Query(new(0, 0), double.MaxValue).Count(), 2); TestSuite.True(index.LastQueryUsedFullScan);
            TestSuite.Equal(index.Query(new(.5, .5), 0).Count(), 2); TestSuite.True(!index.LastQueryUsedFullScan);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => index.Query(default, double.PositiveInfinity).ToArray());
            TestSuite.Throws<ArgumentOutOfRangeException>(() => index.Query(default, -1).ToArray());
            TestSuite.Throws<ArgumentException>(() => new HitTestIndex([new(f.Series, 0, new(0, 0, double.NaN, 1), default)]));
            TestSuite.Equal(index.Query(new(double.NaN, 0), 1).Count(), 0);
        });
        suite.Run("hit tester rejects invalid radius and ignores nonfinite pointer coordinates", () =>
        {
            using var f = new Fixture();
            TestSuite.Throws<ArgumentOutOfRangeException>(() => f.Tester.Query(f.Model, default, -1));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => f.Tester.Query(f.Model, default, double.NaN));
            TestSuite.Equal(f.Tester.Query(f.Model, new(double.PositiveInfinity, 0)).Count, 0);
        });
        suite.Run("rollover spatial-index allocation evidence", () =>
        {
            using var model = new ChartModel(); var series = new RenderableSeries(SeriesKind.Scatter);
            var regions = Enumerable.Range(0, 50000).Select(i => new HitRegion(series, i, new(i % 1000, i / 1000, 4, 4), new(i % 1000 + 2, i / 1000 + 2), 2)).ToArray();
            var frame = new ChartFrame(new(new(0, 0, 1000, 500), new(0, 0, 1000, 500), []), new(50000, 50000, 1, 0, 0), regions, [], []);
            var tester = new ChartHitTester(); tester.SetFrame(frame);
            long before = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) tester.SetFrame(frame with { Statistics = frame.Statistics with { CpuMilliseconds = i } });
            double milliseconds = timer.Elapsed.TotalMilliseconds; long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            TestSuite.Equal(tester.HitRegionIndexBuildCount, 0L); TestSuite.True(bytes < 100000);
            var report = new { sourceRegions = 50000, frames = 100, milliseconds, allocatedBytes = bytes, indexBuilds = tester.HitRegionIndexBuildCount,
                scope = "Hit tester frame handoff only, excluding frame construction by the renderer, drawing, and query/presentation timing. No spatial buckets are allocated until a bounds query needs them." };
            Directory.CreateDirectory("artifacts/performance"); File.WriteAllText("artifacts/performance/hit-test-handoff.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PERF_HIT " + JsonSerializer.Serialize(report));
        });
    }
}
