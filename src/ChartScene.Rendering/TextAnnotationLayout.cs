using ChartScene.Charting;
using ChartScene.Core;

namespace ChartScene.Rendering;

public sealed record AnnotationTextRegion(ChartAnnotation Annotation, TextAnnotationLayout Layout);
public readonly record struct AnnotationTextLine(string Text, PointD Baseline);

/// <summary>
/// Immutable measured text geometry in logical screen pixels. Anchors, multiline placement,
/// rotation, padding and hit testing use one layout, not a character-width approximation.
/// A layout belongs to the frame that measured it and never owns its canvas or model.
/// </summary>
public sealed partial class TextAnnotationLayout
{
    private readonly double _cos, _sin;
    private readonly TextStyle _style;
    private readonly ColorRgba? _background, _border;
    private readonly double _borderWidth;
    private readonly VectorPath _outline;
    public PointD Anchor { get; }
    public RectD LocalBounds { get; }
    public RectD Bounds { get; }
    public RectD Clip { get; }
    public double RotationAngle { get; }
    public IReadOnlyList<PointD> Corners { get; }
    public IReadOnlyList<AnnotationTextLine> Lines { get; }

    private TextAnnotationLayout(PointD anchor, RectD local, RectD clip, double angle,
        TextStyle style, ColorRgba? background, ColorRgba? border, double borderWidth,
        IReadOnlyList<AnnotationTextLine> lines)
    {
        Anchor=anchor; LocalBounds=local; Clip=clip; RotationAngle=angle;
        double radians=angle*Math.PI/180; _cos=Math.Cos(radians); _sin=Math.Sin(radians);
        _style=style; _background=background; _border=border; _borderWidth=borderWidth;
        var corners=new[]{ToScreen(new(local.X,local.Y)),ToScreen(new(local.Right,local.Y)),
            ToScreen(new(local.Right,local.Bottom)),ToScreen(new(local.X,local.Bottom))};
        Corners=Array.AsReadOnly(corners);
        double x=corners.Min(p=>p.X), y=corners.Min(p=>p.Y);
        Bounds=new(x,y,corners.Max(p=>p.X)-x,corners.Max(p=>p.Y)-y);
        Lines=Array.AsReadOnly(lines.Select(line=>new AnnotationTextLine(line.Text,ToScreen(line.Baseline))).ToArray());
        _outline=new VectorPath().MoveTo(corners[0]).LineTo(corners[1]).LineTo(corners[2]).LineTo(corners[3]).Close();
    }
    public PointD ToScreen(PointD local)=>new(Anchor.X+local.X*_cos-local.Y*_sin,Anchor.Y+local.X*_sin+local.Y*_cos);
    public PointD ToLocal(PointD screen)
    {
        double x=screen.X-Anchor.X,y=screen.Y-Anchor.Y;
        return new(x*_cos+y*_sin,-x*_sin+y*_cos);
    }
    /// <summary>Distance to the rotated padded body. Pixels outside the rendering clip cannot be hit.</summary>
    public double HitDistance(PointD screen)
    {
        if(!screen.IsFinite||!Clip.Contains(screen))return double.PositiveInfinity;
        var local=ToLocal(screen);
        double x=Math.Max(LocalBounds.X-local.X,Math.Max(0,local.X-LocalBounds.Right));
        double y=Math.Max(LocalBounds.Y-local.Y,Math.Max(0,local.Y-LocalBounds.Bottom));
        return Math.Sqrt(x*x+y*y);
    }
    /// <summary>Measures text only at X1/Y1; unused X2/Y2 never hide a label on a logarithmic axis.</summary>
    public static TextAnnotationLayout? Measure(IChartCanvas canvas,ChartModel model,ChartAnnotation annotation,RectD plot,RectD surface)
    {
        ArgumentNullException.ThrowIfNull(canvas);ArgumentNullException.ThrowIfNull(model);ArgumentNullException.ThrowIfNull(annotation);
        if(annotation.Kind!=AnnotationKind.Text)throw new ArgumentException("A text annotation is required.",nameof(annotation));
        if(string.IsNullOrEmpty(annotation.Text)||plot.IsEmpty)return null;
        var anchor=AnnotationGeometry.ToScreen(model,annotation,plot,annotation.X1,annotation.Y1);
        if(!anchor.IsFinite)return null;
        var style=new TextStyle(annotation.Stroke,annotation.FontSize,annotation.FontFamily,annotation.FontBold)
            {Weight=annotation.FontWeight,Slant=annotation.FontSlant};
        var measured=AnnotationTextWrapper.Measure(canvas,annotation.Text,style,annotation.TextWrapping,annotation.TextWrapWidth);
        if(measured is null)return null;
        double width=annotation.TextWrapping==AnnotationTextWrapping.NoWrap?0:annotation.TextWrapWidth;
        double ascent=0,descent=0,height=0;
        foreach(var line in measured)
        {
            var m=line.Metrics;
            width=Math.Max(width,m.Width);ascent=Math.Max(ascent,m.Ascent);descent=Math.Max(descent,m.Descent);height=Math.Max(height,m.Height);
        }
        double padding=annotation.TextPadding,advance=Math.Max(height,ascent+descent)*annotation.LineSpacing;
        double w=width+2*padding,h=ascent+descent+(measured.Count-1)*advance+2*padding;
        if(!double.IsFinite(w)||!double.IsFinite(h)||w<=0||h<=0)return null;
        double left=annotation.HorizontalAnchorPoint switch{HorizontalAnchorPoint.Center=>-w/2,HorizontalAnchorPoint.Right=>-w,_=>0};
        double top=annotation.VerticalAnchorPoint switch{VerticalAnchorPoint.Center=>-h/2,VerticalAnchorPoint.Bottom=>-h,VerticalAnchorPoint.Baseline=>-padding-ascent,_=>0};
        var lines=new AnnotationTextLine[measured.Count];
        for(int i=0;i<measured.Count;i++)
        {
            double offset=annotation.TextAlignment switch{AnnotationTextAlignment.Center=>(width-measured[i].Metrics.Width)/2,AnnotationTextAlignment.Right=>width-measured[i].Metrics.Width,_=>0};
            lines[i]=new(measured[i].Text,new(left+padding+offset,top+padding+ascent+i*advance));
        }
        var result=new TextAnnotationLayout(anchor,new(left,top,w,h),annotation.ClipToPlot?plot:surface,
            annotation.RotationAngle,style,annotation.Background,annotation.BorderColor,annotation.BorderThickness,lines);
        if(annotation.TextWrapping!=AnnotationTextWrapping.NoWrap && annotation.IsEditable)
        {
            // Keep X1/Y1 fixed: center-anchored text grows in both directions and right-anchored text grows left.
            double factor=annotation.HorizontalAnchorPoint switch{HorizontalAnchorPoint.Center=>2,HorizontalAnchorPoint.Right=>-1,_=>1};
            double edge=(annotation.TextWrapWidth+2*padding)/factor;
            result.WidthGrip=new(result.ToScreen(new(edge,result.LocalBounds.Center.Y)),factor);
        }
        return result.Corners.All(p=>p.IsFinite)&&double.IsFinite(result.Bounds.Width)&&double.IsFinite(result.Bounds.Height)?result:null;
    }
    public void Draw(IChartCanvas canvas,bool selected=false,ColorRgba? gripFill=null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if(_background is not null||_border is not null&&_borderWidth>0)
            canvas.DrawPath(_outline,_background,_border is { } color&&_borderWidth>0?new(color,_borderWidth):null);
        foreach(var line in Lines)if(line.Text.Length>0)canvas.DrawText(line.Text,line.Baseline,_style,TextAlignment.Left,RotationAngle);
        if(selected)
        {
            foreach(var corner in Corners)canvas.DrawRectangle(new(corner.X-3,corner.Y-3,6,6),gripFill??ColorRgba.White,new(_style.Color,1));
            if(WidthGrip is { } grip)
                canvas.DrawEllipse(new(grip.Position.X-4,grip.Position.Y-4,8,8),gripFill??ColorRgba.White,new(_style.Color,1.5));
        }
    }
}

public sealed partial class ChartRenderer
{
    private readonly List<AnnotationTextRegion> _annotationTexts=[];
}
