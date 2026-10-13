using ChartScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ChartScene.App;

public sealed partial class TextAnnotationsPage
{
    private static readonly string[] Families=["Arial","Georgia","Courier New"];
    private static readonly int[] Weights=[300,400,500,600,700,900];
    private readonly ComboBox _familyChoice=new(){Header="Font family",HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly ComboBox _weightChoice=new(){Header="Font weight",HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly ComboBox _slantChoice=new(){Header="Font style",HorizontalAlignment=HorizontalAlignment.Stretch};
    private bool _syncingTypography;

    private void BuildTypographyInspector(StackPanel panel)
    {
        _familyChoice.ItemsSource=Families;
        _weightChoice.ItemsSource=new[]{"Light (300)","Normal (400)","Medium (500)","SemiBold (600)","Bold (700)","Black (900)"};
        _slantChoice.ItemsSource=new[]{"Normal","Italic","Oblique"};
        Add("family",_familyChoice,()=>Edit("Font family",()=>_label.FontFamily,v=>_label.FontFamily=v,Families[_familyChoice.SelectedIndex]));
        Add("weight",_weightChoice,()=>Edit("Font weight",()=>_label.FontWeight,v=>_label.FontWeight=v,Weights[_weightChoice.SelectedIndex]));
        Add("slant",_slantChoice,()=>Edit("Font style",()=>_label.FontSlant,v=>_label.FontSlant=v,(FontSlant)_slantChoice.SelectedIndex));
        void Add(string id,ComboBox choice,Action action)
        {
            Register(id,choice);AutomationProperties.SetName(choice,choice.Header.ToString());panel.Children.Add(choice);
            choice.SelectionChanged+=(_,_)=>
            {
                if(_syncingTypography||choice.SelectedIndex<0)return;
                _actions++;_lastAction=id;_chart.Controller.Cancel();action();Refresh();Publish();
            };
        }
        RefreshTypography();
    }
    private void RefreshTypography()
    {
        _syncingTypography=true;
        try
        {
            // Do not redirect focus or selection while a native popup is being navigated.
            if(!_familyChoice.IsDropDownOpen)_familyChoice.SelectedIndex=Array.IndexOf(Families,_label.FontFamily);
            if(!_weightChoice.IsDropDownOpen)_weightChoice.SelectedIndex=Array.IndexOf(Weights,_label.FontWeight);
            if(!_slantChoice.IsDropDownOpen)_slantChoice.SelectedIndex=(int)_label.FontSlant;
            ApplyInputTypography(_text);
        }
        finally{_syncingTypography=false;}
    }
    private void ApplyInputTypography(TextBox input)
    {
        if(input.FontFamily?.Source!=_label.FontFamily)input.FontFamily=new FontFamily(_label.FontFamily);
        if(input.FontWeight.Weight!=_label.FontWeight)input.FontWeight=new Windows.UI.Text.FontWeight{Weight=(ushort)_label.FontWeight};
        var style=_label.FontSlant switch
        {
            FontSlant.Italic=>Windows.UI.Text.FontStyle.Italic,
            FontSlant.Oblique=>Windows.UI.Text.FontStyle.Oblique,
            _=>Windows.UI.Text.FontStyle.Normal
        };
        if(input.FontStyle!=style)input.FontStyle=style;
    }
    private TextChoiceSnapshot Choice(ComboBox choice)
    {
        int focusedIndex=-1;
        if(XamlRoot is { } root)
            for(DependencyObject? focused=FocusManager.GetFocusedElement(root) as DependencyObject;
                focused is not null;focused=VisualTreeHelper.GetParent(focused))
                if(focused is ComboBoxItem item){focusedIndex=choice.IndexFromContainer(item);break;}
        return new(choice.SelectedIndex,choice.IsDropDownOpen,focusedIndex);
    }
    private TextTypographySnapshot Typography()=>new(_label.FontFamily,_label.FontWeight,_label.FontSlant.ToString(),
        Choice(_familyChoice),Choice(_weightChoice),Choice(_slantChoice));
}

public sealed record TextChoiceSnapshot(int selectedIndex,bool open,int focusedIndex);
public sealed record TextTypographySnapshot(string family,int weight,string slant,TextChoiceSnapshot familyChoice,TextChoiceSnapshot weightChoice,TextChoiceSnapshot slantChoice);
