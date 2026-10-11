using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace ChartScene.App;

/// <summary>
/// Optional, local-only input observations for integration tests. Disabled unless explicitly
/// requested in the URL. It never changes focus, marks events handled, or mutates chart state.
/// Records target IDs and pointer coordinates only, not typed text or application data.
/// </summary>
internal static class UiInputTrace
{
#if __WASM__
    private static bool? _enabled;
#endif
    public static void Register(FrameworkElement element, string id)
    {
#if __WASM__
        _enabled ??= Uno.Foundation.WebAssemblyRuntime.InvokeJS("new URLSearchParams(location.search).get('inputTrace') === '1' ? 'yes' : 'no'") == "yes";
        if (_enabled != true) return;
        element.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) => Publish(id, "move", e)), true);
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => Publish(id, "down", e)), true);
        element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => Publish(id, "up", e)), true);
#endif
    }
#if __WASM__
    private static void Publish(string id, string kind, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null).Position;
        var trace = new UiPointerTrace(id, kind, point.X, point.Y);
        string json = JsonSerializer.Serialize(trace, UiInputJsonContext.Default.UiPointerTrace);
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("(()=>{const e=" + json + ";e.sequence=(window.chartSceneInput?.sequence||0)+1;window.chartSceneInput=e;const q=window.chartSceneInputEvents ||= [];q.push(e);if(q.length>64)q.shift();return '';})()");
    }
#endif
}
public sealed record UiPointerTrace(string target, string kind, double x, double y);
[JsonSerializable(typeof(UiPointerTrace))]
public partial class UiInputJsonContext : JsonSerializerContext;
