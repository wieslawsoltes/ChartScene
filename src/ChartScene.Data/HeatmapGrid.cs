namespace ChartScene.Data;

/// <summary>Immutable rectilinear cells; row-major Z values and NaN transparency.</summary>
public sealed class HeatmapGrid
{
    private readonly double[] _x, _y, _z;
    public int Width => _x.Length - 1;
    public int Height => _y.Length - 1;
    public ReadOnlyMemory<double> XEdges => _x;
    public ReadOnlyMemory<double> YEdges => _y;
    public ReadOnlyMemory<double> Values => _z;
    public double this[int x, int y]
    {
        get
        {
            if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x));
            if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
            return _z[y * Width + x];
        }
    }
    public HeatmapGrid(double[] xEdges, double[] yEdges, double[] values)
    {
        ArgumentNullException.ThrowIfNull(xEdges); ArgumentNullException.ThrowIfNull(yEdges); ArgumentNullException.ThrowIfNull(values);
        if (xEdges.Length < 2 || yEdges.Length < 2 || values.LongLength != (long)(xEdges.Length - 1) * (yEdges.Length - 1)) throw new ArgumentException("Grid dimensions do not match.");
        static void Edges(double[] a)
        {
            for (int i = 0; i < a.Length; i++) if (!double.IsFinite(a[i]) || i > 0 && a[i] <= a[i - 1]) throw new ArgumentException("Cell edges must be finite and strictly increasing.");
        }
        Edges(xEdges); Edges(yEdges);
        if (values.Any(double.IsInfinity)) throw new ArgumentException("Infinity is not a valid heatmap value.");
        _x = (double[])xEdges.Clone(); _y = (double[])yEdges.Clone(); _z = (double[])values.Clone();
    }
    public static HeatmapGrid Uniform(int width, int height, double xStart, double xStep, double yStart, double yStep, Func<int, int, double> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (width < 1 || height < 1 || (long)width * height > Array.MaxLength || !double.IsFinite(xStart) || !double.IsFinite(yStart)
            || !double.IsFinite(xStep) || !double.IsFinite(yStep) || xStep <= 0 || yStep <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        return new(Enumerable.Range(0, checked(width + 1)).Select(i => xStart + i * xStep).ToArray(),
            Enumerable.Range(0, checked(height + 1)).Select(i => yStart + i * yStep).ToArray(),
            Enumerable.Range(0, checked(width * height)).Select(i => value(i % width, i / width)).ToArray());
    }
}
