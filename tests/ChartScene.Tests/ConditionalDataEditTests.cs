using System.Runtime.CompilerServices;
using System.Text.Json;
using ChartScene.Data;

public static class ConditionalDataEditTests
{
    public static void Register(TestSuite suite)
    {
        suite.Run("conditional point edits preserve all other values and old snapshots", () =>
        {
            var data = new XyDataSeries(); data.AppendRange([1, 2, 3], [4, 5, 6]);
            var before = data.Snapshot(); int events = 0; data.DataSeriesChanged += (_, _) => events++;
            TestSuite.True(data.TryUpdateChannelValue(1, 1, 5, 8, data.Revision, out long revision));
            var after = data.Snapshot(); TestSuite.Equal(revision, before.Revision + 1);
            TestSuite.Near(before[1, 1], 5); TestSuite.Near(after[1, 1], 8); TestSuite.Equal(events, 1);
            for (int i = 0; i < 3; i++) TestSuite.Near(after[0, i], before[0, i]);
            TestSuite.Near(after[1, 0], 4); TestSuite.Near(after[1, 2], 6);
        });
        suite.Run("conditional edits reject stale generations and value mismatches", () =>
        {
            var data = new XyDataSeries(); data.Append(1, 2); long old = data.Revision;
            data.ReplaceAll([1], [2]); var current = data.Snapshot();
            TestSuite.True(!data.TryUpdateChannelValue(0, 1, 2, 9, old, out _));
            TestSuite.True(!data.TryUpdateChannelValue(0, 1, 3, 9, data.Revision, out _));
            TestSuite.True(ReferenceEquals(current, data.Snapshot()));
            data.Clear(); TestSuite.True(!data.TryUpdateChannelValue(0, 1, 2, 9, old, out _));
        });
        suite.Run("conditional stationary edits preserve revision cache and notifications", () =>
        {
            var data = new XyDataSeries(); data.Append(1, 2); var before = data.Snapshot(); int events = 0;
            data.DataSeriesChanged += (_, _) => events++;
            for (int i = 0; i < 100; i++) TestSuite.True(data.TryUpdateChannelValue(0, 1, 2, 2, before.Revision, out _));
            TestSuite.Equal(events, 0); TestSuite.True(ReferenceEquals(before, data.Snapshot()));
        });
        suite.Run("conditional edits validate row constraints and X neighbors atomically", () =>
        {
            var data = new XyDataSeries(); data.AppendRange([1, 2, 3], [4, 5, 6]); var before = data.Snapshot();
            TestSuite.Throws<ArgumentException>(() => data.TryUpdateChannelValue(1, 0, 2, 4, data.Revision, out _));
            TestSuite.Throws<ArgumentException>(() => data.TryUpdateChannelValue(1, 1, 5, double.PositiveInfinity, data.Revision, out _));
            TestSuite.True(ReferenceEquals(before, data.Snapshot()));
            var financial = new OhlcDataSeries(); financial.Append(1, 5, 9, 2, 6); var saved = financial.Snapshot();
            TestSuite.Throws<ArgumentException>(() => financial.TryUpdateChannelValue(0, 2, 9, 3, financial.Revision, out _));
            TestSuite.True(ReferenceEquals(saved, financial.Snapshot()));
        });
        suite.Run("conditional edits maintain wrapped FIFO storage and sorted metadata", () =>
        {
            var data = new XyDataSeries { FifoCapacity = 4, AcceptsUnsortedData = true };
            for (int i = 0; i < 10; i++) data.Append(i, i);
            TestSuite.True(data.TryUpdateChannelValue(1, 1, 7, 70, data.Revision, out _));
            TestSuite.Near(data.Snapshot()[1, 1], 70);
            TestSuite.True(data.TryUpdateChannelValue(1, 0, 7, 10, data.Revision, out _));
            TestSuite.True(!data.Snapshot().IsSorted);
            TestSuite.True(data.TryUpdateChannelValue(1, 0, 10, 7, data.Revision, out _));
            TestSuite.True(data.Snapshot().IsSorted);
        });
        suite.Run("conditional edit reports its own revision before reentrant mutation", () =>
        {
            var data = new XyDataSeries(); data.Append(1, 2); long before = data.Revision; bool entered = false;
            data.DataSeriesChanged += (_, _) => { if (!entered) { entered = true; data.Update(0, 1, 100); } };
            TestSuite.True(data.TryUpdateChannelValue(0, 1, 2, 3, before, out long applied));
            TestSuite.Equal(applied, before + 1); TestSuite.Equal(data.Revision, before + 2); TestSuite.Near(data.Snapshot()[1, 0], 100);
        });
        suite.Run("conditional same-generation concurrent writers have exactly one winner", () =>
        {
            var data = new XyDataSeries(); data.Append(1, 2); long before = data.Revision; int wins = 0;
            Parallel.For(0, 32, i => { if (data.TryUpdateChannelValue(0, 1, 2, i + 3, before, out _)) Interlocked.Increment(ref wins); });
            TestSuite.Equal(wins, 1); TestSuite.Equal(data.Revision, before + 1);
        });
        suite.Run("conditional edits respect batching and bitwise signed zero", () =>
        {
            var data = new XyDataSeries(); data.Append(1, 0); int events = 0; data.DataSeriesChanged += (_, _) => events++;
            using (data.SuspendUpdates())
            {
                double negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
                TestSuite.True(data.TryUpdateChannelValue(0, 1, 0, negativeZero, data.Revision, out _));
                TestSuite.True(!data.TryUpdateChannelValue(0, 1, 0, 1, data.Revision, out _));
                TestSuite.True(data.TryUpdateChannelValue(0, 1, negativeZero, 1, data.Revision, out _));
                TestSuite.Equal(events, 0);
            }
            TestSuite.Equal(events, 1);
        });
        suite.Run("conditional edit validation does not allocate or scan source-sized arrays", () =>
        {
            var data = new XyDataSeries();
            data.AppendRange(Enumerable.Range(0, 1000000).Select(i => (double)i).ToArray(), new double[1000000]);
            Measure(data, 1000); long bytes = Measure(data, 10000);
            TestSuite.Equal(bytes, 0L); TestSuite.Equal(data.Count, 1000000);
            var report = new { sourcePoints = data.Count, updates = 10000, allocatedBytes = bytes,
                scope = "Conditional channel mutation only, in a non-tiered warmed helper. No observers or snapshots in interval. Excludes renderer, UI, GPU and presentation." };
            Directory.CreateDirectory("artifacts/performance");
            File.WriteAllText("artifacts/performance/conditional-edits.json", JsonSerializer.Serialize(report));
            Console.WriteLine("PERF_CONDITIONAL_EDITS " + JsonSerializer.Serialize(report));
        });
    }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static long Measure(DataSeries data, int count)
    {
        long revision = data.Revision, before = GC.GetAllocatedBytesForCurrentThread(); double value = 0;
        for (int i = 0; i < count; i++)
        {
            double next = value == 0 ? 1 : 0;
            if (!data.TryUpdateChannelValue(500000, 1, value, next, revision, out revision)) throw new Exception("Unexpected conflict");
            value = next;
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
