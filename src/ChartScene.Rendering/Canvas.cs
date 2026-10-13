using ChartScene.Core;
using ChartScene.Charting;

namespace ChartScene.Rendering;

public readonly record struct StrokeStyle(ColorRgba Color,double Width=1,double[]? Dash=null);
public readonly record struct TextStyle(ColorRgba Color,double Size=12,string Family="Arial",bool Bold=false)
{
    /// <summary>Zero uses the legacy Bold flag (700/400); an explicit weight in [1,1000] takes precedence.</summary>
    public int Weight { get; init; }
    public FontSlant Slant { get; init; }
    public int EffectiveWeight => Weight==0?(Bold?700:400):Weight;
}
public readonly record struct TextMetrics(double Width,double Height,double Ascent,double Descent);
public enum TextAlignment { Left,Center,Right }
public enum PathVerb { Move,Line,Cubic,Close }
public readonly record struct PathCommand(PathVerb Verb,PointD A,PointD B=default,PointD C=default);
public sealed class VectorPath
{
    private readonly List<PathCommand> _commands=[];
    public IReadOnlyList<PathCommand> Commands=>_commands;
    public VectorPath MoveTo(PointD p) { _commands.Add(new(PathVerb.Move,p)); return this; }
    public VectorPath LineTo(PointD p) { _commands.Add(new(PathVerb.Line,p)); return this; }
    public VectorPath CubicTo(PointD control1,PointD control2,PointD end) { _commands.Add(new(PathVerb.Cubic,control1,control2,end)); return this; }
    public VectorPath Close() { _commands.Add(new(PathVerb.Close,default)); return this; }
}
public sealed class RasterImage
{
    private readonly byte[] _pixels;
    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Pixels=>_pixels;
    public RasterImage(int width,int height,ReadOnlySpan<byte> rgba)
    {
        if(width<=0||height<=0||rgba.Length!=checked(width*height*4)) throw new ArgumentException("Image dimensions and RGBA byte length do not match.");
        Width=width; Height=height; _pixels=rgba.ToArray();
    }
}

/// <summary>Immediate-mode operations in logical pixels. Implementations must not own the host's canvas lifetime.</summary>
public interface IChartCanvas
{
    void Save();
    void Restore();
    void Clip(RectD rectangle);
    void Clear(ColorRgba color);
    void DrawLine(PointD a,PointD b,StrokeStyle stroke);
    void DrawPolyline(ReadOnlySpan<PointD> points,StrokeStyle stroke,bool closed=false);
    void DrawPath(VectorPath path,ColorRgba? fill,StrokeStyle? stroke=null);
    void DrawRectangle(RectD rectangle,ColorRgba? fill,StrokeStyle? stroke=null,double cornerRadius=0);
    void DrawEllipse(RectD rectangle,ColorRgba? fill,StrokeStyle? stroke=null);
    void DrawMarkers(ReadOnlySpan<PointD> points,PointMarker marker,double size,ColorRgba fill,StrokeStyle? stroke=null);
    void DrawImage(RasterImage image,RectD destination,bool flipX=false,bool flipY=false,bool interpolate=false);
    void DrawText(string text,PointD baseline,TextStyle style,TextAlignment alignment=TextAlignment.Left,double rotationDegrees=0);
    TextMetrics MeasureText(string text,TextStyle style);
}

public sealed class CountingCanvas(IChartCanvas inner):IChartCanvas
{
    public int Commands { get; private set; }
    public void Save()=>inner.Save();
    public void Restore()=>inner.Restore();
    public void Clip(RectD r)=>inner.Clip(r);
    public void Clear(ColorRgba c) { Commands++; inner.Clear(c); }
    public void DrawLine(PointD a,PointD b,StrokeStyle s) { Commands++; inner.DrawLine(a,b,s); }
    public void DrawPolyline(ReadOnlySpan<PointD> p,StrokeStyle s,bool c=false) { Commands++; inner.DrawPolyline(p,s,c); }
    public void DrawPath(VectorPath p,ColorRgba? f,StrokeStyle? s=null) { Commands++; inner.DrawPath(p,f,s); }
    public void DrawRectangle(RectD r,ColorRgba? f,StrokeStyle? s=null,double radius=0) { Commands++; inner.DrawRectangle(r,f,s,radius); }
    public void DrawEllipse(RectD r,ColorRgba? f,StrokeStyle? s=null) { Commands++; inner.DrawEllipse(r,f,s); }
    public void DrawMarkers(ReadOnlySpan<PointD> p,PointMarker m,double z,ColorRgba f,StrokeStyle? s=null) { Commands++; inner.DrawMarkers(p,m,z,f,s); }
    public void DrawImage(RasterImage i,RectD r,bool x=false,bool y=false,bool filter=false) { Commands++; inner.DrawImage(i,r,x,y,filter); }
    public void DrawText(string t,PointD p,TextStyle s,TextAlignment a=TextAlignment.Left,double rotation=0) { Commands++; inner.DrawText(t,p,s,a,rotation); }
    public TextMetrics MeasureText(string t,TextStyle s)=>inner.MeasureText(t,s);
}

/// <summary>Deterministic geometry recorder for tests and custom exporters; not a visual renderer.</summary>
public sealed class RecordingCanvas:IChartCanvas
{
    public List<RecordedCommand> Commands { get; }=[];
    public int SaveDepth { get; private set; }
    public void Save() { SaveDepth++; Commands.Add(new("save",[])); }
    public void Restore() { if(SaveDepth==0) throw new InvalidOperationException("Unbalanced restore."); SaveDepth--; Commands.Add(new("restore",[])); }
    public void Clip(RectD r)=>Commands.Add(new("clip",[new(r.X,r.Y),new(r.Right,r.Bottom)]));
    public void Clear(ColorRgba c)=>Commands.Add(new("clear",[],c));
    public void DrawLine(PointD a,PointD b,StrokeStyle s)=>Add(new("line",[a,b],s.Color));
    public void DrawPolyline(ReadOnlySpan<PointD> p,StrokeStyle s,bool closed=false)=>Add(new(closed?"polygon-stroke":"polyline",p.ToArray(),s.Color));
    public void DrawPath(VectorPath p,ColorRgba? fill,StrokeStyle? stroke=null)
    {
        var points=p.Commands.Where(c=>c.Verb!=PathVerb.Close).SelectMany(c=>c.Verb==PathVerb.Cubic?new[]{c.A,c.B,c.C}:new[]{c.A}).ToArray();
        Add(new("path",points,fill??stroke?.Color));
    }
    public void DrawRectangle(RectD r,ColorRgba? f,StrokeStyle? s=null,double cornerRadius=0)=>Add(new("rect",[new(r.X,r.Y),new(r.Right,r.Bottom)],f??s?.Color));
    public void DrawEllipse(RectD r,ColorRgba? f,StrokeStyle? s=null)=>Add(new("ellipse",[new(r.X,r.Y),new(r.Right,r.Bottom)],f??s?.Color));
    public void DrawMarkers(ReadOnlySpan<PointD> p,PointMarker m,double size,ColorRgba f,StrokeStyle? s=null)=>Add(new("markers",p.ToArray(),f));
    public void DrawImage(RasterImage image,RectD r,bool flipX=false,bool flipY=false,bool interpolate=false)=>Add(new("image",[new(r.X,r.Y),new(r.Right,r.Bottom)]));
    public void DrawText(string text,PointD p,TextStyle s,TextAlignment a=TextAlignment.Left,double rotationDegrees=0)=>Add(new("text",[p],s.Color,text));
    public TextMetrics MeasureText(string text,TextStyle s)=>new(text.Length*s.Size*.56,s.Size*1.2,s.Size*.9,s.Size*.3);
    private void Add(RecordedCommand command) { if(command.Points.Any(p=>!p.IsFinite)) throw new InvalidOperationException("Non-finite render coordinates."); Commands.Add(command); }
}
public sealed record RecordedCommand(string Kind,PointD[] Points,ColorRgba? Color=null,string? Text=null);
