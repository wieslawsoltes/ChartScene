using ChartScene.Controls;
using ChartScene.Interaction;
using ChartScene.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace ChartScene.App;

public sealed partial class TextAnnotationsPage
{
    private bool _compactInspectorOpen,_wideInspectorClosed;
    private TextAnnotationResizeModifier _widthResize=null!;
    private bool CompactInspector=>ActualWidth<1100;
    private bool InspectorOpen=>CompactInspector?_compactInspectorOpen:!_wideInspectorClosed;

    private Grid BuildInspectorContent(StackPanel panel)
    {
        var header=new Grid();header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        header.Children.Add(new TextBlock{Text="ANNOTATION PROPERTIES",FontSize=11,Opacity=.7,VerticalAlignment=VerticalAlignment.Center});
        var close=new ChartToolButton(ChartGlyph.Close);close.Describe("Close annotation properties");
        Register("close-properties",close);_buttons.Add("close-properties",close);Grid.SetColumn(close,1);header.Children.Add(close);
        close.Click+=(_,_)=>CloseInspector();header.Margin=new Thickness(0,0,0,12);
        var content=new Grid();content.RowDefinitions.Add(new(){Height=GridLength.Auto});
        content.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});content.Children.Add(header);
        var scroll=new ScrollViewer{Content=panel,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        Grid.SetRow(scroll,1);content.Children.Add(scroll);
        _inspector.KeyDown+=(_,e)=>{if(e.Key==VirtualKey.Escape&&CompactInspector&&InspectorOpen){CloseInspector();e.Handled=true;}};
        return content;
    }
    private string WidthHint()=>_label.TextWrapping==ChartScene.Charting.AnnotationTextWrapping.NoWrap
        ? "Drag body to move · Enable wrapping for a width grip"
        : $"{_label.TextWrapWidth:0.##} px wrap · Select and drag the round width grip";
    private void ToggleInspector()
    {
        if(CompactInspector)_compactInspectorOpen=!_compactInspectorOpen;else _wideInspectorClosed=!_wideInspectorClosed;
        Resize();
    }
    private void CloseInspector()
    {
        _actions++;_lastAction="close-properties";
        if(CompactInspector)_compactInspectorOpen=false;else _wideInspectorClosed=true;
        Resize();_buttons["properties"].Focus(FocusState.Programmatic);Publish();
    }
    private void Resize()
    {
        bool compact=CompactInspector,visible=InspectorOpen;
        _inspector.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
        _inspector.BorderThickness=compact?new Thickness(1):new Thickness(1,0,0,0);
        // A compact inspector overlays rather than reparenting or resizing the chart.
        // Draft controls and native popup/focus state retain their identity.
        Grid.SetColumn(_inspector,compact?0:1);Grid.SetColumnSpan(_inspector,compact?2:1);
        _inspector.HorizontalAlignment=compact?HorizontalAlignment.Right:HorizontalAlignment.Stretch;
        _inspector.Width=compact?Math.Min(280,Math.Max(0,ActualWidth-24)):double.NaN;
        _workspace.ColumnDefinitions[1].Width=new GridLength(!compact&&visible?260:0);
        _buttons["properties"].IsActive=visible;
    }
}
public sealed record TextResizeSnapshot(bool active,TextPointDto? grip,double widthPerPixel,bool inspectorOpen,bool floatingInspector);
