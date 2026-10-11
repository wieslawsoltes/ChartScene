using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChartScene.Charting;
using ChartScene.Controls;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using ChartScene.Samples;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace ChartScene.App;

/// <summary>Independent examples browser, using reusable chart controls and a bounded navigation journal.</summary>
public sealed class MainPage : Page
{
    private enum Document { Example, Source, Catalogue }
    private sealed record Location(string ExampleId, Document Document);
    private readonly Grid _root = new(), _workspace = new(), _center = new(), _tiles = new();
    private readonly ColumnDefinition _navigationColumn = new() { Width = new GridLength(0) };
    private readonly ColumnDefinition _inspectorColumn = new() { Width = new GridLength(285) };
    private readonly ListView _navigation = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(5), BorderThickness = new Thickness(0) };
    private readonly TextBox _search = new() { PlaceholderText = "Search examples, source code and more…", MaxLength = 4096, FontSize = 12, MinWidth = 130, MaxWidth = 480, Height = 32, VerticalAlignment = VerticalAlignment.Center };
    private readonly ChartSurface _chart = new() { MinHeight = 100 };
    private readonly TextBlock _status = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _breadcrumb = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 20 };
    private readonly TextBlock _exampleTitle = new() { TextWrapping = TextWrapping.Wrap, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBox _source = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"), FontSize = 12, BorderThickness = new Thickness(0), Padding = new Thickness(16) };
    private readonly TextBlock _dataSummary = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _resultCount = new() { FontSize = 10, Margin = new Thickness(12,10,12,8) };
    private readonly ComboBox _theme = new() { MinWidth = 120, FontSize = 11, Height = 28, MinHeight = 28 };
    private readonly ComboBox _annotationKind = new() { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    private readonly TextBox _xMin = new() { PlaceholderText = "Minimum", FontSize = 11 }, _xMax = new() { PlaceholderText = "Maximum", FontSize = 11 };
    private readonly TextBox _yMin = new() { PlaceholderText = "Minimum", FontSize = 11 }, _yMax = new() { PlaceholderText = "Maximum", FontSize = 11 };
    private readonly CheckBox _legendToggle = new() { Content = "Show legend", IsChecked = true, MinHeight = 28, FontSize = 12 };
    private readonly CheckBox _cursorToggle = new() { Content = "Cursor and rollover", IsChecked = true, MinHeight = 28, FontSize = 12 };
    private readonly CheckBox _labelsToggle = new() { Content = "Data labels", MinHeight = 28, FontSize = 12 };
    private readonly Dictionary<string, FrameworkElement> _controls = [];
    private readonly Dictionary<string, ListViewItem> _rows = [];
    private readonly List<ChartToolButton> _buttons = [];
    private readonly Dictionary<ChartInteractionMode, ChartToolButton> _modeButtons = [];
    private readonly List<Border> _panels = [];
    private readonly ExampleSearchIndex _searchIndex = new(SampleCatalog.Examples);
    private readonly NavigationJournal<Location> _journal = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _streamTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly ChartTheme _examplesDark = ChartTheme.Dark with { Background = new(21,26,38), PlotBackground = new(25,42,65), Border = new(49,59,77), GridMajor = new(34,50,73), GridMinor = new(29,45,67) };
    private readonly CatalogEntryDto[] _catalogDtos = SampleCatalog.Examples.Select(e => new CatalogEntryDto(e.Id,e.Title,e.Category)).ToArray();
    private ChartToolButton _back = null!, _forward = null!, _exampleTab = null!, _sourceTab = null!, _info = null!, _play = null!;
    private ScrollViewer _catalogue = null!;
    private Grid _sourcePane = null!;
    private IReadOnlyList<ChartExample>? _shownResults;
    private ChartExample _example = SampleCatalog.Find("overview");
    private ChartModel? _model;
    private bool _changing, _streaming, _navigationOpen, _inspectorOpen = true, _inspectorPinned, _sourceLoaded;
    private int _tileColumns, _navigationBuilds;
    private long _modelLoads;
    private double _nextTime = 10;
    private string _message = "Ready";
    private DateTime _messageUntil;
    public MainPage()
    {
        RequestedTheme = ElementTheme.Dark;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        _root.RowDefinitions.Add(new() { Height = new GridLength(60) });
        _root.RowDefinitions.Add(new() { Height = new GridLength(34) });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = new GridLength(32) });
        BuildHeader(); BuildCommands(); BuildWorkspace(); BuildFooter(); Content = _root;
        Register("chart",_chart); Register("search",_search); Register("navigation",_navigation);
        _journal.Changed += (_, _) => ApplyLocation();
        _search.TextChanged += (_, _) => { RebuildNavigation(); if (!string.IsNullOrWhiteSpace(_search.Text)) { _navigationOpen = true; ResizeWorkspace(); } };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && _shownResults is { Count: > 0 }) { OpenExample(_shownResults[0]); e.Handled = true; }
            else if (e.Key == VirtualKey.Escape) { _search.Text = ""; _navigationOpen = false; ResizeWorkspace(); _chart.Focus(FocusState.Keyboard); e.Handled = true; }
        };
        _navigation.SelectionChanged += (_, _) => { if (!_changing && _navigation.SelectedItem is ListViewItem { Tag: ChartExample example }) OpenExample(example); };
        _theme.SelectionChanged += (_, _) => { if (!_changing) ApplyTheme(); };
        _annotationKind.SelectionChanged += (_, _) => { if (_model is not null && _annotationKind.SelectedItem is AnnotationKind kind) _chart.Controller.AnnotationKind = kind; };
        _legendToggle.Checked += (_, _) => SetLegend(true); _legendToggle.Unchecked += (_, _) => SetLegend(false);
        _cursorToggle.Checked += (_, _) => SetCursor(true); _cursorToggle.Unchecked += (_, _) => SetCursor(false);
        _labelsToggle.Checked += (_, _) => SetLabels(true); _labelsToggle.Unchecked += (_, _) => SetLabels(false);
        _chart.RenderCompleted += (_, _) => PublishSnapshot();
        _statusTimer.Tick += (_, _) => UpdateStatus(); _streamTimer.Tick += (_, _) => AppendStream();
        Loaded += (_, _) =>
        {
            _statusTimer.Start();
            // Reattaching a view must not throw away data, history or a user's current viewport.
            if (_model is null) { RebuildNavigation(); _journal.Navigate(new(SampleCatalog.Find(PlatformBridge.InitialExample()).Id,Document.Example)); }
            ResizeWorkspace(); PublishSnapshot();
        };
        Unloaded += (_, _) => { _statusTimer.Stop(); PauseStream(); };
        SizeChanged += (_, _) => ResizeWorkspace();
        Shortcut(VirtualKey.F,VirtualKeyModifiers.Control,()=>{_search.Focus(FocusState.Keyboard);_search.SelectAll();});
        Shortcut(VirtualKey.Left,VirtualKeyModifiers.Menu,()=>_journal.GoBack());
        Shortcut(VirtualKey.Right,VirtualKeyModifiers.Menu,()=>_journal.GoForward());
    }
    private void Shortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) => { action(); e.Handled = true; }; KeyboardAccelerators.Add(accelerator);
    }
    private ChartToolButton Button(string id,ChartGlyph glyph,string label,Action action,string help)
    {
        var button = new ChartToolButton(glyph,label); button.Describe(help);
        button.Click += (_, _) => { try { action(); } catch (Exception error) { ShowMessage(error.Message); Console.Error.WriteLine(error); } };
        _buttons.Add(button); Register(id,button); return button;
    }
    private void Register(string id,FrameworkElement element)
    { _controls[id] = element; AutomationProperties.SetAutomationId(element,id); UiInputTrace.Register(element,"browser-"+id); }
    private static SolidColorBrush Brush(ColorRgba c) => new(Windows.UI.Color.FromArgb(c.A,c.R,c.G,c.B));
    private Border Panel(UIElement child,Thickness thickness)
    {
        var border = new Border { Child = child, BorderThickness = thickness }; _panels.Add(border); return border;
    }
    private void Root(UIElement element,int row) { Grid.SetRow(element,row); _root.Children.Add(element); }
    private void BuildHeader()
    {
        var header = new Grid { Padding = new Thickness(12,8,12,8), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        _back = Button("browser-back",ChartGlyph.Back,"",()=>_journal.GoBack(),"Previous example (Alt+Left)"); navigation.Children.Add(_back);
        navigation.Children.Add(Button("browser-home",ChartGlyph.Home,"",()=>ShowDocument(Document.Catalogue),"Browse chart examples"));
        _forward = Button("browser-forward",ChartGlyph.Forward,"",()=>_journal.GoForward(),"Next example (Alt+Right)"); navigation.Children.Add(_forward); header.Children.Add(navigation);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(4,0,8,0), VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new ChartIcon { Glyph = ChartGlyph.Line, Width = 26, Height = 26, Tint = Windows.UI.Color.FromArgb(255,83,184,207) });
        brand.Children.Add(new TextBlock { Text = "CHARTSCENE", FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(brand,1); header.Children.Add(brand); Grid.SetColumn(_search,2); _search.HorizontalAlignment = HorizontalAlignment.Stretch; header.Children.Add(_search);
        var browse = Button("navigation-toggle",ChartGlyph.Menu,"",()=>{_navigationOpen=!_navigationOpen;RebuildNavigation();ResizeWorkspace();},"Toggle example navigation"); Grid.SetColumn(browse,3); header.Children.Add(browse);
        Root(Panel(header,new Thickness(0,0,0,1)),0);
    }
    private void BuildCommands()
    {
        var row = new Grid { ColumnSpacing = 6, Padding = new Thickness(12,1,8,1) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(_breadcrumb);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        _exampleTab = Button("example-tab",ChartGlyph.Line,"Example",()=>ShowDocument(Document.Example),"View interactive chart"); commands.Children.Add(_exampleTab);
        _sourceTab = Button("source-tab",ChartGlyph.Source,"Source code",()=>ShowDocument(Document.Source),"View the actual shared sample-factory C# source"); commands.Children.Add(_sourceTab);
        commands.Children.Add(Button("export-png",ChartGlyph.Download,"PNG",async()=>await ExportPngAsync(),"Download chart PNG"));
        commands.Children.Add(Button("export-csv",ChartGlyph.Download,"CSV",async()=>await ExportCsvAsync(),"Download first visible series as CSV"));
        _info = Button("info-toggle",ChartGlyph.Info,"",()=>{_inspectorPinned=true;_inspectorOpen=_inspectorColumn.Width.Value==0;ResizeWorkspace();},"Show or hide example information"); commands.Children.Add(_info);
        Grid.SetColumn(commands,1); row.Children.Add(commands); Root(Panel(row,new Thickness(0,0,0,1)),1);
    }
    private void BuildWorkspace()
    {
        _workspace.ColumnDefinitions.Add(new() { Width = new GridLength(38) }); _workspace.ColumnDefinitions.Add(_navigationColumn);
        _workspace.ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star) }); _workspace.ColumnDefinitions.Add(_inspectorColumn);
        var rail = new StackPanel { Spacing = 4, Padding = new Thickness(3,5,3,5) };
        void Mode(ChartInteractionMode mode,string id,ChartGlyph glyph,string help)
        { var button=Button(id,glyph,"",()=>SetMode(mode),help);_modeButtons.Add(mode,button);rail.Children.Add(button); }
        Mode(ChartInteractionMode.ZoomRectangle,"zoom-rectangle",ChartGlyph.Search,"Rectangle zoom (Shift+drag)");
        Mode(ChartInteractionMode.Pan,"pan",ChartGlyph.Pan,"Drag to pan");
        rail.Children.Add(Button("home",ChartGlyph.Fit,"",()=>_chart.Controller.ZoomExtents(),"Fit all visible data (Home)"));
        Mode(ChartInteractionMode.Select,"select",ChartGlyph.Select,"Select data points; Control adds to selection");
        Mode(ChartInteractionMode.DrawAnnotation,"annotate",ChartGlyph.Annotate,"Create an annotation");
        rail.Children.Add(Button("undo",ChartGlyph.Undo,"",()=>{_chart.Controller.History.Undo();_chart.RequestRender();},"Undo chart edit (Control+Z)"));
        rail.Children.Add(Button("redo",ChartGlyph.Redo,"",()=>{_chart.Controller.History.Redo();_chart.RequestRender();},"Redo chart edit (Control+Y)"));
        rail.Children.Add(Button("zoom-in",ChartGlyph.Plus,"",()=>_chart.Controller.ZoomAt(_chart.LastFrame?.Layout.PlotArea.Center??default,.8),"Zoom in"));
        rail.Children.Add(Button("zoom-out",ChartGlyph.Minus,"",()=>_chart.Controller.ZoomAt(_chart.LastFrame?.Layout.PlotArea.Center??default,1.25),"Zoom out"));
        _play=Button("play",ChartGlyph.Play,"",ToggleStreaming,"Start or pause real-time FIFO streaming");rail.Children.Add(_play);
        _workspace.Children.Add(Panel(new ScrollViewer{Content=rail,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden},new Thickness(0,0,1,0)));
        var navigation = new Grid(); navigation.RowDefinitions.Add(new() { Height = GridLength.Auto }); navigation.RowDefinitions.Add(new() { Height = new GridLength(1,GridUnitType.Star) });
        navigation.Children.Add(_resultCount);Grid.SetRow(_navigation,1);navigation.Children.Add(_navigation);
        var navPanel=Panel(navigation,new Thickness(0,0,1,0));Grid.SetColumn(navPanel,1);_workspace.Children.Add(navPanel);Register("navigation-panel",navPanel);
        _center.Children.Add(_chart);BuildSource();BuildCatalogue();Grid.SetColumn(_center,2);_workspace.Children.Add(_center);
        var inspector = new StackPanel { Spacing = 14, Padding = new Thickness(16) };
        inspector.Children.Add(_exampleTitle);inspector.Children.Add(_description);
        inspector.Children.Add(new TextBlock{Text="EXAMPLE OPTIONS",FontSize=10,Opacity=.6,Margin=new Thickness(0,10,0,0)});
        var checks = new StackPanel();checks.Children.Add(_legendToggle);checks.Children.Add(_cursorToggle);checks.Children.Add(_labelsToggle);inspector.Children.Add(checks);
        Register("legend-toggle",_legendToggle);Register("cursor-toggle",_cursorToggle);Register("labels-toggle",_labelsToggle);
        inspector.Children.Add(new TextBlock{Text="Annotation type",FontSize=11});foreach(var kind in Enum.GetValues<AnnotationKind>())_annotationKind.Items.Add(kind);_annotationKind.SelectedItem=AnnotationKind.Box;
        inspector.Children.Add(_annotationKind);Register("annotation-kind",_annotationKind);
        inspector.Children.Add(new TextBlock{Text="VISIBLE RANGE",FontSize=10,Opacity=.6});inspector.Children.Add(RangeEditor("X",_xMin,_xMax));inspector.Children.Add(RangeEditor("Y",_yMin,_yMax));
        Register("x-min",_xMin);Register("x-max",_xMax);Register("y-min",_yMin);Register("y-max",_yMax);
        inspector.Children.Add(Button("apply-ranges",ChartGlyph.Settings,"Apply ranges",ApplyRanges,"Apply finite ordered axis ranges"));
        var data=new Expander{Header="Data and rendering",Content=_dataSummary,HorizontalAlignment=HorizontalAlignment.Stretch};inspector.Children.Add(data);Register("data-expander",data);
        inspector.Children.Add(new TextBlock{Text="Independent ChartScene implementation. Full SciChart WPF compatibility is an ongoing target.",FontSize=10,TextWrapping=TextWrapping.Wrap,Opacity=.55});
        var info=Panel(new ScrollViewer{Content=inspector,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},new Thickness(1,0,0,0));Grid.SetColumn(info,3);_workspace.Children.Add(info);Register("inspector-panel",info);
        Root(_workspace,2);
    }
    private Grid RangeEditor(string label,TextBox minimum,TextBox maximum)
    {
        var grid=new Grid{ColumnSpacing=5};grid.ColumnDefinitions.Add(new(){Width=new GridLength(16)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        grid.Children.Add(new TextBlock{Text=label,FontSize=11,VerticalAlignment=VerticalAlignment.Center});Grid.SetColumn(minimum,1);Grid.SetColumn(maximum,2);grid.Children.Add(minimum);grid.Children.Add(maximum);return grid;
    }
    private void BuildSource()
    {
        _sourcePane=new Grid{Visibility=Visibility.Collapsed};_sourcePane.RowDefinitions.Add(new(){Height=GridLength.Auto});_sourcePane.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var heading=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12,Padding=new Thickness(12)};
        heading.Children.Add(new TextBlock{Text="SampleCatalog.cs  ·  shared factory source",FontSize=12,VerticalAlignment=VerticalAlignment.Center});
        heading.Children.Add(Button("download-source",ChartGlyph.Download,"C#",async()=>await DownloadSourceAsync(),"Download the exact C# sample factory"));_sourcePane.Children.Add(heading);
        Grid.SetRow(_source,1);_sourcePane.Children.Add(_source);Register("source-view",_source);_center.Children.Add(_sourcePane);
    }
    private void BuildCatalogue()
    {
        var content=new StackPanel{Spacing=18,Padding=new Thickness(24)};
        content.Children.Add(new TextBlock{Text="Chart examples",FontSize=28,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        content.Children.Add(new TextBlock{Text="Explore scientific, financial and performance examples. Search also includes descriptions and source code.",FontSize=13,TextWrapping=TextWrapping.Wrap,Opacity=.75});
        foreach(var example in SampleCatalog.Examples)
        {
            var button=new Button{Tag=example,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(12),Margin=new Thickness(0,0,8,8),MinHeight=68};
            var layout=new Grid{ColumnSpacing=12};layout.ColumnDefinitions.Add(new(){Width=new GridLength(30)});layout.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            layout.Children.Add(new ChartIcon{Glyph=example.Category=="Financial"?ChartGlyph.Columns:example.Category=="Scientific"?ChartGlyph.Grid:ChartGlyph.Line,Width=26,Height=26});
            var text=new StackPanel{Spacing=3};text.Children.Add(new TextBlock{Text=example.Title,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis});text.Children.Add(new TextBlock{Text=example.Category,FontSize=10,Opacity=.6});Grid.SetColumn(text,1);layout.Children.Add(text);button.Content=layout;
            button.Click+=(_,_)=>OpenExample(example);AutomationProperties.SetName(button,example.Title);Register("tile:"+example.Id,button);_tiles.Children.Add(button);
        }
        content.Children.Add(_tiles);_catalogue=new ScrollViewer{Content=content,Visibility=Visibility.Collapsed,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};_center.Children.Add(_catalogue);
    }
    private void BuildFooter()
    {
        var row=new Grid{Padding=new Thickness(10,0,8,0),ColumnSpacing=10};row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.Children.Add(_status);
        foreach(string theme in new[]{"Dark","Light","High contrast"})_theme.Items.Add(theme);_theme.SelectedIndex=0;Register("theme",_theme);Grid.SetColumn(_theme,1);row.Children.Add(_theme);Root(Panel(row,new Thickness(0,1,0,0)),3);
    }
    private void RebuildNavigation()
    {
        var results=_searchIndex.Search(_search.Text);
        if(ReferenceEquals(results,_shownResults))return;
        _shownResults=results;_navigationBuilds++;
        _changing=true;
        try
        {
            _navigation.Items.Clear();
            foreach(string key in _controls.Keys.Where(k=>k.StartsWith("example:",StringComparison.Ordinal)).ToArray())_controls.Remove(key);
            foreach(var example in results)
            {
                if(!_rows.TryGetValue(example.Id,out var row))
                {
                    var content=new StackPanel{Spacing=2};content.Children.Add(new TextBlock{Text=example.Title,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis});content.Children.Add(new TextBlock{Text=example.Category,FontSize=9,Opacity=.6});
                    row=new ListViewItem{Tag=example,Content=content,MinHeight=44,Padding=new Thickness(8,5,8,5)};_rows.Add(example.Id,row);
                }
                _navigation.Items.Add(row);Register("example:"+example.Id,row);
            }
            _resultCount.Text=$"{results.Count} EXAMPLES";
        }
        finally{_changing=false;}
        PublishSnapshot();
    }
    private void OpenExample(ChartExample example)
    {
        _navigationOpen=false;_journal.Navigate(new(example.Id,Document.Example));
        // Same-location selection still dismisses search results, without allocating a new chart.
        ResizeWorkspace();
    }
    private void ShowDocument(Document document)
    { if(_model is not null)_journal.Navigate(new(_example.Id,document)); }
    private void ApplyLocation()
    {
        if(!_journal.HasCurrent)return;
        var location=_journal.Current;
        if(_model is null||_example.Id!=location.ExampleId)LoadExample(SampleCatalog.Find(location.ExampleId));
        if(location.Document!=Document.Example)PauseStream();
        if(location.Document==Document.Source&&!_sourceLoaded){_source.Text=ExampleSource.FactoryCode;_sourceLoaded=true;}
        _chart.Visibility=location.Document==Document.Example?Visibility.Visible:Visibility.Collapsed;
        _sourcePane.Visibility=location.Document==Document.Source?Visibility.Visible:Visibility.Collapsed;
        _catalogue.Visibility=location.Document==Document.Catalogue?Visibility.Visible:Visibility.Collapsed;
        _back.IsEnabled=_journal.CanGoBack;_forward.IsEnabled=_journal.CanGoForward;
        _exampleTab.IsActive=location.Document==Document.Example;_sourceTab.IsActive=location.Document==Document.Source;
        ResizeWorkspace();PublishSnapshot();
    }
    private void LoadExample(ChartExample example)
    {
        _changing=true;PauseStream();
        try
        {
            var next=example.Create();var previous=_model;_example=example;_model=next;_chart.Model=next;previous?.Dispose();_modelLoads++;_nextTime=10;
            _model.AutoRangeAxes();_breadcrumb.Text="2D Charts  ›  "+example.Category+"  ›  "+example.Title;_exampleTitle.Text=example.Title;_description.Text=example.Description;
            _legendToggle.IsChecked=_model.ShowLegend;_cursorToggle.IsChecked=true;_labelsToggle.IsChecked=_model.RenderableSeries.Any(s=>s.ShowDataLabels);
            _chart.Controller.AnnotationKind=_annotationKind.SelectedItem is AnnotationKind kind?kind:AnnotationKind.Box;
            _chart.Controller.AnnotationEditRequested+=async(_,annotation)=>await EditAnnotationTextAsync(annotation);
            _dataSummary.Text=string.Join("\n",_model.RenderableSeries.Select(s=>$"{s.SeriesName}: {s.DataSeries?.Count??s.HeatmapGrid?.Values.Length??0:N0} samples · {s.Kind}"));
            SetMode(ChartInteractionMode.Pan);ApplyTheme();UpdateRangeEditors();_messageUntil=DateTime.MinValue;_chart.RequestRender();
        }
        finally{_changing=false;}
    }
    private void SetMode(ChartInteractionMode mode)
    {
        if(_model is null)return;_chart.Controller.Mode=mode;
        foreach(var pair in _modeButtons)pair.Value.IsActive=pair.Key==mode;
        PublishSnapshot();
    }
    private void ApplyTheme()
    {
        if(_model is null)return;
        var previous=_model.Theme;var next=_theme.SelectedIndex==1?ChartTheme.Light:_theme.SelectedIndex==2?ChartTheme.HighContrast:_examplesDark;
        using(_model.SuspendUpdates())
        {
            _model.Theme=next;
            ColorRgba Convert(ColorRgba color){for(int i=0;i<previous.Palette.Count;i++)if((previous.Palette[i] with{A=color.A})==color)return next.Palette[i%next.Palette.Count].WithAlpha(color.A);return color;}
            foreach(var series in _model.RenderableSeries){series.Stroke=Convert(series.Stroke);series.Fill=Convert(series.Fill);series.StrokeY1=Convert(series.StrokeY1);series.FillY1=Convert(series.FillY1);}
        }
        RequestedTheme=_theme.SelectedIndex==1?ElementTheme.Light:ElementTheme.Dark;
        foreach(var panel in _panels){panel.Background=Brush(next.Background);panel.BorderBrush=Brush(next.Border);}
        foreach(var button in _buttons)button.ApplyTheme(next);
        _root.Background=Brush(next.Background);_center.Background=Brush(next.PlotBackground);_source.Background=Brush(next.Background);_source.Foreground=Brush(next.Foreground);
        _status.Foreground=_breadcrumb.Foreground=Brush(next.Muted);_navigation.Background=Brush(next.Background);
    }
    private void SetLegend(bool value){if(!_changing&&_model is not null)_model.ShowLegend=value;}
    private void SetCursor(bool value){if(_changing||_model is null)return;_chart.Controller.View.ShowCursor=value;_chart.Controller.View.ShowRollover=value;_chart.RequestRender();}
    private void SetLabels(bool value){if(_changing||_model is null)return;using(_model.SuspendUpdates())foreach(var series in _model.RenderableSeries)series.ShowDataLabels=value;}
    private void UpdateRangeEditors()
    {
        if(_model is null)return;
        _xMin.Text=_model.XAxis.VisibleRange.Min.ToString("G8",CultureInfo.InvariantCulture);_xMax.Text=_model.XAxis.VisibleRange.Max.ToString("G8",CultureInfo.InvariantCulture);
        _yMin.Text=_model.YAxis.VisibleRange.Min.ToString("G8",CultureInfo.InvariantCulture);_yMax.Text=_model.YAxis.VisibleRange.Max.ToString("G8",CultureInfo.InvariantCulture);
    }
    private void ApplyRanges()
    {
        if(_model is null)return;
        double Parse(TextBox box)=>double.Parse(box.Text,CultureInfo.InvariantCulture);
        var x=new DoubleRange(Parse(_xMin),Parse(_xMax));var y=new DoubleRange(Parse(_yMin),Parse(_yMax));
        if(_model.XAxis.Scale==AxisScale.Logarithmic&&x.Min<=0||_model.YAxis.Scale==AxisScale.Logarithmic&&y.Min<=0)throw new ArgumentOutOfRangeException("Logarithmic ranges must be positive.");
        var before=new ViewportSnapshot(_model);
        using(_model.SuspendUpdates()){_model.XAxis.AutoRange=_model.YAxis.AutoRange=AutoRange.Never;_model.XAxis.VisibleRange=x;_model.YAxis.VisibleRange=y;}
        _chart.Controller.RecordViewport("Set ranges",before);ShowMessage("Visible ranges updated");
    }
    private void PauseStream(){_streaming=false;_streamTimer.Stop();if(_play is not null){_play.Glyph=ChartGlyph.Play;_play.IsActive=false;}}
    private void ToggleStreaming()
    {
        if(_example.Id!="live")_journal.Navigate(new("live",Document.Example));
        else if(_journal.Current.Document!=Document.Example)ShowDocument(Document.Example);
        _streaming=!_streaming;if(_streaming)_streamTimer.Start();else _streamTimer.Stop();
        _play.Glyph=_streaming?ChartGlyph.Pause:ChartGlyph.Play;_play.IsActive=_streaming;PublishSnapshot();
    }
    private void AppendStream()
    {
        if(!_streaming||_model is null)return;
        using(_model.SuspendUpdates())
        {
            for(int channel=0;channel<_model.RenderableSeries.Count;channel++)
            {
                if(_model.RenderableSeries[channel].DataSeries is not XyDataSeries data)continue;
                using(data.SuspendUpdates())for(int i=0;i<7;i++){double time=_nextTime+i*.005;data.Append(time,SampleCatalog.Signal(time,channel));}
            }
            _nextTime+=.035;_model.XAxis.VisibleRange=new(_nextTime-10,_nextTime);_model.YAxis.VisibleRange=new(-1.5,1.5);
        }
    }
    private async Task ExportPngAsync()
    {
        if(_model is null)return;
        try{byte[] image=RasterExporter.ExportPng(_model,Math.Max(300,(int)_chart.ActualWidth),Math.Max(200,(int)_chart.ActualHeight),2);ShowMessage(await PlatformBridge.ExportAsync($"ChartScene-{_example.Id}.png","image/png",image));}
        catch(Exception error){ShowMessage(error.Message);}
    }
    private async Task ExportCsvAsync()
    {
        if(_model?.RenderableSeries.FirstOrDefault(s=>s.IsVisible&&s.DataSeries is not null)?.DataSeries is not { } source){ShowMessage("No tabular data series in this example.");return;}
        var data=source.Snapshot();var text=new StringBuilder();text.AppendLine(string.Join(",",Enumerable.Range(0,data.ChannelCount).Select(i=>i==0?"X":i==1?"Y":"Channel"+i)));
        for(int i=0;i<data.Count;i++){for(int c=0;c<data.ChannelCount;c++){if(c>0)text.Append(',');if(double.IsFinite(data[c,i]))text.Append(data[c,i].ToString("R",CultureInfo.InvariantCulture));}text.AppendLine();}
        ShowMessage(await PlatformBridge.ExportAsync($"ChartScene-{_example.Id}.csv","text/csv;charset=utf-8",Encoding.UTF8.GetBytes(text.ToString())));
    }
    private async Task DownloadSourceAsync()=>ShowMessage(await PlatformBridge.ExportAsync(ExampleSource.FileName,"text/plain;charset=utf-8",Encoding.UTF8.GetBytes(ExampleSource.FactoryCode)));
    private async Task EditAnnotationTextAsync(ChartAnnotation annotation)
    {
        var input=new TextBox{Text=annotation.Text,AcceptsReturn=true,MinWidth=260};var dialog=new ContentDialog{Title="Annotation text",Content=input,PrimaryButtonText="Apply",CloseButtonText="Cancel",XamlRoot=XamlRoot};
        if(await dialog.ShowAsync()==ContentDialogResult.Primary){string before=annotation.Text,next=input.Text;_chart.Controller.History.Execute(new DelegateCommand("Edit annotation text",()=>annotation.Text=next,()=>annotation.Text=before));}
    }
    private void ShowMessage(string text){_message=text;_messageUntil=DateTime.UtcNow.AddSeconds(8);UpdateStatus();}
    private void UpdateStatus()
    {
        var frame=_chart.LastFrame;
        string text=DateTime.UtcNow<_messageUntil?_message:frame is not null?$"Skia host  ·  {frame.Statistics.SourcePoints:N0} samples  ·  {frame.Statistics.RenderedPoints:N0} rendered  ·  {frame.Statistics.CpuMilliseconds:0.00} ms CPU  ·  {(_streaming?"LIVE":"Ready")}":"Initializing chart…";
        if(_status.Text!=text)_status.Text=text;PublishSnapshot();
    }
    private void ResizeWorkspace()
    {
        if(!_controls.ContainsKey("inspector-panel"))return;
        double width=ActualWidth;
        bool catalogue=_journal.HasCurrent&&_journal.Current.Document==Document.Catalogue;
        _navigationColumn.Width=new GridLength(_navigationOpen?Math.Min(235,Math.Max(160,width*.3)):0);
        bool showInfo=_inspectorOpen&&!catalogue&&(width>=1150||_inspectorPinned);
        _inspectorColumn.Width=new GridLength(showInfo?Math.Min(285,width*.45):0);
        _controls["navigation-panel"].Visibility=_navigationOpen?Visibility.Visible:Visibility.Collapsed;
        _controls["inspector-panel"].Visibility=showInfo?Visibility.Visible:Visibility.Collapsed;_info.IsActive=showInfo;
        int columns=Math.Clamp((int)Math.Max(1,(width-100)/250),1,5);
        if(columns!=_tileColumns)
        {
            _tileColumns=columns;_tiles.ColumnDefinitions.Clear();_tiles.RowDefinitions.Clear();
            for(int i=0;i<columns;i++)_tiles.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            for(int i=0;i<(SampleCatalog.Examples.Count+columns-1)/columns;i++)_tiles.RowDefinitions.Add(new(){Height=GridLength.Auto});
            for(int i=0;i<_tiles.Children.Count;i++){Grid.SetColumn(_tiles.Children[i],i%columns);Grid.SetRow(_tiles.Children[i],i/columns);}
        }
        PublishSnapshot();
    }
    private ControlBounds? Bounds(FrameworkElement element)
    {
        if(!element.IsLoaded||element.ActualWidth<=0||element.ActualHeight<=0)return null;
        for(DependencyObject? current=element;current is not null;current=VisualTreeHelper.GetParent(current))
            if(current is UIElement ui&&ui.Visibility!=Visibility.Visible)return null;
        var p=element.TransformToVisual(null).TransformPoint(new Point(0,0));return new(p.X,p.Y,element.ActualWidth,element.ActualHeight);
    }
    private void PublishSnapshot()
    {
        if(!IsLoaded||_model is null)return;
        var frame=_chart.LastFrame;
        PlatformBridge.Publish(new DiagnosticSnapshot
        {
            ready=frame is not null&&_chart.LastError is null,exampleId=_example.Id,theme=_model.Theme.Name,mode=_chart.Controller.Mode.ToString(),streaming=_streaming,
            frames=_chart.FrameCount,error=_chart.LastError,renderer=_chart.RendererDescription,width=ActualWidth,height=ActualHeight,plot=frame?.Layout.PlotArea,
            ranges=new(new(_model.XAxis.VisibleRange.Min,_model.XAxis.VisibleRange.Max),new(_model.YAxis.VisibleRange.Min,_model.YAxis.VisibleRange.Max)),statistics=frame?.Statistics,
            series=_model.RenderableSeries.Select(s=>new SeriesSnapshotDto(s.Id,s.SeriesName,s.Kind.ToString(),s.IsVisible,s.SelectedIndices.Count,s.DataSeries?.Count??s.HeatmapGrid?.Values.Length??0,s.DataSeries?.Revision??0)).ToArray(),
            annotations=_model.Annotations.Select(a=>new AnnotationSnapshotDto(a.Id,a.Kind.ToString(),a.X1,a.Y1,a.X2,a.Y2,a.IsSelected)).ToArray(),hits=_chart.Controller.View.Hits.Count,canUndo=_chart.Controller.History.CanUndo,canRedo=_chart.Controller.History.CanRedo,
            controls=_controls.ToDictionary(p=>p.Key,p=>Bounds(p.Value)),catalog=_catalogDtos
        });
#if __WASM__
        var state=new BrowserSnapshot(_journal.HasCurrent?_journal.Current.Document.ToString():"Example",_journal.CanGoBack,_journal.CanGoForward,_modelLoads,_navigationBuilds,_searchIndex.Statistics.Executions,_shownResults?.Count??0,_sourceLoaded);
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartSceneBrowser="+JsonSerializer.Serialize(state,BrowserJsonContext.Default.BrowserSnapshot));
#endif
    }
}
public sealed record BrowserSnapshot(string document,bool canBack,bool canForward,long modelLoads,int navigationBuilds,long searchExecutions,int resultCount,bool sourceLoaded);
[JsonSerializable(typeof(BrowserSnapshot))]
public partial class BrowserJsonContext:JsonSerializerContext;
