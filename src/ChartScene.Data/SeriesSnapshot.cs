using ChartScene.Core;

namespace ChartScene.Data;

/// <summary>Stable read-only owned columns. Full-column extents are cached per channel and sign policy.</summary>
public sealed class SeriesSnapshot
{
    private sealed record Extent(DoubleRange? Value);
    private readonly double[][] _columns;
    private readonly Extent?[] _extents;
    public int Count { get; }
    public int ChannelCount => _columns.Length;
    public long Revision { get; }
    public bool IsSortedAscending { get; }
    public SeriesSnapshot(double[][] columns, long revision, bool sorted)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Length < 2 || columns.Any(c => c is null)) throw new ArgumentException("At least two non-null channels are required.");
        if (columns.Any(c => c.Length != columns[0].Length)) throw new ArgumentException("All channels must have equal lengths.");
        _columns = columns.Select(c => (double[])c.Clone()).ToArray();
        Count = _columns[0].Length;
        for (int c = 0; c < _columns.Length; c++)
            for (int i = 0; i < Count; i++)
                if (double.IsInfinity(_columns[c][i]) || c == 0 && !double.IsFinite(_columns[c][i])) throw new ArgumentException("X must be finite; other channels may contain NaN gaps, not infinity.");
        if (sorted) for (int i = 1; i < Count; i++) if (_columns[0][i] < _columns[0][i - 1]) throw new ArgumentException("Sorted metadata does not match X values.");
        Revision = revision; IsSortedAscending = sorted; _extents = new Extent[checked(ChannelCount * 2)];
    }
    internal SeriesSnapshot(double[][] ownedColumns, long revision, bool sorted, bool takeOwnership)
    {
        _columns = ownedColumns; Count = ownedColumns[0].Length; Revision = revision; IsSortedAscending = sorted;
        _extents = new Extent[checked(ChannelCount * 2)];
    }
    public ReadOnlyMemory<double> Channel(int index) => _columns[index];
    public double this[int channel, int index] => _columns[channel][index];
    public int LowerBound(double x)
    {
        CheckSearch(x); int lo = 0, hi = Count;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (_columns[0][mid] < x) lo = mid + 1; else hi = mid; }
        return lo;
    }
    public int UpperBound(double x)
    {
        CheckSearch(x); int lo = 0, hi = Count;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (_columns[0][mid] <= x) lo = mid + 1; else hi = mid; }
        return lo;
    }
    private void CheckSearch(double x)
    {
        if (!IsSortedAscending) throw new InvalidOperationException("Binary search requires sorted X data.");
        if (double.IsNaN(x)) throw new ArgumentOutOfRangeException(nameof(x));
    }
    public (int Start, int End) VisibleIndices(DoubleRange range, bool neighbors = true)
    {
        if (!range.IsValid) throw new ArgumentOutOfRangeException(nameof(range));
        if (!IsSortedAscending) return (0, Count);
        return (Math.Max(0, LowerBound(range.Min) - (neighbors ? 1 : 0)), Math.Min(Count, UpperBound(range.Max) + (neighbors ? 1 : 0)));
    }
    public DoubleRange? Extents(int channel, DoubleRange? xRange = null, bool positiveOnly = false)
    {
        if ((uint)channel >= (uint)ChannelCount) throw new ArgumentOutOfRangeException(nameof(channel));
        if (xRange is { IsValid: false }) throw new ArgumentOutOfRangeException(nameof(xRange));
        int slot = channel * 2 + (positiveOnly ? 1 : 0);
        if (xRange is null && Volatile.Read(ref _extents[slot]) is { } cached) return cached.Value;
        var (start, end) = xRange is { } r ? VisibleIndices(r, false) : (0, Count);
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = start; i < end; i++)
        {
            if (xRange is { } xr && !xr.Contains(_columns[0][i])) continue;
            double value = _columns[channel][i];
            if (!double.IsFinite(value) || positiveOnly && value <= 0) continue;
            min = Math.Min(min, value); max = Math.Max(max, value);
        }
        DoubleRange? result = null;
        if (double.IsFinite(min))
        {
            if (positiveOnly && min == max)
            {
                double lo = min * .95, hi = min * 1.05;
                if (lo <= 0) lo = min;
                if (!double.IsFinite(hi)) hi = min;
                if (hi == lo) hi = Math.BitIncrement(min);
                result = new(lo, hi);
            }
            else result = DoubleRange.FromExtents(min, max);
        }
        if (xRange is null) Interlocked.CompareExchange(ref _extents[slot], new Extent(result), null);
        return result;
    }
}
