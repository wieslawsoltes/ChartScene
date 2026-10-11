using ChartScene.Charting;
using ChartScene.Core;
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

/// <summary>Small vector icon. Owns only its drawing cache, not its host Skia canvas.</summary>
public sealed class ChartIcon : SKCanvasElement
{
    private SkiaResources? _resources;
    public static readonly DependencyProperty GlyphProperty=DependencyProperty.Register(nameof(Glyph),typeof(ChartGlyph),typeof(ChartIcon),new PropertyMetadata(ChartGlyph.Line,Changed));
    public static readonly DependencyProperty TintProperty=DependencyProperty.Register(nameof(Tint),typeof(Windows.UI.Color),typeof(ChartIcon),new PropertyMetadata(Microsoft.UI.Colors.LightGray,Changed));
    public ChartGlyph Glyph { get=>(ChartGlyph)GetValue(GlyphProperty);set=>SetValue(GlyphProperty,value); }
    public Windows.UI.Color Tint { get=>(Windows.UI.Color)GetValue(TintProperty);set=>SetValue(TintProperty,value); }
    public ChartIcon()
    {
        Width=18;Height=18;IsHitTestVisible=false;
        Unloaded+=(_,_)=>{_resources?.Dispose();_resources=null;};
    }
    private static void Changed(DependencyObject sender,DependencyPropertyChangedEventArgs args)=>((ChartIcon)sender).Invalidate();
    protected override void RenderOverride(SKCanvas canvas,Size area)
    {
        _resources??=new();_resources.BeginFrame();var c=Tint;
        ChartIconRenderer.Draw(new SkiaChartCanvas(canvas,_resources),Glyph,new(0,0,area.Width,area.Height),new(c.R,c.G,c.B,c.A));
    }
}

/// <summary>
/// Theme-aware vector command for code or XAML. Glyph, Label and IsActive are bindable
/// dependency properties; command execution uses the normal inherited Button contract.
/// </summary>
public sealed class ChartToolButton : Button
{
    private readonly ChartIcon _icon=new();
    private readonly TextBlock _label=new(){FontSize=12,VerticalAlignment=VerticalAlignment.Center};
    private ChartTheme _theme=ChartTheme.Dark;
    public static readonly DependencyProperty GlyphProperty=DependencyProperty.Register(nameof(Glyph),typeof(ChartGlyph),typeof(ChartToolButton),new PropertyMetadata(ChartGlyph.Line,Changed));
    public static readonly DependencyProperty LabelProperty=DependencyProperty.Register(nameof(Label),typeof(string),typeof(ChartToolButton),new PropertyMetadata("",Changed));
    public static readonly DependencyProperty IsActiveProperty=DependencyProperty.Register(nameof(IsActive),typeof(bool),typeof(ChartToolButton),new PropertyMetadata(false,Changed));
    public ChartGlyph Glyph { get=>(ChartGlyph)GetValue(GlyphProperty);set=>SetValue(GlyphProperty,value); }
    public string Label { get=>(string)GetValue(LabelProperty);set=>SetValue(LabelProperty,value??""); }
    public bool IsActive { get=>(bool)GetValue(IsActiveProperty);set=>SetValue(IsActiveProperty,value); }
    public ChartToolButton()
    {
        MinWidth=30;MinHeight=30;Padding=new Thickness(7,4,7,4);CornerRadius=new CornerRadius(2);BorderThickness=new Thickness(1);
        var panel=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6,VerticalAlignment=VerticalAlignment.Center};panel.Children.Add(_icon);panel.Children.Add(_label);Content=panel;
        HorizontalContentAlignment=HorizontalAlignment.Center;VerticalContentAlignment=VerticalAlignment.Center;
        Refresh();
    }
    public ChartToolButton(ChartGlyph glyph,string label=""):this(){Glyph=glyph;Label=label;}
    private static void Changed(DependencyObject sender,DependencyPropertyChangedEventArgs args)=>((ChartToolButton)sender).Refresh();
    public void ApplyTheme(ChartTheme theme){ArgumentNullException.ThrowIfNull(theme);_theme=theme;Refresh();}
    public void Describe(string text){AutomationProperties.SetName(this,text);ToolTipService.SetToolTip(this,text);}
    private static SolidColorBrush Brush(ColorRgba c)=>new(Windows.UI.Color.FromArgb(c.A,c.R,c.G,c.B));
    private void Refresh()
    {
        _icon.Glyph=Glyph;_label.Text=Label;_label.Visibility=string.IsNullOrEmpty(Label)?Visibility.Collapsed:Visibility.Visible;
        ColorRgba color=IsActive?_theme.Accent:_theme.Foreground;
        _icon.Tint=Windows.UI.Color.FromArgb(color.A,color.R,color.G,color.B);
        Foreground=Brush(color);_label.Foreground=Brush(color);
        Background=Brush(IsActive?_theme.Accent.WithAlpha(45):_theme.Background);
        BorderBrush=Brush(IsActive?_theme.Accent:_theme.Border);
    }
}
