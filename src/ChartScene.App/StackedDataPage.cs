using System.Text.Json;
using System.Text.Json.Serialization;
using ChartScene.Charting;
using ChartScene.Controls;
using ChartScene.Core;
using ChartScene.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ChartScene.App;

/// <summary>Exercises shared stack captures and atomic data mutation through real Uno controls.</summary>
public sealed class StackedDataPage : Page
{
    private readonly ChartSurface _chart = new();
    private readonly ChartModel _model = new() { Title = "Stacked signals", Subtitle = "Shared stack generations · atomic data edits", ShowLegend = true };
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(14, 6, 14, 6), TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, FrameworkElement> _controls = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private StackedFrame? _renderedStack;
    private int _generation, _edits, _notifications, _lastBatchNotifications;
    private string? _error;
    public StackedDataPage()
    {
        RequestedTheme = ElementTheme.Dark;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(12, 8, 12, 8) };
        toolbar.Children.Add(Button("mode", "Absolute / percent", ToggleMode));
        toolbar.Children.Add(Button("edit", "Edit 64 samples", Edit));
        toolbar.Children.Add(Button("replace", "Replace all data", Replace));
        toolbar.Children.Add(Button("visibility", "Toggle middle series", () => _model.RenderableSeries[1].IsVisible = !_model.RenderableSeries[1].IsVisible));
        toolbar.Children.Add(Button("theme", "Light / dark", Theme));
        toolbar.Children.Add(Button("fit", "Fit", () => _chart.Controller.ZoomExtents()));
        root.Children.Add(new ScrollViewer { Content = toolbar, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled });
        Grid.SetRow(_chart, 1); Grid.SetRow(_status, 2); root.Children.Add(_chart); root.Children.Add(_status); Content = root;
        _model.XAxis.AxisTitle = "Time (s)"; _model.YAxis.AxisTitle = "Amplitude";
        for (int channel = 0; channel < 3; channel++)
        {
            var color = _model.Theme.Palette[channel];
            var data = new XyDataSeries(); var (x, y) = Values(5000, channel); data.AppendRange(x, y);
            _model.RenderableSeries.Add(new StackedMountainRenderableSeries { DataSeries = data, SeriesName = $"Channel {channel + 1}", Stroke = color, Fill = color.WithAlpha(170) });
        }
        _model.YAxis.AutoRange = AutoRange.Always;
        _chart.Model = _model; _chart.Controller.InterpolateRollover = true; Register("chart", _chart);
        _model.Changed += (_, _) => _notifications++;
        _chart.RenderCompleted += (_, _) => { _renderedStack = StackCalculator.Capture(_model); Publish(); };
        Loaded += (_, _) => _timer.Start(); Unloaded += (_, _) => _timer.Stop(); _timer.Tick += (_, _) => Publish();
    }
    private (double[] X, double[] Y) Values(int count, int channel)
    {
        double[] x = new double[count], y = new double[count];
        for (int i = 0; i < count; i++) { x[i] = i * .002; y[i] = 1.5 + channel * .5 + Math.Sin(x[i] * (1 + channel * .4) + _generation * .3) * .5; }
        return (x, y);
    }
    private void Batch(Action action)
    {
        int before = _notifications;
        using (_model.SuspendUpdates()) action();
        _lastBatchNotifications = _notifications - before;
    }
    private void ToggleMode() => Batch(() =>
    {
        var next = _model.RenderableSeries[0].StackMode == StackMode.Absolute ? StackMode.Percent : StackMode.Absolute;
        foreach (var series in _model.RenderableSeries) series.StackMode = next;
        _model.YAxis.AxisTitle = next == StackMode.Percent ? "Contribution (%)" : "Amplitude";
    });
    private void Edit() => Batch(() =>
    {
        _edits++;
        foreach (var series in _model.RenderableSeries)
        {
            var data = (XyDataSeries)series.DataSeries!; int index = data.Count / 2;
            data.UpdateRange(index, Enumerable.Repeat(3.0 + _edits * .1, 64).ToArray());
        }
    });
    private void Replace() => Batch(() =>
    {
        _generation++;
        for (int channel = 0; channel < 3; channel++)
        {
            var (x, y) = Values(_generation % 2 == 1 ? 4000 : 5000, channel);
            ((XyDataSeries)_model.RenderableSeries[channel].DataSeries!).ReplaceAll(x, y);
        }
    });
    private void Theme()
    {
        bool light = _model.Theme != ChartTheme.Light;
        RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
        Batch(() =>
        {
            _model.Theme = light ? ChartTheme.Light : ChartTheme.Dark;
            for (int i = 0; i < 3; i++) { _model.RenderableSeries[i].Stroke = _model.Theme.Palette[i]; _model.RenderableSeries[i].Fill = _model.Theme.Palette[i].WithAlpha(170); }
        });
    }
    private Button Button(string id, string label, Action action)
    {
        var button = new Button { Content = label, FontSize = 12, MinHeight = 30 };
        button.Click += (_, _) => { try { _error = null; action(); } catch (Exception error) { _error = error.Message; } Publish(); };
        Register(id, button); return button;
    }
    private void Register(string id, FrameworkElement element)
    { _controls[id] = element; AutomationProperties.SetAutomationId(element, "stacks-" + id); }
    private static ControlBounds? Bounds(FrameworkElement element)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;
        var origin = element.TransformToVisual(null).TransformPoint(new Point(0, 0));
        return new(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
    }
    private void Publish()
    {
        if (!IsLoaded) return;
        var error = _error ?? _chart.LastError;
        _status.Text = error ?? $"Stack generation {_renderedStack?.BuildNumber ?? 0} · {_renderedStack?.SourcePoints ?? 0:N0} visible source samples · last batch: {_lastBatchNotifications} model notification(s) · {_chart.FrameCount} rendered frames";
#if __WASM__
        bool ready = _chart.LastFrame is { } rendered && rendered.Statistics.ModelRevision == _model.Revision && _renderedStack is not null && error is null;
        double[] top = [];
        if (_renderedStack is not null && _renderedStack.TryGetValue(_model.RenderableSeries[^1], out var values))
            top = [values.Upper[0], values.Upper[values.Upper.Count / 2], values.Upper[^1]];
        var plot = _chart.LastFrame?.Layout.PlotArea ?? default; var origin = _chart.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var snapshot = new StackedDataSnapshot(ready, error, _generation, _edits, _lastBatchNotifications,
            _chart.FrameCount, _renderedStack?.BuildNumber ?? 0, _renderedStack?.SourcePoints ?? 0,
            _model.RenderableSeries[0].StackMode.ToString(), _model.RenderableSeries[1].IsVisible, _model.Theme.Name,
            _model.XAxis.VisibleRange.Min, _model.XAxis.VisibleRange.Max, _chart.Controller.View.Hits.Count,
            _model.RenderableSeries.Select(s => s.DataSeries!.Revision).ToArray(), top,
            new(origin.X + plot.X, origin.Y + plot.Y, plot.Width, plot.Height), _controls.ToDictionary(p => p.Key, p => Bounds(p.Value)));
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartSceneStacks=" + JsonSerializer.Serialize(snapshot, StackedDataJsonContext.Default.StackedDataSnapshot));
#endif
    }
}
public sealed record StackedDataSnapshot(bool ready, string? error, int generation, int edits, int batchNotifications,
    long frames, long stackBuild, long sourcePoints, string mode, bool middleVisible, string theme,
    double min, double max, int hits, long[] revisions, double[] top, ControlBounds plot, Dictionary<string, ControlBounds?> controls);
[JsonSerializable(typeof(StackedDataSnapshot))]
public partial class StackedDataJsonContext : JsonSerializerContext;
