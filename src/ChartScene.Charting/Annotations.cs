using System.Collections.ObjectModel;
using ChartScene.Core;

namespace ChartScene.Charting;

public enum AnnotationKind { Line,HorizontalLine,VerticalLine,Box,Text,Arrow,Polyline,Polygon,AxisMarker,FibonacciRetracement }
public enum CoordinateMode { Data,Relative,Pixel }
public enum AnnotationLayer { BelowSeries,AboveSeries }
public class ChartAnnotation:ObservableObject
{
    private double _x1,_y1,_x2=1,_y2=1,_thickness=1.5,_fontSize=12;
    private string _text="",_xAxisId="X",_yAxisId="Y";
    private bool _visible=true,_editable=true,_selected;
    private ColorRgba _stroke=ColorRgba.Parse("#E9A85B"),_fill=ColorRgba.Parse("#E9A85B").WithAlpha(35);
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public AnnotationKind Kind { get; }
    public string XAxisId { get=>_xAxisId; set=>Set(ref _xAxisId,value); }
    public string YAxisId { get=>_yAxisId; set=>Set(ref _yAxisId,value); }
    public double X1 { get=>_x1; set { Finite(value); Set(ref _x1,value); } }
    public double Y1 { get=>_y1; set { Finite(value); Set(ref _y1,value); } }
    public double X2 { get=>_x2; set { Finite(value); Set(ref _x2,value); } }
    public double Y2 { get=>_y2; set { Finite(value); Set(ref _y2,value); } }
    public string Text { get=>_text; set=>Set(ref _text,value??""); }
    public bool IsVisible { get=>_visible; set=>Set(ref _visible,value); }
    public bool IsEditable { get=>_editable; set=>Set(ref _editable,value); }
    public bool IsSelected { get=>_selected; set=>Set(ref _selected,value); }
    public ColorRgba Stroke { get=>_stroke; set=>Set(ref _stroke,value); }
    public ColorRgba Fill { get=>_fill; set=>Set(ref _fill,value); }
    public double StrokeThickness { get=>_thickness; set { Finite(value); if(value<0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _thickness,value); } }
    public double FontSize { get=>_fontSize; set { Finite(value); if(value<=0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _fontSize,value); } }
    public CoordinateMode XCoordinateMode { get; set; }
    public CoordinateMode YCoordinateMode { get; set; }
    public AnnotationLayer Layer { get; set; }=AnnotationLayer.AboveSeries;
    public bool ClipToPlot { get; set; }=true;
    public bool ShowLabel { get; set; }=true;
    public double[] DashArray { get; set; }=[];
    public ObservableCollection<PointD> Points { get; }=[];
    public ChartAnnotation(AnnotationKind kind=AnnotationKind.Line)
    { Kind=kind; Points.CollectionChanged+=(_,_)=>RaisePropertyChanged(nameof(Points)); }
    public void Invalidate()=>RaisePropertyChanged("Content");
    private static void Finite(double v) { if(!double.IsFinite(v)) throw new ArgumentOutOfRangeException(nameof(v)); }
}
public sealed class LineAnnotation():ChartAnnotation(AnnotationKind.Line);
public sealed class HorizontalLineAnnotation():ChartAnnotation(AnnotationKind.HorizontalLine);
public sealed class VerticalLineAnnotation():ChartAnnotation(AnnotationKind.VerticalLine);
public sealed class BoxAnnotation():ChartAnnotation(AnnotationKind.Box);
public sealed class TextAnnotation():ChartAnnotation(AnnotationKind.Text);
