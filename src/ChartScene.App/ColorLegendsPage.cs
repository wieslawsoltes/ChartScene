using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChartScene.Charting;
using ChartScene.Controls;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ChartScene.App;

/// <summary>Uses the public legend control twice against the same observable series.</summary>
public sealed class ColorLegendsPage : Page
{
    private readonly Grid _root = new();
    private readonly ChartSurface _chart = new();
    private readonly HeatmapColorLegend _vertical = new() { Options = new() { Title = "Intensity" } };
    private readonly HeatmapColorLegend _horizontal = new() { Options = new() { Orientation = ColorLegendOrientation.Horizontal, Title = "Intensity · shared color scale" } };
    private readonly TextBox _minimum = new() { Text = "1", Width = 80, FontSize = 12 };
    private readonly TextBox _maximum = new() { Text = "1000", Width = 80, FontSize = 12 };
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(12, 4, 12, 4) };
    private readonly Dictionary<string, FrameworkElement> _controls = [];
    private readonly DispatcherTimer _diagnostics = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ChartModel _model = new() { ShowLegend = false, Title = "Color-mapped measurements", Subtitle = "Uniform and non-uniform cells · transparent gaps · linear and logarithmic intensity" };
    private FastHeatMapRenderableSeries _series = new();
    private int _generation, _palette;
    private bool _nonUniform, _light;
    private string? _validationError;
    private static readonly ColorMap[] Maps = [ColorMap.Viridis, ColorMap.Thermal, ColorMap.Diverging];
    private static readonly string[] MapNames = ["Viridis", "Thermal", "Diverging"];

    public ColorLegendsPage()
    {
        RequestedTheme = ElementTheme.Dark;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = new GridLength(110) });
        _root.RowDefinitions.Add(new() { Height = new GridLength(28) });
        var toolbar = new StackPanel { Spacing = 6, Padding = new Thickness(10, 6, 10, 6) };
        var range = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        range.Children.Add(new TextBlock { Text = "Color scale", FontSize = 16, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        range.Children.Add(Button("auto", "Auto range", () => _series.ColorMapRange = null));
        range.Children.Add(_minimum); range.Children.Add(_maximum);
        range.Children.Add(Button("apply", "Apply range", ApplyRange));
        range.Children.Add(Button("scale", "Linear / log", () => _series.ColorScaleMode = _series.ColorScaleMode == HeatmapColorScaleMode.Linear ? HeatmapColorScaleMode.Logarithmic : HeatmapColorScaleMode.Linear));
        range.Children.Add(Button("palette", "Palette", () => _series.ColorMap = Maps[_palette = (_palette + 1) % Maps.Length]));
        range.Children.Add(Button("theme", "Light / dark", ToggleTheme));
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        tools.Children.Add(Button("grid", "Uniform / non-uniform", () => { _nonUniform = !_nonUniform; ReplaceGrid(); }));
        tools.Children.Add(Button("data", "Update values", () => { _generation++; ReplaceGrid(); }));
        tools.Children.Add(Button("series", "Replace series", ReplaceSeries));
        tools.Children.Add(Button("opacity", "Opacity", () => _series.Opacity = _series.Opacity == 1 ? .45 : 1));
        tools.Children.Add(Button("flip", "Flip legends", () => Configure(o => o with { Flipped = !o.Flipped })));
        tools.Children.Add(Button("labels", "Labels", () => Configure(o => o with { ShowLabels = !o.ShowLabels })));
        tools.Children.Add(Button("format", "Number format", () => Configure(o => o with { LabelFormatting = o.LabelFormatting == "G4" ? "0.0" : "G4" })));
        toolbar.Children.Add(Scroll(range)); toolbar.Children.Add(Scroll(tools)); _root.Children.Add(toolbar);
        var content = new Grid(); content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new() { Width = new GridLength(155) });
        content.Children.Add(_chart); Grid.SetColumn(_vertical, 1); content.Children.Add(_vertical);
        Grid.SetRow(content, 1); _root.Children.Add(content);
        Grid.SetRow(_horizontal, 2); _root.Children.Add(_horizontal);
        Grid.SetRow(_status, 3); _root.Children.Add(_status); Content = _root;
        Register("minimum", _minimum); Register("maximum", _maximum); Register("chart", _chart);
        Register("vertical", _vertical); Register("horizontal", _horizontal);
        AutomationProperties.SetName(_minimum, "Minimum color value"); AutomationProperties.SetName(_maximum, "Maximum color value");
        _series = new() { HeatmapGrid = CreateGrid(), ColorMap = Maps[0], SeriesName = "Intensity" };
        _model.RenderableSeries.Add(_series); _model.XAxis.AxisTitle = "Position"; _model.YAxis.AxisTitle = "Sensor";
        _chart.Model = _model; _vertical.Series = _horizontal.Series = _series;
        _chart.RenderCompleted += (_, _) => Publish(); _vertical.RenderCompleted += (_, _) => Publish(); _horizontal.RenderCompleted += (_, _) => Publish();
        _vertical.HoveredValueChanged += (_, _) => Publish(); _horizontal.HoveredValueChanged += (_, _) => Publish();
        _diagnostics.Tick += (_, _) => Publish();
        Loaded += (_, _) => { _diagnostics.Start(); ApplyTheme(); Publish(); };
        Unloaded += (_, _) => _diagnostics.Stop();
    }
    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private void Configure(Func<HeatmapLegendOptions, HeatmapLegendOptions> change)
    { _vertical.Options = change(_vertical.Options); _horizontal.Options = change(_horizontal.Options); }
    private HeatmapGrid CreateGrid()
    {
        const int width = 64, height = 32;
        double[] x = Enumerable.Range(0, width + 1).Select(i => _nonUniform ? i + i * i / (double)width : (double)i).ToArray();
        double[] y = Enumerable.Range(0, height + 1).Select(i => _nonUniform ? i + i * i / (double)height : (double)i).ToArray();
        var values = new double[width * height];
        for (int row = 0; row < height; row++) for (int column = 0; column < width; column++)
            values[row * width + column] = row is > 11 and < 17 && column is > 25 and < 31 ? double.NaN
                : row == 0 && column == 0 ? 0 : row == 0 && column == 1 ? -1
                : (1 + _generation * .5) * Math.Pow(10, 3.0 * column / (width - 1)) * (1 + .15 * Math.Sin(row * .2));
        return new(x, y, values);
    }
    private void ApplyRange()
    {
        if (!double.TryParse(_minimum.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double min)
            || !double.TryParse(_maximum.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double max))
            throw new ArgumentException("Enter two finite numeric limits.");
        _series.ColorMapRange = new(min, max);
    }
    private void ReplaceGrid() { _series.HeatmapGrid = CreateGrid(); _model.ZoomExtents(); }
    private void ReplaceSeries()
    {
        var old = _series;
        _series = new() { HeatmapGrid = CreateGrid(), SeriesName = "Intensity", ColorMap = old.ColorMap,
            ColorMapRange = old.ColorMapRange, ColorScaleMode = old.ColorScaleMode, Opacity = old.Opacity };
        _model.RenderableSeries.Remove(old); _model.RenderableSeries.Add(_series);
        _vertical.Series = _horizontal.Series = _series;
        old.Dispose(); // The legends never own source-series lifetime.
    }
    private void ToggleTheme() { _light = !_light; ApplyTheme(); }
    private void ApplyTheme()
    {
        var theme = _light ? ChartTheme.Light : ChartTheme.Dark;
        RequestedTheme = _light ? ElementTheme.Light : ElementTheme.Dark;
        _model.Theme = _vertical.Theme = _horizontal.Theme = theme;
        _root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(theme.Background.A, theme.Background.R, theme.Background.G, theme.Background.B));
    }
    private Button Button(string id, string title, Action action)
    {
        var button = new Button { Content = title, FontSize = 12, MinHeight = 30, Padding = new Thickness(9, 4, 9, 4) };
        button.Click += (_, _) => { _validationError = null; try { action(); } catch (ArgumentException error) { _validationError = error.Message; } Publish(); };
        Register(id, button); return button;
    }
    private void Register(string id, FrameworkElement element) { _controls[id] = element; AutomationProperties.SetAutomationId(element, "colors-" + id); }
    private static ControlBounds? Bounds(FrameworkElement element)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;
        var p = element.TransformToVisual(null).TransformPoint(new Point(0, 0)); return new(p.X, p.Y, element.ActualWidth, element.ActualHeight);
    }
    private static ColorLegendState? State(HeatmapColorLegend legend)
    {
        if (legend.LastFrame is not { } frame || !legend.IsLoaded) return null;
        var p = legend.TransformToVisual(null).TransformPoint(new Point(0, 0));
        return new(legend.FrameCount, frame.Scale.Range.Min, frame.Scale.Range.Max, frame.Scale.Mode.ToString(), frame.Orientation.ToString(), frame.Flipped,
            legend.Options.ShowLabels, frame.Ticks.Select(t => t.Label).ToArray(), legend.HoveredValue,
            new(p.X + frame.Bar.X, p.Y + frame.Bar.Y, frame.Bar.Width, frame.Bar.Height),
            frame.Scale.ColorAtFraction(0).Hex, frame.Scale.ColorAtFraction(.5).Hex, frame.Scale.ColorAtFraction(1).Hex);
    }
    private void Publish()
    {
        if (!IsLoaded) return;
        var scale = HeatmapColorScale.Capture(_series);
        string? error = _chart.LastError ?? _vertical.LastError ?? _horizontal.LastError;
        bool Current(HeatmapColorLegend legend) => legend.LastFrame is { } frame && frame.Scale.Range == scale.Range
            && frame.Scale.Mode == scale.Mode && ReferenceEquals(frame.Scale.ColorMap, scale.ColorMap) && frame.Scale.Opacity == scale.Opacity;
        bool ready = error is null && _chart.LastFrame?.Statistics.ModelRevision == _model.Revision && Current(_vertical) && Current(_horizontal);
        _status.Text = _validationError ?? $"{scale.Mode} · {( _series.ColorMapRange is null ? "Automatic" : "Fixed")} range {scale.Range} · {MapNames[_palette]} · Move over either legend to inspect a value.";
#if __WASM__
        var state = new ColorLegendsState(ready, error, _validationError, _series.Id, _generation, _nonUniform, MapNames[_palette], _model.Theme.Name,
            scale.Mode.ToString(), scale.Opacity, _series.ColorMapRange is null, scale.Range.Min, scale.Range.Max,
            State(_vertical), State(_horizontal), _controls.ToDictionary(p => p.Key, p => Bounds(p.Value)));
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartSceneColors=" + JsonSerializer.Serialize(state, ColorLegendsJsonContext.Default.ColorLegendsState));
#endif
    }
}
public sealed record ColorLegendState(long frames, double minimum, double maximum, string scale, string orientation, bool flipped,
    bool labels, string[] tickLabels, double? hovered, ControlBounds bar, string lowColor, string midColor, string highColor);
public sealed record ColorLegendsState(bool ready, string? error, string? validationError, string seriesId, int generation, bool nonUniform,
    string palette, string theme, string scale, double opacity, bool automatic, double minimum, double maximum,
    ColorLegendState? vertical, ColorLegendState? horizontal, Dictionary<string, ControlBounds?> controls);
[JsonSerializable(typeof(ColorLegendsState))]
public partial class ColorLegendsJsonContext : JsonSerializerContext;
