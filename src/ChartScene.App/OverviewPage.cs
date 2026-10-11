using System.Text.Json;
using System.Text.Json.Serialization;
using ChartScene.Charting;
using ChartScene.Controls;
using ChartScene.Core;
using ChartScene.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ChartScene.App;

/// <summary>Actual shared-data overview and independent Y scrollbar; diagnostics never drive application state.</summary>
public sealed class OverviewPage : Page
{
    private readonly ChartSurface _chart = new();
    private readonly ChartOverview _overview = new();
    private readonly ChartScrollbar _scrollbar = new() { Orientation = Orientation.Vertical };
    private readonly ComboBox _preset = new() { Width = 185, FontSize = 12 };
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(14, 4, 14, 4) };
    private readonly Dictionary<string, FrameworkElement> _controls = [];
    private readonly DispatcherTimer _diagnostics = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _stream = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private ChartModel? _model;
    private string _presetId = "million";
    private bool _changing, _streaming, _light;
    private int _generation;
    private double _time = 20;
    public OverviewPage()
    {
        RequestedTheme = ElementTheme.Dark;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 23, 28, 40)) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = new GridLength(125) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(12, 6, 12, 6) };
        tools.Children.Add(new TextBlock { Text = "Overview navigation", FontSize = 17, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        foreach (string text in new[] { "Million samples", "Logarithmic sweep", "Streaming FIFO", "Non-uniform heatmap" }) _preset.Items.Add(text);
        _preset.SelectedIndex = 0; tools.Children.Add(_preset); Register("preset", _preset);
        tools.Children.Add(Button("fit", "Fit", () => _overview.Navigator?.Fit()));
        tools.Children.Add(Button("undo", "Undo", () => _chart.Controller.History.Undo()));
        tools.Children.Add(Button("redo", "Redo", () => _chart.Controller.History.Redo()));
        tools.Children.Add(Button("flip", "Flip X", () => { if (_model is not null) _model.XAxis.FlippedCoordinates = !_model.XAxis.FlippedCoordinates; }));
        tools.Children.Add(Button("replace", "Replace source", ReplaceSource));
        tools.Children.Add(Button("theme", "Light / dark", ToggleTheme));
        tools.Children.Add(Button("stream", "Stream / pause", ToggleStream));
        root.Children.Add(new ScrollViewer { Content = tools, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled });
        var main = new Grid(); main.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); main.ColumnDefinitions.Add(new() { Width = new GridLength(48) });
        main.Children.Add(_chart); Grid.SetColumn(_scrollbar, 1); main.Children.Add(_scrollbar); Grid.SetRow(main, 1); root.Children.Add(main);
        Grid.SetRow(_overview, 2); root.Children.Add(_overview); Grid.SetRow(_status, 3); root.Children.Add(_status); Content = root;
        Register("chart", _chart); Register("overview", _overview); Register("scrollbar", _scrollbar);
        _overview.ParentSurface = _chart;
        _preset.SelectionChanged += (_, _) => { if (!_changing && IsLoaded) LoadPreset(_preset.SelectedIndex); };
        _chart.RenderCompleted += (_, _) => Publish(); _overview.RenderCompleted += (_, _) => Publish(); _scrollbar.RenderCompleted += (_, _) => Publish();
        _diagnostics.Tick += (_, _) => Publish(); _stream.Tick += (_, _) => Append();
        Loaded += (_, _) => { if (_model is null) LoadPreset(0); _diagnostics.Start(); };
        Unloaded += (_, _) => { _diagnostics.Stop(); _stream.Stop(); _streaming = false; };
    }
    private Button Button(string id, string text, Action action)
    {
        var button = new Button { Content = text, FontSize = 12, MinHeight = 30 };
        button.Click += (_, _) => action(); Register(id, button); return button;
    }
    private void Register(string id, FrameworkElement element)
    { _controls[id] = element; AutomationProperties.SetAutomationId(element, "overview-" + id); }
    private static double Signal(double t) => Math.Sin(t * 3) + .18 * Math.Cos(t * 31);
    private void LoadPreset(int index)
    {
        _changing = true; _streaming = false; _stream.Stop(); _time = 20;
        try
        {
            _presetId = index switch { 1 => "logarithmic", 2 => "streaming", 3 => "heatmap", _ => "million" };
            _preset.SelectedIndex = index;
            var model = new ChartModel { Title = _preset.Items[index].ToString()!, Subtitle = "Drag the overview viewport or grips · Shift-drag a new interval · Full data remains unchanged", ShowLegend = false,
                Theme = _light ? ChartTheme.Light : ChartTheme.Dark };
            var series = CreateSource(index, _generation);
            model.RenderableSeries.Add(series);
            if (index == 1) model.XAxis.Scale = AxisScale.Logarithmic;
            model.ZoomExtents(); model.XAxis.AutoRange = model.YAxis.AutoRange = AutoRange.Never;
            model.XAxis.VisibleRange = index switch { 1 => new(1, 1000), 2 => new(5, 12), 3 => new(100, 450), _ => new(100000, 400000) };
            if (index != 3) model.YAxis.VisibleRange = new(-1.5, 1.5);
            model.XAxis.AxisTitle = index switch { 1 => "Frequency", 2 => "Time (s)", _ => "X" };
            model.YAxis.AxisTitle = "Y";
            var previous = _model; _model = model; _chart.Model = model;
            _scrollbar.Axis = null; _scrollbar.FullRange = index == 3 ? new(0, 60) : new(-5, 5);
            _scrollbar.Axis = model.YAxis; _scrollbar.History = _chart.Controller.History; _scrollbar.Theme = model.Theme;
            previous?.Dispose(); Publish();
        }
        finally { _changing = false; }
    }
    private static RenderableSeries CreateSource(int index, int generation)
    {
        if (index == 3)
        {
            double[] x = Enumerable.Range(0, 201).Select(i => Math.Pow(i / 200.0, 1.4) * 1000).ToArray();
            double[] y = Enumerable.Range(0, 121).Select(i => Math.Pow(i / 120.0, 1.2) * 60).ToArray();
            double[] z = new double[200 * 120];
            for (int row = 0; row < 120; row++) for (int column = 0; column < 200; column++) z[row * 200 + column] = Math.Sin(column * .08 + generation * .2) * Math.Cos(row * .05);
            return new(SeriesKind.Heatmap) { HeatmapGrid = new(x, y, z), SeriesName = "Field", ColorMap = ColorMap.Thermal };
        }
        int count = index == 0 ? 1_000_000 : index == 1 ? 100000 : 4000;
        var data = new XyDataSeries(count); if (index == 2) data.FifoCapacity = 4000;
        double[] xs = new double[count], ys = new double[count];
        for (int i = 0; i < count; i++)
        {
            xs[i] = index == 0 ? i + 1 : index == 1 ? Math.Pow(10, -3 + 9.0 * i / (count - 1)) : i * .005;
            double t = index == 0 ? i * .00005 : index == 1 ? i * .0005 : xs[i];
            ys[i] = Signal(t + generation * .1);
        }
        data.AppendRange(xs, ys);
        return new FastLineRenderableSeries { DataSeries = data, SeriesName = "Signal", Stroke = ChartTheme.Dark.Palette[generation % 2] };
    }
    private void ReplaceSource()
    {
        if (_model is null) return;
        _streaming = false; _stream.Stop(); _generation++;
        var previous = _model.RenderableSeries[0]; _model.RenderableSeries[0] = CreateSource(_preset.SelectedIndex, _generation); previous.Dispose();
        _time = 20; Publish();
    }
    private void ToggleTheme()
    {
        if (_model is null) return;
        _light = !_light; RequestedTheme = _light ? ElementTheme.Light : ElementTheme.Dark;
        _model.Theme = _light ? ChartTheme.Light : ChartTheme.Dark; _scrollbar.Theme = _model.Theme;
        _model.RenderableSeries[0].Stroke = _model.Theme.Palette[_generation % 2];
    }
    private void ToggleStream()
    {
        if (_presetId != "streaming") LoadPreset(2);
        _streaming = !_streaming; if (_streaming) _stream.Start(); else _stream.Stop(); Publish();
    }
    private void Append()
    {
        if (!_streaming || _model?.RenderableSeries[0].DataSeries is not XyDataSeries data) return;
        using (data.SuspendUpdates()) for (int i = 0; i < 7; i++) { double time = _time + i * .005; data.Append(time, Signal(time)); }
        _time += .035;
    }
    private static ControlBounds? Bounds(FrameworkElement element)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;
        var p = element.TransformToVisual(null).TransformPoint(new Point(0, 0));
        return new(p.X, p.Y, element.ActualWidth, element.ActualHeight);
    }
    private static OverviewLayoutSnapshot? Layout(ChartOverview view)
    {
        if (view.LastLayout is not { } layout || Bounds(view) is not { } bounds) return null;
        ControlBounds Rect(RectD r) => new(bounds.x + r.X, bounds.y + r.Y, r.Width, r.Height);
        return new(Rect(layout.Track), Rect(layout.Selection), bounds.x + layout.MinimumGrip.X, bounds.y + layout.MinimumGrip.Y,
            bounds.x + layout.MaximumGrip.X, bounds.y + layout.MaximumGrip.Y);
    }
    private void Publish()
    {
        if (!IsLoaded || _model is null) return;
        var error = _chart.LastError ?? _overview.LastError ?? _scrollbar.LastError;
        _status.Text = error ?? $"{_model.RenderableSeries[0].DataSeries?.Count ?? _model.RenderableSeries[0].HeatmapGrid?.Values.Length ?? 0:N0} source samples · Preview recordings {_overview.PreviewBuildCount} · Viewport and cursor moves reuse the miniature · {(_streaming ? "Streaming" : "Paused")}";
#if __WASM__
        var snapshot = new OverviewSnapshot(error is null && _chart.LastFrame is not null && _overview.LastLayout is not null && _scrollbar.LastLayout is not null,
            error, _presetId, _generation, _streaming, _model.Theme.Name, _model.XAxis.FlippedCoordinates,
            _model.XAxis.VisibleRange.Min, _model.XAxis.VisibleRange.Max, _model.YAxis.VisibleRange.Min, _model.YAxis.VisibleRange.Max,
            _overview.Navigator?.FullRange.Min ?? 0, _overview.Navigator?.FullRange.Max ?? 1,
            _model.RenderableSeries[0].DataSeries?.Count ?? _model.RenderableSeries[0].HeatmapGrid?.Values.Length ?? 0,
            _model.RenderableSeries[0].DataSeries?.Revision ?? 0, _overview.PreviewBuildCount, _chart.FrameCount, _overview.FrameCount,
            _chart.Controller.History.CanUndo, _chart.Controller.History.CanRedo, Layout(_overview), Layout(_scrollbar),
            _controls.ToDictionary(p => p.Key, p => Bounds(p.Value)));
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartSceneOverview=" + JsonSerializer.Serialize(snapshot, OverviewJsonContext.Default.OverviewSnapshot));
#endif
    }
}

public sealed record OverviewLayoutSnapshot(ControlBounds track, ControlBounds selection, double minimumX, double minimumY, double maximumX, double maximumY);
public sealed record OverviewSnapshot(bool ready, string? error, string preset, int generation, bool streaming, string theme, bool flipped,
    double min, double max, double yMin, double yMax, double fullMin, double fullMax, int sourcePoints, long dataRevision,
    long previewBuilds, long chartFrames, long overviewFrames, bool canUndo, bool canRedo, OverviewLayoutSnapshot? overview,
    OverviewLayoutSnapshot? scrollbar, Dictionary<string, ControlBounds?> controls);
[JsonSerializable(typeof(OverviewSnapshot))]
public partial class OverviewJsonContext : JsonSerializerContext;
