using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Controls;
using ChartScene.ThreeD;
using ChartScene.Samples3D;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
namespace ChartScene.App;

public sealed class ThreeDPage : Page
{
    private readonly Grid _root = new();
    private readonly ChartSurface3D _chart = new() { WindowProvider = () => App.MainWindow! };
    private readonly ComboBox _examples = new() { MinWidth = 185, FontSize = 12 };
    private readonly ComboBox _surface = new() { MinWidth = 170, FontSize = 12 };
    private readonly ComboBox _theme = new() { MinWidth = 100, FontSize = 12 };
    private readonly CheckBox _grid = new() { Content = "Grid", IsChecked = true, MinHeight = 28 };
    private readonly Button _projection = new() { Content = "Perspective", MinHeight = 30, FontSize = 12 };
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Dictionary<string, FrameworkElement> _controls = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _index = 4;
    private bool _changing;
    private ChartModel3D? _model;
    public ThreeDPage()
    {
        RequestedTheme = ElementTheme.Dark;
        _root.RowDefinitions.Add(new() { Height = new GridLength(48) });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = new GridLength(28) });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Padding = new Thickness(12, 6, 12, 6) };
        foreach (var e in SampleCatalog3D.Examples) _examples.Items.Add(e.Title);
        toolbar.Children.Add(_examples); Register("examples", _examples);
        Add(toolbar, "previous", "‹", () => Load((_index + SampleCatalog3D.Examples.Count - 1) % SampleCatalog3D.Examples.Count));
        Add(toolbar, "next", "›", () => Load((_index + 1) % SampleCatalog3D.Examples.Count));
        Add(toolbar, "fit", "Fit", _chart.ZoomExtents); Add(toolbar, "undo", "↶", _chart.Undo); Add(toolbar, "redo", "↷", _chart.Redo);
        toolbar.Children.Add(_projection); Register("projection", _projection);
        _projection.Click += (_, _) => { _chart.SetProjection(_chart.Model.Camera.Projection == CameraProjection.Perspective ? CameraProjection.Orthographic : CameraProjection.Perspective); Update(); };
        foreach (var mode in Enum.GetValues<SurfaceDrawMode>()) _surface.Items.Add(mode);
        toolbar.Children.Add(_surface); Register("surface", _surface);
        toolbar.Children.Add(_grid); Register("grid", _grid);
        foreach (string theme in new[] { "Dark", "Light", "High contrast" }) _theme.Items.Add(theme);
        _theme.SelectedIndex = 0; toolbar.Children.Add(_theme); Register("theme", _theme);
        var scroll = new ScrollViewer { Content = toolbar, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _root.Children.Add(scroll); Grid.SetRow(_chart, 1); _root.Children.Add(_chart); Register("chart", _chart);
        Grid.SetRow(_status, 2); _root.Children.Add(_status); Content = _root;
        _examples.SelectionChanged += (_, _) => { if (!_changing && _examples.SelectedIndex >= 0) Load(_examples.SelectedIndex); };
        _surface.SelectionChanged += (_, _) => { if (!_changing && _model is not null && _surface.SelectedItem is SurfaceDrawMode mode) foreach (var s in _model.RenderableSeries) s.SurfaceDrawMode = mode; };
        _grid.Checked += (_, _) => { if (!_changing && _model is not null) _model.ShowGrid = true; }; _grid.Unchecked += (_, _) => { if (!_changing && _model is not null) _model.ShowGrid = false; };
        _theme.SelectionChanged += (_, _) => ApplyTheme();
        _surface.DropDownOpened += (_, _) => Update(); _surface.DropDownClosed += (_, _) => Update();
        _theme.DropDownOpened += (_, _) => Update(); _theme.DropDownClosed += (_, _) => Update();
        _chart.RenderCompleted += (_, _) => Update();
        _timer.Tick += (_, _) => Update();
        Loaded += (_, _) => { if (_model is null) Load(_index); _timer.Start(); }; Unloaded += (_, _) => _timer.Stop();
        SizeChanged += (_, _) => Update();
    }
    private void Add(StackPanel panel, string id, string text, Action action)
    {
        var button = new Button { Content = text, MinHeight = 30, FontSize = 12, Padding = new Thickness(11, 3, 11, 3) };
        button.Click += (_, _) => action(); panel.Children.Add(button); Register(id, button);
    }
    private void Register(string id, FrameworkElement control) { _controls[id] = control; AutomationProperties.SetAutomationId(control, "3d-" + id); UiInputTrace.Register(control,"3d-"+id); }
    private void Load(int index)
    {
        _changing = true;
        try
        {
            _index = index; var previous = _model; _model = SampleCatalog3D.Examples[index].Create();
            _chart.Model = _model; previous?.Dispose(); _examples.SelectedIndex = index;
            _surface.SelectedItem = _model.RenderableSeries[0].SurfaceDrawMode; _grid.IsChecked = _model.ShowGrid;
            ApplyTheme(); _chart.RequestRender();
        }
        finally { _changing = false; }
        Update();
    }
    private void ApplyTheme()
    {
        if (_model is null) return;
        _model.Theme = _theme.SelectedIndex == 1 ? ChartTheme.Light : _theme.SelectedIndex == 2 ? ChartTheme.HighContrast : ChartTheme.Dark;
        RequestedTheme = _theme.SelectedIndex == 1 ? ElementTheme.Light : ElementTheme.Dark;
        var c = _model.Theme.Background; _root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B));
        c = _model.Theme.Muted; _status.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B));
    }
    private ControlBounds? Bounds(FrameworkElement e)
    {
        if (!e.IsLoaded || e.ActualWidth < 1 || e.ActualHeight < 1 || e.Visibility != Visibility.Visible) return null;
        var p = e.TransformToVisual(null).TransformPoint(new Point(0, 0)); return new(p.X, p.Y, e.ActualWidth, e.ActualHeight);
    }
    private int FocusedIndex(ComboBox box)
    {
        if (XamlRoot is not { } root) return -1;
        DependencyObject? focused=FocusManager.GetFocusedElement(root) as DependencyObject;
        while(focused is not null)
        {
            if(focused is ComboBoxItem item) return box.IndexFromContainer(item);
            focused=VisualTreeHelper.GetParent(focused);
        }
        return -1;
    }
    private void Update()
    {
        if (_model is null || !IsLoaded) return;
        _projection.Content = _model.Camera.Projection.ToString();
        var stats = _chart.LastStatistics;
        _status.Text = _chart.LastError is { } error ? "GPU initialization/rendering error: " + error : stats is null ? "Initializing GPU chart…" : $"{_chart.Scene?.SourcePoints:N0} source points · {stats.Vertices:N0} vertices · {stats.Triangles:N0} triangles · {stats.DrawCalls} draws · {stats.UploadedBytes:N0} bytes uploaded · {stats.CpuMilliseconds:0.00} ms CPU · {stats.Driver}";
#if __WASM__
        var camera = _model.Camera;
        var state = new ThreeDDiagnosticSnapshot
        {
            ready = stats is not null && _chart.LastError is null, exampleId = SampleCatalog3D.Examples[_index].Id, error = _chart.LastError,
            frames = _chart.FrameCount, sourcePoints = _chart.Scene?.SourcePoints ?? 0, vertices = stats?.Vertices ?? 0, triangles = stats?.Triangles ?? 0,
            uploadedBytes = stats?.UploadedBytes ?? 0, drawCalls = stats?.DrawCalls ?? 0, driver = stats?.Driver ?? "", width = ActualWidth, height = ActualHeight,
            selected = _model.RenderableSeries.Sum(s => s.SelectedIndices.Count), canUndo = _chart.History.CanUndo, canRedo = _chart.History.CanRedo,
            grid = _model.ShowGrid, surfaceMode = _model.RenderableSeries[0].SurfaceDrawMode.ToString(), theme = _model.Theme.Name,
            surfaceDropDownOpen = _surface.IsDropDownOpen, themeDropDownOpen = _theme.IsDropDownOpen,
            surfaceFocusedIndex = FocusedIndex(_surface), themeFocusedIndex = FocusedIndex(_theme),
            camera = new(camera.Yaw, camera.Pitch, camera.Distance, camera.OrthographicWidth, camera.Projection.ToString(), camera.Target.X, camera.Target.Y, camera.Target.Z),
            controls = _controls.ToDictionary(p => p.Key, p => Bounds(p.Value)),
            catalog = SampleCatalog3D.Examples.Select(e => new CatalogEntryDto(e.Id, e.Title, "3D")).ToArray()
        };
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartScene3D=" + JsonSerializer.Serialize(state, Diagnostic3DJsonContext.Default.ThreeDDiagnosticSnapshot));
#endif
    }
}
