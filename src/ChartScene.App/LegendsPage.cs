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

/// <summary>Actual reusable control consumption; diagnostic objects are read-only snapshots.</summary>
public sealed class LegendsPage : Page
{
    private readonly Grid _root = new(), _plot = new();
    private readonly ChartSurface _chart = new() { MinHeight = 100, MinWidth = 100 };
    private readonly ChartLegend _legend = new();
    private readonly Border _legendHost = new() { Padding = new Thickness(10), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
    private readonly TextBlock _heading = new() { Text = "Series legends", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _description = new() { Text = "Visibility · series markers · placement · live metadata · templates", FontSize = 12 };
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(12, 6, 12, 6), TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, FrameworkElement> _controls = [];
    private readonly Dictionary<string, ChartToolButton> _buttons = [];
    private readonly DispatcherTimer _diagnostics = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly LegendTemplates _templates = new();
    private ChartModel? _model, _previous;
    private RenderableSeries? _optional;
    private int _placement, _generation, _appends, _oldEdits;
    private bool _light, _style, _customRow, _customMarker;
    private static readonly string[] Placements = ["Right", "Bottom", "Left", "Top", "Inside"];
    public LegendsPage()
    {
        RequestedTheme = ElementTheme.Dark;
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Padding = new Thickness(12, 10, 12, 8), Spacing = 3 };
        header.Children.Add(_heading); header.Children.Add(_description); _root.Children.Add(header);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Padding = new Thickness(12, 2, 12, 8) };
        void Command(string id, string label, ChartGlyph glyph, Action action)
        {
            var button = new ChartToolButton(glyph, id is "placement" or "filter" or "checks" or "markers" ? label : ""); button.Describe(label);
            button.Click += (_, _) => { action(); UpdateCommands(); Publish(); };
            _buttons.Add(id, button); Register(id, button); tools.Children.Add(button);
        }
        Command("placement", "Place: Right", ChartGlyph.Grid, () => { _placement = (_placement + 1) % Placements.Length; PlaceLegend(); });
        Command("filter", "Series: All", ChartGlyph.Select, () => _legend.SourceMode = (LegendSourceMode)(((int)_legend.SourceMode + 1) % 3));
        Command("checks", "Checkboxes", ChartGlyph.Select, () => _legend.ShowVisibilityCheckboxes = !_legend.ShowVisibilityCheckboxes);
        Command("markers", "Markers", ChartGlyph.Line, () => _legend.ShowSeriesMarkers = !_legend.ShowSeriesMarkers);
        Command("undo", "Undo", ChartGlyph.Undo, () => _chart.Controller.History.Undo());
        Command("redo", "Redo", ChartGlyph.Redo, () => _chart.Controller.History.Redo());
        Command("style", "Edit style", ChartGlyph.Settings, ChangeStyle);
        Command("exclude", "Exclude cosine", ChartGlyph.Minus, () => { var s = Find("cosine"); s.IncludeInLegend = !s.IncludeInLegend; });
        Command("select", "Select cosine", ChartGlyph.Cursor, () => { var s = Find("cosine"); s.IsSelected = !s.IsSelected; });
        Command("reorder", "Reorder", ChartGlyph.Forward, () => { if (_model is { RenderableSeries.Count: > 1 }) _model.RenderableSeries.Move(0, _model.RenderableSeries.Count - 1); });
        Command("remove", "Remove / add", ChartGlyph.Plus, ToggleOptional);
        Command("append", "Append data", ChartGlyph.Play, Append);
        Command("replace", "Replace model", ChartGlyph.Home, LoadModel);
        Command("old", "Edit old model", ChartGlyph.Info, () => { if (_previous is null) return; _previous.RenderableSeries[0].SeriesName = "Detached " + ++_oldEdits; _previous.RenderableSeries.Add(new()); });
        Command("rowTemplate", "Row template", ChartGlyph.Source, () => { _customRow = !_customRow; _legend.ItemTemplate = _customRow ? (DataTemplate)_templates["LegendRow"] : null; });
        Command("markerTemplate", "Marker template", ChartGlyph.Columns, () => { _customMarker = !_customMarker; _legend.MarkerTemplate = _customMarker ? (DataTemplate)_templates["LegendMarker"] : null; });
        Command("theme", "Light / dark", ChartGlyph.Settings, () => { _light = !_light; ApplyTheme(); });
        var scroll = new ScrollViewer { Content = tools, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); _root.Children.Add(scroll); Register("toolbar", scroll);
        for (int i = 0; i < 3; i++)
        {
            _plot.RowDefinitions.Add(new() { Height = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            _plot.ColumnDefinitions.Add(new() { Width = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        }
        Grid.SetRow(_chart, 1); Grid.SetColumn(_chart, 1); _plot.Children.Add(_chart);
        _legend.ParentSurface = _chart;
        _legendHost.Child = new ScrollViewer { Content = _legend, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _plot.Children.Add(_legendHost); Grid.SetRow(_plot, 2); _root.Children.Add(_plot);
        Grid.SetRow(_status, 3); _root.Children.Add(_status); Content = _root;
        Register("chart", _chart); Register("legend", _legendHost);
        _chart.RenderCompleted += (_, _) => Publish(); _legend.Updated += (_, _) => Publish();
        _diagnostics.Tick += (_, _) => { UpdateCommands(); Publish(); };
        Loaded += (_, _) => { if (_model is null) LoadModel(); PlaceLegend(); ApplyTheme(); _diagnostics.Start(); };
        Unloaded += (_, _) => _diagnostics.Stop();
        SizeChanged += (_, _) => { PlaceLegend(); Publish(); };
    }
    private RenderableSeries Find(string id) => _model!.RenderableSeries.First(s => s.Id == id);
    private void Register(string id, FrameworkElement element)
    { _controls[id] = element; AutomationProperties.SetAutomationId(element, "legends-" + id); }
    private static SolidColorBrush Brush(ColorRgba c) => new(Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B));
    private void PlaceLegend()
    {
        bool horizontal = _placement is 1 or 3;
        _legend.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        _legendHost.MaxWidth = horizontal ? double.PositiveInfinity : Math.Max(150, Math.Min(300, ActualWidth * .4));
        _legendHost.MaxHeight = horizontal ? 130 : Math.Max(80, ActualHeight - 170);
        _legendHost.HorizontalAlignment = _placement == 4 ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        _legendHost.VerticalAlignment = _placement == 4 ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        _legendHost.Margin = new Thickness(8);
        Grid.SetRow(_legendHost, _placement == 1 ? 2 : _placement == 3 ? 0 : 1);
        Grid.SetColumn(_legendHost, _placement == 0 ? 2 : _placement == 2 ? 0 : 1);
    }
    private void LoadModel()
    {
        _generation++; _style = false;
        var model = new ChartModel { Title = "Interactive chart legend", Subtitle = "The same series models drive the plot, legend and visibility history", ShowLegend = false, Theme = _light ? ChartTheme.Light : ChartTheme.Dark };
        string[] ids = ["sine", "cosine", "area", "scatter"];
        string[] names = ["Sine wave", "Cosine wave", "Envelope", "Samples"];
        SeriesKind[] kinds = [SeriesKind.Line, SeriesKind.Line, SeriesKind.Mountain, SeriesKind.Scatter];
        for (int j = 0; j < ids.Length; j++)
        {
            var data = new XyDataSeries(); int count = j == 3 ? 65 : 513;
            double[] x = new double[count], y = new double[count];
            for (int i = 0; i < count; i++) { x[i] = 10.0 * i / (count - 1); y[i] = Signal(x[i], j); }
            data.AppendRange(x, y);
            model.RenderableSeries.Add(new(kinds[j]) { Id = ids[j], SeriesName = names[j], DataSeries = data, Stroke = model.Theme.Palette[j], Fill = model.Theme.Palette[j].WithAlpha(65), PointMarker = j == 1 ? PointMarker.Diamond : PointMarker.None, MarkerSize = 5, IsSelected = j == 2 });
        }
        model.XAxis.VisibleRange = new(0, 10); model.YAxis.VisibleRange = new(-2, 2);
        model.XAxis.AutoRange = model.YAxis.AutoRange = AutoRange.Never;
        model.XAxis.AxisTitle = "Time (s)"; model.YAxis.AxisTitle = "Amplitude";
        _previous?.Dispose(); _previous = _model; _model = model; _chart.Model = model;
        _optional = model.RenderableSeries[^1]; _previous?.Dispose(); UpdateCommands(); ApplyTheme();
    }
    private static double Signal(double x, int series) => series switch { 1 => Math.Cos(x * 1.4), 2 => .5 * Math.Sin(x * .6) + .2, 3 => Math.Sin(x * 1.4) * .8 - .4, _ => Math.Sin(x * 1.4) };
    private void ChangeStyle()
    {
        var series = Find("sine"); _style = !_style;
        using (_model!.SuspendUpdates())
        {
            series.SeriesName = _style ? "Sine wave / updated" : "Sine wave";
            series.Stroke = _model.Theme.Palette[_style ? 4 : 0]; series.PointMarker = _style ? PointMarker.Square : PointMarker.None;
        }
    }
    private void ToggleOptional()
    {
        if (_model is null || _optional is null) return;
        if (_model.RenderableSeries.Contains(_optional)) _model.RenderableSeries.Remove(_optional); else _model.RenderableSeries.Add(_optional);
    }
    private void Append()
    {
        if (_model is null) return; _appends++;
        using (_model.SuspendUpdates()) foreach (var series in _model.RenderableSeries)
        {
            if (series.DataSeries is not XyDataSeries data) continue;
            var snapshot = data.Snapshot(); double last = snapshot[0, snapshot.Count - 1];
            using (data.SuspendUpdates()) for (int i = 1; i <= 100; i++) data.Append(last + i * .02, Math.Sin(last + i * .02));
        }
    }
    private void ApplyTheme()
    {
        var theme = _light ? ChartTheme.Light : ChartTheme.Dark;
        RequestedTheme = _light ? ElementTheme.Light : ElementTheme.Dark;
        _root.Background = Brush(theme.Background); _legendHost.Background = Brush(theme.Background); _legendHost.BorderBrush = Brush(theme.Border);
        _heading.Foreground = Brush(theme.Foreground); _description.Foreground = _status.Foreground = Brush(theme.Muted);
        if (_model is not null) _model.Theme = theme;
        foreach (var button in _buttons.Values) button.ApplyTheme(theme);
    }
    private void UpdateCommands()
    {
        _buttons["placement"].Label = "Place: " + Placements[_placement]; _buttons["filter"].Label = "Series: " + _legend.SourceMode;
        _buttons["checks"].IsActive = _legend.ShowVisibilityCheckboxes; _buttons["markers"].IsActive = _legend.ShowSeriesMarkers;
        _buttons["rowTemplate"].IsActive = _customRow; _buttons["markerTemplate"].IsActive = _customMarker;
        _buttons["old"].IsEnabled = _previous is not null;
        if (_model is null) return;
        _buttons["undo"].IsEnabled = _chart.Controller.History.CanUndo; _buttons["redo"].IsEnabled = _chart.Controller.History.CanRedo;
        _buttons["exclude"].IsActive = !Find("cosine").IncludeInLegend; _buttons["select"].IsActive = Find("cosine").IsSelected;
    }
    private static ControlBounds? Bounds(FrameworkElement? element)
    {
        if (element is null || !element.IsLoaded || element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;
        var p = element.TransformToVisual(null).TransformPoint(new Point(0, 0)); return new(p.X, p.Y, element.ActualWidth, element.ActualHeight);
    }
    private void Publish()
    {
        if (!IsLoaded || _model is null) return;
        _status.Text = _chart.LastError ?? $"{_legend.Entries.Count} legend entries · {_legend.RowCreationCount} rows created · {_legend.MembershipPassCount} membership passes · Native checkboxes retain focus on edits";
#if __WASM__
        var state = new LegendsSnapshot(_chart.LastError is null && _chart.LastFrame is not null && _legend.Entries.Count > 0,
            _chart.LastError, _generation, _appends, _oldEdits, Placements[_placement], _legend.SourceMode.ToString(), _model.Theme.Name,
            _legend.ShowVisibilityCheckboxes, _legend.ShowSeriesMarkers, _customRow, _customMarker, _legend.RowCreationCount, _legend.EntryCreationCount, _legend.MembershipPassCount,
            _chart.Controller.History.CanUndo, _chart.Controller.History.CanRedo,
            _legend.Entries.Select(e => new LegendEntrySnapshot(e.Identity, e.Series.Id, e.SeriesName, e.IsVisible, Bounds(_legend.ContainerFromEntry(e)))).ToArray(),
            _model.RenderableSeries.Select(s => new LegendSeriesSnapshot(s.Id, s.SeriesName, s.IsVisible, s.IncludeInLegend, s.IsSelected, s.DataSeries?.Count ?? 0)).ToArray(),
            _controls.ToDictionary(p => p.Key, p => Bounds(p.Value)));
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartSceneLegends=" + JsonSerializer.Serialize(state, LegendsJsonContext.Default.LegendsSnapshot));
#endif
    }
}
public sealed record LegendEntrySnapshot(long identity, string id, string name, bool visible, ControlBounds? bounds);
public sealed record LegendSeriesSnapshot(string id, string name, bool visible, bool included, bool selected, int points);
public sealed record LegendsSnapshot(bool ready, string? error, int generation, int appends, int oldEdits, string placement, string filter, string theme,
    bool checkboxes, bool markers, bool rowTemplate, bool markerTemplate, long rowsCreated, long entriesCreated, long membershipPasses, bool canUndo, bool canRedo,
    LegendEntrySnapshot[] entries, LegendSeriesSnapshot[] series, Dictionary<string, ControlBounds?> controls);
[JsonSerializable(typeof(LegendsSnapshot))]
public partial class LegendsJsonContext : JsonSerializerContext;
