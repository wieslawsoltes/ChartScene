using Uno.UI.Hosting;

namespace ChartScene.App;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // Expose the native semantic tree and normal keyboard navigation for every browser
        // session, not only after activating Uno's hidden accessibility opt-in button.
        // This must precede host construction. Chart data remains represented by its
        // existing automation peers, not one DOM element per plotted point.
        Uno.UI.FeatureConfiguration.AutomationPeer.AutoEnableAccessibility=true;
        var host=UnoPlatformHostBuilder.Create().App(()=>new App()).UseWebAssembly().Build();
        await host.RunAsync();
    }
}
