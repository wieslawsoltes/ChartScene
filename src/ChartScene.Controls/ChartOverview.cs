using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
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
using Windows.Foundation;
using Windows.System;

namespace ChartScene.Controls;

/// <summary>
/// Reusable overview with draggable viewport, edge grips, Shift-drag selection, wheel/keyboard
/// navigation and shared history. The miniature is an immutable Skia picture reused while only
/// the viewport changes. ParentSurface is optional: Axis, Source and FullRange also bind independently.
/// The control owns only its mirror model/resources, never the source chart or data.
/// </summary>
public class ChartOverview : UserControl, IDisposable
{
    private sealed class OverviewCanvas(ChartOverview owner) : SKCanvasElement
    {
        protected override void RenderOverride(SKCanvas canvas, Size area) => owner.Render(canvas, area);
        public void RequestRender() => Invalidate();
    }
    private static DependencyProperty Property(string name,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors |
            DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] Type type, object? initial = null) =>
        DependencyProperty.Register(name, type, typeof(ChartOverview), new PropertyMetadata(initial, (d, _) => ((ChartOverview)d).ConfigurationChanged()));
    public static readonly DependencyProperty ParentSurfaceProperty = Property(nameof(ParentSurface), typeof(ChartSurface));
    public static readonly DependencyProperty SourceProperty = Property(nameof(Source), typeof(RenderableSeries));
    public static readonly DependencyProperty AxisProperty = Property(nameof(Axis), typeof(AxisModel));
    public static readonly DependencyProperty FullRangeProperty = Property(nameof(FullRange), typeof(DoubleRange?));
    public static readonly DependencyProperty ThemeProperty = Property(nameof(Theme), typeof(ChartTheme));
    public static readonly DependencyProperty OrientationProperty = Property(nameof(Orientation), typeof(Orientation), Orientation.Horizontal);
    public static readonly DependencyProperty ShowPreviewProperty = Property(nameof(ShowPreview), typeof(bool), true);
    public ChartSurface? ParentSurface { get => (ChartSurface?)GetValue(ParentSurfaceProperty); set => SetValue(ParentSurfaceProperty, value); }
    public RenderableSeries? Source { get => (RenderableSeries?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public AxisModel? Axis { get => (AxisModel?)GetValue(AxisProperty); set => SetValue(AxisProperty, value); }
    public DoubleRange? FullRange { get => (DoubleRange?)GetValue(FullRangeProperty); set => SetValue(FullRangeProperty, value); }
    public ChartTheme? Theme { get => (ChartTheme?)GetValue(ThemeProperty); set => SetValue(ThemeProperty, value); }
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public bool ShowPreview { get => (bool)GetValue(ShowPreviewProperty); set => SetValue(ShowPreviewProperty, value); }
    private UndoManager? _history;
    public UndoManager? History { get => _history; set { if (ReferenceEquals(_history, value)) return; _history = value; ConfigurationChanged(); } }
    public AxisRangeNavigator? Navigator { get; private set; }
    public RangeNavigationLayout? LastLayout { get; private set; }
    public long FrameCount { get; private set; }
    public long PreviewBuildCount { get; private set; }
    public string? LastError { get; private set; }
    public event EventHandler? RenderCompleted;

    private readonly OverviewCanvas _canvas;
    private readonly ChartRenderer _previewRenderer = new();
    private readonly UndoManager _standaloneHistory = new();
    private readonly HashSet<VirtualKey> _keys = [];
    private ChartSurface? _parent;
    private ChartModel? _parentModel, _preview;
    private RenderableSeries? _source;
    private AxisModel? _axis, _yAxis, _implicitRangeAxis;
    private DoubleRange _implicitRange;
    private long _parentCallback;
    private ChartTheme _theme = ChartTheme.Dark;
    private SkiaResources? _resources;
    private SKPicture? _picture;
    private Size _pictureSize;
    private bool _attached, _disposed, _refreshing;
    private volatile bool _previewDirty = true;
    private int _queued;
    private uint? _pointer;
    private record PreviewAxes(AxisScale XScale, double XBase, bool XFlip, AxisScale YScale, double YBase, bool YFlip, Orientation Orientation);
    private PreviewAxes? _axes;

    public ChartOverview()
    {
        IsTabStop = true; MinWidth = 40; MinHeight = 32;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        _canvas = new(this) { IsHitTestVisible = false };
        Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { _canvas } };
        AutomationProperties.SetName(this, "Chart overview");
        AutomationProperties.SetHelpText(this, "Drag the viewport to scroll, drag either grip to zoom, or Shift-drag a new range. Arrow keys pan, plus/minus zoom, Home fits all data, Escape cancels.");
        Loaded += (_, _) => { _attached = true; RefreshBindings(); RequestRender(); };
        Unloaded += (_, _) => { _attached = false; Detach(); };
        SizeChanged += (_, _) => { Navigator?.Cancel(); RequestRender(); };
        PointerPressed += Down; PointerMoved += Move; PointerReleased += Up;
        PointerCanceled += (_, _) => Cancel(); PointerCaptureLost += (_, _) => { if (_pointer is not null) Cancel(); };
        PointerWheelChanged += (_, e) =>
        {
            if (Navigator is null) return;
            Navigator.Zoom(Math.Exp(Math.Clamp(-e.GetCurrentPoint(this).Properties.MouseWheelDelta * .0015, -2, 2))); e.Handled = true;
        };
        KeyDown += Key; KeyUp += (_, e) => _keys.Remove(e.Key); LostFocus += (_, _) => _keys.Clear();
    }
    private void ConfigurationChanged() { _previewDirty = true; RefreshBindings(); RequestRender(); }
    private void ParentChanged(object? sender, EventArgs e) => RequestRender();
    private void SourceChanged(object? sender, PropertyChangedEventArgs e) { _previewDirty = true; RequestRender(); }
    private void NavigationChanged(object? sender, EventArgs e) => RequestRender();
    private void RefreshBindings()
    {
        if (!_attached || _disposed || _refreshing) return;
        _refreshing = true;
        try
        {
            if (!ReferenceEquals(_parent, ParentSurface))
            {
                if (_parent is not null) _parent.UnregisterPropertyChangedCallback(ChartSurface.ModelProperty, _parentCallback);
                _parent = ParentSurface;
                if (_parent is not null) _parentCallback = _parent.RegisterPropertyChangedCallback(ChartSurface.ModelProperty, (_, _) => ConfigurationChanged());
                _previewDirty = true;
            }
            var model = _parent?.Model;
            if (!ReferenceEquals(_parentModel, model))
            {
                if (_parentModel is not null) _parentModel.Changed -= ParentChanged;
                _parentModel = model;
                if (model is not null) model.Changed += ParentChanged;
                _previewDirty = true;
            }
            var source = Source ?? model?.RenderableSeries.FirstOrDefault();
            if (!ReferenceEquals(_source, source))
            {
                if (_source is not null) _source.PropertyChanged -= SourceChanged;
                _source = source;
                if (source is not null) source.PropertyChanged += SourceChanged;
                _previewDirty = true;
            }
            var axis = Axis ?? (source is not null && model is not null ? model.FindXAxis(source.XAxisId) : model?.XAxes.FirstOrDefault());
            var yAxis = source is not null && model is not null ? model.FindYAxis(source.YAxisId) : null;
            var theme = Theme ?? model?.Theme ?? ChartTheme.Dark;
            var axes = new PreviewAxes(axis?.Scale ?? AxisScale.Numeric, axis?.LogarithmicBase ?? 10, axis?.FlippedCoordinates ?? false,
                yAxis?.Scale ?? AxisScale.Numeric, yAxis?.LogarithmicBase ?? 10, yAxis?.FlippedCoordinates ?? false, Orientation);
            if (_axes != axes || !ReferenceEquals(_theme, theme) || !ReferenceEquals(_axis, axis) || !ReferenceEquals(_yAxis, yAxis)) _previewDirty = true;
            _axes = axes; _theme = theme; _axis = axis; _yAxis = yAxis;
            PreparePreview();
            var history = History ?? _parent?.Controller.History ?? _standaloneHistory;
            if (Navigator is not null && (!ReferenceEquals(Navigator.Axis, axis) || !ReferenceEquals(Navigator.History, history))) ReleaseNavigator();
            if (axis is null) { LastError = null; return; }
            if (!ReferenceEquals(_implicitRangeAxis, axis)) { _implicitRangeAxis = axis; _implicitRange = axis.VisibleRange; }
            var range = FullRange ?? _preview?.XAxis.VisibleRange ?? _implicitRange;
            if (Navigator is null)
            {
                Navigator = new(axis, range, history) { IsVertical = Orientation == Orientation.Vertical };
                Navigator.Changed += NavigationChanged;
            }
            else { Navigator.FullRange = range; Navigator.IsVertical = Orientation == Orientation.Vertical; }
            LastError = null;
        }
        catch (Exception error) { _previewDirty = true; LastError = error.Message; ReleaseNavigator(); }
        finally { _refreshing = false; }
    }
    private void PreparePreview()
    {
        if (!_previewDirty) return;
        _previewDirty = false; _picture?.Dispose(); _picture = null; _preview?.Dispose(); _preview = null;
        if (_source is not { } source || _axes is not { } axes) return;
        if (_parentModel?.CoordinateSystem is CoordinateSystem.Polar or CoordinateSystem.Ternary || source.IsCircular
            || source.Kind is SeriesKind.PolarLine or SeriesKind.Radar or SeriesKind.TernaryLine or SeriesKind.TernaryScatter)
            throw new NotSupportedException("Overview navigation requires Cartesian axes and series.");
        var preview = new ChartModel { Title = "", Subtitle = "", ShowLegend = false, Theme = _theme };
        try
        {
            var mirror = new RenderableSeries(source.Kind)
            {
                Id = source.Id, SeriesName = source.SeriesName, DataSeries = source.DataSeries, HeatmapGrid = source.HeatmapGrid,
                Stroke = source.Stroke, Fill = source.Fill, StrokeY1 = source.StrokeY1, FillY1 = source.FillY1,
                StrokeThickness = Math.Min(1.5, source.StrokeThickness), Opacity = source.Opacity,
                DataPointWidth = source.DataPointWidth, ZeroLineY = source.ZeroLineY, ColorMap = source.ColorMap,
                ColorScaleMode = source.ColorScaleMode, ColorMapRange = source.ColorMapRange, ContourLevels = source.ContourLevels,
                PointMarker = source.PointMarker, MarkerSize = Math.Min(4, source.MarkerSize), PaletteProvider = source.PaletteProvider,
                StackMode = source.StackMode, StackedGroupId = source.StackedGroupId, IsResamplingEnabled = source.IsResamplingEnabled,
                VectorScale = source.VectorScale, VectorColorMode = source.VectorColorMode, VectorLengthMode = source.VectorLengthMode,
                ArrowHeadLength = source.ArrowHeadLength, ArrowHeadWidth = source.ArrowHeadWidth,
                VectorMinimumSpacing = source.VectorMinimumSpacing, IsArrowHeadFilled = source.IsArrowHeadFilled
            };
            preview.RenderableSeries.Add(mirror);
            preview.XAxis.Scale = axes.XScale; preview.XAxis.LogarithmicBase = axes.XBase; preview.XAxis.FlippedCoordinates = axes.XFlip;
            preview.YAxis.Scale = axes.YScale; preview.YAxis.LogarithmicBase = axes.YBase; preview.YAxis.FlippedCoordinates = axes.YFlip;
            preview.XAxis.AxisAlignment = Orientation == Orientation.Vertical ? AxisAlignment.Left : AxisAlignment.Bottom;
            preview.YAxis.AxisAlignment = Orientation == Orientation.Vertical ? AxisAlignment.Bottom : AxisAlignment.Left;
            preview.XAxis.IsVisible = preview.YAxis.IsVisible = false;
            preview.XAxis.GrowByLower = preview.XAxis.GrowByUpper = 0;
            preview.XAxis.AxisTitle = preview.YAxis.AxisTitle = "";
            preview.ZoomExtents();
            if (FullRange is { } range) preview.XAxis.VisibleRange = range;
            preview.XAxis.AutoRange = preview.YAxis.AutoRange = AutoRange.Never;
            _preview = preview;
        }
        catch { preview.Dispose(); throw; }
    }
    public void RequestRender()
    {
        if (_disposed || !_attached || Interlocked.Exchange(ref _queued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && _attached) _canvas.RequestRender(); else Interlocked.Exchange(ref _queued, 0);
        })) Interlocked.Exchange(ref _queued, 0);
    }
    private void Render(SKCanvas canvas, Size area)
    {
        Interlocked.Exchange(ref _queued, 0);
        if (_disposed || !_attached) return;
        RefreshBindings(); _resources ??= new(); _resources.BeginFrame();
        var c = new SkiaChartCanvas(canvas, _resources); c.Clear(_theme.Background);
        LastLayout = null;
        if (area.Width <= 24 || area.Height <= 20) return;
        try
        {
            var track = new RectD(12, 8, area.Width - 24, area.Height - 20);
            if (LastError is not null) { c.DrawText(LastError, new(12, 22), new(_theme.Foreground, 11)); return; }
            if (ShowPreview && _preview is not null)
            {
                if (_picture is null || _pictureSize != area)
                {
                    _picture?.Dispose();
                    using var recorder = new SKPictureRecorder();
                    var target = recorder.BeginRecording(new SKRect(0, 0, (float)area.Width, (float)area.Height));
                    _previewRenderer.Render(new SkiaChartCanvas(target, _resources), _preview, area.Width, area.Height);
                    _picture = recorder.EndRecording(); _pictureSize = area; PreviewBuildCount++;
                }
                canvas.DrawPicture(_picture);
            }
            else c.DrawRectangle(track, _theme.GridMinor);
            if (Navigator is null) return;
            var layout = Navigator.Layout(track); LastLayout = layout; var selected = layout.Selection;
            c.Save(); c.Clip(track);
            if (Orientation == Orientation.Horizontal)
            {
                c.DrawRectangle(new(track.X, track.Y, selected.X - track.X, track.Height), _theme.Background.WithAlpha(175));
                c.DrawRectangle(new(selected.Right, track.Y, track.Right - selected.Right, track.Height), _theme.Background.WithAlpha(175));
            }
            else
            {
                c.DrawRectangle(new(track.X, track.Y, track.Width, selected.Y - track.Y), _theme.Background.WithAlpha(175));
                c.DrawRectangle(new(track.X, selected.Bottom, track.Width, track.Bottom - selected.Bottom), _theme.Background.WithAlpha(175));
            }
            c.DrawRectangle(selected, _theme.Accent.WithAlpha(18), new(_theme.Accent, 1.4));
            c.Restore();
            foreach (var grip in new[] { layout.MinimumGrip, layout.MaximumGrip })
            {
                var bounds = Orientation == Orientation.Horizontal ? new RectD(grip.X - 4, track.Y, 8, track.Height)
                    : new RectD(track.X, grip.Y - 4, track.Width, 8);
                c.DrawRectangle(bounds, _theme.Accent, null, 2);
                if (Orientation == Orientation.Horizontal) c.DrawLine(new(grip.X, grip.Y - 6), new(grip.X, grip.Y + 6), new(_theme.Background, 1));
                else c.DrawLine(new(grip.X - 6, grip.Y), new(grip.X + 6, grip.Y), new(_theme.Background, 1));
            }
            FrameCount++; LastError = null;
        }
        catch (Exception error) { LastError = error.Message; }
        finally { RenderCompleted?.Invoke(this, EventArgs.Empty); }
    }
    private static PointD Position(PointerRoutedEventArgs e, UIElement target)
    { var point = e.GetCurrentPoint(target).Position; return new(point.X, point.Y); }
    private void Down(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer is not null || Navigator is null || LastLayout is not { } layout) return;
        Focus(FocusState.Pointer);
        if (!Navigator.Begin(Position(e, this), layout.Track, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))) return;
        _pointer = e.Pointer.PointerId; CapturePointer(e.Pointer); e.Handled = true;
    }
    private void Move(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId) return;
        Navigator?.Move(Position(e, this)); e.Handled = true;
    }
    private void Up(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId) return;
        _pointer = null; Navigator?.End(Position(e, this)); ReleasePointerCapture(e.Pointer); e.Handled = true;
    }
    private void Cancel() { _pointer = null; Navigator?.Cancel(); ReleasePointerCaptures(); }
    private void Key(object sender, KeyRoutedEventArgs e)
    {
        _keys.Add(e.Key); if (Navigator is not { } n) return;
        bool control = _keys.Contains(VirtualKey.Control) || _keys.Contains(VirtualKey.LeftControl) || _keys.Contains(VirtualKey.RightControl);
        if (control && e.Key == VirtualKey.Z) n.History.Undo();
        else if (control && e.Key == VirtualKey.Y) n.History.Redo();
        else if (e.Key == VirtualKey.Escape) Cancel();
        else if (e.Key == VirtualKey.Home) n.Fit();
        else if (e.Key == VirtualKey.Add || e.Key.ToString() == "OemPlus") n.Zoom(.8);
        else if (e.Key == VirtualKey.Subtract || e.Key.ToString() == "OemMinus") n.Zoom(1.25);
        else if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown)
        {
            double direction = e.Key is VirtualKey.Left or VirtualKey.Up or VirtualKey.PageUp ? -1 : 1;
            n.Pan(direction * (n.IsReversed ? -1 : 1) * (e.Key is VirtualKey.PageUp or VirtualKey.PageDown ? .8 : .1));
        }
        else return;
        e.Handled = true;
    }
    private void ReleaseNavigator()
    {
        var navigator = Navigator; Navigator = null;
        if (navigator is not null) { navigator.Changed -= NavigationChanged; navigator.Dispose(); }
    }
    private void Detach()
    {
        if (_parent is not null) _parent.UnregisterPropertyChangedCallback(ChartSurface.ModelProperty, _parentCallback);
        if (_parentModel is not null) _parentModel.Changed -= ParentChanged;
        if (_source is not null) _source.PropertyChanged -= SourceChanged;
        _parent = null; _parentModel = null; _source = null; _pointer = null;
        ReleaseNavigator(); _preview?.Dispose(); _preview = null; _picture?.Dispose(); _picture = null;
        _resources?.Dispose(); _resources = null; _previewDirty = true; LastLayout = null;
        Interlocked.Exchange(ref _queued, 0);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new OverviewPeer(this);
    private sealed class OverviewPeer(ChartOverview owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(ChartOverview);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ScrollBar;
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.RangeValue ? this : base.GetPatternCore(pattern)!;
        public bool IsReadOnly => owner.Navigator is null;
        public double Minimum => 0;
        public double Maximum => owner.Navigator is { } n ? Math.Max(0, 100 * (1 - n.Selection.Maximum + n.Selection.Minimum)) : 100;
        public double Value => owner.Navigator is { } n ? Math.Clamp(n.Selection.Minimum * 100, 0, Maximum) : 0;
        public double SmallChange => 1;
        public double LargeChange => 10;
        public void SetValue(double value)
        {
            if (owner.Navigator is not { } n) throw new InvalidOperationException("Bind an axis before scrolling.");
            n.SetStartFraction(value / 100);
        }
    }
    public new virtual void Dispose()
    {
        if (_disposed) return; _disposed = true; _attached = false; Detach(); base.Dispose();
    }
}

/// <summary>Standalone range selector with the same navigation, history and automation contract, without a miniature chart.</summary>
public sealed class ChartScrollbar : ChartOverview
{
    public ChartScrollbar() => ShowPreview = false;
}
