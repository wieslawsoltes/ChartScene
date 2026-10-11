using ChartScene.Charting;
using ChartScene.Core;

namespace ChartScene.Interaction;

/// <summary>
/// Links cursor and rollover views without forwarding input gestures or mutating data/ranges.
/// The selected X axes may have different units, sizes, orientations, ranges and logarithmic scales.
/// Own a group on the views' UI thread and dispose the returned registrations when views detach.
/// Call Refresh after arranging/resizing linked views; unchanged refreshes do not invalidate rendering.
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
        public PointD? ObservedCursor { get; set; }
        public PointD? WrittenCursor { get; set; }
        public bool OwnsCursor { get; set; }
        public long AppliedRevision { get; set; } = -1;
        public RectD AppliedPlot { get; set; }
        public bool AppliedInterpolation { get; set; }
        public Registration(ChartCursorSyncGroup group, ChartController controller, AxisModel axis,
            Func<double, double> toCanonical, Func<double, double> fromCanonical)
        {
            Controller = controller; Axis = axis;
            ToCanonical = toCanonical; FromCanonical = fromCanonical;
            ObservedCursor = controller.View.Cursor;
            ViewHandler = (_, _) => group.ViewChanged(this);
            ModelHandler = (_, _) => group.Refresh();
        }
    }

    private readonly List<Registration> _registrations = [];
    private Registration? _active;
    private double _canonicalX, _perpendicularFraction;
    private bool _updating, _disposed;
    public string Id { get; }
    public int Count => _registrations.Count;
    public ChartController? ActiveController => _active?.Controller;

    public ChartCursorSyncGroup(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    /// <summary>
    /// The conversion pair maps raw axis data values to/from common units (not logarithmic exponents).
    /// The perpendicular cursor position is copied as a plot fraction, not as an equivalent Y value.
    /// Non-Cartesian models are rejected; a destination outside its visible X range has no cursor.
    /// </summary>
    public IDisposable Add(ChartController controller, string xAxisId = "X",
        Func<double, double>? toCanonical = null, Func<double, double>? fromCanonical = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(controller);
        if (controller.Model.CoordinateSystem != CoordinateSystem.Cartesian)
            throw new ArgumentException("Cursor linking requires a Cartesian chart.", nameof(controller));
        if (_registrations.Any(item => ReferenceEquals(item.Controller, controller)))
            throw new ArgumentException("The controller is already registered.", nameof(controller));
        var registration = new Registration(this, controller, controller.Model.FindXAxis(xAxisId),
            toCanonical ?? (value => value), fromCanonical ?? (value => value));
        _registrations.Add(registration);
        controller.ViewChanged += registration.ViewHandler;
        controller.Model.Changed += registration.ModelHandler;
        Refresh();
        return new ActionDisposable(() => Remove(registration));
    }

    private void ViewChanged(Registration source)
    {
        PointD? cursor = source.Controller.View.Cursor;
        bool moved = source.ObservedCursor != cursor;
        source.ObservedCursor = cursor;
        if (_updating || _disposed || (!moved && !ReferenceEquals(source, _active))) return;
        if (!TryPlot(source, out var plot) || cursor is not { IsFinite: true } point || !plot.Contains(point))
        {
            if (ReferenceEquals(source, _active)) Clear();
            return;
        }
        var calculator = new CoordinateCalculator(source.Axis,
            source.Axis.IsVertical ? plot.Y : plot.X, source.Axis.IsVertical ? plot.Height : plot.Width);
        double canonical = source.ToCanonical(calculator.GetDataValue(source.Axis.IsVertical ? point.Y : point.X));
        if (!double.IsFinite(canonical))
        {
            if (ReferenceEquals(source, _active)) Clear();
            return;
        }
        _active = source;
        source.OwnsCursor = false;
        _canonicalX = canonical;
        _perpendicularFraction = source.Axis.IsVertical ? (point.X - plot.X) / plot.Width : (point.Y - plot.Y) / plot.Height;
        Refresh();
    }

    /// <summary>Reprojects the active common-unit cursor after layout, axis or data changes.</summary>
    public void Refresh()
    {
        if (_disposed || _updating || _active is null) return;
        if (!TryPlot(_active, out _)) { Clear(); return; }
        _updating = true;
        try
        {
            foreach (var target in _registrations.ToArray())
            {
                if (ReferenceEquals(target, _active) || target.Controller.HasCapture) continue;
                PointD? cursor = null;
                RectD plot = default;
                if (TryPlot(target, out plot))
                {
                    double value = target.FromCanonical(_canonicalX);
                    if (double.IsFinite(value) && target.Axis.VisibleRange.Contains(value))
                    {
                        var calculator = new CoordinateCalculator(target.Axis,
                            target.Axis.IsVertical ? plot.Y : plot.X, target.Axis.IsVertical ? plot.Height : plot.Width);
                        double coordinate = calculator.GetCoordinate(value);
                        var point = target.Axis.IsVertical
                            ? new PointD(plot.X + plot.Width * _perpendicularFraction, coordinate)
                            : new PointD(coordinate, plot.Y + plot.Height * _perpendicularFraction);
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
        return !plot.IsEmpty && double.IsFinite(plot.Width) && double.IsFinite(plot.Height)
            && registration.Controller.Model.CoordinateSystem == CoordinateSystem.Cartesian
            && registration.Controller.Model.XAxes.Contains(registration.Axis);
    }

    private static void Write(Registration target, PointD? point, RectD plot)
    {
        var controller = target.Controller;
        if (target.OwnsCursor && target.WrittenCursor == point && controller.View.Cursor == point
            && target.AppliedRevision == controller.Model.Revision && target.AppliedPlot == plot
            && target.AppliedInterpolation == controller.InterpolateRollover) return;
        target.OwnsCursor = true;
        target.WrittenCursor = target.ObservedCursor = point;
        target.AppliedRevision = controller.Model.Revision;
        target.AppliedPlot = plot;
        target.AppliedInterpolation = controller.InterpolateRollover;
        controller.View.Cursor = point;
        controller.View.Hits = point is { } position
            ? controller.HitTester.Query(controller.Model, position, 10, nearestX: true, interpolate: controller.InterpolateRollover)
            : [];
        controller.NotifyViewChanged();
    }

    /// <summary>Clears only destination cursors owned by this group; the source is left untouched.</summary>
    public void Clear()
    {
        if (_updating) return;
        _active = null;
        _updating = true;
        try { foreach (var registration in _registrations.ToArray()) ReleaseCursor(registration); }
        finally { _updating = false; }
    }

    private static void ReleaseCursor(Registration registration)
    {
        if (!registration.OwnsCursor) return;
        registration.OwnsCursor = false;
        if (registration.Controller.View.Cursor != registration.WrittenCursor) return;
        registration.Controller.View.Cursor = null;
        registration.Controller.View.Hits = [];
        registration.WrittenCursor = registration.ObservedCursor = null;
        registration.AppliedRevision = -1;
        registration.Controller.NotifyViewChanged();
    }

    private void Remove(Registration registration)
    {
        if (!_registrations.Remove(registration)) return;
        registration.Controller.ViewChanged -= registration.ViewHandler;
        registration.Controller.Model.Changed -= registration.ModelHandler;
        if (ReferenceEquals(_active, registration)) Clear();
        bool updating = _updating;
        _updating = true;
        try { ReleaseCursor(registration); }
        finally { _updating = updating; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
        foreach (var registration in _registrations.ToArray()) Remove(registration);
    }
}
