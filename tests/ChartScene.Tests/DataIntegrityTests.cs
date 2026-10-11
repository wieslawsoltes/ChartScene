using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChartScene.Core;
using ChartScene.Data;

public static class DataIntegrityTests
{
    private static void Check(DataSeries data, List<double[]> expected)
    {
        var snapshot = data.Snapshot(); TestSuite.Equal(snapshot.Count, expected.Count);
        for (int i = 0; i < expected.Count; i++) for (int c = 0; c < data.ChannelCount; c++) TestSuite.True(snapshot[c, i].Equals(expected[i][c]));
        bool sorted = expected.Zip(expected.Skip(1), (a, b) => a[0] <= b[0]).All(x => x);
        TestSuite.Equal(snapshot.IsSortedAscending, sorted);
        if (sorted && snapshot.Count > 0)
            TestSuite.Equal(snapshot.LowerBound(snapshot[0, snapshot.Count - 1]), expected.FindIndex(row => row[0] == expected[^1][0]));
    }
    private static double[][] Columns(IEnumerable<double[]> rows, int channels = 3)
    { var array = rows.ToArray(); return Enumerable.Range(0, channels).Select(c => array.Select(row => row[c]).ToArray()).ToArray(); }
    private static void Unchanged(DataSeries data, Action invalid)
    {
        var before = data.Snapshot(); int events = 0;
        void Changed(object? sender, EventArgs e) => events++;
        data.DataSeriesChanged += Changed;
        try { TestSuite.Throws<ArgumentException>(invalid); }
        finally { data.DataSeriesChanged -= Changed; }
        TestSuite.True(ReferenceEquals(before, data.Snapshot())); TestSuite.Equal(data.Revision, before.Revision); TestSuite.Equal(events, 0);
    }
    public static void Register(TestSuite suite)
    {
        suite.Run("data randomized ring mutation scalar model", () =>
        {
            var random = new Random(276891); var data = new DataSeries(3, 4) { AcceptsUnsortedData = true }; var expected = new List<double[]>();
            double[] Row() => [random.Next(-10, 40), random.Next(9) == 0 ? double.NaN : random.Next(-50, 50), random.NextDouble()];
            void Trim() { if (data.FifoCapacity > 0 && expected.Count > data.FifoCapacity) expected.RemoveRange(0, expected.Count - data.FifoCapacity); }
            for (int step = 0; step < 5000; step++)
            {
                int operation = random.Next(9);
                if (operation == 0)
                { var row = Row(); data.Append(row); expected.Add(row); Trim(); }
                else if (operation == 1)
                { var rows = Enumerable.Range(0, random.Next(30)).Select(_ => Row()).ToArray(); data.AppendRange(Columns(rows)); expected.AddRange(rows); Trim(); }
                else if (operation == 2)
                { int capacity = random.Next(4) == 0 ? 0 : random.Next(1, 31); data.FifoCapacity = capacity; Trim(); }
                else if (operation == 3 && expected.Count > 0)
                { int index = random.Next(expected.Count); var row = Row(); data.Update(index, row); expected[index] = row; }
                else if (operation == 4)
                {
                    int index = random.Next(expected.Count + 1), length = random.Next(expected.Count - index + 1);
                    data.RemoveRange(index, length); expected.RemoveRange(index, length);
                }
                else if (operation == 5 && data.FifoCapacity == 0)
                {
                    int index = random.Next(expected.Count + 1); var rows = Enumerable.Range(0, random.Next(8)).Select(_ => Row()).ToArray();
                    data.InsertRange(index, Columns(rows)); expected.InsertRange(index, rows);
                }
                else if (operation == 6)
                {
                    int index = random.Next(expected.Count + 1), length = random.Next(expected.Count - index + 1);
                    var rows = Enumerable.Range(0, length).Select(_ => Row()).ToArray(); data.UpdateRange(index, Columns(rows));
                    for (int i = 0; i < length; i++) expected[index + i] = rows[i];
                }
                else if (operation == 7)
                {
                    int index = random.Next(expected.Count + 1), length = random.Next(expected.Count - index + 1), channel = random.Next(3);
                    double[] values = Enumerable.Range(0, length).Select(_ => (double)random.Next(40)).ToArray();
                    data.UpdateChannelRange(index, channel, values);
                    for (int i = 0; i < length; i++) expected[index + i][channel] = values[i];
                }
                else
                { expected = Enumerable.Range(0, random.Next(30)).Select(_ => Row()).ToList(); data.ReplaceAll(Columns(expected)); Trim(); }
                Check(data, expected);
            }
        });
        suite.Run("FIFO eviction restores sortedness and binary search", () =>
        {
            var data = new XyDataSeries { AcceptsUnsortedData = true, FifoCapacity = 3 };
            data.AppendRange([4, 1, 2], [0, 0, 0]); TestSuite.True(!data.Snapshot().IsSortedAscending);
            data.Append(3, 0); TestSuite.True(data.Snapshot().IsSortedAscending);
            data.AcceptsUnsortedData = false; TestSuite.Equal(data.Snapshot().LowerBound(2), 1);
        });
        suite.Run("FIFO oversized batch keeps newest rows and exact sortedness", () =>
        {
            var data = new DataSeries(2) { AcceptsUnsortedData = true, FifoCapacity = 2 };
            data.AppendRange([4, 1], [40, 10]); data.AppendRange([9, 8, 7, 2, 3], [90, 80, 70, 20, 30]);
            TestSuite.True(data.Snapshot().IsSortedAscending); TestSuite.Equal(data.Count, 2);
            TestSuite.Near(data.Snapshot()[0, 0], 2); TestSuite.Near(data.Snapshot()[1, 1], 30);
        });
        suite.Run("FIFO snapshot copies wrapped columns without aliasing", () =>
        {
            var data = new XyyDataSeries { FifoCapacity = 7 };
            for (int i = 0; i < 25; i++) data.Append(i, i * 2, i * 3);
            var old = data.Snapshot(); data.AppendRange([25, 26, 27, 28], [50, 52, 54, 56], [75, 78, 81, 84]);
            TestSuite.Equal(old.Count, 7); TestSuite.Near(old[0, 0], 18);
            for (int i = 0; i < 7; i++) { TestSuite.Near(data.Snapshot()[0, i], i + 22); TestSuite.Near(data.Snapshot()[2, i], (i + 22) * 3); }
        });
        suite.Run("data range update is one coherent notification", () =>
        {
            var data = new XyyDataSeries(); data.AppendRange([0, 1, 2, 3], [0, 2, 4, 6], [0, 3, 6, 9]);
            int events = 0; long revision = data.Revision;
            data.DataSeriesChanged += (_, _) => { events++; TestSuite.Near(data.Snapshot()[1, 1], 20); TestSuite.Near(data.Snapshot()[2, 2], 60); };
            data.UpdateRange(1, [1, 2], [20, 40], [30, 60]);
            TestSuite.Equal(events, 1); TestSuite.Equal(data.Revision, revision + 1);
        });
        suite.Run("data range update handles wrapped FIFO storage", () =>
        {
            var data = new DataSeries(2) { FifoCapacity = 5 };
            for (int i = 0; i < 12; i++) data.Append(i, -i);
            data.UpdateRange(1, [8, 9, 10], [80, 90, 100]);
            TestSuite.True(data.Snapshot().Channel(1).Span.SequenceEqual(new double[] { -7, 80, 90, 100, -11 }));
            data.UpdateChannelRange(2, 1, [4, 5, 6]);
            TestSuite.True(data.Snapshot().Channel(1).Span.SequenceEqual(new double[] { -7, 80, 4, 5, 6 }));
        });
        suite.Run("data batch rejects last invalid value before any writes", () =>
        {
            var data = new DataSeries(2); data.AppendRange([0, 1, 2], [0, 1, 2]);
            Unchanged(data, () => data.UpdateRange(0, [0, 1, 2], [4, 5, double.PositiveInfinity]));
            Unchanged(data, () => data.UpdateChannelRange(0, 1, [4, 5, double.PositiveInfinity]));
            Unchanged(data, () => data.ReplaceAll([0, 1], [4, double.NegativeInfinity]));
            Unchanged(data, () => data.AppendRange([3, 4], [3, double.PositiveInfinity]));
            Unchanged(data, () => data.InsertRange(1, [.25, .5], [4, double.NegativeInfinity]));
        });
        suite.Run("data update validates internal and neighbor ordering", () =>
        {
            var data = new DataSeries(2); data.AppendRange([0, 1, 2, 3, 4], [0, 1, 2, 3, 4]);
            Unchanged(data, () => data.UpdateRange(1, [3, 2], [0, 0]));
            Unchanged(data, () => data.UpdateRange(1, [-1, 2], [0, 0]));
            Unchanged(data, () => data.UpdateRange(1, [1, 4], [0, 0]));
            Unchanged(data, () => data.UpdateChannelRange(2, 0, [1, .5]));
            data.UpdateRange(1, [1, 1], [5, 6]); TestSuite.True(data.Snapshot().IsSortedAscending);
        });
        suite.Run("data whole replacement does not compare old last X", () =>
        {
            var data = new XyDataSeries(); data.AppendRange([100, 200], [1, 2]); var old = data.Snapshot(); int events = 0;
            data.DataSeriesChanged += (_, _) => { events++; TestSuite.Equal(data.Count, 3); TestSuite.Near(data.Snapshot()[0, 0], 0); };
            data.ReplaceAll([0, 1, 2], [10, 20, 30]); TestSuite.Equal(events, 1); TestSuite.Near(old[0, 0], 100);
        });
        suite.Run("data FIFO replacement trims after validating discarded rows", () =>
        {
            var data = new DataSeries(2) { FifoCapacity = 2 }; data.Append(1, 1);
            Unchanged(data, () => data.ReplaceAll([0, 1, 2], [double.PositiveInfinity, 1, 2]));
            data.ReplaceAll([0, 1, 2, 3], [0, 10, 20, 30]); TestSuite.Equal(data.Count, 2); TestSuite.Near(data.Snapshot()[0, 0], 2);
        });
        suite.Run("data zero-length operations preserve snapshots", () =>
        {
            var data = new DataSeries(2); data.Append(1, 2); var old = data.Snapshot();
            data.AppendRange([], []); data.UpdateRange(1, [], []); data.UpdateChannelRange(1, 1, []); data.RemoveRange(1, 0); data.InsertRange(1, [], []);
            TestSuite.True(ReferenceEquals(old, data.Snapshot()));
            data.ReplaceAll([], []); TestSuite.Equal(data.Count, 0); var empty = data.Snapshot(); data.ReplaceAll([], []); TestSuite.True(ReferenceEquals(empty, data.Snapshot()));
        });
        suite.Run("data bulk operations coalesce nested suspended notifications", () =>
        {
            var data = new DataSeries(2); int events = 0; data.DataSeriesChanged += (_, _) => events++;
            using (data.SuspendUpdates())
            {
                data.ReplaceAll([0, 1, 2], [0, 1, 2]);
                using (data.SuspendUpdates()) { data.UpdateRange(0, [0, 1], [10, 20]); data.UpdateChannelRange(2, 1, [30]); }
                TestSuite.Equal(events, 0);
            }
            TestSuite.Equal(events, 1); TestSuite.True(data.Snapshot().Channel(1).Span.SequenceEqual(new double[] { 10, 20, 30 }));
        });
        suite.Run("data reentrant readers and writers are notified outside lock", () =>
        {
            var data = new XyDataSeries(); data.Append(0, 0); int events = 0;
            data.DataSeriesChanged += (_, _) => { events++; if (events == 1) data.Append(2, 2); else TestSuite.Equal(data.Snapshot().Count, 3); };
            data.Append(1, 1); TestSuite.Equal(events, 2); TestSuite.Equal(data.Count, 3);
        });
        suite.Run("data concurrent replacements publish coherent complete generations", () =>
        {
            var data = new DataSeries(2); data.ReplaceAll([0, 1], [0, 0]); int failures = 0;
            Parallel.Invoke(
                () => { for (int generation = 1; generation < 200; generation++) data.ReplaceAll(Enumerable.Range(0, 100).Select(i => (double)i).ToArray(), Enumerable.Repeat((double)generation, 100).ToArray()); },
                () => { for (int i = 0; i < 2000; i++) { var snapshot = data.Snapshot(); double value = snapshot[1, 0]; for (int p = 0; p < snapshot.Count; p++) if (snapshot[1, p] != value) Interlocked.Increment(ref failures); } });
            TestSuite.Equal(failures, 0);
        });
        suite.Run("OHLC base-typed mutation entry points enforce invariants", () =>
        {
            DataSeries data = new OhlcDataSeries(); data.Append(0, 10, 12, 8, 11);
            Unchanged(data, () => data.Append(1, 10, 9, 8, 11));
            Unchanged(data, () => data.AppendRange([1], [10], [9], [8], [11]));
            Unchanged(data, () => data.Update(0, 0, 10, 9, 8, 11));
            Unchanged(data, () => data.UpdateRange(0, [0], [10], [9], [8], [11]));
            Unchanged(data, () => data.UpdateChannelRange(0, 2, [9]));
            Unchanged(data, () => data.InsertRange(0, [-1], [10], [9], [8], [11]));
            Unchanged(data, () => data.ReplaceAll([1], [10], [9], [8], [11]));
        });
        suite.Run("box-plot base mutations reject unordered and partial-gap rows", () =>
        {
            DataSeries data = new BoxPlotDataSeries(); data.Append(0, 1, 2, 3, 4, 5);
            Unchanged(data, () => data.Append(1, 1, 2, 7, 4, 5));
            Unchanged(data, () => data.UpdateChannelRange(0, 3, [8]));
            Unchanged(data, () => data.UpdateRange(0, [0], [1], [2], [7], [4], [5]));
            Unchanged(data, () => data.ReplaceAll([0], [1], [2], [double.NaN], [4], [5]));
        });
        suite.Run("financial and statistical all-NaN gaps stay supported", () =>
        {
            var ohlc = new OhlcDataSeries(); ohlc.Append(0, double.NaN, double.NaN, double.NaN, double.NaN);
            TestSuite.Equal(ohlc.Count, 1); TestSuite.True(ohlc.Snapshot().Extents(1) is null);
            Unchanged(ohlc, () => ohlc.Append(1, double.NaN, 2, 1, 1));
            var box = new BoxPlotDataSeries(); box.Append(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
            TestSuite.Equal(box.Count, 1);
        });
        suite.Run("public snapshots reject false sorted metadata and invalid columns", () =>
        {
            TestSuite.Throws<ArgumentException>(() => new SeriesSnapshot([[2, 1], [0, 0]], 0, true));
            TestSuite.Throws<ArgumentException>(() => new SeriesSnapshot([[double.NaN], [0]], 0, false));
            TestSuite.Throws<ArgumentException>(() => new SeriesSnapshot([[0], [double.PositiveInfinity]], 0, true));
            TestSuite.Throws<ArgumentException>(() => new SeriesSnapshot([null!, [0]], 0, true));
            TestSuite.Throws<ArgumentException>(() => new SeriesSnapshot([[0], []], 0, true));
            var snapshot = new SeriesSnapshot([[1, 2], [3, 4]], 5, true);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => snapshot.LowerBound(double.NaN));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => snapshot.VisibleIndices(default));
            TestSuite.Equal(snapshot.LowerBound(double.NegativeInfinity), 0); TestSuite.Equal(snapshot.UpperBound(double.PositiveInfinity), 2);
        });
        suite.Run("snapshot cached extents preserve positive policy and X filters", () =>
        {
            var data = new XyDataSeries(); data.AppendRange([0, 1, 2, 3], [-5, double.NaN, 2, 7]); var s = data.Snapshot();
            TestSuite.Equal(s.Extents(1), new DoubleRange(-5, 7)); TestSuite.Equal(s.Extents(1, positiveOnly: true), new DoubleRange(2, 7));
            TestSuite.Equal(s.Extents(1, new(1.5, 3.5)), new DoubleRange(2, 7));
            TestSuite.True(s.Extents(1, new(.9, 1.1)) is null);
            data.Update(3, 3, 100); TestSuite.Equal(s.Extents(1), new DoubleRange(-5, 7)); TestSuite.Equal(data.Snapshot().Extents(1), new DoubleRange(-5, 100));
        });
        suite.Run("snapshot singleton positive extent never includes zero", () =>
        {
            foreach (double value in new[] { double.Epsilon, 1e-300, 1e-12, 1.0, double.MaxValue })
            {
                var data = new XyDataSeries(); data.Append(0, value); var range = data.Snapshot().Extents(1, positiveOnly: true)!.Value;
                TestSuite.True(range.IsValid && range.Min > 0 && range.Contains(value));
            }
        });
        suite.Run("grid invalid coordinates cannot alias adjacent rows", () =>
        {
            var grid = new HeatmapGrid([0, 1, 2], [0, 1, 2], [10, 20, 30, 40]);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => { _ = grid[2, 0]; });
            TestSuite.Throws<ArgumentOutOfRangeException>(() => { _ = grid[-1, 1]; });
            TestSuite.Throws<ArgumentOutOfRangeException>(() => { _ = grid[0, 2]; });
            TestSuite.Throws<ArgumentNullException>(() => new HeatmapGrid(null!, [0, 1], [0]));
        });
        suite.Run("typed point paths allocate no per-point arrays", () =>
        {
            var xy = new XyDataSeries { FifoCapacity = 1024 }; var xyz = new XyzDataSeries { FifoCapacity = 1024 };
            var ohlc = new OhlcDataSeries { FifoCapacity = 1024 };
            for (int i = 0; i < 5000; i++) { xy.Append(i, i); xyz.Append(i, i, i); ohlc.Append(i, 10, 12, 8, 11); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 5000; i < 15000; i++) { xy.Append(i, i); xyz.Append(i, i, i); ohlc.Append(i, 10, 12, 8, 11); }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            TestSuite.True(bytes < 4096); TestSuite.Equal(xy.Count, 1024);
        });
        suite.Run("data mutation benchmark captures CPU and allocation evidence", Benchmark);
    }
    private sealed class LegacyRing
    {
        private readonly double[] _x, _y;
        private int _head, _count;
        public LegacyRing(int capacity) { _x = new double[capacity]; _y = new double[capacity]; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Append(params double[] values)
        {
            if (_count == _x.Length) { _head = (_head + 1) % _x.Length; _count--; }
            int p = (_head + _count++) % _x.Length; _x[p] = values[0]; _y[p] = values[1];
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(int index, double x, double y)
        {
            _x[(_head + index) % _x.Length] = x; _y[(_head + index) % _x.Length] = y;
            for (int i = 1; i < _count; i++) if (_x[(_head + i) % _x.Length] < _x[(_head + i - 1) % _x.Length]) throw new Exception("Unexpected benchmark ordering.");
        }
        public double[][] Snapshot()
        {
            var x = new double[_count]; var y = new double[_count];
            for (int i = 0; i < _count; i++) { x[i] = _x[(_head + i) % _x.Length]; y[i] = _y[(_head + i) % _x.Length]; }
            return [x, y];
        }
    }
    private static void Benchmark()
    {
        const int count = 200000, queries = 21, appendCount = 20000;
        var legacy = new LegacyRing(count); var data = new XyDataSeries(count) { FifoCapacity = count };
        for (int i = 0; i < count; i++) { legacy.Append(i, -i); data.Append(i, -i); }
        var timer = new Stopwatch(); var oldTimes = new double[queries]; var newTimes = new double[queries];
        for (int q = 0; q < 5; q++) { legacy.Update(100, 100, q); data.Update(100, 100, q); }
        for (int q = 0; q < queries; q++)
        {
            timer.Restart(); legacy.Update(100 + q, 100 + q, q); oldTimes[q] = timer.Elapsed.TotalMilliseconds;
            timer.Restart(); data.Update(100 + q, 100 + q, q); newTimes[q] = timer.Elapsed.TotalMilliseconds;
        }
        var expected = legacy.Snapshot(); var actual = data.Snapshot();
        for (int c = 0; c < 2; c++) TestSuite.True(actual.Channel(c).Span.SequenceEqual(expected[c]));
        Array.Sort(oldTimes); Array.Sort(newTimes);
        long before = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
        for (int i = count; i < count + appendCount; i++) legacy.Append(i, -i);
        double oldAppend = timer.Elapsed.TotalMilliseconds; long oldAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
        for (int i = count; i < count + appendCount; i++) data.Append(i, -i);
        double newAppend = timer.Elapsed.TotalMilliseconds; long newAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
        expected = legacy.Snapshot(); actual = data.Snapshot();
        for (int c = 0; c < 2; c++) TestSuite.True(actual.Channel(c).Span.SequenceEqual(expected[c]));
        // Warm extent lookup must not scan or allocate repeated full-column results.
        actual.Extents(1); before = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
        for (int i = 0; i < 1000; i++) actual.Extents(1);
        double extentMs = timer.Elapsed.TotalMilliseconds; long extentBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var report = new { count, queries, appendCount, legacyUpdateMedianMs = oldTimes[queries / 2], updateMedianMs = newTimes[queries / 2],
            legacyAppendMs = oldAppend, appendMs = newAppend, legacyAppendAllocatedBytes = oldAlloc, appendAllocatedBytes = newAlloc,
            cachedExtentQueries = 1000, cachedExtentTotalMs = extentMs, cachedExtentAllocatedBytes = extentBytes,
            scope = "CPU data mutation only; legacy comparator preserves the prior ring/modulo/full-order-scan algorithm. No GPU, FPS or end-to-end presentation claim." };
        Directory.CreateDirectory("artifacts/performance");
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText("artifacts/performance/data-mutations.json", json); Console.WriteLine("PERF_DATA " + JsonSerializer.Serialize(report));
    }
}
