using System.Collections.ObjectModel;
using ChartScene.Core;

namespace ChartScene.Charting;

public enum AnnotationKind { Line,HorizontalLine,VerticalLine,Box,Text,Arrow,Polyline,Polygon,AxisMarker,FibonacciRetracement }
public enum CoordinateMode { Data,Relative,Pixel }
public enum AnnotationLayer { BelowSeries,AboveSeries }
public enum HorizontalAnchorPoint { Left,Center,Right }
public enum VerticalAnchorPoint { Top,Center,Bottom,Baseline }
public enum AnnotationTextAlignment { Left,Center,Right }
public partial class ChartAnnotation:ObservableObject
{
    private HorizontalAnchorPoint _horizontalAnchor;
    private VerticalAnchorPoint _verticalAnchor=VerticalAnchorPoint.Baseline;
    private AnnotationTextAlignment _textAlignment;
    private string _fontFamily="Arial";
    private int _fontWeight=400;
    private FontSlant _fontSlant;
    private double _rotationAngle,_textPadding,_borderThickness,_lineSpacing=1;
    private ColorRgba? _background,_borderColor;
    public HorizontalAnchorPoint HorizontalAnchorPoint { get=>_horizontalAnchor; set { CheckEnum(value); Set(ref _horizontalAnchor,value); } }
    /// <summary>Base annotations retain legacy baseline placement. TextAnnotation defaults to Top.</summary>
    public VerticalAnchorPoint VerticalAnchorPoint { get=>_verticalAnchor; set { CheckEnum(value); Set(ref _verticalAnchor,value); } }
    public AnnotationTextAlignment TextAlignment { get=>_textAlignment; set { CheckEnum(value); Set(ref _textAlignment,value); } }
    public string FontFamily { get=>_fontFamily; set { if(string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A font family is required.",nameof(value)); Set(ref _fontFamily,value); } }
    /// <summary>OpenType-style weight in [1,1000]; 400 is normal and 700 is bold.</summary>
    public int FontWeight
    {
        get=>_fontWeight;
        set
        {
            if(value is <1 or >1000) throw new ArgumentOutOfRangeException(nameof(value));
            bool wasBold=FontBold;
            if(Set(ref _fontWeight,value) && wasBold!=FontBold) RaisePropertyChanged(nameof(FontBold));
        }
    }
    /// <summary>Compatibility shortcut. Assigning selects 700/400; use FontWeight for other weights.</summary>
    public bool FontBold { get=>_fontWeight>=700; set=>FontWeight=value?700:400; }
    public FontSlant FontSlant { get=>_fontSlant; set { CheckEnum(value); Set(ref _fontSlant,value); } }
    /// <summary>Clockwise screen rotation around the X1/Y1 anchor, in degrees.</summary>
    public double RotationAngle { get=>_rotationAngle; set { Finite(value); Set(ref _rotationAngle,value%360); } }
    public double TextPadding { get=>_textPadding; set { Finite(value); if(value<0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _textPadding,value); } }
    public double LineSpacing { get=>_lineSpacing; set { Finite(value); if(value<1) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _lineSpacing,value); } }
    public ColorRgba? Background { get=>_background; set=>Set(ref _background,value); }
    public ColorRgba? BorderColor { get=>_borderColor; set=>Set(ref _borderColor,value); }
    public double BorderThickness { get=>_borderThickness; set { Finite(value); if(value<0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _borderThickness,value); } }
    private double _x1,_y1,_x2=1,_y2=1,_thickness=1.5,_fontSize=12;
    private string _text="",_xAxisId="X",_yAxisId="Y";
    private bool _visible=true,_editable=true,_selected,_clipToPlot=true,_showLabel=true;
    private CoordinateMode _xCoordinateMode,_yCoordinateMode;
    private AnnotationLayer _layer=AnnotationLayer.AboveSeries;
    private double[] _dashArray=[];
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
    public CoordinateMode XCoordinateMode { get=>_xCoordinateMode; set { CheckEnum(value); Set(ref _xCoordinateMode,value); } }
    public CoordinateMode YCoordinateMode { get=>_yCoordinateMode; set { CheckEnum(value); Set(ref _yCoordinateMode,value); } }
    public AnnotationLayer Layer { get=>_layer; set { CheckEnum(value); Set(ref _layer,value); } }
    public bool ClipToPlot { get=>_clipToPlot; set=>Set(ref _clipToPlot,value); }
    public bool ShowLabel { get=>_showLabel; set=>Set(ref _showLabel,value); }
    /// <summary>Assign a new dash pattern to change rendering; returned arrays are defensive copies.</summary>
    public double[] DashArray
    {
        get=>(double[])_dashArray.Clone();
        set
        {
            double[] copy=value?.ToArray()??[];
            if(copy.Any(v=>!double.IsFinite(v)||v<=0)) throw new ArgumentException("Dash lengths must be finite and positive.",nameof(value));
            if(!_dashArray.AsSpan().SequenceEqual(copy)) Set(ref _dashArray,copy);
        }
    }
    public ObservableCollection<PointD> Points { get; }=[];
    public ChartAnnotation(AnnotationKind kind=AnnotationKind.Line)
    { Kind=kind; Points.CollectionChanged+=(_,_)=>RaisePropertyChanged(nameof(Points)); }
    public void Invalidate()=>RaisePropertyChanged("Content");
    private static void CheckEnum<T>(T value) where T:struct,Enum { if(!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); }
    private static void Finite(double v) { if(!double.IsFinite(v)) throw new ArgumentOutOfRangeException(nameof(v)); }
}
public sealed class LineAnnotation():ChartAnnotation(AnnotationKind.Line);
public sealed class HorizontalLineAnnotation():ChartAnnotation(AnnotationKind.HorizontalLine);
public sealed class VerticalLineAnnotation():ChartAnnotation(AnnotationKind.VerticalLine);
public sealed class BoxAnnotation():ChartAnnotation(AnnotationKind.Box);
public sealed class TextAnnotation:ChartAnnotation
{
    public TextAnnotation():base(AnnotationKind.Text) => VerticalAnchorPoint=VerticalAnchorPoint.Top;
}
