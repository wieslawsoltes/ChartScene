using System.Runtime.CompilerServices;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Rendering;

public static class AnnotationTextContentTests
{
    public static void Register(TestSuite suite)
    {
        suite.Run("annotation input canonicalizes native line breaks without losing blank lines", () =>
        {
            foreach (string value in new[] { "", "text", "a\rb\r", "a\nb\n", "a\r\nb\r\n", "\r\n\r\n", "\r\r\n\n", "A😀\rB\u2028C" })
            {
                string oracle = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                TestSuite.Equal(AnnotationTextContent.NormalizeLineEndings(value), oracle);
                TestSuite.True(AnnotationTextContent.Equivalent(value, oracle));
            }
        });
        suite.Run("annotation input equivalence distinguishes edits not separator representation", () =>
        {
            TestSuite.True(AnnotationTextContent.Equivalent("First\nSecond\n", "First\rSecond\r"));
            TestSuite.True(AnnotationTextContent.Equivalent("First\r\nSecond", "First\nSecond"));
            foreach (var value in new[] { "First\nsecond", "First\nSecond\n", "First\n\nSecond", "First Second", "First\u2028Second" })
                TestSuite.True(!AnnotationTextContent.Equivalent("First\nSecond", value));
        });
        suite.Run("annotation input preserves original model text for non-editor consumers", () =>
        {
            var text = "A\r\nB\rC\n"; var annotation = new TextAnnotation { Text = text };
            TestSuite.True(ReferenceEquals(annotation.Text, text));
            using var model = new ChartModel { ShowLegend = false }; model.Annotations.Add(annotation);
            annotation.XCoordinateMode = annotation.YCoordinateMode = CoordinateMode.Relative;
            annotation.X1 = annotation.Y1 = .5;
            var frame = new ChartRenderer().Render(new RecordingCanvas(), model, 900, 600);
            TestSuite.Equal(frame.TextAnnotations.Single().Layout.Lines.Count, 4);
            TestSuite.True(ReferenceEquals(annotation.Text, text));
        });
        suite.Run("annotation input seeded scalar separator oracle", () =>
        {
            var random = new Random(4991);
            string[] tokens = ["a", " B", "\r", "\n", "\r\n", "😀", "\u2028"];
            for (int pass = 0; pass < 500; pass++)
            {
                string first = string.Concat(Enumerable.Range(0, random.Next(24)).Select(_ => tokens[random.Next(tokens.Length)]));
                string second = pass % 2 == 0 ? first.Replace("\r\n", "\n").Replace('\r', '\n') : first + "x";
                string Normalize(string x) => x.Replace("\r\n", "\n").Replace('\r', '\n');
                TestSuite.Equal(AnnotationTextContent.Equivalent(first, second), Normalize(first) == Normalize(second));
                TestSuite.Equal(AnnotationTextContent.NormalizeLineEndings(first), Normalize(first));
            }
        });
        suite.Run("annotation input validates nulls and reuses canonical text", () =>
        {
            TestSuite.Throws<ArgumentNullException>(() => AnnotationTextContent.NormalizeLineEndings(null!));
            TestSuite.Throws<ArgumentNullException>(() => AnnotationTextContent.Equivalent(null!, ""));
            TestSuite.Throws<ArgumentNullException>(() => AnnotationTextContent.Equivalent("", null!));
            const string value = "canonical\ntext\n";
            TestSuite.True(ReferenceEquals(value, AnnotationTextContent.NormalizeLineEndings(value)));
        });
        suite.Run("annotation unchanged-input comparisons allocate no normalized buffers", () =>
        {
            const string first = "First\nSecond\nThird\n", second = "First\rSecond\r\nThird\r";
            Measure(first, second);
            long bytes = Measure(first, second); TestSuite.Equal(bytes, 0L);
            Directory.CreateDirectory("artifacts/performance");
            File.WriteAllText("artifacts/performance/annotation-input.json", JsonSerializer.Serialize(new
            {
                comparisons = 10000, allocatedBytes = bytes,
                scope = "Warmed equivalent-line-ending comparisons only. Excludes changed text normalization, native editor, history, rendering and GPU."
            }));
        });
    }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static long Measure(string first, string second)
    {
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
            if (!AnnotationTextContent.Equivalent(first, second)) throw new InvalidOperationException("Unexpected difference");
        return GC.GetAllocatedBytesForCurrentThread() - bytes;
    }
}
