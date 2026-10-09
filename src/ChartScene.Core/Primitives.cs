using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ChartScene.Core;

public readonly record struct PointD(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
    public static PointD operator *(PointD a, double b) => new(a.X * b, a.Y * b);
    public double LengthSquared => X * X + Y * Y;
}

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public PointD Center => new(X + Width / 2, Y + Height / 2);
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(PointD p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    public bool Intersects(RectD r) => r.Right >= X && r.X <= Right && r.Bottom >= Y && r.Y <= Bottom;
    public RectD Inflate(double v) => new(X - v, Y - v, Width + 2 * v, Height + 2 * v);
    public static RectD FromPoints(PointD a, PointD b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}

/// <summary>A finite, ordered, non-empty numeric interval. The default struct is invalid; use Unit instead.</summary>
public readonly record struct DoubleRange
{
    public double Min { get; }
    public double Max { get; }
    public double Size => Max - Min;
    public double Center => Min + Size / 2;
    public bool IsValid => double.IsFinite(Min) && double.IsFinite(Max) && Min < Max && double.IsFinite(Size);
    public static DoubleRange Unit => new(0, 1);
    public DoubleRange(double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max || !double.IsFinite(max - min))
            throw new ArgumentOutOfRangeException(nameof(max), "Ranges must have finite ordered endpoints and a finite positive extent.");
        Min = min; Max = max;
    }
    public bool Contains(double value) => value >= Min && value <= Max;
    public double Fraction(double value) => (value - Min) / Size;
    public double At(double fraction) => Min + fraction * Size;
    public DoubleRange Pan(double delta) => new(Min + delta, Max + delta);
    public DoubleRange Zoom(double factor, double anchor = .5)
    {
        if (!double.IsFinite(factor) || factor <= 0 || !double.IsFinite(anchor)) throw new ArgumentOutOfRangeException(nameof(factor));
        double origin = At(anchor);
        return new(origin + (Min - origin) * factor, origin + (Max - origin) * factor);
    }
    public DoubleRange Grow(double lowerFraction, double upperFraction) => new(Min - Size * lowerFraction, Max + Size * upperFraction);
    public DoubleRange Union(DoubleRange other) => new(Math.Min(Min, other.Min), Math.Max(Max, other.Max));
    public static DoubleRange FromExtents(double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max)) return Unit;
        if (min > max) (min, max) = (max, min);
        if (min == max)
        {
            double radius = Math.Max(1e-9, Math.Abs(min) * .05);
            double lo = min - radius, hi = max + radius;
            if (!double.IsFinite(lo)) lo = min;
            if (!double.IsFinite(hi)) hi = max;
            if (lo == hi) { lo = Math.BitDecrement(min); hi = Math.BitIncrement(max); }
            return new(lo, hi);
        }
        return new(min, max);
    }
    public override string ToString() => FormattableString.Invariant($"[{Min:G8}, {Max:G8}]");
}

public readonly record struct ColorRgba(byte R, byte G, byte B, byte A = 255)
{
    public static ColorRgba Transparent => new(0, 0, 0, 0);
    public static ColorRgba White => new(255, 255, 255);
    public static ColorRgba Black => new(0, 0, 0);
    public ColorRgba WithAlpha(byte alpha) => this with { A = alpha };
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    public static ColorRgba Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string hex = value.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8)) throw new FormatException("Use #RRGGBB or #RRGGBBAA.");
        byte B(int index) => byte.Parse(hex.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new(B(0), B(2), B(4), hex.Length == 8 ? B(6) : (byte)255);
    }
    public static ColorRgba Lerp(ColorRgba a, ColorRgba b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return new(Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B), Mix(a.A, b.A));
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; RaisePropertyChanged(name); return true;
    }
    protected void RaisePropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class ActionDisposable(Action action) : IDisposable
{
    private Action? _action = action;
    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}

public static class Geometry
{
    public static double DistanceToSegment(PointD p, PointD a, PointD b, out double fraction)
    {
        PointD d = b - a;
        fraction = d.LengthSquared <= 0 ? 0 : Math.Clamp(((p.X - a.X) * d.X + (p.Y - a.Y) * d.Y) / d.LengthSquared, 0, 1);
        return Math.Sqrt((p - (a + d * fraction)).LengthSquared);
    }
    // Liang-Barsky clipping avoids enormous off-screen float coordinates entering GPU paths.
    public static bool ClipLine(RectD r, ref PointD a, ref PointD b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, t0 = 0, t1 = 1;
        bool Clip(double p, double q)
        {
            if (p == 0) return q >= 0;
            double t = q / p;
            if (p < 0) { if (t > t1) return false; t0 = Math.Max(t0, t); }
            else { if (t < t0) return false; t1 = Math.Min(t1, t); }
            return true;
        }
        if (!a.IsFinite || !b.IsFinite || !Clip(-dx, a.X-r.X) || !Clip(dx, r.Right-a.X) || !Clip(-dy, a.Y-r.Y) || !Clip(dy, r.Bottom-a.Y)) return false;
        PointD start = a;
        a = new(start.X + t0 * dx, start.Y + t0 * dy);
        b = new(start.X + t1 * dx, start.Y + t1 * dy);
        return true;
    }
}
