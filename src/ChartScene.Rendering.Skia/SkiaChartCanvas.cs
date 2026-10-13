using System.Runtime.InteropServices;
using ChartScene.Core;
using ChartScene.Charting;
using ChartScene.Rendering;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace ChartScene.Rendering.Skia;

public readonly record struct SkiaCacheStatistics(int Fonts,int TextBlobs,int Images,int Fills,int Strokes);

/// <summary>View-owned cache. Pruning occurs between frames, never while a drawing call holds a resource.</summary>
public sealed class SkiaResources:IDisposable
{
    internal sealed record FontEntry(SKTypeface Typeface,SKFont Font,SKShaper Shaper,bool OwnsTypeface):IDisposable
    { public void Dispose() { Shaper.Dispose(); Font.Dispose(); if(OwnsTypeface) Typeface.Dispose(); } }
    internal sealed record TextEntry(SKTextBlob? Blob,TextMetrics Metrics):IDisposable { public void Dispose()=>Blob?.Dispose(); }
    private readonly Dictionary<ColorRgba,SKPaint> _fills=[];
    private readonly Dictionary<(ColorRgba,double,string,SKStrokeCap),SKPaint> _strokes=[];
    private readonly Dictionary<(string,double,int,FontSlant),FontEntry> _fonts=[];
    private readonly Dictionary<(string,string,double,int,FontSlant),TextEntry> _text=[];
    private readonly Dictionary<RasterImage,SKImage> _images=[];
    private bool _disposed;
    public SkiaCacheStatistics CacheStatistics=>new(_fonts.Count,_text.Count,_images.Count,_fills.Count,_strokes.Count);
    public void BeginFrame()
    {
        Check();
        if(_fills.Count>512) Clear(_fills);
        if(_strokes.Count>512) Clear(_strokes);
        // Text blobs are cleared before their font/shaper entries. Variable-size labels cannot
        // retain an unbounded sequence of native fonts over the lifetime of an animated view.
        if(_fonts.Count>64) { Clear(_text); Clear(_fonts); }
        else if(_text.Count>2048) Clear(_text);
        if(_images.Count>16) Clear(_images);
    }
    private void Check()=>ObjectDisposedException.ThrowIf(_disposed,this);
    internal SKPaint Fill(ColorRgba color)
    {
        Check();
        if(!_fills.TryGetValue(color,out var paint)) _fills[color]=paint=new(){Color=Color(color),IsAntialias=true,Style=SKPaintStyle.Fill};
        return paint;
    }
    internal SKPaint Stroke(StrokeStyle style,SKStrokeCap cap=SKStrokeCap.Butt)
    {
        Check();
        if(!float.IsFinite((float)style.Width)||style.Width<=0) throw new ArgumentOutOfRangeException(nameof(style),"Stroke width must be finite and positive.");
        string dash=style.Dash is {Length:>0}?string.Join(",",style.Dash.Select(v=>v.ToString("R",System.Globalization.CultureInfo.InvariantCulture))):"";
        var key=(style.Color,style.Width,dash,cap);
        if(!_strokes.TryGetValue(key,out var paint))
        {
            paint=new(){Color=Color(style.Color),IsAntialias=true,Style=SKPaintStyle.Stroke,StrokeWidth=(float)style.Width,StrokeCap=cap,StrokeJoin=SKStrokeJoin.Round};
            if(style.Dash is {Length:>0} values&&values.All(v=>float.IsFinite((float)v)&&v>0))
            {
                float[] intervals=values.Length%2==0?values.Select(v=>(float)v).ToArray():values.Concat(values).Select(v=>(float)v).ToArray();
                using var effect=SKPathEffect.CreateDash(intervals,0); paint.PathEffect=effect;
            }
            _strokes[key]=paint;
        }
        return paint;
    }
    internal FontEntry Font(TextStyle style)
    {
        Check();
        ValidateTextStyle(style);
        var key=(style.Family,style.Size,style.EffectiveWeight,style.Slant);
        if(!_fonts.TryGetValue(key,out var result))
        {
            using var nativeStyle=new SKFontStyle(style.EffectiveWeight,(int)SKFontStyleWidth.Normal,style.Slant switch
            {FontSlant.Italic=>SKFontStyleSlant.Italic,FontSlant.Oblique=>SKFontStyleSlant.Oblique,_=>SKFontStyleSlant.Upright});
            var typeface=SKTypeface.FromFamilyName(style.Family,nativeStyle);
            bool owns=typeface is not null; typeface??=SKTypeface.Default;
            var font=new SKFont(typeface,(float)style.Size){Edging=SKFontEdging.SubpixelAntialias,Subpixel=true};
            _fonts[key]=result=new(typeface,font,new SKShaper(typeface),owns);
        }
        return result;
    }
    internal TextEntry Text(string text,TextStyle style)
    {
        Check();
        ArgumentNullException.ThrowIfNull(text);
        ValidateTextStyle(style);
        // CR, LF and CRLF are the same hard break, also for non-annotation canvas consumers.
        // Normalize before keying so equivalent inputs share one shaped blob.
        text=AnnotationTextContent.NormalizeLineEndings(text);
        var key=(text,style.Family,style.Size,style.EffectiveWeight,style.Slant);
        if(_text.TryGetValue(key,out var cached)) return cached;
        var entry=Font(style); var metrics=entry.Font.Metrics;
        double ascent=-metrics.Ascent,descent=metrics.Descent,lineHeight=Math.Max(style.Size,metrics.Descent-metrics.Ascent+metrics.Leading),width=0;
        string[] lines=text.Split('\n');
        using var builder=new SKTextBlobBuilder();
        for(int line=0;line<lines.Length;line++)
        {
            if(lines[line].Length==0) continue;
            var shaped=entry.Shaper.Shape(lines[line],entry.Font);
            width=Math.Max(width,shaped.Width);
            // No capturing LINQ lambdas here: their display class would be allocated
            // at method entry even on a warm cache hit before this miss-only branch.
            var glyphs=new ushort[shaped.Codepoints.Length];var positions=new SKPoint[glyphs.Length];
            for(int i=0;i<glyphs.Length;i++)
            {
                glyphs[i]=(ushort)shaped.Codepoints[i];
                positions[i]=new(shaped.Points[i].X,shaped.Points[i].Y+(float)(line*lineHeight));
            }
            var run=builder.AllocatePositionedRun(entry.Font,glyphs.Length); run.SetGlyphs(glyphs); run.SetPositions(positions);
        }
        var blob=builder.Build();
        // An empty paragraph has font metrics but no ink. Never emit a sentinel glyph:
        // a host transform could bring that supposedly offscreen glyph back into view.
        return _text[key]=new(blob,new(width,lineHeight*lines.Length,ascent,descent));
    }
    private static void ValidateTextStyle(TextStyle style)
    {
        if(!float.IsFinite((float)style.Size)||style.Size<=0) throw new ArgumentOutOfRangeException(nameof(style),"Font size must be finite and positive.");
        if(string.IsNullOrWhiteSpace(style.Family)) throw new ArgumentException("A font family is required.",nameof(style));
        if(style.Weight is <0 or >1000 || !Enum.IsDefined(style.Slant)) throw new ArgumentOutOfRangeException(nameof(style),"Invalid font weight or slant.");
    }
    internal SKImage Image(RasterImage image)
    {
        Check(); ArgumentNullException.ThrowIfNull(image);
        if(_images.TryGetValue(image,out var cached)) return cached;
        using var bitmap=new SKBitmap(new SKImageInfo(image.Width,image.Height,SKColorType.Rgba8888,SKAlphaType.Unpremul));
        Marshal.Copy(image.Pixels.ToArray(),0,bitmap.GetPixels(),image.Pixels.Length);
        var result=SKImage.FromBitmap(bitmap); _images[image]=result; return result;
    }
    internal static SKColor Color(ColorRgba color)=>new(color.R,color.G,color.B,color.A);
    private static void Clear<TKey,TValue>(Dictionary<TKey,TValue> cache) where TKey:notnull where TValue:IDisposable
    { foreach(var item in cache.Values) item.Dispose(); cache.Clear(); }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true;
        Clear(_text); Clear(_images); Clear(_fills); Clear(_strokes); Clear(_fonts);
    }
}

/// <summary>Draws directly into the host's SKCanvas. It never creates a competing surface or disposes the host.</summary>
public sealed class SkiaChartCanvas(SKCanvas canvas,SkiaResources resources):IChartCanvas
{
    private static SKPoint Point(PointD p)=>new((float)p.X,(float)p.Y);
    private static SKRect Rect(RectD r)=>new((float)r.X,(float)r.Y,(float)r.Right,(float)r.Bottom);
    public void Save()=>canvas.Save();
    public void Restore()=>canvas.Restore();
    public void Clip(RectD rectangle)=>canvas.ClipRect(Rect(rectangle),SKClipOperation.Intersect,true);
    public void Clear(ColorRgba color)=>canvas.Clear(SkiaResources.Color(color));
    public void DrawLine(PointD a,PointD b,StrokeStyle stroke)
    { if(a.IsFinite&&b.IsFinite&&stroke.Width>0) canvas.DrawLine(Point(a),Point(b),resources.Stroke(stroke)); }
    public void DrawPolyline(ReadOnlySpan<PointD> points,StrokeStyle stroke,bool closed=false)
    {
        if(points.Length<2||stroke.Width<=0) return;
        using var path=new SKPath(); path.MoveTo(Point(points[0]));
        for(int i=1;i<points.Length;i++) path.LineTo(Point(points[i]));
        if(closed) path.Close(); canvas.DrawPath(path,resources.Stroke(stroke));
    }
    public void DrawPath(VectorPath vector,ColorRgba? fill,StrokeStyle? stroke=null)
    {
        using var path=new SKPath();
        foreach(var command in vector.Commands)
        {
            switch(command.Verb)
            {
                case PathVerb.Move:path.MoveTo(Point(command.A));break;
                case PathVerb.Line:path.LineTo(Point(command.A));break;
                case PathVerb.Cubic:path.CubicTo(Point(command.A),Point(command.B),Point(command.C));break;
                case PathVerb.Close:path.Close();break;
            }
        }
        Paint(path,fill,stroke);
    }
    private void Paint(SKPath path,ColorRgba? fill,StrokeStyle? stroke)
    {
        if(fill is {A:>0} color) canvas.DrawPath(path,resources.Fill(color));
        if(stroke is {Width:>0} line) canvas.DrawPath(path,resources.Stroke(line));
    }
    public void DrawRectangle(RectD rectangle,ColorRgba? fill,StrokeStyle? stroke=null,double cornerRadius=0)
    {
        var rect=Rect(rectangle);
        if(fill is {A:>0} color) { if(cornerRadius>0) canvas.DrawRoundRect(rect,(float)cornerRadius,(float)cornerRadius,resources.Fill(color)); else canvas.DrawRect(rect,resources.Fill(color)); }
        if(stroke is {Width:>0} line) { if(cornerRadius>0) canvas.DrawRoundRect(rect,(float)cornerRadius,(float)cornerRadius,resources.Stroke(line)); else canvas.DrawRect(rect,resources.Stroke(line)); }
    }
    public void DrawEllipse(RectD rectangle,ColorRgba? fill,StrokeStyle? stroke=null)
    {
        if(fill is {A:>0} color) canvas.DrawOval(Rect(rectangle),resources.Fill(color));
        if(stroke is {Width:>0} line) canvas.DrawOval(Rect(rectangle),resources.Stroke(line));
    }
    public void DrawMarkers(ReadOnlySpan<PointD> points,PointMarker marker,double size,ColorRgba fill,StrokeStyle? stroke=null)
    {
        if(points.Length==0||marker==PointMarker.None||size<=0) return;
        if(marker is PointMarker.Ellipse or PointMarker.Square)
        {
            var pixels=new SKPoint[points.Length]; for(int i=0;i<points.Length;i++) pixels[i]=Point(points[i]);
            var cap=marker==PointMarker.Ellipse?SKStrokeCap.Round:SKStrokeCap.Square;
            if(stroke is {Width:>0} line) canvas.DrawPoints(SKPointMode.Points,pixels,resources.Stroke(new(line.Color,size+2*line.Width),cap));
            canvas.DrawPoints(SKPointMode.Points,pixels,resources.Stroke(new(fill,size),cap)); return;
        }
        using var path=new SKPath(); float half=(float)(size/2);
        foreach(var point in points)
        {
            float x=(float)point.X,y=(float)point.Y;
            switch(marker)
            {
                case PointMarker.Triangle:path.MoveTo(x,y-half);path.LineTo(x+half,y+half);path.LineTo(x-half,y+half);path.Close();break;
                case PointMarker.Diamond:path.MoveTo(x,y-half);path.LineTo(x+half,y);path.LineTo(x,y+half);path.LineTo(x-half,y);path.Close();break;
                case PointMarker.Cross:path.MoveTo(x-half,y-half);path.LineTo(x+half,y+half);path.MoveTo(x-half,y+half);path.LineTo(x+half,y-half);break;
                case PointMarker.Plus:path.MoveTo(x-half,y);path.LineTo(x+half,y);path.MoveTo(x,y-half);path.LineTo(x,y+half);break;
            }
        }
        if(marker is PointMarker.Cross or PointMarker.Plus) canvas.DrawPath(path,resources.Stroke(stroke??new(fill,Math.Max(1,size/5))));
        else Paint(path,fill,stroke);
    }
    public void DrawImage(RasterImage image,RectD destination,bool flipX=false,bool flipY=false,bool interpolate=false)
    {
        var native=resources.Image(image);
        canvas.Save();
        try
        {
            canvas.Translate((float)destination.Center.X,(float)destination.Center.Y); canvas.Scale(flipX?-1:1,flipY?-1:1);
            var rect=new SKRect((float)(-destination.Width/2),(float)(-destination.Height/2),(float)(destination.Width/2),(float)(destination.Height/2));
            canvas.DrawImage(native,rect,new SKSamplingOptions(interpolate?SKFilterMode.Linear:SKFilterMode.Nearest,SKMipmapMode.None));
        }
        finally { canvas.Restore(); }
    }
    public void DrawText(string text,PointD baseline,TextStyle style,TextAlignment alignment=TextAlignment.Left,double rotationDegrees=0)
    {
        if(string.IsNullOrEmpty(text)||!baseline.IsFinite) return;
        var shaped=resources.Text(text,style);
        if(shaped.Blob is null) return;
        double offset=alignment==TextAlignment.Center?-shaped.Metrics.Width/2:alignment==TextAlignment.Right?-shaped.Metrics.Width:0;
        canvas.Save();
        try
        {
            canvas.Translate((float)baseline.X,(float)baseline.Y); if(rotationDegrees!=0) canvas.RotateDegrees((float)rotationDegrees);
            canvas.DrawText(shaped.Blob,(float)offset,0,resources.Fill(style.Color));
        }
        finally { canvas.Restore(); }
    }
    public TextMetrics MeasureText(string text,TextStyle style)=>resources.Text(text,style).Metrics;
}

public static class RasterExporter
{
    /// <summary>Off-screen export is deliberately CPU rasterized, independent of the interactive GPU host.</summary>
    public static byte[] ExportPng(ChartModel model,int width,int height,double scale=1)
    {
        if(width<1||height<1||!double.IsFinite(scale)||scale<=0||width*scale>16384||height*scale>16384) throw new ArgumentOutOfRangeException(nameof(width));
        using var surface=SKSurface.Create(new SKImageInfo(checked((int)Math.Ceiling(width*scale)),checked((int)Math.Ceiling(height*scale)),SKColorType.Rgba8888,SKAlphaType.Premul))??throw new InvalidOperationException("Skia could not allocate the export surface.");
        surface.Canvas.Scale((float)scale); using var resources=new SkiaResources(); resources.BeginFrame();
        new ChartRenderer().Render(new SkiaChartCanvas(surface.Canvas,resources),model,width,height);
        using var image=surface.Snapshot(); using var data=image.Encode(SKEncodedImageFormat.Png,100); return data.ToArray();
    }
}
