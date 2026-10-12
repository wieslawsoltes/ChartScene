using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;
using ChartScene.Rendering;

public static class DataPointEditTests
{
    private sealed class Fixture : IDisposable
    {
        public ChartModel Model { get; } = new() { ShowLegend = false };
        public XyDataSeries Data { get; } = new();
        public RenderableSeries Series { get; }
        public ChartController Controller { get; }
        public DataPointEditModifier Editor { get; } = new() { IsEnabled = true };
        public Fixture(SeriesKind kind = SeriesKind.Line, bool rotated = false, bool flipped = false, bool logarithmic = false)
        {
            Model.XAxis.VisibleRange = new(0, 10); Model.YAxis.VisibleRange = logarithmic ? new(1, 100) : new(0, 10);
            Model.XAxis.AutoRange = Model.YAxis.AutoRange = AutoRange.Never;
            if (rotated) { Model.XAxis.AxisAlignment = AxisAlignment.Left; Model.YAxis.AxisAlignment = AxisAlignment.Bottom; }
            Model.YAxis.FlippedCoordinates = flipped;
            if (logarithmic) Model.YAxis.Scale = AxisScale.Logarithmic;
            Data.AppendRange([1, 5, 9], logarithmic ? [2, 10, 80] : [2, 5, 8]);
            Series = new(kind) { DataSeries = Data, PointMarker = PointMarker.Ellipse }; Model.RenderableSeries.Add(Series);
            Controller = new(Model); Controller.Modifiers.Insert(0, Editor); Render();
        }
        public void Render() => Controller.SetFrame(new ChartRenderer().Render(new RecordingCanvas(), Model, 900, 600));
        public PointD Screen(double x, double y) => Controller.Frame!.Layout.Projection(Model, Series).ToScreen(x, y);
        public void Send(ChartInputKind kind, PointD point, int id = 1) => Controller.Process(new(kind, point, id));
        public void Drag(double from, double to)
        { Render(); var start = Screen(5, from); var end = Screen(5, to); Send(ChartInputKind.Down, start); Send(ChartInputKind.Move, end); Render(); Send(ChartInputKind.Up, end); Render(); }
        public void Dispose() { Controller.Dispose(); Model.Dispose(); }
    }
    public static void Register(TestSuite suite)
    {
        foreach (bool rotated in new[] { false, true }) foreach (bool flipped in new[] { false, true }) foreach (bool log in new[] { false, true })
            suite.Run($"point editor uses value coordinates rotated={rotated} flipped={flipped} log={log}", () =>
            {
                using var f = new Fixture(rotated: rotated, flipped: flipped, logarithmic: log); double from = log ? 10 : 5, to = log ? 25 : 7;
                var xRange = f.Model.XAxis.VisibleRange; var yRange = f.Model.YAxis.VisibleRange;
                f.Drag(from, to); TestSuite.Near(f.Data.Snapshot()[1, 1], to);
                TestSuite.Near(f.Data.Snapshot()[0, 1], 5); TestSuite.Equal(f.Editor.CommittedEdits, 1L);
                TestSuite.Equal(f.Model.XAxis.VisibleRange, xRange); TestSuite.Equal(f.Model.YAxis.VisibleRange, yRange);
                f.Controller.History.Undo(); TestSuite.Near(f.Data.Snapshot()[1, 1], from);
                f.Controller.History.Redo(); TestSuite.Near(f.Data.Snapshot()[1, 1], to);
            });
        foreach (var kind in new[] { SeriesKind.Line, SeriesKind.DigitalLine, SeriesKind.Spline, SeriesKind.Mountain, SeriesKind.SplineMountain, SeriesKind.Scatter, SeriesKind.Column, SeriesKind.Bar, SeriesKind.Impulse })
            suite.Run("point editor original XY endpoint " + kind, () =>
            {
                using var f = new Fixture(kind); f.Drag(5, 6); TestSuite.Near(f.Data.Snapshot()[1, 1], 6);
                TestSuite.Equal(f.Editor.CommittedEdits, 1L);
            });
        suite.Run("point editor stationary click and roundtrip create no history", () =>
        {
            using var f = new Fixture(logarithmic: true); var before = f.Data.Snapshot(); var start = f.Screen(5, 10);
            f.Send(ChartInputKind.Down, start); f.Send(ChartInputKind.Move, start); f.Send(ChartInputKind.Up, start);
            TestSuite.True(ReferenceEquals(before, f.Data.Snapshot())); TestSuite.True(!f.Controller.History.CanUndo);
            f.Send(ChartInputKind.Down, start); f.Send(ChartInputKind.Move, f.Screen(5, 25)); f.Render();
            f.Send(ChartInputKind.Move, start); f.Render(); f.Send(ChartInputKind.Up, start);
            TestSuite.Equal(BitConverter.DoubleToInt64Bits(f.Data.Snapshot()[1, 1]), BitConverter.DoubleToInt64Bits(10));
            TestSuite.True(!f.Controller.History.CanUndo);
        });
        suite.Run("point editor many live moves form one history entry", () =>
        {
            using var f = new Fixture(); f.Send(ChartInputKind.Down, f.Screen(5, 5));
            for (int i = 1; i <= 20; i++) { f.Send(ChartInputKind.Move, f.Screen(5, 5 + i * .1)); f.Render(); }
            f.Send(ChartInputKind.Up, f.Screen(5, 7)); TestSuite.Equal(f.Editor.CommittedEdits, 1L);
            f.Controller.History.Undo(); TestSuite.Near(f.Data.Snapshot()[1, 1], 5); TestSuite.True(!f.Controller.History.CanUndo);
        });
        suite.Run("point editor multiple gestures support complete undo and redo", () =>
        {
            using var f = new Fixture(); f.Drag(5, 6); f.Drag(6, 7); f.Drag(7, 8);
            for (int i = 7; i >= 5; i--) { TestSuite.True(f.Controller.History.Undo()); TestSuite.Near(f.Data.Snapshot()[1, 1], i); }
            for (int i = 6; i <= 8; i++) { TestSuite.True(f.Controller.History.Redo()); TestSuite.Near(f.Data.Snapshot()[1, 1], i); }
        });
        suite.Run("point editor Escape rolls back only its own preview", () =>
        {
            using var f = new Fixture(); f.Send(ChartInputKind.Down, f.Screen(5, 5)); f.Send(ChartInputKind.Move, f.Screen(5, 7)); f.Render();
            f.Controller.Process(new(ChartInputKind.Key, default, Key: "Escape"));
            TestSuite.Near(f.Data.Snapshot()[1, 1], 5); TestSuite.True(!f.Controller.HasCapture && !f.Editor.IsEditing);
            TestSuite.True(!f.Controller.History.CanUndo); TestSuite.Equal(f.Editor.LastOutcome, DataPointEditOutcome.Cancelled);
        });
        suite.Run("point editor external replacement supersedes preview and cancellation", () =>
        {
            using var f = new Fixture(); f.Send(ChartInputKind.Down, f.Screen(5, 5)); f.Send(ChartInputKind.Move, f.Screen(5, 7));
            f.Data.ReplaceAll([1, 5, 9], [3, 9, 4]); var revision = f.Data.Revision;
            f.Controller.Cancel(); TestSuite.Near(f.Data.Snapshot()[1, 1], 9); TestSuite.Equal(f.Data.Revision, revision);
            TestSuite.Equal(f.Editor.LastOutcome, DataPointEditOutcome.Conflict); TestSuite.True(!f.Controller.History.CanUndo);
        });
        suite.Run("point editor callback edits cannot be overwritten or recorded", () =>
        {
            using var f = new Fixture(); bool once = false;
            f.Data.DataSeriesChanged += (_, _) => { if (!once) { once = true; f.Data.Update(1, 5, 9); } };
            f.Drag(5, 7); TestSuite.Near(f.Data.Snapshot()[1, 1], 9);
            TestSuite.True(!f.Controller.History.CanUndo); TestSuite.Equal(f.Editor.LastOutcome, DataPointEditOutcome.Conflict);
        });
        suite.Run("point editor cancellation inside a data callback leaves no stale session", () =>
        {
            using var f = new Fixture(); f.Data.DataSeriesChanged += (_, _) => f.Controller.Cancel();
            f.Drag(5, 7); TestSuite.True(!f.Editor.IsEditing && !f.Controller.HasCapture);
            TestSuite.True(!f.Controller.History.CanUndo);
        });
        suite.Run("point editor axis and layout changes invalidate captured coordinates", () =>
        {
            using var f = new Fixture(); f.Send(ChartInputKind.Down, f.Screen(5, 5));
            f.Model.YAxis.FlippedCoordinates = true; f.Send(ChartInputKind.Move, f.Screen(5, 7)); f.Controller.Cancel();
            TestSuite.Near(f.Data.Snapshot()[1, 1], 5); TestSuite.True(!f.Controller.History.CanUndo);
            f.Render(); f.Send(ChartInputKind.Down, f.Screen(5, 5));
            f.Controller.SetFrame(new ChartRenderer().Render(new RecordingCanvas(), f.Model, 700, 500));
            f.Send(ChartInputKind.Up, f.Screen(5, 7)); TestSuite.Near(f.Data.Snapshot()[1, 1], 5);
        });
        suite.Run("point editor stale undo preserves externally changed source", () =>
        {
            using var f = new Fixture(); f.Drag(5, 7); f.Data.Update(1, 5, 9); long revision = f.Data.Revision;
            f.Controller.History.Undo(); f.Controller.History.Redo();
            TestSuite.Near(f.Data.Snapshot()[1, 1], 9); TestSuite.Equal(f.Data.Revision, revision);
            TestSuite.Equal(f.Editor.LastOutcome, DataPointEditOutcome.Conflict);
        });
        suite.Run("point editor replaced series cannot receive old history writes", () =>
        {
            using var f = new Fixture(); f.Drag(5, 7); var replacement = new XyDataSeries(); replacement.AppendRange([1, 5, 9], [2, 9, 8]);
            f.Series.DataSeries = replacement; f.Controller.History.Undo();
            TestSuite.Near(replacement.Snapshot()[1, 1], 9); TestSuite.Near(f.Data.Snapshot()[1, 1], 7);
        });
        suite.Run("point editor ignores gaps curve interiors and unsupported stacks", () =>
        {
            using var f = new Fixture(); f.Data.ReplaceAll([1, 5, 9], [2, double.NaN, 8]); f.Render();
            f.Send(ChartInputKind.Down, f.Screen(5, 5)); TestSuite.True(!f.Editor.IsEditing);
            using var g = new Fixture(SeriesKind.StackedColumn); g.Drag(5, 7);
            TestSuite.Near(g.Data.Snapshot()[1, 1], 5); TestSuite.True(!g.Controller.History.CanUndo);
        });
        suite.Run("point editor disabled mode and invalid options are explicit", () =>
        {
            using var f = new Fixture(); TestSuite.Throws<ArgumentOutOfRangeException>(() => f.Editor.HitRadius = double.NaN);
            f.Editor.IsEnabled = false; TestSuite.Equal(f.Editor.Handle(f.Controller, new(ChartInputKind.Down, f.Screen(5, 5))), ModifierResult.Unhandled);
            f.Editor.IsEnabled = true; f.Model.YAxis.AutoRange = AutoRange.Always; f.Render();
            f.Editor.Handle(f.Controller, new(ChartInputKind.Down, f.Screen(5, 5))); TestSuite.True(!f.Editor.IsEditing);
        });
        suite.Run("point editor active gesture uses only its captured pointer", () =>
        {
            using var f = new Fixture(); f.Send(ChartInputKind.Down, f.Screen(5, 5));
            // The controller must route capture consistently. Direct modifier users own pointer routing.
            f.Send(ChartInputKind.Move, f.Screen(5, 7)); f.Render(); f.Send(ChartInputKind.Up, f.Screen(5, 7));
            TestSuite.Near(f.Data.Snapshot()[1, 1], 7); TestSuite.True(!f.Controller.HasCapture);
        });
    }
}
