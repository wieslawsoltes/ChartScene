using System.Diagnostics;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Rendering;
using SkiaSharp;

namespace ChartScene.Rendering.Skia;

public readonly record struct SceneCacheStatistics(long SceneBuilds, long SceneReplays, bool LastFrameReusedScene);

/// <summary>
/// View-owned display-list cache separating chart content from transient interaction overlays.
/// Records vector commands, not a fixed-resolution screenshot. All calls belong to the view's
/// rendering thread. The host canvas/context is borrowed and is never retained or disposed.
/// </summary>
public sealed class SkiaChartSceneCache : IDisposable
{
    private readonly ChartRenderer _renderer = new();
    private readonly SkiaResources _resources = new();
    private ChartModel? _model;
    private ChartFrame? _frame;
    private SKPicture? _picture;
    private ColorRgba[] _palette = [];
    private string[] _seriesIds = [];
    private long _revision = -1, _builds, _replays;
    private double _width, _height;
    private bool _disposed, _lastHit;
    public SceneCacheStatistics Statistics => new(_builds, _replays, _lastHit);

    /// <summary>
    /// Returns current CPU recording/replay/overlay duration, not GPU or presentation timing.
    /// Data, styles, axes, annotations, theme, model identity and size invalidate recording.
    /// User providers may have unobservable state, so they deliberately bypass this cache.
    /// </summary>
    public ChartFrame Render(SKCanvas canvas, ChartModel model, double width, double height, ChartViewState? view = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas); ArgumentNullException.ThrowIfNull(model);
        if (!float.IsFinite((float)width) || !float.IsFinite((float)height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        long started = Stopwatch.GetTimestamp();
        _resources.BeginFrame();
        bool eligible = CanCache(model);
        _lastHit = eligible && _picture is not null && _frame is not null && ReferenceEquals(_model, model)
            && _revision == model.Revision && _width == width && _height == height && ConfigurationMatches(model);
        SKPicture? transient = null;
        try
        {
            ChartFrame frame; SKPicture picture;
            if (_lastHit)
            {
                frame = _frame!; picture = _picture!; _replays++;
            }
            else
            {
                // Capture the revision before recording. A model/data change during recording
                // makes this generation ineligible for reuse, including initial auto-ranging.
                long revision = model.Revision;
                using var recorder = new SKPictureRecorder();
                var recording = recorder.BeginRecording(new(0, 0, (float)width, (float)height));
                frame = _renderer.Render(new SkiaChartCanvas(recording, _resources), model, width, height);
                picture = recorder.EndRecording(); transient = picture; _builds++;
                ClearRecording();
                // One retained picture at most. Very large command/point sets bypass retention;
                // this bounds retained geometry work, not all native texture memory.
                if (eligible && revision == model.Revision && frame.Diagnostics.Count == 0
                    && frame.Statistics.DrawingCommands <= 100000 && frame.Statistics.RenderedPoints <= 1000000)
                {
                    _picture = picture; transient = null; _frame = frame; _model = model;
                    _revision = revision; _width = width; _height = height;
                    _palette = model.Theme.Palette.ToArray();
                    _seriesIds = model.RenderableSeries.Select(s => s.Id).ToArray();
                }
            }
            var overlay = new CountingCanvas(new SkiaChartCanvas(canvas, _resources));
            int saveCount = canvas.Save();
            try
            {
                canvas.ClipRect(new(0, 0, (float)width, (float)height));
                canvas.DrawPicture(picture);
                if (view is not null) ChartRenderer.RenderInteraction(overlay, model, frame, view);
            }
            finally { canvas.RestoreToCount(saveCount); }
            return frame with { Statistics = frame.Statistics with {
                DrawingCommands = frame.Statistics.DrawingCommands + overlay.Commands,
                CpuMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds } };
        }
        catch
        {
            ClearRecording(); throw;
        }
        finally { transient?.Dispose(); }
    }
    private bool ConfigurationMatches(ChartModel model)
    {
        if (model.Theme.Palette.Count != _palette.Length || model.RenderableSeries.Count != _seriesIds.Length) return false;
        for (int i = 0; i < _palette.Length; i++) if (model.Theme.Palette[i] != _palette[i]) return false;
        for (int i = 0; i < _seriesIds.Length; i++) if (model.RenderableSeries[i].Id != _seriesIds[i]) return false;
        return true;
    }
    private static bool CanCache(ChartModel model)
    {
        foreach (var axis in model.XAxes) if (axis.LabelProvider is not null) return false;
        foreach (var axis in model.YAxes) if (axis.LabelProvider is not null) return false;
        foreach (var series in model.RenderableSeries)
            if (series.PaletteProvider is not null || series.DataLabelProvider is not null || series.SelectedIndices.Count != 0)
                return false;
        return true;
    }
    /// <summary>Release the retained picture/model immediately. A subsequent draw rebuilds normally.</summary>
    public void Invalidate()
    { ObjectDisposedException.ThrowIf(_disposed, this); ClearRecording(); }
    private void ClearRecording()
    {
        _picture?.Dispose(); _picture = null; _frame = null; _model = null;
        _revision = -1; _palette = []; _seriesIds = [];
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        ClearRecording(); _resources.Dispose();
    }
}
