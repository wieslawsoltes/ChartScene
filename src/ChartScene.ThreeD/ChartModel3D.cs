using System.Collections.ObjectModel;
using System.ComponentModel;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Charting;

namespace ChartScene.ThreeD;

public enum SeriesKind3D { Scatter,Line,PointLine,Bubble,SurfaceMesh,Column,Waterfall,Impulse,ErrorBars,Mesh }
public enum SurfaceDrawMode { Solid,Wireframe,SolidWithWireframe }
public interface IPaletteProvider3D { ColorRgba? GetColor(RenderableSeries3D series,int dataIndex,Vector3D position); }

public sealed class RenderableSeries3D:ObservableObject,IDisposable
{
    private IDataSeries? _data;
    private SurfaceData3D? _surface;
    private MeshData3D? _mesh;
    private string _name="3D series";
    private bool _visible=true,_lighting=true;
    private ColorRgba _stroke=ChartTheme.Dark.Palette[0];
    private double _pointSize=6,_lineThickness=2,_opacity=1,_columnWidthX=.7,_columnWidthZ=.7,_baseline,_bubbleScale=.04;
    private ColorMap _colorMap=ColorMap.Thermal;
    private DoubleRange? _colorRange;
    private SurfaceDrawMode _drawMode;
    private IPaletteProvider3D? _palette;
    public string Id { get; }=Guid.NewGuid().ToString("N");
    public HashSet<int> SelectedIndices { get; } = [];
    public SeriesKind3D Kind { get; }
    public int RequiredChannels=>Kind==SeriesKind3D.Bubble?4:Kind==SeriesKind3D.ErrorBars?9:3;
    public string SeriesName { get=>_name; set=>Set(ref _name,value??""); }
    public bool IsVisible { get=>_visible; set=>Set(ref _visible,value); }
    public bool EnableLighting { get=>_lighting; set=>Set(ref _lighting,value); }
    public ColorRgba Stroke { get=>_stroke; set=>Set(ref _stroke,value); }
    public double PointSize { get=>_pointSize; set { Positive(value); Set(ref _pointSize,value); } }
    public double StrokeThickness { get=>_lineThickness; set { Positive(value); Set(ref _lineThickness,value); } }
    public double Opacity { get=>_opacity; set { if(!double.IsFinite(value)||value<0||value>1) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _opacity,value); } }
    public double ColumnWidthX { get=>_columnWidthX; set { Positive(value); Set(ref _columnWidthX,value); } }
    public double ColumnWidthZ { get=>_columnWidthZ; set { Positive(value); Set(ref _columnWidthZ,value); } }
    public double ZeroLineY { get=>_baseline; set { if(!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _baseline,value); } }
    public double BubbleScale { get=>_bubbleScale; set { Positive(value); Set(ref _bubbleScale,value); } }
    public ColorMap ColorMap { get=>_colorMap; set { ArgumentNullException.ThrowIfNull(value); Set(ref _colorMap,value); } }
    public DoubleRange? ColorMapRange { get=>_colorRange; set { if(value is {IsValid:false}) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _colorRange,value); } }
    public SurfaceDrawMode SurfaceDrawMode { get=>_drawMode; set=>Set(ref _drawMode,value); }
    public IPaletteProvider3D? PaletteProvider { get=>_palette; set=>Set(ref _palette,value); }
    public IDataSeries? DataSeries
    {
        get=>_data;
        set
        {
            if(ReferenceEquals(value,_data)) return;
            if(value is not null&&value.ChannelCount<RequiredChannels) throw new ArgumentException($"{Kind} requires at least {RequiredChannels} channels.");
            if(_data is not null) _data.DataSeriesChanged-=Changed;
            _data=value;
            if(_data is not null) _data.DataSeriesChanged+=Changed;
            RaisePropertyChanged();
        }
    }
    public SurfaceData3D? SurfaceData { get=>_surface; set=>Set(ref _surface,value); }
    public MeshData3D? MeshData { get=>_mesh; set=>Set(ref _mesh,value); }
    public RenderableSeries3D(SeriesKind3D kind)=>Kind=kind;
    private void Changed(object? sender,EventArgs e)=>RaisePropertyChanged("DataRevision");
    private static void Positive(double value) { if(!double.IsFinite(value)||value<=0) throw new ArgumentOutOfRangeException(nameof(value)); }
    public void Invalidate()=>RaisePropertyChanged("Content");
    public void Dispose() { if(_data is not null) _data.DataSeriesChanged-=Changed; }
}

public sealed class ChartModel3D:ObservableObject,IDisposable
{
    private string _title="3D chart",_subtitle="Drag to orbit · Control-drag to pan · Wheel to zoom";
    private ChartTheme _theme=ChartTheme.Dark;
    private Vector3D _worldSize=new(2,1.5,2);
    private bool _grid=true,_legend=true,_disposed;
    private long _revision,_geometryRevision;
    private readonly HashSet<RenderableSeries3D> _subscribed=[];
    private readonly HashSet<AxisModel> _autoRanged=[];
    public AxisModel XAxis { get; }=new NumericAxis{Id="X",AxisTitle="X"};
    public AxisModel YAxis { get; }=new NumericAxis{Id="Y",AxisTitle="Y"};
    public AxisModel ZAxis { get; }=new NumericAxis{Id="Z",AxisTitle="Z"};
    public Camera3D Camera { get; }=new();
    public ObservableCollection<RenderableSeries3D> RenderableSeries { get; }=[];
    public string Title { get=>_title; set=>Set(ref _title,value??""); }
    public string Subtitle { get=>_subtitle; set=>Set(ref _subtitle,value??""); }
    public ChartTheme Theme { get=>_theme; set { ArgumentNullException.ThrowIfNull(value); Set(ref _theme,value); } }
    public bool ShowGrid { get=>_grid; set=>Set(ref _grid,value); }
    public bool ShowLegend { get=>_legend; set=>Set(ref _legend,value); }
    public Vector3D WorldSize { get=>_worldSize; set { if(!value.IsFinite||value.X<=0||value.Y<=0||value.Z<=0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _worldSize,value); } }
    public long Revision=>Interlocked.Read(ref _revision);
    public long GeometryRevision=>Interlocked.Read(ref _geometryRevision);
    public event EventHandler? Changed;
    public ChartModel3D()
    {
        foreach(var axis in Axes) axis.PropertyChanged+=GeometryChanged;
        PropertyChanged+=GeometryChanged;
        Camera.PropertyChanged+=CameraChanged;
        RenderableSeries.CollectionChanged+=CollectionChanged;
    }
    public IReadOnlyList<AxisModel> Axes=>new[]{XAxis,YAxis,ZAxis};
    public Vector3D Normalize(Vector3D point)
    {
        double Coordinate(AxisModel axis,double value,double size)
        {
            double fraction=(axis.ToScale(value)-axis.ToScale(axis.VisibleRange.Min))/axis.ScaleRange.Size;
            if(axis.FlippedCoordinates) fraction=1-fraction;
            return (fraction-.5)*size;
        }
        return new(Coordinate(XAxis,point.X,WorldSize.X),Coordinate(YAxis,point.Y,WorldSize.Y),Coordinate(ZAxis,point.Z,WorldSize.Z));
    }
    public Vector3D Denormalize(Vector3D point)
    {
        double Coordinate(AxisModel axis,double value,double size)
        {
            double fraction=value/size+.5; if(axis.FlippedCoordinates) fraction=1-fraction;
            return axis.FromScale(axis.ScaleRange.At(fraction));
        }
        return new(Coordinate(XAxis,point.X,WorldSize.X),Coordinate(YAxis,point.Y,WorldSize.Y),Coordinate(ZAxis,point.Z,WorldSize.Z));
    }
    public void AutoRangeAxes()
    {
        foreach(var axis in Axes) if(axis.AutoRange==AutoRange.Always||axis.AutoRange==AutoRange.Once&&!_autoRanged.Contains(axis)) Fit(axis);
    }
    public void ZoomExtents() { foreach(var axis in Axes) Fit(axis); Camera.Reset(); }
    private void Fit(AxisModel axis)
    {
        int channel=ReferenceEquals(axis,XAxis)?0:ReferenceEquals(axis,YAxis)?1:2;
        double min=double.PositiveInfinity,max=double.NegativeInfinity;
        void Include(double value)
        {
            if(!double.IsFinite(value)||axis.Scale==AxisScale.Logarithmic&&value<=0) return;
            min=Math.Min(min,value); max=Math.Max(max,value);
        }
        foreach(var series in RenderableSeries.Where(s=>s.IsVisible))
        {
            if(series.SurfaceData is { } surface)
            {
                foreach(double value in (channel==0?surface.XCoordinates:channel==1?surface.Heights:surface.ZCoordinates).Span) Include(value);
                if(channel==1&&series.Kind==SeriesKind3D.Waterfall) Include(series.ZeroLineY);
            }
            if(series.MeshData is { } mesh) foreach(var point in mesh.Positions.Span) Include(point.Component(channel));
            if(series.DataSeries is { } source)
            {
                var data=source.Snapshot();
                for(int i=0;i<data.Count;i++)
                {
                    Include(data[channel,i]);
                    if(series.Kind==SeriesKind3D.Column&&channel!=1)
                    {
                        double half=(channel==0?series.ColumnWidthX:series.ColumnWidthZ)/2;
                        Include(data[channel,i]-half); Include(data[channel,i]+half);
                    }
                    if(series.Kind==SeriesKind3D.ErrorBars) { Include(data[3+channel*2,i]); Include(data[4+channel*2,i]); }
                }
                if(channel==1&&series.Kind is SeriesKind3D.Column or SeriesKind3D.Impulse) Include(series.ZeroLineY);
            }
        }
        if(!double.IsFinite(min)) return;
        DoubleRange range=axis.ZoomExtentsRange??DoubleRange.FromExtents(min,max);
        if(axis.Scale==AxisScale.Logarithmic)
        {
            if(range.Min<=0) return;
            var scaled=new DoubleRange(axis.ToScale(range.Min),axis.ToScale(range.Max)); var grown=scaled.Grow(axis.GrowByLower,axis.GrowByUpper);
            double a=axis.FromScale(grown.Min),b=axis.FromScale(grown.Max);
            if(double.IsFinite(a)&&double.IsFinite(b)&&a>0&&a<b) axis.VisibleRange=new(a,b);
        }
        else axis.VisibleRange=range.Grow(axis.GrowByLower,axis.GrowByUpper);
        _autoRanged.Add(axis);
    }
    public IReadOnlyList<string> Validate()
    {
        var errors=new List<string>();
        foreach(var series in RenderableSeries.Where(s=>s.IsVisible))
        {
            if(series.Kind is SeriesKind3D.SurfaceMesh or SeriesKind3D.Waterfall)
            { if(series.SurfaceData is null) errors.Add($"'{series.SeriesName}' requires surface data."); }
            else if(series.Kind==SeriesKind3D.Mesh)
            { if(series.MeshData is null) errors.Add($"'{series.SeriesName}' requires indexed mesh data."); }
            else if(series.DataSeries is null) errors.Add($"'{series.SeriesName}' requires {series.RequiredChannels}-channel data.");
        }
        return errors;
    }
    private void CollectionChanged(object? sender,System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        var next=RenderableSeries.ToHashSet();
        foreach(var series in _subscribed.Except(next).ToArray()) { series.PropertyChanged-=GeometryChanged; _subscribed.Remove(series); }
        foreach(var series in next.Except(_subscribed)) { series.PropertyChanged+=GeometryChanged; _subscribed.Add(series); }
        InvalidateGeometry();
    }
    private void GeometryChanged(object? sender,PropertyChangedEventArgs e)=>InvalidateGeometry();
    private void CameraChanged(object? sender,PropertyChangedEventArgs e) { Interlocked.Increment(ref _revision); Changed?.Invoke(this,EventArgs.Empty); }
    public void InvalidateGeometry() { if(_disposed) return; Interlocked.Increment(ref _geometryRevision); Interlocked.Increment(ref _revision); Changed?.Invoke(this,EventArgs.Empty); }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true;
        PropertyChanged-=GeometryChanged; Camera.PropertyChanged-=CameraChanged; RenderableSeries.CollectionChanged-=CollectionChanged;
        foreach(var axis in Axes) axis.PropertyChanged-=GeometryChanged;
        foreach(var series in _subscribed) { series.PropertyChanged-=GeometryChanged; series.Dispose(); }
        _subscribed.Clear();
    }
}
