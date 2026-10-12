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

public sealed partial class LinkedChartsPage : Page
{
    private readonly ChartSurface _upper = new(), _lower = new();
    private readonly CheckBox _rangeLink = new() { Content = "Link X ranges", IsChecked = true, MinHeight = 30 };
    private readonly CheckBox _cursorLink = new() { Content = "Link cursors", IsChecked = true, MinHeight = 30 };
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(14, 4, 14, 4) };
    private readonly Dictionary<string, FrameworkElement> _controls = [];
    private readonly DispatcherTimer _diagnostics = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ChartSynchronizationGroup? _synchronization;
    private int _generation;
    private bool _light;

    public LinkedChartsPage()
    {
        RequestedTheme = ElementTheme.Dark;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 23, 28, 40)) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(12, 8, 12, 8) };
        toolbar.Children.Add(new TextBlock { Text = "Linked scientific signals", FontSize = 17, VerticalAlignment = VerticalAlignment.Center });
        toolbar.Children.Add(_rangeLink); toolbar.Children.Add(_cursorLink);
        toolbar.Children.Add(Button("fit", "Fit", Reset));
        toolbar.Children.Add(Button("replace", "Replace lower model", ReplaceLower));
        toolbar.Children.Add(Button("theme", "Light / dark", ToggleTheme));
        var toolbars = new StackPanel();
        toolbars.Children.Add(new ScrollViewer { Content = toolbar, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled });
        BuildLayoutToolbar(toolbars); root.Children.Add(toolbars);
        Grid.SetRow(_upper, 1); Grid.SetRow(_lower, 2); Grid.SetRow(_status, 3);
        root.Children.Add(_upper); root.Children.Add(_lower); root.Children.Add(_status); Content = root;
        Register("upper", _upper); Register("lower", _lower); Register("ranges", _rangeLink); Register("cursors", _cursorLink);
        _upper.Model = CreateModel(false); _lower.Model = CreateModel(true);
        _upper.Controller.InterpolateRollover = _lower.Controller.InterpolateRollover = true;
        InitializeLayout();
        _upper.RenderCompleted += (_, _) => Publish(); _lower.RenderCompleted += (_, _) => Publish();
        _rangeLink.Checked += (_, _) => Relink(); _rangeLink.Unchecked += (_, _) => Relink();
        _cursorLink.Checked += (_, _) => Relink(); _cursorLink.Unchecked += (_, _) => Relink();
        Loaded += (_, _) => { Relink(); _diagnostics.Start(); };
        Unloaded += (_, _) => { _diagnostics.Stop(); _synchronization?.Dispose(); _synchronization = null; };
        _diagnostics.Tick += (_, _) => Publish();
    }

    private ChartModel CreateModel(bool milliseconds)
    {
        double units = milliseconds ? 1000 : 1;
        var model = new ChartModel
        {
            Title = milliseconds ? "Response · milliseconds" : "Reference · seconds",
            Subtitle = "Move over either chart to compare signals; wheel or drag to navigate linked X ranges.",
            Theme = _light ? ChartTheme.Light : ChartTheme.Dark,
            ShowLegend = false
        };
        model.XAxis.VisibleRange = new(0, 10 * units); model.XAxis.AutoRange = AutoRange.Never;
        model.YAxis.VisibleRange = new(-1.6, 1.6); model.YAxis.AutoRange = AutoRange.Never;
        model.XAxis.AxisTitle = milliseconds ? "Time (ms)" : "Time (s)";
        model.YAxis.AxisTitle = milliseconds ? "Response" : "Reference";
        var data = new XyDataSeries();
        double[] x = new double[3001], y = new double[3001];
        for (int i = 0; i < x.Length; i++)
        {
            double t = i / 300.0;
            x[i] = t * units;
            y[i] = Math.Sin(t * 3 + (milliseconds ? .35 + _generation * .1 : 0))
                + (milliseconds ? .18 * Math.Cos(t * 13) : .08 * Math.Cos(t * 7));
        }
        data.AppendRange(x, y);
        var color = model.Theme.Palette[milliseconds ? 1 : 0];
        model.RenderableSeries.Add(new FastLineRenderableSeries { DataSeries = data, SeriesName = milliseconds ? "Response" : "Reference", Stroke = color });
        ConfigureLayoutAxes(model, milliseconds);
        return model;
    }
    private void Relink()
    {
        _synchronization?.Dispose(); _synchronization = null;
        if (!IsLoaded) return;
        _synchronization = new("time");
        bool ranges = _rangeLink.IsChecked == true, cursors = _cursorLink.IsChecked == true;
        _synchronization.Add(_upper, synchronizeRanges: ranges, synchronizeCursors: cursors);
        _synchronization.Add(_lower, toCanonical: x => x / 1000, fromCanonical: x => x * 1000,
            synchronizeRanges: ranges, synchronizeCursors: cursors);
        Publish();
    }
    private void Reset()
    {
        _upper.Model.XAxis.VisibleRange = new(0, 10); _lower.Model.XAxis.VisibleRange = new(0, 10000);
        _upper.Model.YAxis.VisibleRange = _lower.Model.YAxis.VisibleRange = new(-1.6, 1.6);
    }
    private void ReplaceLower()
    {
        _generation++;
        var previous = _lower.Model;
        _lower.Model = CreateModel(true);
        _lower.Controller.InterpolateRollover = true;
        previous.Dispose();
        Publish();
    }
    private void ToggleTheme()
    {
        _light = !_light; RequestedTheme = _light ? ElementTheme.Light : ElementTheme.Dark;
        var theme = _light ? ChartTheme.Light : ChartTheme.Dark;
        _upper.Model.Theme = _lower.Model.Theme = theme;
        _upper.Model.RenderableSeries[0].Stroke = theme.Palette[0]; _lower.Model.RenderableSeries[0].Stroke = theme.Palette[1];
    }
    private Button Button(string id, string label, Action action)
    {
        var button = new Button { Content = label, FontSize = 12, MinHeight = 30 };
        button.Click += (_, _) => action(); Register(id, button); return button;
    }
    private void Register(string id, FrameworkElement element)
    {
        _controls[id] = element; AutomationProperties.SetAutomationId(element, "linked-" + id); UiInputTrace.Register(element,"linked-"+id);
    }
    private static ControlBounds? Bounds(FrameworkElement element)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;
        var point = element.TransformToVisual(null).TransformPoint(new Point(0, 0));
        return new(point.X, point.Y, element.ActualWidth, element.ActualHeight);
    }
    private static LinkedSurfaceSnapshot Surface(ChartSurface surface)
    {
        var model = surface.Model; var plot = surface.LastFrame?.Layout.PlotArea ?? default;
        double? dataX = null;
        if (surface.Controller.View.Cursor is { } cursor && !plot.IsEmpty)
        {
            double value = new CoordinateCalculator(model.XAxis, plot.X, plot.Width).GetDataValue(cursor.X);
            if (double.IsFinite(value)) dataX = value;
        }
        var origin = surface.TransformToVisual(null).TransformPoint(new Point(0, 0));
        return new(model.XAxis.VisibleRange.Min, model.XAxis.VisibleRange.Max, dataX,
            surface.Controller.View.Hits.Count, model.RenderableSeries[0].DataSeries!.Revision, surface.FrameCount,
            new(origin.X + plot.X, origin.Y + plot.Y, plot.Width, plot.Height));
    }
    private void Publish()
    {
        if (!IsLoaded) return;
        PublishLayout();
        var error = _upper.LastError ?? _lower.LastError;
        bool ready = _upper.LastFrame is not null && _lower.LastFrame is not null && error is null;
        _status.Text = error ?? $"Range linking: {(_rangeLink.IsChecked == true ? "on" : "off")} · Cursor linking: {(_cursorLink.IsChecked == true ? "on" : "off")} · Source data is unchanged by linked navigation.";
#if __WASM__
        var snapshot = new LinkedChartsSnapshot(ready, error, _generation, _rangeLink.IsChecked == true, _cursorLink.IsChecked == true,
            Surface(_upper), Surface(_lower), _controls.ToDictionary(pair => pair.Key, pair => Bounds(pair.Value)));
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartSceneLinked=" + JsonSerializer.Serialize(snapshot, LinkedChartsJsonContext.Default.LinkedChartsSnapshot));
#endif
    }
}

public sealed record LinkedSurfaceSnapshot(double min, double max, double? cursorX, int hits, long dataRevision, long frames, ControlBounds plot);
public sealed record LinkedChartsSnapshot(bool ready, string? error, int generation, bool rangesLinked, bool cursorsLinked,
    LinkedSurfaceSnapshot upper, LinkedSurfaceSnapshot lower, Dictionary<string, ControlBounds?> controls);
[JsonSerializable(typeof(LinkedChartsSnapshot))]
public partial class LinkedChartsJsonContext : JsonSerializerContext;
