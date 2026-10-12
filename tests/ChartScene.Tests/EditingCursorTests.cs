using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;
using ChartScene.Rendering;

public static class EditingCursorTests
{
    private sealed class ObservedData : DataSeries, IDataSeries
    {
        public long SnapshotReads { get; private set; }
        SeriesSnapshot IDataSeries.Snapshot() { SnapshotReads++; return Snapshot(); }
    }
    private sealed class Fixture : IDisposable
    {
        public ChartModel Model { get; } = new() { ShowLegend = false };
        public ObservedData Data { get; } = new();
        public RenderableSeries Series { get; }
        public ChartController Controller { get; }
        public DataPointEditModifier Editor { get; } = new() { IsEnabled = true };
        public Fixture(int count = 3)
        {
            Model.XAxis.VisibleRange = new(0, count - 1); Model.YAxis.VisibleRange = new(0, 10);
            Model.XAxis.AutoRange = Model.YAxis.AutoRange = AutoRange.Never;
            Data.AppendRange(Enumerable.Range(0, count).Select(i => (double)i).ToArray(), Enumerable.Repeat(5.0, count).ToArray());
            Series = new() { DataSeries = Data }; Model.RenderableSeries.Add(Series);
            Controller = new(Model); Controller.Modifiers.Insert(0, Editor);
            Controller.SetFrame(new ChartRenderer().Render(new RecordingCanvas(), Model, 900, 600));
        }
        public PointD Screen(double x, double y) => Controller.Frame!.Layout.Projection(Model, Series).ToScreen(x, y);
        public void Send(ChartInputKind kind, PointD point, int id = 1) => Controller.Process(new(kind, point, id));
        public void Dispose() { Controller.Dispose(); Model.Dispose(); }
    }
    public static void Register(TestSuite suite)
    {
        suite.Run("editing double-clicks never escape into navigation history", () =>
        {
            using var f = new Fixture(); var point = f.Screen(1, 5);
            var x = f.Model.XAxis.VisibleRange; var y = f.Model.YAxis.VisibleRange;
            long revision = f.Data.Revision;
            f.Send(ChartInputKind.Down, point); f.Send(ChartInputKind.Up, point);
            // Native DoubleTapped is routed without the original PointerId and can
            // arrive after the second release, when the modifier has no session.
            f.Controller.Process(new(ChartInputKind.DoubleClick, point));
            TestSuite.Equal(f.Model.XAxis.VisibleRange, x); TestSuite.Equal(f.Model.YAxis.VisibleRange, y);
            TestSuite.Equal(f.Data.Revision, revision); TestSuite.True(!f.Controller.History.CanUndo);
            f.Send(ChartInputKind.Down, point); TestSuite.True(f.Editor.IsEditing);
            f.Send(ChartInputKind.Move, f.Screen(1, 7)); f.Send(ChartInputKind.Up, f.Screen(1, 7));
            TestSuite.Near(f.Data.Snapshot()[1, 1], 7); f.Controller.History.Undo();
            TestSuite.Near(f.Data.Snapshot()[1, 1], 5); TestSuite.True(!f.Controller.History.CanUndo);
        });
        suite.Run("disabled point editing retains double-click zoom extents", () =>
        {
            using var f = new Fixture(); f.Editor.IsEnabled = false;
            f.Controller.Process(new(ChartInputKind.DoubleClick, f.Screen(1, 5)));
            TestSuite.True(f.Controller.History.CanUndo);
        });
        suite.Run("captured editing cursor stays on source point during perpendicular and foreign movement", () =>
        {
            using var f = new Fixture(); var point = f.Screen(1, 5);
            f.Send(ChartInputKind.Down, point); var hits = f.Controller.View.Hits; long reads = f.Data.SnapshotReads;
            TestSuite.True(f.Editor.IsEditing); TestSuite.Equal(hits.Count, 1);
            f.Send(ChartInputKind.Move, point + new PointD(75, 0));
            TestSuite.Equal(f.Controller.View.Cursor, (PointD?)point); TestSuite.True(ReferenceEquals(hits, f.Controller.View.Hits));
            f.Send(ChartInputKind.Move, point + new PointD(100, 50), 2);
            TestSuite.Equal(f.Controller.View.Cursor, (PointD?)point); TestSuite.True(ReferenceEquals(hits, f.Controller.View.Hits));
            TestSuite.Equal(f.Data.SnapshotReads, reads);
        });
        suite.Run("captured million-point editing does not read rollover source snapshots", () =>
        {
            using var f = new Fixture(1000000); const int index = 500000, moves = 100;
            f.Send(ChartInputKind.Down, f.Screen(index, 5)); TestSuite.True(f.Editor.IsEditing); TestSuite.Equal(f.Editor.ActiveIndex, index);
            long reads = f.Data.SnapshotReads; long revision = f.Data.Revision;
            for (int i = 0; i < moves; i++) f.Send(ChartInputKind.Move, f.Screen(index, i % 2 == 0 ? 6 : 7));
            long capturedReads = f.Data.SnapshotReads - reads;
            TestSuite.Equal(capturedReads, 0L); TestSuite.Equal(f.Data.Revision - revision, (long)moves);
            f.Send(ChartInputKind.Up, f.Screen(index, 7)); TestSuite.Equal(f.Editor.CommittedEdits, 1L);
            TestSuite.Near(f.Data.Snapshot()[1, index], 7);
            var report = new { sourcePoints = 1000000, capturedMoves = moves, rolloverSnapshotReads = capturedReads,
                scope = "Captured pointer dispatch without frame rendering or user subscribers. Existing uncaptured rollover and pan remain enabled. Not a whole-application allocation, GPU, FPS or presentation claim." };
            Directory.CreateDirectory("artifacts/performance");
            File.WriteAllText("artifacts/performance/editing-dispatch.json", JsonSerializer.Serialize(report));
            Console.WriteLine("PERF_EDITING_DISPATCH " + JsonSerializer.Serialize(report));
        });
        suite.Run("uncaptured editing and ordinary pan keep automatic rollover", () =>
        {
            using var f = new Fixture(); var point = f.Screen(1, 5); long reads = f.Data.SnapshotReads;
            f.Send(ChartInputKind.Move, point); TestSuite.True(f.Data.SnapshotReads > reads); TestSuite.Equal(f.Controller.View.Cursor, (PointD?)point);
            f.Editor.IsEnabled = false; f.Send(ChartInputKind.Down, point); reads = f.Data.SnapshotReads;
            var moved = point + new PointD(12, 0); f.Send(ChartInputKind.Move, moved);
            TestSuite.True(f.Data.SnapshotReads > reads); TestSuite.Equal(f.Controller.View.Cursor, (PointD?)moved);
        });
        foreach (bool dispose in new[] { false, true })
            suite.Run("point start callback cannot capture after controller " + (dispose ? "disposal" : "cancellation"), () =>
            {
                using var f = new Fixture(); long revision = f.Data.Revision;
                f.Editor.Changed += (_, _) =>
                {
                    if (f.Editor.LastOutcome != DataPointEditOutcome.Started) return;
                    if (dispose) f.Controller.Dispose(); else f.Controller.Cancel();
                };
                f.Send(ChartInputKind.Down, f.Screen(1, 5));
                TestSuite.True(!f.Editor.IsEditing && !f.Controller.HasCapture);
                TestSuite.Equal(f.Data.Revision, revision); TestSuite.True(!f.Controller.History.CanUndo);
            });
        suite.Run("point start callback source edits supersede capture immediately", () =>
        {
            using var f = new Fixture();
            f.Editor.Changed += (_, _) =>
            {
                if (f.Editor.LastOutcome == DataPointEditOutcome.Started) f.Data.Update(1, 1, 8);
            };
            f.Send(ChartInputKind.Down, f.Screen(1, 5));
            TestSuite.True(!f.Editor.IsEditing && !f.Controller.HasCapture);
            TestSuite.Near(f.Data.Snapshot()[1, 1], 8); TestSuite.True(!f.Controller.History.CanUndo);
            TestSuite.Equal(f.Editor.LastOutcome, DataPointEditOutcome.Conflict);
        });
    }
}
