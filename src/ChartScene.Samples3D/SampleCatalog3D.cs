using ChartScene.Core;
using ChartScene.Data;
using ChartScene.ThreeD;
using ChartScene.Charting;
namespace ChartScene.Samples3D;

public sealed record ChartExample3D(string Id, string Title, Func<ChartModel3D> Create);
public static class SampleCatalog3D
{
    public static IReadOnlyList<ChartExample3D> Examples { get; } = Enum.GetValues<SeriesKind3D>()
        .Select(kind => new ChartExample3D(kind.ToString().ToLowerInvariant(), kind.ToString(), () => Create(kind))).ToArray();
    public static ChartModel3D Create(SeriesKind3D kind)
    {
        var m = new ChartModel3D { Title = kind + " · 3D", Subtitle = "Drag to orbit · Control-drag to pan · Wheel to zoom · Click to select" };
        m.XAxis.AxisTitle = "X coordinate"; m.YAxis.AxisTitle = "Amplitude"; m.ZAxis.AxisTitle = "Z coordinate";
        var s = new RenderableSeries3D(kind) { SeriesName = kind.ToString(), PointSize = 5, StrokeThickness = 2 };
        m.RenderableSeries.Add(s); var random = new Random(4571);
        if (kind is SeriesKind3D.SurfaceMesh or SeriesKind3D.Waterfall)
        {
            int width = kind == SeriesKind3D.Waterfall ? 120 : 81, depth = kind == SeriesKind3D.Waterfall ? 18 : 61;
            s.SurfaceData = SurfaceData3D.Uniform(width, depth, -4, 8.0 / (width - 1), -3, 6.0 / (depth - 1), (x, z) =>
            {
                double a = -4 + 8.0 * x / (width - 1), b = -3 + 6.0 * z / (depth - 1), r = Math.Sqrt(a * a + b * b);
                return kind == SeriesKind3D.Waterfall ? 1.5 + Math.Sin(a * 2 + b) * Math.Exp(-a * a / 10) : Math.Cos(r * 2) * Math.Exp(-r * r / 12);
            });
            if (kind == SeriesKind3D.SurfaceMesh) { s.SurfaceDrawMode = SurfaceDrawMode.SolidWithWireframe; s.Stroke = new(22, 42, 56); s.StrokeThickness = .6; }
            return m;
        }
        if (kind == SeriesKind3D.Mesh)
        {
            const int major = 64, minor = 28; var positions = new Vector3D[major * minor]; var indices = new List<uint>();
            for (int i = 0; i < major; i++) for (int j = 0; j < minor; j++)
            {
                double a = i * 2 * Math.PI / major, b = j * 2 * Math.PI / minor;
                positions[i * minor + j] = new((2 + .7 * Math.Cos(b)) * Math.Cos(a), .7 * Math.Sin(b), (2 + .7 * Math.Cos(b)) * Math.Sin(a));
                uint p = (uint)(i * minor + j), q = (uint)(((i + 1) % major) * minor + j), r = (uint)(((i + 1) % major) * minor + (j + 1) % minor), t = (uint)(i * minor + (j + 1) % minor);
                indices.AddRange([p, q, r, p, r, t]);
            }
            s.MeshData = new(positions, indices.ToArray()); return m;
        }
        var data = new DataSeries(s.RequiredChannels) { AcceptsUnsortedData = true }; s.DataSeries = data;
        if (kind == SeriesKind3D.Column)
        {
            for (int z = 0; z < 9; z++) for (int x = 0; x < 12; x++) data.Append(x, 2 + 2 * Math.Sin(x * .45) * Math.Cos(z * .4), z);
        }
        else if (kind is SeriesKind3D.Line or SeriesKind3D.PointLine)
        {
            for (int i = 0; i < 700; i++) { double t = i * .025; data.Append(Math.Cos(t), t * .15, Math.Sin(t)); }
        }
        else
        {
            int count = kind == SeriesKind3D.Scatter ? 12000 : kind == SeriesKind3D.Bubble ? 70 : 90;
            for (int i = 0; i < count; i++)
            {
                double x = (random.NextDouble() - .5) * 6, z = (random.NextDouble() - .5) * 6, y = Math.Sin(x) * Math.Cos(z) + random.NextDouble() * .6;
                if (kind == SeriesKind3D.Bubble) data.Append(x, y, z, 1 + random.NextDouble() * 9);
                else if (kind == SeriesKind3D.ErrorBars) data.Append(x, y, z, x - .2, x + .2, y - .15, y + .15, z - .2, z + .2);
                else data.Append(x, y, z);
            }
        }
        return m;
    }
}
