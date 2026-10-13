using ChartScene.Core;

namespace ChartScene.Rendering;

/// <summary>A logical-pixel width handle. WidthPerPixel accounts for the fixed text anchor.</summary>
public readonly record struct AnnotationWidthGrip(PointD Position,double WidthPerPixel);

public sealed partial class TextAnnotationLayout
{
    /// <summary>Requested wrapping-width handle, captured with this layout; null for unwrapped or read-only text.</summary>
    public AnnotationWidthGrip? WidthGrip { get; private set; }

    /// <summary>Uses the same clipped, rotated handle position as drawing. Does not measure or allocate.</summary>
    public bool HitWidthGrip(PointD point,double radius=8)
    {
        if(!double.IsFinite(radius)||radius<=0)throw new ArgumentOutOfRangeException(nameof(radius));
        return point.IsFinite && WidthGrip is { } grip && Clip.Contains(point) && Clip.Contains(grip.Position)
            && (point-grip.Position).LengthSquared<=radius*radius;
    }
}
