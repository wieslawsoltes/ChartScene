using ChartScene.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChartScene.App;

public sealed partial class App:Application
{
    public static Window? MainWindow { get; private set; }
    public App()
    {
        GraphicsDiagnostics.Initialize();
        InitializeComponent();
        UnhandledException+=(_,e)=> { Console.Error.WriteLine(e.Exception); PlatformBridge.ReportFailure(e.Exception.ToString()); };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow=new Window{Title="ChartScene — scientific charting"};
        try
        {
            var shell=new GalleryShell();
#if __WASM__
            shell.Loaded+=RequestStartupFrame;
#endif
            MainWindow.Content=shell;
        }
        catch(Exception error)
        {
            Console.Error.WriteLine(error); PlatformBridge.ReportFailure(error.ToString());
            MainWindow.Content=new TextBlock{Text="ChartScene could not start.\n"+error.Message,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(32)};
        }
        MainWindow.Activate();
    }
#if __WASM__
    private static async void RequestStartupFrame(object sender,RoutedEventArgs args)
    {
        var root=(FrameworkElement)sender;
        root.Loaded-=RequestStartupFrame;
        try
        {
            bool requested=await UnoBrowserStartup.RequestInitialFrameAsync(root);
            Uno.Foundation.WebAssemblyRuntime.InvokeJS(requested
                ? "window.chartSceneStartup={qualifiedUno:'6.7.135',requests:1,detached:false}"
                : "window.chartSceneStartup={qualifiedUno:'6.7.135',requests:0,detached:true}");
        }
        catch(Exception error)
        {
            Console.Error.WriteLine(error);
            PlatformBridge.ReportFailure(error.ToString());
        }
    }
#endif
}
