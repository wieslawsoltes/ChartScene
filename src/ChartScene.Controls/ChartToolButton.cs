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
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph),typeof(ChartGlyph),typeof(ChartIcon),new PropertyMetadata(ChartGlyph.Line,Changed));
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(nameof(Tint),typeof(Windows.UI.Color),typeof(ChartIcon),new PropertyMetadata(Microsoft.UI.Colors.LightGray,Changed));
    public ChartGlyph Glyph { get => (ChartGlyph)GetValue(GlyphProperty); set => SetValue(GlyphProperty,value); }
    public Windows.UI.Color Tint { get => (Windows.UI.Color)GetValue(TintProperty); set => SetValue(TintProperty,value); }
    public ChartIcon()
    {
        Width=18; Height=18; IsHitTestVisible=false;
        Unloaded+=(_,_)=>{_resources?.Dispose();_resources=null;};
    }
    private static void Changed(DependencyObject sender,DependencyPropertyChangedEventArgs args)=>((ChartIcon)sender).Invalidate();
    protected override void RenderOverride(SKCanvas canvas,Size area)
    {
        _resources??=new();_resources.BeginFrame();var c=Tint;
        ChartIconRenderer.Draw(new SkiaChartCanvas(canvas,_resources),Glyph,new(0,0,area.Width,area.Height),new(c.R,c.G,c.B,c.A));
    }
}

/// <summary>Theme-aware compact command button with a reusable vector glyph and optional label.</summary>
public sealed class ChartToolButton : Button
{
    private readonly ChartIcon _icon=new();
    private readonly TextBlock _label=new(){FontSize=12,VerticalAlignment=VerticalAlignment.Center};
    private ChartTheme _theme=ChartTheme.Dark;
    private bool _active;
    public ChartGlyph Glyph { get=>_icon.Glyph;set=>_icon.Glyph=value; }
    public string Label
    {
        get=>_label.Text;
        set { _label.Text=value??"";_label.Visibility=string.IsNullOrEmpty(value)?Visibility.Collapsed:Visibility.Visible; }
    }
    public bool IsActive { get=>_active;set{if(_active==value)return;_active=value;Refresh();} }
    public ChartToolButton(ChartGlyph glyph,string label="")
    {
        Glyph=glyph;Label=label; MinWidth=30;MinHeight=30;Padding=new Thickness(7,4,7,4);CornerRadius=new CornerRadius(2);BorderThickness=new Thickness(1);
        var panel=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6,VerticalAlignment=VerticalAlignment.Center};panel.Children.Add(_icon);panel.Children.Add(_label);Content=panel;
        HorizontalContentAlignment=HorizontalAlignment.Center;VerticalContentAlignment=VerticalAlignment.Center;
        Refresh();
    }
    public void ApplyTheme(ChartTheme theme){ArgumentNullException.ThrowIfNull(theme);_theme=theme;Refresh();}
    public void Describe(string text){AutomationProperties.SetName(this,text);ToolTipService.SetToolTip(this,text);}
    private static SolidColorBrush Brush(ColorRgba c)=>new(Windows.UI.Color.FromArgb(c.A,c.R,c.G,c.B));
    private void Refresh()
    {
        ColorRgba color=IsActive?_theme.Accent:_theme.Foreground;
        _icon.Tint=Windows.UI.Color.FromArgb(color.A,color.R,color.G,color.B);
        Foreground=Brush(color);_label.Foreground=Brush(color);
        Background=Brush(IsActive?_theme.Accent.WithAlpha(45):_theme.Background);
        BorderBrush=Brush(IsActive?_theme.Accent:_theme.Border);
    }
}
