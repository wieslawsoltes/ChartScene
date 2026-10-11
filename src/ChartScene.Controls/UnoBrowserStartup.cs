using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ChartScene.Controls;

/// <summary>
/// One-shot startup compatibility adapter qualified against Uno 6.7.135's Skia browser host.
/// Requalify or remove when upgrading Uno. It does not hide the splash, modify chart data,
/// synthesize input, or keep a continuous Rendering subscription.
/// </summary>
public static class UnoBrowserStartup
{
    public const string QualifiedUnoVersion = "6.7.135";

    /// <summary>
    /// Call on the UI thread after the window root is Loaded. Waits for Uno's font preload,
    /// then requests one complete composition frame. Returns false on desktop or if the
    /// root detached while fonts loaded. A missing pinned contract is reported, not ignored.
    /// </summary>
    public static async Task<bool> RequestInitialFrameAsync(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
#if __WASM__
        if (!root.DispatcherQueue.HasThreadAccess || !root.IsLoaded || root.XamlRoot is not { } xamlRoot)
            throw new InvalidOperationException("Request the browser startup frame from a loaded window root on its UI thread.");
        Task fonts;
        try
        {
            fonts = FontPreloadTask(Application.Current)
                ?? throw new InvalidOperationException("Uno has not initialized its font-preload task.");
        }
        catch (MissingMethodException error)
        {
            throw new NotSupportedException("The Uno 6.7.135 startup adapter must be requalified for this runtime.", error);
        }
        await fonts.ConfigureAwait(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!root.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (!root.IsLoaded || !ReferenceEquals(root.XamlRoot, xamlRoot)) { completion.SetResult(false); return; }
                // Upstream fix: https://github.com/unoplatform/uno/pull/24260
                // In 6.7.135, adding the first public Rendering handler calls each live
                // target's ICompositionTarget.RequestNewFrame(), unlike the browser host's
                // RequestRender(). Removing it immediately leaves that queued frame intact
                // without starting perpetual animation. If another handler is active, that
                // target is already scheduling full frames and needs no extra subscription.
                CompositionTarget.Rendering += FrameRequested;
                CompositionTarget.Rendering -= FrameRequested;
                completion.SetResult(true);
            }
            catch (Exception error) { completion.SetException(error); }
        })) completion.SetException(new InvalidOperationException("The window dispatcher rejected the startup frame request."));
        return await completion.Task.ConfigureAwait(false);
#else
        await Task.CompletedTask;
        return false;
#endif
    }
#if __WASM__
    // Typed access binds the exact getter signature and survives trimming without rooting
    // every private Application member or doing reflection in a rendering hot path.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FontPreloadTask")]
    private static extern Task FontPreloadTask(Application application);
    private static void FrameRequested(object? sender, object args) { }
#endif
}
