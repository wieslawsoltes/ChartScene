using System.Diagnostics;
using System.Text.Json;
using ChartScene.Core;
using ChartScene.Samples;

public static class ExampleBrowserTests
{
    public static void Register(TestSuite suite)
    {
        suite.Run("browser journal retains back forward and drops abandoned future", () =>
        {
            var history = new NavigationJournal<string>(); int changes = 0; history.Changed += (_, _) => changes++;
            history.Navigate("line"); history.Navigate("heatmap"); history.Navigate("source");
            TestSuite.True(history.GoBack()); TestSuite.Equal(history.Current, "heatmap");
            TestSuite.True(history.GoForward()); TestSuite.Equal(history.Current, "source");
            history.GoBack(); history.Navigate("scatter"); TestSuite.True(!history.CanGoForward);
            TestSuite.True(!history.Navigate("scatter")); TestSuite.Equal(changes, 7);
            TestSuite.True(history.Snapshot().SequenceEqual(new[] { "line", "heatmap", "scatter" }));
        });
        suite.Run("browser history bounds memory without losing current route", () =>
        {
            var history = new NavigationJournal<int>(4);
            for (int i = 0; i < 100; i++) history.Navigate(i);
            TestSuite.Equal(history.Count, 4); TestSuite.Equal(history.Current, 99);
            var old = history.Snapshot(); history.GoBack(); history.Navigate(200);
            TestSuite.Equal(old[^1], 99); TestSuite.Equal(history.Current, 200);
            while (history.GoBack()) { }
            TestSuite.Equal(history.Current, 96); TestSuite.True(!history.GoBack());
            history.Clear(); TestSuite.True(!history.HasCurrent && !history.CanGoBack && !history.CanGoForward);
            TestSuite.Throws<InvalidOperationException>(() => { _ = history.Current; });
        });
        suite.Run("browser journal comparer and validation", () =>
        {
            var history = new NavigationJournal<string>(8, StringComparer.OrdinalIgnoreCase);
            history.Navigate("LINE"); TestSuite.True(!history.Navigate("line"));
            TestSuite.Throws<ArgumentNullException>(() => history.Navigate(null!));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => new NavigationJournal<int>(1));
        });
        suite.Run("example search includes description and source without constructing charts", () =>
        {
            int factories = 0;
            var examples = new[] {
                new ChartExample("line", "Wave", "Scientific", "Measure a transient", () => { factories++; throw new Exception(); }, "var fft = Fourier.Transform(data);"),
                new ChartExample("bars", "Region", "Statistical", "Compare regions", () => { factories++; throw new Exception(); }, "var stack = new StackedColumnRenderableSeries();") };
            var index = new ExampleSearchIndex(examples);
            TestSuite.Equal(index.Search("FOURIER").Single().Id, "line");
            TestSuite.Equal(index.Search("transient scientific").Single().Id, "line");
            TestSuite.Equal(index.Search("stackedcolumn").Single().Id, "bars");
            TestSuite.Equal(index.Search("region transient").Count, 0); TestSuite.Equal(factories, 0);
        });
        suite.Run("normalized example search reuses immutable bounded results", () =>
        {
            var index = new ExampleSearchIndex(SampleCatalog.Examples);
            var first = index.Search("  LINE\tchart ");
            TestSuite.True(ReferenceEquals(first, index.Search("line chart")));
            TestSuite.True(ReferenceEquals(index.All, index.Search(null)));
            TestSuite.Equal(index.Statistics.Executions, 1L);
            TestSuite.Equal(index.Statistics.ExaminedEntries, (long)SampleCatalog.Examples.Count);
            TestSuite.Throws<NotSupportedException>(() => ((IList<ChartExample>)first).Clear());
            for (int i = 0; i < 100; i++) index.Search("no-result-" + i);
            TestSuite.Equal(index.Statistics.CachedQueries, 16);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => index.Search(new string('a', 4097)));
        });
        suite.Run("example search matches scalar catalogue oracle", () =>
        {
            var index = new ExampleSearchIndex(SampleCatalog.Examples);
            foreach (string query in new[] { "", "NaN", "sin", "logarithmic", "data series", "PaletteProvider", "confidence", "impossible match" })
            {
                string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var expected = SampleCatalog.Examples.Where(e => terms.All(t => string.Concat(e.Id," ",e.Title," ",e.Category," ",e.Description," ",e.Source).Contains(t,StringComparison.OrdinalIgnoreCase))).Select(e => e.Id);
                TestSuite.True(index.Search(query).Select(e => e.Id).SequenceEqual(expected));
            }
        });
        suite.Run("example search concurrent readers publish one result", () =>
        {
            var index = new ExampleSearchIndex(SampleCatalog.Examples); var results = new IReadOnlyList<ChartExample>[16];
            Parallel.For(0, 16, i => results[i] = index.Search("data"));
            TestSuite.True(results.All(r => ReferenceEquals(r, results[0]))); TestSuite.Equal(index.Statistics.Executions, 1L);
        });
        suite.Run("search rejects oversized whitespace without changing valid cache", () =>
        {
            var index = new ExampleSearchIndex(SampleCatalog.Examples); var result = index.Search("data"); var before = index.Statistics;
            foreach (char value in new[] { ' ', '\t', '\u2003', 'a' })
                TestSuite.Throws<ArgumentOutOfRangeException>(() => index.Search(new string(value, 4097)));
            TestSuite.Equal(index.Statistics, before);
            TestSuite.True(ReferenceEquals(result, index.Search("data")));
            TestSuite.True(ReferenceEquals(index.All, index.Search(new string(' ', 4096))));
        });
        suite.Run("search raw equality empty transitions and cache eviction stay coherent", () =>
        {
            var index = new ExampleSearchIndex(SampleCatalog.Examples);
            var first = index.Search("data"); var equal = new string("data".ToCharArray());
            TestSuite.True(ReferenceEquals(first, index.Search(equal)));
            foreach (var empty in new string?[] { null, null, "", "", " \t", " \t", null })
                TestSuite.True(ReferenceEquals(index.All, index.Search(empty)));
            for (int i = 0; i < 32; i++) index.Search("absent-" + i);
            TestSuite.True(first.SequenceEqual(index.Search("data")));
            TestSuite.Equal(index.Statistics.CachedQueries, 16);
            TestSuite.Throws<NotSupportedException>(() => ((IList<ChartExample>)first).Clear());
        });
        suite.Run("repeated search avoids normalization allocation and source scans", () =>
        {
            var index = new ExampleSearchIndex(SampleCatalog.Examples); const string query = "  LINE\tchart ";
            var expected = index.Search(query);
            for (int i = 0; i < 100; i++) index.Search(query);
            var before = index.Statistics; long started = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
                if (!ReferenceEquals(expected, index.Search(query))) throw new Exception("Repeated search did not reuse the published result.");
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var after = index.Statistics;
            TestSuite.Equal(bytes, 0L); TestSuite.Equal(after.Executions, before.Executions);
            TestSuite.Equal(after.ExaminedEntries, before.ExaminedEntries); TestSuite.Equal(after.CacheHits - before.CacheHits, 10000L);
            var report = new { queries = 10000, allocatedBytes = bytes, sourceEntriesVisited = after.ExaminedEntries - before.ExaminedEntries, milliseconds,
                scope = "Warm identical-input metadata search only. Excludes initial normalization, UI layout/rendering and GPU/presentation." };
            Directory.CreateDirectory("artifacts/performance");
            File.WriteAllText("artifacts/performance/example-search.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PERF_SEARCH " + JsonSerializer.Serialize(report));
        });
    }
}
