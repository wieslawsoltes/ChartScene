using System.Text.Json;
using System.Text.Json.Serialization;
using ChartScene.Controls;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using ChartScene.Samples;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ChartScene.App;

public sealed partial class MainPage
{
    private const int CataloguePageSize = 12;
    private readonly ChartThumbnailCache _thumbnails = new();
    private readonly Dictionary<string, ChartThumbnail> _previews = [];
    private readonly TextBlock _cataloguePosition = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private ChartToolButton _cataloguePrevious = null!, _catalogueNext = null!;
    private int _cataloguePage;
    private bool _cataloguePopulated;

    private void BuildCatalogue()
    {
        var content = new StackPanel { Spacing = 16, Padding = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = "Chart examples", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = "Explore real chart previews. Every miniature uses the same chart renderer and sample data as its interactive example.",
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Opacity = .75 });
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _cataloguePrevious = Button("catalogue-previous", ChartGlyph.Back, "Previous", () => PopulateCatalogue(_cataloguePage - 1), "Previous page of chart examples");
        _catalogueNext = Button("catalogue-next", ChartGlyph.Forward, "Next", () => PopulateCatalogue(_cataloguePage + 1), "Next page of chart examples");
        navigation.Children.Add(_cataloguePrevious); navigation.Children.Add(_cataloguePosition); navigation.Children.Add(_catalogueNext);
        content.Children.Add(navigation); content.Children.Add(_tiles);
        _catalogue = new ScrollViewer { Content = content, Visibility = Visibility.Collapsed, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _center.Children.Add(_catalogue);
        // A closed catalogue has no preview controls and invokes no sample factories.
        Unloaded += (_, _) => _thumbnails.Clear();
    }

    private void PopulateCatalogue(int page)
    {
        int pages = (SampleCatalog.Examples.Count + CataloguePageSize - 1) / CataloguePageSize;
        page = Math.Clamp(page, 0, Math.Max(0, pages - 1));
        if (_cataloguePopulated && _cataloguePage == page) return;
        _cataloguePage = page; _cataloguePopulated = true;
        _tiles.Children.Clear(); _previews.Clear();
        foreach (string id in _controls.Keys.Where(id => id.StartsWith("tile:", StringComparison.Ordinal) || id.StartsWith("preview:", StringComparison.Ordinal)).ToArray())
            _controls.Remove(id);
        var theme = _model?.Theme ?? _examplesDark;
        foreach (var example in SampleCatalog.Examples.Skip(page * CataloguePageSize).Take(CataloguePageSize))
        {
            var preview = new ChartThumbnail { Factory = example.Create, Theme = theme, SharedCache = _thumbnails, Height = 144,
                HorizontalAlignment = HorizontalAlignment.Stretch };
            var error = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            preview.RenderCompleted += (_, _) =>
            {
                error.Text = preview.LastError is null ? "" : "Preview unavailable: " + preview.LastError;
                error.Visibility = preview.LastError is null ? Visibility.Collapsed : Visibility.Visible;
                PublishCatalogue();
            };
            _previews.Add(example.Id, preview); Register("preview:" + example.Id, preview);
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(preview);
            panel.Children.Add(new TextBlock { Text = example.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(new TextBlock { Text = example.Category, FontSize = 10, Opacity = .7 });
            panel.Children.Add(error);
            var button = new Button { Content = panel, Tag = example, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10), Margin = new Thickness(0, 0, 12, 12),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
            button.Click += (_, _) => OpenExample(example);
            AutomationProperties.SetName(button, example.Title);
            ToolTipService.SetToolTip(button, example.Description);
            Register("tile:" + example.Id, button); _tiles.Children.Add(button);
        }
        _cataloguePosition.Text = $"{page * CataloguePageSize + 1}–{Math.Min((page + 1) * CataloguePageSize, SampleCatalog.Examples.Count)} of {SampleCatalog.Examples.Count} examples";
        _cataloguePrevious.IsEnabled = page > 0; _catalogueNext.IsEnabled = page + 1 < pages;
        _tileColumns = 0; ResizeWorkspace(); _catalogue.ChangeView(null, 0, null, true);
        PublishCatalogue();
    }

    private void UpdateCatalogueTheme()
    {
        if (_model is null) return;
        foreach (var preview in _previews.Values) preview.Theme = _model.Theme;
    }
    private void PublishCatalogue()
    {
#if __WASM__
        if (!IsLoaded) return;
        var stats = _thumbnails.Statistics;
        var snapshot = new CatalogueSnapshot(_cataloguePage,
            (SampleCatalog.Examples.Count + CataloguePageSize - 1) / CataloguePageSize,
            _cataloguePopulated, stats.Builds, stats.Hits, stats.Evictions, stats.Entries, stats.RetainedPixelBytes,
            _previews.Select(pair => new CataloguePreviewSnapshot(pair.Key, pair.Value.IsReady, pair.Value.LastError, Bounds(pair.Value))).ToArray());
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartSceneCatalogue=" + JsonSerializer.Serialize(snapshot, CatalogueJsonContext.Default.CatalogueSnapshot));
#endif
    }
}
public sealed record CataloguePreviewSnapshot(string id, bool ready, string? error, ControlBounds? bounds);
public sealed record CatalogueSnapshot(int page, int pages, bool populated, long builds, long hits, long evictions, int entries, long pixelBytes, CataloguePreviewSnapshot[] previews);
[JsonSerializable(typeof(CatalogueSnapshot))]
public partial class CatalogueJsonContext : JsonSerializerContext;
