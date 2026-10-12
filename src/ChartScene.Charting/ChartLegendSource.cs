using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using ChartScene.Core;

namespace ChartScene.Charting;

public enum LegendSourceMode { All, Visible, Selected }

/// <summary>Stable, bindable legend data. A retained, detached entry cannot edit its former series.</summary>
public sealed class ChartLegendEntry : INotifyPropertyChanged
{
    public RenderableSeries Series { get; }
    public long Identity { get; }
    public bool IsAttached { get; private set; } = true;
    public string SeriesName => Series.SeriesName;
    public bool IsVisible
    {
        get => Series.IsVisible;
        set
        {
            if (!IsAttached) throw new InvalidOperationException("This legend entry is detached.");
            Series.IsVisible = value;
        }
    }
    public bool IsSelected => Series.IsSelected;
    public ColorRgba Stroke => Series.Stroke;
    public ColorRgba Fill => Series.Fill;
    public double Opacity => Series.Opacity;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal ChartLegendEntry(RenderableSeries series, long identity) { Series = series; Identity = identity; }
    internal void Forward(PropertyChangedEventArgs args) => PropertyChanged?.Invoke(this, args);
    internal void Detach()
    {
        IsAttached = false;
        Forward(new(nameof(IsAttached)));
    }
}

/// <summary>
/// UI-independent legend projection. Membership edits preserve surviving entry identities and
/// publish granular collection changes. Source data notifications do not rebuild or scan entries.
/// Change chart membership, styling and filters on their owner thread; data-only notifications
/// may arrive from worker threads. This object never owns/disposes the chart or its series.
/// </summary>
public sealed class ChartLegendSource : ObservableObject, IDisposable
{
    private readonly ObservableCollection<ChartLegendEntry> _entries = [];
    private readonly Dictionary<RenderableSeries, ChartLegendEntry> _observed = new(ReferenceEqualityComparer.Instance);
    private ChartModel? _model;
    private LegendSourceMode _mode;
    private bool _updating, _pending, _disposed;
    public ReadOnlyObservableCollection<ChartLegendEntry> Entries { get; }
    public long EntryCreationCount { get; private set; }
    public long MembershipPassCount { get; private set; }
    public ChartTheme Theme => _model?.Theme ?? ChartTheme.Dark;
    public ChartModel? Model
    {
        get => _model;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_model, value)) return;
            UnsubscribeModel(); _model = value;
            if (value is not null)
            {
                value.RenderableSeries.CollectionChanged += CollectionChanged;
                value.PropertyChanged += ModelChanged;
            }
            Reconcile(); RaisePropertyChanged(); RaisePropertyChanged(nameof(Theme));
        }
    }
    public LegendSourceMode SourceMode
    {
        get => _mode;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_mode == value) return;
            _mode = value; Reconcile(); RaisePropertyChanged();
        }
    }
    public ChartLegendSource() => Entries = new(_entries);
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => Reconcile();
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(ChartModel.Theme)) RaisePropertyChanged(nameof(Theme));
    }
    private void SeriesChanged(object? sender, PropertyChangedEventArgs args)
    {
        // In particular, Content/DataSeries/HeatmapGrid notifications do not touch the
        // dictionary or UI. Continuous source appends may be raised on a worker thread.
        string? name = args.PropertyName;
        if (!string.IsNullOrEmpty(name) && name is not (nameof(RenderableSeries.SeriesName) or
            nameof(RenderableSeries.IsVisible) or nameof(RenderableSeries.IsSelected) or
            nameof(RenderableSeries.IncludeInLegend) or nameof(RenderableSeries.Stroke) or
            nameof(RenderableSeries.Fill) or nameof(RenderableSeries.ColorMap) or nameof(RenderableSeries.Opacity) or
            nameof(RenderableSeries.PointMarker) or nameof(RenderableSeries.MarkerSize) or
            nameof(RenderableSeries.StrokeThickness))) return;
        if (_disposed || sender is not RenderableSeries series || !_observed.TryGetValue(series, out var entry)) return;
        if (string.IsNullOrEmpty(name) || name == nameof(RenderableSeries.IncludeInLegend) ||
            name == nameof(RenderableSeries.IsVisible) && _mode == LegendSourceMode.Visible ||
            name == nameof(RenderableSeries.IsSelected) && _mode == LegendSourceMode.Selected) Reconcile();
        // A collection subscriber can detach the source during reconciliation.
        if (!_disposed && entry.IsAttached) entry.Forward(args);
    }
    private bool Includes(RenderableSeries series) => series.IncludeInLegend && (_mode switch
    {
        LegendSourceMode.Visible => series.IsVisible,
        LegendSourceMode.Selected => series.IsSelected,
        _ => true
    });
    private void Reconcile()
    {
        _pending = true;
        if (_updating) return;
        _updating = true;
        try { while (_pending) { _pending = false; ReconcileCore(); } }
        finally { _updating = false; }
    }
    private void ReconcileCore()
    {
        MembershipPassCount++;
        // Snapshot before invoking consumers. Reentrant source changes request a fresh
        // pass instead of mutating an ObservableCollection from its own notification.
        var series = _model?.RenderableSeries.Distinct<RenderableSeries>(ReferenceEqualityComparer.Instance).ToArray() ?? [];
        var retained = new HashSet<RenderableSeries>(series, ReferenceEqualityComparer.Instance);
        foreach (var pair in _observed.ToArray())
        {
            if (retained.Contains(pair.Key)) continue;
            _observed.Remove(pair.Key); pair.Key.PropertyChanged -= SeriesChanged; pair.Value.Detach();
            if (_pending) return;
        }
        foreach (var item in series)
        {
            if (_observed.ContainsKey(item)) continue;
            _observed.Add(item, new(item, ++EntryCreationCount)); item.PropertyChanged += SeriesChanged;
        }
        var desired = series.Where(Includes).Select(item => _observed[item]).ToArray();
        // Avoid repeated linear misses when a large set of new series is first bound.
        var existingEntries = new HashSet<ChartLegendEntry>(_entries);
        for (int i = 0; i < desired.Length; i++)
        {
            if (i < _entries.Count && ReferenceEquals(_entries[i], desired[i])) continue;
            int existing = existingEntries.Contains(desired[i]) ? _entries.IndexOf(desired[i]) : -1;
            if (existing >= 0) _entries.Move(existing, i); else _entries.Insert(i, desired[i]);
            if (_pending) return;
        }
        while (_entries.Count > desired.Length)
        {
            _entries.RemoveAt(_entries.Count - 1);
            if (_pending) return;
        }
    }
    private void UnsubscribeModel()
    {
        if (_model is null) return;
        _model.RenderableSeries.CollectionChanged -= CollectionChanged;
        _model.PropertyChanged -= ModelChanged;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; UnsubscribeModel(); _model = null; Reconcile();
    }
}
