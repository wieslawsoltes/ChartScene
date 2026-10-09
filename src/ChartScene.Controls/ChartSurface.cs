using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Interaction;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Microsoft.UI.Input;
using Windows.Foundation;
using Windows.System;

namespace ChartScene.Controls;

/// <summary>Focusable Uno control which renders directly into Uno's Skia canvas and delegates input to reusable modifiers.</summary>
public class ChartSurface:UserControl,IDisposable
{
    private sealed class PlotCanvas(ChartSurface owner):SKCanvasElement
    {
        protected override void RenderOverride(SKCanvas canvas,Size area)=>owner.Render(canvas,area);
        public void RequestRender()=>Invalidate();
    }
    private readonly PlotCanvas _canvas;
    private readonly ChartRenderer _renderer=new();
    private SkiaResources? _resources;
    private ChartController? _controller;
    private int _queued;
    private bool _attached,_disposed;
    private readonly HashSet<VirtualKey> _keys=[];
    public static readonly DependencyProperty ModelProperty=DependencyProperty.Register(nameof(Model),typeof(ChartModel),typeof(ChartSurface),new PropertyMetadata(null,(d,e)=>((ChartSurface)d).ChangeModel((ChartModel?)e.OldValue,(ChartModel?)e.NewValue)));
    public ChartModel Model { get=>(ChartModel)GetValue(ModelProperty); set=>SetValue(ModelProperty,value); }
    public ChartController Controller=>_controller??throw new InvalidOperationException("Assign a model before interacting with the chart.");
    public ChartFrame? LastFrame { get; private set; }
    public long FrameCount { get; private set; }
    public string? LastError { get; private set; }
    public string RendererDescription=>"Uno Skia host (GPU availability is host-dependent)";
    public event EventHandler<ChartFrame>? RenderCompleted;
    public ChartSurface()
    {
        IsTabStop=true; HorizontalContentAlignment=HorizontalAlignment.Stretch; VerticalContentAlignment=VerticalAlignment.Stretch;
        _canvas=new(this){IsHitTestVisible=false};
        Content=new Grid{Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),Children={_canvas}};
        AutomationProperties.SetName(this,"ChartScene chart");
        AutomationProperties.SetHelpText(this,"Drag to pan. Shift-drag to zoom a rectangle. Use the wheel to zoom. Home fits the data. Control-Z and Control-Y undo and redo.");
        Loaded+=OnLoaded; Unloaded+=OnUnloaded;
        PointerPressed+=OnPointerPressed; PointerMoved+=OnPointerMoved; PointerReleased+=OnPointerReleased;
        PointerWheelChanged+=OnPointerWheel; PointerCanceled+=OnPointerCanceled; PointerCaptureLost+=OnPointerCanceled;
        PointerExited+=(_,_)=>_controller?.PointerExited();
        DoubleTapped+=(_,e)=> { _controller?.Process(new(ChartInputKind.DoubleClick,Point(e.GetPosition(this)))); e.Handled=true; };
        KeyDown+=OnKeyDown; KeyUp+=(_,e)=>_keys.Remove(e.Key); LostFocus+=(_,_)=>_keys.Clear();
        Model=new ChartModel();
    }
    private void ChangeModel(ChartModel? previous,ChartModel? next)
    {
        if(previous is not null&&_attached) previous.Changed-=OnModelChanged;
        _attached=false;
        if(_controller is not null) { _controller.ViewChanged-=OnViewChanged; _controller.Dispose(); }
        _controller=next is null?null:new(next);
        if(_controller is not null) _controller.ViewChanged+=OnViewChanged;
        LastFrame=null; LastError=null;
        if(IsLoaded&&next is not null) Attach();
        RequestRender();
    }
    private void Attach()
    {
        if(_attached||_disposed||Model is null) return;
        Model.Changed+=OnModelChanged; _attached=true;
        AutomationProperties.SetName(this,"Chart: "+Model.Title);
    }
    private void OnLoaded(object sender,RoutedEventArgs e) { Attach(); RequestRender(); }
    private void OnUnloaded(object sender,RoutedEventArgs e)
    {
        if(_attached&&Model is not null) Model.Changed-=OnModelChanged;
        _attached=false; _controller?.Cancel(); _resources?.Dispose(); _resources=null; Interlocked.Exchange(ref _queued,0);
    }
    private void OnModelChanged(object? sender,EventArgs e)=>RequestRender();
    private void OnViewChanged(object? sender,EventArgs e)=>RequestRender();
    public void RequestRender()
    {
        if(_disposed||!IsLoaded||Interlocked.Exchange(ref _queued,1)!=0) return;
        if(!DispatcherQueue.TryEnqueue(()=> { if(!_disposed&&IsLoaded) _canvas.RequestRender(); else Interlocked.Exchange(ref _queued,0); })) Interlocked.Exchange(ref _queued,0);
    }
    private void Render(SKCanvas canvas,Size area)
    {
        Interlocked.Exchange(ref _queued,0);
        if(_disposed||Model is null||_controller is null) return;
        _resources??=new(); _resources.BeginFrame();
        try
        {
            LastFrame=_renderer.Render(new SkiaChartCanvas(canvas,_resources),Model,area.Width,area.Height,_controller.View);
            _controller.SetFrame(LastFrame); FrameCount++; LastError=LastFrame.Diagnostics.Count==0?null:string.Join("\n",LastFrame.Diagnostics);
            RenderCompleted?.Invoke(this,LastFrame);
        }
        catch(Exception error)
        {
            LastError=error.ToString();
            canvas.Clear(new SKColor(23,28,40));
            var adapter=new SkiaChartCanvas(canvas,_resources); adapter.DrawText("Chart rendering failed",new(20,32),new(new(235,135,135),16)); adapter.DrawText(error.Message,new(20,56),new(new(220,228,239),12));
        }
    }
    private static PointD Point(Point point)=>new(point.X,point.Y);
    private ChartInput Input(ChartInputKind kind,PointerRoutedEventArgs e,double wheel=0)
    {
        var point=e.GetCurrentPoint(this);
        var device=e.Pointer.PointerDeviceType switch {PointerDeviceType.Touch=>ChartPointerKind.Touch,PointerDeviceType.Pen=>ChartPointerKind.Pen,_=>ChartPointerKind.Mouse};
        return new(kind,Point(point.Position),unchecked((int)e.Pointer.PointerId),device,e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift),e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control),e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu),wheel);
    }
    private void OnPointerPressed(object sender,PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        bool handled=_controller?.Process(Input(ChartInputKind.Down,e))??false;
        if(_controller?.HasCapture==true) CapturePointer(e.Pointer);
        e.Handled=handled;
    }
    private void OnPointerMoved(object sender,PointerRoutedEventArgs e) { bool captured=_controller?.HasCapture==true; _controller?.Process(Input(ChartInputKind.Move,e)); if(captured) e.Handled=true; }
    private void OnPointerReleased(object sender,PointerRoutedEventArgs e)
    {
        bool captured=_controller?.HasCapture==true; _controller?.Process(Input(ChartInputKind.Up,e)); ReleasePointerCapture(e.Pointer); if(captured) e.Handled=true;
    }
    private void OnPointerWheel(object sender,PointerRoutedEventArgs e)=>e.Handled=_controller?.Process(Input(ChartInputKind.Wheel,e,e.GetCurrentPoint(this).Properties.MouseWheelDelta))??false;
    private void OnPointerCanceled(object sender,PointerRoutedEventArgs e)=>_controller?.Cancel();
    private void OnKeyDown(object sender,KeyRoutedEventArgs e)
    {
        _keys.Add(e.Key);
        bool control=_keys.Contains(VirtualKey.Control)||_keys.Contains(VirtualKey.LeftControl)||_keys.Contains(VirtualKey.RightControl);
        bool shift=_keys.Contains(VirtualKey.Shift)||_keys.Contains(VirtualKey.LeftShift)||_keys.Contains(VirtualKey.RightShift);
        e.Handled=_controller?.Process(new(ChartInputKind.Key,LastFrame?.Layout.PlotArea.Center??default,Shift:shift,Control:control,Key:e.Key.ToString()))??false;
    }
    protected override AutomationPeer OnCreateAutomationPeer()=>new ChartSurfacePeer(this);
    private sealed class ChartSurfacePeer(ChartSurface owner):FrameworkElementAutomationPeer(owner),IValueProvider
    {
        protected override string GetClassNameCore()=>nameof(ChartSurface);
        protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.Custom;
        protected override string GetNameCore()=>owner.Model?.Title??"Chart";
        protected override object GetPatternCore(PatternInterface pattern)=>pattern==PatternInterface.Value?this:base.GetPatternCore(pattern)!;
        public bool IsReadOnly=>true;
        public string Value=>owner.Model is { } m?$"{m.Title}; {m.RenderableSeries.Count(s=>s.IsVisible)} visible series; X {m.XAxis.VisibleRange}; Y {m.YAxis.VisibleRange}":"No chart";
        public void SetValue(string value)=>throw new InvalidOperationException("Use the chart's keyboard navigation to change the viewport.");
    }
    public new void Dispose()
    {
        if(_disposed) return; _disposed=true;
        if(_attached&&Model is not null) Model.Changed-=OnModelChanged;
        _attached=false;
        if(_controller is not null) { _controller.ViewChanged-=OnViewChanged; _controller.Dispose(); }
        _resources?.Dispose(); _resources=null;
        base.Dispose();
    }
}

/// <summary>Familiar surface naming for source migration; this is an Uno control, not a binary-compatible WPF type.</summary>
public sealed class SciChartSurface:ChartSurface;
