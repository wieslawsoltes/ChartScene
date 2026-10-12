using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using ChartScene.Charting;

public static class LegendRefreshTests
{
    private static ChartModel Model(int count = 32)
    {
        var model = new ChartModel();
        for (int i = 0; i < count; i++) model.RenderableSeries.Add(new() { SeriesName = "Channel " + i, IsSelected = i % 2 == 0 });
        return model;
    }
    public static void Register(TestSuite suite)
    {
        suite.Run("legend removal does not move surviving entries", () =>
        {
            using var model = Model(); using var source = new ChartLegendSource { Model = model };
            var before = source.Entries.ToArray(); var events = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)source.Entries).CollectionChanged += (_, e) => events.Add(e.Action);
            model.RenderableSeries[0].IncludeInLegend = false;
            TestSuite.True(events.SequenceEqual(new[] { NotifyCollectionChangedAction.Remove }));
            TestSuite.True(source.Entries.SequenceEqual(before.Skip(1)));
            events.Clear(); source.SourceMode = LegendSourceMode.Selected;
            TestSuite.True(events.All(e => e == NotifyCollectionChangedAction.Remove));
            TestSuite.True(source.Entries.SequenceEqual(before.Skip(1).Where(e => e.IsSelected)));
        });
        suite.Run("legend native consumers observe one settled initial generation", () =>
        {
            using var model = Model(512); using var source = new ChartLegendSource();
            int completed = 0, edits = 0;
            ((INotifyCollectionChanged)source.Entries).CollectionChanged += (_, _) => { edits++; TestSuite.True(source.IsRefreshing); };
            source.Refreshed += (_, _) => { completed++; TestSuite.True(!source.IsRefreshing); TestSuite.Equal(source.Entries.Count, source.Model is null ? 0 : 512); };
            source.Model = model; TestSuite.Equal(completed, 1); TestSuite.Equal(edits, 512);
        });
        suite.Run("legend no-op metadata never publishes membership completion", () =>
        {
            using var model = Model(); using var source = new ChartLegendSource { Model = model }; int completed = 0;
            source.Refreshed += (_, _) => completed++;
            source.Model = model; source.SourceMode = source.SourceMode;
            model.RenderableSeries[0].SeriesName = "Changed"; model.Title = "Changed";
            model.RenderableSeries[0].StrokeThickness = 4; model.RenderableSeries[0].IsVisible = false;
            TestSuite.Equal(completed, 0);
        });
        suite.Run("legend queued reentrant replacement completes only the final model", () =>
        {
            using var first = Model(16); using var second = Model(7); using var source = new ChartLegendSource();
            bool changed = false; int completed = 0;
            ((INotifyCollectionChanged)source.Entries).CollectionChanged += (_, _) =>
            { if (!changed) { changed = true; source.Model = second; } };
            source.Refreshed += (_, _) =>
            {
                completed++; TestSuite.True(!source.IsRefreshing);
                if (source.Model is null) TestSuite.Equal(source.Entries.Count, 0);
                else
                {
                    TestSuite.True(ReferenceEquals(source.Model, second));
                    TestSuite.True(source.Entries.Select(e => e.Series).SequenceEqual(second.RenderableSeries));
                }
            };
            source.Model = first; TestSuite.Equal(completed, 1);
        });
        suite.Run("legend disposal during pruning completes with no observed entries", () =>
        {
            using var model = Model(); var source = new ChartLegendSource { Model = model }; bool disposed = false; int completed = 0;
            ((INotifyCollectionChanged)source.Entries).CollectionChanged += (_, e) =>
            { if (!disposed && e.Action == NotifyCollectionChangedAction.Remove) { disposed = true; source.Dispose(); } };
            source.Refreshed += (_, _) => { completed++; TestSuite.Equal(source.Entries.Count, 0); TestSuite.True(!source.IsRefreshing); };
            source.SourceMode = LegendSourceMode.Selected;
            TestSuite.Equal(completed, 1); source.Dispose();
        });
        suite.Run("legend completion supports a subsequent user filter change", () =>
        {
            using var model = Model(); using var source = new ChartLegendSource(); int completed = 0;
            source.Refreshed += (_, _) => { completed++; if (source.SourceMode == LegendSourceMode.All) source.SourceMode = LegendSourceMode.Selected; };
            source.Model = model;
            TestSuite.Equal(completed, 2); TestSuite.Equal(source.Entries.Count, 16); TestSuite.True(!source.IsRefreshing);
        });
        suite.Run("legend filter notification-work evidence", () =>
        {
            using var model = Model(2048); using var source = new ChartLegendSource { Model = model };
            int moves = 0, removals = 0, resets = 0, completed = 0;
            ((INotifyCollectionChanged)source.Entries).CollectionChanged += (_, e) =>
            { if (e.Action == NotifyCollectionChangedAction.Move) moves++; if (e.Action == NotifyCollectionChangedAction.Remove) removals++; if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
            source.Refreshed += (_, _) => completed++;
            long start = Stopwatch.GetTimestamp(); model.RenderableSeries[0].IncludeInLegend = false;
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            TestSuite.Equal(moves, 0); TestSuite.Equal(removals, 1); TestSuite.Equal(resets, 0); TestSuite.Equal(completed, 1);
            TestSuite.Equal(source.EntryCreationCount, 2048L);
            var report = new { sourceSeries = 2048, remainingEntries = source.Entries.Count, moves, removals, resets,
                completedNotifications = completed, milliseconds = ms,
                scope = "Single first-entry exclusion: removes that entry instead of moving every survivor. Includes projection; excludes native UI layout, drawing, GPU and presentation. Not an O(1) algorithm claim." };
            Directory.CreateDirectory("artifacts/performance");
            File.WriteAllText("artifacts/performance/legend-filter.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PERF_LEGEND_FILTER " + JsonSerializer.Serialize(report));
        });
    }
}
