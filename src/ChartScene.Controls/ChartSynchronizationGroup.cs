using ChartScene.Core;
using ChartScene.Interaction;
using Microsoft.UI.Xaml;

namespace ChartScene.Controls;

/// <summary>
/// Reusable Uno adapter for data-space range and cursor synchronization. Registrations follow
/// Loaded/Unloaded, model replacement and completed rendering. All operations use the UI thread.
/// The group never owns or disposes chart models, data series or controls.
/// </summary>
public sealed class ChartSynchronizationGroup : IDisposable
{
    private sealed class Link : IDisposable
    {
        private readonly ChartSynchronizationGroup _owner;
        private readonly ChartSurface _surface;
        private readonly string _axisId;
        private readonly Func<double, double>? _toCanonical, _fromCanonical;
        private readonly bool _ranges, _cursors;
        private IDisposable? _rangeRegistration, _cursorRegistration;
        private readonly long _modelCallback;
        private bool _disposed;
        public ChartSurface Surface => _surface;
        public Link(ChartSynchronizationGroup owner, ChartSurface surface, string axisId,
            Func<double, double>? toCanonical, Func<double, double>? fromCanonical, bool ranges, bool cursors)
        {
            _owner = owner; _surface = surface; _axisId = axisId;
            _toCanonical = toCanonical; _fromCanonical = fromCanonical; _ranges = ranges; _cursors = cursors;
            _modelCallback = surface.RegisterPropertyChangedCallback(ChartSurface.ModelProperty, (_, _) => Bind());
            surface.Loaded += Loaded; surface.Unloaded += Unloaded; surface.RenderCompleted += Rendered;
            try { Bind(); }
            catch { Dispose(); throw; }
        }
        private void Loaded(object sender, RoutedEventArgs e) => Bind();
        private void Unloaded(object sender, RoutedEventArgs e) => Unbind();
        private void Rendered(object? sender, ChartScene.Rendering.ChartFrame frame) => _owner.Cursors.Refresh();
        private void Bind()
        {
            Unbind();
            if (_disposed || !_surface.IsLoaded || _surface.Model is null) return;
            try
            {
                if (_ranges) _rangeRegistration = _owner.Ranges.Add(_surface.Model.FindXAxis(_axisId), _toCanonical, _fromCanonical);
                if (_cursors) _cursorRegistration = _owner.Cursors.Add(_surface.Controller, _axisId, _toCanonical, _fromCanonical);
            }
            catch { Unbind(); throw; }
        }
        private void Unbind()
        {
            _cursorRegistration?.Dispose(); _cursorRegistration = null;
            _rangeRegistration?.Dispose(); _rangeRegistration = null;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _surface.Loaded -= Loaded; _surface.Unloaded -= Unloaded; _surface.RenderCompleted -= Rendered;
            _surface.UnregisterPropertyChangedCallback(ChartSurface.ModelProperty, _modelCallback);
            Unbind(); _owner._links.Remove(this);
        }
    }
    private readonly List<Link> _links = [];
    private bool _disposed;
    public RangeSyncGroup Ranges { get; }
    public ChartCursorSyncGroup Cursors { get; }
    public int Count => _links.Count;
    public ChartSynchronizationGroup(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Ranges = new(id); Cursors = new(id);
    }
    public IDisposable Add(ChartSurface surface, string xAxisId = "X",
        Func<double, double>? toCanonical = null, Func<double, double>? fromCanonical = null,
        bool synchronizeRanges = true, bool synchronizeCursors = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(surface);
        if (_links.Any(link => ReferenceEquals(link.Surface, surface)))
            throw new ArgumentException("The surface is already linked.", nameof(surface));
        var link = new Link(this, surface, xAxisId, toCanonical, fromCanonical, synchronizeRanges, synchronizeCursors);
        _links.Add(link);
        return new ActionDisposable(link.Dispose);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var link in _links.ToArray()) link.Dispose();
        Cursors.Dispose(); Ranges.Dispose();
    }
}
