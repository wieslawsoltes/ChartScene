using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Charting;
using ChartScene.Rendering;

namespace ChartScene.Interaction;

/// <summary>Bounded screen-space broad phase. Large query radii scan stored regions, never billions of empty cells.</summary>
public sealed class HitTestIndex
{
    private readonly Dictionary<(int X, int Y), List<HitRegion>> _cells = [];
    private readonly List<HitRegion> _large = [];
    private readonly HitRegion[] _regions;
    private const double CellSize = 48;
    public bool LastQueryUsedFullScan { get; private set; }
    public HitTestIndex(IEnumerable<HitRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions); _regions = regions.ToArray();
        foreach (var region in _regions)
        {
            var r = region.Bounds;
            if (region.DataIndex < 0 || !region.Position.IsFinite || !double.IsFinite(region.Radius) || region.Radius < 0
                || !double.IsFinite(r.X) || !double.IsFinite(r.Y) || !double.IsFinite(r.Right) || !double.IsFinite(r.Bottom) || r.Width < 0 || r.Height < 0)
                throw new ArgumentException("Hit regions require finite positions, bounds, nonnegative radii and source indices.", nameof(regions));
            int x0 = Cell(r.X), x1 = Cell(r.Right), y0 = Cell(r.Y), y1 = Cell(r.Bottom);
            if (TooManyCells(x0, x1, y0, y1, 64)) { _large.Add(region); continue; }
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                if (!_cells.TryGetValue((x, y), out var bucket)) _cells[(x, y)] = bucket = [];
                bucket.Add(region);
            }
        }
    }
    private static int Cell(double value) => (int)Math.Clamp(Math.Floor(value / CellSize), int.MinValue + 1.0, int.MaxValue - 1.0);
    private static bool TooManyCells(int x0, int x1, int y0, int y1, int budget)
    {
        long width = (long)x1 - x0 + 1, height = (long)y1 - y0 + 1;
        return width > budget || height > budget || width * height > budget;
    }
    private static bool Intersects(HitRegion region, PointD point, double radius)
        => point.X + radius >= region.Bounds.X && point.X - radius <= region.Bounds.Right
            && point.Y + radius >= region.Bounds.Y && point.Y - radius <= region.Bounds.Bottom;
    public IEnumerable<HitRegion> Query(PointD point, double radius)
    {
        if (!double.IsFinite(radius) || radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
        if (!point.IsFinite) yield break;
        int x0 = Cell(point.X - radius), x1 = Cell(point.X + radius), y0 = Cell(point.Y - radius), y1 = Cell(point.Y + radius);
        LastQueryUsedFullScan = TooManyCells(x0, x1, y0, y1, 4096);
        if (LastQueryUsedFullScan)
        {
            foreach (var region in _regions) if (Intersects(region, point, radius)) yield return region;
            yield break;
        }
        var visited = new HashSet<HitRegion>();
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            if (_cells.TryGetValue((x, y), out var bucket))
                foreach (var region in bucket) if (visited.Add(region) && Intersects(region, point, radius)) yield return region;
        foreach (var region in _large) if (Intersects(region, point, radius)) yield return region;
    }
}

public sealed class ChartHitTester
{
    private ChartFrame? _frame;
    private HitTestIndex? _index;
    /// <summary>Cumulative count of lazy broad-phase builds; nearest-X rollover does not require this index.</summary>
    public long HitRegionIndexBuildCount { get; private set; }
    public void SetFrame(ChartFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (ReferenceEquals(frame, _frame)) return;
        _frame = frame; _index = null;
    }
    public IReadOnlyList<SeriesHit> Query(ChartModel model, PointD point, double radius = 10, bool nearestX = false, bool interpolate = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!double.IsFinite(radius) || radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
        if (!point.IsFinite || _frame is null || !_frame.Layout.PlotArea.Contains(point)) return [];
        var output = new Dictionary<RenderableSeries, SeriesHit>();
        // Old screen bounds must not be paired with newly edited data or hidden/removed series.
        if (!nearestX && _frame.Statistics.ModelRevision == model.Revision && _frame.HitRegions.Count > 0)
        {
            if (_index is null) { _index = new(_frame.HitRegions); HitRegionIndexBuildCount++; }
            foreach (var hit in _index.Query(point, radius))
            {
                if (!hit.Series.IsVisible || hit.Series.IsCircular || !model.RenderableSeries.Contains(hit.Series) || hit.Series.DataSeries is not { } source) continue;
                var data = source.Snapshot(); if ((uint)hit.DataIndex >= (uint)data.Count) continue;
                double distance = hit.Radius > 0 ? Math.Max(0, Math.Sqrt((point - hit.Position).LengthSquared) - hit.Radius) : DistanceToRectangle(point, hit.Bounds);
                if (distance > radius) continue;
                var value = new SeriesHit(hit.Series, hit.DataIndex, data[0, hit.DataIndex], data[hit.Series.YChannel, hit.DataIndex], hit.Position, distance, Details(hit.Series, data, hit.DataIndex));
                if (!output.TryGetValue(hit.Series, out var old) || value.Distance < old.Distance) output[hit.Series] = value;
            }
        }
        var stacks = StackCalculator.Capture(model);
        foreach (var series in model.RenderableSeries.Where(s => s.IsVisible))
        {
            var projection = _frame.Layout.Projection(model, series);
            if (series.HeatmapGrid is { } grid)
            {
                PointD data = projection.ToData(point);
                int x = CellAt(grid.XEdges.Span, data.X), y = CellAt(grid.YEdges.Span, data.Y);
                var colorScale = HeatmapColorScale.Capture(series);
                if (x >= 0 && y >= 0 && double.IsFinite(colorScale.Fraction(grid[x, y])))
                {
                    var center = new PointD(grid.XEdges.Span[x] + (grid.XEdges.Span[x + 1] - grid.XEdges.Span[x]) / 2,
                        grid.YEdges.Span[y] + (grid.YEdges.Span[y + 1] - grid.YEdges.Span[y]) / 2);
                    var pixel = projection.ToScreen(center.X, center.Y);
                    if (pixel.IsFinite) output[series] = new(series, y * grid.Width + x, center.X, center.Y, pixel, 0, $"Z={grid[x, y]:G6}");
                }
                continue;
            }
            if (series.DataSeries is not { } source) continue;
            var snapshot = stacks.TryGetValue(series, out var captured) ? captured.Snapshot : source.Snapshot(); if (snapshot.Count == 0) continue;
            if (series.IsCircular)
            {
                var hit = PieHit(series, snapshot, _frame.Layout.PlotArea, point); if (hit is not null) output[series] = hit;
                continue;
            }
            if (!nearestX && output.ContainsKey(series)) continue;
            var coordinates = projection.ToData(point); if (!coordinates.IsFinite) continue;
            bool cartesian = model.CoordinateSystem == CoordinateSystem.Cartesian && series.Kind is not (SeriesKind.PolarLine or SeriesKind.Radar or SeriesKind.TernaryLine or SeriesKind.TernaryScatter);
            int start = 0, end = snapshot.Count;
            if (cartesian && snapshot.IsSortedAscending)
            {
                if (nearestX)
                {
                    int position = snapshot.LowerBound(coordinates.X);
                    // Include complete duplicate-X groups; choose the closest screen point on ties.
                    start = position > 0 ? snapshot.LowerBound(snapshot[0, position - 1]) : 0;
                    end = position < snapshot.Count ? snapshot.UpperBound(snapshot[0, position]) : snapshot.Count;
                }
                else
                {
                    var a = projection.ToData(new(point.X - radius, point.Y - radius)); var b = projection.ToData(new(point.X + radius, point.Y + radius));
                    if (!double.IsNaN(a.X) && !double.IsNaN(b.X))
                    {
                        start = Math.Max(0, snapshot.LowerBound(Math.Min(a.X, b.X)) - 1);
                        end = Math.Min(snapshot.Count, snapshot.UpperBound(Math.Max(a.X, b.X)) + 1);
                    }
                }
            }
            StackedSeriesValues? stacked = stacks.GetValueOrDefault(series);
            double Y(int i) => stacked is null ? snapshot[series.YChannel, i] : stacked.Upper[i];
            SeriesHit? best = null; double bestSecondaryDistance = double.PositiveInfinity;
            void Consider(SeriesHit candidate)
            {
                double secondary = (candidate.Position - point).LengthSquared;
                if (best is null || candidate.Distance < best.Distance || candidate.Distance == best.Distance && secondary < bestSecondaryDistance)
                { best = candidate; bestSecondaryDistance = secondary; }
            }
            for (int i = start; i < end; i++)
            {
                double y = Y(i); var pixel = projection.ToScreen(snapshot[0, i], y, snapshot.ChannelCount > 2 ? snapshot[2, i] : 0);
                if (!pixel.IsFinite) continue;
                double distance = nearestX && cartesian ? Math.Abs(projection.XAxis.IsVertical ? pixel.Y - point.Y : pixel.X - point.X) : Math.Sqrt((pixel - point).LengthSquared);
                Consider(new(series, i, snapshot[0, i], snapshot[series.YChannel, i], pixel, distance, Details(series, snapshot, i)));
                bool continuous = series.Kind is SeriesKind.Line or SeriesKind.DigitalLine or SeriesKind.Spline or SeriesKind.Mountain or SeriesKind.SplineMountain or SeriesKind.Band or SeriesKind.DigitalBand or SeriesKind.StackedMountain or SeriesKind.PolarLine or SeriesKind.Radar or SeriesKind.TernaryLine;
                if (!continuous || !interpolate || i + 1 >= snapshot.Count) continue;
                double nextY = Y(i + 1); var next = projection.ToScreen(snapshot[0, i + 1], nextY, snapshot.ChannelCount > 2 ? snapshot[2, i + 1] : 0);
                if (!next.IsFinite) continue;
                bool digital = series.Kind is SeriesKind.DigitalLine or SeriesKind.DigitalBand;
                if (nearestX && cartesian && snapshot[0, i + 1] > snapshot[0, i] && coordinates.X > snapshot[0, i] && coordinates.X < snapshot[0, i + 1])
                {
                    // Use screen-space X fractions so the result lies on the line actually drawn,
                    // including logarithmic/rotated/flipped axes and cumulative stacked positions.
                    var probe = projection.ToScreen(coordinates.X, y);
                    double fraction = projection.XAxis.IsVertical ? (probe.Y - pixel.Y) / (next.Y - pixel.Y) : (probe.X - pixel.X) / (next.X - pixel.X);
                    var screen = digital ? probe : pixel + (next - pixel) * fraction;
                    if (!screen.IsFinite) continue;
                    var value = projection.ToData(screen);
                    double rawY = stacked is null ? value.Y : digital ? snapshot[series.YChannel, i] : snapshot[series.YChannel, i] + fraction * (snapshot[series.YChannel, i + 1] - snapshot[series.YChannel, i]);
                    best = new(series, i, coordinates.X, rawY, screen, 0, "interpolated"); break;
                }
                if (nearestX) continue;
                void Segment(PointD a, PointD b)
                {
                    double segmentDistance = Geometry.DistanceToSegment(point, a, b, out double fraction);
                    var screen = a + (b - a) * fraction;
                    if (!screen.IsFinite) return;
                    var value = projection.ToData(screen);
                    if (!value.IsFinite) return;
                    double rawY = stacked is null ? value.Y : snapshot[series.YChannel, i];
                    Consider(new(series, i, value.X, rawY, screen, segmentDistance, "interpolated"));
                }
                if (digital)
                {
                    var corner = projection.ToScreen(snapshot[0, i + 1], y);
                    if (corner.IsFinite) { Segment(pixel, corner); Segment(corner, next); }
                }
                else Segment(pixel, next);
            }
            if (best is not null && (nearestX || best.Distance <= radius)) output[series] = best;
        }
        return output.Values.OrderBy(hit => hit.Distance).ToArray();
    }
    private static int CellAt(ReadOnlySpan<double> edges, double value)
    {
        if (!double.IsFinite(value) || value < edges[0] || value > edges[^1]) return -1;
        int lo = 0, hi = edges.Length - 1;
        while (lo + 1 < hi) { int mid = lo + (hi - lo) / 2; if (edges[mid] <= value) lo = mid; else hi = mid; }
        return Math.Min(lo, edges.Length - 2);
    }
    private static double DistanceToRectangle(PointD p, RectD r) => double.Hypot(Math.Max(r.X - p.X, Math.Max(0, p.X - r.Right)), Math.Max(r.Y - p.Y, Math.Max(0, p.Y - r.Bottom)));
    private static string? Details(RenderableSeries s, SeriesSnapshot d, int i) => s.Kind switch
    {
        SeriesKind.Candlestick or SeriesKind.Ohlc => $"O {d[1, i]:G5} H {d[2, i]:G5} L {d[3, i]:G5} C {d[4, i]:G5}",
        SeriesKind.Bubble => $"Z {d[2, i]:G5}",
        SeriesKind.Band or SeriesKind.DigitalBand => $"Y₂ {d[2, i]:G5}",
        SeriesKind.VectorField => $"ΔX {d[2, i]:G5} ΔY {d[3, i]:G5}",
        SeriesKind.BoxPlot or SeriesKind.GroupedBoxPlot => $"min {d[1, i]:G5} Q1 {d[2, i]:G5} Q3 {d[4, i]:G5} max {d[5, i]:G5}",
        _ => null
    };
    private static SeriesHit? PieHit(RenderableSeries s, SeriesSnapshot data, RectD plot, PointD point)
    {
        double radius = Math.Min(plot.Width, plot.Height) * .43, inner = s.Kind == SeriesKind.Donut ? radius * Math.Clamp(s.DonutHoleRatio, 0, .95) : 0;
        double distance = Math.Sqrt((point - plot.Center).LengthSquared); if (distance < inner || distance > radius) return null;
        double total = 0; for (int i = 0; i < data.Count; i++) if (double.IsFinite(data[1, i]) && data[1, i] > 0) total += data[1, i];
        if (total <= 0 || !double.IsFinite(total)) return null;
        double angle = Math.Atan2(point.Y - plot.Center.Y, point.X - plot.Center.X) + Math.PI / 2; if (angle < 0) angle += 2 * Math.PI;
        double accumulated = 0;
        for (int i = 0; i < data.Count; i++)
        {
            if (!double.IsFinite(data[1, i]) || data[1, i] <= 0) continue;
            accumulated += data[1, i] / total * 2 * Math.PI;
            if (angle <= accumulated) return new(s, i, data[0, i], data[1, i], point, 0, $"{data[1, i] / total:P1}");
        }
        return null;
    }
}
