using System.Text.Json;
using System.Text.Json.Serialization;
using ChartScene.Charting;
using ChartScene.Controls;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;
using ChartScene.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ChartScene.App;

/// <summary>Native text annotation controls backed by the same measured geometry used for rendering and input.</summary>
public sealed partial class TextAnnotationsPage:Page
{
    private readonly Grid _root=new(),_workspace=new();
    private readonly ChartSurface _chart=new();
    private readonly ChartModel _model=new(){Title="Text annotations",Subtitle="Drag the label body · Double-click to edit",ShowLegend=false};
    private readonly TextAnnotation _label=new(){Id="note",Text="Measured annotation\nSecond line",X1=.5,Y1=.4,XCoordinateMode=CoordinateMode.Relative,YCoordinateMode=CoordinateMode.Relative,FontSize=20,TextPadding=8,BorderThickness=1};
    private readonly TextBox _text=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=105,MaxLength=4096,FontSize=13};
    private readonly TextBlock _draftStatus=new(){FontSize=11,TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _status=new(){FontSize=11,Margin=new Thickness(12,5,12,5),TextWrapping=TextWrapping.Wrap};
    private readonly Border _inspector=new(){Padding=new Thickness(14),BorderThickness=new Thickness(1,0,0,0)};
    private readonly Dictionary<string,FrameworkElement> _controls=[];
    private readonly Dictionary<string,ChartToolButton> _buttons=[];
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(200)};
    private bool _light,_historyAttached;
    private long _actions;
    private string _lastAction="",_error="";
    private ContentDialog? _dialog;
    public TextAnnotationsPage()
    {
        HorizontalContentAlignment=HorizontalAlignment.Stretch;VerticalContentAlignment=VerticalAlignment.Stretch;
        _root.RowDefinitions.Add(new(){Height=GridLength.Auto});_root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});_root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var tools=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5,Padding=new Thickness(10,8,10,8)};
        void Add(string id,ChartGlyph glyph,string text,Action action)
        {
            var button=new ChartToolButton(glyph,text);button.Describe(text);Register(id,button);_buttons.Add(id,button);tools.Children.Add(button);
            button.Click+=(_,_)=>{_actions++;_lastAction=id;_chart.Controller.Cancel();action();Refresh();Publish();};
        }
        Add("horizontal",ChartGlyph.Forward,"H: Left",()=>Edit("Horizontal anchor",()=>_label.HorizontalAnchorPoint,v=>_label.HorizontalAnchorPoint=v,(HorizontalAnchorPoint)(((int)_label.HorizontalAnchorPoint+1)%3)));
        Add("vertical",ChartGlyph.Columns,"V: Top",()=>Edit("Vertical anchor",()=>_label.VerticalAnchorPoint,v=>_label.VerticalAnchorPoint=v,(VerticalAnchorPoint)(((int)_label.VerticalAnchorPoint+1)%3)));
        Add("align",ChartGlyph.Menu,"Align",()=>Edit("Multiline alignment",()=>_label.TextAlignment,v=>_label.TextAlignment=v,(AnnotationTextAlignment)(((int)_label.TextAlignment+1)%3)));
        Add("rotate",ChartGlyph.Undo,"Rotate",()=>Edit("Text rotation",()=>_label.RotationAngle,v=>_label.RotationAngle=v,(_label.RotationAngle+30)%360));
        Add("bold",ChartGlyph.TextBold,"Bold",()=>Edit("Text weight",()=>_label.FontWeight,v=>_label.FontWeight=v,_label.FontBold?400:700));
        Add("italic",ChartGlyph.TextItalic,"Italic",()=>Edit("Text style",()=>_label.FontSlant,v=>_label.FontSlant=v,_label.FontSlant==FontSlant.Italic?FontSlant.Normal:FontSlant.Italic));
        Add("font",ChartGlyph.Plus,"20 px",()=>Edit("Font size",()=>_label.FontSize,v=>_label.FontSize=v,_label.FontSize==32?16:_label.FontSize+4));
        Add("background",ChartGlyph.Grid,"Panel",()=>Edit("Text background",()=>_label.Background,v=>_label.Background=v,_label.Background is null?(ColorRgba?)_model.Theme.Background:null));
        Add("flip",ChartGlyph.Forward,"Flip Y",()=>Edit("Axis direction",()=>_model.YAxis.FlippedCoordinates,v=>_model.YAxis.FlippedCoordinates=v,!_model.YAxis.FlippedCoordinates));
        Add("undo",ChartGlyph.Undo,"Undo",()=>_chart.Controller.History.Undo());
        Add("redo",ChartGlyph.Redo,"Redo",()=>_chart.Controller.History.Redo());
        Add("theme",ChartGlyph.Settings,"Theme",()=>{_light=!_light;Theme();});
        _root.Children.Add(new ScrollViewer{Content=tools,HorizontalScrollMode=ScrollMode.Enabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollMode=ScrollMode.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
        _workspace.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});_workspace.ColumnDefinitions.Add(new(){Width=new GridLength(260)});
        _chart.Model.Dispose();_chart.Model=_model;_workspace.Children.Add(_chart);Register("chart",_chart);
        _text.Text=_label.Text;Register("text",_text);
        var panel=new StackPanel{Spacing=12};panel.Children.Add(new TextBlock{Text="ANNOTATION TEXT",FontSize=11,Opacity=.7});panel.Children.Add(_text);
        var apply=new ChartToolButton(ChartGlyph.Source,"Apply text");apply.Describe("Apply annotation text");Register("apply",apply);_buttons.Add("apply",apply);panel.Children.Add(apply);
        apply.Click+=(_,_)=>{_actions++;_lastAction="apply";ApplyText(_text.Text);RefreshDraft();Publish();};
        var revert=new ChartToolButton(ChartGlyph.Undo,"Revert draft");revert.Describe("Discard unapplied text changes");Register("revert",revert);_buttons.Add("revert",revert);panel.Children.Add(revert);
        revert.Click+=(_,_)=>{_actions++;_lastAction="revert";_text.Text=_label.Text;RefreshDraft();Publish();};
        panel.Children.Add(_draftStatus);
        BuildTypographyInspector(panel);
        BuildWrappingInspector(panel);
        _text.TextChanged+=(_,_)=>{RefreshDraft();Publish();};RefreshDraft();
        panel.Children.Add(new TextBlock{Text="Click or drag anywhere in the label, not just its anchor. Double-click opens the native text editor.\n\nAlignment, rotation, font changes and text edits share chart undo/redo. Revert discards only the unapplied inspector draft.",FontSize=12,TextWrapping=TextWrapping.Wrap});
        _inspector.Child=new ScrollViewer{Content=panel,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetColumn(_inspector,1);_workspace.Children.Add(_inspector);Register("inspector",_inspector);
        Grid.SetRow(_workspace,1);_root.Children.Add(_workspace);Grid.SetRow(_status,2);_root.Children.Add(_status);Content=_root;
        var data=new XyDataSeries();data.AppendRange(Enumerable.Range(0,101).Select(i=>i*.1).ToArray(),Enumerable.Range(0,101).Select(i=>5+2*Math.Sin(i*.08)).ToArray());
        _model.RenderableSeries.Add(new FastLineRenderableSeries{DataSeries=data,SeriesName="Signal"});_model.Annotations.Add(_label);
        _model.XAxis.VisibleRange=_model.YAxis.VisibleRange=new(0,10);_model.XAxis.AutoRange=_model.YAxis.AutoRange=AutoRange.Never;
        _chart.Controller.AnnotationEditRequested+=(_,annotation)=>OpenEditor(annotation);
        _chart.RenderCompleted+=(_,_)=>{Refresh();Publish();};_timer.Tick+=(_,_)=>Publish();
        Loaded+=(_,_)=>{if(!_historyAttached){_chart.Controller.History.Changed+=HistoryChanged;_historyAttached=true;}Theme();Resize();Refresh();_timer.Start();};
        Unloaded+=(_,_)=>{_timer.Stop();if(_historyAttached){_chart.Controller.History.Changed-=HistoryChanged;_historyAttached=false;}_dialog?.Hide();};
        SizeChanged+=(_,_)=>{Resize();Publish();};
    }
    private void HistoryChanged(object? sender,EventArgs args){Refresh();Publish();}
    private void Edit<T>(string name,Func<T> get,Action<T> set,T value)
    {
        var before=get();if(EqualityComparer<T>.Default.Equals(before,value))return;
        _chart.Controller.History.Execute(new DelegateCommand(name,()=>set(value),()=>set(before)));
    }
    private void ApplyText(string value)
    {
        // Native multiline TextBox returns CR separators. Unchanged text must not
        // become a history entry merely because its separator representation changed.
        if (AnnotationTextContent.Equivalent(_label.Text, value)) return;
        Edit("Annotation text", () => _label.Text, SetText, AnnotationTextContent.NormalizeLineEndings(value));
    }
    private void SetText(string value)
    {
        _label.Text = value;
        // Synchronize Apply and Undo/Redo, not every render: styling and pointer
        // movement must never replace a draft the user is still typing.
        _text.Text = value;
    }
    private bool HasDraft => !AnnotationTextContent.Equivalent(_label.Text, _text.Text);
    private void RefreshDraft()
    {
        bool dirty=HasDraft;
        _buttons["revert"].IsEnabled=dirty;
        _buttons["apply"].IsActive=dirty;
        _draftStatus.Text=dirty?"Unapplied changes":"Text is up to date";
    }
    private async void OpenEditor(ChartAnnotation annotation)
    {
        if(_dialog is not null||!IsLoaded||!ReferenceEquals(annotation,_label))return;
        var input=new TextBox{Text=annotation.Text,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinWidth=300,Height=130,MaxLength=4096};Register("dialog-text",input);
        ApplyInputTypography(input);
        var dialog=new ContentDialog{Title="Edit annotation text",Content=input,PrimaryButtonText="Apply",CloseButtonText="Cancel",XamlRoot=XamlRoot};_dialog=dialog;
        try
        {
            // Apply synchronizes through SetText. Cancel, Escape, unload and a
            // no-op Apply must leave a separate, unapplied inspector draft intact.
            if(await dialog.ShowAsync()==ContentDialogResult.Primary)ApplyText(input.Text);
        }
        catch(Exception error){_error=error.Message;}
        finally{_dialog=null;foreach(string key in _controls.Keys.Where(k=>k.StartsWith("dialog-",StringComparison.Ordinal)).ToArray())_controls.Remove(key);RefreshDraft();Publish();}
    }
    private void Register(string id,FrameworkElement control){_controls[id]=control;AutomationProperties.SetAutomationId(control,"text-"+id);UiInputTrace.Register(control,"text-"+id);}
    private static SolidColorBrush Brush(ColorRgba c)=>new(Windows.UI.Color.FromArgb(c.A,c.R,c.G,c.B));
    private void Theme()
    {
        var theme=_light?ChartTheme.Light:ChartTheme.Dark;RequestedTheme=_light?ElementTheme.Light:ElementTheme.Dark;
        _model.Theme=theme;_label.Stroke=theme.Accent;_label.BorderColor=theme.Accent;if(_label.Background is not null)_label.Background=theme.Background;
        _root.Background=_inspector.Background=Brush(theme.Background);_inspector.BorderBrush=Brush(theme.Border);_status.Foreground=_draftStatus.Foreground=Brush(theme.Muted);
        foreach(var button in _buttons.Values)button.ApplyTheme(theme);
    }
    private void Resize(){bool visible=ActualWidth>=1100;_inspector.Visibility=visible?Visibility.Visible:Visibility.Collapsed;_workspace.ColumnDefinitions[1].Width=new GridLength(visible?260:0);}
    private void Refresh()
    {
        _buttons["horizontal"].Label="H: "+_label.HorizontalAnchorPoint;_buttons["vertical"].Label="V: "+_label.VerticalAnchorPoint;
        _buttons["font"].Label=_label.FontSize+" px";_buttons["bold"].IsActive=_label.FontBold;_buttons["background"].IsActive=_label.Background is not null;_buttons["flip"].IsActive=_model.YAxis.FlippedCoordinates;
        _buttons["italic"].IsActive=_label.FontSlant==FontSlant.Italic;
        _buttons["align"].Glyph=_label.TextAlignment switch{AnnotationTextAlignment.Center=>ChartGlyph.TextAlignCenter,AnnotationTextAlignment.Right=>ChartGlyph.TextAlignRight,_=>ChartGlyph.TextAlignLeft};
        RefreshTypography();
        RefreshWrapping();
        _buttons["undo"].IsEnabled=_chart.Controller.History.CanUndo;_buttons["redo"].IsEnabled=_chart.Controller.History.CanRedo;
        _status.Text=string.IsNullOrEmpty(_error)?$"{_label.HorizontalAnchorPoint} / {_label.VerticalAnchorPoint} · {_label.RotationAngle}° · {_label.FontSize} px · Drag body or double-click to edit":_error;
        RefreshDraft();
    }
    private static FrameworkElement? Find(DependencyObject root,string name)
    {
        if(root is FrameworkElement e&&e.Name==name)return e;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(Find(VisualTreeHelper.GetChild(root,i),name) is { } found)return found;
        return null;
    }
    private static ControlBounds? Bounds(FrameworkElement e)
    {
        if(!e.IsLoaded||e.ActualWidth<=0||e.ActualHeight<=0)return null;
        for(DependencyObject? current=e;current is not null;current=VisualTreeHelper.GetParent(current))if(current is UIElement ui&&ui.Visibility!=Visibility.Visible)return null;
        var p=e.TransformToVisual(null).TransformPoint(new Point(0,0));return new(p.X,p.Y,e.ActualWidth,e.ActualHeight);
    }
    private void Publish()
    {
#if __WASM__
        if(!IsLoaded||_chart.LastFrame is not { } frame)return;
        if(_dialog is { } dialog)
        {
            if(Find(dialog,"PrimaryButton") is { } primary)Register("dialog-apply",primary);
            if(Find(dialog,"CloseButton") is { } close)Register("dialog-cancel",close);
        }
        var origin=_chart.TransformToVisual(null).TransformPoint(new Point(0,0));
        TextPointDto Point(PointD p)=>new(p.X+origin.X,p.Y+origin.Y);
        ControlBounds Rect(RectD r)=>new(r.X+origin.X,r.Y+origin.Y,r.Width,r.Height);
        var layout=frame.FindTextAnnotation(_label);
        var snapshot=new TextPageSnapshot(_chart.LastError is null&&frame.Statistics.ModelRevision==_model.Revision,
            _chart.LastError??(string.IsNullOrEmpty(_error)?null:_error),_label.Text,_label.HorizontalAnchorPoint.ToString(),_label.VerticalAnchorPoint.ToString(),
            _label.RotationAngle,_label.FontSize,_label.FontBold,_label.TextAlignment.ToString(),_label.Background is not null,_model.Theme.Name,
            _label.IsSelected,_label.X1,_label.Y1,_chart.FrameCount,_chart.Controller.History.CanUndo,_chart.Controller.History.CanRedo,_dialog is not null,
            _actions,_lastAction,layout is null?null:Rect(layout.Bounds),layout is null?null:Point(layout.Anchor),layout?.Corners.Select(Point).ToArray()??[],layout?.Lines.Count??0,
            _controls.ToDictionary(p=>p.Key,p=>Bounds(p.Value)),_text.Text,HasDraft,_buttons["revert"].IsEnabled,Typography(),Wrapping());
        Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.chartSceneText="+JsonSerializer.Serialize(snapshot,TextPageJsonContext.Default.TextPageSnapshot));
#endif
    }
}
public sealed record TextPointDto(double x,double y);
public sealed record TextPageSnapshot(bool ready,string? error,string text,string horizontal,string vertical,double rotation,double fontSize,bool bold,string alignment,bool background,string theme,
    bool selected,double x1,double y1,long frames,bool canUndo,bool canRedo,bool dialog,long actions,string lastAction,ControlBounds? body,TextPointDto? anchor,TextPointDto[] corners,int lines,Dictionary<string,ControlBounds?> controls,string editorText,bool hasDraft,bool revertEnabled,TextTypographySnapshot typography,TextWrappingSnapshot wrapping);
[JsonSerializable(typeof(TextPageSnapshot))]
public partial class TextPageJsonContext:JsonSerializerContext;
