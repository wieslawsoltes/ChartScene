using System.Collections.ObjectModel;
using System.ComponentModel;
using ChartScene.Core;
using ChartScene.Data;

namespace ChartScene.Charting;

public enum CoordinateSystem { Cartesian,Polar,Ternary }
public enum AspectRatioMode { X,Y,Fit }
public sealed class ChartModel:ObservableObject,IDisposable
{
    private string _title="ChartScene",_subtitle="";
    private ChartTheme _theme=ChartTheme.Dark;
    private CoordinateSystem _coordinates;
    private bool _legend=true;
    private long _revision;
    private readonly HashSet<INotifyPropertyChanged> _observed=[];
    private bool _disposed;
    private readonly object _changeGate=new();
    private int _suspended;
    private bool _pending;
    private double? _aspectRatio;
    private AspectRatioMode _aspectMode=AspectRatioMode.Fit;
    private bool _polarDegrees=true;
    public ObservableCollection<AxisModel> XAxes { get; }=[];
    public ObservableCollection<AxisModel> YAxes { get; }=[];
    public ObservableCollection<RenderableSeries> RenderableSeries { get; }=[];
    public ObservableCollection<ChartAnnotation> Annotations { get; }=[];
    public AxisModel XAxis=>XAxes[0];
    public AxisModel YAxis=>YAxes[0];
    public string Title { get=>_title; set=>Set(ref _title,value??""); }
    public string Subtitle { get=>_subtitle; set=>Set(ref _subtitle,value??""); }
    public ChartTheme Theme { get=>_theme; set { ArgumentNullException.ThrowIfNull(value); Set(ref _theme,value); } }
    public CoordinateSystem CoordinateSystem { get=>_coordinates; set=>Set(ref _coordinates,value); }
    public bool ShowLegend { get=>_legend; set=>Set(ref _legend,value); }
    public double? AspectRatio
    {
        get=>_aspectRatio;
        set { if(value is { } ratio&&(!double.IsFinite(ratio)||ratio<=0)) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _aspectRatio,value); }
    }
    public AspectRatioMode AspectRatioMode
    {
        get=>_aspectMode;
        set { if(!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _aspectMode,value); }
    }
    public bool PolarAnglesInDegrees { get=>_polarDegrees; set=>Set(ref _polarDegrees,value); }
    public long Revision=>Interlocked.Read(ref _revision);
    public event EventHandler? Changed;
    public ChartModel()
    {
        XAxes.Add(new NumericAxis{Id="X",AxisAlignment=AxisAlignment.Bottom,AxisTitle="X"});
        YAxes.Add(new NumericAxis{Id="Y",AxisAlignment=AxisAlignment.Left,AxisTitle="Y"});
        XAxes.CollectionChanged+=OnCollectionChanged; YAxes.CollectionChanged+=OnCollectionChanged;
        RenderableSeries.CollectionChanged+=OnCollectionChanged; Annotations.CollectionChanged+=OnCollectionChanged;
        PropertyChanged+=OnObjectChanged; ReconcileSubscriptions();
    }
    private void OnCollectionChanged(object? sender,System.Collections.Specialized.NotifyCollectionChangedEventArgs e) { ReconcileSubscriptions(); Invalidate(); }
    private void ReconcileSubscriptions()
    {
        var next=XAxes.Cast<INotifyPropertyChanged>().Concat(YAxes).Concat(RenderableSeries).Concat(Annotations).ToHashSet();
        foreach(var item in _observed.Except(next).ToArray()) { item.PropertyChanged-=OnObjectChanged; _observed.Remove(item); }
        foreach(var item in next.Except(_observed)) { item.PropertyChanged+=OnObjectChanged; _observed.Add(item); }
    }
    private void OnObjectChanged(object? sender,PropertyChangedEventArgs e)=>Invalidate();
    /// <summary>
    /// Coalesces Changed notifications for a batch. Properties and Revision remain current;
    /// this is notification batching, not rollback or cross-series storage isolation.
    /// Mutate model collections on their owning UI thread. Data callbacks may be concurrent.
    /// </summary>
    public IDisposable SuspendUpdates()
    {
        lock(_changeGate) { ObjectDisposedException.ThrowIf(_disposed,this); _suspended=checked(_suspended+1); }
        return new ActionDisposable(()=>
        {
            bool notify;
            lock(_changeGate)
            {
                if(_disposed) return;
                notify=--_suspended==0&&_pending;
                if(notify) _pending=false;
            }
            if(notify) Changed?.Invoke(this,EventArgs.Empty);
        });
    }
    public void Invalidate()
    {
        lock(_changeGate)
        {
            if(_disposed) return;
            Interlocked.Increment(ref _revision);
            if(_suspended>0) { _pending=true; return; }
        }
        Changed?.Invoke(this,EventArgs.Empty);
    }
    public AxisModel FindXAxis(string id)=>XAxes.FirstOrDefault(a=>a.Id==id)??throw new InvalidOperationException($"Unknown X axis '{id}'.");
    public AxisModel FindYAxis(string id)=>YAxes.FirstOrDefault(a=>a.Id==id)??throw new InvalidOperationException($"Unknown Y axis '{id}'.");
    public IReadOnlyList<string> Validate()
    {
        var errors=new List<string>();
        if(XAxes.Count==0||YAxes.Count==0) errors.Add("At least one X and one Y axis are required.");
        if(XAxes.GroupBy(a=>a.Id).Any(g=>g.Count()>1)||YAxes.GroupBy(a=>a.Id).Any(g=>g.Count()>1)) errors.Add("Axis IDs must be unique within each dimension.");
        foreach(var s in RenderableSeries)
        {
            if(!XAxes.Any(a=>a.Id==s.XAxisId)||!YAxes.Any(a=>a.Id==s.YAxisId)) errors.Add($"Series '{s.SeriesName}' references an unknown axis.");
            if(s.DataSeries is { } d&&d.ChannelCount<s.RequiredChannels) errors.Add($"Series '{s.SeriesName}' has insufficient data channels.");
        }
        foreach(var a in Annotations) if(!XAxes.Any(x=>x.Id==a.XAxisId)||!YAxes.Any(y=>y.Id==a.YAxisId)) errors.Add($"Annotation '{a.Id}' references an unknown axis.");
        if(AspectRatio is { } aspect&&(!double.IsFinite(aspect)||aspect<=0)) errors.Add("Aspect ratio must be finite and positive.");
        return errors;
    }
    public void ZoomExtents(bool visibleYOnly=false)
    {
        if(!visibleYOnly) foreach(var axis in XAxes) FitAxis(axis,true,null);
        foreach(var axis in YAxes) FitAxis(axis,false,visibleYOnly?XAxis.VisibleRange:null);
    }
    public void AutoRangeAxes()
    {
        foreach(var axis in XAxes.Concat(YAxes))
            if(axis.AutoRange==AutoRange.Always||axis.AutoRange==AutoRange.Once&&!axis.HasAutoRanged)
                FitAxis(axis,XAxes.Contains(axis),null);
    }
    private void FitAxis(AxisModel axis,bool x,DoubleRange? visibleX)
    {
        var extent=axis.ZoomExtentsRange??GetDataRange(axis,x,visibleX);
        if(extent is not { } range) return;
        if(axis.Scale==AxisScale.Logarithmic)
        {
            if(range.Min<=0) return;
            var scaled=new DoubleRange(axis.ToScale(range.Min),axis.ToScale(range.Max));
            var grown=scaled.Grow(axis.GrowByLower,axis.GrowByUpper);
            axis.VisibleRange=new(axis.FromScale(grown.Min),axis.FromScale(grown.Max));
        }
        else axis.VisibleRange=range.Grow(axis.GrowByLower,axis.GrowByUpper);
        axis.HasAutoRanged=true;
    }
    public DoubleRange? GetDataRange(AxisModel axis,bool x,DoubleRange? visibleX=null)
    {
        DoubleRange? result=null;
        void Add(DoubleRange? range) { if(range is { } r) result=result is { } old?old.Union(r):r; }
        var stacks=StackCalculator.Capture(this);
        foreach(var s in RenderableSeries.Where(s=>s.IsVisible&&!s.IsCircular&&(x?s.XAxisId:s.YAxisId)==axis.Id))
        {
            if(s.HeatmapGrid is { } grid)
            {
                var edges=x?grid.XEdges.Span:grid.YEdges.Span;
                if(axis.Scale!=AxisScale.Logarithmic||edges[0]>0) Add(new(edges[0],edges[^1]));
                continue;
            }
            if(s.DataSeries is not { } data) continue;
            var snapshot=stacks.TryGetValue(s,out var captured)?captured.Snapshot:data.Snapshot();
            bool positive=axis.Scale==AxisScale.Logarithmic;
            if(x != s.IsHorizontal) { Add(snapshot.Extents(0,null,positive)); continue; }
            if(stacks.TryGetValue(s,out var stack))
            {
                Add(stack.GetRange(visibleX,positive));
                continue;
            }
            int[] channels=s.Kind switch {SeriesKind.Candlestick or SeriesKind.Ohlc=>[2,3],SeriesKind.BoxPlot or SeriesKind.GroupedBoxPlot=>[1,5],SeriesKind.Band or SeriesKind.DigitalBand=>[1,2],SeriesKind.ErrorBars=>[2,3],_=>[s.YChannel]};
            foreach(int channel in channels) Add(snapshot.Extents(channel,visibleX,positive));
            if(s.Kind is SeriesKind.Column or SeriesKind.Bar or SeriesKind.Mountain or SeriesKind.SplineMountain or SeriesKind.Impulse)
                if(!positive||s.ZeroLineY>0) Add(DoubleRange.FromExtents(s.ZeroLineY,s.ZeroLineY));
        }
        return result;
    }
    public void ApplyAspectRatio(double width,double height)
    {
        if(AspectRatio is not { } ratio||CoordinateSystem!=CoordinateSystem.Cartesian||width<=0||height<=0) return;
        double xSize=XAxis.ScaleRange.Size,ySize=YAxis.ScaleRange.Size;
        double desiredY=xSize*height*ratio/width,desiredX=ySize*width/(height*ratio);
        if(AspectRatioMode==AspectRatioMode.X||AspectRatioMode==AspectRatioMode.Fit&&desiredY>=ySize)
        { double center=YAxis.ScaleRange.Center; YAxis.SetScaleRange(new(center-desiredY/2,center+desiredY/2)); }
        else { double center=XAxis.ScaleRange.Center; XAxis.SetScaleRange(new(center-desiredX/2,center+desiredX/2)); }
    }
    public void Dispose()
    {
        lock(_changeGate) { if(_disposed) return; _disposed=true; _pending=false; _suspended=0; }
        StackCalculator.Release(this);
        XAxes.CollectionChanged-=OnCollectionChanged; YAxes.CollectionChanged-=OnCollectionChanged; RenderableSeries.CollectionChanged-=OnCollectionChanged; Annotations.CollectionChanged-=OnCollectionChanged;
        PropertyChanged-=OnObjectChanged;
        foreach(var item in _observed) item.PropertyChanged-=OnObjectChanged;
        _observed.Clear(); foreach(var s in RenderableSeries) s.Dispose();
    }
}
