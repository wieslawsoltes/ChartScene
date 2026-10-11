using ChartScene.Core;
using ChartScene.Charting;
using ChartScene.Data;

namespace ChartScene.ThreeD;

public sealed record SceneNode3D(RenderableSeries3D? Series, MeshBuffer3D Geometry, bool Lighting, bool ClipToPlot = true);
public sealed record Scene3D(long GeometryRevision, IReadOnlyList<SceneNode3D> Nodes, IReadOnlyList<string> Diagnostics, long SourcePoints)
{
    public long VertexCount => Nodes.Sum(n => (long)n.Geometry.Vertices.Length);
    public long TriangleCount => Nodes.Where(n => n.Geometry.Topology != MeshTopology3D.Points).Sum(n => (long)n.Geometry.Indices.Length / 3);
}

/// <summary>View-owned geometry cache. Moving the camera does not rebuild geometry.</summary>
public sealed class SceneBuilder3D
{
    private ChartModel3D? _model;
    private Scene3D? _scene;
    public Scene3D Build(ChartModel3D model)
    {
        model.AutoRangeAxes();
        if (ReferenceEquals(_model, model) && _scene?.GeometryRevision == model.GeometryRevision) return _scene;
        var errors = model.Validate().ToList();
        var nodes = new List<SceneNode3D>();
        long count = 0;
        if (errors.Count == 0)
        {
            if (model.ShowGrid) Grid(model, nodes);
            foreach (var series in model.RenderableSeries.Where(s => s.IsVisible))
            {
                if (series.SurfaceData is { } surface)
                {
                    count += surface.Heights.Length;
                    if (series.Kind == SeriesKind3D.Waterfall) Waterfall(model, series, surface, nodes);
                    else Surface(model, series, surface, nodes);
                }
                else if (series.MeshData is { } mesh) { count += mesh.Positions.Length; Mesh(model, series, mesh, nodes); }
                else if (series.DataSeries is { } data) { var snapshot = data.Snapshot(); count += snapshot.Count; Data(model, series, snapshot, nodes); }
            }
        }
        _model = model;
        return _scene = new(model.GeometryRevision, nodes.AsReadOnly(), errors.AsReadOnly(), count);
    }
    public void Clear() { _model = null; _scene = null; }

    private sealed class Builder(MeshTopology3D topology)
    {
        public readonly List<Vertex3D> Vertices = [];
        public readonly List<uint> Indices = [];
        public readonly List<int> DataIndices = [];
        public uint Vertex(Vector3D p, ColorRgba c, Vector3D n = default, float size = 1)
        { uint id = (uint)Vertices.Count; Vertices.Add(new(p, n, c, size)); return id; }
        public void Triangle(uint a, uint b, uint c, int dataIndex) { Indices.AddRange([a, b, c]); DataIndices.Add(dataIndex); }
        public void Line(Vector3D a, Vector3D b, ColorRgba color, double thickness, int dataIndex)
        {
            if (!a.IsFinite || !b.IsFinite || (a - b).LengthSquared < 1e-24) return;
            uint first = (uint)Vertices.Count; float width = (float)Math.Min(4096, thickness);
            Vertex(a, color, b, -width); Vertex(a, color, b, width);
            Vertex(b, color, a, -width); Vertex(b, color, a, width);
            // At B the tangent is reversed, so matching sides have opposite signs.
            Triangle(first, first + 1, first + 2, dataIndex);
            Triangle(first, first + 2, first + 3, dataIndex);
        }
        public void Point(Vector3D p, ColorRgba color, double size, int dataIndex)
        { if (!p.IsFinite) return; Vertex(p, color, Vector3D.UnitY, (float)Math.Min(4096, size)); DataIndices.Add(dataIndex); }
        public void Quad(Vector3D a, Vector3D b, Vector3D c, Vector3D d, ColorRgba color, int dataIndex)
        {
            if (!a.IsFinite || !b.IsFinite || !c.IsFinite || !d.IsFinite) return;
            var normal = Vector3D.Cross(b - a, c - a).Normalized;
            if (normal.LengthSquared < .5) return;
            uint first = (uint)Vertices.Count;
            Vertex(a, color, normal); Vertex(b, color, normal); Vertex(c, color, normal); Vertex(d, color, normal);
            Triangle(first, first + 1, first + 2, dataIndex); Triangle(first, first + 2, first + 3, dataIndex);
        }
        public void SmoothNormals()
        {
            var normals = new Vector3D[Vertices.Count];
            for (int i = 0; i < Indices.Count; i += 3)
            {
                int a = (int)Indices[i], b = (int)Indices[i + 1], c = (int)Indices[i + 2];
                var normal = Vector3D.Cross(Vertices[b].Position - Vertices[a].Position, Vertices[c].Position - Vertices[a].Position);
                normals[a] += normal; normals[b] += normal; normals[c] += normal;
            }
            for (int i = 0; i < Vertices.Count; i++) Vertices[i] = Vertices[i] with { Normal = normals[i].Normalized };
        }
        public void Finish(List<SceneNode3D> nodes, RenderableSeries3D? series, bool lighting, bool clip = true)
        {
            if (Vertices.Count == 0 || topology != MeshTopology3D.Points && Indices.Count == 0) return;
            nodes.Add(new(series, new(Vertices.ToArray(), Indices.ToArray(), topology, DataIndices.ToArray()), lighting, clip));
        }
    }
    private static ColorRgba Color(RenderableSeries3D s, int index, Vector3D p, DoubleRange range, bool mapped)
    {
        var c = s.SelectedIndices.Contains(index) ? new ColorRgba(255, 235, 100) : s.PaletteProvider?.GetColor(s, index, p) ?? (mapped ? s.ColorMap.Sample(range.Fraction(p.Y)) : s.Stroke);
        return c.WithAlpha((byte)Math.Clamp(Math.Round(c.A * s.Opacity), 0, 255));
    }
    private static DoubleRange ColorRange(RenderableSeries3D s, IEnumerable<double> values)
    {
        if (s.ColorMapRange is { } r) return r;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (double v in values) if (double.IsFinite(v)) { min = Math.Min(min, v); max = Math.Max(max, v); }
        return double.IsFinite(min) ? DoubleRange.FromExtents(min, max) : DoubleRange.Unit;
    }
    private static void Surface(ChartModel3D m, RenderableSeries3D s, SurfaceData3D data, List<SceneNode3D> nodes)
    {
        var solid = new Builder(MeshTopology3D.Triangles); var wire = new Builder(MeshTopology3D.ThickLines);
        var range = ColorRange(s, data.Heights.ToArray());
        int[] map = Enumerable.Repeat(-1, data.Heights.Length).ToArray();
        for (int z = 0; z < data.Depth; z++) for (int x = 0; x < data.Width; x++)
        {
            int i = z * data.Width + x;
            var value = new Vector3D(data.XCoordinates.Span[x], data[x, z], data.ZCoordinates.Span[z]);
            var p = m.Normalize(value); if (!p.IsFinite) continue;
            map[i] = (int)solid.Vertex(p, Color(s, i, value, range, true));
        }
        for (int z = 0; z < data.Depth - 1; z++) for (int x = 0; x < data.Width - 1; x++)
        {
            int i = z * data.Width + x, a = map[i], b = map[i + 1], c = map[i + data.Width], d = map[i + data.Width + 1];
            if (a < 0 || b < 0 || c < 0 || d < 0) continue;
            solid.Triangle((uint)a, (uint)c, (uint)b, i); solid.Triangle((uint)b, (uint)c, (uint)d, i);
        }
        if (s.SurfaceDrawMode != SurfaceDrawMode.Wireframe) { solid.SmoothNormals(); solid.Finish(nodes, s, s.EnableLighting); }
        if (s.SurfaceDrawMode == SurfaceDrawMode.Solid) return;
        for (int z = 0; z < data.Depth; z++) for (int x = 0; x < data.Width; x++)
        {
            int i = z * data.Width + x, a = map[i]; if (a < 0) continue;
            void Segment(int j) { int b = map[j]; if (b >= 0) wire.Line(solid.Vertices[a].Position, solid.Vertices[b].Position, s.Stroke, s.StrokeThickness, i); }
            if (x + 1 < data.Width) Segment(i + 1);
            if (z + 1 < data.Depth) Segment(i + data.Width);
        }
        wire.Finish(nodes, s, false);
    }
    private static void Waterfall(ChartModel3D m, RenderableSeries3D s, SurfaceData3D data, List<SceneNode3D> nodes)
    {
        var solid = new Builder(MeshTopology3D.Triangles); var lines = new Builder(MeshTopology3D.ThickLines);
        double baseline = m.YAxis.Scale == AxisScale.Logarithmic && s.ZeroLineY <= 0 ? m.YAxis.VisibleRange.Min : s.ZeroLineY;
        for (int z = 0; z < data.Depth; z++)
        {
            var color = s.ColorMap.Sample(z / (double)Math.Max(1, data.Depth - 1)); color = color.WithAlpha((byte)(color.A * s.Opacity));
            for (int x = 0; x < data.Width - 1; x++)
            {
                int i = z * data.Width + x;
                var a = m.Normalize(new(data.XCoordinates.Span[x], data[x, z], data.ZCoordinates.Span[z]));
                var b = m.Normalize(new(data.XCoordinates.Span[x + 1], data[x + 1, z], data.ZCoordinates.Span[z]));
                var c = m.Normalize(new(data.XCoordinates.Span[x + 1], baseline, data.ZCoordinates.Span[z]));
                var d = m.Normalize(new(data.XCoordinates.Span[x], baseline, data.ZCoordinates.Span[z]));
                if (s.SurfaceDrawMode != SurfaceDrawMode.Wireframe) solid.Quad(a, b, c, d, color, i);
                lines.Line(a, b, color, s.StrokeThickness, i);
            }
        }
        solid.Finish(nodes, s, s.EnableLighting); lines.Finish(nodes, s, false);
    }
    private static void Mesh(ChartModel3D m, RenderableSeries3D s, MeshData3D data, List<SceneNode3D> nodes)
    {
        var solid = new Builder(MeshTopology3D.Triangles); var lines = new Builder(MeshTopology3D.ThickLines);
        var range = ColorRange(s, data.Positions.ToArray().Select(p => p.Y));
        int[] map = Enumerable.Repeat(-1, data.Positions.Length).ToArray();
        for (int i = 0; i < data.Positions.Length; i++)
        {
            var value = data.Positions.Span[i]; var p = m.Normalize(value); if (!p.IsFinite) continue;
            var color = data.Colors.IsEmpty ? Color(s, i, value, range, true) : data.Colors.Span[i].WithAlpha((byte)(data.Colors.Span[i].A * s.Opacity));
            map[i] = (int)solid.Vertex(p, color);
        }
        var edges = new HashSet<(int, int)>();
        for (int i = 0; i < data.Indices.Length; i += 3)
        {
            int a = map[(int)data.Indices.Span[i]], b = map[(int)data.Indices.Span[i + 1]], c = map[(int)data.Indices.Span[i + 2]];
            if (a < 0 || b < 0 || c < 0) continue;
            solid.Triangle((uint)a, (uint)b, (uint)c, i / 3);
            if (s.SurfaceDrawMode == SurfaceDrawMode.Solid) continue;
            foreach (var edge in new[] { (a, b), (b, c), (c, a) })
            {
                var sorted = (Math.Min(edge.Item1, edge.Item2), Math.Max(edge.Item1, edge.Item2));
                if (edges.Add(sorted)) lines.Line(solid.Vertices[sorted.Item1].Position, solid.Vertices[sorted.Item2].Position, s.Stroke, s.StrokeThickness, i / 3);
            }
        }
        if (s.SurfaceDrawMode != SurfaceDrawMode.Wireframe) { solid.SmoothNormals(); solid.Finish(nodes, s, s.EnableLighting); }
        lines.Finish(nodes, s, false);
    }
    private static void Data(ChartModel3D m, RenderableSeries3D s, SeriesSnapshot data, List<SceneNode3D> nodes)
    {
        var triangles = new Builder(MeshTopology3D.Triangles); var lines = new Builder(MeshTopology3D.ThickLines); var points = new Builder(MeshTopology3D.Points);
        var range = ColorRange(s, data.Channel(1).ToArray()); Vector3D? previous = null;
        double baseline = m.YAxis.Scale == AxisScale.Logarithmic && s.ZeroLineY <= 0 ? m.YAxis.VisibleRange.Min : s.ZeroLineY;
        for (int i = 0; i < data.Count; i++)
        {
            var value = new Vector3D(data[0, i], data[1, i], data[2, i]); var p = m.Normalize(value);
            if (!p.IsFinite) { previous = null; continue; }
            var color = Color(s, i, value, range, s.Kind is SeriesKind3D.Bubble or SeriesKind3D.Column);
            switch (s.Kind)
            {
                case SeriesKind3D.Scatter: points.Point(p, color, s.PointSize, i); break;
                case SeriesKind3D.Line: case SeriesKind3D.PointLine:
                    if (previous is { } last) lines.Line(last, p, color, s.StrokeThickness, i);
                    if (s.Kind == SeriesKind3D.PointLine) points.Point(p, color, s.PointSize, i);
                    previous = p; break;
                case SeriesKind3D.Bubble:
                    if (double.IsFinite(data[3, i]) && data[3, i] > 0) Sphere(triangles, p, Math.Sqrt(data[3, i]) * s.BubbleScale, color, i);
                    break;
                case SeriesKind3D.Column:
                    var a = m.Normalize(new(value.X - s.ColumnWidthX / 2, baseline, value.Z - s.ColumnWidthZ / 2));
                    var b = m.Normalize(new(value.X + s.ColumnWidthX / 2, value.Y, value.Z + s.ColumnWidthZ / 2));
                    if (a.IsFinite && b.IsFinite) Box(triangles, Vector3D.Min(a, b), Vector3D.Max(a, b), color, i);
                    break;
                case SeriesKind3D.Impulse:
                    lines.Line(m.Normalize(new(value.X, baseline, value.Z)), p, color, s.StrokeThickness, i); points.Point(p, color, s.PointSize, i); break;
                case SeriesKind3D.ErrorBars:
                    points.Point(p, color, s.PointSize, i);
                    for (int axis = 0; axis < 3; axis++)
                    {
                        double low = data[3 + axis * 2, i], high = data[4 + axis * 2, i];
                        if (!double.IsFinite(low) || !double.IsFinite(high) || low > high) continue;
                        Vector3D With(double v) => axis switch { 0 => value with { X = v }, 1 => value with { Y = v }, _ => value with { Z = v } };
                        var start = m.Normalize(With(low)); var end = m.Normalize(With(high));
                        if (!start.IsFinite || !end.IsFinite) continue;
                        lines.Line(start, end, color, s.StrokeThickness, i);
                        var cap = (axis == 0 ? Vector3D.UnitY : Vector3D.UnitX) * .018;
                        lines.Line(start - cap, start + cap, color, s.StrokeThickness, i); lines.Line(end - cap, end + cap, color, s.StrokeThickness, i);
                    }
                    break;
            }
        }
        triangles.Finish(nodes, s, s.EnableLighting); lines.Finish(nodes, s, false); points.Finish(nodes, s, false);
    }
    private static void Box(Builder b, Vector3D min, Vector3D max, ColorRgba color, int index)
    {
        Vector3D a = new(min.X, min.Y, min.Z), c = new(max.X, min.Y, min.Z), d = new(max.X, max.Y, min.Z), e = new(min.X, max.Y, min.Z);
        Vector3D f = new(min.X, min.Y, max.Z), g = new(max.X, min.Y, max.Z), h = new(max.X, max.Y, max.Z), j = new(min.X, max.Y, max.Z);
        b.Quad(a, e, d, c, color, index); b.Quad(f, g, h, j, color, index);
        b.Quad(a, f, j, e, color, index); b.Quad(c, d, h, g, color, index);
        b.Quad(e, j, h, d, color, index); b.Quad(a, c, g, f, color, index);
    }
    private static void Sphere(Builder b, Vector3D center, double radius, ColorRgba color, int index)
    {
        if (!double.IsFinite(radius) || radius <= 0) return;
        const int longitude = 12, latitude = 8; uint start = (uint)b.Vertices.Count;
        for (int row = 0; row <= latitude; row++) for (int col = 0; col <= longitude; col++)
        {
            double phi = Math.PI * row / latitude, theta = 2 * Math.PI * col / longitude;
            var normal = new Vector3D(Math.Sin(phi) * Math.Cos(theta), Math.Cos(phi), Math.Sin(phi) * Math.Sin(theta));
            b.Vertex(center + normal * radius, color, normal);
        }
        for (int row = 0; row < latitude; row++) for (int col = 0; col < longitude; col++)
        {
            uint a = start + (uint)(row * (longitude + 1) + col), c = a + longitude + 1;
            if (row > 0) b.Triangle(a, a + 1, c, index);
            if (row < latitude - 1) b.Triangle(a + 1, c + 1, c, index);
        }
    }
    private static void Grid(ChartModel3D m, List<SceneNode3D> nodes)
    {
        var b = new Builder(MeshTopology3D.ThickLines); var h = m.WorldSize * .5;
        for (int i = 0; i <= 10; i++)
        {
            double t = i / 10.0, x = -h.X + 2 * h.X * t, y = -h.Y + 2 * h.Y * t, z = -h.Z + 2 * h.Z * t;
            b.Line(new(x, -h.Y, -h.Z), new(x, -h.Y, h.Z), m.Theme.GridMajor, 1, -1);
            b.Line(new(-h.X, -h.Y, z), new(h.X, -h.Y, z), m.Theme.GridMajor, 1, -1);
            b.Line(new(-h.X, y, -h.Z), new(h.X, y, -h.Z), m.Theme.GridMajor, 1, -1);
            b.Line(new(-h.X, y, -h.Z), new(-h.X, y, h.Z), m.Theme.GridMajor, 1, -1);
            b.Line(new(x, -h.Y, -h.Z), new(x, h.Y, -h.Z), m.Theme.GridMajor, 1, -1);
            b.Line(new(-h.X, -h.Y, z), new(-h.X, h.Y, z), m.Theme.GridMajor, 1, -1);
        }
        foreach (int a in new[] { -1, 1 }) foreach (int c in new[] { -1, 1 })
        {
            b.Line(new(-h.X, a * h.Y, c * h.Z), new(h.X, a * h.Y, c * h.Z), m.Theme.Border, 1.4, -1);
            b.Line(new(a * h.X, -h.Y, c * h.Z), new(a * h.X, h.Y, c * h.Z), m.Theme.Border, 1.4, -1);
            b.Line(new(a * h.X, c * h.Y, -h.Z), new(a * h.X, c * h.Y, h.Z), m.Theme.Border, 1.4, -1);
        }
        b.Finish(nodes, null, false, false);
    }
}
