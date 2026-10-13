using ChartScene.Charting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChartScene.App;

public sealed partial class TextAnnotationsPage
{
    private static readonly double[] WrapWidths=[80,120,160,240,320];
    private readonly ComboBox _wrapChoice=new(){Header="Text wrapping",HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly ComboBox _wrapWidthChoice=new(){Header="Content width (logical px)",HorizontalAlignment=HorizontalAlignment.Stretch};
    private bool _syncingWrapping;
    private void BuildWrappingInspector(StackPanel panel)
    {
        _wrapChoice.ItemsSource=new[]{"No wrapping","Wrap long words","Keep whole words"};
        _wrapWidthChoice.ItemsSource=WrapWidths;
        Add("wrap",_wrapChoice,()=>Edit("Text wrapping",()=>_label.TextWrapping,v=>_label.TextWrapping=v,(AnnotationTextWrapping)_wrapChoice.SelectedIndex));
        Add("wrapWidth",_wrapWidthChoice,()=>Edit("Wrap width",()=>_label.TextWrapWidth,v=>_label.TextWrapWidth=v,WrapWidths[_wrapWidthChoice.SelectedIndex]));
        void Add(string id,ComboBox choice,Action action)
        {
            Register(id,choice);AutomationProperties.SetName(choice,choice.Header.ToString());panel.Children.Add(choice);
            choice.SelectionChanged+=(_,_)=>
            {
                if(_syncingWrapping||choice.SelectedIndex<0)return;
                _actions++;_lastAction=id;_chart.Controller.Cancel();action();Refresh();Publish();
            };
        }
        RefreshWrapping();
    }
    private void RefreshWrapping()
    {
        _syncingWrapping=true;
        try
        {
            if(!_wrapChoice.IsDropDownOpen)_wrapChoice.SelectedIndex=(int)_label.TextWrapping;
            if(!_wrapWidthChoice.IsDropDownOpen)_wrapWidthChoice.SelectedIndex=Array.IndexOf(WrapWidths,_label.TextWrapWidth);
            _wrapWidthChoice.IsEnabled=_label.TextWrapping!=AnnotationTextWrapping.NoWrap;
        }
        finally{_syncingWrapping=false;}
    }
    private TextWrappingSnapshot Wrapping()=>new(_label.TextWrapping.ToString(),_label.TextWrapWidth,
        Choice(_wrapChoice),Choice(_wrapWidthChoice),_wrapWidthChoice.IsEnabled);
}
public sealed record TextWrappingSnapshot(string mode,double width,TextChoiceSnapshot wrapChoice,TextChoiceSnapshot wrapWidthChoice,bool widthEnabled);
