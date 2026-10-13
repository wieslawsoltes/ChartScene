using ChartScene.Core;
using ChartScene.Charting;
using ChartScene.Rendering;

namespace ChartScene.Interaction;

public enum ChartInteractionMode { Pan,ZoomRectangle,Select,DrawAnnotation }
public enum ChartInputKind { Down,Move,Up,Cancel,Wheel,DoubleClick,Key }
public enum ChartPointerKind { Mouse,Touch,Pen }
public enum ModifierResult { Unhandled,Handled,Capture }
public readonly record struct ChartInput(ChartInputKind Kind,PointD Position,int PointerId=0,ChartPointerKind PointerKind=ChartPointerKind.Mouse,bool Shift=false,bool Control=false,bool Alt=false,double WheelDelta=0,string? Key=null);
public interface IChartModifier
{
    bool IsEnabled { get; set; }
    ModifierResult Handle(ChartController controller,ChartInput input);
    void Cancel(ChartController controller);
}
public abstract class ChartModifier:IChartModifier
{
    public bool IsEnabled { get; set; }=true;
    public abstract ModifierResult Handle(ChartController controller,ChartInput input);
    public virtual void Cancel(ChartController controller) { }
}

/// <summary>UI-independent modifier pipeline. Each view owns a controller; models and data may be shared.</summary>
public sealed class ChartController:IDisposable
{
    private IChartModifier? _captured;
    private int _capturePointer;
    private long _cancellationVersion;
    private readonly Dictionary<int,PointD> _touches=[];
    private ViewportSnapshot? _pinchBefore;
    private PointD _pinchCenter;
    private double _pinchDistance;
    public ChartModel Model { get; }
    public ChartFrame? Frame { get; private set; }
    public ChartViewState View { get; }=new();
    public ChartHitTester HitTester { get; }=new();
    public UndoManager History { get; }=new();
    public List<IChartModifier> Modifiers { get; }=[];
    public ChartInteractionMode Mode { get; set; }
    public AnnotationKind AnnotationKind { get; set; }=AnnotationKind.Box;
    public bool InterpolateRollover { get; set; }
    public bool ZoomX { get; set; }=true;
    public bool ZoomY { get; set; }=true;
    public bool HasCapture=>_captured is not null||_pinchBefore is not null;
    /// <summary>Whether this pointer owns the active mouse/pen gesture or participates in the active pinch.</summary>
    public bool IsPointerCaptured(int pointerId)=>_captured is not null&&_capturePointer==pointerId
        ||_pinchBefore is not null&&_touches.ContainsKey(pointerId);
    /// <summary>Native pointer cancellation must not roll back a different pointer's gesture.</summary>
    public void CancelPointer(int pointerId)
    {
        if(IsPointerCaptured(pointerId))Cancel();
        else _touches.Remove(pointerId);
    }
    public event EventHandler? ViewChanged;
    public event EventHandler<ChartAnnotation>? AnnotationEditRequested;
    public ChartController(ChartModel model)
    {
        Model=model;
        Modifiers.AddRange([new LegendModifier(),new TextAnnotationResizeModifier(),new TextAnnotationEditModifier(),new MouseWheelZoomModifier(),new ZoomExtentsModifier(),new AnnotationDragModifier(),new AxisDragModifier(),new RubberBandXyZoomModifier(),new SeriesSelectionModifier(),new ZoomPanModifier()]);
    }
    public void SetFrame(ChartFrame frame) { Frame=frame; HitTester.SetFrame(frame); }
    public bool Process(ChartInput input)
    {
        if(Frame is null||Frame.Layout.PlotArea.IsEmpty) return false;
        if(input.Kind==ChartInputKind.Cancel) { Cancel(); return true; }
        if(input.Kind==ChartInputKind.Key) return Key(input);
        if(input.PointerKind==ChartPointerKind.Touch)
        {
            if(input.Kind==ChartInputKind.Down) _touches[input.PointerId]=input.Position;
            else if(input.Kind==ChartInputKind.Move&&_touches.ContainsKey(input.PointerId)) _touches[input.PointerId]=input.Position;
            if(input.Kind==ChartInputKind.Down&&_touches.Count==2)
            {
                _captured?.Cancel(this); _captured=null;
                var points=_touches.Values.Take(2).ToArray(); _pinchCenter=(points[0]+points[1])*.5;
                _pinchDistance=Math.Max(1,Math.Sqrt((points[0]-points[1]).LengthSquared)); _pinchBefore=new(Model);
            }
            if(_pinchBefore is not null)
            {
                if(input.Kind==ChartInputKind.Move&&_touches.Count>=2)
                {
                    var points=_touches.Values.Take(2).ToArray(); var center=(points[0]+points[1])*.5;
                    double distance=Math.Max(1,Math.Sqrt((points[0]-points[1]).LengthSquared));
                    ZoomSnapshot(_pinchBefore,_pinchDistance/distance,_pinchCenter,center); NotifyViewChanged(); return true;
                }
                if(input.Kind==ChartInputKind.Up)
                {
                    RecordViewport("Pinch zoom",_pinchBefore); _pinchBefore=null; _touches.Remove(input.PointerId); NotifyViewChanged(); return true;
                }
                return true;
            }
            if(input.Kind==ChartInputKind.Up) _touches.Remove(input.PointerId);
        }
        // A captured pointer owns the gesture. Other mouse/pen IDs cannot steal it,
        // dispatch another modifier, or trigger an unrelated rollover query.
        // Two-touch pinch handoff is intentionally processed above this guard.
        if(_captured is not null&&input.PointerId!=_capturePointer)return true;
        // A captured editor supplies its original-point overlay. A redundant rollover
        // would overwrite it and recreate changed source snapshots before rendering.
        if(input.Kind==ChartInputKind.Move && _captured is not IChartCursorOwner { OwnsCursor: true })
        {
            View.Cursor=input.Position;
            View.Hits=HitTester.Query(Model,input.Position,10,nearestX:true,interpolate:InterpolateRollover);
        }
        if(_captured is not null&&input.PointerId==_capturePointer)
        {
            _captured.Handle(this,input);
            if(input.Kind==ChartInputKind.Up) _captured=null;
            NotifyViewChanged(); return true;
        }
        foreach(var modifier in Modifiers.Where(m=>m.IsEnabled))
        {
            long cancellationVersion=_cancellationVersion;
            var result=modifier.Handle(this,input);
            if(result==ModifierResult.Capture)
            {
                // A modifier can publish a start notification before this assignment.
                // Respect Cancel/Dispose called by that notification, including releasing
                // the modifier's not-yet-captured state rather than reviving its gesture.
                if(cancellationVersion!=_cancellationVersion) modifier.Cancel(this);
                else { _captured=modifier; _capturePointer=input.PointerId; }
            }
            if(result!=ModifierResult.Unhandled) { NotifyViewChanged(); return true; }
        }
        if(input.Kind==ChartInputKind.Move) NotifyViewChanged();
        return false;
    }
    public void PointerExited() { if(HasCapture) return; View.Cursor=null; View.Hits=[]; NotifyViewChanged(); }
    public void Cancel()
    {
        _cancellationVersion++;
        _captured?.Cancel(this); _captured=null;
        _pinchBefore?.Restore(); _pinchBefore=null; _touches.Clear(); View.RubberBand=null; NotifyViewChanged();
    }
    public void ZoomExtents()
    {
        var before=new ViewportSnapshot(Model); FreezeAutoRange(); Model.ZoomExtents(); RecordViewport("Zoom extents",before); NotifyViewChanged();
    }
    public void ZoomAt(PointD point,double factor,bool x=true,bool y=true)
    {
        if(!double.IsFinite(factor)||factor<=0||Frame is null) return;
        var before=new ViewportSnapshot(Model);
        ZoomSnapshot(before,factor,point,point,x,y); RecordViewport("Zoom",before); NotifyViewChanged();
    }
    public void PanSnapshot(ViewportSnapshot before,PointD start,PointD current,AxisModel? onlyAxis=null)
    {
        if(Frame is null) return;
        foreach(var pair in before.Ranges)
        {
            var axis=pair.Key; if(onlyAxis is not null&&!ReferenceEquals(axis,onlyAxis)) continue;
            double initial=Fraction(axis,start),now=Fraction(axis,current);
            var range=new DoubleRange(axis.ToScale(pair.Value.Min),axis.ToScale(pair.Value.Max));
            TrySetScaleRange(axis,range.Min+(initial-now)*range.Size,range.Max+(initial-now)*range.Size);
        }
    }
    public void ZoomSnapshot(ViewportSnapshot before,double factor,PointD anchorStart,PointD anchorNow,bool x=true,bool y=true)
    {
        if(Frame is null) return;
        foreach(var pair in before.Ranges)
        {
            var axis=pair.Key; bool isX=Model.XAxes.Contains(axis);
            if(isX&&(!x||!ZoomX)||!isX&&(!y||!ZoomY)) continue;
            double oldMin=axis.ToScale(pair.Value.Min),oldMax=axis.ToScale(pair.Value.Max),size=(oldMax-oldMin)*factor;
            double minimum=oldMin+Fraction(axis,anchorStart)*(oldMax-oldMin)-Fraction(axis,anchorNow)*size;
            TrySetScaleRange(axis,minimum,minimum+size);
        }
    }
    public double Fraction(AxisModel axis,PointD point)
    {
        if(Frame is null) return .5;
        var p=Frame!.Layout.PlotArea;
        double value=axis.IsVertical?(point.Y-p.Y)/p.Height:(point.X-p.X)/p.Width;
        return axis.IsVertical^axis.FlippedCoordinates?1-value:value;
    }
    public void ZoomToRectangle(RectD rectangle)
    {
        if(Frame is null||rectangle.Width<3||rectangle.Height<3) return;
        var before=new ViewportSnapshot(Model);
        foreach(var axis in Model.XAxes.Concat(Model.YAxes))
        {
            if(Model.XAxes.Contains(axis)?!ZoomX:!ZoomY) continue;
            var scaled=axis.ScaleRange; double a=scaled.At(Fraction(axis,new(rectangle.X,rectangle.Y))),b=scaled.At(Fraction(axis,new(rectangle.Right,rectangle.Bottom)));
            TrySetScaleRange(axis,Math.Min(a,b),Math.Max(a,b));
        }
        RecordViewport("Rectangle zoom",before); NotifyViewChanged();
    }
    public static bool TrySetScaleRange(AxisModel axis,double min,double max)
    {
        if(!double.IsFinite(min)||!double.IsFinite(max)||min>=max||!double.IsFinite(max-min)) return false;
        double a=axis.FromScale(min),b=axis.FromScale(max);
        if(!double.IsFinite(a)||!double.IsFinite(b)||a>=b||!double.IsFinite(b-a)||axis.Scale==AxisScale.Logarithmic&&a<=0) return false;
        axis.AutoRange=AutoRange.Never; axis.VisibleRange=new(a,b); return true;
    }
    public void RecordViewport(string description,ViewportSnapshot before)
    {
        var after=new ViewportSnapshot(Model);
        if(!before.Matches(after)) History.RecordExecuted(new DelegateCommand(description,after.Restore,before.Restore));
    }
    public void RequestAnnotationEdit(ChartAnnotation annotation)=>AnnotationEditRequested?.Invoke(this,annotation);
    public void NotifyViewChanged()=>ViewChanged?.Invoke(this,EventArgs.Empty);
    private void FreezeAutoRange() { foreach(var axis in Model.XAxes.Concat(Model.YAxes)) axis.AutoRange=AutoRange.Never; }
    private bool Key(ChartInput input)
    {
        string key=input.Key??"";
        if(key=="Escape") { Cancel(); return true; }
        if(input.Control&&key=="Z") { History.Undo(); NotifyViewChanged(); return true; }
        if(input.Control&&key=="Y") { History.Redo(); NotifyViewChanged(); return true; }
        if(key is "Home" or "0") { ZoomExtents(); return true; }
        if(key is "Add" or "OemPlus" or "+") { ZoomAt(Frame!.Layout.PlotArea.Center,.8); return true; }
        if(key is "Subtract" or "OemMinus" or "-") { ZoomAt(Frame!.Layout.PlotArea.Center,1.25); return true; }
        if(key is "Left" or "Right" or "Up" or "Down")
        {
            var p=Frame!.Layout.PlotArea; var before=new ViewportSnapshot(Model);
            var delta=key switch {"Left"=>new PointD(p.Width*.1,0),"Right"=>new PointD(-p.Width*.1,0),"Up"=>new PointD(0,p.Height*.1),_=>new PointD(0,-p.Height*.1)};
            PanSnapshot(before,p.Center,p.Center+delta); RecordViewport("Keyboard pan",before); NotifyViewChanged(); return true;
        }
        if(key=="Delete")
        {
            var selected=Model.Annotations.Where(a=>a.IsSelected&&a.IsEditable).ToArray();
            if(selected.Length==0) return false;
            History.Execute(new DelegateCommand("Delete annotations",()=> { foreach(var a in selected) Model.Annotations.Remove(a); },()=> { foreach(var a in selected) Model.Annotations.Add(a); }));
            NotifyViewChanged(); return true;
        }
        return false;
    }
    public void Dispose()=>Cancel();
}

public sealed class LegendModifier:ChartModifier
{
    public override ModifierResult Handle(ChartController c,ChartInput input)
    {
        if(input.Kind!=ChartInputKind.Down||c.Frame is null) return ModifierResult.Unhandled;
        var item=c.Frame.LegendItems.FirstOrDefault(l=>l.Bounds.Contains(input.Position));
        if(item is null) return ModifierResult.Unhandled;
        bool old=item.Series.IsVisible;
        c.History.Execute(new DelegateCommand("Toggle series",()=>item.Series.IsVisible=!old,()=>item.Series.IsVisible=old));
        return ModifierResult.Handled;
    }
}
public sealed class MouseWheelZoomModifier:ChartModifier
{
    public double Sensitivity { get; set; }=.0015;
    public override ModifierResult Handle(ChartController c,ChartInput input)
    {
        if(input.Kind!=ChartInputKind.Wheel||c.Frame is null||!c.Frame.Layout.PlotArea.Contains(input.Position)) return ModifierResult.Unhandled;
        c.ZoomAt(input.Position,Math.Exp(Math.Clamp(-input.WheelDelta*Sensitivity,-2,2)),!input.Alt,!input.Control);
        return ModifierResult.Handled;
    }
}
public sealed class ZoomExtentsModifier:ChartModifier
{
    public override ModifierResult Handle(ChartController c,ChartInput input)
    {
        if(input.Kind!=ChartInputKind.DoubleClick) return ModifierResult.Unhandled;
        c.ZoomExtents(); return ModifierResult.Handled;
    }
}
public sealed class ZoomPanModifier:ChartModifier
{
    private PointD _start;
    private ViewportSnapshot? _before;
    public override ModifierResult Handle(ChartController c,ChartInput input)
    {
        if(input.Kind==ChartInputKind.Down)
        {
            if(c.Mode!=ChartInteractionMode.Pan||input.Shift||!c.Frame!.Layout.PlotArea.Contains(input.Position)) return ModifierResult.Unhandled;
            _before=new(c.Model); _start=input.Position; return ModifierResult.Capture;
        }
        if(_before is null) return ModifierResult.Unhandled;
        if(input.Kind==ChartInputKind.Move) c.PanSnapshot(_before,_start,input.Position);
        if(input.Kind==ChartInputKind.Up) { c.RecordViewport("Pan",_before); _before=null; }
        return ModifierResult.Handled;
    }
    public override void Cancel(ChartController c) { _before?.Restore(); _before=null; }
}
public sealed class AxisDragModifier:ChartModifier
{
    private AxisModel? _axis;
    private ViewportSnapshot? _before;
    private PointD _start;
    private bool _pan;
    public override ModifierResult Handle(ChartController c,ChartInput input)
    {
        if(input.Kind==ChartInputKind.Down)
        {
            _axis=c.Frame!.Layout.Axes.FirstOrDefault(a=>a.Bounds.Contains(input.Position))?.Axis;
            if(_axis is null) return ModifierResult.Unhandled;
            _before=new(c.Model); _start=input.Position; _pan=input.Control; return ModifierResult.Capture;
        }
        if(_axis is null||_before is null) return ModifierResult.Unhandled;
        if(input.Kind==ChartInputKind.Move)
        {
            if(_pan) c.PanSnapshot(_before,_start,input.Position,_axis);
            else
            {
                var r=_before.Ranges[_axis]; var scale=new DoubleRange(_axis.ToScale(r.Min),_axis.ToScale(r.Max));
                double delta=_axis.IsVertical?input.Position.Y-_start.Y:input.Position.X-_start.X;
                var zoomed=scale.Zoom(Math.Exp(Math.Clamp(delta/150,-10,10)),c.Fraction(_axis,_start));
                ChartController.TrySetScaleRange(_axis,zoomed.Min,zoomed.Max);
            }
        }
        if(input.Kind==ChartInputKind.Up) { c.RecordViewport(_pan?"Axis pan":"Axis scale",_before); _before=null; _axis=null; }
        return ModifierResult.Handled;
    }
    public override void Cancel(ChartController c) { _before?.Restore(); _before=null; _axis=null; }
}
public sealed class RubberBandXyZoomModifier:ChartModifier
{
    private PointD? _start;
    public override ModifierResult Handle(ChartController c,ChartInput input)
    {
        if(input.Kind==ChartInputKind.Down)
        {
            if(c.Mode!=ChartInteractionMode.ZoomRectangle&&!input.Shift||!c.Frame!.Layout.PlotArea.Contains(input.Position)) return ModifierResult.Unhandled;
            _start=input.Position; c.View.RubberBand=new(input.Position.X,input.Position.Y,0,0); return ModifierResult.Capture;
        }
        if(_start is not { } start) return ModifierResult.Unhandled;
        var plot=c.Frame!.Layout.PlotArea; var current=new PointD(Math.Clamp(input.Position.X,plot.X,plot.Right),Math.Clamp(input.Position.Y,plot.Y,plot.Bottom));
        if(input.Kind==ChartInputKind.Move) c.View.RubberBand=RectD.FromPoints(start,current);
        if(input.Kind==ChartInputKind.Up) { c.ZoomToRectangle(RectD.FromPoints(start,current)); c.View.RubberBand=null; _start=null; }
        return ModifierResult.Handled;
    }
    public override void Cancel(ChartController c) { _start=null; c.View.RubberBand=null; }
}
public sealed class SeriesSelectionModifier:ChartModifier
{
    private PointD? _start;
    private bool _add;
    public override ModifierResult Handle(ChartController c,ChartInput input)
    {
        if(input.Kind==ChartInputKind.Down)
        {
            if(c.Mode!=ChartInteractionMode.Select||!c.Frame!.Layout.PlotArea.Contains(input.Position)) return ModifierResult.Unhandled;
            _start=input.Position; _add=input.Control; return ModifierResult.Capture;
        }
        if(_start is not { } start) return ModifierResult.Unhandled;
        if(input.Kind==ChartInputKind.Move) c.View.RubberBand=RectD.FromPoints(start,input.Position);
        if(input.Kind==ChartInputKind.Up)
        {
            var before=c.Model.RenderableSeries.ToDictionary(s=>s,s=>s.SelectedIndices.ToArray());
            if(!_add) foreach(var series in c.Model.RenderableSeries) { series.SelectedIndices.Clear(); series.IsSelected=false; }
            var rect=RectD.FromPoints(start,input.Position);
            if(rect.Width<4&&rect.Height<4)
            {
                var hit=c.HitTester.Query(c.Model,input.Position,12).FirstOrDefault();
                if(hit is not null) { if(_add&&!hit.Series.SelectedIndices.Add(hit.DataIndex)) hit.Series.SelectedIndices.Remove(hit.DataIndex); else hit.Series.SelectedIndices.Add(hit.DataIndex); hit.Series.IsSelected=hit.Series.SelectedIndices.Count>0; }
            }
            else
            {
                foreach(var series in c.Model.RenderableSeries.Where(s=>s.IsVisible&&s.DataSeries is not null))
                {
                    var data=series.DataSeries!.Snapshot(); var p=c.Frame!.Layout.Projection(c.Model,series);
                    for(int i=0;i<data.Count;i++) if(rect.Contains(p.ToScreen(data[0,i],data[series.YChannel,i],data.ChannelCount>2?data[2,i]:0))) series.SelectedIndices.Add(i);
                    series.IsSelected=series.SelectedIndices.Count>0;
                }
            }
            var after=c.Model.RenderableSeries.ToDictionary(s=>s,s=>s.SelectedIndices.ToArray());
            void Restore(Dictionary<RenderableSeries,int[]> values) { foreach(var pair in values) { pair.Key.SelectedIndices.Clear(); pair.Key.SelectedIndices.UnionWith(pair.Value); pair.Key.IsSelected=pair.Value.Length>0; pair.Key.Invalidate(); } }
            c.History.RecordExecuted(new DelegateCommand("Select data points",()=>Restore(after),()=>Restore(before)));
            c.Model.Invalidate(); c.View.RubberBand=null; _start=null;
        }
        return ModifierResult.Handled;
    }
    public override void Cancel(ChartController c) { _start=null; c.View.RubberBand=null; }
}
public sealed class AnnotationDragModifier:ChartModifier
{
    private sealed record State(double X1,double Y1,double X2,double Y2,PointD[] Points)
    {
        public static State Capture(ChartAnnotation a)=>new(a.X1,a.Y1,a.X2,a.Y2,a.Points.ToArray());
        public void Restore(ChartAnnotation a) { a.X1=X1; a.Y1=Y1; a.X2=X2; a.Y2=Y2; a.Points.Clear(); foreach(var point in Points) a.Points.Add(point); }
    }
    private ChartAnnotation? _annotation;
    private State? _before;
    private PointD _startValue;
    private int _handle;
    private bool _created;
    public override ModifierResult Handle(ChartController c,ChartInput input)
    {
        var plot=c.Frame!.Layout.PlotArea;
        if(input.Kind==ChartInputKind.Down)
        {
            bool withinPlot=plot.Contains(input.Position);
            _annotation=c.Model.Annotations.Reverse().FirstOrDefault(a=>a.IsVisible&&a.IsEditable&&(withinPlot||a.Kind==AnnotationKind.Text)&&AnnotationGeometry.HitDistance(c.Model,a,plot,input.Position,c.Frame)<=8);
            // Unclipped text can be painted in the surface's axis/header margins.
            // Its measured body wins over axis navigation there, while creation and
            // other annotation gestures remain confined to the plot.
            if(_annotation is null&&!withinPlot)return ModifierResult.Unhandled;
            _created=false;
            if(_annotation is null&&c.Mode==ChartInteractionMode.DrawAnnotation)
            {
                _annotation=new(c.AnnotationKind){Stroke=c.Model.Theme.Accent,Fill=c.Model.Theme.Accent.WithAlpha(35),Text=c.AnnotationKind==AnnotationKind.Text?"Annotation":""};
                var value=AnnotationGeometry.ToValues(c.Model,_annotation,plot,input.Position); _annotation.X1=_annotation.X2=value.X; _annotation.Y1=_annotation.Y2=value.Y;
                c.Model.Annotations.Add(_annotation); _created=true;
            }
            if(_annotation is null) return ModifierResult.Unhandled;
            foreach(var a in c.Model.Annotations) a.IsSelected=ReferenceEquals(a,_annotation);
            _before=State.Capture(_annotation); _startValue=AnnotationGeometry.ToValues(c.Model,_annotation,plot,input.Position);
            var(a1,a2)=AnnotationGeometry.Endpoints(c.Model,_annotation,plot);
            _handle=_annotation.Kind==AnnotationKind.Text?0:_created?2:(input.Position-a1).LengthSquared<64?1:(input.Position-a2).LengthSquared<64?2:0;
            return ModifierResult.Capture;
        }
        if(_annotation is null||_before is null) return ModifierResult.Unhandled;
        if(input.Kind==ChartInputKind.Move)
        {
            var current=AnnotationGeometry.ToValues(c.Model,_annotation,plot,input.Position); var delta=current-_startValue;
            if(_annotation.Kind==AnnotationKind.HorizontalLine) delta=new(0,delta.Y);
            if(_annotation.Kind==AnnotationKind.VerticalLine) delta=new(delta.X,0);
            if(_handle!=2) { _annotation.X1=_before.X1+delta.X; _annotation.Y1=_before.Y1+delta.Y; }
            if(_handle!=1) { _annotation.X2=_before.X2+delta.X; _annotation.Y2=_before.Y2+delta.Y; }
            if(_handle==0&&_before.Points.Length>0) { _annotation.Points.Clear(); foreach(var point in _before.Points) _annotation.Points.Add(point+delta); }
        }
        if(input.Kind==ChartInputKind.Up)
        {
            var annotation=_annotation; var before=_before; var after=State.Capture(annotation);
            if(!_created&&annotation.Kind==AnnotationKind.Text&&before.X1==after.X1&&before.Y1==after.Y1)
            { _annotation=null; _before=null; return ModifierResult.Handled; }
            if(_created) c.History.RecordExecuted(new DelegateCommand("Create annotation",()=>c.Model.Annotations.Add(annotation),()=>c.Model.Annotations.Remove(annotation)));
            else c.History.RecordExecuted(new DelegateCommand("Edit annotation",()=>after.Restore(annotation),()=>before.Restore(annotation)));
            if(_created&&annotation.Kind==AnnotationKind.Text) c.RequestAnnotationEdit(annotation);
            _annotation=null; _before=null;
        }
        return ModifierResult.Handled;
    }
    public override void Cancel(ChartController c)
    {
        if(_annotation is not null) { if(_created) c.Model.Annotations.Remove(_annotation); else _before?.Restore(_annotation); }
        _annotation=null; _before=null;
    }
}
