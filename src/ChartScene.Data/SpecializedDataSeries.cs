namespace ChartScene.Data;

public class XyDataSeries : DataSeries
{
    public XyDataSeries(int capacity = 256) : base(2, capacity) { }
    public void Append(double x, double y) => AppendPoint(stackalloc double[] { x, y });
    public void AppendRange(double[] x, double[] y) => base.AppendRange(x, y);
    public void Update(int index, double x, double y) => UpdatePoint(index, stackalloc double[] { x, y });
    public void Update(int index, double y) => UpdateChannelRange(index, 1, stackalloc double[] { y });
    public void UpdateRange(int index, double[] y) => UpdateChannelRange(index, 1, y);
}
public sealed class XyDataSeries<TX, TY> : XyDataSeries
{
    public void Append(TX x, TY y) => base.Append(Coordinate(x), Coordinate(y));
    public void AppendRange(IEnumerable<TX> x, IEnumerable<TY> y)
    {
        ArgumentNullException.ThrowIfNull(x); ArgumentNullException.ThrowIfNull(y);
        base.AppendRange(x.Select(Coordinate).ToArray(), y.Select(Coordinate).ToArray());
    }
    private static double Coordinate<T>(T value) => value switch
    {
        DateTime dt => new DateTimeOffset(dt.ToUniversalTime()).ToUnixTimeMilliseconds(),
        DateTimeOffset dt => dt.ToUnixTimeMilliseconds(), TimeSpan time => time.TotalMilliseconds,
        _ => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)
    };
}
public sealed class XyyDataSeries() : DataSeries(3)
{ public void Append(double x, double y1, double y2) => AppendPoint(stackalloc double[] { x, y1, y2 }); }
public sealed class XyzDataSeries() : DataSeries(3)
{ public void Append(double x, double y, double z) => AppendPoint(stackalloc double[] { x, y, z }); }
public sealed class VectorFieldDataSeries() : DataSeries(4)
{ public void Append(double x, double y, double dx, double dy) => AppendPoint(stackalloc double[] { x, y, dx, dy }); }

/// <summary>OHLC rows are finite and ordered, or have all four price channels set to NaN as a gap.</summary>
public sealed class OhlcDataSeries() : DataSeries(5)
{
    public void Append(double x, double open, double high, double low, double close)
        => AppendPoint(stackalloc double[] { x, open, high, low, close });
    protected override void ValidateRow(ReadOnlySpan<double> values)
    {
        int missing = 0; for (int c = 1; c < 5; c++) if (double.IsNaN(values[c])) missing++;
        if (missing == 4) return;
        if (missing != 0 || values[3] > Math.Min(values[1], values[4]) || values[2] < Math.Max(values[1], values[4]) || values[3] > values[2])
            throw new ArgumentException("OHLC low/high must contain open and close; gaps require four NaN price channels.");
    }
}
/// <summary>Box statistics are ordered, or all five statistics are NaN as a gap.</summary>
public sealed class BoxPlotDataSeries() : DataSeries(6)
{
    public void Append(double x, double minimum, double lowerQuartile, double median, double upperQuartile, double maximum)
        => AppendPoint(stackalloc double[] { x, minimum, lowerQuartile, median, upperQuartile, maximum });
    protected override void ValidateRow(ReadOnlySpan<double> values)
    {
        int missing = 0; for (int c = 1; c < 6; c++) if (double.IsNaN(values[c])) missing++;
        if (missing == 5) return;
        if (missing != 0) throw new ArgumentException("Box-plot gaps require all statistics to be NaN.");
        for (int c = 2; c < 6; c++) if (values[c] < values[c - 1]) throw new ArgumentException("Box plot statistics must be ordered.");
    }
}
