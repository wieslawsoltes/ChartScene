using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Rendering;

namespace ChartScene.Interaction;

public enum DataPointEditOutcome { None, Started, Preview, Committed, Cancelled, Unchanged, Conflict, Unsupported, Rejected }

/// <summary>
/// Opt-in Y-value editing of original samples on Cartesian XY line, spline, mountain,
/// scatter, column, bar and impulse series. Insert before the pan/selection modifiers.
/// Use one instance per controller. Model/axis/input changes belong on the owner thread;
/// concurrent data changes are checked atomically and supersede the edit without rollback.
/// A single successful gesture records one guarded history entry. Continuous auto-ranging
/// of the value axis must be disabled before editing, so pointer motion has a stable scale.
/// </summary>
public sealed class DataPointEditModifier : ChartModifier, IChartCursorOwner
{
    private sealed class Epoch(long revision) { public long Revision = revision; public bool Valid = true; }
    private sealed class Session(ChartController owner, RenderableSeries series, DataSeries data, Epoch epoch,
        SeriesProjection projection, int index, double x, double y, PointD pointer, int pointerId)
    {
        public readonly ChartController Owner = owner;
        public readonly RenderableSeries Series = series;
        public readonly DataSeries Data = data;
        public readonly Epoch Epoch = epoch;
        public readonly SeriesProjection Projection = projection;
        public readonly int Index = index, PointerId = pointerId;
        public readonly double X = x, Before = y;
        public readonly PointD Pointer = pointer, Screen = projection.ToScreen(x, y);
        public double Current = y;
        public bool Invalid;
    }
    private readonly ConditionalWeakTable<DataSeries, Epoch> _epochs = new();
    private Session? _session;
    private double _hitRadius = 10;
    private long _generation;
    public double HitRadius
    {
        get => _hitRadius;
        set { if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _hitRadius = value; }
    }
    public bool IsEditing => _session is not null;
    public bool OwnsCursor => IsEditing;
    public RenderableSeries? ActiveSeries => _session?.Series;
    public int ActiveIndex => _session?.Index ?? -1;
    public double? ActiveValue => _session?.Current;
    public DataPointEditOutcome LastOutcome { get; private set; }
    public long CommittedEdits { get; private set; }
    public long Conflicts { get; private set; }
    public event EventHandler? Changed;
    public DataPointEditModifier() => IsEnabled = false;
    public static bool Supports(RenderableSeries series) => series.DataSeries is DataSeries { ChannelCount: 2 }
        && series.Kind is SeriesKind.Line or SeriesKind.DigitalLine or SeriesKind.Spline or SeriesKind.Mountain
            or SeriesKind.SplineMountain or SeriesKind.Scatter or SeriesKind.Column or SeriesKind.Bar or SeriesKind.Impulse;

    public override ModifierResult Handle(ChartController controller, ChartInput input)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (_session is { } active)
        {
            if (!ReferenceEquals(active.Owner, controller)) return ModifierResult.Unhandled;
            if (input.Kind == ChartInputKind.Cancel || !IsEnabled) { Cancel(controller); return ModifierResult.Handled; }
            // Do not let an unrelated pointer update this gesture or fall through to pan,
            // which could otherwise replace the controller's current capture.
            if (input.PointerId != active.PointerId) return ModifierResult.Handled;
            if (!Valid(active)) { Abandon(active); return ModifierResult.Handled; }
            if (input.Kind is ChartInputKind.Move or ChartInputKind.Up) Move(active, input.Position);
            if (input.Kind == ChartInputKind.Up && ReferenceEquals(_session, active)) Complete(active);
            return ModifierResult.Handled;
        }
        if (!IsEnabled || input.Kind != ChartInputKind.Down || controller.Frame is not { } frame
            || !input.Position.IsFinite || !frame.Layout.PlotArea.Contains(input.Position)) return ModifierResult.Unhandled;
        // Editing mode consumes blank plot clicks instead of unexpectedly starting a pan.
        if (controller.Model.CoordinateSystem != CoordinateSystem.Cartesian || frame.Statistics.ModelRevision != controller.Model.Revision)
        { Report(DataPointEditOutcome.Unsupported); return ModifierResult.Handled; }
        foreach (var hit in controller.HitTester.Query(controller.Model, input.Position, HitRadius, interpolate: false).OrderBy(h => h.Distance))
        {
            if (!Supports(hit.Series) || hit.Series.Opacity <= 0) continue;
            var projection = frame.Layout.Projection(controller.Model, hit.Series);
            if (projection.YAxis.AutoRange == AutoRange.Always) continue;
            var data = (DataSeries)hit.Series.DataSeries!;
            var snapshot = data.Snapshot();
            if ((uint)hit.DataIndex >= (uint)snapshot.Count) continue;
            double x = snapshot[0, hit.DataIndex], y = snapshot[1, hit.DataIndex];
            var point = projection.ToScreen(x, y);
            // A filled column/bar body is not itself an original source point.
            if (!double.IsFinite(y) || !point.IsFinite || (point - input.Position).LengthSquared > HitRadius * HitRadius) continue;
            if (data.Revision != snapshot.Revision || frame.Statistics.ModelRevision != controller.Model.Revision)
            { Report(DataPointEditOutcome.Conflict); return ModifierResult.Handled; }
            var epoch = _epochs.GetValue(data, d => new(d.Revision));
            if (!epoch.Valid || epoch.Revision != snapshot.Revision)
            { epoch.Valid = false; _epochs.Remove(data); _epochs.Add(data, epoch = new(snapshot.Revision)); }
            var session = new Session(controller, hit.Series, data, epoch, projection, hit.DataIndex, x, y, input.Position, input.PointerId);
            _session = session; _generation++;
            Watch(session, true); Overlay(session); Report(DataPointEditOutcome.Started);
            // A subscriber may cancel or replace the chart synchronously.
            if (!ReferenceEquals(_session, session)) return ModifierResult.Handled;
            if (!Valid(session)) { Abandon(session); return ModifierResult.Handled; }
            return ModifierResult.Capture;
        }
        Report(DataPointEditOutcome.Unsupported); return ModifierResult.Handled;
    }
    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    private static bool Attached(ChartModel model, RenderableSeries series, DataSeries data)
        => model.RenderableSeries.Contains(series) && ReferenceEquals(series.DataSeries, data);
    private bool Valid(Session s) => !s.Invalid && s.Epoch.Valid && s.Data.Revision == s.Epoch.Revision
        && Attached(s.Owner.Model, s.Series, s.Data) && s.Series.IsVisible
        && s.Owner.Frame?.Layout.PlotArea == s.Projection.Plot;
    private void Invalidated(object? sender, PropertyChangedEventArgs args)
    {
        if (_session is not { } s) return;
        // Own data writes also raise DataRevision. The CAS revision, rather than a broad
        // internal-writing flag, distinguishes them from external/reentrant data changes.
        if (ReferenceEquals(sender, s.Series) && args.PropertyName == "DataRevision") return;
        s.Invalid = true;
    }
    private void MembershipChanged(object? sender, NotifyCollectionChangedEventArgs args)
    { if (_session is { } s) s.Invalid = true; }
    private void Watch(Session s, bool add)
    {
        var model = s.Owner.Model;
        if (add)
        {
            s.Series.PropertyChanged += Invalidated; s.Projection.XAxis.PropertyChanged += Invalidated; s.Projection.YAxis.PropertyChanged += Invalidated;
            model.PropertyChanged += Invalidated; model.XAxes.CollectionChanged += MembershipChanged;
            model.YAxes.CollectionChanged += MembershipChanged; model.RenderableSeries.CollectionChanged += MembershipChanged;
        }
        else
        {
            s.Series.PropertyChanged -= Invalidated; s.Projection.XAxis.PropertyChanged -= Invalidated; s.Projection.YAxis.PropertyChanged -= Invalidated;
            model.PropertyChanged -= Invalidated; model.XAxes.CollectionChanged -= MembershipChanged;
            model.YAxes.CollectionChanged -= MembershipChanged; model.RenderableSeries.CollectionChanged -= MembershipChanged;
        }
    }
    private void Move(Session s, PointD pointer)
    {
        if (!pointer.IsFinite) { Report(DataPointEditOutcome.Rejected); return; }
        var axis = s.Projection.YAxis; var plot = s.Projection.Plot;
        double delta = axis.IsVertical ? pointer.Y - s.Pointer.Y : pointer.X - s.Pointer.X;
        // Preserve original bits when returning to the starting value coordinate, including
        // stationary clicks on logarithmic or adjacent-large ranges.
        double value = s.Before;
        if (delta != 0)
        {
            var screen = axis.IsVertical ? new PointD(s.Screen.X, Math.Clamp(s.Screen.Y + delta, plot.Y, plot.Bottom))
                : new PointD(Math.Clamp(s.Screen.X + delta, plot.X, plot.Right), s.Screen.Y);
            value = s.Projection.ToData(screen).Y;
        }
        if (!double.IsFinite(value) || axis.Scale == AxisScale.Logarithmic && value <= 0)
        { Report(DataPointEditOutcome.Rejected); return; }
        if (Same(value, s.Current)) return;
        bool success;
        try { success = s.Data.TryUpdateChannelValue(s.Index, 1, s.Current, value, s.Epoch.Revision, out long revision); if (success) s.Epoch.Revision = revision; }
        catch (ArgumentException) { Report(DataPointEditOutcome.Rejected); return; }
        if (!ReferenceEquals(_session, s)) return;
        if (!success || !Valid(s)) { Abandon(s); return; }
        s.Current = value; Overlay(s); Report(DataPointEditOutcome.Preview);
    }
    private void Overlay(Session s)
    {
        var point = s.Projection.ToScreen(s.X, s.Current);
        s.Owner.View.Cursor = point;
        s.Owner.View.Hits = [new SeriesHit(s.Series, s.Index, s.X, s.Current, point, 0, "Editing source point")];
    }
    private void Release(Session s)
    {
        if (!ReferenceEquals(_session, s)) return;
        _session = null; _generation++; Watch(s, false); s.Owner.View.Hits = [];
    }
    private void Abandon(Session s)
    { s.Epoch.Valid = false; Release(s); Report(DataPointEditOutcome.Conflict); }
    private void Complete(Session s)
    {
        if (!Valid(s)) { Abandon(s); return; }
        Release(s); long generation = _generation;
        if (Same(s.Before, s.Current)) { Report(DataPointEditOutcome.Unchanged); return; }
        var command = new EditCommand(s.Owner.Model, s.Series, s.Data, s.Epoch, s.Index, s.Before, s.Current, this);
        CommittedEdits++; s.Owner.History.RecordExecuted(command);
        if (_generation == generation) Report(DataPointEditOutcome.Committed);
    }
    public override void Cancel(ChartController controller)
    {
        if (_session is not { } s || !ReferenceEquals(s.Owner, controller)) return;
        if (!Valid(s)) { Abandon(s); return; }
        Release(s); long generation = _generation;
        bool success = true;
        if (!Same(s.Current, s.Before))
        {
            try
            {
                success = s.Data.TryUpdateChannelValue(s.Index, 1, s.Current, s.Before, s.Epoch.Revision, out long revision);
                if (success) s.Epoch.Revision = revision;
            }
            catch (ArgumentException) { success = false; }
        }
        if (!success || s.Data.Revision != s.Epoch.Revision) s.Epoch.Valid = false;
        if (_generation == generation) Report(s.Epoch.Valid ? DataPointEditOutcome.Cancelled : DataPointEditOutcome.Conflict);
    }
    private void Report(DataPointEditOutcome outcome)
    {
        LastOutcome = outcome; if (outcome == DataPointEditOutcome.Conflict) Conflicts++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private sealed class EditCommand(ChartModel model, RenderableSeries series, DataSeries data, Epoch epoch,
        int index, double before, double after, DataPointEditModifier reporter) : IUndoableCommand
    {
        private bool _valid = true;
        public string Description => "Edit data point";
        public void Execute() => Apply(before, after);
        public void Undo() => Apply(after, before);
        private void Apply(double expected, double value)
        {
            // Known edits share their current epoch so several gestures can be undone in
            // reverse order. Any external append/replacement/edit invalidates that epoch.
            // The value comparison additionally protects independently ordered histories.
            bool success = false; long revision = epoch.Revision;
            if (_valid && epoch.Valid && Attached(model, series, data) && data.Revision == epoch.Revision)
            {
                try { success = data.TryUpdateChannelValue(index, 1, expected, value, epoch.Revision, out revision); }
                catch (ArgumentException) { }
            }
            if (!success)
            { _valid = false; epoch.Valid = false; reporter.Report(DataPointEditOutcome.Conflict); return; }
            epoch.Revision = revision;
            if (data.Revision != revision || !Attached(model, series, data))
            { _valid = false; epoch.Valid = false; reporter.Report(DataPointEditOutcome.Conflict); }
            else reporter.Report(DataPointEditOutcome.Committed);
        }
    }
}
