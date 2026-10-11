using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Interaction;
using ChartScene.Rendering.OpenGL;
using ChartScene.ThreeD;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Silk.NET.OpenGL;
using Uno.WinUI.Graphics3DGL;
using Windows.Foundation;
using Windows.System;

namespace ChartScene.Controls;

/// <summary>Reusable GPU 3D surface; data, camera, mesh generation, picking and renderer are independent packages.</summary>
public sealed class ChartSurface3D : UserControl, IDisposable
{
    private sealed class GpuCanvas(ChartSurface3D owner) : GLCanvasElement(() => owner.WindowProvider?.Invoke()!)
    {
        private OpenGlChartRenderer? _renderer;
        protected override void Init(GL gl)
        {
            try { BrowserGlReadback.Configure(this); _renderer = new(gl); owner.SetError(null); }
            catch (Exception error) { owner.SetError(error.ToString()); }
        }
        protected override unsafe void RenderOverride(GL gl)
        {
            Interlocked.Exchange(ref owner._queued, 0);
            if (_renderer is null || owner._disposed || owner.Model is null) return;
            try
            {
                var scene = owner._builder.Build(owner.Model);
                if (scene.Diagnostics.Count > 0) throw new InvalidOperationException(string.Join("\n", scene.Diagnostics));
                int* viewport = stackalloc int[4]; gl.GetInteger(GLEnum.Viewport, viewport);
                if (viewport[2] < 1 || viewport[3] < 1) return;
                var stats = _renderer.Render(scene, owner.Model, viewport[2], viewport[3], ActualWidth > 0 ? viewport[2] / ActualWidth : 1);
                owner.Scene = scene; owner._hitTester.SetScene(scene); owner.LastStatistics = stats; owner.FrameCount++;
                owner.SetError(null); owner.DispatcherQueue.TryEnqueue(owner.UpdateOverlay);
            }
            catch (Exception error) { owner.SetError(error.ToString()); }
        }
        protected override void OnDestroy(GL gl) { _renderer?.Dispose(); _renderer = null; }
        public void RequestRender() => Invalidate();
    }
    private sealed record State(double Yaw, double Pitch, double Distance, double Width, CameraProjection Projection, Vector3D Target, DoubleRange X, DoubleRange Y, DoubleRange Z)
    {
        public static State Capture(ChartModel3D m) => new(m.Camera.Yaw, m.Camera.Pitch, m.Camera.Distance, m.Camera.OrthographicWidth, m.Camera.Projection, m.Camera.Target, m.XAxis.VisibleRange, m.YAxis.VisibleRange, m.ZAxis.VisibleRange);
        public void Restore(ChartModel3D m)
        {
            m.Camera.Yaw = Yaw; m.Camera.Pitch = Pitch; m.Camera.Distance = Distance; m.Camera.OrthographicWidth = Width; m.Camera.Projection = Projection; m.Camera.Target = Target;
            foreach (var a in m.Axes) a.AutoRange = AutoRange.Never;
            m.XAxis.VisibleRange = X; m.YAxis.VisibleRange = Y; m.ZAxis.VisibleRange = Z;
        }
    }
    private readonly GpuCanvas _canvas;
    private readonly Canvas _labels = new() { IsHitTestVisible = false };
    private readonly List<TextBlock> _labelPool = [];
    private readonly TextBlock _title = new() { FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(16, 12, 16, 0), IsHitTestVisible = false };
    private readonly TextBlock _subtitle = new() { FontSize = 11, Margin = new Thickness(16, 38, 16, 0), IsHitTestVisible = false };
    private readonly TextBlock _tooltip = new() { FontSize = 12, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly StackPanel _legend = new() { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 65, 16, 0) };
    private readonly SceneBuilder3D _builder = new();
    private readonly SceneHitTester3D _hitTester = new();
    private readonly Dictionary<uint, PointD> _touches = [];
    private State? _gesture;
    private PointD _start, _previous;
    private double _pinchDistance;
    private PointD _pinchCenter;
    private bool _pan, _attached, _disposed, _dragged;
    private uint? _pointer;
    private int _queued;
    private readonly HashSet<VirtualKey> _keys = [];
    public Func<Window>? WindowProvider { get; set; }
    public UndoManager History { get; } = new();
    public Scene3D? Scene { get; private set; }
    public SeriesHit3D? HoveredPoint { get; private set; }
    public GpuFrameStatistics? LastStatistics { get; private set; }
    public string? LastError { get; private set; }
    public long FrameCount { get; private set; }
    public event EventHandler? RenderCompleted;
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(ChartModel3D), typeof(ChartSurface3D), new PropertyMetadata(null, (d, e) => ((ChartSurface3D)d).ChangeModel((ChartModel3D?)e.OldValue, (ChartModel3D?)e.NewValue)));
    public ChartModel3D Model { get => (ChartModel3D)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public ChartSurface3D()
    {
        IsTabStop = true; HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        _canvas = new(this) { IsHitTestVisible = false };
        var root = new Grid { Background = Brush(ChartTheme.Dark.PlotBackground) };
        root.Children.Add(new ArrangedGlHost(_canvas)); root.Children.Add(_labels); root.Children.Add(_title); root.Children.Add(_subtitle); root.Children.Add(_tooltip); root.Children.Add(_legend); root.Children.Add(_error);
        Content = root;
        AutomationProperties.SetName(this, "3D chart");
        AutomationProperties.SetHelpText(this, "Drag to orbit, Control-drag to pan, mouse wheel to zoom, click data to select. Home fits the chart. Arrow keys orbit. Control-Z undoes navigation.");
        Loaded += (_, _) => { Attach(); RequestRender(); };
        Unloaded += (_, _) => { Detach(); CancelGesture(); Interlocked.Exchange(ref _queued, 0); };
        SizeChanged += (_, _) => { RequestRender(); UpdateOverlay(); };
        _canvas.RegisterPropertyChangedCallback(GLCanvasElement.IsGLInitializedProperty, (_, _) =>
        {
            if (_canvas.IsLoaded && _canvas.IsGLInitialized == false) SetError("OpenGL / WebGL could not initialize. A WebGL 2 or OpenGL 3.3 compatible host is required.");
        });
        PointerPressed += Down; PointerMoved += Move; PointerReleased += Up;
        PointerCanceled += (_, _) => CancelGesture(); PointerCaptureLost += (_, _) => { if (_gesture is not null) CancelGesture(); };
        PointerExited += (_, _) => { if (_gesture is null) { HoveredPoint = null; _tooltip.Text = ""; } };
        PointerWheelChanged += (_, e) => { var before = State.Capture(Model); Model.Camera.Zoom(Math.Exp(Math.Clamp(-e.GetCurrentPoint(this).Properties.MouseWheelDelta * .0015, -2, 2))); Record("3D zoom", before); e.Handled = true; };
        DoubleTapped += (_, e) => { ZoomExtents(); e.Handled = true; };
        KeyDown += Key; KeyUp += (_, e) => _keys.Remove(e.Key); LostFocus += (_, _) => _keys.Clear();
        Model = new();
    }
    private void ChangeModel(ChartModel3D? previous, ChartModel3D? next)
    {
        if (_attached && previous is not null) previous.Changed -= Changed;
        _attached = false; _gesture = null; _touches.Clear(); _pointer = null; _builder.Clear(); Scene = null; LastStatistics = null; HoveredPoint = null; History.Clear();
        _legend.Children.Clear();
        if (IsLoaded) Attach();
        UpdateOverlay(); RequestRender();
    }
    private void Attach() { if (!_attached && !_disposed && Model is not null) { Model.Changed += Changed; _attached = true; } }
    private void Detach() { if (_attached && Model is not null) Model.Changed -= Changed; _attached = false; }
    private void Changed(object? sender, EventArgs e) => RequestRender();
    public void RequestRender()
    {
        if (_disposed || !IsLoaded || Interlocked.Exchange(ref _queued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() => { if (IsLoaded && !_disposed) _canvas.RequestRender(); else Interlocked.Exchange(ref _queued, 0); })) Interlocked.Exchange(ref _queued, 0);
    }
    private void SetError(string? message)
    {
        LastError = message;
        DispatcherQueue.TryEnqueue(() => { _error.Text = message ?? ""; _error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible; _error.Foreground = Brush(new(255, 160, 160)); if (message is not null) RenderCompleted?.Invoke(this, EventArgs.Empty); });
    }
    public void ZoomExtents() { var before = State.Capture(Model); Model.ZoomExtents(); Record("3D zoom extents", before); }
    public void SetProjection(CameraProjection projection) { var before = State.Capture(Model); Model.Camera.Projection = projection; Record("3D projection", before); }
    public void Undo() { History.Undo(); RequestRender(); }
    public void Redo() { History.Redo(); RequestRender(); }
    private void Record(string description, State before)
    {
        var after = State.Capture(Model); if (after == before) return;
        var model = Model; History.RecordExecuted(new DelegateCommand(description, () => after.Restore(model), () => before.Restore(model)));
    }
    private static PointD Position(PointerRoutedEventArgs e, UIElement relative) { var p = e.GetCurrentPoint(relative).Position; return new(p.X, p.Y); }
    private void Down(object sender, PointerRoutedEventArgs e)
    {
        if (Model is null || LastError is not null) return;
        var p = Position(e, this); Focus(FocusState.Pointer);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            _touches[e.Pointer.PointerId] = p;
            if (_touches.Count == 2)
            {
                if (_gesture is not null) _gesture.Restore(Model);
                _gesture = State.Capture(Model); var points = _touches.Values.Take(2).ToArray(); _pinchDistance = Math.Max(1, Math.Sqrt((points[0] - points[1]).LengthSquared)); _pinchCenter = (points[0] + points[1]) * .5;
                _dragged = true; CapturePointer(e.Pointer); e.Handled = true; return;
            }
        }
        if (_pointer is not null) return;
        _pointer = e.Pointer.PointerId; _gesture = State.Capture(Model); _start = _previous = p; _dragged = false;
        _pan = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
        CapturePointer(e.Pointer); e.Handled = true;
    }
    private void Move(object sender, PointerRoutedEventArgs e)
    {
        if (Model is null) return;
        var p = Position(e, this);
        if (_touches.ContainsKey(e.Pointer.PointerId)) _touches[e.Pointer.PointerId] = p;
        if (_touches.Count >= 2 && _gesture is not null)
        {
            var points = _touches.Values.Take(2).ToArray(); double distance = Math.Max(1, Math.Sqrt((points[0] - points[1]).LengthSquared)); var center = (points[0] + points[1]) * .5;
            _gesture.Restore(Model); Model.Camera.Zoom(_pinchDistance / distance); Model.Camera.Pan(center.X - _pinchCenter.X, center.Y - _pinchCenter.Y, ActualWidth, ActualHeight); e.Handled = true; return;
        }
        if (_pointer == e.Pointer.PointerId && _gesture is not null)
        {
            var delta = p - _previous; _previous = p; _dragged |= (p - _start).LengthSquared > 16;
            if (_pan) Model.Camera.Pan(delta.X, delta.Y, ActualWidth, ActualHeight); else Model.Camera.Orbit(delta.X, delta.Y);
            e.Handled = true; return;
        }
        if (Scene is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        HoveredPoint = _hitTester.HitTest(Model, p, new(0, 0, ActualWidth, ActualHeight));
        _tooltip.Text = HoveredPoint is { } hit ? $"{hit.Series.SeriesName}   #{hit.DataIndex}   X {Model.XAxis.Format(hit.DataPosition.X, true)}   Y {Model.YAxis.Format(hit.DataPosition.Y, true)}   Z {Model.ZAxis.Format(hit.DataPosition.Z, true)}" : "";
    }
    private void Up(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture is null) { _touches.Remove(e.Pointer.PointerId); return; }
        var before = _gesture; bool pinch = _touches.Count >= 2; _touches.Remove(e.Pointer.PointerId);
        if (_pointer != e.Pointer.PointerId && !pinch) return;
        _gesture = null; _pointer = null; _touches.Clear(); ReleasePointerCaptures();
        Record(pinch ? "3D pinch" : _pan ? "3D pan" : "3D orbit", before);
        if (!_dragged)
        {
            var p = Position(e, this); var hit = _hitTester.HitTest(Model, p, new(0, 0, ActualWidth, ActualHeight));
            var old = Model.RenderableSeries.ToDictionary(s => s, s => s.SelectedIndices.ToArray());
            if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) foreach (var series in Model.RenderableSeries) series.SelectedIndices.Clear();
            if (hit is not null && !hit.Series.SelectedIndices.Add(hit.DataIndex)) hit.Series.SelectedIndices.Remove(hit.DataIndex);
            var next = Model.RenderableSeries.ToDictionary(s => s, s => s.SelectedIndices.ToArray());
            void Restore(Dictionary<RenderableSeries3D, int[]> value) { foreach (var item in value) { item.Key.SelectedIndices.Clear(); item.Key.SelectedIndices.UnionWith(item.Value); item.Key.Invalidate(); } }
            History.RecordExecuted(new DelegateCommand("3D selection", () => Restore(next), () => Restore(old)));
            Model.InvalidateGeometry();
        }
        e.Handled = true;
    }
    private void CancelGesture() { var before = _gesture; _gesture = null; _pointer = null; _touches.Clear(); if (before is not null && Model is not null) before.Restore(Model); }
    private void Key(object sender, KeyRoutedEventArgs e)
    {
        _keys.Add(e.Key); bool control = _keys.Contains(VirtualKey.Control) || _keys.Contains(VirtualKey.LeftControl) || _keys.Contains(VirtualKey.RightControl);
        if (control && e.Key == VirtualKey.Z) Undo();
        else if (control && e.Key == VirtualKey.Y) Redo();
        else if (e.Key == VirtualKey.Escape) CancelGesture();
        else if (e.Key == VirtualKey.Home) ZoomExtents();
        else if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        { var before = State.Capture(Model); Model.Camera.Orbit(e.Key == VirtualKey.Left ? -15 : e.Key == VirtualKey.Right ? 15 : 0, e.Key == VirtualKey.Up ? -15 : e.Key == VirtualKey.Down ? 15 : 0); Record("3D keyboard orbit", before); }
        else return;
        e.Handled = true;
    }
    private static SolidColorBrush Brush(ColorRgba color) => new(Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B));
    private void UpdateOverlay()
    {
        if (_disposed || Model is null || ActualWidth < 1 || ActualHeight < 1) return;
        _title.Text = Model.Title; _title.Foreground = Brush(Model.Theme.Foreground); _subtitle.Text = Model.Subtitle; _subtitle.Foreground = Brush(Model.Theme.Muted); _tooltip.Foreground = Brush(Model.Theme.Foreground);
        int used = 0; var viewport = new RectD(0, 0, ActualWidth, ActualHeight); var half = Model.WorldSize * .5;
        void Label(string text, Vector3D position, bool title = false)
        {
            var p = Model.Camera.Project(position, viewport); if (!p.IsVisible) return;
            while (_labelPool.Count <= used) { var created = new TextBlock { IsHitTestVisible = false }; _labelPool.Add(created); _labels.Children.Add(created); }
            var label = _labelPool[used++]; label.Text = text; label.FontSize = title ? 12 : 10; label.Foreground = Brush(title ? Model.Theme.Foreground : Model.Theme.Muted); label.Visibility = Visibility.Visible;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Clamp(p.Pixel.X - label.DesiredSize.Width / 2, 0, Math.Max(0, ActualWidth - label.DesiredSize.Width)));
            Canvas.SetTop(label, Math.Clamp(p.Pixel.Y + 4, 55, Math.Max(55, ActualHeight - label.DesiredSize.Height)));
        }
        double frontZ = Model.Camera.Position.Z >= 0 ? half.Z : -half.Z, sideX = Model.Camera.Position.X >= 0 ? -half.X : half.X;
        foreach (var axis in Model.Axes)
        {
            int index = ReferenceEquals(axis, Model.XAxis) ? 0 : ReferenceEquals(axis, Model.YAxis) ? 1 : 2;
            foreach (var tick in TickGenerator.Generate(axis, 420).Where(t => t.IsMajor))
            {
                double f = axis.ScaleRange.Fraction(axis.ToScale(tick.Value)); if (axis.FlippedCoordinates) f = 1 - f;
                var p = index switch { 0 => new Vector3D((f - .5) * Model.WorldSize.X, -half.Y, frontZ), 1 => new Vector3D(sideX, (f - .5) * Model.WorldSize.Y, frontZ), _ => new Vector3D(sideX, -half.Y, (f - .5) * Model.WorldSize.Z) };
                Label(tick.Label, p);
            }
            Label(axis.AxisTitle, index switch { 0 => new(0, -half.Y - .12, frontZ), 1 => new(sideX, half.Y + .08, frontZ), _ => new(sideX - .12, -half.Y, 0) }, true);
        }
        for (int i = used; i < _labelPool.Count; i++) _labelPool[i].Visibility = Visibility.Collapsed;
        if (_legend.Children.Count != Model.RenderableSeries.Count || !_legend.Children.OfType<CheckBox>().Select(c => c.Tag).SequenceEqual(Model.RenderableSeries.Cast<object>()))
        {
            _legend.Children.Clear();
            foreach (var series in Model.RenderableSeries)
            {
                var check = new CheckBox { Tag = series, Content = series.SeriesName, IsChecked = series.IsVisible, MinHeight = 24, FontSize = 11 };
                check.Checked += (_, _) => series.IsVisible = true; check.Unchecked += (_, _) => series.IsVisible = false; _legend.Children.Add(check);
            }
        }
        foreach (var check in _legend.Children.OfType<CheckBox>()) { var series = (RenderableSeries3D)check.Tag; check.IsChecked = series.IsVisible; check.Foreground = Brush(Model.Theme.Foreground); }
        _legend.Visibility = Model.ShowLegend ? Visibility.Visible : Visibility.Collapsed;
        RenderCompleted?.Invoke(this, EventArgs.Empty);
    }
    public new void Dispose()
    {
        if (_disposed) return; _disposed = true; Detach(); CancelGesture(); _builder.Clear();
        // GL resources belong to the host's OnDestroy callback with a current context.
        base.Dispose();
    }
}
