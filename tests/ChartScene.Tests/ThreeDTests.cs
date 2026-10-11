using ChartScene.Core;
using ChartScene.Charting;
using ChartScene.Data;
using ChartScene.ThreeD;
using ChartScene.Samples3D;

public static partial class AdditionalTests
{
    static partial void RegisterThreeD(TestSuite suite)
    {
        suite.Run("3D vector normalization and cross product", () =>
        {
            var v = new Vector3D(3, 4, 0); TestSuite.Near(v.Length, 5); TestSuite.Near(v.Normalized.Length, 1);
            TestSuite.True(Vector3D.Cross(Vector3D.UnitX, Vector3D.UnitY) == Vector3D.UnitZ);
            TestSuite.True(Vector3D.Zero.Normalized == Vector3D.Zero);
        });
        suite.Run("3D slabs parallel and inside rays", () =>
        {
            var bounds = new Bounds3D(new(-1, -1, -1), new(1, 1, 1));
            TestSuite.True(bounds.Intersect(new(new(0, 0, 3), new(0, 0, -1)), 10, out double d)); TestSuite.Near(d, 2);
            TestSuite.True(!bounds.Intersect(new(new(2, 0, 3), new(0, 0, -1)), 10, out _));
            TestSuite.True(bounds.Intersect(new(Vector3D.Zero, Vector3D.UnitX), 10, out d)); TestSuite.Near(d, 0);
        });
        suite.Run("3D ray triangle barycentrics", () =>
        {
            var ray = new Ray3D(new(.25, .25, 1), new(0, 0, -1));
            TestSuite.True(ray.IntersectTriangle(Vector3D.Zero, Vector3D.UnitX, Vector3D.UnitY, out var distance, out var b));
            TestSuite.Near(distance, 1); TestSuite.Near(b.X, .5); TestSuite.Near(b.Y, .25); TestSuite.Near(b.Z, .25);
        });
        foreach (var projection in Enum.GetValues<CameraProjection>()) suite.Run($"3D camera project unproject {projection}", () =>
        {
            var camera = new Camera3D { Projection = projection }; var viewport = new RectD(10, 20, 800, 600);
            var point = new Vector3D(.3, .2, -.1); var p = camera.Project(point, viewport); TestSuite.True(p.IsVisible);
            var ray = camera.GetRay(p.Pixel, viewport); var t = Vector3D.Dot(point - ray.Origin, ray.Direction);
            TestSuite.True((ray.At(t) - point).Length < 2e-4);
            TestSuite.True((camera.Project(Vector3D.Zero, viewport).Pixel - viewport.Center).LengthSquared < .001);
        });
        suite.Run("3D camera pan aspect-aware orthographic", () =>
        {
            var c = new Camera3D { Projection = CameraProjection.Orthographic, OrthographicWidth = 4 };
            c.Pan(100, 0, 1000, 500); TestSuite.Near(c.Target.Length, .4);
        });
        suite.Run("3D normalized doubles preserve distant coordinates", () =>
        {
            using var m = new ChartModel3D(); m.XAxis.VisibleRange = new(1e12, 1e12 + 10); m.YAxis.VisibleRange = new(-10, 10); m.ZAxis.VisibleRange = new(-1, 1);
            var v = new Vector3D(1e12 + 2, 3, .25); var normalized = m.Normalize(v); var roundtrip = m.Denormalize(normalized);
            TestSuite.Near(roundtrip.X - 1e12, 2); TestSuite.Near(roundtrip.Y, 3); TestSuite.Near(roundtrip.Z, .25);
        });
        suite.Run("3D logarithmic normalization", () =>
        {
            using var m = new ChartModel3D(); m.YAxis.Scale = AxisScale.Logarithmic; m.YAxis.VisibleRange = new(1, 1000);
            var p = m.Normalize(new(.5, Math.Sqrt(1000), .5)); TestSuite.Near(p.Y, 0);
            TestSuite.Near(m.Denormalize(p).Y, Math.Sqrt(1000)); TestSuite.True(!m.Normalize(new(.5, -1, .5)).IsFinite);
        });
        suite.Run("3D surface rejects dimensions and unordered coordinates", () =>
        {
            TestSuite.Throws<ArgumentException>(() => new SurfaceData3D([0, 0], [0, 1], [1, 2, 3, 4]));
            TestSuite.Throws<ArgumentException>(() => new SurfaceData3D([0, 1], [0, 1], [1]));
            double[] heights = [1, 2, 3, 4]; var s = new SurfaceData3D([0, 1], [0, 1], heights); heights[0] = 99; TestSuite.Near(s[0, 0], 1);
        });
        suite.Run("3D custom mesh validates indices", () =>
        {
            TestSuite.Throws<ArgumentException>(() => new MeshData3D([Vector3D.Zero, Vector3D.UnitX, Vector3D.UnitY], [0, 1, 3]));
            TestSuite.Throws<ArgumentException>(() => new MeshData3D([Vector3D.Zero, Vector3D.UnitX, Vector3D.UnitY], [0, 1]));
        });
        foreach (var e in SampleCatalog3D.Examples) suite.Run("3D example geometry " + e.Id, () =>
        {
            using var model = e.Create(); var builder = new SceneBuilder3D(); var scene = builder.Build(model);
            TestSuite.Equal(scene.Diagnostics.Count, 0); TestSuite.True(scene.SourcePoints > 0 && scene.Nodes.Any(n => n.Series is not null));
            foreach (var node in scene.Nodes)
            {
                var mesh = node.Geometry; TestSuite.True(mesh.Vertices.Span.ToArray().All(v => v.Position.IsFinite && v.Normal.IsFinite));
                TestSuite.Equal(mesh.InterleavedVertices().Length, mesh.Vertices.Length * 11);
                TestSuite.Equal(mesh.PrimitiveDataIndices.Length, mesh.PrimitiveCount);
                TestSuite.True(mesh.Indices.ToArray().All(i => i < mesh.Vertices.Length));
            }
            TestSuite.True(ReferenceEquals(scene, builder.Build(model)));
            model.Camera.Yaw += 20; TestSuite.True(ReferenceEquals(scene, builder.Build(model)));
            model.RenderableSeries[0].Stroke = ColorRgba.White; TestSuite.True(!ReferenceEquals(scene, builder.Build(model)));
        });
        suite.Run("3D surface topology and upward normals", () =>
        {
            using var m = new ChartModel3D { ShowGrid = false }; var s = new RenderableSeries3D(SeriesKind3D.SurfaceMesh) { SurfaceData = SurfaceData3D.Uniform(4, 3, 0, 1, 0, 1, (_, _) => 1) };
            m.RenderableSeries.Add(s); var mesh = new SceneBuilder3D().Build(m).Nodes.Single().Geometry;
            TestSuite.Equal(mesh.Vertices.Length, 12); TestSuite.Equal(mesh.PrimitiveCount, 12);
            TestSuite.True(mesh.Vertices.ToArray().All(v => v.Normal.Y > .999));
        });
        suite.Run("3D NaN surface holes do not bridge", () =>
        {
            using var m = new ChartModel3D { ShowGrid = false }; m.RenderableSeries.Add(new(SeriesKind3D.SurfaceMesh) { SurfaceData = SurfaceData3D.Uniform(3, 3, 0, 1, 0, 1, (x, z) => x == 1 && z == 1 ? double.NaN : 1) });
            var scene = new SceneBuilder3D().Build(m); TestSuite.Equal(scene.Nodes.Count, 0);
        });
        foreach (var mode in Enum.GetValues<SurfaceDrawMode>()) suite.Run("3D surface mode " + mode, () =>
        {
            using var m = SampleCatalog3D.Create(SeriesKind3D.SurfaceMesh); m.ShowGrid = false; m.RenderableSeries[0].SurfaceDrawMode = mode;
            var scene = new SceneBuilder3D().Build(m);
            TestSuite.Equal(scene.Nodes.Count, mode == SurfaceDrawMode.SolidWithWireframe ? 2 : 1);
        });
        suite.Run("3D NaN line splits immutable line strips", () =>
        {
            using var m = new ChartModel3D { ShowGrid = false }; var data = new DataSeries(3); data.AppendRange([0, 1, 2, 3, 4], [0, 1, double.NaN, 1, 0], [0, 1, 2, 3, 4]);
            m.RenderableSeries.Add(new(SeriesKind3D.Line) { DataSeries = data }); var mesh = new SceneBuilder3D().Build(m).Nodes.Single().Geometry;
            TestSuite.Equal(mesh.Vertices.Length, 8); TestSuite.Equal(mesh.PrimitiveCount, 4);
        });
        suite.Run("3D signed columns include baseline and footprint", () =>
        {
            using var m = new ChartModel3D { ShowGrid = false }; var data = new DataSeries(3); data.AppendRange([0, 1], [-2, 3], [0, 1]);
            m.RenderableSeries.Add(new(SeriesKind3D.Column) { DataSeries = data }); var scene = new SceneBuilder3D().Build(m);
            TestSuite.True(m.YAxis.VisibleRange.Contains(-2) && m.YAxis.VisibleRange.Contains(3) && m.XAxis.VisibleRange.Contains(-.35));
            TestSuite.Equal(scene.TriangleCount, 24L);
        });
        suite.Run("3D BVH nearest accepted surface and source index", () =>
        {
            Vertex3D V(double x, double y, double z) => new(new(x, y, z), Vector3D.UnitZ, ColorRgba.White);
            var mesh = new MeshBuffer3D([V(0, 0, 1), V(1, 0, 1), V(0, 1, 1), V(0, 0, 0), V(1, 0, 0), V(0, 1, 0)], [0, 1, 2, 3, 4, 5], MeshTopology3D.Triangles, [7, 9]);
            var bvh = new TriangleBvh(mesh); var ray = new Ray3D(new(.2, .2, 3), new(0, 0, -1));
            TestSuite.Equal(bvh.Intersect(ray)!.DataIndex, 7); TestSuite.Equal(bvh.Intersect(ray, accept: p => p.Z < .5)!.DataIndex, 9);
        });
        suite.Run("3D BVH agrees with brute force", () =>
        {
            using var m = SampleCatalog3D.Create(SeriesKind3D.Mesh); m.ShowGrid = false;
            var mesh = new SceneBuilder3D().Build(m).Nodes.Single().Geometry; var bvh = new TriangleBvh(mesh); var rng = new Random(651);
            for (int test = 0; test < 50; test++)
            {
                var ray = new Ray3D(new(rng.NextDouble() * 4 - 2, 2, rng.NextDouble() * 4 - 2), new(0, -1, 0));
                double nearest = double.PositiveInfinity;
                for (int i = 0; i < mesh.Indices.Length; i += 3)
                {
                    Vector3D P(int offset) => mesh.Vertices.Span[(int)mesh.Indices.Span[i + offset]].Position;
                    if (ray.IntersectTriangle(P(0), P(1), P(2), out double distance, out _)) nearest = Math.Min(nearest, distance);
                }
                var hit = bvh.Intersect(ray);
                if (double.IsFinite(nearest)) TestSuite.Near(hit!.Distance, nearest, 1e-8); else TestSuite.True(hit is null);
            }
        });
        suite.Run("3D selection changes colors and invalidates cache", () =>
        {
            using var m = SampleCatalog3D.Create(SeriesKind3D.Scatter); m.ShowGrid = false; var builder = new SceneBuilder3D(); var before = builder.Build(m);
            m.RenderableSeries[0].SelectedIndices.Add(0); m.RenderableSeries[0].Invalidate(); var after = builder.Build(m);
            TestSuite.True(!ReferenceEquals(before, after)); TestSuite.True(before.Nodes[0].Geometry.Vertices.Span[0].Color != after.Nodes[0].Geometry.Vertices.Span[0].Color);
        });
    }
}
