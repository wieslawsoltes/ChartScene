using System.ComponentModel;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Interaction;

public static class NavigatorReentrancyTests
{
    private static readonly RectD Track = new(0, 0, 1000, 100);
    private static NumericAxis Axis() => new() { VisibleRange = new(20, 40), AutoRange = AutoRange.Always };
    private static void Expect(AxisModel axis, double minimum, double maximum)
    { TestSuite.Equal(axis.VisibleRange, new DoubleRange(minimum, maximum)); }
    public static void Register(TestSuite suite)
    {
        suite.Run("navigator reentrant AutoRange callback cannot overwrite external range", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100));
            bool edited = false;
            n.Changed += (_, _) => { if (!edited && axis.AutoRange == AutoRange.Never) { edited = true; axis.VisibleRange = new(71, 89); } };
            n.Begin(new(300, 50), Track); n.Move(new(400, 50)); n.End(new(500, 50)); n.Cancel();
            Expect(axis, 71, 89); TestSuite.True(!n.HasGesture && !n.History.CanUndo);
        });
        suite.Run("navigator reentrant VisibleRange callback invalidates later mouse up", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100)); bool edited = false;
            n.Changed += (_, _) => { if (!edited && axis.VisibleRange.Min > 20) { edited = true; axis.VisibleRange = new(65, 95); } };
            n.Begin(new(300, 50), Track); n.Move(new(400, 50)); n.End(new(700, 50)); n.Cancel();
            Expect(axis, 65, 95); TestSuite.True(!n.History.CanUndo);
        });
        suite.Run("navigator reentrant auto policy wins over pending gesture", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100)); bool edited = false;
            n.Changed += (_, _) => { if (!edited && axis.AutoRange == AutoRange.Never) { edited = true; axis.AutoRange = AutoRange.Once; } };
            n.Begin(new(300, 50), Track); n.End(new(600, 50)); n.Cancel();
            Expect(axis, 20, 40); TestSuite.Equal(axis.AutoRange, AutoRange.Once); TestSuite.True(!n.History.CanUndo);
        });
        suite.Run("navigator external extents callback aborts precomputed pan", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100)); bool edited = false;
            n.Changed += (_, _) => { if (!edited && axis.AutoRange == AutoRange.Never) { edited = true; n.FullRange = new(0, 200); axis.VisibleRange = new(110, 180); } };
            n.Pan(1); Expect(axis, 110, 180); TestSuite.True(!n.History.CanUndo);
        });
        foreach (string property in new[] { "Scale", "FlippedCoordinates", "LogarithmicBase" })
            suite.Run("navigator callback transform supersedes captured gesture " + property, () =>
            {
                var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(1, 100)); bool edited = false;
                n.Changed += (_, _) =>
                {
                    if (edited || axis.AutoRange != AutoRange.Never) return;
                    edited = true;
                    if (property == "Scale") axis.Scale = AxisScale.Logarithmic;
                    else if (property == "FlippedCoordinates") axis.FlippedCoordinates = true;
                    else axis.LogarithmicBase = 2;
                };
                n.Begin(n.Layout(Track).Selection.Center, Track); n.Move(new(500, 50)); n.End(new(600, 50));
                Expect(axis, 20, 40); TestSuite.True(!n.History.CanUndo && !n.HasGesture);
            });
        suite.Run("navigator subscribers registered earlier cannot hide external edits", () =>
        {
            var axis = Axis(); bool edited = false;
            axis.PropertyChanged += (_, e) => { if (!edited && e.PropertyName == nameof(AxisModel.AutoRange)) { edited = true; axis.VisibleRange = new(61, 81); } };
            using var n = new AxisRangeNavigator(axis, new(0, 100));
            n.Zoom(.5); Expect(axis, 61, 81); TestSuite.True(!n.History.CanUndo);
        });
        suite.Run("navigator cancellation yields to newer range inside rollback callback", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100));
            n.Begin(new(300, 50), Track); n.Move(new(400, 50)); bool edited = false;
            n.Changed += (_, _) => { if (!edited && axis.AutoRange == AutoRange.Always) { edited = true; axis.VisibleRange = new(78, 98); } };
            n.Cancel(); Expect(axis, 78, 98); TestSuite.True(!n.History.CanUndo);
        });
        suite.Run("navigator nested pan supersedes outer pan without phantom history", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100)); bool nested = false;
            n.Changed += (_, _) => { if (!nested && axis.AutoRange == AutoRange.Never) { nested = true; n.Pan(.5); } };
            n.Pan(2); TestSuite.Near(axis.VisibleRange.Min, 30); TestSuite.Near(axis.VisibleRange.Max, 50); n.History.Undo(); Expect(axis, 20, 40); TestSuite.True(!n.History.CanUndo);
        });
        suite.Run("navigator source replacement during recenter prevents capture", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100)); bool edited = false;
            n.Changed += (_, _) => { if (!edited && axis.AutoRange == AutoRange.Never) { edited = true; axis.VisibleRange = new(1, 11); } };
            TestSuite.True(!n.Begin(new(900, 50), Track)); Expect(axis, 1, 11); TestSuite.True(!n.HasGesture);
        });
        suite.Run("navigator disposal from notification is bounded and unsubscribes", () =>
        {
            var axis = Axis(); var n = new AxisRangeNavigator(axis, new(0, 100)); bool disposed = false;
            n.Changed += (_, _) => { if (!disposed && axis.AutoRange == AutoRange.Never) { disposed = true; n.Dispose(); } };
            n.Pan(1); Expect(axis, 20, 40); TestSuite.True(!n.History.CanUndo); n.Dispose();
            TestSuite.Throws<ObjectDisposedException>(() => n.Pan(1));
        });
    }
}
