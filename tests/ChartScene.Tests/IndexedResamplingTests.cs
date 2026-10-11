using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Numerics;

public static class IndexedResamplingTests
{
    // Independent scalar oracle: keep every finite run's first, earliest minimum/maximum,
    // and last index within each screen bucket, then retain every invalid sample verbatim.
    internal static int[] Reference(SeriesSnapshot d, int channel, DoubleRange range, int pixels, bool log = false)
    {
        var (start, end) = d.VisibleIndices(range);
        if (!d.IsSortedAscending || end - start <= (long)pixels * 4) return Enumerable.Range(start, end - start).ToArray();
        double LogRatio(double x)
        {
            double relative = (x - range.Min) / range.Min;
            return relative < .5 ? double.LogP1(relative) : Math.Log(x) - Math.Log(range.Min);
        }
        int Bucket(int i)
        {
            double x = d[0, i];
            return x < range.Min ? -1 : x >= range.Max ? pixels :
                (int)Math.Clamp(Math.Floor((log ? LogRatio(x) / LogRatio(range.Max) : (x - range.Min) / range.Size) * pixels), -1, pixels);
        }
        var output = new List<int>();
        int position = start;
        while (position < end)
        {
            if (!double.IsFinite(d[channel, position])) { output.Add(position++); continue; }
            int first = position, minimum = position, maximum = position, bucket = Bucket(position);
            while (++position < end && double.IsFinite(d[channel, position]) && Bucket(position) == bucket)
            {
                if (d[channel, position] < d[channel, minimum]) minimum = position;
                if (d[channel, position] > d[channel, maximum]) maximum = position;
            }
            foreach (int i in new[] { first, minimum, maximum, position - 1 }.Distinct().Order()) output.Add(i);
        }
        return output.ToArray();
    }
    private static SeriesSnapshot Data(int count, Func<int, double>? x = null, Func<int, double>? y = null)
    {
        var d = new XyDataSeries(count);
        d.AppendRange(Enumerable.Range(0, count).Select(x ?? (i => i)).ToArray(),
            Enumerable.Range(0, count).Select(y ?? (i => Math.Sin(i * .013))).ToArray());
        return d.Snapshot();
    }
    private static void Same(int[] actual, int[] expected)
    {
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new Exception($"Source-index mismatch: actual {actual.Length}, expected {expected.Length}; first difference " +
                Enumerable.Range(0, Math.Min(actual.Length, expected.Length)).FirstOrDefault(i => actual[i] != expected[i], -1));
    }
    public static void Register(TestSuite suite)
    {
        suite.Run("indexed extrema exact seeded scalar equivalence", () =>
        {
            var random = new Random(47191);
            for (int scenario = 0; scenario < 36; scenario++)
            {
                int n = 16385 + random.Next(15000);
                double[] x = new double[n], y = new double[n]; double time = scenario % 3 == 0 ? 1e12 : 0;
                for (int i = 0; i < n; i++)
                {
                    time += random.Next(4) * .125; x[i] = time;
                    y[i] = random.Next(97) == 0 ? double.NaN : random.Next(-20, 21);
                }
                var d = new XyDataSeries(); d.AppendRange(x, y); var snapshot = d.Snapshot(); var index = new MinMaxIndex(snapshot);
                for (int query = 0; query < 8; query++)
                {
                    int a = random.Next(n / 3), b = random.Next(n * 2 / 3, n);
                    var range = new DoubleRange(x[a] - .0625, x[b] + .0625); int pixels = random.Next(1, 300);
                    Same(index.Sample(range, pixels, out _), Reference(snapshot, 1, range, pixels));
                }
            }
        });
        suite.Run("indexed logarithmic buckets retain low-decade extrema", () =>
        {
            var d = Data(50000, i => Math.Pow(10, -6 + i * 12.0 / 49999), i => Math.Sin(i * .3));
            var range = new DoubleRange(1e-6, 1e6);
            var selected = IndexedResampling.MinMax(d, 1, range, 800, true);
            Same(selected, Reference(d, 1, range, 800, true));
            TestSuite.True(selected.Count(i => d[0, i] < 1) > 500);
        });
        suite.Run("indexed subnormal and adjacent-large log ranges", () =>
        {
            foreach (var data in new[] { Data(30000, i => double.Epsilon * (i + 1)), Data(30000, i => 1e200 + i * 2e184) })
            {
                var range = new DoubleRange(data[0, 0], data[0, data.Count - 1]);
                Same(new MinMaxIndex(data).Sample(range, 25, out _, true), Reference(data, 1, range, 25, true));
            }
        });
        suite.Run("indexed invalid samples retained across all block boundaries", () =>
        {
            var d = Data(65537, y: i => i % 31 == 0 || i is >= 1000 and < 3000 ? double.NaN : i % 7);
            var range = new DoubleRange(0, d.Count - 1);
            var actual = new MinMaxIndex(d).Sample(range, 16, out _);
            Same(actual, Reference(d, 1, range, 16));
            var set = actual.ToHashSet();
            for (int i = 0; i < d.Count; i++) if (double.IsNaN(d[1, i])) TestSuite.True(set.Contains(i));
        });
        suite.Run("indexed tied extrema retain earliest original index", () =>
        {
            foreach (var data in new[] { Data(30000, y: _ => 7), Data(30000, y: i => i % 2) })
            {
                var range = new DoubleRange(0, data.Count - 1);
                Same(new MinMaxIndex(data).Sample(range, 13, out _), Reference(data, 1, range, 13));
            }
        });
        suite.Run("indexed clip boundaries include neighboring samples", () =>
        {
            var d = Data(50000); var index = new MinMaxIndex(d);
            foreach (var range in new[] { new DoubleRange(-100, -50), new DoubleRange(60000, 70000), new DoubleRange(0, 1), new DoubleRange(133.1, 49995.4), new DoubleRange(10000, 35000) })
                Same(index.Sample(range, 3, out _), Reference(d, 1, range, 3));
        });
        suite.Run("indexed duplicates and all-X-equal series", () =>
        {
            foreach (var d in new[] { Data(30000, i => i / 100), Data(30000, _ => 4) })
                Same(new MinMaxIndex(d).Sample(new(0, 301), 11, out _), Reference(d, 1, new(0, 301), 11));
        });
        suite.Run("indexed unsorted series preserve insertion order", () =>
        {
            var d = new XyDataSeries { AcceptsUnsortedData = true }; d.AppendRange([3, 1, 2], [7, 8, 9]);
            Same(IndexedResampling.MinMax(d.Snapshot(), 1, new(0, 5), 1), [0, 1, 2]);
            TestSuite.Throws<ArgumentException>(() => new MinMaxIndex(d.Snapshot()));
        });
        suite.Run("indexed channels snapshots and returned arrays are isolated", () =>
        {
            var d = new XyyDataSeries();
            for (int i = 0; i < 20000; i++) d.Append(i, Math.Sin(i * .02), Math.Cos(i * .05));
            var old = d.Snapshot(); var range = new DoubleRange(0, 19999);
            for (int channel = 1; channel <= 2; channel++)
            {
                var actual = IndexedResampling.MinMax(old, channel, range, 100);
                Same(actual, Reference(old, channel, range, 100)); actual[0] = -123;
                Same(IndexedResampling.MinMax(old, channel, range, 100), Reference(old, channel, range, 100));
            }
            d.Update(500, 500, 100, -100);
            Same(IndexedResampling.MinMax(d.Snapshot(), 1, range, 100), Reference(d.Snapshot(), 1, range, 100));
            Same(IndexedResampling.MinMax(old, 1, range, 100), Reference(old, 1, range, 100));
        });
        suite.Run("indexed concurrent readers share immutable metadata", () =>
        {
            var d = Data(100000); var range = new DoubleRange(0, 99999); var expected = Reference(d, 1, range, 100);
            Parallel.For(0, 16, _ => Same(IndexedResampling.MinMax(d, 1, range, 100), expected));
        });
        suite.Run("indexed cache does not retain discarded snapshots", () =>
        {
            var weak = DiscardSnapshot();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            TestSuite.True(!weak.TryGetTarget(out _));
        });
        suite.Run("indexed invalid inputs diagnosed before traversal", () =>
        {
            var d = Data(10);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => IndexedResampling.MinMax(d, -1, new(0, 10), 10));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => IndexedResampling.MinMax(d, 1, default, 10));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => IndexedResampling.MinMax(d, 1, new(0, 10), 0));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => IndexedResampling.MinMax(d, 1, new(0, 10), 10, true));
            Same(IndexedResampling.MinMax(d, 1, new(0, 10), int.MaxValue), Enumerable.Range(0, 10).ToArray());
            TestSuite.Equal(new MinMaxIndex(Data(0)).Sample(new(0, 1), 10, out _).Length, 0);
        });
        suite.Run("million-point indexed queries have bounded work and storage", () =>
        {
            var d = Data(1_000_000); var index = new MinMaxIndex(d);
            TestSuite.True(index.StorageBytes < 1_000_000);
            foreach (int pixels in new[] { 1, 64, 512, 1024 })
            {
                var range = new DoubleRange(17.3, 999965.7);
                var actual = index.Sample(range, pixels, out var work);
                Same(actual, Reference(d, 1, range, pixels));
                TestSuite.True(work.ScannedPoints < d.Count / 4 && work.AcceptedNodes > 0);
            }
        });
        suite.Run("renderer reuses only matching private index buffers", () =>
        {
            using var model = new ChartScene.Charting.ChartModel { ShowLegend = false };
            var data = new XyDataSeries();
            data.AppendRange(Enumerable.Range(0, 100000).Select(i => (double)i + 1).ToArray(), Enumerable.Range(0, 100000).Select(i => Math.Sin(i * .001)).ToArray());
            var series = new ChartScene.Charting.FastLineRenderableSeries { DataSeries = data };
            model.XAxis.IsVisible = model.YAxis.IsVisible = false;
            model.RenderableSeries.Add(series); var renderer = new ChartScene.Rendering.ChartRenderer();
            void Draw(int width = 900) => renderer.Render(new ChartScene.Rendering.RecordingCanvas(), model, width, 500);
            Draw(); TestSuite.Equal(renderer.ResamplingCacheHits, 0);
            Draw(); TestSuite.Equal(renderer.ResamplingCacheHits, 1); TestSuite.Equal(renderer.ResamplingScannedPoints, 0L);
            model.YAxis.VisibleRange = new(-2, 2); Draw(); TestSuite.Equal(renderer.ResamplingCacheHits, 1);
            series.Stroke = ColorRgba.White; Draw(); TestSuite.Equal(renderer.ResamplingCacheHits, 1);
            model.XAxis.VisibleRange = new(100, 90000); Draw(); TestSuite.Equal(renderer.ResamplingCacheHits, 0);
            Draw(1000); TestSuite.Equal(renderer.ResamplingCacheHits, 0);
            data.Update(500, 501, 7); Draw(1000); TestSuite.Equal(renderer.ResamplingCacheHits, 0);
            model.XAxis.Scale = ChartScene.Charting.AxisScale.Logarithmic; Draw(1000); TestSuite.Equal(renderer.ResamplingCacheHits, 0);
            Draw(1000); TestSuite.Equal(renderer.ResamplingCacheHits, 1);
            // A new source with the same count and revision is still not the old snapshot.
            var replacement = new XyDataSeries(); replacement.AppendRange(data.Snapshot().Channel(0).ToArray(), data.Snapshot().Channel(1).ToArray());
            series.DataSeries = replacement; Draw(1000); TestSuite.Equal(renderer.ResamplingCacheHits, 0);
        });
        suite.Run("resampling performance evidence and equivalence", Benchmark);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<SeriesSnapshot> DiscardSnapshot()
    {
        var data = Data(20000); _ = IndexedResampling.MinMax(data, 1, new(0, 19999), 100);
        return new(data);
    }
    private static void Benchmark()
    {
        var data = Data(1_000_000, y: i => Math.Sin(i * .00007) + .3 * Math.Sin(i * .017));
        var range = new DoubleRange(0, data.Count - 1);
        var timer = Stopwatch.StartNew(); var index = new MinMaxIndex(data); double buildMs = timer.Elapsed.TotalMilliseconds;
        Same(index.Sample(range, 800, out var work), Reference(data, 1, range, 800));
        // Scalar production baseline from before the indexed implementation, not the LINQ oracle.
        var baseline = new double[21]; var indexed = new double[21];
        for (int i = 0; i < 5; i++) { Scalar(data, range, 800); index.Sample(range, 800, out _); }
        long scalarBytes = 0, indexedBytes = 0;
        for (int i = 0; i < baseline.Length; i++)
        {
            var r = new DoubleRange(i * 3.1, data.Count - 1 - i * 2.3);
            long before = GC.GetAllocatedBytesForCurrentThread(); timer.Restart(); var a = Scalar(data, r, 800);
            baseline[i] = timer.Elapsed.TotalMilliseconds; scalarBytes += GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread(); timer.Restart(); var b = index.Sample(r, 800, out _);
            indexed[i] = timer.Elapsed.TotalMilliseconds; indexedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
            Same(a, b);
        }
        Array.Sort(baseline); Array.Sort(indexed);
        var evidence = new { sampleCount = data.Count, pixels = 800, iterations = baseline.Length, indexBuildMilliseconds = buildMs,
            indexStorageBytes = index.StorageBytes, scalarP50Milliseconds = baseline[10], scalarP95Milliseconds = baseline[19],
            indexedP50Milliseconds = indexed[10], indexedP95Milliseconds = indexed[19], medianSpeedup = baseline[10] / indexed[10],
            scalarAllocatedBytesPerQuery = scalarBytes / baseline.Length, indexedAllocatedBytesPerQuery = indexedBytes / indexed.Length, work,
            scope = "CPU extrema query only; local runtime and driver independent. Not GPU, FPS, streaming or presentation timing." };
        Directory.CreateDirectory("artifacts/performance");
        File.WriteAllText("artifacts/performance/resampling.json", JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PERF extrema: scalar {baseline[10]:F3} ms; indexed {indexed[10]:F3} ms; {baseline[10] / indexed[10]:F2}x (CPU queries)");
    }
    private static int[] Scalar(SeriesSnapshot data, DoubleRange visible, int pixels)
    {
        var (start, end) = data.VisibleIndices(visible);
        if (end - start <= pixels * 4 || !data.IsSortedAscending) return Enumerable.Range(start, end - start).ToArray();
        var result = new List<int>(pixels * 4 + 8);
        int bucket = int.MinValue, first = -1, last = -1, min = -1, max = -1;
        void Flush()
        {
            if (first < 0) return;
            Span<int> candidates = stackalloc int[] { first, min, max, last }; candidates.Sort();
            foreach (int i in candidates) if (i >= 0 && (result.Count == 0 || result[^1] != i)) result.Add(i);
            first = last = min = max = -1;
        }
        for (int i = start; i < end; i++)
        {
            double y = data[1, i];
            if (!double.IsFinite(y)) { Flush(); result.Add(i); bucket = int.MinValue; continue; }
            int next = (int)Math.Clamp(Math.Floor(visible.Fraction(data[0, i]) * pixels), -1, pixels);
            if (next != bucket) { Flush(); bucket = next; }
            if (first < 0) first = min = max = i;
            last = i;
            if (y < data[1, min]) min = i;
            if (y > data[1, max]) max = i;
        }
        Flush(); return result.ToArray();
    }
}
