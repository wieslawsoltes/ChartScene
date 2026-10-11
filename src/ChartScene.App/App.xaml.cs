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
        try { MainWindow.Content=new GalleryShell(); }
        catch(Exception error)
        {
            Console.Error.WriteLine(error); PlatformBridge.ReportFailure(error.ToString());
            MainWindow.Content=new TextBlock{Text="ChartScene could not start.\n"+error.Message,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(32)};
        }
        MainWindow.Activate();
    }
}
