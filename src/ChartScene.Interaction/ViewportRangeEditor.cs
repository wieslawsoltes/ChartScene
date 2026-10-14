using System.Globalization;
using ChartScene.Charting;
using ChartScene.Core;

namespace ChartScene.Interaction;

/// <summary>A UI-thread numeric range draft. Refresh observes live axes without rewriting pending input.</summary>
public sealed class AxisRangeDraft
{
    public const int MaximumInputLength = 128;
    internal AxisModel Axis { get; }
    internal AxisRangeState Baseline { get; private set; }
    private string _minimum = "", _maximum = "", _originalMinimum = "", _originalMaximum = "";
    private DoubleRange? _parsed;
    public string MinimumText { get => _minimum; set => SetText(value, true); }
    public string MaximumText { get => _maximum; set => SetText(value, false); }
    public bool IsDirty => _minimum != _originalMinimum || _maximum != _originalMaximum;
    public bool HasConflict { get; private set; }
    public string? Error { get; private set; }
    public bool HasChanges => _parsed is { } value && value != Baseline.Range;
    public long Version { get; private set; }
    internal DoubleRange Parsed => _parsed!.Value;
    public AxisRangeDraft(AxisModel axis) { ArgumentNullException.ThrowIfNull(axis); Axis = axis; Revert(); }
    private void SetText(string? value, bool minimum)
    {
        value ??= "";
        if (value.Length > MaximumInputLength) throw new ArgumentOutOfRangeException(nameof(value));
        if ((minimum ? _minimum : _maximum) == value) return;
        if (minimum) _minimum = value; else _maximum = value;
        Validate(); Version++;
    }
    public void Refresh()
    {
        var current = AxisRangeState.Read(Axis);
        if (!IsDirty)
        {
            if (current != Baseline) Revert();
            else if (HasConflict) { HasConflict = false; Version++; }
            return;
        }
        bool conflict = current != Baseline;
        if (HasConflict != conflict) { HasConflict = conflict; Version++; }
    }
    public void Revert()
    {
        Baseline = AxisRangeState.Read(Axis);
        // Round-trip formatting avoids a no-input Apply changing adjacent or high-precision doubles.
        _minimum = _originalMinimum = Baseline.Range.Min.ToString("R", CultureInfo.InvariantCulture);
        _maximum = _originalMaximum = Baseline.Range.Max.ToString("R", CultureInfo.InvariantCulture);
        _parsed = Baseline.Range; Error = null; HasConflict = false; Version++;
    }
    private void Validate()
    {
        _parsed = null;
        // No thousands separators: accepting "1,5" as fifteen would silently change user intent.
        if (!double.TryParse(_minimum, NumberStyles.Float, CultureInfo.InvariantCulture, out double min) ||
            !double.TryParse(_maximum, NumberStyles.Float, CultureInfo.InvariantCulture, out double max) ||
            !double.IsFinite(min) || !double.IsFinite(max))
            Error = "Enter finite numbers using a decimal point or exponent.";
        else if (min >= max || !double.IsFinite(max - min)) Error = "Minimum must be below maximum, with a finite span.";
        else if (Baseline.Scale == AxisScale.Logarithmic && min <= 0) Error = "Logarithmic ranges must be positive.";
        else { _parsed = new(min, max); Error = null; }
    }
}

internal readonly record struct AxisRangeState(DoubleRange Range, AutoRange Policy, AxisScale Scale, double LogBase)
{
    internal static AxisRangeState Read(AxisModel axis) => new(axis.VisibleRange, axis.AutoRange, axis.Scale, axis.LogarithmicBase);
}
public enum RangeApplyResult { Applied, Unchanged, Invalid, Conflict, Superseded }

/// <summary>
/// Edits the primary X/Y numerical ranges. The host calls Refresh on its UI thread.
/// Pending input belongs to its captured axis/range, not subsequent pan, zoom or replacement.
/// Does not subscribe to data or read series snapshots. Dispose when replacing the model.
/// </summary>
public sealed class ViewportRangeEditor : IDisposable
{
    private readonly ChartModel _model;
    private bool _disposed;
    public AxisRangeDraft X { get; }
    public AxisRangeDraft Y { get; }
    public bool IsStale => _disposed || _model.XAxes.Count == 0 || _model.YAxes.Count == 0 ||
        !ReferenceEquals(_model.XAxis, X.Axis) || !ReferenceEquals(_model.YAxis, Y.Axis);
    public bool HasDraft => X.IsDirty || Y.IsDirty;
    public bool HasConflict => IsStale || X.HasConflict || Y.HasConflict;
    public bool CanApply => !HasConflict && X.Error is null && Y.Error is null && (X.HasChanges || Y.HasChanges);
    public ViewportRangeEditor(ChartModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.XAxes.Count == 0 || model.YAxes.Count == 0) throw new ArgumentException("Primary axes are required.", nameof(model));
        _model = model; X = new(model.XAxis); Y = new(model.YAxis);
    }
    public void Refresh() { if (!IsStale) { X.Refresh(); Y.Refresh(); } }
    public void Revert()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsStale) throw new InvalidOperationException("Create a new editor for replaced primary axes.");
        X.Revert(); Y.Revert();
    }
    public RangeApplyResult Apply(UndoManager history)
    {
        ArgumentNullException.ThrowIfNull(history); ObjectDisposedException.ThrowIf(_disposed, this);
        Refresh();
        if (HasConflict) return RangeApplyResult.Conflict;
        if (X.Error is not null || Y.Error is not null) return RangeApplyResult.Invalid;
        if (!X.HasChanges && !Y.HasChanges) return RangeApplyResult.Unchanged;
        var changes = new List<RangeChange>(2);
        AddChange(changes, X); AddChange(changes, Y);
        var command = new RangeCommand(this, changes.ToArray());
        if (!command.Move(false)) { Refresh(); return RangeApplyResult.Superseded; }
        history.RecordExecuted(command); if (!IsStale) Revert(); return RangeApplyResult.Applied;
    }
    private static void AddChange(List<RangeChange> changes, AxisRangeDraft draft)
    {
        if (draft.HasChanges) changes.Add(new(draft.Axis, draft.Baseline,
            draft.Baseline with { Range = draft.Parsed, Policy = AutoRange.Never }));
    }
    public void Dispose() => _disposed = true;

    private sealed record RangeChange(AxisModel Axis, AxisRangeState Before, AxisRangeState After);
    private readonly record struct RangeWrite(AxisModel Axis, bool IsPolicy, AxisRangeState Before, AxisRangeState After);
    private sealed class RangeCommand(ViewportRangeEditor owner, RangeChange[] changes) : IUndoableCommand
    {
        public string Description => "Set visible ranges";
        public void Execute() => Move(false);
        public void Undo() => Move(true);
        internal bool Move(bool undo)
        {
            if (owner.IsStale) return false;
            var expected = new AxisRangeState[changes.Length];
            for (int i = 0; i < changes.Length; i++) expected[i] = undo ? changes[i].After : changes[i].Before;
            if (!Matches()) return false; // Validate every participating axis before the first write.
            var writes = new List<RangeWrite>(changes.Length * 2);
            bool completed = true;
            try
            {
                using (owner._model.SuspendUpdates())
                {
                    for (int i = 0; completed && i < changes.Length; i++)
                    {
                        var target = undo ? changes[i].Before : changes[i].After;
                        var axis = changes[i].Axis;
                        if (expected[i].Policy != target.Policy)
                        {
                            var next = expected[i] with { Policy = target.Policy };
                            writes.Add(new(axis, true, expected[i], next)); expected[i] = next;
                            axis.AutoRange = next.Policy; completed = Matches();
                        }
                        if (completed && expected[i].Range != target.Range)
                        {
                            var next = expected[i] with { Range = target.Range };
                            writes.Add(new(axis, false, expected[i], next)); expected[i] = next;
                            axis.VisibleRange = next.Range; completed = Matches();
                        }
                    }
                }
                // Model.Changed runs when the batch is released; it too can supersede this operation.
                completed &= Matches();
            }
            catch { RollBack(); throw; }
            if (!completed) RollBack();
            return completed;
            bool Matches()
            {
                if (owner.IsStale) return false;
                for (int i = 0; i < changes.Length; i++)
                    if (AxisRangeState.Read(changes[i].Axis) != expected[i]) return false;
                return true;
            }
            void RollBack()
            {
                // Roll back ONLY still-owned fields. Never replace a newer value from a callback.
                // This is not general transaction isolation or an ABA/document-epoch protocol.
                for (int i = writes.Count - 1; i >= 0; i--)
                {
                    if (owner.IsStale) return;
                    var write = writes[i]; var axis = write.Axis;
                    if (write.IsPolicy)
                    {
                        if (axis.AutoRange == write.After.Policy) axis.AutoRange = write.Before.Policy;
                    }
                    else if (axis.VisibleRange == write.After.Range &&
                        (axis.Scale != AxisScale.Logarithmic || write.Before.Range.Min > 0)) axis.VisibleRange = write.Before.Range;
                }
            }
        }
    }
}
