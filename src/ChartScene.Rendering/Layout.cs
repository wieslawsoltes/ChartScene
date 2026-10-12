using ChartScene.Core;
using ChartScene.Charting;

namespace ChartScene.Rendering;

public sealed record AxisLayout(AxisModel Axis,RectD Bounds,IReadOnlyList<AxisTick> Ticks,CoordinateCalculator Calculator);
public sealed record LegendItem(RenderableSeries Series,RectD Bounds);
public sealed record HitRegion(RenderableSeries Series,int DataIndex,RectD Bounds,PointD Position,double Radius=0);
public sealed record SeriesHit(RenderableSeries Series,int DataIndex,double X,double Y,PointD Position,double Distance,string? Details=null);
public sealed record RenderStatistics(long SourcePoints,long RenderedPoints,int DrawingCommands,double CpuMilliseconds,long ModelRevision);
public sealed record ChartFrame(ChartLayout Layout,RenderStatistics Statistics,IReadOnlyList<HitRegion> HitRegions,IReadOnlyList<LegendItem> LegendItems,IReadOnlyList<string> Diagnostics)
{
    /// <summary>Measured text geometry belonging to this rendered frame, including its actual font metrics.</summary>
    public IReadOnlyList<AnnotationTextRegion> TextAnnotations { get; init; } = [];
}
public sealed class ChartViewState
{
    public PointD? Cursor { get; set; }
    public RectD? RubberBand { get; set; }
    public bool ShowCursor { get; set; }=true;
    public bool ShowRollover { get; set; }=true;
    public IReadOnlyList<SeriesHit> Hits { get; set; }=Array.Empty<SeriesHit>();
}

public sealed class ChartLayout
{
    public RectD Bounds { get; }
    public RectD PlotArea { get; }
    public IReadOnlyList<AxisLayout> Axes { get; }
    public double HeaderHeight { get; }
    public PlotMargins NaturalMargins { get; }
    public ChartLayout(RectD bounds,RectD plot,IReadOnlyList<AxisLayout> axes,double headerHeight=0,PlotMargins naturalMargins=default)
    { Bounds=bounds; PlotArea=plot; Axes=axes; HeaderHeight=headerHeight; NaturalMargins=naturalMargins; }
    public SeriesProjection Projection(ChartModel model,RenderableSeries series)=>new(model,series,PlotArea);
    public static ChartLayout Measure(IChartCanvas canvas,ChartModel model,double width,double height,PlotMargins minimumMargins=default)
    {
        if(!double.IsFinite(width)||!double.IsFinite(height)||width<0||height<0) throw new ArgumentOutOfRangeException(nameof(width));
        var bounds=new RectD(0,0,Math.Max(0,width),Math.Max(0,height));
        if(width<100||height<80) return new(bounds,new(0,0,0,0),[]);
        double header=string.IsNullOrEmpty(model.Title)?8:string.IsNullOrEmpty(model.Subtitle)?35:53;
        bool noAxes=model.RenderableSeries.Any(s=>s.IsVisible&&s.IsCircular);
        if(noAxes||model.CoordinateSystem!=CoordinateSystem.Cartesian)
        {
            var natural=new PlotMargins(42,header+12,42,43);
            var margins=PlotMargins.Max(natural,minimumMargins);
            return new(bounds,Plot(bounds,margins),[],header,natural);
        }
        double left=12,right=12,top=header,bottom=12;
        var thickness=new Dictionary<AxisModel,double>();
        foreach(var axis in model.XAxes.Concat(model.YAxes).Where(a=>a.IsVisible))
        {
            var ticks=TickGenerator.Generate(axis,axis.IsVertical?height-header-60:width-120);
            double band=axis.IsVertical?Math.Max(20,ticks.Where(t=>t.IsMajor).Select(t=>canvas.MeasureText(t.Label,new(model.Theme.Muted,11)).Width).DefaultIfEmpty(20).Max())+15:27;
            if(!string.IsNullOrEmpty(axis.AxisTitle)) band+=18;
            band=Math.Min(axis.IsVertical?180:90,band);
            thickness[axis]=band;
            switch(axis.AxisAlignment) { case AxisAlignment.Left:left+=band;break;case AxisAlignment.Right:right+=band;break;case AxisAlignment.Top:top+=band;break;case AxisAlignment.Bottom:bottom+=band;break; }
        }
        var naturalMargins=new PlotMargins(left,top,right,bottom);
        var area=Plot(bounds,PlotMargins.Max(naturalMargins,minimumMargins));
        if(area.IsEmpty) return new(bounds,area,[],header,naturalMargins);
        double l=area.X,r=area.Right,t=area.Y,b=area.Bottom;
        var layouts=new List<AxisLayout>();
        foreach(var axis in model.XAxes.Concat(model.YAxes).Where(a=>a.IsVisible))
        {
            double band=thickness[axis]; RectD rect;
            switch(axis.AxisAlignment)
            {
                case AxisAlignment.Left: l-=band; rect=new(l,area.Y,band,area.Height);break;
                case AxisAlignment.Right:rect=new(r,area.Y,band,area.Height);r+=band;break;
                case AxisAlignment.Top:t-=band;rect=new(area.X,t,area.Width,band);break;
                default:rect=new(area.X,b,area.Width,band);b+=band;break;
            }
            layouts.Add(new(axis,rect,TickGenerator.Generate(axis,axis.IsVertical?area.Height:area.Width),new(axis,axis.IsVertical?area.Y:area.X,axis.IsVertical?area.Height:area.Width)));
        }
        return new(bounds,area,layouts,header,naturalMargins);
    }
    private static RectD Plot(RectD bounds,PlotMargins margins)
    {
        // Preserve an empty plot when the reserved axes do not fit. Never put a fabricated
        // one-pixel plot outside the surface, or create zero-size coordinate calculators.
        double x=Math.Min(bounds.Width,margins.Left), y=Math.Min(bounds.Height,margins.Top);
        return new(x,y,Math.Max(0,bounds.Width-x-margins.Right),Math.Max(0,bounds.Height-y-margins.Bottom));
    }

}

public sealed class SeriesProjection
{
    public RectD Plot { get; }
    public AxisModel XAxis { get; }
    public AxisModel YAxis { get; }
    private readonly CoordinateCalculator _x,_y;
    private readonly ChartModel _model;
    private readonly RenderableSeries _series;
    public SeriesProjection(ChartModel model,RenderableSeries series,RectD plot)
    {
        _model=model; _series=series; Plot=plot;
        XAxis=series.IsHorizontal?model.FindYAxis(series.YAxisId):model.FindXAxis(series.XAxisId);
        YAxis=series.IsHorizontal?model.FindXAxis(series.XAxisId):model.FindYAxis(series.YAxisId);
        _x=new(XAxis,XAxis.IsVertical?plot.Y:plot.X,XAxis.IsVertical?plot.Height:plot.Width);
        _y=new(YAxis,YAxis.IsVertical?plot.Y:plot.X,YAxis.IsVertical?plot.Height:plot.Width);
    }
    public PointD ToScreen(double x,double y,double z=0)
    {
        if(_model.CoordinateSystem==CoordinateSystem.Ternary||_series.Kind is SeriesKind.TernaryScatter or SeriesKind.TernaryLine)
        {
            double total=x+y+z;
            if(total<=0||x<0||y<0||z<0) return new(double.NaN,double.NaN);
            var triangle=TernaryBounds(Plot);
            return new(triangle.X+triangle.Width*(y+z/2)/total,triangle.Bottom-triangle.Height*z/total);
        }
        if(_model.CoordinateSystem==CoordinateSystem.Polar||_series.Kind is SeriesKind.PolarLine or SeriesKind.Radar)
        {
            double angle=_model.PolarAnglesInDegrees?x*Math.PI/180:x;
            double ratio=(YAxis.ToScale(y)-YAxis.ToScale(YAxis.VisibleRange.Min))/YAxis.ScaleRange.Size;
            if(YAxis.FlippedCoordinates) ratio=1-ratio;
            double radius=ratio*Math.Min(Plot.Width,Plot.Height)/2;
            return new(Plot.Center.X+Math.Cos(angle)*radius,Plot.Center.Y-Math.Sin(angle)*radius);
        }
        double a=_x.GetCoordinate(x),b=_y.GetCoordinate(y);
        return XAxis.IsVertical?new(b,a):new(a,b);
    }
    public PointD ToData(PointD p)
    {
        if(_model.CoordinateSystem==CoordinateSystem.Ternary)
        {
            var triangle=TernaryBounds(Plot); double c=(triangle.Bottom-p.Y)/triangle.Height,b=(p.X-triangle.X)/triangle.Width-c/2;
            return new(100*(1-b-c),100*b);
        }
        if(_model.CoordinateSystem==CoordinateSystem.Polar||_series.Kind is SeriesKind.PolarLine or SeriesKind.Radar)
        {
            double angle=Math.Atan2(Plot.Center.Y-p.Y,p.X-Plot.Center.X); if(angle<0) angle+=2*Math.PI;
            double ratio=Math.Sqrt((p-Plot.Center).LengthSquared)/(Math.Min(Plot.Width,Plot.Height)/2);
            if(YAxis.FlippedCoordinates) ratio=1-ratio;
            return new(_model.PolarAnglesInDegrees?angle*180/Math.PI:angle,YAxis.FromScale(YAxis.ScaleRange.At(ratio)));
        }
        return XAxis.IsVertical?new(_x.GetDataValue(p.Y),_y.GetDataValue(p.X)):new(_x.GetDataValue(p.X),_y.GetDataValue(p.Y));
    }
    public static RectD TernaryBounds(RectD plot)
    {
        double w=Math.Min(plot.Width,plot.Height*2/Math.Sqrt(3)),h=w*Math.Sqrt(3)/2;
        return new(plot.Center.X-w/2,plot.Center.Y-h/2,w,h);
    }
}
