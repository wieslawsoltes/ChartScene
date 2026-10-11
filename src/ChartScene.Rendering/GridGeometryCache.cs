using System.Runtime.CompilerServices;
using ChartScene.Core;
using ChartScene.Data;

namespace ChartScene.Rendering;

public readonly record struct GridCellWindow(int FirstColumn, int EndColumn, int FirstRow, int EndRow)
{
    public long CellCount => (long)(EndColumn - FirstColumn) * (EndRow - FirstRow);
}

/// <summary>Binary-search visibility for immutable rectilinear cells, including clipped edge neighbors.</summary>
public static class GridVisibility
{
    private sealed record Topology(bool Uniform);
    private static readonly ConditionalWeakTable<HeatmapGrid, Topology> Topologies = new();
    public static bool IsUniform(HeatmapGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return Topologies.GetValue(grid, static g => new(Uniform(g.XEdges.Span) && Uniform(g.YEdges.Span))).Uniform;
    }
    private static bool Uniform(ReadOnlySpan<double> edges)
    {
        double step = edges[1] - edges[0];
        for (int i = 2; i < edges.Length; i++)
            if (Math.Abs((edges[i] - edges[i - 1]) - step) > Math.Max(1e-12, Math.Abs(step) * 1e-10)) return false;
        return true;
    }
    public static GridCellWindow VisibleCells(HeatmapGrid grid, DoubleRange x, DoubleRange y)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (!x.IsValid || !y.IsValid) throw new ArgumentOutOfRangeException(nameof(x));
        var columns = Find(grid.XEdges.Span, x); var rows = Find(grid.YEdges.Span, y);
        return new(columns.Start, columns.End, rows.Start, rows.End);
    }
    private static (int Start, int End) Find(ReadOnlySpan<double> edges, DoubleRange visible)
    {
        if (visible.Max < edges[0] || visible.Min > edges[^1]) return (0, 0);
        int lo = 0, hi = edges.Length;
        while (lo < hi) { int m = lo + (hi - lo) / 2; if (edges[m] < visible.Min) lo = m + 1; else hi = m; }
        int start = Math.Max(0, lo - 1);
        lo = start; hi = edges.Length;
        while (lo < hi) { int m = lo + (hi - lo) / 2; if (edges[m] <= visible.Max) lo = m + 1; else hi = m; }
        return (start, Math.Min(edges.Length - 1, lo));
    }
}

/// <summary>Weak grid-owned, bounded contour cache. Palette, opacity, camera and pointer changes reuse geometry.</summary>
public static class ContourGeometryCache
{
    private const long MaximumCachedBytes = 16 * 1024 * 1024;
    private sealed class Entry
    {
        public readonly object Gate = new();
        public double[] Levels = [];
        public IReadOnlyList<ContourSegment>? Segments;
    }
    private static readonly ConditionalWeakTable<HeatmapGrid, Entry> Entries = new();
    public static IReadOnlyList<ContourSegment> Capture(HeatmapGrid grid, IReadOnlyList<double> levels)
    {
        ArgumentNullException.ThrowIfNull(grid); ArgumentNullException.ThrowIfNull(levels);
        double[] snapshot = levels.ToArray();
        if (snapshot.Any(v => !double.IsFinite(v))) throw new ArgumentException("Contour levels must be finite.", nameof(levels));
        var entry = Entries.GetValue(grid, static _ => new());
        lock (entry.Gate)
        {
            if (entry.Segments is not null && snapshot.AsSpan().SequenceEqual(entry.Levels)) return entry.Segments;
            var segments = Contours.MarchingSquares(grid, snapshot).ToArray();
            var readOnly = Array.AsReadOnly(segments);
            // At most one level-set per live immutable grid; very large results are not retained.
            if ((long)segments.Length * 40 + (long)snapshot.Length * 8 <= MaximumCachedBytes)
            { entry.Levels = snapshot; entry.Segments = readOnly; }
            else { entry.Levels = []; entry.Segments = null; }
            return readOnly;
        }
    }
}
