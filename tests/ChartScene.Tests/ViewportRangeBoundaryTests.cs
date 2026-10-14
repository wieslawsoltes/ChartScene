using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Interaction;

public static class ViewportRangeBoundaryTests
{
    public static void Register(TestSuite suite)
    {
        suite.Run("range draft invariant parsing ignores the host decimal-comma culture", () =>
        {
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
                using var model = Model(); using var editor = new ViewportRangeEditor(model);
                editor.X.MinimumText = "1.25"; TestSuite.Equal(editor.Apply(new()), RangeApplyResult.Applied);
                TestSuite.Near(model.XAxis.VisibleRange.Min, 1.25);
                editor.X.MinimumText = "1,5"; TestSuite.Equal(editor.Apply(new()), RangeApplyResult.Invalid);
                TestSuite.Near(model.XAxis.VisibleRange.Min, 1.25);
            }
            finally { CultureInfo.CurrentCulture = culture; }
        });
        suite.Run("range draft model decoration does not conflict with numerical input", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model);
            editor.X.MinimumText = "1"; model.XAxis.FlippedCoordinates = true;
            model.XAxis.AxisAlignment = AxisAlignment.Left; model.XAxis.AxisTitle = "Renamed";
            model.Theme = ChartTheme.Light; editor.Refresh();
            TestSuite.True(!editor.HasConflict && editor.CanApply); TestSuite.Equal(editor.Apply(new()), RangeApplyResult.Applied);
            TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(1, 10));
        });
        suite.Run("range draft log-base change conflicts even with unchanged endpoints", () =>
        {
            using var model = Model(); model.XAxis.VisibleRange = new(1, 10); model.XAxis.Scale = AxisScale.Logarithmic;
            using var editor = new ViewportRangeEditor(model); editor.X.MinimumText = "2"; model.XAxis.LogarithmicBase = 2;
            TestSuite.Equal(editor.Apply(new()), RangeApplyResult.Conflict); TestSuite.Equal(editor.X.MinimumText, "2");
            TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(1, 10));
        });
        suite.Run("range draft restored original input resumes the new live viewport", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model);
            editor.X.MinimumText = "1"; model.XAxis.VisibleRange = new(3, 13); editor.Refresh();
            TestSuite.True(editor.HasConflict); editor.X.MinimumText = "0"; editor.Refresh();
            TestSuite.True(!editor.HasConflict && !editor.HasDraft); TestSuite.Equal(editor.X.MinimumText, "3");
            TestSuite.Equal(editor.X.MaximumText, "13");
        });
        suite.Run("range draft successive edits replay in order on the same editor", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model); var history = new UndoManager();
            editor.X.MinimumText = "1"; editor.Apply(history); editor.Y.MaximumText = "7"; editor.Apply(history);
            history.Undo(); editor.Refresh(); TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(1, 10));
            TestSuite.Equal(model.YAxis.VisibleRange, new DoubleRange(-2, 2));
            history.Undo(); editor.Refresh(); TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(0, 10));
            history.Redo(); history.Redo(); editor.Refresh(); TestSuite.True(!editor.HasDraft && !editor.HasConflict);
            TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(1, 10)); TestSuite.Equal(model.YAxis.VisibleRange, new DoubleRange(-2, 7));
        });
        suite.Run("range draft disposal in a setter callback stops the remaining axis writes", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model); var history = new UndoManager();
            editor.X.MinimumText = "1"; editor.Y.MaximumText = "7";
            model.XAxis.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AxisModel.AutoRange)) editor.Dispose(); };
            TestSuite.Equal(editor.Apply(history), RangeApplyResult.Superseded);
            TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(0, 10)); TestSuite.Equal(model.YAxis.VisibleRange, new DoubleRange(-2, 2));
            TestSuite.Equal(model.YAxis.AutoRange, AutoRange.Once); TestSuite.True(!history.CanUndo);
        });
        suite.Run("range draft history notification cannot resurrect a disposed editor", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model); var history = new UndoManager();
            history.Changed += (_, _) => { editor.Dispose(); model.XAxis.VisibleRange = new(50, 60); };
            editor.X.MinimumText = "1"; TestSuite.Equal(editor.Apply(history), RangeApplyResult.Applied);
            TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(50, 60)); TestSuite.True(editor.IsStale);
            history.Undo(); TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(50, 60));
        });
        suite.Run("range draft unchanged apply does no allocation or history notification", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model); var history = new UndoManager();
            int changes = 0; history.Changed += (_, _) => changes++;
            Warm(editor, history); long[] windows = [Warm(editor, history), Warm(editor, history), Warm(editor, history)];
            TestSuite.True(windows.All(value => value == 0)); TestSuite.Equal(changes, 0); TestSuite.True(!history.CanUndo);
            Directory.CreateDirectory("artifacts/performance");
            File.WriteAllText("artifacts/performance/range-unchanged-apply.json", JsonSerializer.Serialize(new
            {
                queriesPerWindow = 10000, allocatedBytes = windows, historyNotifications = changes,
                scope = "Warm unchanged Apply only; excludes changed-range commands, formatting, native controls, drawing and GPU."
            }));
        });
    }
    private static ChartModel Model()
    {
        var model = new ChartModel(); model.XAxis.VisibleRange = new(0, 10); model.YAxis.VisibleRange = new(-2, 2); return model;
    }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static long Warm(ViewportRangeEditor editor, UndoManager history)
    {
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
            if (editor.Apply(history) != RangeApplyResult.Unchanged) throw new InvalidOperationException("Expected unchanged range.");
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
}
