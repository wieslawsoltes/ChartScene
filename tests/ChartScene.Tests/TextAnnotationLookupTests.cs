using System.Collections;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Rendering;

public static class TextAnnotationLookupTests
{
    private sealed class ObservedRegions(IReadOnlyList<AnnotationTextRegion> source) : IReadOnlyList<AnnotationTextRegion>
    {
        public long Reads;
        public int Count => source.Count;
        public AnnotationTextRegion this[int index] { get { Reads++; return source[index]; } }
        public IEnumerator<AnnotationTextRegion> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static ChartModel Model(int count)
    {
        var model = new ChartModel { ShowLegend = false };
        model.XAxis.AutoRange = model.YAxis.AutoRange = AutoRange.Never;
        for (int i = 0; i < count; i++) model.Annotations.Add(new TextAnnotation
        {
            Text = "Note " + i, X1 = .2, Y1 = .4, XCoordinateMode = CoordinateMode.Relative,
            YCoordinateMode = CoordinateMode.Relative, FontSize = 12
        });
        return model;
    }
    public static void Register(TestSuite suite)
    {
        suite.Run("text frame lookup ignores duplicate IDs and uses object identity", () =>
        {
            using var model = Model(2); model.Annotations[0].Id = model.Annotations[1].Id = "same";
            model.Annotations[1].X1 = .6;
            // IDs are application metadata here; measure layouts directly because model
            // validation correctly rejects duplicate IDs for a complete chart render.
            var first = TextAnnotationLayout.Measure(new RecordingCanvas(), model, model.Annotations[0], new(0, 0, 800, 500), new(0, 0, 800, 500))!;
            var second = TextAnnotationLayout.Measure(new RecordingCanvas(), model, model.Annotations[1], new(0, 0, 800, 500), new(0, 0, 800, 500))!;
            var frame = new ChartFrame(new(new(0,0,800,500),new(0,0,800,500),[]), new(0,0,0,0,model.Revision), [], [], [])
                { TextAnnotations = new[] { new AnnotationTextRegion(model.Annotations[0], first), new AnnotationTextRegion(model.Annotations[1], second) } };
            TestSuite.True(ReferenceEquals(frame.FindTextAnnotation(model.Annotations[0]), first));
            TestSuite.True(ReferenceEquals(frame.FindTextAnnotation(model.Annotations[1]), second));
            TestSuite.True(frame.FindTextAnnotation(new TextAnnotation { Id = "same" }) is null);
        });
        suite.Run("text region snapshot survives record copies and cannot be mutated", () =>
        {
            using var model = Model(1); var frame = new ChartRenderer().Render(new RecordingCanvas(), model, 900, 600);
            var copy = frame with { Statistics = frame.Statistics with { CpuMilliseconds = 123 } };
            TestSuite.True(ReferenceEquals(frame.TextAnnotations, copy.TextAnnotations));
            TestSuite.True(ReferenceEquals(frame.FindTextAnnotation(model.Annotations[0]), copy.FindTextAnnotation(model.Annotations[0])));
            TestSuite.Throws<NotSupportedException>(() => ((IList<AnnotationTextRegion>)frame.TextAnnotations).Clear());
            TestSuite.Throws<ArgumentNullException>(() => { _ = frame with { TextAnnotations = null! }; });
            TestSuite.Throws<ArgumentNullException>(() => frame.FindTextAnnotation(null!));
        });
        suite.Run("text region lookup retains last paint and separates different views", () =>
        {
            using var model = Model(1); var renderer = new ChartRenderer();
            var first = renderer.Render(new RecordingCanvas(), model, 900, 600);
            var second = renderer.Render(new RecordingCanvas(), model, 500, 350);
            var annotation = model.Annotations[0];
            TestSuite.True(!ReferenceEquals(first.FindTextAnnotation(annotation), second.FindTextAnnotation(annotation)));
            TestSuite.True(first.FindTextAnnotation(annotation)!.Anchor != second.FindTextAnnotation(annotation)!.Anchor);
            var both = first with { TextAnnotations = new[] { first.TextAnnotations[0], second.TextAnnotations[0] } };
            TestSuite.True(ReferenceEquals(both.FindTextAnnotation(annotation), second.FindTextAnnotation(annotation)));
            TestSuite.True(ReferenceEquals(first.FindTextAnnotation(annotation), first.TextAnnotations[0].Layout));
        });
        suite.Run("text frame snapshots externally supplied geometry collections", () =>
        {
            using var model = Model(2);
            var frame = new ChartRenderer().Render(new RecordingCanvas(), model, 900, 600);
            var source = frame.TextAnnotations.ToList(); var captured = frame with { TextAnnotations = source };
            source.Clear();
            TestSuite.Equal(captured.TextAnnotations.Count, 2);
            var region = captured.TextAnnotations[0];
            TestSuite.Near(AnnotationGeometry.HitDistance(model, region.Annotation, captured.Layout.PlotArea,
                region.Layout.Bounds.Center, captured), 0);
        });
        suite.Run("text hit dispatch does not rescan every frame region for each candidate", () =>
        {
            using var model = Model(512);
            var original = new ChartRenderer().Render(new RecordingCanvas(), model, 900, 600);
            var supplied = new ObservedRegions(original.TextAnnotations);
            var frame = original with { TextAnnotations = supplied };
            long before = supplied.Reads;
            foreach (var region in original.TextAnnotations)
                TestSuite.Near(AnnotationGeometry.HitDistance(model, region.Annotation, frame.Layout.PlotArea,
                    region.Layout.Bounds.Center, frame), 0);
            long reads = supplied.Reads - before;
            TestSuite.Equal(reads, 0L);
            Directory.CreateDirectory("artifacts/performance");
            File.WriteAllText("artifacts/performance/annotation-region-lookup.json", JsonSerializer.Serialize(new
            {
                regions = 512, candidateQueries = 512, repeatedSourceRegionReads = reads,
                scope = "Per-frame identity lookup after publication. Index construction is O(n); a complete annotation search still visits its candidates. Excludes shaping, drawing, UI and GPU."
            }));
        });
    }
}
