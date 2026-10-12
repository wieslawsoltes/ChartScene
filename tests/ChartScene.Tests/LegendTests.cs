using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using SkiaSharp;

public static class LegendTests
{
    private static ChartModel Model()
    {
        var m = new ChartModel();
        for (int i = 0; i < 4; i++) m.RenderableSeries.Add(new() { Id = "s" + i, SeriesName = "Signal " + i, IsSelected = i == 2 });
        return m;
    }
    private static void Same(IEnumerable<ChartLegendEntry> actual, params ChartLegendEntry[] expected)
        => TestSuite.True(actual.SequenceEqual(expected));
    public static void Register(TestSuite suite)
    {
        suite.Run("legend observes inclusion visibility and selection with stable identities", () =>
        {
            using var model = Model(); using var source = new ChartLegendSource { Model = model };
            var rows = source.Entries.ToArray();
            rows[0].IsVisible = false; Same(source.Entries, rows);
            source.SourceMode = LegendSourceMode.Visible; Same(source.Entries, rows[1], rows[2], rows[3]);
            rows[0].IsVisible = true; Same(source.Entries, rows);
            source.SourceMode = LegendSourceMode.Selected; Same(source.Entries, rows[2]);
            model.RenderableSeries[0].IsSelected = true; Same(source.Entries, rows[0], rows[2]);
            model.RenderableSeries[2].IncludeInLegend = false; Same(source.Entries, rows[0]);
            source.SourceMode = LegendSourceMode.All; Same(source.Entries, rows[0], rows[1], rows[3]);
            model.RenderableSeries[2].IncludeInLegend = true; Same(source.Entries, rows);
            TestSuite.Equal(source.EntryCreationCount, 4L);
        });
        suite.Run("legend moves rename and style preserve entries without reset", () =>
        {
            using var model = Model(); using var source = new ChartLegendSource { Model = model };
            var rows = source.Entries.ToArray(); long passes = source.MembershipPassCount; int resets = 0, changes = 0;
            ((INotifyCollectionChanged)source.Entries).CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
            rows[0].PropertyChanged += (_, _) => changes++;
            model.RenderableSeries[0].SeriesName = "Renamed"; model.RenderableSeries[0].Stroke = ColorRgba.White;
            TestSuite.Equal(source.MembershipPassCount, passes); TestSuite.Equal(changes, 2);
            model.RenderableSeries.Move(0, 3); Same(source.Entries, rows[1], rows[2], rows[3], rows[0]);
            TestSuite.Equal(resets, 0); TestSuite.Equal(source.EntryCreationCount, 4L);
        });
        suite.Run("legend removed entries detach and cannot mutate former series", () =>
        {
            using var model = Model(); using var source = new ChartLegendSource { Model = model };
            var row = source.Entries[0]; int events = 0; row.PropertyChanged += (_, _) => events++;
            model.RenderableSeries.RemoveAt(0); TestSuite.True(!row.IsAttached);
            int detachedEvents = events; row.Series.SeriesName = "Old";
            TestSuite.Equal(events, detachedEvents); TestSuite.Throws<InvalidOperationException>(() => row.IsVisible = false);
            model.RenderableSeries.Add(row.Series); TestSuite.True(source.Entries[^1].Identity != row.Identity);
        });
        suite.Run("legend model replacement releases old membership and theme observation", () =>
        {
            using var first = Model(); using var next = Model(); using var source = new ChartLegendSource { Model = first };
            var old = source.Entries.ToArray(); source.Model = next; var current = source.Entries.ToArray(); long passes = source.MembershipPassCount;
            first.RenderableSeries.Add(new()); first.RenderableSeries[0].SeriesName = "Old changed"; first.Theme = ChartTheme.Light;
            Same(source.Entries, current); TestSuite.Equal(source.MembershipPassCount, passes); TestSuite.True(old.All(r => !r.IsAttached));
            next.Theme = ChartTheme.Light; TestSuite.Equal(source.Theme, ChartTheme.Light);
        });
        suite.Run("legend null and disposed bindings cannot resurrect observations", () =>
        {
            using var model = Model(); var source = new ChartLegendSource { Model = model }; var old = source.Entries[0];
            source.Model = null; TestSuite.Equal(source.Entries.Count, 0); TestSuite.True(!old.IsAttached);
            source.Model = model; source.Dispose(); source.Dispose(); long passes = source.MembershipPassCount;
            model.RenderableSeries.Add(new()); model.Theme = ChartTheme.Light;
            TestSuite.Equal(source.MembershipPassCount, passes); TestSuite.Equal(source.Entries.Count, 0);
            TestSuite.Throws<ObjectDisposedException>(() => source.Model = model);
            TestSuite.Throws<ObjectDisposedException>(() => source.SourceMode = LegendSourceMode.Visible);
        });
        suite.Run("legend validates mode and exposes immutable collection", () =>
        {
            using var model = Model(); using var source = new ChartLegendSource { Model = model };
            TestSuite.Throws<ArgumentOutOfRangeException>(() => source.SourceMode = (LegendSourceMode)99);
            TestSuite.Equal(source.SourceMode, LegendSourceMode.All);
            TestSuite.Throws<NotSupportedException>(() => ((IList<ChartLegendEntry>)source.Entries).Clear());
        });
        suite.Run("legend no-op settings and metadata do not run membership passes", () =>
        {
            using var model = Model(); using var source = new ChartLegendSource { Model = model }; long passes = source.MembershipPassCount;
            source.Model = model; source.SourceMode = source.SourceMode; model.Title = "Other chart title";
            model.RenderableSeries[0].StrokeThickness = 3; model.RenderableSeries[0].IsSelected = true;
            TestSuite.Equal(source.MembershipPassCount, passes);
        });
        suite.Run("legend reentrant model replacement publishes only new entries", () =>
        {
            using var first = Model(); using var next = Model(); using var source = new ChartLegendSource(); bool replaced = false;
            ((INotifyCollectionChanged)source.Entries).CollectionChanged += (_, _) => { if (!replaced) { replaced = true; source.Model = next; } };
            source.Model = first;
            TestSuite.True(ReferenceEquals(source.Model, next)); TestSuite.True(source.Entries.All(e => next.RenderableSeries.Contains(e.Series)));
            TestSuite.Equal(source.Entries.Count, 4);
        });
        suite.Run("legend reentrant filtering converges without collection reentrancy", () =>
        {
            using var model = Model(); using var source = new ChartLegendSource(); bool changed = false;
            ((INotifyCollectionChanged)source.Entries).CollectionChanged += (_, _) => { if (!changed) { changed = true; source.SourceMode = LegendSourceMode.Selected; } };
            source.Model = model; TestSuite.Equal(source.Entries.Count, 1); TestSuite.Equal(source.Entries[0].Series.Id, "s2");
        });
        suite.Run("legend disposal during collection notification leaves no live rows", () =>
        {
            using var model = Model(); var source = new ChartLegendSource(); bool disposed = false;
            ((INotifyCollectionChanged)source.Entries).CollectionChanged += (_, _) => { if (!disposed) { disposed = true; source.Dispose(); } };
            source.Model = model; TestSuite.Equal(source.Entries.Count, 0); TestSuite.True(source.Model is null);
        });
        suite.Run("legend random edits agree with scalar filter while preserving surviving entries", () =>
        {
            using var model = Model(); using var source = new ChartLegendSource { Model = model }; var random = new Random(932);
            var identities = model.RenderableSeries.Zip(source.Entries).ToDictionary(p => p.First, p => p.Second);
            for (int i = 0; i < 500; i++)
            {
                var s = model.RenderableSeries[random.Next(4)];
                switch (random.Next(5))
                {
                    case 0: s.IsVisible = !s.IsVisible; break;
                    case 1: s.IsSelected = !s.IsSelected; break;
                    case 2: s.IncludeInLegend = !s.IncludeInLegend; break;
                    case 3: model.RenderableSeries.Move(random.Next(4), random.Next(4)); break;
                    default: source.SourceMode = (LegendSourceMode)random.Next(3); break;
                }
                var expected = model.RenderableSeries.Where(s => s.IncludeInLegend && (source.SourceMode == LegendSourceMode.All || source.SourceMode == LegendSourceMode.Visible && s.IsVisible || source.SourceMode == LegendSourceMode.Selected && s.IsSelected)).Select(s => identities[s]);
                TestSuite.True(source.Entries.SequenceEqual(expected));
            }
            TestSuite.Equal(source.EntryCreationCount, 4L);
        });
        suite.Run("legend ignores worker data notifications without snapshots or extra allocations", Benchmark);
        suite.Run("built-in legend honors the same inclusion metadata", () =>
        {
            using var model = Model(); model.RenderableSeries[1].IncludeInLegend = false;
            var frame = new ChartRenderer().Render(new RecordingCanvas(), model, 800, 600);
            TestSuite.Equal(frame.LegendItems.Count, 3); TestSuite.True(frame.LegendItems.All(e => e.Series.IncludeInLegend));
        });
        foreach (SeriesKind kind in Enum.GetValues<SeriesKind>())
            suite.Run("legend vector swatch renders without data " + kind, () =>
            {
                using var series = new RenderableSeries(kind) { Stroke = new(60, 180, 230), Fill = new(40, 150, 190), PointMarker = PointMarker.Diamond };
                var recording = new RecordingCanvas(); LegendMarkerRenderer.Draw(recording, series, new(5, 5, 60, 24));
                TestSuite.Equal(recording.SaveDepth, 0); TestSuite.True(recording.Commands.Count > 3);
                using var surface = SKSurface.Create(new SKImageInfo(80, 40)); using var resources = new SkiaResources(); surface.Canvas.Clear(SKColors.Black);
                LegendMarkerRenderer.Draw(new SkiaChartCanvas(surface.Canvas, resources), series, new(5, 5, 60, 24));
                using var image = surface.Snapshot(); using var bitmap = SKBitmap.FromImage(image);
                int colored = 0; for (int y = 5; y < 29; y++) for (int x = 5; x < 65; x++) if (bitmap.GetPixel(x, y).Blue > 40) colored++;
                TestSuite.True(colored > 8); TestSuite.Equal(surface.Canvas.SaveCount, 1);
            });
        suite.Run("legend marker invalid and transparent bounds preserve canvas", () =>
        {
            using var series = new RenderableSeries(); var c = new RecordingCanvas();
            LegendMarkerRenderer.Draw(c, series, default); LegendMarkerRenderer.Draw(c, series, new(double.NaN, 0, 10, 10));
            TestSuite.Equal(c.Commands.Count, 0);
            series.Opacity = 0; LegendMarkerRenderer.Draw(c, series, new(0, 0, 30, 15));
            TestSuite.True(c.Commands.Where(x => x.Color is not null).All(x => x.Color!.Value.A == 0)); TestSuite.Equal(c.SaveDepth, 0);
        });
    }
    private sealed class PulseData : IDataSeries
    {
        public int Count => 0;
        public int ChannelCount => 2;
        public long Revision => 0;
        public string SeriesName { get; set; } = "Pulse";
        public event EventHandler? DataSeriesChanged;
        public int SnapshotReads;
        public SeriesSnapshot Snapshot() { SnapshotReads++; throw new InvalidOperationException("Legend must not snapshot source data."); }
        public void Pulse() => DataSeriesChanged?.Invoke(this, EventArgs.Empty);
    }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static long Measure(PulseData data)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) data.Pulse();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    private static void Benchmark()
    {
        var data = new PulseData(); using var model = Model(); model.RenderableSeries[0].DataSeries = data;
        Measure(data); long baseline = Measure(data);
        using var source = new ChartLegendSource { Model = model }; var rows = source.Entries.ToArray(); int updates = 0;
        foreach (var row in rows) row.PropertyChanged += (_, _) => updates++;
        long passes = source.MembershipPassCount; Measure(data); long observed = Measure(data);
        Parallel.For(0, 1000, _ => data.Pulse());
        TestSuite.Equal(data.SnapshotReads, 0); TestSuite.Equal(updates, 0); TestSuite.Equal(source.MembershipPassCount, passes);
        Same(source.Entries, rows); TestSuite.Equal(observed, baseline);
        var report = new { updates = 10000, baselineManagedBytes = baseline, observedManagedBytes = observed, extraLegendBytes = observed - baseline, snapshotReads = data.SnapshotReads, membershipPassesDuringUpdates = source.MembershipPassCount - passes,
            scope = "Data-notification observation only. Both runs include existing model/series notification allocations; legend adds zero. No rendering, GPU or FPS claim." };
        Directory.CreateDirectory("artifacts/performance"); File.WriteAllText("artifacts/performance/legend-observation.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PERF_LEGEND " + JsonSerializer.Serialize(report));
    }
}
