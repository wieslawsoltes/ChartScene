using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;
using ChartScene.Rendering;

public static class CursorSyncTests
{
    private sealed class View : IDisposable
    {
        public ChartModel Model { get; } = new() { ShowLegend = false };
        public ChartController Controller { get; }
        public XyDataSeries Data { get; } = new();
        public View(double min = 0, double max = 10, int width = 900, int height = 500)
        {
            Model.XAxis.VisibleRange = new(min, max); Model.XAxis.AutoRange = AutoRange.Never;
            Model.YAxis.VisibleRange = new(0, 10); Model.YAxis.AutoRange = AutoRange.Never;
            Data.Append(min, 0); Data.Append(max, 10);
            Model.RenderableSeries.Add(new FastLineRenderableSeries { DataSeries = Data });
            Controller = new(Model); Resize(width, height);
        }
        public RectD Plot => Controller.Frame!.Layout.PlotArea;
        public void Resize(int width, int height) => Controller.SetFrame(new ChartRenderer().Render(new RecordingCanvas(), Model, width, height));
        public PointD Screen(double x, double perpendicular = .6)
        {
            var axis = Model.XAxis;
            var calc = new CoordinateCalculator(axis, axis.IsVertical ? Plot.Y : Plot.X, axis.IsVertical ? Plot.Height : Plot.Width);
            return axis.IsVertical ? new(Plot.X + Plot.Width * perpendicular, calc.GetCoordinate(x)) : new(calc.GetCoordinate(x), Plot.Y + Plot.Height * perpendicular);
        }
        public void Move(double x, double perpendicular = .6) => Controller.Process(new(ChartInputKind.Move, Screen(x, perpendicular)));
        public double CursorX()
        {
            var point = Controller.View.Cursor!.Value; var axis = Model.XAxis;
            return new CoordinateCalculator(axis, axis.IsVertical ? Plot.Y : Plot.X, axis.IsVertical ? Plot.Height : Plot.Width)
                .GetDataValue(axis.IsVertical ? point.Y : point.X);
        }
        public void Dispose() { Controller.Dispose(); Model.Dispose(); }
    }

    public static void Register(TestSuite suite)
    {
        suite.Run("cursor sync unequal view sizes use data X", () =>
        {
            using var a = new View(); using var b = new View(width: 1400, height: 700);
            using var group = new ChartCursorSyncGroup("signals"); group.Add(a.Controller); group.Add(b.Controller);
            long revision = b.Model.Revision;
            a.Move(3, .7);
            TestSuite.Near(b.CursorX(), 3);
            TestSuite.Near((b.Controller.View.Cursor!.Value.Y - b.Plot.Y) / b.Plot.Height, .7);
            TestSuite.True(b.Controller.View.Hits.Count == 1);
            TestSuite.Equal(b.Model.Revision, revision);
        });
        suite.Run("cursor sync converts physical units both directions", () =>
        {
            using var meters = new View(); using var centimeters = new View(0, 1000);
            using var group = new ChartCursorSyncGroup("distance"); group.Add(meters.Controller);
            group.Add(centimeters.Controller, toCanonical: x => x / 100, fromCanonical: x => x * 100);
            meters.Move(2); TestSuite.Near(centimeters.CursorX(), 200);
            centimeters.Move(450); TestSuite.Near(meters.CursorX(), 4.5);
            TestSuite.True(ReferenceEquals(group.ActiveController, centimeters.Controller));
        });
        suite.Run("cursor sync logarithmic axes map raw values", () =>
        {
            using var a = new View(1, 1000); using var b = new View(1, 1000);
            b.Model.XAxis.Scale = AxisScale.Logarithmic; b.Resize(900, 500);
            using var group = new ChartCursorSyncGroup("log"); group.Add(a.Controller); group.Add(b.Controller);
            a.Move(100);
            TestSuite.Near(b.CursorX(), 100);
            TestSuite.Near((b.Controller.View.Cursor!.Value.X - b.Plot.X) / b.Plot.Width, 2.0 / 3);
        });
        suite.Run("cursor sync rotated and flipped X axis", () =>
        {
            using var a = new View(); using var b = new View();
            b.Model.XAxis.AxisAlignment = AxisAlignment.Left; b.Model.XAxis.FlippedCoordinates = true;
            b.Model.YAxis.AxisAlignment = AxisAlignment.Bottom; b.Resize(700, 900);
            using var group = new ChartCursorSyncGroup("orientation"); group.Add(a.Controller); group.Add(b.Controller);
            a.Move(7, .3); TestSuite.Near(b.CursorX(), 7);
            TestSuite.Near((b.Controller.View.Cursor!.Value.X - b.Plot.X) / b.Plot.Width, .3);
        });
        suite.Run("cursor sync outside destination range clears instead of clamping", () =>
        {
            using var a = new View(); using var b = new View(2, 6);
            using var group = new ChartCursorSyncGroup("clipping"); group.Add(a.Controller); group.Add(b.Controller);
            a.Move(4); TestSuite.True(b.Controller.View.Cursor is not null);
            a.Move(8); TestSuite.True(b.Controller.View.Cursor is null); TestSuite.Equal(b.Controller.View.Hits.Count, 0);
            a.Move(3); TestSuite.Near(b.CursorX(), 3);
        });
        suite.Run("cursor sync exit clears peers without changing ranges", () =>
        {
            using var a = new View(); using var b = new View(); var range = b.Model.XAxis.VisibleRange;
            using var group = new ChartCursorSyncGroup("exit"); group.Add(a.Controller); group.Add(b.Controller);
            a.Move(4); a.Controller.PointerExited();
            TestSuite.True(group.ActiveController is null && b.Controller.View.Cursor is null);
            TestSuite.Equal(b.Model.XAxis.VisibleRange, range);
        });
        suite.Run("cursor sync source handoff has no feedback loop", () =>
        {
            using var a = new View(); using var b = new View(); using var c = new View();
            using var group = new ChartCursorSyncGroup("three"); group.Add(a.Controller); group.Add(b.Controller); group.Add(c.Controller);
            int events = 0; a.Controller.ViewChanged += (_, _) => events++; b.Controller.ViewChanged += (_, _) => events++; c.Controller.ViewChanged += (_, _) => events++;
            a.Move(2); b.Move(7);
            TestSuite.Near(a.CursorX(), 7); TestSuite.Near(c.CursorX(), 7);
            TestSuite.Equal(events, 6);
        });
        suite.Run("cursor sync repeated refresh avoids render invalidation loops", () =>
        {
            using var a = new View(); using var b = new View();
            using var group = new ChartCursorSyncGroup("idle"); group.Add(a.Controller); group.Add(b.Controller);
            a.Move(4); int events = 0; b.Controller.ViewChanged += (_, _) => events++;
            for (int i = 0; i < 30; i++) { b.Resize(900, 500); group.Refresh(); }
            TestSuite.Equal(events, 0);
        });
        suite.Run("cursor sync resize refresh preserves data position", () =>
        {
            using var a = new View(); using var b = new View();
            using var group = new ChartCursorSyncGroup("resize"); group.Add(a.Controller); group.Add(b.Controller);
            a.Move(4); var before = b.Controller.View.Cursor;
            b.Resize(1500, 700); group.Refresh();
            TestSuite.Near(b.CursorX(), 4); TestSuite.True(before != b.Controller.View.Cursor);
        });
        suite.Run("cursor sync updates rollover after data changes", () =>
        {
            using var a = new View(); using var b = new View(); b.Controller.InterpolateRollover = true;
            using var group = new ChartCursorSyncGroup("data"); group.Add(a.Controller); group.Add(b.Controller);
            a.Move(5); TestSuite.Near(b.Controller.View.Hits.Single().Y, 5);
            b.Data.Update(1, 10, 8); TestSuite.Near(b.Controller.View.Hits.Single().Y, 4);
        });
        suite.Run("cursor sync registration removal clears owned state", () =>
        {
            using var a = new View(); using var b = new View();
            using var group = new ChartCursorSyncGroup("detach"); group.Add(a.Controller); var registration = group.Add(b.Controller);
            a.Move(4); registration.Dispose(); registration.Dispose();
            TestSuite.Equal(group.Count, 1); TestSuite.True(b.Controller.View.Cursor is null);
            a.Move(7); TestSuite.True(b.Controller.View.Cursor is null);
        });
        suite.Run("cursor sync source removal releases peer overlays", () =>
        {
            using var a = new View(); using var b = new View();
            using var group = new ChartCursorSyncGroup("source-detach"); var registration = group.Add(a.Controller); group.Add(b.Controller);
            a.Move(4); registration.Dispose();
            TestSuite.True(group.ActiveController is null && b.Controller.View.Cursor is null);
            TestSuite.True(a.Controller.View.Cursor is not null);
        });
        suite.Run("cursor sync invalid conversions are not drawn", () =>
        {
            using var a = new View(); using var b = new View();
            using var group = new ChartCursorSyncGroup("finite"); group.Add(a.Controller); group.Add(b.Controller, fromCanonical: _ => double.NaN);
            a.Move(4); TestSuite.True(b.Controller.View.Cursor is null);
        });
        suite.Run("cursor sync validates registration and disposal", () =>
        {
            using var a = new View(); using var polar = new View(); polar.Model.CoordinateSystem = CoordinateSystem.Polar;
            var group = new ChartCursorSyncGroup("validation"); group.Add(a.Controller);
            TestSuite.Throws<ArgumentException>(() => group.Add(a.Controller));
            TestSuite.Throws<ArgumentException>(() => group.Add(polar.Controller));
            using var b = new View(); TestSuite.Throws<InvalidOperationException>(() => group.Add(b.Controller, "missing"));
            group.Dispose(); group.Dispose();
            TestSuite.Throws<ObjectDisposedException>(() => group.Add(b.Controller));
        });
    }
}
