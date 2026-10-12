using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;
using ChartScene.Rendering;

public static class CursorLayoutTests
{
    private sealed class View : IDisposable
    {
        public ChartModel Model { get; } = new() { ShowLegend = false };
        public ChartController Controller { get; }
        private readonly ChartRenderer _renderer = new();
        public View(double units = 1, bool rotated = false, bool flipped = false, bool logarithmic = false)
        {
            Model.XAxis.Scale = logarithmic ? AxisScale.Logarithmic : AxisScale.Numeric;
            Model.XAxis.VisibleRange = new(units, 100 * units); Model.YAxis.VisibleRange = new(0, 100);
            Model.XAxis.AutoRange = Model.YAxis.AutoRange = AutoRange.Never;
            Model.XAxis.FlippedCoordinates = flipped;
            if (rotated) { Model.XAxis.AxisAlignment = AxisAlignment.Left; Model.YAxis.AxisAlignment = AxisAlignment.Bottom; }
            var data = new XyDataSeries(); data.AppendRange([units, 100 * units], [0, 100]);
            Model.RenderableSeries.Add(new FastLineRenderableSeries { DataSeries = data });
            Controller = new(Model) { InterpolateRollover = true }; Resize();
        }
        public RectD Plot => Controller.Frame!.Layout.PlotArea;
        public void Resize(double width = 1000, double height = 600, PlotMargins margins = default)
            => Controller.SetFrame(_renderer.Render(new RecordingCanvas(), Model, width, height, minimumMargins: margins));
        public void Move()
            => Controller.Process(new(ChartInputKind.Move, new(Plot.X + Plot.Width * .55, Plot.Y + Plot.Height * .55)));
        public double DataX()
        {
            var axis = Model.XAxis; var point = Controller.View.Cursor!.Value;
            return new CoordinateCalculator(axis, axis.IsVertical ? Plot.Y : Plot.X, axis.IsVertical ? Plot.Height : Plot.Width)
                .GetDataValue(axis.IsVertical ? point.Y : point.X);
        }
        public void Dispose() { Controller.Dispose(); Model.Dispose(); }
    }
    public static void Register(TestSuite suite)
    {
        foreach (bool rotated in new[] { false, true }) foreach (bool flipped in new[] { false, true }) foreach (bool logarithmic in new[] { false, true })
            suite.Run($"cursor follows live source layout rotated={rotated} flipped={flipped} log={logarithmic}", () =>
            {
                using var source = new View(rotated: rotated, flipped: flipped, logarithmic: logarithmic);
                using var target = new View(1000, logarithmic: logarithmic);
                using var group = new ChartCursorSyncGroup("layout");
                group.Add(source.Controller); group.Add(target.Controller, toCanonical: x => x / 1000, fromCanonical: x => x * 1000);
                source.Move(); var pointer = source.Controller.View.Cursor; long revision = source.Model.Revision;
                source.Resize(1200, 700, new(220, 100, 80, 100)); group.Refresh();
                TestSuite.Equal(source.Controller.View.Cursor, pointer); TestSuite.Equal(source.Model.Revision, revision);
                TestSuite.Near(target.DataX() / 1000, source.DataX(), 1e-8);
                TestSuite.Near(source.Controller.View.Hits.Single().X, source.DataX(), 1e-8);
                // Complete another frame in the opposite order, without new pointer input.
                target.Resize(1200, 700, new(240, 80, 60, 90)); group.Refresh();
                source.Resize(1200, 700, new(240, 80, 60, 90)); group.Refresh();
                TestSuite.Near(target.DataX() / 1000, source.DataX(), 1e-8);
                TestSuite.Near(target.Controller.View.Hits.Single().X / 1000, source.Controller.View.Hits.Single().X, 1e-8);
            });
        suite.Run("cursor clears peers when new source margins exclude pointer", () =>
        {
            using var source = new View(); using var target = new View(); using var group = new ChartCursorSyncGroup("excluded");
            group.Add(source.Controller); group.Add(target.Controller); source.Move();
            source.Resize(margins: new(800, 0, 0, 0)); group.Refresh();
            TestSuite.True(group.ActiveController is null); TestSuite.True(target.Controller.View.Cursor is null);
            TestSuite.Equal(target.Controller.View.Hits.Count, 0);
        });
        suite.Run("cursor source-range changes are reflected without another input event", () =>
        {
            using var source = new View(); using var target = new View(1000); using var group = new ChartCursorSyncGroup("range");
            group.Add(source.Controller); group.Add(target.Controller, fromCanonical: x => x * 1000);
            source.Move(); var pointer = source.Controller.View.Cursor;
            source.Model.XAxis.VisibleRange = new(10, 80); source.Resize(); group.Refresh();
            TestSuite.Equal(source.Controller.View.Cursor, pointer); TestSuite.Near(source.DataX(), target.DataX() / 1000, 1e-8);
        });
        suite.Run("cursor reentrant peer removal cannot revive a detached overlay", () =>
        {
            using var source = new View(); using var first = new View(); using var last = new View();
            using var group = new ChartCursorSyncGroup("detach"); group.Add(source.Controller); group.Add(first.Controller);
            var registration = group.Add(last.Controller);
            first.Controller.ViewChanged += (_, _) => registration.Dispose();
            source.Move(); TestSuite.Equal(group.Count, 2); TestSuite.True(last.Controller.View.Cursor is null);
        });
        suite.Run("cursor reentrant group disposal releases peer state", () =>
        {
            using var source = new View(); using var target = new View(); var group = new ChartCursorSyncGroup("dispose");
            group.Add(source.Controller); group.Add(target.Controller);
            target.Controller.ViewChanged += (_, _) => group.Dispose();
            source.Move(); TestSuite.Equal(group.Count, 0); TestSuite.True(group.ActiveController is null);
            TestSuite.True(target.Controller.View.Cursor is null); group.Dispose();
        });
        suite.Run("stationary aligned cursor refreshes allocate no membership arrays", () =>
        {
            using var source = new View(); using var target = new View(); using var group = new ChartCursorSyncGroup("warm");
            group.Add(source.Controller); group.Add(target.Controller); source.Move();
            int events = 0; source.Controller.ViewChanged += (_, _) => events++; target.Controller.ViewChanged += (_, _) => events++;
            Action refresh = group.Refresh;
            // Separate the first invocation/setup from warmed measurements. Compile the
            // measurement helper outside the interval and use that same helper to warm it.
            // Do not change production JIT settings or accept a best-of-N measurement.
            var warmup = Measure(refresh, 10000);
            var windows = new Measurement[3];
            for (int i = 0; i < windows.Length; i++) windows[i] = Measure(refresh, 10000);
            var result = new { refreshesPerWindow = 10000, warmup, windows, notifications = events,
                scope = "Warm unchanged cursor synchronization in a precompiled non-tiered test helper. Every measured window must allocate zero bytes. Warmup is reported separately; no timing threshold, GPU or presentation claim." };
            // Preserve evidence BEFORE assertions, including all failed windows.
            Directory.CreateDirectory("artifacts/performance");
            File.WriteAllText("artifacts/performance/cursor-layout.json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PERF_CURSOR_LAYOUT " + JsonSerializer.Serialize(result));
            for (int i = 0; i < windows.Length; i++)
                if (windows[i].AllocatedBytes != 0) throw new Exception($"Warm cursor window {i}: expected zero managed bytes, got {windows[i].AllocatedBytes}.");
            TestSuite.Equal(events, 0);
        });
        suite.Run("cursor allocation probe detects intentional per-call allocations", () =>
        {
            Action allocate = AllocateSentinel;
            try
            {
                _ = Measure(allocate, 100);
                var measured = Measure(allocate, 1000);
                TestSuite.True(measured.AllocatedBytes >= 64000);
            }
            finally { _sentinel = null; }
        });
    }

    private readonly record struct Measurement(long AllocatedBytes, long ElapsedTicks);
    // Fixture only: prevents this measurement loop from transitioning tiers inside its own
    // interval. The chart library and application keep their normal compilation behavior.
    // https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.methodimploptions
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static Measurement Measure(Action operation, int iterations)
    {
        long start = Stopwatch.GetTimestamp();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) operation();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return new(bytes, Stopwatch.GetTimestamp() - start);
    }
    private static object? _sentinel;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateSentinel() => _sentinel = new byte[64];
}
