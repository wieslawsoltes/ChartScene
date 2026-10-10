using ChartScene.Core;
namespace ChartScene.ThreeD;

public sealed record TriangleHit3D(int TriangleIndex, int DataIndex, double Distance, Vector3D Position, Vector3D Barycentric);

/// <summary>Immutable median-split BVH; queries reject clipped intersections before nearest-hit selection.</summary>
public sealed class TriangleBvh
{
    private sealed record Node(Bounds3D Bounds, int Start, int Count, int Left = -1, int Right = -1);
    private readonly MeshBuffer3D _mesh;
    private readonly int[] _triangles;
    private readonly Bounds3D[] _bounds;
    private readonly List<Node> _nodes = [];
    public int NodeCount => _nodes.Count;
    public TriangleBvh(MeshBuffer3D mesh)
    {
        if (mesh.Topology != MeshTopology3D.Triangles || mesh.Indices.Length == 0) throw new ArgumentException("A non-empty triangle mesh is required.");
        _mesh = mesh; _triangles = Enumerable.Range(0, mesh.Indices.Length / 3).ToArray(); _bounds = new Bounds3D[_triangles.Length];
        var indices = mesh.Indices.Span; var vertices = mesh.Vertices.Span;
        for (int i = 0; i < _bounds.Length; i++)
        {
            var a = vertices[(int)indices[i * 3]].Position; var b = vertices[(int)indices[i * 3 + 1]].Position; var c = vertices[(int)indices[i * 3 + 2]].Position;
            _bounds[i] = new(Vector3D.Min(a, Vector3D.Min(b, c)), Vector3D.Max(a, Vector3D.Max(b, c)));
        }
        Build(0, _triangles.Length);
    }
    private int Build(int start, int count)
    {
        var bounds = _bounds[_triangles[start]];
        for (int i = start + 1; i < start + count; i++) bounds = bounds.Union(_bounds[_triangles[i]]);
        int node = _nodes.Count; _nodes.Add(new(bounds, start, count));
        if (count <= 8) return node;
        var size = bounds.Size; int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        Array.Sort(_triangles, start, count, Comparer<int>.Create((a, b) => _bounds[a].Center.Component(axis).CompareTo(_bounds[b].Center.Component(axis))));
        int leftCount = count / 2, left = Build(start, leftCount), right = Build(start + leftCount, count - leftCount);
        _nodes[node] = new(bounds, start, 0, left, right); return node;
    }
    public TriangleHit3D? Intersect(Ray3D ray, double maximumDistance = double.PositiveInfinity, Func<Vector3D, bool>? accept = null)
    {
        if (!ray.Origin.IsFinite || !ray.Direction.IsFinite || ray.Direction.LengthSquared < 1e-24) return null;
        TriangleHit3D? best = null; var stack = new Stack<int>(); stack.Push(0);
        while (stack.Count > 0)
        {
            var node = _nodes[stack.Pop()]; if (!node.Bounds.Intersect(ray, best?.Distance ?? maximumDistance, out _)) continue;
            if (node.Count == 0)
            {
                bool left = _nodes[node.Left].Bounds.Intersect(ray, best?.Distance ?? maximumDistance, out double a);
                bool right = _nodes[node.Right].Bounds.Intersect(ray, best?.Distance ?? maximumDistance, out double b);
                if (left && right) { stack.Push(a < b ? node.Right : node.Left); stack.Push(a < b ? node.Left : node.Right); }
                else if (left) stack.Push(node.Left); else if (right) stack.Push(node.Right);
                continue;
            }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                int triangle = _triangles[i], offset = triangle * 3; var indices = _mesh.Indices.Span; var vertices = _mesh.Vertices.Span;
                if (!ray.IntersectTriangle(vertices[(int)indices[offset]].Position, vertices[(int)indices[offset + 1]].Position, vertices[(int)indices[offset + 2]].Position, out double distance, out var barycentric)) continue;
                var point = ray.At(distance);
                if (distance < (best?.Distance ?? maximumDistance) && (accept is null || accept(point)))
                    best = new(triangle, _mesh.PrimitiveDataIndices.Span[triangle], distance, point, barycentric);
            }
        }
        return best;
    }
}
public sealed record SeriesHit3D(RenderableSeries3D Series, int DataIndex, Vector3D DataPosition, Vector3D WorldPosition, PointD Pixel, double Distance);
public sealed class SceneHitTester3D
{
    private Scene3D? _scene;
    private readonly Dictionary<MeshBuffer3D, TriangleBvh> _hierarchies = [];
    public void SetScene(Scene3D scene)
    {
        if (ReferenceEquals(_scene, scene)) return;
        _scene = scene; var retained = scene.Nodes.Select(n => n.Geometry).ToHashSet();
        foreach (var key in _hierarchies.Keys.Where(k => !retained.Contains(k)).ToArray()) _hierarchies.Remove(key);
    }
    public SeriesHit3D? HitTest(ChartModel3D model, PointD pixel, RectD viewport, double tolerance = 7)
    {
        if (_scene is null || !viewport.Contains(pixel)) return null;
        var ray = model.Camera.GetRay(pixel, viewport); SeriesHit3D? best = null; double nearest = double.PositiveInfinity;
        bool InPlot(Vector3D p) { var h = model.WorldSize * .5; return Math.Abs(p.X) <= h.X + 1e-8 && Math.Abs(p.Y) <= h.Y + 1e-8 && Math.Abs(p.Z) <= h.Z + 1e-8; }
        foreach (var node in _scene.Nodes)
        {
            if (node.Series is not { IsVisible: true } series) continue;
            var mesh = node.Geometry;
            if (mesh.Topology == MeshTopology3D.Triangles)
            {
                if (!_hierarchies.TryGetValue(mesh, out var bvh)) _hierarchies[mesh] = bvh = new(mesh);
                var hit = bvh.Intersect(ray, nearest, node.ClipToPlot ? InPlot : null);
                if (hit is null) continue;
                nearest = hit.Distance; best = new(series, hit.DataIndex, model.Denormalize(hit.Position), hit.Position, pixel, hit.Distance); continue;
            }
            if (mesh.Topology == MeshTopology3D.Points)
            {
                for (int i = 0; i < mesh.Vertices.Length; i++)
                {
                    var v = mesh.Vertices.Span[i]; if (node.ClipToPlot && !InPlot(v.Position)) continue;
                    var p = model.Camera.Project(v.Position, viewport); if (!p.IsVisible) continue;
                    double distance = Math.Sqrt((p.Pixel - pixel).LengthSquared), depth = Vector3D.Dot(v.Position - ray.Origin, ray.Direction);
                    if (distance <= tolerance + v.Size / 2 && depth >= 0 && depth < nearest)
                    { nearest = depth; best = new(series, mesh.PrimitiveDataIndices.Span[i], model.Denormalize(v.Position), v.Position, p.Pixel, depth); }
                }
            }
            else for (int i = 0; i + 3 < mesh.Vertices.Length; i += 4)
            {
                var a = mesh.Vertices.Span[i]; var b = mesh.Vertices.Span[i + 2];
                var pa = model.Camera.Project(a.Position, viewport); var pb = model.Camera.Project(b.Position, viewport);
                // The normalized chart is normally fully in front of the camera. Reject
                // near-plane crossings rather than projecting a behind-camera endpoint.
                if (!double.IsFinite(pa.Depth) || !double.IsFinite(pb.Depth) || pa.Depth < -1 || pb.Depth < -1) continue;
                double distance = Geometry.DistanceToSegment(pixel, pa.Pixel, pb.Pixel, out double t);
                var point = a.Position + (b.Position - a.Position) * t;
                if (node.ClipToPlot && !InPlot(point)) continue;
                double depth = Vector3D.Dot(point - ray.Origin, ray.Direction);
                if (distance <= tolerance + Math.Abs(a.Size) / 2 && depth >= 0 && depth < nearest)
                { nearest = depth; best = new(series, mesh.PrimitiveDataIndices.Span[i / 2], model.Denormalize(point), point, pa.Pixel + (pb.Pixel - pa.Pixel) * t, depth); }
            }
        }
        return best;
    }
}
