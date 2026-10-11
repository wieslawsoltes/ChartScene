using System.Text.Json;
using ChartScene.Controls;
using ChartScene.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Windows.Foundation;
namespace ChartScene.App;

public sealed class GalleryShell : UserControl
{
    private readonly Grid _root=new();
    private readonly ContentControl _content=new(){HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch};
    private readonly Dictionary<string,ChartToolButton> _tabs=[];
    private readonly Dictionary<string,UIElement> _pages=[];
    private readonly Dictionary<string,Func<UIElement>> _factories;
    private string _view="2d";
    private bool _initialized;
    public GalleryShell()
    {
        RequestedTheme=ElementTheme.Dark;HorizontalContentAlignment=HorizontalAlignment.Stretch;VerticalContentAlignment=VerticalAlignment.Stretch;
        _factories=new(){["2d"]=()=>new MainPage(),["3d"]=()=>new ThreeDPage(),["linked"]=()=>new LinkedChartsPage(),["colors"]=()=>new ColorLegendsPage(),["overview"]=()=>new OverviewPage(),["stacks"]=()=>new StackedDataPage()};
        _root.RowDefinitions.Add(new(){Height=new GridLength(36)});_root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var tabs=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4,Padding=new Thickness(10,3,10,3)};
        void Tab(string id,string label,ChartGlyph glyph)
        {
            var button=new ChartToolButton(glyph,label){MinHeight=28,Height=28,Padding=new Thickness(8,2,8,2)};
            button.Describe(label);AutomationProperties.SetAutomationId(button,"gallery-"+id);UiInputTrace.Register(button,"gallery-"+id);
            button.Click+=(_,_)=>Switch(id);_tabs.Add(id,button);tabs.Children.Add(button);
        }
        Tab("2d","2D Charts",ChartGlyph.Line);Tab("3d","3D Charts",ChartGlyph.Surface);Tab("linked","Linked",ChartGlyph.Cursor);
        Tab("colors","Color legends",ChartGlyph.Grid);Tab("overview","Overview",ChartGlyph.Fit);Tab("stacks","Stacked data",ChartGlyph.Columns);
        tabs.Children.Add(new TextBlock{Text="UNO  ·  DESKTOP + WEB",FontSize=9,Opacity=.5,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(18,0,0,0)});
        _root.Children.Add(new Border{Background=new SolidColorBrush(Windows.UI.Color.FromArgb(255,17,22,33)),BorderThickness=new Thickness(0,0,0,1),BorderBrush=new SolidColorBrush(Windows.UI.Color.FromArgb(255,45,55,72)),Child=new ScrollViewer{Content=tabs,HorizontalScrollMode=ScrollMode.Enabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,VerticalScrollMode=ScrollMode.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled}});
        Grid.SetRow(_content,1);_root.Children.Add(_content);Content=_root;
        Loaded+=(_,_)=>
        {
            if(_initialized){Publish();return;}_initialized=true;string initial="2d";
#if __WASM__
            initial=Uno.Foundation.WebAssemblyRuntime.InvokeJS("new URLSearchParams(location.search).get('view') || '2d'");
#endif
            Switch(_factories.ContainsKey(initial)?initial:"2d");
        };
        SizeChanged+=(_,_)=>Publish();
    }
    private void Switch(string view)
    {
        if(!_factories.TryGetValue(view,out var create))return;
        if(!_pages.TryGetValue(view,out var page))_pages[view]=page=create();
        _view=view;
        if(!ReferenceEquals(_content.Content,page))_content.Content=page;
        foreach(var pair in _tabs)pair.Value.IsActive=pair.Key==view;
        DispatcherQueue.TryEnqueue(Publish);
    }
    private void Publish()
    {
#if __WASM__
        ControlBounds? Bounds(FrameworkElement e)
        {
            if(!e.IsLoaded)return null;
            var p=e.TransformToVisual(null).TransformPoint(new Point(0,0));return new(p.X,p.Y,e.ActualWidth,e.ActualHeight);
        }
        var state=new ShellSnapshotDto(_view,_tabs.ToDictionary(p=>"gallery-"+p.Key,p=>Bounds(p.Value)));
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartSceneGallery="+JsonSerializer.Serialize(state,Diagnostic3DJsonContext.Default.ShellSnapshotDto));
#endif
    }
}
