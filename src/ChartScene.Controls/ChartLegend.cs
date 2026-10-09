using System.Collections.Specialized;
using System.ComponentModel;
using ChartScene.Charting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ChartScene.Controls;

public sealed class ChartLegend:UserControl,IDisposable
{
    private readonly StackPanel _items=new(){Spacing=4};
    private readonly HashSet<RenderableSeries> _observed=[];
    private ChartModel? _model;
    public ChartModel? Model
    {
        get=>_model;
        set
        {
            if(ReferenceEquals(_model,value)) return;
            Detach(); _model=value;
            if(_model is not null) { _model.RenderableSeries.CollectionChanged+=CollectionChanged; _model.PropertyChanged+=ModelChanged; }
            Rebuild();
        }
    }
    public Orientation Orientation { get=>_items.Orientation; set=>_items.Orientation=value; }
    public ChartLegend()=>Content=_items;
    private void CollectionChanged(object? sender,NotifyCollectionChangedEventArgs e)=>Rebuild();
    private void ModelChanged(object? sender,PropertyChangedEventArgs e) { if(e.PropertyName==nameof(ChartModel.Theme)) Rebuild(); }
    private void SeriesChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(RenderableSeries.SeriesName) or nameof(RenderableSeries.IsVisible) or nameof(RenderableSeries.Stroke)) Rebuild();
    }
    private void Rebuild()
    {
        foreach(var old in _observed) old.PropertyChanged-=SeriesChanged;
        _observed.Clear(); _items.Children.Clear();
        if(_model is null) return;
        foreach(var series in _model.RenderableSeries)
        {
            var color=series.Stroke;
            var check=new CheckBox{Content=series.SeriesName,IsChecked=series.IsVisible,MinHeight=28,Foreground=new SolidColorBrush(Windows.UI.Color.FromArgb(color.A,color.R,color.G,color.B))};
            check.Checked+=(_,_)=>series.IsVisible=true; check.Unchecked+=(_,_)=>series.IsVisible=false;
            _items.Children.Add(check); series.PropertyChanged+=SeriesChanged; _observed.Add(series);
        }
    }
    private void Detach()
    {
        if(_model is not null) { _model.RenderableSeries.CollectionChanged-=CollectionChanged; _model.PropertyChanged-=ModelChanged; }
        foreach(var old in _observed) old.PropertyChanged-=SeriesChanged;
        _observed.Clear();
    }
    public new void Dispose() { Detach(); base.Dispose(); }
}
