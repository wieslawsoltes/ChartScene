using ChartScene.Core;
using ChartScene.Data;

namespace ChartScene.Charting;

public readonly record struct ColorStop(double Offset,ColorRgba Color);
public sealed class ColorMap
{
    private readonly ColorStop[] _stops;
    public IReadOnlyList<ColorStop> Stops=>Array.AsReadOnly(_stops);
    public ColorMap(params ColorStop[] stops)
    {
        if(stops.Length<2||stops.Any(s=>!double.IsFinite(s.Offset)||s.Offset<0||s.Offset>1)) throw new ArgumentException("At least two valid stops in [0,1] are required.");
        _stops=stops.OrderBy(s=>s.Offset).ToArray();
        if(_stops[0].Offset!=0||_stops[^1].Offset!=1) throw new ArgumentException("A color map must cover [0,1].");
        for(int i=1;i<_stops.Length;i++) if(_stops[i].Offset==_stops[i-1].Offset) throw new ArgumentException("Color stops must have unique offsets.");
    }
    public ColorRgba Sample(double value)
    {
        if(double.IsNaN(value)) return ColorRgba.Transparent;
        value=Math.Clamp(value,0,1);
        int i=1; while(i<_stops.Length-1&&_stops[i].Offset<value) i++;
        var a=_stops[i-1]; var b=_stops[i];
        return ColorRgba.Lerp(a.Color,b.Color,(value-a.Offset)/(b.Offset-a.Offset));
    }
    public static ColorMap Viridis { get; }=new(new(0,ColorRgba.Parse("#440154")),new(.25,ColorRgba.Parse("#3B528B")),new(.5,ColorRgba.Parse("#21918C")),new(.75,ColorRgba.Parse("#5EC962")),new(1,ColorRgba.Parse("#FDE725")));
    public static ColorMap Thermal { get; }=new(new(0,ColorRgba.Parse("#13284B")),new(.25,ColorRgba.Parse("#237EA3")),new(.5,ColorRgba.Parse("#55C2CF")),new(.7,ColorRgba.Parse("#F5D363")),new(1,ColorRgba.Parse("#E34B5A")));
    public static ColorMap Diverging { get; }=new(new(0,ColorRgba.Parse("#277DA8")),new(.5,ColorRgba.Parse("#EDEDEB")),new(1,ColorRgba.Parse("#C84E65")));
}

public sealed record ChartTheme(string Name,ColorRgba Background,ColorRgba PlotBackground,ColorRgba Foreground,ColorRgba Muted,ColorRgba GridMajor,ColorRgba GridMinor,ColorRgba Border,ColorRgba Accent,IReadOnlyList<ColorRgba> Palette)
{
    public static ChartTheme Dark { get; }=new("Dark",ColorRgba.Parse("#171C28"),ColorRgba.Parse("#1B2942"),ColorRgba.Parse("#DCE4EF"),ColorRgba.Parse("#8594AB"),ColorRgba.Parse("#29394F"),ColorRgba.Parse("#213149"),ColorRgba.Parse("#334055"),ColorRgba.Parse("#58BCCF"),new[]{"#58BCCF","#E9A85B","#C568A9","#6FB899","#8A8BCC","#E27A7C","#D5CA7E"}.Select(ColorRgba.Parse).ToArray());
    public static ChartTheme Light { get; }=new("Light",ColorRgba.Parse("#FAFBFD"),ColorRgba.White,ColorRgba.Parse("#25334A"),ColorRgba.Parse("#718096"),ColorRgba.Parse("#DFE5EE"),ColorRgba.Parse("#F0F3F7"),ColorRgba.Parse("#CED7E3"),ColorRgba.Parse("#238AA9"),new[]{"#268EAC","#C67C28","#A7558E","#359770","#6768AF","#C45457","#9D9225"}.Select(ColorRgba.Parse).ToArray());
    public static ChartTheme HighContrast { get; }=new("High contrast",ColorRgba.Black,ColorRgba.Black,ColorRgba.White,ColorRgba.White,new(95,95,95),new(40,40,40),ColorRgba.White,new(255,255,0),[new(0,255,255),new(255,255,0),new(255,0,255),new(0,255,0),new(255,255,255)]);
}
public interface IPaletteProvider
{
    ColorRgba? GetStroke(RenderableSeries series,SeriesSnapshot data,int index);
    ColorRgba? GetFill(RenderableSeries series,SeriesSnapshot data,int index);
}
public interface IDataLabelProvider { string? GetLabel(RenderableSeries series,SeriesSnapshot data,int index); }
public sealed class NumericDataLabelProvider(string format="G4"):IDataLabelProvider
{
    public string? GetLabel(RenderableSeries series,SeriesSnapshot data,int index)=>double.IsFinite(data[series.YChannel,index])?data[series.YChannel,index].ToString(format,System.Globalization.CultureInfo.InvariantCulture):null;
}
public sealed class ThresholdPaletteProvider(double threshold,ColorRgba below,ColorRgba above):IPaletteProvider
{
    public ColorRgba? GetStroke(RenderableSeries series,SeriesSnapshot data,int index)=>data[series.YChannel,index]>=threshold?above:below;
    public ColorRgba? GetFill(RenderableSeries series,SeriesSnapshot data,int index)=>GetStroke(series,data,index)?.WithAlpha(160);
}
