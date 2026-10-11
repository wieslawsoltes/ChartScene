using System.ComponentModel;
using System.Globalization;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace ChartScene.Controls;

/// <summary>
/// Standalone Uno color legend bound to a heatmap/contour series. Observes source changes only
/// while loaded, never owns/disposes the source, and draws through the reusable legend renderer.
/// </summary>
public sealed class HeatmapColorLegend : UserControl, IDisposable
{
    private sealed class LegendCanvas(HeatmapColorLegend owner) : SKCanvasElement
    {
        protected override void RenderOverride(SKCanvas canvas, Size area) => owner.Render(canvas, area);
        public void RequestRender() => Invalidate();
    }
    private readonly LegendCanvas _canvas;
    private readonly HeatmapLegendRenderer _renderer = new();
    private SkiaResources? _resources;
    private bool _attached, _disposed;
    private int _queued;
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(RenderableSeries), typeof(HeatmapColorLegend),
        new PropertyMetadata(null, (d, e) => ((HeatmapColorLegend)d).ChangeSeries((RenderableSeries?)e.OldValue)));
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(nameof(Options), typeof(HeatmapLegendOptions), typeof(HeatmapColorLegend),
        new PropertyMetadata(null, (d, _) => ((HeatmapColorLegend)d).InvalidateConfiguration()));
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(nameof(Theme), typeof(ChartTheme), typeof(HeatmapColorLegend),
        new PropertyMetadata(null, (d, _) => ((HeatmapColorLegend)d).InvalidateConfiguration()));
    public RenderableSeries? Series
    {
        get => (RenderableSeries?)GetValue(SeriesProperty);
        set
        {
            if (value is not null && value.Kind is not (SeriesKind.Heatmap or SeriesKind.Contour))
                throw new ArgumentException("A color legend requires a heatmap or contour series.", nameof(value));
            SetValue(SeriesProperty, value);
        }
    }
    public HeatmapLegendOptions Options { get => (HeatmapLegendOptions?)GetValue(OptionsProperty) ?? DefaultOptions; set => SetValue(OptionsProperty, value); }
    public ChartTheme Theme { get => (ChartTheme?)GetValue(ThemeProperty) ?? ChartTheme.Dark; set => SetValue(ThemeProperty, value); }
    private static readonly HeatmapLegendOptions DefaultOptions = new();
    public HeatmapLegendFrame? LastFrame { get; private set; }
    public string? LastError { get; private set; }
    public double? HoveredValue { get; private set; }
    public long FrameCount { get; private set; }
    public event EventHandler<HeatmapLegendFrame>? RenderCompleted;
    public event EventHandler<double?>? HoveredValueChanged;

    public HeatmapColorLegend()
    {
        IsTabStop = true; HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        _canvas = new(this) { IsHitTestVisible = false };
        Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { _canvas } };
        AutomationProperties.SetName(this, "Heatmap color legend");
        AutomationProperties.SetHelpText(this, "Shows the mapping from heatmap data values to colors. Move over the gradient to inspect a value.");
        Loaded += (_, _) => { Attach(); RequestRender(); };
        Unloaded += (_, _) => { Detach(); SetHover(null); _resources?.Dispose(); _resources = null; Interlocked.Exchange(ref _queued, 0); };
        SizeChanged += (_, _) => InvalidateConfiguration();
        PointerMoved += (_, e) => { var p = e.GetCurrentPoint(this).Position; SetHover(LastFrame?.HitTest(new(p.X, p.Y))); };
        PointerExited += (_, _) => SetHover(null);
    }
    private void ChangeSeries(RenderableSeries? previous)
    {
        if (_attached && previous is not null) previous.PropertyChanged -= SeriesChanged;
        _attached = false; if (IsLoaded) Attach(); InvalidateConfiguration();
    }
    private void Attach()
    {
        if (_attached || _disposed || Series is null) return;
        Series.PropertyChanged += SeriesChanged; _attached = true;
    }
    private void Detach()
    {
        if (_attached && Series is not null) Series.PropertyChanged -= SeriesChanged;
        _attached = false;
    }
    private void SeriesChanged(object? sender, PropertyChangedEventArgs e)
    {
        void Apply() { if (!_disposed && ReferenceEquals(sender, Series)) InvalidateConfiguration(); }
        if (DispatcherQueue.HasThreadAccess) Apply(); else DispatcherQueue.TryEnqueue(Apply);
    }
    private void InvalidateConfiguration() { LastFrame = null; LastError = null; SetHover(null); RequestRender(); }
    private void SetHover(double? value)
    {
        if (HoveredValue == value) return;
        HoveredValue = value;
        ToolTipService.SetToolTip(this, value is { } number ? number.ToString("G6", CultureInfo.InvariantCulture) : null);
        HoveredValueChanged?.Invoke(this, value); RequestRender();
    }
    public void RequestRender()
    {
        if (_disposed || !IsLoaded || Interlocked.Exchange(ref _queued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() => { if (!_disposed && IsLoaded) _canvas.RequestRender(); else Interlocked.Exchange(ref _queued, 0); }))
            Interlocked.Exchange(ref _queued, 0);
    }
    private void Render(SKCanvas canvas, Size area)
    {
        Interlocked.Exchange(ref _queued, 0); if (_disposed) return;
        _resources ??= new(); _resources.BeginFrame(); var adapter = new SkiaChartCanvas(canvas, _resources);
        if (Series is null) { adapter.Clear(Theme.Background); LastFrame = null; return; }
        try
        {
            var scale = HeatmapColorScale.Capture(Series);
            LastFrame = _renderer.Render(adapter, scale, area.Width, area.Height, Options, Theme, HoveredValue);
            LastError = null; FrameCount++; RenderCompleted?.Invoke(this, LastFrame);
        }
        catch (Exception error)
        {
            LastFrame = null; LastError = error.Message; adapter.Clear(Theme.Background);
            adapter.DrawText("Color legend: " + error.Message, new(10, 24), new(Theme.Foreground, 11));
        }
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new LegendPeer(this);
    private sealed class LegendPeer(HeatmapColorLegend owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        protected override string GetClassNameCore() => nameof(HeatmapColorLegend);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetNameCore() => string.IsNullOrEmpty(owner.Options.Title) ? "Heatmap color legend" : owner.Options.Title;
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.Value ? this : base.GetPatternCore(pattern)!;
        public bool IsReadOnly => true;
        public string Value => owner.LastFrame is { } frame
            ? $"{frame.Scale.Mode}; {frame.Scale.Range}; {frame.Orientation}; opacity {frame.Scale.Opacity:G3}" : owner.LastError ?? "No color scale";
        public void SetValue(string value) => throw new InvalidOperationException("The legend observes its series color scale.");
    }
    public new void Dispose()
    {
        if (_disposed) return; _disposed = true; Detach(); _resources?.Dispose(); _resources = null; LastFrame = null;
        base.Dispose();
    }
}
