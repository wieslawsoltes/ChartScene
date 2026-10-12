using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChartScene.Charting;
using ChartScene.Controls;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;
using ChartScene.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace ChartScene.App;

/// <summary>Native Uno consumption of the reusable point modifier, surface and external legend.</summary>
public sealed class EditingPage : Page
{
    private readonly Grid _root = new(), _workspace = new();
    private readonly ChartSurface _chart = new();
    private readonly ChartLegend _legend = new();
    private readonly DataPointEditModifier _editor = new() { IsEnabled = true, HitRadius = 14 };
    private readonly Dictionary<string, FrameworkElement> _controls = [];
    private readonly Dictionary<string, ChartToolButton> _commands = [];
    private readonly TextBlock _title = new() { Text = "Select and drag data points", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _description = new() { Text = "Original samples · live Y editing · guarded undo and redo", FontSize = 12 };
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(12, 5, 12, 5), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _point = new() { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _details = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Border _inspector = new() { Padding = new Thickness(14), Margin = new Thickness(8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
    private UndoManager? _observedHistory;
    private readonly DispatcherTimer _diagnostics = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly ChartTheme _dark = ChartTheme.Dark with { Background = new(21, 26, 38), PlotBackground = new(25, 42, 65), Border = new(49, 59, 77) };
    private static readonly SeriesKind[] Kinds = [SeriesKind.Line, SeriesKind.Spline, SeriesKind.Scatter, SeriesKind.Column, SeriesKind.Bar, SeriesKind.Impulse];
    private ChartModel? _model;
    private XyDataSeries? _data;
    private RenderableSeries? _series;
    private int _kind, _generation, _lastIndex = 8;
    private bool _light, _logarithmic, _rotated, _flipped, _showInspector = true;
    private long _actions;
    private string _lastAction = "";

    public EditingPage()
    {
        RequestedTheme = ElementTheme.Dark;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Padding = new Thickness(12, 10, 12, 8), Spacing = 3 };
        heading.Children.Add(_title); heading.Children.Add(_description); _root.Children.Add(heading);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Padding = new Thickness(12, 2, 12, 8) };
        void Command(string id, string text, ChartGlyph glyph, Action execute)
        {
            var button = new ChartToolButton(glyph, id is "edit" or "kind" ? text : ""); button.Describe(text);
            button.Click += (_, _) => Act(id, execute);
            _commands.Add(id, button); Register(id, button); tools.Children.Add(button);
        }
        Command("edit", "Edit points", ChartGlyph.Annotate, () => { _chart.Controller.Cancel(); _editor.IsEnabled = !_editor.IsEnabled; });
        Command("kind", "Line", ChartGlyph.Line, () => { _kind = (_kind + 1) % Kinds.Length; LoadModel(); });
        Command("undo", "Undo data edit", ChartGlyph.Undo, () => _chart.Controller.History.Undo());
        Command("redo", "Redo data edit", ChartGlyph.Redo, () => _chart.Controller.History.Redo());
        Command("reset", "Reset source data", ChartGlyph.Home, LoadModel);
        Command("log", "Logarithmic value axis", ChartGlyph.Grid, () => { _chart.Controller.Cancel(); _logarithmic = !_logarithmic; ConfigureAxes(); });
        Command("rotate", "Rotate axes", ChartGlyph.Forward, () => { _chart.Controller.Cancel(); _rotated = !_rotated; ConfigureAxes(); });
        Command("flip", "Flip value axis", ChartGlyph.Columns, () => { _chart.Controller.Cancel(); _flipped = !_flipped; ConfigureAxes(); });
        Command("external", "External source update (F6)", ChartGlyph.Play, ExternalUpdate);
        Command("theme", "Light / dark", ChartGlyph.Settings, () => { _chart.Controller.Cancel(); _light = !_light; ApplyTheme(); });
        Command("info", "Show / hide point inspector", ChartGlyph.Info, () => { _chart.Controller.Cancel(); _showInspector = !_showInspector; ArrangeInspector(); });
        var toolbar = new ScrollViewer { Content = tools, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(toolbar, 1); _root.Children.Add(toolbar); Register("toolbar", toolbar);
        _workspace.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _workspace.ColumnDefinitions.Add(new() { Width = new GridLength(240) });
        _workspace.Children.Add(_chart);
        _legend.ParentSurface = _chart;
        var properties = new StackPanel { Spacing = 8 };
        properties.Children.Add(new TextBlock { Text = "DATA POINT", FontSize = 11, Opacity = .7 });
        properties.Children.Add(_point); properties.Children.Add(_details);
        properties.Children.Add(new TextBlock { Text = "SERIES", FontSize = 11, Opacity = .7, Margin = new Thickness(0, 20, 0, 2) });
        properties.Children.Add(_legend);
        properties.Children.Add(new TextBlock { Text = "Drag a marker along the value axis. Escape cancels. Control-Z / Control-Y undo and redo.\n\nF6 simulates an external data update; stale edits must not overwrite it.", FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0) });
        _inspector.Child = new ScrollViewer { Content = properties, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(_inspector, 1); _workspace.Children.Add(_inspector);
        Grid.SetRow(_workspace, 2); _root.Children.Add(_workspace); Grid.SetRow(_status, 3); _root.Children.Add(_status); Content = _root;
        Register("chart", _chart); Register("inspector", _inspector);
        AutomationProperties.SetHelpText(_chart, "In Edit points mode, drag an original marker to change its Y value. Escape cancels. Control-Z undoes. Control-Y redoes. F6 applies an external source update.");
        _editor.Changed += (_, _) => { if (_editor.ActiveIndex >= 0) _lastIndex = _editor.ActiveIndex; UpdatePresentation(); Publish(); };
        _chart.RenderCompleted += (_, _) => { UpdatePresentation(); Publish(); };
        _diagnostics.Tick += (_, _) => Publish();
        KeyDown += (_, e) => { if (e.Key == VirtualKey.F6) { Act("external", ExternalUpdate); e.Handled = true; } };
        Loaded += (_, _) => { if (_model is null) LoadModel(); ArrangeInspector(); ApplyTheme(); ObserveHistory(); _diagnostics.Start(); };
        Unloaded += (_, _) => { _diagnostics.Stop(); ObserveHistory(detach: true); _chart.Controller.Cancel(); };
        SizeChanged += (_, _) => { ArrangeInspector(); Publish(); };
    }
    private void ObserveHistory(bool detach = false)
    {
        var next = !detach && IsLoaded && _model is not null ? _chart.Controller.History : null;
        if (ReferenceEquals(next, _observedHistory)) return;
        if (_observedHistory is not null) _observedHistory.Changed -= HistoryChanged;
        _observedHistory = next;
        if (next is not null) next.Changed += HistoryChanged;
    }
    private void HistoryChanged(object? sender, EventArgs args)
    {
        if (!IsLoaded || !ReferenceEquals(sender, _observedHistory)) return;
        UpdatePresentation(); Publish();
    }
    private void Register(string id, FrameworkElement element)
    { _controls[id] = element; AutomationProperties.SetAutomationId(element, "editing-" + id); UiInputTrace.Register(element, "editing-" + id); }
    private void Act(string id, Action execute)
    { _actions++; _lastAction = id; execute(); UpdatePresentation(); Publish(); }
    private void LoadModel()
    {
        var previous = _model ?? _chart.Model;
        var data = new XyDataSeries();
        double[] x = Enumerable.Range(0, 17).Select(i => (double)i).ToArray();
        double[] y = x.Select(i => 50 + 24 * Math.Sin(i * .6)).ToArray(); y[8] = 50;
        data.AppendRange(x, y);
        var theme = _light ? ChartTheme.Light : _dark;
        var model = new ChartModel { Title = "Editable " + Kinds[_kind], Subtitle = "Drag a source marker · X remains unchanged", ShowLegend = false, Theme = theme };
        var series = new RenderableSeries(Kinds[_kind]) { Id = "editable", SeriesName = "Editable samples", DataSeries = data,
            PointMarker = PointMarker.Ellipse, MarkerSize = 9, StrokeThickness = 2.5, Stroke = theme.Palette[0], Fill = theme.Palette[0].WithAlpha(90) };
        model.RenderableSeries.Add(series);
        _chart.Model = model; _chart.Controller.Modifiers.Insert(0, _editor);
        _model = model; _series = series; _data = data; _generation++; _lastIndex = 8;
        ConfigureAxes(); previous.Dispose(); ObserveHistory(); UpdatePresentation();
    }
    private AxisModel ValueAxis => Kinds[_kind] == SeriesKind.Bar ? _model!.XAxis : _model!.YAxis;
    private void ConfigureAxes()
    {
        if (_model is null) return;
        using (_model.SuspendUpdates())
        {
            _model.XAxis.AutoRange = _model.YAxis.AutoRange = AutoRange.Never;
            _model.XAxis.Scale = _model.YAxis.Scale = AxisScale.Numeric;
            _model.XAxis.AxisAlignment = _rotated ? AxisAlignment.Left : AxisAlignment.Bottom;
            _model.YAxis.AxisAlignment = _rotated ? AxisAlignment.Bottom : AxisAlignment.Left;
            _model.XAxis.FlippedCoordinates = _model.YAxis.FlippedCoordinates = false;
            _model.XAxis.VisibleRange = _model.YAxis.VisibleRange = new(0, 16);
            ValueAxis.VisibleRange = _logarithmic ? new(1, 100) : new(0, 100);
            if (_logarithmic) ValueAxis.Scale = AxisScale.Logarithmic;
            ValueAxis.FlippedCoordinates = _flipped;
            _model.XAxis.AxisTitle = Kinds[_kind] == SeriesKind.Bar ? "Value" : "Sample X";
            _model.YAxis.AxisTitle = Kinds[_kind] == SeriesKind.Bar ? "Sample X" : "Value";
        }
    }
    private void ExternalUpdate()
    {
        if (_data is null) return;
        var snapshot = _data.Snapshot();
        _data.Update(8, snapshot[0, 8], 82); // ordinary external mutation, not an editor/history operation
    }
    private void ArrangeInspector()
    {
        bool visible = _showInspector && ActualWidth >= 1000;
        _inspector.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _workspace.ColumnDefinitions[1].Width = new GridLength(visible ? 240 : 0);
    }
    private static SolidColorBrush Brush(ColorRgba c) => new(Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B));
    private void ApplyTheme()
    {
        var theme = _light ? ChartTheme.Light : _dark;
        RequestedTheme = _light ? ElementTheme.Light : ElementTheme.Dark;
        _root.Background = _inspector.Background = Brush(theme.Background); _inspector.BorderBrush = Brush(theme.Border);
        _title.Foreground = _point.Foreground = _details.Foreground = Brush(theme.Foreground);
        _description.Foreground = _status.Foreground = Brush(theme.Muted);
        if (_model is not null) _model.Theme = theme;
        foreach (var command in _commands.Values) command.ApplyTheme(theme);
    }
    private void UpdatePresentation()
    {
        if (_model is null || _data is null) return;
        var snapshot = _data.Snapshot(); int index = Math.Clamp(_lastIndex, 0, snapshot.Count - 1);
        _point.Text = snapshot[1, index].ToString("G7", CultureInfo.InvariantCulture);
        _details.Text = $"Index {index}\nX = {snapshot[0, index]:G7}\nY = {snapshot[1, index]:G7}\n\n{_editor.LastOutcome}\nData revision {snapshot.Revision}";
        _status.Text = _chart.LastError ?? $"{(_editor.IsEnabled ? "EDIT POINTS" : "PAN CHART")} · {_editor.CommittedEdits} committed edits · {_editor.Conflicts} conflicts · {_editor.LastOutcome}";
        _commands["edit"].IsActive = _editor.IsEnabled; _commands["edit"].Label = _editor.IsEnabled ? "Edit points" : "Pan chart";
        _commands["kind"].Label = Kinds[_kind].ToString();
        _commands["undo"].IsEnabled = _chart.Controller.History.CanUndo; _commands["redo"].IsEnabled = _chart.Controller.History.CanRedo;
        _commands["log"].IsActive = _logarithmic; _commands["rotate"].IsActive = _rotated; _commands["flip"].IsActive = _flipped;
        _commands["info"].IsActive = _showInspector;
    }
    private static ControlBounds? Bounds(FrameworkElement e)
    {
        if (!e.IsLoaded || e.Visibility != Visibility.Visible || e.ActualWidth <= 0 || e.ActualHeight <= 0) return null;
        var p = e.TransformToVisual(null).TransformPoint(new Point(0, 0)); return new(p.X, p.Y, e.ActualWidth, e.ActualHeight);
    }
    private void Publish()
    {
#if __WASM__
        if (!IsLoaded || _model is null || _series is null || _data is null || _chart.LastFrame is not { } frame || Bounds(_chart) is not { } chart) return;
        var origin = _chart.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var projection = frame.Layout.Projection(_model, _series); var data = _data.Snapshot(); var plot = frame.Layout.PlotArea;
        var points = new EditablePointSnapshot[data.Count];
        for (int i = 0; i < points.Length; i++)
        {
            var p = projection.ToScreen(data[0, i], data[1, i]);
            points[i] = new(i, data[0, i], data[1, i], p.IsFinite ? p.X + origin.X : null, p.IsFinite ? p.Y + origin.Y : null);
        }
        var state = new EditingSnapshot(_chart.LastError is null && frame.Statistics.ModelRevision == _model.Revision, _chart.LastError,
            _generation, Kinds[_kind].ToString(), _model.Theme.Name, _editor.IsEnabled, _editor.IsEditing, _editor.LastOutcome.ToString(),
            _editor.CommittedEdits, _editor.Conflicts, data.Revision, _chart.Controller.History.CanUndo, _chart.Controller.History.CanRedo,
            _actions, _lastAction, ValueAxis.IsVertical, _logarithmic, _rotated, _flipped, _chart.FrameCount,
            new(origin.X + plot.X, origin.Y + plot.Y, plot.Width, plot.Height), points,
            _controls.ToDictionary(p => p.Key, p => Bounds(p.Value)), _commands["undo"].IsEnabled, _commands["redo"].IsEnabled);
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartSceneEditing=" + JsonSerializer.Serialize(state, EditingJsonContext.Default.EditingSnapshot));
#endif
    }
}
public sealed record EditablePointSnapshot(int index, double x, double y, double? screenX, double? screenY);
public sealed record EditingSnapshot(bool ready, string? error, int generation, string kind, string theme, bool enabled, bool editing, string outcome,
    long commits, long conflicts, long dataRevision, bool canUndo, bool canRedo, long actions, string lastAction, bool valueVertical,
    bool logarithmic, bool rotated, bool flipped, long frames, ControlBounds plot, EditablePointSnapshot[] points, Dictionary<string, ControlBounds?> controls, bool undoEnabled, bool redoEnabled);
[JsonSerializable(typeof(EditingSnapshot))]
public partial class EditingJsonContext : JsonSerializerContext;
