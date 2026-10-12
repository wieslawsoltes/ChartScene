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
        RangeNavigationPart Part, double Anchor, double Minimum, double Maximum, DoubleRange AnchorRange, AutoRange AnchorAutoRange);
    private Gesture? _gesture;
    private DoubleRange _fullRange;
    private bool _vertical, _disposed, _disposing;
    private long _changeVersion;
    private string? _expectedProperty;
    private DoubleRange _expectedRange;
    private AutoRange _expectedAutoRange;
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
        set { Check(); if (_vertical == value) return; if (!CancelPending()) return; _vertical = value; Supersede(); Notify(); }
    }
    public DoubleRange FullRange
    {
        get => _fullRange;
        set { Check(); Validate(value); if (_fullRange == value) return; Supersede(); _fullRange = value; Notify(); }
    }
    public double MinimumSelectionFraction
    {
        get => _minimumFraction;
        set
        {
            Check(); if (!double.IsFinite(value) || value <= 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value));
            if (_minimumFraction == value || !CancelPending()) return; _minimumFraction = value; Supersede(); Notify();
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
        if (!CancelPending()) return false; var layout = Layout(track); var (min, max) = Selection;
        double coordinate = IsVertical ? point.Y : point.X;
        double da = Math.Abs(coordinate - (IsVertical ? layout.MinimumGrip.Y : layout.MinimumGrip.X));
        double db = Math.Abs(coordinate - (IsVertical ? layout.MaximumGrip.Y : layout.MaximumGrip.X));
        RangeNavigationPart part = newSelection ? RangeNavigationPart.NewSelection :
            Math.Min(da, db) <= 8 ? (da <= db ? RangeNavigationPart.MinimumGrip : RangeNavigationPart.MaximumGrip) : RangeNavigationPart.Body;
        double anchor = PointerFraction(point, track);
        var gesture = new Gesture(Axis.VisibleRange, Axis.AutoRange, track, part, anchor, min, max, Axis.VisibleRange, Axis.AutoRange);
        _gesture = gesture;
        if (part == RangeNavigationPart.Body && !layout.Selection.Contains(point))
        {
            double size = Math.Clamp(max - min, MinimumSelectionFraction, 1);
            double start = Math.Clamp(anchor - size / 2, 0, 1 - size);
            Apply(start, start + size);
            // A data-source notification may replace extents while Apply invokes subscribers.
            if (!ReferenceEquals(_gesture, gesture)) { Notify(); return false; }
            _gesture = gesture with { Minimum = start, Maximum = start + size, AnchorRange = Axis.VisibleRange, AnchorAutoRange = Axis.AutoRange };
        }
        Notify(); return !_disposed && _gesture is not null;
    }
    public void Move(PointD point)
    {
        Check();
        if (_gesture is not { } gesture || !point.IsFinite) return;
        double current = PointerFraction(point, gesture.Track), min = gesture.Minimum, max = gesture.Maximum;
        // Avoid logarithmic round-trip drift and phantom history for stationary or
        // returned-to-origin gestures. Restore the exact recorded coordinates, not exp(log(x)).
        if (current == gesture.Anchor && gesture.Part != RangeNavigationPart.NewSelection)
        {
            if (Axis.VisibleRange != gesture.AnchorRange || Axis.AutoRange != gesture.AnchorAutoRange)
                Restore(gesture.AnchorRange, gesture.AnchorAutoRange);
            return;
        }
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
        Move(point);
        if (!ReferenceEquals(_gesture, gesture)) return;
        _gesture = null;
        Record("Overview navigation", gesture.Before, gesture.AutoRange); Notify();
    }
    public void Cancel()
    {
        Check(); CancelPending();
    }
    private bool CancelPending()
    {
        var gesture = _gesture; Supersede();
        long version = _changeVersion;
        if (gesture is not null) { Restore(gesture.Before, gesture.AutoRange); Notify(); }
        return !_disposed && version == _changeVersion;
    }
    private void Supersede() { _changeVersion++; _gesture = null; }
    public void Fit() => Change("Overview fit", () => Apply(0, 1));
    /// <summary>Pan by a fraction of the current selection span; positive moves toward increasing data.</summary>
    public void Pan(double amount)
    {
        Check(); if (!double.IsFinite(amount)) throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount == 0) return;
        Change("Overview pan", () =>
        {
            var (min, max) = Selection; double size = Math.Clamp(max - min, MinimumSelectionFraction, 1);
            double start = Math.Clamp(min + amount * size, 0, 1 - size); Apply(start, start + size);
        });
    }
    public void Zoom(double factor, double anchor = .5)
    {
        Check(); if (!double.IsFinite(factor) || factor <= 0 || !double.IsFinite(anchor) || anchor < 0 || anchor > 1) throw new ArgumentOutOfRangeException(nameof(factor));
        if (factor == 1) return;
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
        if (value == Selection.Minimum && FullRange.Contains(Axis.VisibleRange.Min) && FullRange.Contains(Axis.VisibleRange.Max)) return;
        Change("Overview position", () =>
        {
            var (min, max) = Selection; double size = Math.Clamp(max - min, MinimumSelectionFraction, 1);
            double start = Math.Clamp(value, 0, 1 - size); Apply(start, start + size);
        });
    }
    private void Change(string description, Action operation)
    {
        Check(); if (!CancelPending()) return;
        long version = _changeVersion;
        var before = Axis.VisibleRange; var autoRange = Axis.AutoRange;
        operation();
        // Callbacks can replace the range, dispose this navigator or start a newer operation.
        // Do not claim their edits in the unfinished operation's undo command.
        if (!_disposed && version == _changeVersion) Record(description, before, autoRange);
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
        long version = _changeVersion;
        try
        {
            // Consume exactly our own synchronous property notification, before notifying
            // consumers. A blanket "writing" flag incorrectly masks nested external writes.
            _expectedAutoRange = autoRange; _expectedProperty = nameof(AxisModel.AutoRange);
            Axis.AutoRange = autoRange; _expectedProperty = null;
            if (_disposed || version != _changeVersion || Axis.AutoRange != autoRange) return;
            _expectedRange = range; _expectedProperty = nameof(AxisModel.VisibleRange);
            Axis.VisibleRange = range;
        }
        catch { Supersede(); throw; }
        finally { _expectedProperty = null; }
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
        bool own = e.PropertyName == _expectedProperty && (e.PropertyName switch
        {
            nameof(AxisModel.AutoRange) => Axis.AutoRange == _expectedAutoRange,
            nameof(AxisModel.VisibleRange) => Axis.VisibleRange == _expectedRange,
            _ => false
        });
        if (own) _expectedProperty = null;
        else if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(AxisModel.AutoRange) or
                 nameof(AxisModel.VisibleRange) or nameof(AxisModel.Scale) or nameof(AxisModel.LogarithmicBase) or
                 nameof(AxisModel.FlippedCoordinates)) Supersede();
        Notify();
    }
    private void Notify() { if (!_disposed) Changed?.Invoke(this, EventArgs.Empty); }
    public void Dispose()
    {
        if (_disposed || _disposing) return;
        _disposing = true;
        try { CancelPending(); }
        finally
        {
            _disposed = true; Supersede(); Axis.PropertyChanged -= AxisChanged;
            Changed = null; _disposing = false;
        }
    }
}
