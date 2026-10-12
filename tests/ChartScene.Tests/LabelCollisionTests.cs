using System.Diagnostics;
using System.Text.Json;
using ChartScene.Core;
using ChartScene.Rendering;

public static class LabelCollisionTests
{
    public static void Register(TestSuite suite)
    {
        suite.Run("label index exactly matches ordered scalar suppression", () =>
        {
            var random = new Random(386401);
            for (int pass = 0; pass < 20; pass++)
            {
                var index = new LabelCollisionIndex(); var scalar = new List<RectD>();
                for (int i = 0; i < 1200; i++)
                {
                    var r = new RectD(random.Next(-1000, 1001), random.Next(-700, 701),
                        random.Next(1, 20) == 1 ? random.Next(1000) : random.Next(80), random.Next(30));
                    double padding = random.Next(5);
                    bool accepted = !scalar.Any(previous => previous.Intersects(r.Inflate(padding)));
                    TestSuite.Equal(index.TryAdd(r, padding), accepted);
                    if (accepted) scalar.Add(r);
                }
                TestSuite.Equal(index.Count, scalar.Count);
            }
        });
        suite.Run("label index preserves inclusive edge contact and asymmetric padding", () =>
        {
            var index = new LabelCollisionIndex();
            TestSuite.True(index.TryAdd(new(0, 0, 10, 10), 0));
            TestSuite.True(!index.TryAdd(new(10, 0, 1, 1), 0));
            TestSuite.True(!index.TryAdd(new(12, 0, 1, 1), 2));
            TestSuite.True(index.TryAdd(new(12.01, 0, 1, 1), 2));
            TestSuite.True(index.TryAdd(new(0, 12.01, 0, 0), 2));
            TestSuite.Equal(index.Count, 3);
        });
        suite.Run("label index handles negative cells and wide fallback without false positives", () =>
        {
            var index = new LabelCollisionIndex();
            TestSuite.True(index.TryAdd(new(-1000000, -1000000, 2000000, 1), 0));
            TestSuite.True(index.TryAdd(new(-20, 0, 10, 10)));
            TestSuite.True(!index.TryAdd(new(-20, -1000000, 5, 5)));
            TestSuite.True(index.TryAdd(new(-1e100, -10, 1, 1)));
            TestSuite.True(!index.TryAdd(new(-1e110, -1e110, 2e110, 2e110)));
            TestSuite.True(index.FullScans > 0);
        });
        suite.Run("label index invalid input leaves occupancy unchanged", () =>
        {
            var index = new LabelCollisionIndex();
            foreach (var invalid in new[] { new RectD(0, 0, -1, 1), new RectD(double.NaN, 0, 1, 1),
                new RectD(0, 0, 1, double.PositiveInfinity), new RectD(double.MaxValue, 0, double.MaxValue, 1) })
                TestSuite.Throws<ArgumentOutOfRangeException>(() => index.TryAdd(invalid));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => index.TryAdd(default, -1));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => index.TryAdd(default, double.NaN));
            TestSuite.Equal(index.Count, 0);
            TestSuite.True(index.TryAdd(default, 0));
        });
        suite.Run("label index handles many shared buckets without duplicate exact checks", () =>
        {
            var index = new LabelCollisionIndex();
            TestSuite.True(index.TryAdd(new(0, 0, 200, 200)));
            long before = index.ExactComparisons;
            TestSuite.True(!index.TryAdd(new(100, 100, 100, 100)));
            TestSuite.Equal(index.ExactComparisons - before, 1L);
        });
        suite.Run("label padding overflow does not lose intersections", () =>
        {
            var index = new LabelCollisionIndex();
            TestSuite.True(index.TryAdd(new(0, 0, 1, 1)));
            TestSuite.True(!index.TryAdd(new(-double.MaxValue, -double.MaxValue, 0, 0), double.MaxValue));
            TestSuite.Equal(index.Count, 1);
        });
        suite.Run("label layout work and timing evidence", () =>
        {
            const int count = 6000;
            var rectangles = Enumerable.Range(0, count).Select(i => new RectD(i % 100 * 50, i / 100 * 40, 30, 18)).ToArray();
            var scalar = new List<RectD>(); long scalarComparisons = 0;
            var timer = Stopwatch.StartNew();
            foreach (var r in rectangles)
            {
                bool overlaps = false;
                foreach (var previous in scalar) { scalarComparisons++; if (previous.Intersects(r.Inflate(2))) { overlaps = true; break; } }
                if (!overlaps) scalar.Add(r);
            }
            double scalarMs = timer.Elapsed.TotalMilliseconds; timer.Restart();
            var index = new LabelCollisionIndex();
            foreach (var r in rectangles) TestSuite.True(index.TryAdd(r));
            double indexedMs = timer.Elapsed.TotalMilliseconds;
            TestSuite.Equal(index.Count, scalar.Count);
            TestSuite.Equal(scalarComparisons, (long)count * (count - 1) / 2);
            TestSuite.True(index.ExactComparisons < count * 8L);
            var evidence = new { candidates = count, accepted = index.Count, scalarComparisons,
                indexedComparisons = index.ExactComparisons, scalarMs, indexedMs,
                scope = "CPU rectangle suppression only; no text shaping, drawing, GPU or presentation timing. Pathological overlap distributions retain an exact linear fallback." };
            Directory.CreateDirectory("artifacts/performance");
            File.WriteAllText("artifacts/performance/label-collisions.json", JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PERF_LABELS " + JsonSerializer.Serialize(evidence));
        });
    }
}
