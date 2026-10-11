using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ChartScene.Controls;

/// <summary>The startup hand-off result; callbacks are composition observations, not chart frames or FPS.</summary>
public readonly record struct BrowserStartupResult(bool Attached, int CompositionCallbacks);

/// <summary>
/// Version-qualified adapter for the Uno 6.7.135 Skia browser startup race.
/// Requalify or remove when upgrading Uno. Never removes/hides the native loader.
/// </summary>
public static class UnoBrowserStartup
{
    public const string QualifiedUnoVersion = "6.7.135";

    /// <summary>
    /// Called once on the UI thread after the window root loads. Completes only after Uno
    /// releases its own loader, or returns Attached=false when the root detaches.
    /// The temporary composition subscription is removed on every completion/error path.
    /// </summary>
    public static async Task<BrowserStartupResult> EnsureInitialPresentationAsync(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
#if __WASM__
        if (!root.DispatcherQueue.HasThreadAccess || !root.IsLoaded || root.XamlRoot is not { } xamlRoot)
            throw new InvalidOperationException("Request startup presentation from a loaded window root on its UI thread.");
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
        await fonts.WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        var completion = new TaskCompletionSource<BrowserStartupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!root.DispatcherQueue.TryEnqueue(() =>
        {
            bool finished = false;
            int callbacks = 0;
            // This is only a failure/cleanup deadline. It never requests or paints frames.
            var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            void Finish(bool attached, Exception? error = null)
            {
                if (finished) return;
                finished = true;
                deadline.Stop();
                deadline.Tick -= OnTimeout;
                root.Unloaded -= OnUnloaded;
                CompositionTarget.Rendering -= OnRendering;
                if (error is null) completion.TrySetResult(new(attached, callbacks));
                else completion.TrySetException(error);
            }
            void OnTimeout(object? sender, object args) =>
                Finish(false, new TimeoutException("Uno did not complete its post-font startup presentation within 30 seconds."));
            void OnUnloaded(object sender, RoutedEventArgs args) => Finish(false);
            void OnRendering(object? sender, object args)
            {
                if (finished) return;
                try
                {
                    callbacks++;
                    if (!root.IsLoaded || !ReferenceEquals(root.XamlRoot, xamlRoot)) Finish(false);
                    else if (!HasNativeLoader()) Finish(true);
                }
                catch (Exception error) { Finish(false, error); }
            }
            try
            {
                if (!root.IsLoaded || !ReferenceEquals(root.XamlRoot, xamlRoot)) { Finish(false); return; }
                if (!HasNativeLoader()) { Finish(true); return; }
                root.Unloaded += OnUnloaded;
                deadline.Tick += OnTimeout;
                // Upstream: https://github.com/unoplatform/uno/pull/24260
                // FontPreloadTask completion precedes ShowCore's dispatched InvalidateArrange,
                // LayoutUpdated handler and FrameRendered subscription. An immediate add/remove
                // can therefore finish too early. Keep full-frame scheduling alive through that
                // native hand-off; stop as soon as Uno removes its loader after FrameRendered.
                // No artificial input, chart mutation, arbitrary retry count or permanent loop.
                CompositionTarget.Rendering += OnRendering;
                deadline.Start();
            }
            catch (Exception error) { Finish(false, error); }
        })) completion.TrySetException(new InvalidOperationException("The dispatcher rejected startup presentation."));
        return await completion.Task.ConfigureAwait(false);
#else
        await Task.CompletedTask;
        return new(false, 0);
#endif
    }
#if __WASM__
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FontPreloadTask")]
    private static extern Task FontPreloadTask(Application application);

    // Read-only observation of the pinned host's completion signal. The host, never this
    // adapter, owns loader removal. Failure to release it times out and unsubscribes above.
    private static bool HasNativeLoader() =>
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("document.querySelector('#loading.uno-persistent-loader') !== null") == "true";
#endif
}
