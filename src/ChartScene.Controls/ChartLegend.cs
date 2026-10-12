using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Interaction;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace ChartScene.Controls;

/// <summary>
/// Bindable external legend with native visibility controls, vector markers and optional XAML
/// row/marker templates. ParentSurface supplies its current model and undo history; Model and
/// History can also be bound independently. Unload detaches subscriptions without owning data.
/// Rows are retained across metadata edits and moves. Large legends can be hosted in a ScrollViewer;
/// this control does not virtualize its children.
/// </summary>
public sealed class ChartLegend : UserControl, IDisposable
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(ChartModel), typeof(ChartLegend), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ParentSurfaceProperty = DependencyProperty.Register(nameof(ParentSurface), typeof(ChartSurface), typeof(ChartLegend), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(ChartLegend), new PropertyMetadata(Orientation.Vertical, Changed));
    public static readonly DependencyProperty SourceModeProperty = DependencyProperty.Register(nameof(SourceMode), typeof(LegendSourceMode), typeof(ChartLegend), new PropertyMetadata(LegendSourceMode.All, Changed));
    public static readonly DependencyProperty ShowVisibilityCheckboxesProperty = DependencyProperty.Register(nameof(ShowVisibilityCheckboxes), typeof(bool), typeof(ChartLegend), new PropertyMetadata(true, Changed));
    public static readonly DependencyProperty ShowSeriesMarkersProperty = DependencyProperty.Register(nameof(ShowSeriesMarkers), typeof(bool), typeof(ChartLegend), new PropertyMetadata(true, Changed));
    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(nameof(ItemTemplate), typeof(DataTemplate), typeof(ChartLegend), new PropertyMetadata(null, TemplateChanged));
    public static readonly DependencyProperty MarkerTemplateProperty = DependencyProperty.Register(nameof(MarkerTemplate), typeof(DataTemplate), typeof(ChartLegend), new PropertyMetadata(null, TemplateChanged));
    public ChartModel? Model { get => (ChartModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public ChartSurface? ParentSurface { get => (ChartSurface?)GetValue(ParentSurfaceProperty); set => SetValue(ParentSurfaceProperty, value); }
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public LegendSourceMode SourceMode { get => (LegendSourceMode)GetValue(SourceModeProperty); set => SetValue(SourceModeProperty, value); }
    public bool ShowVisibilityCheckboxes { get => (bool)GetValue(ShowVisibilityCheckboxesProperty); set => SetValue(ShowVisibilityCheckboxesProperty, value); }
    public bool ShowSeriesMarkers { get => (bool)GetValue(ShowSeriesMarkersProperty); set => SetValue(ShowSeriesMarkersProperty, value); }
    /// <summary>Receives ChartLegendEntry as its content/data context. Custom controls own their binding/command policy.</summary>
    public DataTemplate? ItemTemplate { get => (DataTemplate?)GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }
    /// <summary>Receives the RenderableSeries as its content/data context.</summary>
    public DataTemplate? MarkerTemplate { get => (DataTemplate?)GetValue(MarkerTemplateProperty); set => SetValue(MarkerTemplateProperty, value); }
    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(nameof(History), typeof(UndoManager), typeof(ChartLegend), new PropertyMetadata(null));
    public UndoManager? History { get => (UndoManager?)GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public ReadOnlyObservableCollection<ChartLegendEntry> Entries => _source.Entries;
    public long RowCreationCount { get; private set; }
    /// <summary>Completed native row synchronization passes, not individual collection events.</summary>
    public long RowSynchronizationCount { get; private set; }
    /// <summary>Explicit vector-marker invalidations; excludes host composition and initial loading.</summary>
    public long MarkerInvalidationCount { get; private set; }
    public int RetainedRowCount => _rows.Count;
    public long EntryCreationCount => _source.EntryCreationCount;
    public long MembershipPassCount => _source.MembershipPassCount;
    public FrameworkElement? ContainerFromEntry(ChartLegendEntry entry) => _rows.GetValueOrDefault(entry);
    public event EventHandler? Updated;

    private readonly ChartLegendSource _source = new();
    private readonly StackPanel _items = new() { Spacing = 2 };
    private readonly Dictionary<ChartLegendEntry, Row> _rows = [];
    private ChartSurface? _parent;
    private long _parentToken;
    private bool _attached, _disposed, _syncing, _pending, _refreshingRows;
    public ChartLegend()
    {
        Content = _items; FontSize = 12;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        // Entries still expose granular events to external ItemsControls. Our native
        // tree only needs one update after the source has completed the whole projection.
        _source.Refreshed += EntriesRefreshed;
        _source.PropertyChanged += SourceChanged;
        Loaded += (_, _) => { if (_disposed) return; _attached = true; Refresh(); };
        Unloaded += (_, _) => { _attached = false; Detach(); };
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => RefreshRows());
        RegisterPropertyChangedCallback(FontFamilyProperty, (_, _) => RefreshRows());
        RegisterPropertyChangedCallback(FontWeightProperty, (_, _) => RefreshRows());
        RegisterPropertyChangedCallback(FontStyleProperty, (_, _) => RefreshRows());
        RegisterPropertyChangedCallback(FontStretchProperty, (_, _) => RefreshRows());
        RegisterPropertyChangedCallback(CharacterSpacingProperty, (_, _) => RefreshRows());
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => RefreshRows());
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((ChartLegend)sender).Refresh();
    private static void TemplateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var legend = (ChartLegend)sender; legend.ClearRows(); legend.Refresh();
    }
    private void Refresh()
    {
        if (_disposed || !_attached) return;
        if (!ReferenceEquals(_parent, ParentSurface))
        {
            UnsubscribeParent(); _parent = ParentSurface;
            if (_parent is not null) _parentToken = _parent.RegisterPropertyChangedCallback(ChartSurface.ModelProperty, (_, _) => Refresh());
        }
        _items.Orientation = Orientation;
        _items.Spacing = Orientation == Orientation.Horizontal ? 14 : 2;
        _source.SourceMode = SourceMode; _source.Model = Model ?? _parent?.Model;
        SynchronizeRows(); RefreshRows();
    }
    private void EntriesRefreshed(object? sender, EventArgs args) => SynchronizeRows();
    private void SourceChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(ChartLegendSource.Theme)) RefreshRows(); }
    private void SynchronizeRows()
    {
        _pending = true;
        if (_syncing || _source.IsRefreshing) return;
        _syncing = true;
        try
        {
            while (_pending)
            {
                _pending = false; RowSynchronizationCount++;
                var desired = _disposed || !_attached ? [] : Entries.Where(e => e.IsAttached).ToArray();
                var retained = new HashSet<ChartLegendEntry>(desired);
                foreach (var pair in _rows.ToArray())
                    if (!retained.Contains(pair.Key))
                    {
                        _items.Children.Remove(pair.Value);
                        // A filter only hides an observed entry. Keep its native controls
                        // for the next filter change; actual removal/model replacement detaches it.
                        if (!pair.Key.IsAttached) { _rows.Remove(pair.Key); pair.Value.Detach(); }
                    }
                var existingRows = new HashSet<UIElement>(_items.Children);
                for (int i = 0; i < desired.Length; i++)
                {
                    var entry = desired[i];
                    if (!_rows.TryGetValue(entry, out var row))
                    {
                        _rows.Add(entry, row = new(this, entry)); RowCreationCount++;
                    }
                    if (i < _items.Children.Count && ReferenceEquals(_items.Children[i], row)) continue;
                    int existing = existingRows.Contains(row) ? _items.Children.IndexOf(row) : -1;
                    if (existing >= 0) _items.Children.RemoveAt(existing);
                    _items.Children.Insert(i, row);
                }
            }
        }
        finally { _syncing = false; }
        Updated?.Invoke(this, EventArgs.Empty);
    }
    private void RefreshRows()
    {
        if (_disposed || _refreshingRows) return;
        _refreshingRows = true;
        try
        {
            var background = _source.Theme.Background;
            RequestedTheme = background.R * 299 + background.G * 587 + background.B * 114 >= 128000
                ? ElementTheme.Light : ElementTheme.Dark;
            foreach (var row in _rows.Values) row.Refresh();
        }
        finally { _refreshingRows = false; }
        Updated?.Invoke(this, EventArgs.Empty);
    }
    /// <summary>Applies a user edit through shared history. Detached or filtered-out entries are ignored.</summary>
    public bool SetVisibility(ChartLegendEntry entry, bool visible)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_disposed || !_attached || !entry.IsAttached || !Entries.Contains(entry) || entry.IsVisible == visible) return false;
        var series = entry.Series; bool before = series.IsVisible;
        var history = History ?? (ReferenceEquals(_source.Model, _parent?.Model) ? _parent?.Controller.History : null);
        if (history is null) series.IsVisible = visible;
        else history.Execute(new DelegateCommand("Legend visibility", () => series.IsVisible = visible, () => series.IsVisible = before));
        return true;
    }
    private static SolidColorBrush Brush(ColorRgba color) => new(Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B));
    private sealed class Marker(RenderableSeries series) : SKCanvasElement
    {
        private SkiaResources? _resources;
        public void Redraw() => Invalidate();
        public void Release() { _resources?.Dispose(); _resources = null; }
        protected override void RenderOverride(SKCanvas canvas, Size area)
        {
            _resources ??= new(); _resources.BeginFrame();
            LegendMarkerRenderer.Draw(new SkiaChartCanvas(canvas, _resources), series, new(0, 0, area.Width, area.Height));
        }
    }
    private readonly record struct MarkerStyle(SeriesKind Kind, ColorRgba Stroke, ColorRgba Fill,
        double Opacity, double Thickness, double Size, PointMarker Marker, ColorMap Map)
    {
        public static MarkerStyle Capture(RenderableSeries s) => new(s.Kind, s.Stroke, s.Fill,
            s.Opacity, s.StrokeThickness, s.MarkerSize, s.PointMarker, s.ColorMap);
    }
    private sealed class Row : Grid
    {
        private readonly ChartLegend _owner;
        private readonly ChartLegendEntry _entry;
        private readonly CheckBox? _check;
        private readonly TextBlock? _label;
        private readonly Border? _markerHost;
        private readonly Marker? _marker;
        private readonly ContentPresenter? _custom;
        private bool _updating, _detached;
        private MarkerStyle? _markerStyle;
        public Row(ChartLegend owner, ChartLegendEntry entry)
        {
            _owner = owner; _entry = entry; MinHeight = 28; DataContext = entry;
            if (owner.ItemTemplate is { } template)
                Children.Add(_custom = new ContentPresenter { Content = entry, ContentTemplate = template });
            else
            {
                ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                _check = new CheckBox { Width = 26, MinWidth = 0, MinHeight = 0, Height = 28, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
                _check.Checked += Toggle; _check.Unchecked += Toggle; Children.Add(_check);
                _markerHost = new Border { Width = 40, Height = 18, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                if (owner.MarkerTemplate is { } markerTemplate)
                    _markerHost.Child = new ContentPresenter { Content = entry.Series, ContentTemplate = markerTemplate };
                else
                {
                    _marker = new(entry.Series) { Width = 40, Height = 18, IsHitTestVisible = false };
                    _marker.Unloaded += (_, _) => _marker.Release(); _markerHost.Child = _marker;
                }
                Grid.SetColumn(_markerHost, 1); Children.Add(_markerHost);
                _label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                Grid.SetColumn(_label, 2); Children.Add(_label);
            }
            entry.PropertyChanged += EntryChanged; Refresh();
        }
        private void Toggle(object sender, RoutedEventArgs args)
        { if (!_updating && !_detached) _owner.SetVisibility(_entry, _check!.IsChecked == true); }
        private void EntryChanged(object? sender, PropertyChangedEventArgs args)
        {
            // A filtered row is not in Entries; removing its series may therefore emit no
            // collection event. Release that detached native row immediately as well.
            if (!_entry.IsAttached)
            {
                _owner.SynchronizeRows();
                if (_owner._source.IsRefreshing) return;
            }
            Refresh(); _owner.Updated?.Invoke(_owner, EventArgs.Empty);
        }
        public void Refresh()
        {
            if (_detached) return;
            _updating = true;
            try
            {
                var theme = _owner._source.Theme;
                RequestedTheme = theme.Background.R * 299 + theme.Background.G * 587 + theme.Background.B * 114 >= 128000 ? ElementTheme.Light : ElementTheme.Dark;
                if (_check is not null)
                {
                    _check.IsChecked = _entry.IsVisible; _check.IsEnabled = _entry.IsAttached;
                    _check.Visibility = _owner.ShowVisibilityCheckboxes ? Visibility.Visible : Visibility.Collapsed;
                    AutomationProperties.SetName(_check, "Show " + _entry.SeriesName);
                    AutomationProperties.SetAutomationId(_check, "legend-visible-" + _entry.Identity);
                }
                if (_label is not null)
                {
                    _label.Text = _entry.SeriesName; _label.FontSize = _owner.FontSize;
                    _label.FontFamily = _owner.FontFamily; _label.FontWeight = _owner.FontWeight;
                    _label.FontStyle = _owner.FontStyle; _label.FontStretch = _owner.FontStretch;
                    _label.CharacterSpacing = _owner.CharacterSpacing;
                    // Use the normal inherited/local/style brush rather than replacing
                    // consumer styling with a hard-coded chart theme color.
                    _label.Foreground = _owner.Foreground; _label.Opacity = _entry.IsVisible ? 1 : .55;
                }
                if (_markerHost is not null) _markerHost.Visibility = _owner.ShowSeriesMarkers ? Visibility.Visible : Visibility.Collapsed;
                if (_marker is not null)
                {
                    var style = MarkerStyle.Capture(_entry.Series);
                    if (_markerStyle != style)
                    {
                        _markerStyle = style; _owner.MarkerInvalidationCount++; _marker.Redraw();
                    }
                }
            }
            finally { _updating = false; }
        }
        public void Detach()
        {
            if (_detached) return; _detached = true; _entry.PropertyChanged -= EntryChanged;
            _marker?.Release(); if (_custom is not null) _custom.Content = null;
            DataContext = null;
        }
    }
    private void UnsubscribeParent()
    { if (_parent is not null) _parent.UnregisterPropertyChangedCallback(ChartSurface.ModelProperty, _parentToken); _parent = null; }
    private void ClearRows()
    {
        foreach (var row in _rows.Values) row.Detach();
        _rows.Clear(); _items.Children.Clear();
    }
    private void Detach()
    { UnsubscribeParent(); if (!_disposed) _source.Model = null; ClearRows(); }
    public new void Dispose()
    {
        if (_disposed) return; _disposed = true; _attached = false;
        UnsubscribeParent(); _source.Dispose(); ClearRows();
        _source.Refreshed -= EntriesRefreshed; _source.PropertyChanged -= SourceChanged; Updated = null;
        ClearValue(ModelProperty); ClearValue(ParentSurfaceProperty); History = null; base.Dispose();
    }
}
