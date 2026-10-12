using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;
using ChartScene.Rendering;

public static class DataPointEditBoundaryTests
{
    private sealed class Fixture : IDisposable
    {
        public ChartModel Model { get; } = new() { ShowLegend = false };
        public XyDataSeries Data { get; } = new();
        public RenderableSeries Series { get; }
        public ChartController Controller { get; }
        public DataPointEditModifier Editor { get; } = new() { IsEnabled = true };
        public Fixture()
        {
            Model.XAxis.VisibleRange = Model.YAxis.VisibleRange = new(0, 10);
            Model.XAxis.AutoRange = Model.YAxis.AutoRange = AutoRange.Never;
            Data.AppendRange([1, 5, 9], [2, 5, 8]);
            Series = new() { DataSeries = Data, PointMarker = PointMarker.Ellipse };
            Model.RenderableSeries.Add(Series); Controller = new(Model); Controller.Modifiers.Insert(0, Editor); Render();
        }
        public void Render() => Controller.SetFrame(new ChartRenderer().Render(new RecordingCanvas(), Model, 900, 600));
        public PointD Screen(double x, double y) => Controller.Frame!.Layout.Projection(Model, Series).ToScreen(x, y);
        public void Send(ChartInputKind kind, PointD point, int id = 1) => Controller.Process(new(kind, point, id));
        public void Dispose() { Controller.Dispose(); Model.Dispose(); }
    }
    public static void Register(TestSuite suite)
    {
        suite.Run("point editor rejects other pointer moves presses and releases during capture", () =>
        {
            using var f = new Fixture(); var start = f.Screen(5, 5); var end = f.Screen(5, 7);
            f.Send(ChartInputKind.Down, start, 1);
            f.Send(ChartInputKind.Move, end, 2); f.Send(ChartInputKind.Down, end, 2); f.Send(ChartInputKind.Up, end, 2);
            TestSuite.Near(f.Data.Snapshot()[1, 1], 5); TestSuite.True(f.Editor.IsEditing && f.Controller.HasCapture);
            f.Send(ChartInputKind.Move, end, 1); f.Render(); f.Send(ChartInputKind.Up, end, 1);
            TestSuite.Near(f.Data.Snapshot()[1, 1], 7); TestSuite.Equal(f.Editor.CommittedEdits, 1L);
        });
        suite.Run("point editor secondary axis does not edit using the primary scale", () =>
        {
            using var f = new Fixture();
            var secondary = new NumericAxis { Id = "Y2", AxisAlignment = AxisAlignment.Right, VisibleRange = new(100, 200), AutoRange = AutoRange.Never };
            f.Model.YAxes.Add(secondary); f.Series.YAxisId = "Y2"; f.Data.ReplaceAll([1, 5, 9], [120, 150, 180]); f.Render();
            var primary = f.Model.YAxis.VisibleRange;
            f.Send(ChartInputKind.Down, f.Screen(5, 150)); f.Send(ChartInputKind.Move, f.Screen(5, 175)); f.Render(); f.Send(ChartInputKind.Up, f.Screen(5, 175));
            TestSuite.Near(f.Data.Snapshot()[1, 1], 175); TestSuite.Equal(f.Model.YAxis.VisibleRange, primary);
            TestSuite.Equal(secondary.VisibleRange, new DoubleRange(100, 200));
        });
        suite.Run("point editor retains grab offset and ignores perpendicular movement", () =>
        {
            using var f = new Fixture(); var exact = f.Screen(5, 5); var offset = new PointD(3, 4); var before = f.Data.Snapshot();
            f.Send(ChartInputKind.Down, exact + offset);
            f.Send(ChartInputKind.Move, exact + offset + new PointD(90, 0));
            TestSuite.True(ReferenceEquals(before, f.Data.Snapshot()));
            var end = f.Screen(5, 7) + offset + new PointD(90, 0);
            f.Send(ChartInputKind.Move, end); f.Render(); f.Send(ChartInputKind.Up, end);
            TestSuite.Near(f.Data.Snapshot()[1, 1], 7); TestSuite.Near(f.Data.Snapshot()[0, 1], 5);
        });
        suite.Run("point editor clamps captured pointer outside the value viewport", () =>
        {
            using var f = new Fixture(); var start = f.Screen(5, 5); var plot = f.Controller.Frame!.Layout.PlotArea;
            f.Send(ChartInputKind.Down, start); var outside = new PointD(start.X, plot.Y - 1000);
            f.Send(ChartInputKind.Move, outside); f.Render(); f.Send(ChartInputKind.Up, outside);
            TestSuite.Near(f.Data.Snapshot()[1, 1], 10); f.Controller.History.Undo(); TestSuite.Near(f.Data.Snapshot()[1, 1], 5);
        });
        suite.Run("point editor FIFO eviction supersedes captured original indices", () =>
        {
            using var f = new Fixture(); f.Data.FifoCapacity = 3; f.Render();
            f.Send(ChartInputKind.Down, f.Screen(5, 5)); f.Data.Append(10, 9); var retained = f.Data.Snapshot();
            f.Send(ChartInputKind.Move, f.Screen(5, 7)); f.Send(ChartInputKind.Up, f.Screen(5, 7));
            TestSuite.True(ReferenceEquals(retained, f.Data.Snapshot())); TestSuite.True(!f.Controller.History.CanUndo);
            TestSuite.Equal(f.Editor.LastOutcome, DataPointEditOutcome.Conflict);
        });
        suite.Run("point editor removed or hidden source cannot receive unfinished writes", () =>
        {
            using var f = new Fixture(); f.Send(ChartInputKind.Down, f.Screen(5, 5)); f.Series.IsVisible = false;
            f.Send(ChartInputKind.Up, f.Screen(5, 7)); TestSuite.Near(f.Data.Snapshot()[1, 1], 5);
            f.Series.IsVisible = true; f.Render(); f.Send(ChartInputKind.Down, f.Screen(5, 5));
            f.Model.RenderableSeries.Remove(f.Series); f.Controller.Cancel();
            TestSuite.Near(f.Data.Snapshot()[1, 1], 5); TestSuite.True(!f.Editor.IsEditing && !f.Controller.History.CanUndo);
        });
        suite.Run("point editor changing mode mid-gesture rolls back its preview", () =>
        {
            using var f = new Fixture(); f.Send(ChartInputKind.Down, f.Screen(5, 5)); f.Send(ChartInputKind.Move, f.Screen(5, 7)); f.Render();
            f.Editor.IsEnabled = false; f.Send(ChartInputKind.Up, f.Screen(5, 7));
            TestSuite.Near(f.Data.Snapshot()[1, 1], 5); TestSuite.True(!f.Editor.IsEditing && !f.Controller.HasCapture);
        });
        suite.Run("point editor different edited indices share ordered history", () =>
        {
            using var f = new Fixture();
            f.Send(ChartInputKind.Down, f.Screen(1, 2)); f.Send(ChartInputKind.Move, f.Screen(1, 3)); f.Render(); f.Send(ChartInputKind.Up, f.Screen(1, 3));
            f.Render(); f.Send(ChartInputKind.Down, f.Screen(9, 8)); f.Send(ChartInputKind.Move, f.Screen(9, 6)); f.Render(); f.Send(ChartInputKind.Up, f.Screen(9, 6));
            f.Controller.History.Undo(); TestSuite.Near(f.Data.Snapshot()[1, 2], 8);
            f.Controller.History.Undo(); TestSuite.Near(f.Data.Snapshot()[1, 0], 2);
            f.Controller.History.Redo(); f.Controller.History.Redo();
            TestSuite.Near(f.Data.Snapshot()[1, 0], 3); TestSuite.Near(f.Data.Snapshot()[1, 2], 6);
        });
        suite.Run("point editor edits after conflict use a new epoch not obsolete history", () =>
        {
            using var f = new Fixture();
            f.Send(ChartInputKind.Down, f.Screen(5, 5)); f.Send(ChartInputKind.Move, f.Screen(5, 7)); f.Render(); f.Send(ChartInputKind.Up, f.Screen(5, 7));
            f.Data.Update(1, 5, 8); f.Render();
            f.Send(ChartInputKind.Down, f.Screen(5, 8)); f.Send(ChartInputKind.Move, f.Screen(5, 9)); f.Render(); f.Send(ChartInputKind.Up, f.Screen(5, 9));
            f.Controller.History.Undo(); TestSuite.Near(f.Data.Snapshot()[1, 1], 8);
            long revision = f.Data.Revision; f.Controller.History.Undo();
            TestSuite.Near(f.Data.Snapshot()[1, 1], 8); TestSuite.Equal(f.Data.Revision, revision);
        });
        suite.Run("point editor releases observed sources when controller is disposed", () =>
        {
            using var f = new Fixture(); f.Send(ChartInputKind.Down, f.Screen(5, 5)); f.Send(ChartInputKind.Move, f.Screen(5, 7));
            f.Controller.Dispose(); TestSuite.Near(f.Data.Snapshot()[1, 1], 5); TestSuite.True(!f.Editor.IsEditing);
            var outcome = f.Editor.LastOutcome; f.Series.SeriesName = "Detached observation"; f.Model.YAxis.FlippedCoordinates = true;
            TestSuite.Equal(f.Editor.LastOutcome, outcome);
        });
    }
}
