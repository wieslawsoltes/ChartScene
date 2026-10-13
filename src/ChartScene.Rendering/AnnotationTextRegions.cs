using System.Collections.ObjectModel;
using ChartScene.Charting;

namespace ChartScene.Rendering;

/// <summary>Frame-owned snapshot plus an identity index; neither source lists nor other frames are retained.</summary>
internal sealed class AnnotationTextRegions : ReadOnlyCollection<AnnotationTextRegion>
{
    public static readonly AnnotationTextRegions Empty = new([]);
    private readonly Dictionary<ChartAnnotation, TextAnnotationLayout> _byAnnotation;
    private AnnotationTextRegions(AnnotationTextRegion[] items) : base(items)
    {
        _byAnnotation = new(items.Length, ReferenceEqualityComparer.Instance);
        foreach (var region in items)
        {
            ArgumentNullException.ThrowIfNull(region);
            ArgumentNullException.ThrowIfNull(region.Annotation);
            ArgumentNullException.ThrowIfNull(region.Layout);
            // A repeated annotation, if supplied, resolves to its last painted region.
            _byAnnotation[region.Annotation] = region.Layout;
        }
    }
    public static AnnotationTextRegions Capture(IReadOnlyList<AnnotationTextRegion> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source is AnnotationTextRegions snapshot) return snapshot;
        return source.Count == 0 ? Empty : new(source.ToArray());
    }
    public TextAnnotationLayout? Find(ChartAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return _byAnnotation.GetValueOrDefault(annotation);
    }
}
