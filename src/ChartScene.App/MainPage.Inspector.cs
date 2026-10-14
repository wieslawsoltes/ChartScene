using ChartScene.Controls;
using ChartScene.Interaction;
using ChartScene.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace ChartScene.App;

public sealed partial class MainPage
{
    private ViewportRangeEditor? _rangeEditor;
    private ChartToolButton _applyRangesButton = null!, _revertRangesButton = null!;
    private readonly TextBlock _rangeStatus = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _rangeUnits = new() { FontSize = 10, TextWrapping = TextWrapping.Wrap, Opacity = .7 };
    private bool _syncRanges, _compactInfoOpen, _wideInfoClosed;
    private long _rangeXVersion = -1, _rangeYVersion = -1;
    private string _publishedXMinimum = "", _publishedXMaximum = "", _publishedYMinimum = "", _publishedYMaximum = "";
    private bool CompactInfo => ActualWidth < 1150;
    private bool InfoVisible => (!_journal.HasCurrent || _journal.Current.Document != Document.Catalogue) &&
        (CompactInfo ? _compactInfoOpen : !_wideInfoClosed);

    private Grid PropertiesPane(StackPanel content)
    {
        var pane = new Grid(); pane.RowDefinitions.Add(new() { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Padding = new Thickness(16, 8, 8, 8), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "EXAMPLE PROPERTIES", FontSize = 10, Opacity = .7, VerticalAlignment = VerticalAlignment.Center });
        var close = Button("close-info", ChartGlyph.Close, "", CloseInfo, "Close example properties");
        Grid.SetColumn(close, 1); header.Children.Add(close); pane.Children.Add(Panel(header, new Thickness(0, 0, 0, 1)));
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Register("properties-scroll", scroll); Grid.SetRow(scroll, 1); pane.Children.Add(scroll);
        pane.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape && CompactInfo && InfoVisible) { CloseInfo(); e.Handled = true; } };
        return pane;
    }
    private void ToggleInfo()
    {
        if (CompactInfo) _compactInfoOpen = !_compactInfoOpen; else _wideInfoClosed = !_wideInfoClosed;
        ResizeWorkspace();
    }
    private void CloseInfo()
    {
        if (CompactInfo) _compactInfoOpen = false; else _wideInfoClosed = true;
        ResizeWorkspace(); _info.Focus(FocusState.Programmatic);
    }
    private void ArrangeInfo()
    {
        var panel = _controls["inspector-panel"];
        bool visible = InfoVisible, compact = CompactInfo;
        _inspectorColumn.Width = new GridLength(visible && !compact ? 285 : 0);
        panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(panel, compact ? 2 : 3); Grid.SetColumnSpan(panel, compact ? 2 : 1);
        panel.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        panel.Width = compact ? Math.Min(285, Math.Max(0, ActualWidth - 48)) : double.NaN;
        _info.IsActive = visible;
    }
    private void BuildRangeInspector(StackPanel panel)
    {
        panel.Children.Add(new TextBlock { Text = "VISIBLE RANGE", FontSize = 10, Opacity = .6 });
        panel.Children.Add(RangeEditor("X", _xMin, _xMax)); panel.Children.Add(RangeEditor("Y", _yMin, _yMax));
        Configure("x-min", _xMin, "X minimum", (e, text) => e.X.MinimumText = text);
        Configure("x-max", _xMax, "X maximum", (e, text) => e.X.MaximumText = text);
        Configure("y-min", _yMin, "Y minimum", (e, text) => e.Y.MinimumText = text);
        Configure("y-max", _yMax, "Y maximum", (e, text) => e.Y.MaximumText = text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _applyRangesButton = Button("apply-ranges", ChartGlyph.Settings, "Apply ranges", ApplyRanges, "Apply edited ranges (Enter)");
        _revertRangesButton = Button("revert-ranges", ChartGlyph.Undo, "Revert", RevertRanges, "Discard range drafts and use the current viewport (Escape)");
        actions.Children.Add(_applyRangesButton); actions.Children.Add(_revertRangesButton); panel.Children.Add(actions);
        panel.Children.Add(_rangeStatus); panel.Children.Add(_rangeUnits);
        void Configure(string id, TextBox box, string name, Action<ViewportRangeEditor, string> edit)
        {
            Register(id, box); box.MaxLength = AxisRangeDraft.MaximumInputLength;
            AutomationProperties.SetName(box, name); ToolTipService.SetToolTip(box, name + " in numerical model units; decimal point or exponent");
            box.TextChanged += (_, _) =>
            {
                if (_syncRanges || _changing || _rangeEditor is null) return;
                edit(_rangeEditor, box.Text); UpdateRangeEditors(); PublishSnapshot();
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Enter) { ApplyRanges(); e.Handled = true; }
                else if (e.Key == VirtualKey.Escape) { RevertRanges(); e.Handled = true; }
            };
        }
    }
    private void ResetRangeEditor()
    {
        _rangeEditor?.Dispose(); _rangeEditor = _model is null ? null : new(_model);
        _rangeXVersion = _rangeYVersion = -1;
    }
    private void UpdateRangeEditors()
    {
        if (_syncRanges || _changing || _rangeEditor is not { } editor) return;
        // Native Text may already contain input whose routed TextChanged callback has
        // not run. Capture it before a live frame can write the old draft back over it.
        // A reset/revert deliberately invalidates published versions and skips capture.
        if (_rangeXVersion >= 0)
        {
            if (_xMin.Text != _publishedXMinimum) editor.X.MinimumText = _xMin.Text;
            if (_xMax.Text != _publishedXMaximum) editor.X.MaximumText = _xMax.Text;
        }
        if (_rangeYVersion >= 0)
        {
            if (_yMin.Text != _publishedYMinimum) editor.Y.MinimumText = _yMin.Text;
            if (_yMax.Text != _publishedYMaximum) editor.Y.MaximumText = _yMax.Text;
        }
        editor.Refresh();
        bool changedX = _rangeXVersion != editor.X.Version, changedY = _rangeYVersion != editor.Y.Version;
        if (!changedX && !changedY && !editor.IsStale) return;
        _rangeXVersion = editor.X.Version; _rangeYVersion = editor.Y.Version;
        _syncRanges = true;
        try
        {
            if (changedX)
            {
                Set(_xMin, editor.X.MinimumText); Set(_xMax, editor.X.MaximumText);
                _publishedXMinimum = _xMin.Text; _publishedXMaximum = _xMax.Text;
            }
            if (changedY)
            {
                Set(_yMin, editor.Y.MinimumText); Set(_yMax, editor.Y.MaximumText);
                _publishedYMinimum = _yMin.Text; _publishedYMaximum = _yMax.Text;
            }
            _applyRangesButton.IsEnabled = editor.CanApply;
            _revertRangesButton.IsEnabled = editor.HasDraft || editor.HasConflict;
            _rangeStatus.Text = editor.HasConflict ? "Viewport changed. Revert before applying these drafts." :
                editor.X.Error is { } x ? "X: " + x : editor.Y.Error is { } y ? "Y: " + y :
                editor.CanApply ? "Unapplied range changes" : editor.HasDraft ? "Equivalent values; no range change" : "Ranges match the current viewport";
            if (_model is { } model) _rangeUnits.Text = $"X: {model.XAxis.Scale} · Auto {model.XAxis.AutoRange}\nY: {model.YAxis.Scale} · Auto {model.YAxis.AutoRange}";
        }
        finally { _syncRanges = false; }
        static void Set(TextBox box, string text) { if (box.Text != text) box.Text = text; }
    }
    private void ApplyRanges()
    {
        if (_rangeEditor is null) return;
        UpdateRangeEditors(); // Include native input even if its change notification is queued.
        _chart.Controller.Cancel();
        var result = _rangeEditor.Apply(_chart.Controller.History);
        if (result == RangeApplyResult.Applied) _rangeXVersion = _rangeYVersion = -1;
        UpdateRangeEditors();
        ShowMessage(result switch
        {
            RangeApplyResult.Applied => "Visible ranges updated",
            RangeApplyResult.Unchanged => "No visible range change",
            RangeApplyResult.Invalid => "Correct the range fields before applying",
            _ => "Viewport changed; range drafts were not applied"
        });
    }
    private void RevertRanges()
    {
        if (_rangeEditor is null) return;
        if (_rangeEditor.IsStale) ResetRangeEditor(); else _rangeEditor.Revert();
        _rangeXVersion = _rangeYVersion = -1; // Explicit Revert discards even not-yet-notified native input.
        UpdateRangeEditors(); PublishSnapshot();
    }
    private RangeInspectorSnapshot? RangeInspector() => _rangeEditor is not { } editor ? null :
        new(_xMin.Text, _xMax.Text, _yMin.Text, _yMax.Text,
            editor.HasDraft, editor.HasConflict, editor.CanApply, editor.X.Error, editor.Y.Error,
            _rangeStatus.Text, InfoVisible, CompactInfo && InfoVisible, _applyRangesButton.IsEnabled, _revertRangesButton.IsEnabled);
}
public sealed record RangeInspectorSnapshot(string xMin, string xMax, string yMin, string yMax,
    bool dirty, bool conflict, bool canApply, string? xError, string? yError, string message, bool inspectorOpen, bool floatingInspector, bool applyEnabled, bool revertEnabled);
