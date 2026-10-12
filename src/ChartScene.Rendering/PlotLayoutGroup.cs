using ChartScene.Core;

namespace ChartScene.Rendering;

/// <summary>Finite nonnegative reservations around the plot, including titles and axis bands.</summary>
public readonly record struct PlotMargins
{
    public double Left { get; }
    public double Top { get; }
    public double Right { get; }
    public double Bottom { get; }
    public PlotMargins(double left, double top, double right, double bottom)
    {
        static void Check(double value) { if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value)); }
        Check(left); Check(top); Check(right); Check(bottom);
        Left = left; Top = top; Right = right; Bottom = bottom;
    }
    public static PlotMargins Max(PlotMargins a, PlotMargins b) => new(Math.Max(a.Left,b.Left), Math.Max(a.Top,b.Top), Math.Max(a.Right,b.Right), Math.Max(a.Bottom,b.Bottom));
}

/// <summary>Horizontal aligns left/right plot edges; Vertical aligns top/bottom plot edges.</summary>
[Flags]
public enum PlotAlignment { None = 0, Horizontal = 1, Vertical = 2, Both = Horizontal | Vertical }
public readonly record struct PlotLayoutGroupStatistics(int Members, long Reports, long AggregatePasses, long Notifications);

/// <summary>
/// Framework-independent layout negotiation. Report NATURAL margins, never margins already
/// expanded by the group; this permits shrinking when the largest label/axis disappears.
/// Own the group on one UI thread. It owns no charts, renderers, models or global static registry.
/// </summary>
public sealed class PlotLayoutGroup : IDisposable
{
    private readonly List<Member> _members = [];
    private PlotAlignment _alignment;
    private bool _disposed, _publishing, _dirty;
    private long _reports, _passes, _notifications;
    public PlotLayoutGroupStatistics Statistics => new(_members.Count, _reports, _passes, _notifications);
    public PlotAlignment Alignment
    {
        get => _alignment;
        set
        {
            Check(); Validate(value);
            if (_alignment == value) return;
            _alignment = value; Recalculate();
        }
    }
    public PlotLayoutGroup(PlotAlignment alignment = PlotAlignment.Horizontal) { Validate(alignment); _alignment = alignment; }
    private static void Validate(PlotAlignment value) { if ((value & ~PlotAlignment.Both) != 0) throw new ArgumentOutOfRangeException(nameof(value)); }
    private void Check() => ObjectDisposedException.ThrowIf(_disposed, this);
    public Member Add()
    {
        Check(); var member = new Member(this); _members.Add(member); Recalculate(); return member;
    }
    public sealed class Member : IDisposable
    {
        private readonly PlotLayoutGroup _owner;
        internal bool Reported, Disposed;
        internal PlotMargins Natural;
        /// <summary>The group's shared minima, not the member's own natural margins.</summary>
        public PlotMargins MinimumMargins { get; internal set; }
        public event EventHandler? Changed;
        internal Member(PlotLayoutGroup owner) => _owner = owner;
        /// <summary>Equal reports are O(1), allocate nothing and never invalidate another view.</summary>
        public void Report(PlotMargins natural)
        {
            _owner.Check(); ObjectDisposedException.ThrowIf(Disposed, this); _owner._reports++;
            if (Reported && Natural == natural) return;
            Reported = true; Natural = natural; _owner.Recalculate();
        }
        internal void Notify() { if (!Disposed) Changed?.Invoke(this, EventArgs.Empty); }
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true; Changed = null; _owner._members.Remove(this);
            if (!_owner._disposed) _owner.Recalculate();
        }
    }
    private void Recalculate()
    {
        _dirty = true;
        if (_publishing) return;
        _publishing = true;
        try
        {
            // Reentrant reports/removals are handled in another pass, not during enumeration.
            while (_dirty && !_disposed)
            {
                _dirty = false; _passes++; PlotMargins maximum = default;
                foreach (var member in _members) if (member.Reported) maximum = PlotMargins.Max(maximum, member.Natural);
                var shared = new PlotMargins(
                    _alignment.HasFlag(PlotAlignment.Horizontal) ? maximum.Left : 0,
                    _alignment.HasFlag(PlotAlignment.Vertical) ? maximum.Top : 0,
                    _alignment.HasFlag(PlotAlignment.Horizontal) ? maximum.Right : 0,
                    _alignment.HasFlag(PlotAlignment.Vertical) ? maximum.Bottom : 0);
                List<Member>? changed = null;
                // Publish all computed fields before exposing any callback.
                foreach (var member in _members)
                    if (member.MinimumMargins != shared)
                    { member.MinimumMargins = shared; (changed ??= []).Add(member); }
                if (changed is not null) foreach (var member in changed)
                    if (!member.Disposed && !_disposed) { _notifications++; member.Notify(); }
            }
        }
        finally { _publishing = false; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var member in _members.ToArray()) member.Dispose();
        _members.Clear();
    }
}
