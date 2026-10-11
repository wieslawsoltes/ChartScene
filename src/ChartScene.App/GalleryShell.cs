using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Windows.Foundation;
namespace ChartScene.App;

public sealed class GalleryShell : UserControl
{
    private readonly Grid _root = new();
    private readonly ContentControl _content = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private MainPage? _twoD;
    private ThreeDPage? _threeD;
    private LinkedChartsPage? _linkedCharts;
    private ColorLegendsPage? _colorLegends;
    private OverviewPage? _overviewPage;
    private StackedDataPage? _stackedPage;
    private readonly Button _two = Tab("2D CHARTING");
    private readonly Button _three = Tab("3D CHARTING");
    private readonly Button _linked = Tab("LINKED CHARTS");
    private readonly Button _colors = Tab("COLOR LEGENDS");
    private readonly Button _overview = Tab("OVERVIEW");
    private readonly Button _stacks = Tab("STACKED DATA");
    private string _view = "2d";
    private static Button Tab(string title) => new() { Content = title, FontSize = 11, MinHeight = 29, Padding = new Thickness(14, 3, 14, 3) };
    public GalleryShell()
    {
        RequestedTheme = ElementTheme.Dark; HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        _root.RowDefinitions.Add(new() { Height = new GridLength(36) }); _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Padding = new Thickness(12, 3, 12, 3), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 17, 22, 33)) };
        tabs.Children.Add(_two); tabs.Children.Add(_three); tabs.Children.Add(_linked); tabs.Children.Add(_colors); tabs.Children.Add(_overview); tabs.Children.Add(_stacks);
        tabs.Children.Add(new TextBlock { Text = "ChartScene · independent reusable chart libraries", FontSize = 11, Opacity = .6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) });
        _root.Children.Add(tabs); Grid.SetRow(_content, 1); _root.Children.Add(_content); Content = _root;
        AutomationProperties.SetAutomationId(_two, "gallery-2d"); AutomationProperties.SetAutomationId(_three, "gallery-3d"); AutomationProperties.SetAutomationId(_linked, "gallery-linked"); AutomationProperties.SetAutomationId(_colors, "gallery-colors"); AutomationProperties.SetAutomationId(_overview, "gallery-overview"); AutomationProperties.SetAutomationId(_stacks, "gallery-stacks");
        _two.Click += (_, _) => Switch("2d"); _three.Click += (_, _) => Switch("3d"); _linked.Click += (_, _) => Switch("linked"); _colors.Click += (_, _) => Switch("colors"); _overview.Click += (_, _) => Switch("overview"); _stacks.Click += (_, _) => Switch("stacks");
        Loaded += (_, _) =>
        {
            string initial = "2d";
#if __WASM__
            initial = Uno.Foundation.WebAssemblyRuntime.InvokeJS("new URLSearchParams(location.search).get('view') || '2d'");
#endif
            Switch(initial is "3d" or "linked" or "colors" or "overview" or "stacks" ? initial : "2d");
        };
        SizeChanged += (_, _) => Publish();
    }
    private void Switch(string view)
    {
        _view = view;
        if (view == "3d") _content.Content = _threeD ??= new ThreeDPage();
        else if (view == "linked") _content.Content = _linkedCharts ??= new LinkedChartsPage();
        else if (view == "colors") _content.Content = _colorLegends ??= new ColorLegendsPage();
        else if (view == "overview") _content.Content = _overviewPage ??= new OverviewPage();
        else if (view == "stacks") _content.Content = _stackedPage ??= new StackedDataPage();
        else _content.Content = _twoD ??= new MainPage();
        _two.Opacity = view == "2d" ? 1 : .6; _three.Opacity = view == "3d" ? 1 : .6; _linked.Opacity = view == "linked" ? 1 : .6; _colors.Opacity = view == "colors" ? 1 : .6; _overview.Opacity = view == "overview" ? 1 : .6; _stacks.Opacity = view == "stacks" ? 1 : .6;
        DispatcherQueue.TryEnqueue(Publish);
    }
    private void Publish()
    {
#if __WASM__
        ControlBounds? Bounds(FrameworkElement e)
        {
            if (!e.IsLoaded) return null;
            var p = e.TransformToVisual(null).TransformPoint(new Point(0, 0)); return new(p.X, p.Y, e.ActualWidth, e.ActualHeight);
        }
        var state = new ShellSnapshotDto(_view, new() { ["gallery-2d"] = Bounds(_two), ["gallery-3d"] = Bounds(_three), ["gallery-linked"] = Bounds(_linked), ["gallery-colors"] = Bounds(_colors), ["gallery-overview"] = Bounds(_overview), ["gallery-stacks"] = Bounds(_stacks) });
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartSceneGallery=" + JsonSerializer.Serialize(state, Diagnostic3DJsonContext.Default.ShellSnapshotDto));
#endif
    }
}
