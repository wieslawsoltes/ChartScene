using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace ChartScene.App;

/// <summary>Optional local input observations. No focus changes, event interception or typed-text collection.</summary>
internal static class UiInputTrace
{
#if __WASM__
    private sealed class Registration(string id) { public string Id { get; set; }=id; }
    private static readonly ConditionalWeakTable<FrameworkElement,Registration> Registrations=new();
    private static bool? _enabled;
#endif
    public static void Register(FrameworkElement element,string id)
    {
#if __WASM__
        _enabled??=Uno.Foundation.WebAssemblyRuntime.InvokeJS("new URLSearchParams(location.search).get('inputTrace') === '1' ? 'yes' : 'no'")=="yes";
        if(_enabled!=true)return;
        // Reused catalogue rows are registered repeatedly as filters change. Attach handlers once;
        // weak keys do not keep detached controls alive, and identity can still be updated.
        if(Registrations.TryGetValue(element,out var existing)){existing.Id=id;return;}
        var registration=new Registration(id);Registrations.Add(element,registration);
        element.AddHandler(UIElement.PointerMovedEvent,new PointerEventHandler((_,e)=>Publish(registration.Id,"move",e)),true);
        element.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_,e)=>Publish(registration.Id,"down",e)),true);
        element.AddHandler(UIElement.PointerReleasedEvent,new PointerEventHandler((_,e)=>Publish(registration.Id,"up",e)),true);
        // Read current focus after routed notifications finish. No focus redirection, keys,
        // typed text, clipboard content or application-state mutation is recorded.
        element.GotFocus+=(_,_)=>element.DispatcherQueue.TryEnqueue(()=>PublishFocus(element));
        element.LostFocus+=(_,_)=>element.DispatcherQueue.TryEnqueue(()=>PublishFocus(element));
#endif
    }
#if __WASM__
    private static void PublishFocus(FrameworkElement element)
    {
        if(element.XamlRoot is not { } root)return;
        string? id=null;
        for(DependencyObject? current=FocusManager.GetFocusedElement(root) as DependencyObject;
            current is not null;current=VisualTreeHelper.GetParent(current))
        {
            string candidate=AutomationProperties.GetAutomationId(current);
            if(!string.IsNullOrEmpty(candidate)){id=candidate;break;}
        }
        string json=JsonSerializer.Serialize(new UiFocusTrace(id),UiInputJsonContext.Default.UiFocusTrace);
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("(()=>{const e="+json+";e.sequence=(window.chartSceneFocus?.sequence||0)+1;window.chartSceneFocus=e;const q=window.chartSceneFocusEvents ||= [];q.push(e);if(q.length>64)q.shift();return '';})()");
    }
    private static void Publish(string id,string kind,PointerRoutedEventArgs e)
    {
        var point=e.GetCurrentPoint(null).Position;var trace=new UiPointerTrace(id,kind,point.X,point.Y);
        string json=JsonSerializer.Serialize(trace,UiInputJsonContext.Default.UiPointerTrace);
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("(()=>{const e="+json+";e.sequence=(window.chartSceneInput?.sequence||0)+1;window.chartSceneInput=e;const q=window.chartSceneInputEvents ||= [];q.push(e);if(q.length>64)q.shift();return '';})()");
    }
#endif
}
public sealed record UiPointerTrace(string target,string kind,double x,double y);
public sealed record UiFocusTrace(string? control);
[JsonSerializable(typeof(UiPointerTrace))]
[JsonSerializable(typeof(UiFocusTrace))]
public partial class UiInputJsonContext:JsonSerializerContext;
