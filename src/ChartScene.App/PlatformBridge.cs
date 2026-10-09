using System.Text.Json;

namespace ChartScene.App;

/// <summary>Small platform boundary: diagnostics describe the actual Uno view; all chart logic remains managed.</summary>
internal static class PlatformBridge
{
    public static void Publish(object snapshot)
    {
#if __WASM__
        string json=JsonSerializer.Serialize(snapshot);
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartScene = Object.assign(window.chartScene || {}, "+json+");");
#endif
    }
    public static void ReportFailure(string error)
    {
#if __WASM__
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartScene = Object.assign(window.chartScene || {}, {fatalError:"+JsonSerializer.Serialize(error)+"});");
#endif
    }
    public static string InitialExample()
    {
#if __WASM__
        return Uno.Foundation.WebAssemblyRuntime.InvokeJS("new URLSearchParams(location.search).get('example') || 'overview'");
#else
        return "overview";
#endif
    }
    public static async Task<string> ExportAsync(string filename,string mimeType,byte[] contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
#if __WASM__
        string name=JsonSerializer.Serialize(filename),mime=JsonSerializer.Serialize(mimeType),base64=JsonSerializer.Serialize(Convert.ToBase64String(contents));
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("(()=>{const bytes=Uint8Array.from(atob("+base64+"),c=>c.charCodeAt(0));const url=URL.createObjectURL(new Blob([bytes],{type:"+mime+"}));const a=document.createElement('a');a.href=url;a.download="+name+";document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),30000);return '';})()");
        await Task.CompletedTask;
        return "Downloaded "+filename;
#else
        string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"ChartScene");
        Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,Path.GetFileName(filename));
        await File.WriteAllBytesAsync(path,contents);
        return "Saved "+path;
#endif
    }
}
