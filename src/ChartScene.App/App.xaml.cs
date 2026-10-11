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
            var result=await UnoBrowserStartup.EnsureInitialPresentationAsync(root);
            // A session can span several native composition callbacks. Do not call those
            // callbacks one frame or a hardware presentation/FPS measurement.
            Uno.Foundation.WebAssemblyRuntime.InvokeJS(
                "window.chartSceneStartup={qualifiedUno:'6.7.135',sessions:1,active:false,compositionCallbacks:"
                +result.CompositionCallbacks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                +",detached:"+(result.Attached?"false":"true")+"}");
        }
        catch(Exception error)
        {
            Console.Error.WriteLine(error);
            PlatformBridge.ReportFailure(error.ToString());
        }
    }
#endif
}
