using ChartScene.Charting;
using ChartScene.Rendering;

namespace ChartScene.Interaction;

/// <summary>Routes double-click text editing before zoom extents, using the painted frame's measured bounds.</summary>
public sealed class TextAnnotationEditModifier:ChartModifier
{
    public override ModifierResult Handle(ChartController controller,ChartInput input)
    {
        if(input.Kind!=ChartInputKind.DoubleClick||controller.Frame is not { } frame)return ModifierResult.Unhandled;
        // Selection can change the model revision on the first click before another
        // frame is presented. Use the painted text body so a native double tap cannot
        // fall through to zoom extents merely because selection grips are pending.
        foreach(var region in frame.TextAnnotations.Reverse())
        {
            var annotation=region.Annotation;
            if(!annotation.IsVisible||!annotation.IsEditable||!controller.Model.Annotations.Contains(annotation))continue;
            if(region.Layout.HitDistance(input.Position)>4)continue;
            controller.RequestAnnotationEdit(annotation);return ModifierResult.Handled;
        }
        return ModifierResult.Unhandled;
    }
}
