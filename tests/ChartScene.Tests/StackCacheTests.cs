using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;
using ChartScene.Rendering;

public static class StackCacheTests
{
    private static RenderableSeries Add(ChartModel model, double[] x, double[] y, StackMode mode = StackMode.Absolute, string group = "default")
    {
        var data = new XyDataSeries { AcceptsUnsortedData = true }; data.AppendRange(x, y);
        var series = new StackedMountainRenderableSeries { DataSeries = data, StackMode = mode, StackedGroupId = group };
        model.RenderableSeries.Add(series); return series;
    }
    public static void Register(TestSuite suite)
    {
        suite.Run("stack frame shared across render range and cursor queries", () =>
        {
            using var model = new ChartModel { ShowLegend = false }; var series = Add(model, [0, 1, 2], [1, 2, 3]); Add(model, [0, 1, 2], [2, 3, 4]);
            var before = StackCalculator.Capture(model); var renderer = new ChartRenderer();
            var frame = renderer.Render(new RecordingCanvas(), model, 900, 500);
            model.GetDataRange(model.YAxis, false); model.Title = "changed"; model.Theme = ChartTheme.Light;
            model.XAxis.VisibleRange = new(.25, 1.75); model.Annotations.Add(new BoxAnnotation());
            var tester = new ChartHitTester(); tester.SetFrame(frame); tester.Query(model, frame.Layout.PlotArea.Center, nearestX: true, interpolate: true);
            renderer.Render(new RecordingCanvas(), model, 900, 500);
            TestSuite.True(ReferenceEquals(before, StackCalculator.Capture(model))); TestSuite.Equal(before.SourcePoints, 6L);
            TestSuite.Near(before[series].Upper[2], 3);
        });
        suite.Run("stack frame invalidation covers every stacking input", () =>
        {
            using var model = new ChartModel(); var a = Add(model, [0, 1], [1, 2]); var b = Add(model, [0, 1], [3, 4]);
            var frame = StackCalculator.Capture(model);
            void Changed(Action action)
            {
                action(); var next = StackCalculator.Capture(model); TestSuite.True(!ReferenceEquals(frame, next));
                TestSuite.Equal(next.BuildNumber, frame.BuildNumber + 1); frame = next;
            }
            Changed(() => ((XyDataSeries)a.DataSeries!).Update(1, 1, 5));
            Changed(() => { var replacement = new XyDataSeries(); replacement.AppendRange([0, 1], [1, 2]); a.DataSeries = replacement; });
            Changed(() => a.StackedGroupId = "another"); Changed(() => a.StackMode = StackMode.Percent);
            Changed(() => a.XAxisId = "X2"); Changed(() => a.YAxisId = "Y2");
            Changed(() => model.RenderableSeries.Move(1, 0)); Changed(() => b.IsVisible = false);
            Changed(() => b.IsVisible = true); Changed(() => model.RenderableSeries.Remove(b)); b.Dispose();
        });
        suite.Run("non-stacked changes do not rebuild stack buffers", () =>
        {
            using var model = new ChartModel(); Add(model, [0, 1], [1, 2]); var frame = StackCalculator.Capture(model);
            var data = new XyDataSeries(); data.Append(0, 1); var line = new FastLineRenderableSeries { DataSeries = data };
            model.RenderableSeries.Add(line); data.Append(1, 2); line.IsSelected = true;
            TestSuite.True(ReferenceEquals(frame, StackCalculator.Capture(model)));
        });
        suite.Run("stack snapshot and writable compatibility results are isolated", () =>
        {
            using var model = new ChartModel(); var a = Add(model, [0, 1], [1, 2]); var frame = StackCalculator.Capture(model);
            TestSuite.True(frame[a].Lower is not double[]);
            TestSuite.Throws<NotSupportedException>(() => ((IList<double>)frame[a].Upper)[0] = 123);
            var copy = StackCalculator.Calculate(model); copy[a].Upper[0] = 999; copy.Clear();
            TestSuite.Near(frame[a].Upper[0], 1); ((XyDataSeries)a.DataSeries!).ReplaceAll([0], [7]);
            TestSuite.Near(frame[a].Snapshot[1, 1], 2); TestSuite.Equal(StackCalculator.Capture(model)[a].Snapshot.Count, 1);
        });
        suite.Run("stack cache matches independent scalar accumulation", () =>
        {
            var random = new Random(56841);
            for (int scenario = 0; scenario < 20; scenario++)
            {
                using var model = new ChartModel(); var mode = scenario % 2 == 0 ? StackMode.Absolute : StackMode.Percent;
                for (int s = 0; s < 4; s++)
                    Add(model, Enumerable.Range(0, 200).Select(_ => (double)random.Next(50)).ToArray(),
                        Enumerable.Range(0, 200).Select(_ => random.Next(15) == 0 ? double.NaN : (double)random.Next(-20, 21)).ToArray(), mode);
                var actual = StackCalculator.Capture(model);
                var totals = new Dictionary<(double X, bool Positive), double>();
                foreach (var series in model.RenderableSeries)
                {
                    var data = series.DataSeries!.Snapshot();
                    for (int i = 0; i < data.Count; i++) if (double.IsFinite(data[1, i]))
                    { var key = (data[0, i], data[1, i] >= 0); totals[key] = totals.GetValueOrDefault(key) + Math.Abs(data[1, i]); }
                }
                var running = new Dictionary<(double, bool), double>();
                foreach (var series in model.RenderableSeries)
                {
                    var data = series.DataSeries!.Snapshot();
                    for (int i = 0; i < data.Count; i++)
                    {
                        double value = data[1, i];
                        if (double.IsNaN(value)) { TestSuite.True(double.IsNaN(actual[series].Upper[i])); continue; }
                        var key = (data[0, i], value >= 0); double lower = running.GetValueOrDefault(key);
                        if (mode == StackMode.Percent) value = totals[key] == 0 ? 0 : 100 * value / totals[key];
                        double upper = lower + value; TestSuite.Near(actual[series].Lower[i], lower); TestSuite.Near(actual[series].Upper[i], upper); running[key] = upper;
                    }
                }
            }
        });
        suite.Run("percent stacks normalize finite extreme and subnormal samples", () =>
        {
            foreach (double value in new[] { 1e308, double.MaxValue, double.Epsilon, 1e-300 })
            {
                using var model = new ChartModel(); var a = Add(model, [0, 1, 2], [value, -value, 0], StackMode.Percent);
                var b = Add(model, [0, 1, 2], [value, -value, 0], StackMode.Percent); var frame = StackCalculator.Capture(model);
                TestSuite.Near(frame[a].Upper[0], 50); TestSuite.Near(frame[b].Upper[0], 100);
                TestSuite.Near(frame[a].Upper[1], -50); TestSuite.Near(frame[b].Upper[1], -100); TestSuite.Near(frame[b].Upper[2], 0);
            }
        });
        suite.Run("overflowing absolute stack is diagnosed without poisoning cache", () =>
        {
            using var model = new ChartModel(); var a = Add(model, [0], [1e308]); var before = StackCalculator.Capture(model);
            var b = Add(model, [0], [1e308]); TestSuite.Throws<InvalidOperationException>(() => StackCalculator.Capture(model));
            a.StackMode = b.StackMode = StackMode.Percent; var after = StackCalculator.Capture(model);
            TestSuite.Equal(after.BuildNumber, before.BuildNumber + 1); TestSuite.Near(after[b].Upper[0], 100);
        });
        suite.Run("stack captures concurrent immutable readers once", () =>
        {
            using var model = new ChartModel(); Add(model, [0, 1], [1, 2]); Add(model, [0, 1], [3, 4]);
            var captures = new StackedFrame[20]; Parallel.For(0, captures.Length, i => captures[i] = StackCalculator.Capture(model));
            TestSuite.True(captures.All(frame => ReferenceEquals(frame, captures[0]))); TestSuite.Equal(captures[0].BuildNumber, 1L);
        });
        suite.Run("weak stack cache releases discarded models", () =>
        {
            var weak = DiscardModel(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); TestSuite.True(!weak.TryGetTarget(out _));
        });
        suite.Run("stack ranges reuse whole bounds and respect exact X filtering", () =>
        {
            using var model=new ChartModel();
            var a=Add(model,[0,1,2,3],[-2,3,double.NaN,5]); var b=Add(model,[0,1,2,3],[-4,7,2,4]);
            var frame=StackCalculator.Capture(model);
            TestSuite.Equal(frame[b].GetRange(),new DoubleRange(-6,10));
            TestSuite.Equal(frame[b].GetRange(positiveOnly:true),new DoubleRange(2,10));
            TestSuite.Equal(frame[b].GetRange(new(.5,1.5)),new DoubleRange(3,10));
            TestSuite.True(frame[a].GetRange(new(1.5,2.5)) is null);
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>frame[a].GetRange(default(DoubleRange)));
            ((XyDataSeries)b.DataSeries!).Update(1,1,100);
            TestSuite.Equal(frame[b].GetRange(),new DoubleRange(-6,10));
            TestSuite.True(StackCalculator.Capture(model)[b].GetRange()!.Value.Max>100);
        });
        suite.Run("released stack generation is not reused", () =>
        {
            using var model = new ChartModel(); Add(model, [0], [1]); var first = StackCalculator.Capture(model);
            StackCalculator.Release(model); TestSuite.True(!ReferenceEquals(first, StackCalculator.Capture(model)));
        });
        suite.Run("stacked cache warm-query work and allocation evidence", Benchmark);
        suite.Run("nested chart batches coalesce notifications while revisions stay current", () =>
        {
            using var model = new ChartModel(); var series = Add(model, [0, 1], [1, 2]); int events = 0; model.Changed += (_, _) => events++;
            long before = model.Revision;
            using (model.SuspendUpdates())
            {
                model.Title = "Batched";
                using (model.SuspendUpdates()) { model.XAxis.VisibleRange = new(0, 3); series.StackMode = StackMode.Percent; }
                ((XyDataSeries)series.DataSeries!).Append(2, 3); TestSuite.Equal(events, 0); TestSuite.True(model.Revision >= before + 4);
            }
            TestSuite.Equal(events, 1);
        });
        suite.Run("chart batches support idempotent out-of-order disposal and reentrancy", () =>
        {
            using var model = new ChartModel(); var a = model.SuspendUpdates(); var b = model.SuspendUpdates(); int events = 0;
            model.Changed += (_, _) => { events++; if (events == 1) model.Subtitle = "reentrant"; };
            model.Title = "Changed"; a.Dispose(); a.Dispose(); TestSuite.Equal(events, 0); b.Dispose(); TestSuite.Equal(events, 2);
            using (model.SuspendUpdates()) { } TestSuite.Equal(events, 2);
        });
        suite.Run("disposed chart ignores pending batch callbacks", () =>
        {
            var model = new ChartModel(); int events = 0; model.Changed += (_, _) => events++;
            var batch = model.SuspendUpdates(); model.Title = "Pending"; model.Dispose(); batch.Dispose(); TestSuite.Equal(events, 0);
            TestSuite.Throws<ObjectDisposedException>(() => model.SuspendUpdates());
        });
        suite.Run("concurrent data events respect chart notification batching", () =>
        {
            using var model = new ChartModel(); var series = Add(model, [0], [0]); var data = (XyDataSeries)series.DataSeries!;
            int events = 0; model.Changed += (_, _) => Interlocked.Increment(ref events);
            using (model.SuspendUpdates()) { Parallel.For(0, 200, i => data.Append(i, i)); TestSuite.Equal(events, 0); }
            TestSuite.Equal(events, 1); TestSuite.Equal(data.Count, 201);
        });
        suite.Run("rendering configuration changes invalidate chart views", () =>
        {
            using var model = new ChartModel(); var series = Add(model, [0, 1], [1, 2]);
            void Changed(Action action) { long before = model.Revision; action(); TestSuite.True(model.Revision > before); }
            Changed(() => series.StackMode = StackMode.Percent); Changed(() => series.VectorColorMode = VectorColorMode.Solid);
            Changed(() => series.VectorLengthMode = VectorLengthMode.FixedPixels); Changed(() => series.VectorScale = 2);
            Changed(() => series.ArrowHeadLength = 9); Changed(() => series.ArrowHeadWidth = 7); Changed(() => series.VectorMinimumSpacing = 4);
            Changed(() => series.IsArrowHeadFilled = false); Changed(() => series.IsResamplingEnabled = false);
            Changed(() => series.PolarConnectionMode = PolarConnectionMode.Straight); Changed(() => series.DonutHoleRatio = .6);
            Changed(() => series.ContourLevels = [-1, 0, 1]); Changed(() => series.PointLabels = ["A", "B"]);
            Changed(() => series.PaletteProvider = new ThresholdPaletteProvider(0, ColorRgba.Black, ColorRgba.White));
            Changed(() => series.DataLabelProvider = new NumericDataLabelProvider());
            Changed(() => model.AspectRatio = 2); Changed(() => model.AspectRatioMode = AspectRatioMode.Y); Changed(() => model.PolarAnglesInDegrees = false);
        });
        suite.Run("rendering lists own immutable snapshots and reject invalid values atomically", () =>
        {
            using var model = new ChartModel(); var series = Add(model, [0], [1]); double[] levels = [-1, 0, 1]; string[] labels = ["A"];
            series.ContourLevels = levels; series.PointLabels = labels; levels[0] = 999; labels[0] = "mutated";
            TestSuite.Near(series.ContourLevels[0], -1); TestSuite.Equal(series.PointLabels[0], "A");
            TestSuite.Throws<NotSupportedException>(() => ((IList<double>)series.ContourLevels)[0] = 10);
            long before = model.Revision; series.ContourLevels = [-1, 0, 1]; TestSuite.Equal(model.Revision, before);
            TestSuite.Throws<ArgumentException>(() => series.ContourLevels = [double.NaN]);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => series.VectorScale = double.NaN);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => series.StackMode = (StackMode)99);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => series.DonutHoleRatio = 2);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => model.AspectRatio = -1);
            TestSuite.Equal(model.Revision, before);
        });
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<ChartModel> DiscardModel()
    { var model = new ChartModel(); Add(model, [0, 1], [1, 2]); StackCalculator.Capture(model); return new(model); }
    private static void Benchmark()
    {
        using var model = new ChartModel(); const int points = 100000;
        var x = Enumerable.Range(0, points).Select(i => (double)i).ToArray();
        for (int s = 0; s < 3; s++) Add(model, x, Enumerable.Range(0, points).Select(i => 1 + Math.Sin(i * .01 + s) * .5).ToArray());
        var timer = Stopwatch.StartNew(); var cold = StackCalculator.Capture(model); double coldMs = timer.Elapsed.TotalMilliseconds;
        for (int i = 0; i < 20; i++) StackCalculator.Capture(model);
        long before = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
        for (int i = 0; i < 1000; i++) TestSuite.True(ReferenceEquals(cold, StackCalculator.Capture(model)));
        double warmMs = timer.Elapsed.TotalMilliseconds; long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        TestSuite.True(bytes < 4096); TestSuite.Equal(cold.BuildNumber, 1L);
        TestSuite.Equal(cold.SourcePoints, 300000L); TestSuite.Equal(cold.ArrayPayloadBytes, 4800000L);
        var report = new { series = 3, pointsPerSeries = points, coldBuildMs = coldMs, warmQueries = 1000, warmTotalMs = warmMs,
            warmAllocatedBytes = bytes, cachedOutputArrayPayloadBytes = cold.ArrayPayloadBytes,
            warmSourceSamplesVisited = 0, buildCount = cold.BuildNumber,
            scope = "CPU immutable-stack capture only. Source snapshot creation is included in the first capture, not warm queries. Excludes coordinate projection, drawing, GPU and presentation." };
        Directory.CreateDirectory("artifacts/performance");
        File.WriteAllText("artifacts/performance/stack-cache.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PERF_STACK " + JsonSerializer.Serialize(report));
    }
}
