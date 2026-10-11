using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Interaction;

public static class RangeNavigatorTests
{
    private static readonly RectD Track = new(20, 30, 1000, 200);
    private static NumericAxis Axis() => new() { VisibleRange = new(20, 40), AutoRange = AutoRange.Always };
    private static PointD Point(AxisRangeNavigator navigator, double fraction)
    {
        if (navigator.IsReversed) fraction = 1 - fraction;
        return navigator.IsVertical ? new(Track.Center.X, Track.Y + Track.Height * fraction) : new(Track.X + Track.Width * fraction, Track.Center.Y);
    }
    private static void Range(AxisModel axis, double min, double max)
    { TestSuite.Near(axis.VisibleRange.Min, min); TestSuite.Near(axis.VisibleRange.Max, max); }
    public static void Register(TestSuite suite)
    {
        foreach (bool vertical in new[] { false, true }) foreach (bool flipped in new[] { false, true })
        {
            string suffix = $"vertical={vertical} flipped={flipped}";
            suite.Run("overview body pan " + suffix, () =>
            {
                var axis = Axis(); axis.FlippedCoordinates = flipped;
                using var n = new AxisRangeNavigator(axis, new(0, 100)) { IsVertical = vertical };
                TestSuite.True(n.Begin(Point(n, .3), Track)); TestSuite.Equal(n.ActivePart, RangeNavigationPart.Body);
                n.Move(Point(n, .4)); n.End(Point(n, .5)); Range(axis, 40, 60);
                TestSuite.True(n.History.CanUndo); n.History.Undo(); Range(axis, 20, 40); TestSuite.Equal(axis.AutoRange, AutoRange.Always);
                n.History.Redo(); Range(axis, 40, 60); TestSuite.Equal(axis.AutoRange, AutoRange.Never);
            });
            suite.Run("overview grips " + suffix, () =>
            {
                var axis = Axis(); axis.FlippedCoordinates = flipped;
                using var n = new AxisRangeNavigator(axis, new(0, 100)) { IsVertical = vertical, MinimumSelectionFraction = .01 };
                var layout = n.Layout(Track);
                n.Begin(layout.MinimumGrip, Track); n.End(Point(n, .1)); Range(axis, 10, 40);
                n.Begin(n.Layout(Track).MaximumGrip, Track); n.End(Point(n, .7)); Range(axis, 10, 70);
                n.Begin(n.Layout(Track).MinimumGrip, Track); n.End(Point(n, 2)); Range(axis, 69, 70);
                n.Begin(n.Layout(Track).MaximumGrip, Track); n.End(Point(n, -2)); Range(axis, 69, 70);
            });
        }
        suite.Run("overview outside click recenters without changing span", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100));
            n.Begin(Point(n, .9), Track); n.End(Point(n, .9)); Range(axis, 80, 100);
            n.Begin(Point(n, .1), Track); n.End(Point(n, .1)); Range(axis, 0, 20);
        });
        suite.Run("overview shift selection and minimum span", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100)) { MinimumSelectionFraction = .05 };
            n.Begin(Point(n, .65), Track, true); n.End(Point(n, .25)); Range(axis, 25, 65);
            n.Begin(Point(n, .4), Track, true); n.End(Point(n, .4)); Range(axis, 40, 45);
        });
        suite.Run("overview cancel restores viewport and auto range", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100));
            n.Begin(Point(n, .3), Track); n.Move(Point(n, .7)); Range(axis, 60, 80);
            n.Cancel(); Range(axis, 20, 40); TestSuite.Equal(axis.AutoRange, AutoRange.Always); TestSuite.True(!n.History.CanUndo);
        });
        suite.Run("overview external viewport changes supersede stale drag", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100));
            n.Begin(Point(n, .3), Track); n.Move(Point(n, .6)); axis.VisibleRange = new(81, 99);
            TestSuite.True(!n.HasGesture); n.End(Point(n, .8)); n.Cancel(); Range(axis, 81, 99); TestSuite.True(!n.History.CanUndo);
        });
        suite.Run("overview changing extents invalidates gesture without undoing new data", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100));
            n.Begin(Point(n, .3), Track); n.Move(Point(n, .4)); var range = axis.VisibleRange;
            n.FullRange = new(0, 200); n.End(Point(n, .8)); TestSuite.Equal(axis.VisibleRange, range);
        });
        suite.Run("overview logarithmic pan and anchored zoom preserve ratios", () =>
        {
            var axis = new LogarithmicNumericAxis { VisibleRange = new(10, 100), AutoRange = AutoRange.Never };
            using var n = new AxisRangeNavigator(axis, new(1, 1000));
            n.Begin(Point(n, .5), Track); n.End(Point(n, .6));
            TestSuite.Near(axis.VisibleRange.Max / axis.VisibleRange.Min, 10);
            TestSuite.Near(n.Selection.Minimum, 1.0 / 3 + .1);
            double before = n.Selection.Minimum + (n.Selection.Maximum - n.Selection.Minimum) * .25;
            n.Zoom(.5, .25);
            TestSuite.Near(n.Selection.Minimum + (n.Selection.Maximum - n.Selection.Minimum) * .25, before);
            TestSuite.Near(axis.VisibleRange.Max / axis.VisibleRange.Min, Math.Sqrt(10));
        });
        suite.Run("overview close-large and tiny logarithmic extents stay finite", () =>
        {
            foreach (var full in new[] { new DoubleRange(1e200, 1e200 + 1e187), new DoubleRange(double.Epsilon, double.Epsilon * 100000) })
            {
                var axis = new LogarithmicNumericAxis { VisibleRange = full }; using var n = new AxisRangeNavigator(axis, full);
                TestSuite.Equal(n.ValueAt(0), full.Min); TestSuite.Equal(n.ValueAt(1), full.Max);
                for (int i = 1; i < 10; i++) TestSuite.True(double.IsFinite(n.ValueAt(i / 10.0)));
                n.Zoom(.5); TestSuite.True(axis.VisibleRange.IsValid && axis.VisibleRange.Min > 0);
            }
        });
        suite.Run("overview keyboard-style pan zoom fit and accessible position", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100));
            n.Pan(.5); Range(axis, 30, 50); n.Zoom(.5); Range(axis, 35, 45);
            n.SetStartFraction(.65); Range(axis, 65, 75); n.SetStartFraction(2); Range(axis, 90, 100);
            n.Fit(); Range(axis, 0, 100); n.Zoom(100); Range(axis, 0, 100);
        });
        suite.Run("overview degenerate out-of-domain viewport is bounded safely", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100));
            axis.VisibleRange = new(200, 300); TestSuite.Near(n.Layout(Track).Selection.Width, 0);
            n.Fit(); Range(axis, 0, 100);
            axis.VisibleRange = new(-100, 200); n.Begin(Point(n, .5), Track); n.End(Point(n, .8)); Range(axis, 0, 100);
        });
        suite.Run("overview no-op gesture does not disable auto range", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100));
            n.Begin(Point(n, .3), Track); n.End(Point(n, .3)); TestSuite.True(!n.History.CanUndo); TestSuite.Equal(axis.AutoRange, AutoRange.Always);
        });
        suite.Run("overview history survives navigator detachment", () =>
        {
            var axis = Axis(); var history = new UndoManager(); var n = new AxisRangeNavigator(axis, new(0, 100), history);
            n.Pan(1); n.Dispose(); Range(axis, 40, 60); history.Undo(); Range(axis, 20, 40); history.Redo(); Range(axis, 40, 60);
        });
        suite.Run("overview observes axis but unsubscribes on dispose", () =>
        {
            var axis = Axis(); var n = new AxisRangeNavigator(axis, new(0, 100)); int events = 0; n.Changed += (_, _) => events++;
            axis.VisibleRange = new(25, 45); TestSuite.True(events > 0); n.Dispose(); int previous = events;
            axis.VisibleRange = new(30, 50); TestSuite.Equal(events, previous); n.Dispose();
            TestSuite.Throws<ObjectDisposedException>(() => n.Fit());
        });
        suite.Run("overview stationary arbitrary logarithmic viewport stays bit-exact", () =>
        {
            var random = new Random(1337);
            for (int i = 0; i < 64; i++)
            {
                var axis = new LogarithmicNumericAxis { VisibleRange = new(2 + random.NextDouble(), 70 + random.NextDouble()), AutoRange = AutoRange.Always };
                using var n = new AxisRangeNavigator(axis, new(1, 1000));
                var before = axis.VisibleRange;
                var point = n.Layout(Track).Selection.Center;
                n.Begin(point, Track); n.End(point);
                TestSuite.Equal(axis.VisibleRange, before); TestSuite.Equal(axis.AutoRange, AutoRange.Always);
                TestSuite.True(!n.History.CanUndo);
            }
        });
        suite.Run("overview roundtrip gesture creates no phantom history", () =>
        {
            var axis = new LogarithmicNumericAxis { VisibleRange = new(2.731, 73.125), AutoRange = AutoRange.Always };
            using var n = new AxisRangeNavigator(axis, new(1, 1000)); var before = axis.VisibleRange;
            var point = n.Layout(Track).Selection.Center;
            n.Begin(point, Track); n.Move(point + new PointD(40, 0)); n.End(point);
            TestSuite.Equal(axis.VisibleRange, before); TestSuite.Equal(axis.AutoRange, AutoRange.Always); TestSuite.True(!n.History.CanUndo);
        });
        suite.Run("overview no-op navigation methods preserve exact coordinates", () =>
        {
            var axis = new LogarithmicNumericAxis { VisibleRange = new(2.731, 73.125), AutoRange = AutoRange.Always };
            using var n = new AxisRangeNavigator(axis, new(1, 1000)); var before = axis.VisibleRange;
            n.Pan(0); n.Zoom(1); n.SetStartFraction(n.Selection.Minimum);
            TestSuite.Equal(axis.VisibleRange, before); TestSuite.Equal(axis.AutoRange, AutoRange.Always); TestSuite.True(!n.History.CanUndo);
        });
        suite.Run("overview extent change inside notification safely supersedes recenter", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100));
            axis.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AxisModel.VisibleRange)) n.FullRange = new(0, 200); };
            n.Begin(Point(n, .9), Track); var external = axis.VisibleRange;
            n.End(Point(n, .9)); n.Cancel();
            TestSuite.Equal(axis.VisibleRange, external); TestSuite.True(!n.HasGesture && !n.History.CanUndo);
        });
        suite.Run("overview validation rejects invalid ranges and gestures", () =>
        {
            var axis = Axis(); using var n = new AxisRangeNavigator(axis, new(0, 100));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => n.FullRange = default);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => n.Zoom(0));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => n.MinimumSelectionFraction = 0);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => n.Pan(double.NaN));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => n.Layout(default));
            TestSuite.True(!n.Begin(new(double.NaN, 30), Track));
            TestSuite.True(!n.Begin(new(-1, -1), Track));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => new AxisRangeNavigator(new LogarithmicNumericAxis(), new(0, 1)));
        });
    }
}
