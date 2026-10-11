using System.ComponentModel;
using ChartScene.Charting;
using ChartScene.Core;

namespace ChartScene.Interaction;

public enum RangeNavigationPart { None, MinimumGrip, MaximumGrip, Body, NewSelection }
public readonly record struct RangeNavigationLayout(RectD Track, RectD Selection, PointD MinimumGrip, PointD MaximumGrip);

/// <summary>
/// UI-independent overview/scrollbar navigation. All gestures operate in normalized axis-scale
/// coordinates, so logarithmic panning preserves ratios. External range/scale edits invalidate
/// an in-progress gesture rather than overwriting the new state with a stale mouse-up or cancel.
/// </summary>
public sealed class AxisRangeNavigator : IDisposable
{
    private sealed record Gesture(DoubleRange Before, AutoRange AutoRange, RectD Track,
        RangeNavigationPart Part, double Anchor, double Minimum, double Maximum);
    private Gesture? _gesture;
    private DoubleRange _fullRange;
    private bool _vertical, _writing, _disposed;
    private double _minimumFraction = 1e-6;
    public AxisModel Axis { get; }
    public UndoManager History { get; }
    public event EventHandler? Changed;
    public bool HasGesture => _gesture is not null;
    public RangeNavigationPart ActivePart => _gesture?.Part ?? RangeNavigationPart.None;
    public bool IsReversed => IsVertical ^ Axis.FlippedCoordinates;
    public bool IsVertical
    {
        get => _vertical;
        set { Check(); if (_vertical == value) return; Cancel(); _vertical = value; Notify(); }
    }
    public DoubleRange FullRange
    {
        get => _fullRange;
        set { Check(); Validate(value); if (_fullRange == value) return; _gesture = null; _fullRange = value; Notify(); }
    }
    public double MinimumSelectionFraction
    {
        get => _minimumFraction;
        set
        {
            Check(); if (!double.IsFinite(value) || value <= 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value));
            Cancel(); _minimumFraction = value; Notify();
        }
    }
    public AxisRangeNavigator(AxisModel axis, DoubleRange fullRange, UndoManager? history = null)
    {
        ArgumentNullException.ThrowIfNull(axis); Axis = axis; Validate(fullRange); _fullRange = fullRange;
        History = history ?? new(); Axis.PropertyChanged += AxisChanged;
    }
    private void Check() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void Validate(DoubleRange range)
    {
        if (!range.IsValid || Axis.Scale == AxisScale.Logarithmic && range.Min <= 0)
            throw new ArgumentOutOfRangeException(nameof(range), "Navigator extents must be finite, ordered and valid for the axis scale.");
    }
    private static double LogRatio(double value, double minimum)
    {
        double relative = (value - minimum) / minimum;
        return relative < .5 ? double.LogP1(relative) : Math.Log(value) - Math.Log(minimum);
    }
    public double Fraction(double value)
    {
        Check(); Validate(FullRange);
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (value <= FullRange.Min) return 0;
        if (value >= FullRange.Max) return 1;
        return Axis.Scale == AxisScale.Logarithmic ? LogRatio(value, FullRange.Min) / LogRatio(FullRange.Max, FullRange.Min) : FullRange.Fraction(value);
    }
    public double ValueAt(double fraction)
    {
        Check(); Validate(FullRange);
        if (!double.IsFinite(fraction)) throw new ArgumentOutOfRangeException(nameof(fraction));
        if (fraction <= 0) return FullRange.Min;
        if (fraction >= 1) return FullRange.Max;
        if (Axis.Scale != AxisScale.Logarithmic) return FullRange.At(fraction);
        double span = LogRatio(FullRange.Max, FullRange.Min);
        double value = span < .5 ? FullRange.Min + FullRange.Min * double.ExpM1(fraction * span) : Math.Exp(Math.Log(FullRange.Min) + fraction * span);
        return Math.Clamp(value, FullRange.Min, FullRange.Max);
    }
    public (double Minimum, double Maximum) Selection => (Fraction(Axis.VisibleRange.Min), Fraction(Axis.VisibleRange.Max));
    public RangeNavigationLayout Layout(RectD track)
    {
        Check(); ValidateTrack(track);
        var (min, max) = Selection;
        PointD At(double fraction)
        {
            if (IsReversed) fraction = 1 - fraction;
            return IsVertical ? new(track.Center.X, track.Y + track.Height * fraction) : new(track.X + track.Width * fraction, track.Center.Y);
        }
        var a = At(min); var b = At(max);
        var selection = IsVertical ? new RectD(track.X, Math.Min(a.Y, b.Y), track.Width, Math.Abs(b.Y - a.Y))
            : new RectD(Math.Min(a.X, b.X), track.Y, Math.Abs(b.X - a.X), track.Height);
        return new(track, selection, a, b);
    }
    private static void ValidateTrack(RectD track)
    {
        if (track.IsEmpty || !double.IsFinite(track.X) || !double.IsFinite(track.Y) || !double.IsFinite(track.Right) || !double.IsFinite(track.Bottom))
            throw new ArgumentOutOfRangeException(nameof(track));
    }
    private double PointerFraction(PointD point, RectD track)
    {
        double value = IsVertical ? (point.Y - track.Y) / track.Height : (point.X - track.X) / track.Width;
        return IsReversed ? 1 - value : value;
    }
    public bool Begin(PointD point, RectD track, bool newSelection = false)
    {
        Check(); ValidateTrack(track);
        if (!point.IsFinite || !track.Contains(point)) return false;
        Cancel(); var layout = Layout(track); var (min, max) = Selection;
        double coordinate = IsVertical ? point.Y : point.X;
        double da = Math.Abs(coordinate - (IsVertical ? layout.MinimumGrip.Y : layout.MinimumGrip.X));
        double db = Math.Abs(coordinate - (IsVertical ? layout.MaximumGrip.Y : layout.MaximumGrip.X));
        RangeNavigationPart part = newSelection ? RangeNavigationPart.NewSelection :
            Math.Min(da, db) <= 8 ? (da <= db ? RangeNavigationPart.MinimumGrip : RangeNavigationPart.MaximumGrip) : RangeNavigationPart.Body;
        double anchor = PointerFraction(point, track);
        _gesture = new(Axis.VisibleRange, Axis.AutoRange, track, part, anchor, min, max);
        if (part == RangeNavigationPart.Body && !layout.Selection.Contains(point))
        {
            double size = Math.Clamp(max - min, MinimumSelectionFraction, 1);
            double start = Math.Clamp(anchor - size / 2, 0, 1 - size);
            Apply(start, start + size);
            _gesture = _gesture with { Minimum = start, Maximum = start + size };
        }
        Notify(); return true;
    }
    public void Move(PointD point)
    {
        Check();
        if (_gesture is not { } gesture || !point.IsFinite) return;
        double current = PointerFraction(point, gesture.Track), min = gesture.Minimum, max = gesture.Maximum;
        switch (gesture.Part)
        {
            case RangeNavigationPart.MinimumGrip:
                max = Math.Max(MinimumSelectionFraction, max);
                min = Math.Clamp(current, 0, max - MinimumSelectionFraction); break;
            case RangeNavigationPart.MaximumGrip:
                min = Math.Min(1 - MinimumSelectionFraction, min);
                max = Math.Clamp(current, min + MinimumSelectionFraction, 1); break;
            case RangeNavigationPart.NewSelection:
                min = Math.Clamp(Math.Min(gesture.Anchor, current), 0, 1 - MinimumSelectionFraction);
                max = Math.Clamp(Math.Max(gesture.Anchor, current), min + MinimumSelectionFraction, 1); break;
            default:
                double size = Math.Clamp(max - min, MinimumSelectionFraction, 1);
                min = Math.Clamp(min + current - gesture.Anchor, 0, 1 - size); max = min + size; break;
        }
        Apply(min, max);
    }
    public void End(PointD point)
    {
        Check(); if (_gesture is not { } gesture) return;
        Move(point); _gesture = null;
        Record("Overview navigation", gesture.Before, gesture.AutoRange); Notify();
    }
    public void Cancel()
    {
        Check(); var gesture = _gesture; _gesture = null;
        if (gesture is not null) { Restore(gesture.Before, gesture.AutoRange); Notify(); }
    }
    public void Fit() => Change("Overview fit", () => Apply(0, 1));
    /// <summary>Pan by a fraction of the current selection span; positive moves toward increasing data.</summary>
    public void Pan(double amount)
    {
        Check(); if (!double.IsFinite(amount)) throw new ArgumentOutOfRangeException(nameof(amount));
        Change("Overview pan", () =>
        {
            var (min, max) = Selection; double size = Math.Clamp(max - min, MinimumSelectionFraction, 1);
            double start = Math.Clamp(min + amount * size, 0, 1 - size); Apply(start, start + size);
        });
    }
    public void Zoom(double factor, double anchor = .5)
    {
        Check(); if (!double.IsFinite(factor) || factor <= 0 || !double.IsFinite(anchor) || anchor < 0 || anchor > 1) throw new ArgumentOutOfRangeException(nameof(factor));
        Change("Overview zoom", () =>
        {
            var (min, max) = Selection; double size = Math.Clamp((max - min) * factor, MinimumSelectionFraction, 1);
            double start = Math.Clamp(min + (max - min) * anchor - size * anchor, 0, 1 - size); Apply(start, start + size);
        });
    }
    /// <summary>Used by accessible scroll controls; the normalized viewport start is clamped, never the span.</summary>
    public void SetStartFraction(double value)
    {
        Check(); if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        Change("Overview position", () =>
        {
            var (min, max) = Selection; double size = Math.Clamp(max - min, MinimumSelectionFraction, 1);
            double start = Math.Clamp(value, 0, 1 - size); Apply(start, start + size);
        });
    }
    private void Change(string description, Action operation)
    {
        Check(); Cancel(); var before = Axis.VisibleRange; var autoRange = Axis.AutoRange;
        operation(); Record(description, before, autoRange);
    }
    private void Apply(double min, double max)
    {
        double a = ValueAt(min), b = ValueAt(max);
        if (!double.IsFinite(a) || !double.IsFinite(b) || a >= b || !double.IsFinite(b - a)) return;
        var range = new DoubleRange(a, b);
        if (Axis.VisibleRange != range) Restore(range, AutoRange.Never);
    }
    private void Restore(DoubleRange range, AutoRange autoRange)
    {
        _writing = true;
        try { Axis.AutoRange = autoRange; Axis.VisibleRange = range; }
        finally { _writing = false; }
    }
    private void Record(string description, DoubleRange before, AutoRange autoRange)
    {
        var after = Axis.VisibleRange; var afterAuto = Axis.AutoRange;
        if (before == after && autoRange == afterAuto) return;
        // History remains usable when this navigator detaches; commands reference the axis, not the control.
        var axis = Axis;
        void Set(DoubleRange range, AutoRange auto) { axis.AutoRange = auto; axis.VisibleRange = range; }
        History.RecordExecuted(new DelegateCommand(description, () => Set(after, afterAuto), () => Set(before, autoRange)));
    }
    private void AxisChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_writing && e.PropertyName is nameof(AxisModel.VisibleRange) or nameof(AxisModel.Scale) or nameof(AxisModel.LogarithmicBase) or nameof(AxisModel.FlippedCoordinates)) _gesture = null;
        Notify();
    }
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose()
    {
        if (_disposed) return;
        Cancel(); Axis.PropertyChanged -= AxisChanged; _disposed = true;
    }
}
