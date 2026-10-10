using System.Globalization;
using System.Text;
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
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ChartScene.App;

public sealed class MainPage:Page
{
    private readonly Grid _root=new();
    private readonly Grid _workspace=new();
    private readonly ColumnDefinition _navigationColumn=new(){Width=new GridLength(230)};
    private readonly ColumnDefinition _inspectorColumn=new(){Width=new GridLength(285)};
    private readonly ListView _navigation=new(){SelectionMode=ListViewSelectionMode.Single};
    private readonly TextBox _search=new(){PlaceholderText="Search examples",FontSize=12,Margin=new Thickness(12,8,12,10)};
    private readonly ChartSurface _chart=new(){MinHeight=180};
    private readonly TextBlock _status=new(){FontSize=11,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock _breadcrumb=new(){FontSize=12,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock _description=new(){TextWrapping=TextWrapping.Wrap,FontSize=12,LineHeight=20};
    private readonly TextBlock _exampleTitle=new(){TextWrapping=TextWrapping.Wrap,FontSize=20,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold};
    private readonly TextBox _source=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,FontFamily=new FontFamily("Consolas"),FontSize=11,MinHeight=220,BorderThickness=new Thickness(0),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent)};
    private readonly TextBlock _dataSummary=new(){FontSize=11,TextWrapping=TextWrapping.Wrap};
    private readonly ComboBox _theme=new(){MinWidth=105,FontSize=12};
    private readonly ComboBox _annotationKind=new(){MinWidth=110,FontSize=12};
    private readonly TextBox _xMin=new(){PlaceholderText="Minimum",FontSize=11};
    private readonly TextBox _xMax=new(){PlaceholderText="Maximum",FontSize=11};
    private readonly TextBox _yMin=new(){PlaceholderText="Minimum",FontSize=11};
    private readonly TextBox _yMax=new(){PlaceholderText="Maximum",FontSize=11};
    private readonly CheckBox _legendToggle=new(){Content="Show legend",IsChecked=true,MinHeight=28,FontSize=12};
    private readonly CheckBox _cursorToggle=new(){Content="Cursor and rollover",IsChecked=true,MinHeight=28,FontSize=12};
    private readonly CheckBox _labelsToggle=new(){Content="Data labels",MinHeight=28,FontSize=12};
    private readonly Dictionary<string,FrameworkElement> _controls=[];
    private readonly Dictionary<ChartInteractionMode,Button> _modeButtons=[];
    private readonly List<Border> _panelBorders=[];
    private readonly DispatcherTimer _statusTimer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    private readonly DispatcherTimer _streamTimer=new(){Interval=TimeSpan.FromMilliseconds(33)};
    private ChartExample _example=SampleCatalog.Find("overview");
    private ChartModel? _model;
    private Button? _playButton;
    private bool _changing,_streaming,_navigationOpen=true;
    private double _nextTime=10;
    private string _message="Ready";
    private DateTime _messageUntil=DateTime.MinValue;
    public MainPage()
    {
        RequestedTheme=ElementTheme.Dark;
        HorizontalContentAlignment=HorizontalAlignment.Stretch; VerticalContentAlignment=VerticalAlignment.Stretch;
        _root.RowDefinitions.Add(new(){Height=new GridLength(48)});
        _root.RowDefinitions.Add(new(){Height=new GridLength(42)});
        _root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        _root.RowDefinitions.Add(new(){Height=new GridLength(28)});
        BuildHeader(); BuildToolbar(); BuildWorkspace(); BuildFooter();
        Content=_root;
        Register("chart",_chart); Register("search",_search); Register("navigation",_navigation);
        _search.TextChanged+=(_,_)=>RebuildNavigation();
        _navigation.SelectionChanged+=(_,_)=> { if(_navigation.SelectedItem is ListViewItem{Tag:ChartExample selected}) LoadExample(selected); };
        _theme.SelectionChanged+=(_,_)=> { if(!_changing) ApplyTheme(); };
        _annotationKind.SelectionChanged+=(_,_)=> { if(_model is not null&&_annotationKind.SelectedItem is AnnotationKind kind) _chart.Controller.AnnotationKind=kind; };
        _legendToggle.Checked+=(_,_)=> { if(!_changing&&_model is not null) _model.ShowLegend=true; };
        _legendToggle.Unchecked+=(_,_)=> { if(!_changing&&_model is not null) _model.ShowLegend=false; };
        _cursorToggle.Checked+=(_,_)=>SetCursor(true); _cursorToggle.Unchecked+=(_,_)=>SetCursor(false);
        _labelsToggle.Checked+=(_,_)=>SetDataLabels(true); _labelsToggle.Unchecked+=(_,_)=>SetDataLabels(false);
        _chart.RenderCompleted+=(_,_)=>PublishSnapshot();
        _statusTimer.Tick+=(_,_)=>UpdateStatus();
        _streamTimer.Tick+=(_,_)=>AppendStream();
        Loaded+=(_,_)=> { _statusTimer.Start(); RebuildNavigation(); LoadExample(SampleCatalog.Find(PlatformBridge.InitialExample())); };
        Unloaded+=(_,_)=> { _statusTimer.Stop(); _streamTimer.Stop(); };
        SizeChanged+=(_,e)=>ResizeWorkspace(e.NewSize.Width);
    }
    private void BuildHeader()
    {
        var header=new Grid{Padding=new Thickness(14,0,16,0),ColumnSpacing=14};
        header.ColumnDefinitions.Add(new(){Width=GridLength.Auto}); header.ColumnDefinitions.Add(new(){Width=GridLength.Auto}); header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)}); header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var menu=Button("navigation-toggle","☰",()=> { _navigationOpen=!_navigationOpen; ResizeWorkspace(ActualWidth); },"Toggle example navigation"); menu.Width=32; header.Children.Add(menu);
        var brand=new StackPanel{Orientation=Orientation.Horizontal,Spacing=9,VerticalAlignment=VerticalAlignment.Center};
        brand.Children.Add(new TextBlock{Text="∿",FontSize=29,Foreground=Brush(ChartTheme.Dark.Accent),VerticalAlignment=VerticalAlignment.Center});
        brand.Children.Add(new TextBlock{Text="ChartScene",FontSize=20,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        Grid.SetColumn(brand,1); header.Children.Add(brand);
        Grid.SetColumn(_breadcrumb,2); _breadcrumb.Margin=new Thickness(20,0,0,0); header.Children.Add(_breadcrumb);
        var badge=new TextBlock{Text="UNO  ·  DESKTOP + WEB",FontSize=10,CharacterSpacing=100,Opacity=.6,VerticalAlignment=VerticalAlignment.Center}; Grid.SetColumn(badge,3); header.Children.Add(badge);
        AddRoot(Panel(header,new Thickness(0,0,0,1)),0);
    }
    private void BuildToolbar()
    {
        var tools=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4,Padding=new Thickness(12,4,12,4)};
        tools.Children.Add(Button("home","⌂  Fit",()=>_chart.Controller.ZoomExtents(),"Fit all visible data (Home)"));
        tools.Children.Add(Separator());
        AddMode(tools,ChartInteractionMode.Pan,"pan","Pan","Drag to pan the viewport");
        AddMode(tools,ChartInteractionMode.ZoomRectangle,"zoom-rectangle","Zoom","Drag a rectangle to zoom");
        AddMode(tools,ChartInteractionMode.Select,"select","Select","Select points; Control adds to the selection");
        AddMode(tools,ChartInteractionMode.DrawAnnotation,"annotate","Annotate","Drag to create an annotation");
        foreach(var kind in Enum.GetValues<AnnotationKind>()) _annotationKind.Items.Add(kind);
        _annotationKind.SelectedItem=AnnotationKind.Box; Register("annotation-kind",_annotationKind); tools.Children.Add(_annotationKind);
        tools.Children.Add(Separator());
        tools.Children.Add(Button("undo","↶",()=> { _chart.Controller.History.Undo(); _chart.RequestRender(); },"Undo (Control-Z)"));
        tools.Children.Add(Button("redo","↷",()=> { _chart.Controller.History.Redo(); _chart.RequestRender(); },"Redo (Control-Y)"));
        tools.Children.Add(Button("zoom-in","＋",()=>_chart.Controller.ZoomAt(_chart.LastFrame?.Layout.PlotArea.Center??default,.8),"Zoom in"));
        tools.Children.Add(Button("zoom-out","−",()=>_chart.Controller.ZoomAt(_chart.LastFrame?.Layout.PlotArea.Center??default,1.25),"Zoom out"));
        tools.Children.Add(Separator());
        _playButton=Button("play","▶  Stream",ToggleStreaming,"Start or pause the real-time FIFO example"); tools.Children.Add(_playButton);
        foreach(string theme in new[]{"Dark","Light","High contrast"}) _theme.Items.Add(theme);
        _theme.SelectedIndex=0; Register("theme",_theme); tools.Children.Add(_theme);
        tools.Children.Add(Button("export-png","↓  PNG",async()=>await ExportPngAsync(),"Download a PNG rendered from the current chart"));
        tools.Children.Add(Button("export-csv","↓  CSV",async()=>await ExportCsvAsync(),"Download all channels from the first visible data series"));
        var scroll=new ScrollViewer{Content=tools,HorizontalScrollMode=ScrollMode.Enabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,VerticalScrollMode=ScrollMode.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled};
        AddRoot(Panel(scroll,new Thickness(0,0,0,1)),1);
    }
    private void BuildWorkspace()
    {
        _workspace.ColumnDefinitions.Add(_navigationColumn); _workspace.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)}); _workspace.ColumnDefinitions.Add(_inspectorColumn);
        var navigation=new Grid(); navigation.RowDefinitions.Add(new(){Height=GridLength.Auto}); navigation.RowDefinitions.Add(new(){Height=GridLength.Auto}); navigation.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var label=new TextBlock{Text="EXAMPLES",FontSize=10,CharacterSpacing=130,Opacity=.6,Margin=new Thickness(16,17,12,0)}; navigation.Children.Add(label);
        Grid.SetRow(_search,1); navigation.Children.Add(_search); Grid.SetRow(_navigation,2); navigation.Children.Add(_navigation);
        _navigation.BorderThickness=new Thickness(0); _navigation.Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent); _navigation.Padding=new Thickness(6,0,6,12);
        var navPanel=Panel(navigation,new Thickness(0,0,1,0)); _workspace.Children.Add(navPanel); Register("navigation-panel",navPanel);
        var center=new Grid{Padding=new Thickness(10)}; center.Children.Add(_chart); Grid.SetColumn(center,1); _workspace.Children.Add(center);
        var inspector=new StackPanel{Spacing=16,Padding=new Thickness(18,20,18,20)};
        inspector.Children.Add(_exampleTitle); inspector.Children.Add(_description);
        inspector.Children.Add(new Border{Height=1,Background=Brush(ChartTheme.Dark.Border),Margin=new Thickness(0,5,0,2)});
        inspector.Children.Add(SectionLabel("CHART OPTIONS"));
        var toggles=new StackPanel{Spacing=0}; toggles.Children.Add(_legendToggle); toggles.Children.Add(_cursorToggle); toggles.Children.Add(_labelsToggle); inspector.Children.Add(toggles);
        Register("legend-toggle",_legendToggle); Register("cursor-toggle",_cursorToggle); Register("labels-toggle",_labelsToggle);
        inspector.Children.Add(SectionLabel("VISIBLE RANGE"));
        inspector.Children.Add(RangeEditor("X",_xMin,_xMax)); inspector.Children.Add(RangeEditor("Y",_yMin,_yMax));
        Register("x-min",_xMin); Register("x-max",_xMax); Register("y-min",_yMin); Register("y-max",_yMax);
        inspector.Children.Add(Button("apply-ranges","Apply ranges",ApplyRanges,"Apply finite, ordered axis ranges"));
        var sourceExpander=new Expander{Header="C# example",IsExpanded=false,HorizontalAlignment=HorizontalAlignment.Stretch,Content=_source}; inspector.Children.Add(sourceExpander); Register("source-expander",sourceExpander);
        var dataExpander=new Expander{Header="Data and rendering",IsExpanded=false,HorizontalAlignment=HorizontalAlignment.Stretch,Content=_dataSummary}; inspector.Children.Add(dataExpander); Register("data-expander",dataExpander);
        inspector.Children.Add(new TextBlock{Text="Independent implementation. Full SciChart WPF compatibility has not been certified.",FontSize=10,Opacity=.5,TextWrapping=TextWrapping.Wrap,LineHeight=16});
        var inspectorPanel=Panel(new ScrollViewer{Content=inspector,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},new Thickness(1,0,0,0)); Grid.SetColumn(inspectorPanel,2); _workspace.Children.Add(inspectorPanel); Register("inspector-panel",inspectorPanel);
        AddRoot(_workspace,2);
    }
    private void BuildFooter()
    {
        var footer=new Grid{Padding=new Thickness(14,0,14,0)}; footer.Children.Add(_status);
        AddRoot(Panel(footer,new Thickness(0,1,0,0)),3);
    }
    private Grid RangeEditor(string name,TextBox minimum,TextBox maximum)
    {
        var row=new Grid{ColumnSpacing=5}; row.ColumnDefinitions.Add(new(){Width=new GridLength(17)}); row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)}); row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        row.Children.Add(new TextBlock{Text=name,FontSize=11,VerticalAlignment=VerticalAlignment.Center,Opacity=.7}); Grid.SetColumn(minimum,1); row.Children.Add(minimum); Grid.SetColumn(maximum,2); row.Children.Add(maximum); return row;
    }
    private static TextBlock SectionLabel(string text)=>new(){Text=text,FontSize=10,CharacterSpacing=100,Opacity=.6};
    private Border Panel(UIElement content,Thickness border)
    {
        var panel=new Border{Child=content,BorderThickness=border,BorderBrush=Brush(ChartTheme.Dark.Border),Background=Brush(ChartTheme.Dark.Background)}; _panelBorders.Add(panel); return panel;
    }
    private static Border Separator()=>new(){Width=1,Height=20,Background=new SolidColorBrush(Windows.UI.Color.FromArgb(80,130,145,170)),Margin=new Thickness(5,0,5,0)};
    private void AddRoot(UIElement element,int row) { Grid.SetRow(element,row); _root.Children.Add(element); }
    private Button Button(string id,string text,Action action,string help)
    {
        var button=new Button{Content=text,FontSize=12,MinHeight=30,Padding=new Thickness(10,4,10,4),VerticalAlignment=VerticalAlignment.Center};
        button.Click+=(_,_)=> { try { action(); } catch(Exception error) { ShowMessage(error.Message); Console.Error.WriteLine(error); } };
        ToolTipService.SetToolTip(button,help); Register(id,button); return button;
    }
    private void AddMode(StackPanel panel,ChartInteractionMode mode,string id,string text,string help)
    {
        var button=Button(id,text,()=>SetMode(mode),help); _modeButtons[mode]=button; panel.Children.Add(button);
    }
    private void Register(string id,FrameworkElement element)
    {
        _controls[id]=element; AutomationProperties.SetAutomationId(element,id); AutomationProperties.SetName(element,id.Replace('-',' '));
    }
    private void RebuildNavigation()
    {
        string search=_search.Text?.Trim()??"";
        foreach(string key in _controls.Keys.Where(k=>k.StartsWith("example:",StringComparison.Ordinal)).ToArray()) _controls.Remove(key);
        _navigation.Items.Clear();
        var filtered=SampleCatalog.Examples.Where(e=>search.Length==0||$"{e.Title} {e.Category} {e.Id}".Contains(search,StringComparison.OrdinalIgnoreCase));
        foreach(var group in filtered.GroupBy(e=>e.Category))
        {
            _navigation.Items.Add(new ListViewItem{IsEnabled=false,MinHeight=25,Padding=new Thickness(9,9,8,2),Content=new TextBlock{Text=group.Key.ToUpperInvariant(),FontSize=9,CharacterSpacing=75,Opacity=.55}});
            foreach(var example in group)
            {
                var item=new ListViewItem{Tag=example,MinHeight=32,Padding=new Thickness(9,6,8,6),Content=new TextBlock{Text=example.Title,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis}};
                _navigation.Items.Add(item); Register("example:"+example.Id,item);
            }
        }
        PublishSnapshot();
    }
    private void LoadExample(ChartExample example)
    {
        if(_changing) return;
        _changing=true; _streaming=false; _streamTimer.Stop(); _nextTime=10;
        try
        {
            var previous=_model; _example=example; _model=example.Create(); _chart.Model=_model; previous?.Dispose();
            _breadcrumb.Text=example.Category+"  /  "+example.Title; _exampleTitle.Text=example.Title; _description.Text=example.Description; _source.Text=example.Source;
            _model.AutoRangeAxes();
            if(example.Id=="live") { _model.XAxis.VisibleRange=new(0,10); _model.YAxis.VisibleRange=new(-1.5,1.5); }
            _legendToggle.IsChecked=_model.ShowLegend; _labelsToggle.IsChecked=_model.RenderableSeries.Any(s=>s.ShowDataLabels);
            _cursorToggle.IsChecked=true; _chart.Controller.AnnotationKind=_annotationKind.SelectedItem is AnnotationKind kind?kind:AnnotationKind.Box;
            _chart.Controller.AnnotationEditRequested+=async(_,annotation)=>await EditAnnotationTextAsync(annotation);
            SetMode(ChartInteractionMode.Pan); ApplyTheme(); UpdateRangeEditors();
            _dataSummary.Text=string.Join("\n",_model.RenderableSeries.Select(s=>$"{s.SeriesName}: {s.DataSeries?.Count??s.HeatmapGrid?.Values.Length??0:N0} samples, {s.Kind}"));
            if(_playButton is not null) _playButton.Content="▶  Stream";
            _message="Ready"; _messageUntil=DateTime.MinValue; _chart.RequestRender();
        }
        catch(Exception error) { Console.Error.WriteLine(error); ShowMessage(error.Message); PlatformBridge.ReportFailure(error.ToString()); }
        finally { _changing=false; }
        PublishSnapshot();
    }
    private void SetMode(ChartInteractionMode mode)
    {
        _chart.Controller.Mode=mode;
        var theme=_model?.Theme??ChartTheme.Dark;
        foreach(var pair in _modeButtons) pair.Value.Background=Brush(pair.Key==mode?theme.Accent.WithAlpha(65):theme.Background);
        PublishSnapshot();
    }
    private void ApplyTheme()
    {
        if(_model is null) return;
        var previous=_model.Theme; var next=_theme.SelectedIndex==1?ChartTheme.Light:_theme.SelectedIndex==2?ChartTheme.HighContrast:ChartTheme.Dark;
        _model.Theme=next; RequestedTheme=next==ChartTheme.Light?ElementTheme.Light:ElementTheme.Dark;
        ColorRgba ConvertColor(ColorRgba color)
        {
            for(int i=0;i<previous.Palette.Count;i++) if(previous.Palette[i] with{A=color.A}==color) return next.Palette[i%next.Palette.Count].WithAlpha(color.A);
            return color;
        }
        foreach(var series in _model.RenderableSeries) { series.Stroke=ConvertColor(series.Stroke); series.Fill=ConvertColor(series.Fill); series.StrokeY1=ConvertColor(series.StrokeY1); series.FillY1=ConvertColor(series.FillY1); }
        foreach(var border in _panelBorders) { border.Background=Brush(next.Background); border.BorderBrush=Brush(next.Border); }
        _workspace.Background=Brush(next.PlotBackground); _status.Foreground=Brush(next.Muted); _breadcrumb.Foreground=Brush(next.Muted); SetMode(_chart.Controller.Mode);
    }
    private static SolidColorBrush Brush(ColorRgba color)=>new(Windows.UI.Color.FromArgb(color.A,color.R,color.G,color.B));
    private void SetCursor(bool value)
    {
        if(_changing||_model is null) return; _chart.Controller.View.ShowCursor=value; _chart.Controller.View.ShowRollover=value; _chart.RequestRender();
    }
    private void SetDataLabels(bool value)
    {
        if(_changing||_model is null) return; foreach(var series in _model.RenderableSeries) series.ShowDataLabels=value;
    }
    private void UpdateRangeEditors()
    {
        if(_model is null) return;
        _xMin.Text=_model.XAxis.VisibleRange.Min.ToString("G8",CultureInfo.InvariantCulture); _xMax.Text=_model.XAxis.VisibleRange.Max.ToString("G8",CultureInfo.InvariantCulture);
        _yMin.Text=_model.YAxis.VisibleRange.Min.ToString("G8",CultureInfo.InvariantCulture); _yMax.Text=_model.YAxis.VisibleRange.Max.ToString("G8",CultureInfo.InvariantCulture);
    }
    private void ApplyRanges()
    {
        if(_model is null) return;
        double Parse(TextBox input)=>double.Parse(input.Text,CultureInfo.InvariantCulture);
        var x=new DoubleRange(Parse(_xMin),Parse(_xMax)); var y=new DoubleRange(Parse(_yMin),Parse(_yMax));
        if(_model.XAxis.Scale==AxisScale.Logarithmic&&x.Min<=0||_model.YAxis.Scale==AxisScale.Logarithmic&&y.Min<=0) throw new ArgumentOutOfRangeException("Logarithmic ranges must be strictly positive.");
        var before=new ViewportSnapshot(_model); _model.XAxis.AutoRange=AutoRange.Never; _model.YAxis.AutoRange=AutoRange.Never; _model.XAxis.VisibleRange=x; _model.YAxis.VisibleRange=y; _chart.Controller.RecordViewport("Set ranges",before); ShowMessage("Visible ranges updated");
    }
    private void ToggleStreaming()
    {
        if(_example.Id!="live") LoadExample(SampleCatalog.Find("live"));
        _streaming=!_streaming;
        if(_streaming) _streamTimer.Start(); else _streamTimer.Stop();
        if(_playButton is not null) _playButton.Content=_streaming?"Ⅱ  Pause":"▶  Stream";
        PublishSnapshot();
    }
    private void AppendStream()
    {
        if(!_streaming||_model is null) return;
        for(int channel=0;channel<_model.RenderableSeries.Count;channel++)
        {
            if(_model.RenderableSeries[channel].DataSeries is not XyDataSeries data) continue;
            using(data.SuspendUpdates()) for(int i=0;i<7;i++) { double time=_nextTime+i*.005; data.Append(time,SampleCatalog.Signal(time,channel)); }
        }
        _nextTime+=.035; _model.XAxis.VisibleRange=new(_nextTime-10,_nextTime); _model.YAxis.VisibleRange=new(-1.5,1.5);
    }
    private async Task ExportPngAsync()
    {
        if(_model is null) return;
        try
        {
            byte[] image=RasterExporter.ExportPng(_model,Math.Max(300,(int)_chart.ActualWidth),Math.Max(200,(int)_chart.ActualHeight),2);
            ShowMessage(await PlatformBridge.ExportAsync($"ChartScene-{_example.Id}.png","image/png",image));
        }
        catch(Exception error) { ShowMessage(error.Message); }
    }
    private async Task ExportCsvAsync()
    {
        if(_model?.RenderableSeries.FirstOrDefault(s=>s.IsVisible&&s.DataSeries is not null)?.DataSeries is not { } source) { ShowMessage("This example does not have a tabular data series."); return; }
        var data=source.Snapshot(); var text=new StringBuilder(); text.AppendLine(string.Join(",",Enumerable.Range(0,data.ChannelCount).Select(i=>i==0?"X":i==1?"Y":"Channel"+i)));
        for(int i=0;i<data.Count;i++) { for(int c=0;c<data.ChannelCount;c++) { if(c>0) text.Append(','); if(double.IsFinite(data[c,i])) text.Append(data[c,i].ToString("R",CultureInfo.InvariantCulture)); } text.AppendLine(); }
        ShowMessage(await PlatformBridge.ExportAsync($"ChartScene-{_example.Id}.csv","text/csv;charset=utf-8",Encoding.UTF8.GetBytes(text.ToString())));
    }
    private async Task EditAnnotationTextAsync(ChartAnnotation annotation)
    {
        var input=new TextBox{Text=annotation.Text,AcceptsReturn=true,MinWidth=260};
        var dialog=new ContentDialog{Title="Annotation text",Content=input,PrimaryButtonText="Apply",CloseButtonText="Cancel",XamlRoot=XamlRoot};
        if(await dialog.ShowAsync()==ContentDialogResult.Primary)
        {
            string previous=annotation.Text,next=input.Text;
            _chart.Controller.History.Execute(new DelegateCommand("Edit annotation text",()=>annotation.Text=next,()=>annotation.Text=previous));
        }
    }
    private void ShowMessage(string text) { _message=text; _messageUntil=DateTime.UtcNow.AddSeconds(8); UpdateStatus(); }
    private void UpdateStatus()
    {
        var frame=_chart.LastFrame;
        string status=DateTime.UtcNow<_messageUntil?_message:frame is not null?$"{frame.Statistics.SourcePoints:N0} source samples   ·   {frame.Statistics.RenderedPoints:N0} rendered   ·   {frame.Statistics.CpuMilliseconds:0.00} ms CPU render   ·   {_chart.RendererDescription}   ·   {(_streaming?"LIVE":"Ready")}":"Initializing chart renderer…";
        if(_status.Text!=status) _status.Text=status;
        PublishSnapshot();
    }
    private void ResizeWorkspace(double width)
    {
        _navigationColumn.Width=new GridLength(_navigationOpen?(width<800?180:230):0);
        _inspectorColumn.Width=new GridLength(width>=1150?285:0);
        _controls["navigation-panel"].Visibility=_navigationOpen?Visibility.Visible:Visibility.Collapsed;
        _controls["inspector-panel"].Visibility=width>=1150?Visibility.Visible:Visibility.Collapsed;
        PublishSnapshot();
    }
    private ControlBounds? Bounds(FrameworkElement element)
    {
        if(!element.IsLoaded||element.Visibility!=Visibility.Visible||element.ActualWidth<=0||element.ActualHeight<=0) return null;
        try
        {
            var position=element.TransformToVisual(null).TransformPoint(new Point(0,0));
            return new(position.X,position.Y,element.ActualWidth,element.ActualHeight);
        }
        catch(InvalidOperationException) { return null; }
    }
    private void PublishSnapshot()
    {
        if(!IsLoaded||_model is null) return;
        var frame=_chart.LastFrame;
        PlatformBridge.Publish(new DiagnosticSnapshot
        {
            ready=frame is not null&&_chart.LastError is null,exampleId=_example.Id,theme=_model.Theme.Name,mode=_chart.Controller.Mode.ToString(),streaming=_streaming,
            frames=_chart.FrameCount,error=_chart.LastError,renderer=_chart.RendererDescription,
            width=ActualWidth,height=ActualHeight,
            plot=frame?.Layout.PlotArea,
            ranges=new(new(_model.XAxis.VisibleRange.Min,_model.XAxis.VisibleRange.Max),new(_model.YAxis.VisibleRange.Min,_model.YAxis.VisibleRange.Max)),
            statistics=frame?.Statistics,
            series=_model.RenderableSeries.Select(s=>new SeriesSnapshotDto(s.Id,s.SeriesName,s.Kind.ToString(),s.IsVisible,s.SelectedIndices.Count,s.DataSeries?.Count??s.HeatmapGrid?.Values.Length??0,s.DataSeries?.Revision??0)).ToArray(),
            annotations=_model.Annotations.Select(a=>new AnnotationSnapshotDto(a.Id,a.Kind.ToString(),a.X1,a.Y1,a.X2,a.Y2,a.IsSelected)).ToArray(),
            hits=_chart.Controller.View.Hits.Count,canUndo=_chart.Controller.History.CanUndo,canRedo=_chart.Controller.History.CanRedo,
            controls=_controls.ToDictionary(p=>p.Key,p=>Bounds(p.Value)),
            catalog=SampleCatalog.Examples.Select(e=>new CatalogEntryDto(e.Id,e.Title,e.Category)).ToArray()
        });
    }
}
