using ChartScene.Charting;
using ChartScene.Core;

namespace ChartScene.Interaction;

/// <summary>
/// Links cursor and rollover views without forwarding gestures or changing data/ranges.
/// The active cursor stays under the pointer; changes to its plot or axes recompute the
/// common-unit value. Own the group on the UI thread and Refresh after layout completes.
/// </summary>
public sealed class ChartCursorSyncGroup : IDisposable
{
    private sealed class Registration
    {
        public ChartController Controller { get; }
        public AxisModel Axis { get; }
        public Func<double, double> ToCanonical { get; }
        public Func<double, double> FromCanonical { get; }
        public EventHandler ViewHandler { get; }
        public EventHandler ModelHandler { get; }
        public bool IsRegistered { get; set; } = true;
        public PointD? ObservedCursor { get; set; }
        public PointD? WrittenCursor { get; set; }
        public bool OwnsCursor { get; set; }
        public long AppliedRevision { get; set; } = -1;
        public RectD AppliedPlot { get; set; }
        public bool AppliedInterpolation { get; set; }
        public Registration(ChartCursorSyncGroup group, ChartController controller, AxisModel axis,
            Func<double, double> toCanonical, Func<double, double> fromCanonical)
        {
            Controller = controller; Axis = axis; ToCanonical = toCanonical; FromCanonical = fromCanonical;
            ObservedCursor = controller.View.Cursor;
            ViewHandler = (_, _) => group.ViewChanged(this);
            ModelHandler = (_, _) => group.Refresh();
        }
    }

    private readonly List<Registration> _registrations = [];
    // Publish a new membership snapshot only on attach/detach, not on every pointer/frame.
    private Registration[] _members = [];
    private Registration? _active;
    private bool _updating, _disposed;
    public string Id { get; }
    public int Count => _registrations.Count;
    public ChartController? ActiveController => _active?.Controller;

    public ChartCursorSyncGroup(string id)
    { ArgumentException.ThrowIfNullOrWhiteSpace(id); Id = id; }

    /// <summary>
    /// Conversions map raw X data, not logarithmic exponents. The perpendicular coordinate
    /// is a plot fraction, not an assertion of equivalent Y units. Cartesian views only.
    /// </summary>
    public IDisposable Add(ChartController controller, string xAxisId = "X",
        Func<double, double>? toCanonical = null, Func<double, double>? fromCanonical = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(controller);
        if (controller.Model.CoordinateSystem != CoordinateSystem.Cartesian)
            throw new ArgumentException("Cursor linking requires a Cartesian chart.", nameof(controller));
        if (_registrations.Any(item => ReferenceEquals(item.Controller, controller)))
            throw new ArgumentException("The controller is already registered.", nameof(controller));
        var registration = new Registration(this, controller, controller.Model.FindXAxis(xAxisId),
            toCanonical ?? (value => value), fromCanonical ?? (value => value));
        _registrations.Add(registration); _members = _registrations.ToArray();
        controller.ViewChanged += registration.ViewHandler; controller.Model.Changed += registration.ModelHandler;
        Refresh();
        return new ActionDisposable(() => Remove(registration));
    }

    private void ViewChanged(Registration source)
    {
        PointD? cursor = source.Controller.View.Cursor;
        bool moved = source.ObservedCursor != cursor;
        source.ObservedCursor = cursor;
        if (_updating || _disposed || !source.IsRegistered || (!moved && !ReferenceEquals(source, _active))) return;
        if (!ReadSource(source, out var plot, out var point, out _, out _))
        {
            if (ReferenceEquals(source, _active)) Clear();
            return;
        }
        _active = source; source.OwnsCursor = false;
        // The controller has already evaluated hits for this input. Layout/model changes
        // after this event are detected independently by Refresh.
        Remember(source, point, plot);
        Refresh();
    }

    private static bool ReadSource(Registration source, out RectD plot, out PointD point,
        out double canonical, out double perpendicular)
    {
        point = default; canonical = perpendicular = double.NaN;
        if (!TryPlot(source, out plot) || source.Controller.View.Cursor is not { IsFinite: true } cursor || !plot.Contains(cursor)) return false;
        point = cursor;
        var calculator = new CoordinateCalculator(source.Axis,
            source.Axis.IsVertical ? plot.Y : plot.X, source.Axis.IsVertical ? plot.Height : plot.Width);
        canonical = source.ToCanonical(calculator.GetDataValue(source.Axis.IsVertical ? point.Y : point.X));
        perpendicular = source.Axis.IsVertical ? (point.X - plot.X) / plot.Width : (point.Y - plot.Y) / plot.Height;
        return double.IsFinite(canonical) && double.IsFinite(perpendicular);
    }

    /// <summary>
    /// Refresh from the active pointer and CURRENT source plot. Retaining a canonical value
    /// from the previous pointer event would leave peers stale after axis-width negotiation.
    /// Unchanged refreshes do not rebuild hits, allocate membership arrays or notify views.
    /// </summary>
    public void Refresh()
    {
        if (_disposed || _updating || _active is not { } source) return;
        if (!ReadSource(source, out var sourcePlot, out var sourcePoint, out double canonical, out double perpendicular)) { Clear(); return; }
        _updating = true;
        try
        {
            if (!Matches(source, sourcePoint, sourcePlot))
            {
                Remember(source, sourcePoint, sourcePlot);
                source.Controller.View.Hits = source.Controller.HitTester.Query(source.Controller.Model, sourcePoint,
                    10, nearestX: true, interpolate: source.Controller.InterpolateRollover);
                source.Controller.NotifyViewChanged();
            }
            foreach (var target in _members)
            {
                // Callbacks may remove members or dispose the group. Never resurrect an
                // overlay belonging to a detached view from an earlier iteration snapshot.
                if (_disposed || !ReferenceEquals(_active, source)) break;
                if (!target.IsRegistered || ReferenceEquals(target, source) || target.Controller.HasCapture) continue;
                PointD? cursor = null; RectD plot = default;
                if (TryPlot(target, out plot))
                {
                    double value = target.FromCanonical(canonical);
                    if (double.IsFinite(value) && target.Axis.VisibleRange.Contains(value))
                    {
                        var calculator = new CoordinateCalculator(target.Axis,
                            target.Axis.IsVertical ? plot.Y : plot.X, target.Axis.IsVertical ? plot.Height : plot.Width);
                        double coordinate = calculator.GetCoordinate(value);
                        var point = target.Axis.IsVertical
                            ? new PointD(plot.X + plot.Width * perpendicular, coordinate)
                            : new PointD(coordinate, plot.Y + plot.Height * perpendicular);
                        if (point.IsFinite && plot.Inflate(1e-7).Contains(point)) cursor = point;
                    }
                }
                Write(target, cursor, plot);
            }
        }
        finally { _updating = false; }
    }

    private static bool TryPlot(Registration registration, out RectD plot)
    {
        plot = registration.Controller.Frame?.Layout.PlotArea ?? default;
        return registration.IsRegistered && !plot.IsEmpty && double.IsFinite(plot.Width) && double.IsFinite(plot.Height)
            && registration.Controller.Model.CoordinateSystem == CoordinateSystem.Cartesian
            && registration.Controller.Model.XAxes.Contains(registration.Axis);
    }
    private static bool Matches(Registration registration, PointD? point, RectD plot)
        => registration.WrittenCursor == point && registration.Controller.View.Cursor == point
            && registration.AppliedRevision == registration.Controller.Model.Revision && registration.AppliedPlot == plot
            && registration.AppliedInterpolation == registration.Controller.InterpolateRollover;
    private static void Remember(Registration registration, PointD? point, RectD plot)
    {
        registration.WrittenCursor = registration.ObservedCursor = point;
        registration.AppliedRevision = registration.Controller.Model.Revision;
        registration.AppliedPlot = plot; registration.AppliedInterpolation = registration.Controller.InterpolateRollover;
    }
    private static void Write(Registration target, PointD? point, RectD plot)
    {
        if (target.OwnsCursor && Matches(target, point, plot)) return;
        target.OwnsCursor = true; Remember(target, point, plot);
        var controller = target.Controller; controller.View.Cursor = point;
        controller.View.Hits = point is { } position
            ? controller.HitTester.Query(controller.Model, position, 10, nearestX: true, interpolate: controller.InterpolateRollover) : [];
        controller.NotifyViewChanged();
    }

    /// <summary>Clears group-owned peer overlays; the active pointer is left untouched.</summary>
    public void Clear()
    {
        _active = null;
        bool updating = _updating; _updating = true;
        try { foreach (var registration in _members) ReleaseCursor(registration); }
        finally { _updating = updating; }
    }
    private static void ReleaseCursor(Registration registration)
    {
        if (!registration.OwnsCursor) return;
        registration.OwnsCursor = false;
        if (registration.Controller.View.Cursor != registration.WrittenCursor) return;
        registration.Controller.View.Cursor = null; registration.Controller.View.Hits = [];
        registration.WrittenCursor = registration.ObservedCursor = null; registration.AppliedRevision = -1;
        registration.Controller.NotifyViewChanged();
    }
    private void Remove(Registration registration)
    {
        if (!_registrations.Remove(registration)) return;
        registration.IsRegistered = false; _members = _registrations.ToArray();
        registration.Controller.ViewChanged -= registration.ViewHandler; registration.Controller.Model.Changed -= registration.ModelHandler;
        if (ReferenceEquals(_active, registration)) Clear();
        bool updating = _updating; _updating = true;
        try { ReleaseCursor(registration); }
        finally { _updating = updating; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Clear();
        foreach (var registration in _members) Remove(registration);
    }
}
