using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;

public static class ViewportRangeEditorTests
{
    private static ChartModel Model()
    {
        var model = new ChartModel(); model.XAxis.VisibleRange = new(0, 10); model.YAxis.VisibleRange = new(-2, 2);
        return model;
    }
    public static void Register(TestSuite suite)
    {
        foreach (var range in new[] { new DoubleRange(.12345678901234567, 1.9876543210987654),
            new DoubleRange(1e200, Math.BitIncrement(1e200)), new DoubleRange(double.Epsilon, double.Epsilon * 3),
            new DoubleRange(-0d, 1), new DoubleRange(-1e-200, 1e-200) })
            suite.Run("range draft round trips exact endpoints " + range.Min.ToString("R"), () =>
            {
                using var model = Model(); model.XAxis.VisibleRange = range; using var editor = new ViewportRangeEditor(model);
                TestSuite.Equal(BitConverter.DoubleToInt64Bits(double.Parse(editor.X.MinimumText, CultureInfo.InvariantCulture)), BitConverter.DoubleToInt64Bits(range.Min));
                TestSuite.Equal(BitConverter.DoubleToInt64Bits(double.Parse(editor.X.MaximumText, CultureInfo.InvariantCulture)), BitConverter.DoubleToInt64Bits(range.Max));
                var history = new UndoManager(); long revision = model.Revision;
                TestSuite.Equal(editor.Apply(history), RangeApplyResult.Unchanged); TestSuite.True(!history.CanUndo);
                TestSuite.Equal(model.Revision, revision); TestSuite.Equal(model.XAxis.AutoRange, AutoRange.Once);
            });
        suite.Run("range draft unchanged and equivalent numeric input retain redo and policy", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model); var history = new UndoManager();
            history.Execute(new DelegateCommand("marker", () => { }, () => { })); history.Undo();
            editor.X.MinimumText = " +0.000e3 "; editor.Y.MaximumText = "2.0";
            TestSuite.True(editor.HasDraft && !editor.CanApply); TestSuite.Equal(editor.Apply(history), RangeApplyResult.Unchanged);
            editor.Revert(); TestSuite.True(history.CanRedo && !history.CanUndo && !editor.HasDraft);
            TestSuite.Equal(model.XAxis.AutoRange, AutoRange.Once);
        });
        suite.Run("range draft observes clean pan without reformatting unchanged values", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model);
            string original = editor.X.MinimumText; long version = editor.X.Version; editor.Refresh();
            TestSuite.True(ReferenceEquals(original, editor.X.MinimumText)); TestSuite.Equal(editor.X.Version, version);
            model.XAxis.VisibleRange = new(2, 12); editor.Refresh();
            TestSuite.Equal(editor.X.MinimumText, "2"); TestSuite.Equal(editor.X.MaximumText, "12"); TestSuite.True(!editor.HasDraft);
        });
        suite.Run("range draft preserves invalid partial input across render refreshes", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model); editor.X.MinimumText = "1e-";
            for (int i = 0; i < 20; i++) { model.Title = "Frame " + i; editor.Refresh(); }
            TestSuite.Equal(editor.X.MinimumText, "1e-"); TestSuite.True(editor.X.Error is not null && !editor.CanApply && !editor.HasConflict);
        });
        suite.Run("range draft external edited-axis change requires explicit revert", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model); var history = new UndoManager();
            editor.X.MinimumText = "1"; model.XAxis.VisibleRange = new(3, 13); editor.Refresh();
            TestSuite.Equal(editor.X.MinimumText, "1"); TestSuite.True(editor.HasConflict && !editor.CanApply);
            TestSuite.Equal(editor.Apply(history), RangeApplyResult.Conflict); TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(3, 13));
            editor.Revert(); TestSuite.Equal(editor.X.MinimumText, "3"); TestSuite.True(!editor.HasConflict && !editor.HasDraft && !history.CanUndo);
        });
        suite.Run("range draft untouched live X does not block a Y-only edit or undo", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model); var history = new UndoManager();
            editor.Y.MaximumText = "5"; model.XAxis.VisibleRange = new(1, 11); editor.Refresh();
            TestSuite.True(editor.CanApply); TestSuite.Equal(editor.Apply(history), RangeApplyResult.Applied);
            TestSuite.Equal(model.XAxis.AutoRange, AutoRange.Once); TestSuite.Equal(model.YAxis.AutoRange, AutoRange.Never);
            model.XAxis.VisibleRange = new(3, 13); history.Undo();
            TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(3, 13)); TestSuite.Equal(model.YAxis.VisibleRange, new DoubleRange(-2, 2));
            TestSuite.Equal(model.YAxis.AutoRange, AutoRange.Once);
        });
        foreach (string text in new[] { "", "NaN", "Infinity", "-Infinity", "1e999", "1,5", "1,000", "letters", "1e-" })
            suite.Run("range draft rejects invalid invariant input " + JsonSerializer.Serialize(text), () =>
            {
                using var model = Model(); using var editor = new ViewportRangeEditor(model); var history = new UndoManager();
                long revision = model.Revision; editor.X.MinimumText = text; editor.Y.MaximumText = "9";
                TestSuite.Equal(editor.Apply(history), RangeApplyResult.Invalid); TestSuite.Equal(model.Revision, revision); TestSuite.True(!history.CanUndo);
                TestSuite.Equal(model.YAxis.VisibleRange, new DoubleRange(-2, 2));
            });
        foreach (var pair in new[] { ("10", "10"), ("11", "10"), ("-1e308", "1e308") })
            suite.Run("range draft rejects invalid complete interval " + pair, () =>
            {
                using var model = Model(); using var editor = new ViewportRangeEditor(model);
                editor.X.MinimumText = pair.Item1; editor.X.MaximumText = pair.Item2;
                TestSuite.True(editor.X.Error is not null); TestSuite.Equal(editor.Apply(new()), RangeApplyResult.Invalid);
            });
        suite.Run("range draft validates logarithmic domain before either axis writes", () =>
        {
            using var model = Model(); model.YAxis.Scale = AxisScale.Logarithmic; using var editor = new ViewportRangeEditor(model);
            editor.X.MinimumText = "1"; editor.Y.MinimumText = "0"; long revision = model.Revision;
            TestSuite.Equal(editor.Apply(new()), RangeApplyResult.Invalid); TestSuite.Equal(model.Revision, revision);
        });
        suite.Run("range draft bounded input fails without mutating draft", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => editor.X.MinimumText = new('1', 129));
            TestSuite.Equal(editor.X.MinimumText, "0"); TestSuite.True(!editor.HasDraft);
        });
        foreach (bool policy in new[] { false, true })
            suite.Run("range draft policy or scale changes supersede input " + policy, () =>
            {
                using var model = Model(); using var editor = new ViewportRangeEditor(model); editor.X.MinimumText = "1";
                if (policy) model.XAxis.AutoRange = AutoRange.Always; else model.XAxis.Scale = AxisScale.Logarithmic;
                TestSuite.Equal(editor.Apply(new()), RangeApplyResult.Conflict); TestSuite.Equal(editor.X.MinimumText, "1");
            });
        suite.Run("range draft combined edit restores both original automatic policies", () =>
        {
            using var model = Model(); model.XAxis.AutoRange = AutoRange.Always; using var editor = new ViewportRangeEditor(model); var history = new UndoManager();
            int updates = 0; model.Changed += (_, _) => updates++;
            editor.X.MinimumText = "1"; editor.Y.MaximumText = "5"; TestSuite.Equal(editor.Apply(history), RangeApplyResult.Applied);
            TestSuite.Equal(updates, 1); TestSuite.True(!editor.HasDraft); history.Undo();
            TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(0, 10)); TestSuite.Equal(model.YAxis.VisibleRange, new DoubleRange(-2, 2));
            TestSuite.Equal(model.XAxis.AutoRange, AutoRange.Always); TestSuite.Equal(model.YAxis.AutoRange, AutoRange.Once);
            TestSuite.True(!history.CanUndo && history.CanRedo); history.Redo();
            TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(1, 10)); TestSuite.Equal(model.YAxis.VisibleRange, new DoubleRange(-2, 5));
        });
        suite.Run("range draft stale undo cannot overwrite a newer participating range", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model); var history = new UndoManager();
            editor.X.MinimumText = "1"; editor.Y.MaximumText = "5"; editor.Apply(history);
            model.YAxis.VisibleRange = new(20, 30); history.Undo();
            TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(1, 10)); TestSuite.Equal(model.YAxis.VisibleRange, new DoubleRange(20, 30));
        });
        suite.Run("range draft replaced primary axes cannot receive stale writes", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model); var old = model.XAxis;
            editor.X.MinimumText = "1"; model.XAxes[0] = new NumericAxis { Id = "X" };
            TestSuite.Equal(editor.Apply(new()), RangeApplyResult.Conflict); TestSuite.Equal(old.VisibleRange, new DoubleRange(0, 10));
            TestSuite.Throws<InvalidOperationException>(() => editor.Revert());
        });
        suite.Run("range draft disposal releases write authority including saved commands", () =>
        {
            using var model = Model(); var editor = new ViewportRangeEditor(model); var history = new UndoManager();
            editor.X.MinimumText = "1"; editor.Apply(history); editor.Dispose(); editor.Dispose(); history.Undo();
            TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(1, 10)); TestSuite.Throws<ObjectDisposedException>(() => editor.Apply(history));
        });
        foreach (bool fromBatch in new[] { false, true })
            suite.Run("range draft callback edits supersede and roll back only owned fields " + fromBatch, () =>
            {
                using var model = Model(); using var editor = new ViewportRangeEditor(model); var history = new UndoManager();
                editor.X.MinimumText = "1"; editor.Y.MaximumText = "5"; bool fired = false;
                void Change() { if (fired) return; fired = true; model.YAxis.VisibleRange = new(20, 30); }
                if (fromBatch) model.Changed += (_, _) => Change();
                else model.XAxis.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AxisModel.VisibleRange)) Change(); };
                TestSuite.Equal(editor.Apply(history), RangeApplyResult.Superseded); TestSuite.True(!history.CanUndo);
                TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(0, 10)); TestSuite.Equal(model.YAxis.VisibleRange, new DoubleRange(20, 30));
                TestSuite.Equal(model.XAxis.AutoRange, AutoRange.Once); TestSuite.Equal(model.YAxis.AutoRange, AutoRange.Once);
            });
        suite.Run("range draft conflicting policy callback is not overwritten by range assignment", () =>
        {
            using var model = Model(); using var editor = new ViewportRangeEditor(model); editor.X.MinimumText = "1"; bool fired = false;
            model.XAxis.PropertyChanged += (_, e) => { if (!fired && e.PropertyName == nameof(AxisModel.AutoRange)) { fired = true; model.XAxis.AutoRange = AutoRange.Always; } };
            TestSuite.Equal(editor.Apply(new()), RangeApplyResult.Superseded); TestSuite.Equal(model.XAxis.AutoRange, AutoRange.Always);
            TestSuite.Equal(model.XAxis.VisibleRange, new DoubleRange(0, 10));
        });
        suite.Run("range draft unchanged refresh has no source reads notifications or allocations", () =>
        {
            using var model = Model(); var source = new CountingData(); source.Append(0, 1); model.RenderableSeries.Add(new FastLineRenderableSeries { DataSeries = source });
            using var editor = new ViewportRangeEditor(model); editor.X.MinimumText = "1e-"; int changes = 0; model.Changed += (_, _) => changes++;
            Warm(editor); long[] windows = [Warm(editor), Warm(editor), Warm(editor)];
            TestSuite.True(windows.All(v => v == 0)); TestSuite.Equal(source.Reads, 0); TestSuite.Equal(changes, 0);
            Directory.CreateDirectory("artifacts/performance"); File.WriteAllText("artifacts/performance/range-drafts.json",
                JsonSerializer.Serialize(new { refreshesPerWindow = 10000, allocatedBytes = windows, snapshotReads = source.Reads,
                    scope = "Warm unchanged range draft refresh only, including an invalid draft; excludes formatting changed ranges, input, UI, drawing and GPU." }));
        });
    }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static long Warm(ViewportRangeEditor editor)
    {
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) editor.Refresh();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
    private sealed class CountingData : XyDataSeries, IDataSeries
    {
        public int Reads;
        SeriesSnapshot IDataSeries.Snapshot() { Reads++; return Snapshot(); }
    }
}
