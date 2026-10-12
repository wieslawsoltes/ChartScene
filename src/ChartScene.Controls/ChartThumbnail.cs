using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace ChartScene.Controls;

/// <summary>
/// Noninteractive, bindable preview of a newly created chart. Use a shared ChartThumbnailCache
/// to reuse images across tiles; no live ChartSurface, controller or streaming timer is constructed.
/// Factories return exclusive models which the cache disposes after rendering.
/// </summary>
public sealed class ChartThumbnail : UserControl
{
    private sealed class PreviewCanvas(ChartThumbnail owner) : SKCanvasElement
    {
        protected override void RenderOverride(SKCanvas canvas, Size area) => owner.Render(canvas, area);
        public void RequestRender() => Invalidate();
    }
    private readonly PreviewCanvas _canvas;
    private ChartThumbnailCache? _localCache, _sharedCache;
    public static readonly DependencyProperty FactoryProperty = DependencyProperty.Register(nameof(Factory), typeof(Func<ChartModel>),
        typeof(ChartThumbnail), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(nameof(Theme), typeof(ChartTheme),
        typeof(ChartThumbnail), new PropertyMetadata(ChartTheme.Dark, Changed));
    public static readonly DependencyProperty ContentRevisionProperty = DependencyProperty.Register(nameof(ContentRevision), typeof(long),
        typeof(ChartThumbnail), new PropertyMetadata(0L, Changed));
    public Func<ChartModel>? Factory { get => (Func<ChartModel>?)GetValue(FactoryProperty); set => SetValue(FactoryProperty, value); }
    public ChartTheme Theme { get => (ChartTheme)GetValue(ThemeProperty); set => SetValue(ThemeProperty, value); }
    public long ContentRevision { get => (long)GetValue(ContentRevisionProperty); set => SetValue(ContentRevisionProperty, value); }
    public ChartThumbnailCache? SharedCache
    {
        get => _sharedCache;
        set
        {
            if (ReferenceEquals(value, _sharedCache)) return;
            _localCache?.Dispose(); _localCache = null; _sharedCache = value; InvalidatePreview();
        }
    }
    public bool IsReady { get; private set; }
    public string? LastError { get; private set; }
    public ThumbnailResult? LastResult { get; private set; }
    public event EventHandler? RenderCompleted;
    public ChartThumbnail()
    {
        IsTabStop = false; IsHitTestVisible = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        _canvas = new(this); Content = _canvas;
        Loaded += (_, _) => InvalidatePreview();
        Unloaded += (_, _) => { _localCache?.Dispose(); _localCache = null; IsReady = false; };
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ChartThumbnail)d).InvalidatePreview();
    private void InvalidatePreview()
    {
        IsReady = false; LastError = null; LastResult = null;
        if (IsLoaded) _canvas.RequestRender();
    }
    private void Render(SKCanvas canvas, Size size)
    {
        if (Factory is null || size.Width <= 0 || size.Height <= 0) return;
        try
        {
            var cache = SharedCache ?? (_localCache ??= new ChartThumbnailCache());
            LastResult = cache.Draw(canvas, Factory, Theme, new RectD(0, 0, size.Width, size.Height), contentRevision: ContentRevision);
            LastError = null; IsReady = true;
        }
        catch (Exception error)
        {
            LastError = error.Message; IsReady = false;
            // No successful ready state or fake chart on failure. Owners can show LastError.
        }
        RenderCompleted?.Invoke(this, EventArgs.Empty);
    }
}
