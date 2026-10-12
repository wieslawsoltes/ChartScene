using System.Text.Json;
using System.Text.Json.Serialization;
using ChartScene.Charting;
using ChartScene.Controls;
using ChartScene.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChartScene.App;

public sealed partial class LinkedChartsPage
{
    private readonly CheckBox _alignPlots = new() { Content = "Align plot edges", IsChecked = true, FontSize = 12, MinHeight = 28 };
    private readonly CheckBox _wideLabels = new() { Content = "Wide lower labels", IsChecked = true, FontSize = 12, MinHeight = 28 };
    private ChartLayoutGroup? _layoutGroup;
    private bool _extraAxis, _reserved;
    private void BuildLayoutToolbar(StackPanel parent)
    {
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(12,0,12,6) };
        toolbar.Children.Add(_alignPlots); toolbar.Children.Add(_wideLabels);
        toolbar.Children.Add(Button("right-axis", "Right axis", () => { _extraAxis=!_extraAxis; ConfigureLayoutAxes(_lower.Model,true); }));
        toolbar.Children.Add(Button("reserve-space", "Reserve label space", () => { _reserved=!_reserved; _upper.MinimumPlotMargins=_reserved?new(210,0,80,0):default; }));
        toolbar.Children.Add(Button("hide-lower", "Show / hide lower", () => _lower.Visibility=_lower.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible));
        Register("align-plots",_alignPlots); Register("wide-labels",_wideLabels);
        _alignPlots.Checked+=(_,_)=>UpdateAlignment(); _alignPlots.Unchecked+=(_,_)=>UpdateAlignment();
        _wideLabels.Checked+=(_,_)=>ConfigureLayoutAxes(_lower.Model,true); _wideLabels.Unchecked+=(_,_)=>ConfigureLayoutAxes(_lower.Model,true);
        parent.Children.Add(new ScrollViewer { Content=toolbar, HorizontalScrollMode=ScrollMode.Enabled, HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden, VerticalScrollMode=ScrollMode.Disabled });
    }
    private void InitializeLayout()
    {
        _layoutGroup=new(); _layoutGroup.Add(_upper); _layoutGroup.Add(_lower); UpdateAlignment();
    }
    private void UpdateAlignment()
    {
        if(_layoutGroup is not null) _layoutGroup.Layout.Alignment=_alignPlots.IsChecked==true?PlotAlignment.Horizontal:PlotAlignment.None;
        Publish();
    }
    private void ConfigureLayoutAxes(ChartModel model,bool lower)
    {
        if(!lower)return;
        using(model.SuspendUpdates())
        {
            model.YAxis.TextFormatting=_wideLabels.IsChecked==true?"0.000000000":"0.#";
            var existing=model.YAxes.FirstOrDefault(a=>a.Id=="Auxiliary");
            if(_extraAxis&&existing is null)model.YAxes.Add(new NumericAxis { Id="Auxiliary", AxisAlignment=AxisAlignment.Right, AxisTitle="Auxiliary", TextFormatting="0.0000", VisibleRange=new(-1.6,1.6), AutoRange=AutoRange.Never });
            else if(!_extraAxis&&existing is not null)model.YAxes.Remove(existing);
        }
    }
    private void PublishLayout()
    {
#if __WASM__
        var stats=_layoutGroup?.Layout.Statistics??default;
        static MarginObservation Natural(ChartSurface surface)
        {
            var m=surface.LastFrame?.Layout.NaturalMargins??default;return new(m.Left,m.Top,m.Right,m.Bottom);
        }
        var state=new AlignmentSnapshot(_alignPlots.IsChecked==true,_wideLabels.IsChecked==true,_extraAxis,_lower.Visibility==Visibility.Visible,_reserved,
            stats.Members,stats.AggregatePasses,stats.Notifications,_upper.SceneCacheStatistics.SceneBuilds,_lower.SceneCacheStatistics.SceneBuilds,
            Natural(_upper),Natural(_lower));
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartSceneAlignment="+JsonSerializer.Serialize(state,AlignmentJsonContext.Default.AlignmentSnapshot));
#endif
    }
}
public sealed record MarginObservation(double left,double top,double right,double bottom);
public sealed record AlignmentSnapshot(bool enabled,bool wideLabels,bool rightAxis,bool lowerVisible,bool reserved,int members,long passes,long notifications,
    long upperSceneBuilds,long lowerSceneBuilds,MarginObservation upperNatural,MarginObservation lowerNatural);
[JsonSerializable(typeof(AlignmentSnapshot))]
public partial class AlignmentJsonContext:JsonSerializerContext;
