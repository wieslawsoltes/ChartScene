using ChartScene.Core;

namespace ChartScene.Rendering;

/// <summary>
/// Append-only, exact rectangle overlap test for a single layout pass. Screen cells are a
/// broad phase only: final decisions use RectD.Intersects with the requested candidate padding.
/// Accepted order, touching-edge behavior and label spacing match a sequential rectangle scan.
/// Owners serialize access; no global cache retains frames or chart data.
/// </summary>
public sealed class LabelCollisionIndex
{
    private const double CellSize = 32;
    private const int InsertionBudget = 64, QueryBudget = 256;
    private readonly List<RectD> _rectangles = [];
    private readonly List<int> _large = [];
    private readonly Dictionary<(int X, int Y), List<int>> _cells = [];
    private readonly List<uint> _seen = [];
    private uint _stamp;
    public int Count => _rectangles.Count;
    public long ExactComparisons { get; private set; }
    public long FullScans { get; private set; }

    /// <summary>
    /// Adds the uninflated rectangle iff no prior accepted rectangle intersects its padded bounds.
    /// Rejected/invalid candidates never occupy space. Large coordinates or bounds use an exact
    /// stored-rectangle fallback rather than an unbounded enumeration of empty cells.
    /// </summary>
    public bool TryAdd(RectD rectangle, double padding = 2)
    {
        Validate(rectangle);
        if (!double.IsFinite(padding) || padding < 0) throw new ArgumentOutOfRangeException(nameof(padding));
        var query = rectangle.Inflate(padding);
        bool finiteQuery = Finite(query);
        bool Intersects(RectD occupied) => finiteQuery ? occupied.Intersects(query)
            : occupied.Right >= rectangle.X - padding && occupied.X <= rectangle.Right + padding
                && occupied.Bottom >= rectangle.Y - padding && occupied.Y <= rectangle.Bottom + padding;
        // Preserve the exact legacy inflation arithmetic for finite bounds. Overflowing
        // padded bounds use endpoint comparisons, avoiding infinity-minus-infinity NaN.
        bool scan = !finiteQuery;
        var q = scan ? default : Window(query);
        if (scan || Exceeds(q, QueryBudget))
        {
            FullScans++;
            foreach (var occupied in _rectangles)
            {
                ExactComparisons++;
                if (Intersects(occupied)) return false;
            }
        }
        else
        {
            if (++_stamp == 0) { for (int i = 0; i < _seen.Count; i++) _seen[i] = 0; _stamp = 1; }
            for (int y = q.Y0; y <= q.Y1; y++) for (int x = q.X0; x <= q.X1; x++)
                if (_cells.TryGetValue((x, y), out var bucket))
                    foreach (int id in bucket)
                    {
                        if (_seen[id] == _stamp) continue;
                        _seen[id] = _stamp; ExactComparisons++;
                        if (Intersects(_rectangles[id])) return false;
                    }
            foreach (int id in _large)
            {
                ExactComparisons++;
                if (Intersects(_rectangles[id])) return false;
            }
        }
        int next = _rectangles.Count;
        _rectangles.Add(rectangle); _seen.Add(0);
        var bounds = Window(rectangle);
        if (Exceeds(bounds, InsertionBudget)) _large.Add(next);
        else
            for (int y = bounds.Y0; y <= bounds.Y1; y++) for (int x = bounds.X0; x <= bounds.X1; x++)
            {
                if (!_cells.TryGetValue((x, y), out var bucket)) _cells[(x, y)] = bucket = [];
                bucket.Add(next);
            }
        return true;
    }
    private static bool Finite(RectD r) => double.IsFinite(r.X) && double.IsFinite(r.Y)
        && double.IsFinite(r.Width) && double.IsFinite(r.Height) && double.IsFinite(r.Right) && double.IsFinite(r.Bottom);
    private static void Validate(RectD r)
    {
        if (!Finite(r) || r.Width < 0 || r.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(r), "Label rectangles require finite, nonnegative extents.");
    }
    private static int Cell(double value) => (int)Math.Clamp(Math.Floor(value / CellSize), int.MinValue + 1.0, int.MaxValue - 1.0);
    private static (int X0, int X1, int Y0, int Y1) Window(RectD r) => (Cell(r.X), Cell(r.Right), Cell(r.Y), Cell(r.Bottom));
    private static bool Exceeds((int X0, int X1, int Y0, int Y1) w, int budget)
    {
        long columns = (long)w.X1 - w.X0 + 1, rows = (long)w.Y1 - w.Y0 + 1;
        return columns > budget || rows > budget || columns * rows > budget;
    }
}
