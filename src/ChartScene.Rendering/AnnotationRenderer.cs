using ChartScene.Core;
using ChartScene.Charting;

namespace ChartScene.Rendering;

public static class AnnotationGeometry
{
    public static PointD ToScreen(ChartModel model,ChartAnnotation annotation,RectD plot,double x,double y)
    {
        var xa=model.FindXAxis(annotation.XAxisId); var ya=model.FindYAxis(annotation.YAxisId);
        double Map(double value,CoordinateMode mode,AxisModel axis)
        {
            double start=axis.IsVertical?plot.Y:plot.X,length=axis.IsVertical?plot.Height:plot.Width;
            return mode switch {CoordinateMode.Pixel=>start+value,CoordinateMode.Relative=>start+value*length,_=>new CoordinateCalculator(axis,start,length).GetCoordinate(value)};
        }
        double a=Map(x,annotation.XCoordinateMode,xa),b=Map(y,annotation.YCoordinateMode,ya);
        return xa.IsVertical?new(b,a):new(a,b);
    }
    public static PointD ToValues(ChartModel model,ChartAnnotation annotation,RectD plot,PointD point)
    {
        var xa=model.FindXAxis(annotation.XAxisId); var ya=model.FindYAxis(annotation.YAxisId);
        double Map(double pixel,CoordinateMode mode,AxisModel axis)
        {
            double start=axis.IsVertical?plot.Y:plot.X,length=axis.IsVertical?plot.Height:plot.Width;
            return mode switch {CoordinateMode.Pixel=>pixel-start,CoordinateMode.Relative=>(pixel-start)/length,_=>new CoordinateCalculator(axis,start,length).GetDataValue(pixel)};
        }
        return new(Map(xa.IsVertical?point.Y:point.X,annotation.XCoordinateMode,xa),Map(ya.IsVertical?point.Y:point.X,annotation.YCoordinateMode,ya));
    }
    public static (PointD A,PointD B) Endpoints(ChartModel model,ChartAnnotation annotation,RectD plot)
    {
        PointD a=ToScreen(model,annotation,plot,annotation.X1,annotation.Y1),b=ToScreen(model,annotation,plot,annotation.X2,annotation.Y2);
        if(annotation.Kind==AnnotationKind.HorizontalLine) { a=new(plot.X,a.Y); b=new(plot.Right,a.Y); }
        if(annotation.Kind==AnnotationKind.VerticalLine) { a=new(a.X,plot.Y); b=new(a.X,plot.Bottom); }
        return(a,b);
    }
    public static double HitDistance(ChartModel model,ChartAnnotation a,RectD plot,PointD point,ChartFrame? frame=null)
    {
        if(a.Kind==AnnotationKind.Text && frame is not null)
        {
            if(frame.Statistics.ModelRevision!=model.Revision)return double.PositiveInfinity;
            foreach(var region in frame.TextAnnotations)
                if(ReferenceEquals(region.Annotation,a)) return region.Layout.HitDistance(point);
            return double.PositiveInfinity;
        }
        var(p1,p2)=Endpoints(model,a,plot);
        if(!p1.IsFinite||!p2.IsFinite) return double.PositiveInfinity;
        if(a.Kind==AnnotationKind.Box)
        {
            var rect=RectD.FromPoints(p1,p2);
            return rect.Contains(point)?0:Math.Sqrt(Math.Pow(Math.Max(rect.X-point.X,Math.Max(0,point.X-rect.Right)),2)+Math.Pow(Math.Max(rect.Y-point.Y,Math.Max(0,point.Y-rect.Bottom)),2));
        }
        if(a.Kind is AnnotationKind.Text or AnnotationKind.AxisMarker) return Math.Sqrt((point-p1).LengthSquared);
        if(a.Kind is AnnotationKind.Polyline or AnnotationKind.Polygon)
        {
            var points=a.Points.Select(p=>ToScreen(model,a,plot,p.X,p.Y)).ToArray(); double best=double.PositiveInfinity;
            for(int i=1;i<points.Length;i++) best=Math.Min(best,Geometry.DistanceToSegment(point,points[i-1],points[i],out _));
            if(a.Kind==AnnotationKind.Polygon&&points.Length>2) best=Math.Min(best,Geometry.DistanceToSegment(point,points[^1],points[0],out _));
            return best;
        }
        return Geometry.DistanceToSegment(point,p1,p2,out _);
    }
}

public sealed partial class ChartRenderer
{
    private void DrawAnnotations(IChartCanvas c,ChartModel model,ChartLayout layout,AnnotationLayer layer)
    {
        foreach(var annotation in model.Annotations.Where(a=>a.IsVisible&&a.Layer==layer))
        {
            if(annotation.Kind==AnnotationKind.Text)
            {
                var text=TextAnnotationLayout.Measure(c,model,annotation,layout.PlotArea,layout.Bounds);
                if(text is not null)
                {
                    _annotationTexts.Add(new(annotation,text));
                    c.Save();
                    try { c.Clip(text.Clip); text.Draw(c,annotation.IsSelected&&annotation.IsEditable,model.Theme.Background); }
                    finally { c.Restore(); }
                }
                continue;
            }
            var(a,b)=AnnotationGeometry.Endpoints(model,annotation,layout.PlotArea);
            if(!a.IsFinite||!b.IsFinite) continue;
            var clip=annotation.ClipToPlot?layout.PlotArea:layout.Bounds;
            c.Save(); c.Clip(clip);
            var stroke=new StrokeStyle(annotation.Stroke,annotation.StrokeThickness,annotation.DashArray);
            switch(annotation.Kind)
            {
                case AnnotationKind.Box:
                    DrawBoundedRect(c,RectD.FromPoints(a,b),annotation.Fill,stroke,clip); break;
                case AnnotationKind.Text:
                    c.DrawText(annotation.Text,a,new(annotation.Stroke,annotation.FontSize)); break;
                case AnnotationKind.Arrow:
                    DrawArrow(c,a,b,stroke,10,5,true,clip); break;
                case AnnotationKind.Polyline: case AnnotationKind.Polygon:
                {
                    var points=annotation.Points.Select(p=>AnnotationGeometry.ToScreen(model,annotation,layout.PlotArea,p.X,p.Y)).ToArray();
                    if(annotation.Kind==AnnotationKind.Polygon) DrawPolygon(c,points,annotation.Fill,stroke,clip);
                    else DrawClippedPolyline(c,points,stroke,clip);
                    break;
                }
                case AnnotationKind.AxisMarker:
                {
                    string text=string.IsNullOrEmpty(annotation.Text)?model.FindYAxis(annotation.YAxisId).Format(annotation.Y1,true):annotation.Text;
                    var style=new TextStyle(model.Theme.Background,annotation.FontSize); var metrics=c.MeasureText(text,style);
                    c.DrawRectangle(new(a.X-4,a.Y-metrics.Ascent-3,metrics.Width+8,metrics.Height+5),annotation.Stroke);
                    c.DrawText(text,a,style); break;
                }
                case AnnotationKind.FibonacciRetracement:
                    foreach(double ratio in new[]{0,.236,.382,.5,.618,.786,1})
                    {
                        double y=a.Y+(b.Y-a.Y)*ratio; DrawClippedPolyline(c,[new(a.X,y),new(b.X,y)],stroke,clip);
                        c.DrawText($"{ratio:P1}",new(Math.Max(a.X,b.X)+4,y+4),new(annotation.Stroke,10));
                    }
                    break;
                default:
                    DrawClippedPolyline(c,[a,b],stroke,clip); break;
            }
            if(annotation.ShowLabel&&!string.IsNullOrEmpty(annotation.Text)&&annotation.Kind is not (AnnotationKind.Text or AnnotationKind.AxisMarker))
                c.DrawText(annotation.Text,new(Math.Min(a.X,b.X)+6,Math.Min(a.Y,b.Y)-6),new(annotation.Stroke,annotation.FontSize));
            if(annotation.IsSelected&&annotation.IsEditable)
            {
                foreach(var point in new[]{a,b}) c.DrawRectangle(new(point.X-4,point.Y-4,8,8),model.Theme.Background,new(annotation.Stroke,1.5));
            }
            c.Restore();
        }
    }
}
