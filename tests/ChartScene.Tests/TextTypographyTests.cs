using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using SkiaSharp;

public static class TextTypographyTests
{
    private static TextStyle Style(int weight=400,FontSlant slant=FontSlant.Normal)=>
        new(ColorRgba.White,24,"DejaVu Sans"){Weight=weight,Slant=slant};
    private static byte[] Pixels(SKSurface surface)
    {
        using var image=surface.Snapshot();using var bitmap=SKBitmap.FromImage(image);
        return bitmap.Bytes;
    }
    private static ChartModel Model(TextAnnotation annotation)
    {
        var model=new ChartModel{ShowLegend=false};
        model.XAxis.AutoRange=model.YAxis.AutoRange=AutoRange.Never;
        model.XAxis.VisibleRange=model.YAxis.VisibleRange=new(0,10);
        model.Annotations.Add(annotation);return model;
    }
    [MethodImpl(MethodImplOptions.NoInlining|MethodImplOptions.NoOptimization)]
    private static long WarmMeasure(SkiaChartCanvas canvas,TextStyle style)
    {
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)canvas.MeasureText("Warm cached label",style);
        return GC.GetAllocatedBytesForCurrentThread()-before;
    }
    public static void Register(TestSuite suite)
    {
        suite.Run("typography retains legacy TextStyle construction and deconstruction",()=>
        {
            var old=new TextStyle(ColorRgba.White,18,"Arial",true);
            var(color,size,family,bold)=old;
            TestSuite.True(color==ColorRgba.White&&size==18&&family=="Arial"&&bold);
            TestSuite.Equal(old.EffectiveWeight,700);TestSuite.Equal(new TextStyle(ColorRgba.White).EffectiveWeight,400);
            TestSuite.Equal((old with{Weight=500}).EffectiveWeight,500);
        });
        suite.Run("annotation weights and slants validate atomically and notify aliases",()=>
        {
            var a=new TextAnnotation();using var model=Model(a);var events=new List<string?>();
            a.PropertyChanged+=(_,e)=>events.Add(e.PropertyName);long revision=model.Revision;
            foreach(int invalid in new[]{-1,0,1001,int.MaxValue})TestSuite.Throws<ArgumentOutOfRangeException>(()=>a.FontWeight=invalid);
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>a.FontSlant=(FontSlant)99);
            TestSuite.Equal(model.Revision,revision);TestSuite.Equal(events.Count,0);
            a.FontWeight=600;TestSuite.True(!a.FontBold&&events.SequenceEqual(new[]{"FontWeight"}));
            events.Clear();a.FontWeight=800;TestSuite.True(a.FontBold&&events.SequenceEqual(new[]{"FontWeight","FontBold"}));
            events.Clear();revision=model.Revision;a.FontWeight=800;a.FontSlant=FontSlant.Normal;
            TestSuite.Equal(events.Count,0);TestSuite.Equal(model.Revision,revision);
            a.FontBold=false;TestSuite.Equal(a.FontWeight,400);a.FontBold=true;TestSuite.Equal(a.FontWeight,700);
            a.FontSlant=FontSlant.Italic;TestSuite.True(model.Revision>revision);
        });
        foreach(var slant in Enum.GetValues<FontSlant>())foreach(int weight in new[]{100,400,600,900})
            suite.Run($"annotation measures and draws requested typography {weight} {slant}",()=>
            {
                var a=new TextAnnotation{Text="Wide styled label",FontFamily="DejaVu Sans",FontSize=24,FontWeight=weight,FontSlant=slant,X1=2,Y1=3};
                using var model=Model(a);using var surface=SKSurface.Create(new SKImageInfo(600,400));using var resources=new SkiaResources();
                var canvas=new SkiaChartCanvas(surface.Canvas,resources);var expected=canvas.MeasureText(a.Text,Style(weight,slant));
                var layout=TextAnnotationLayout.Measure(canvas,model,a,new(0,0,600,400),new(0,0,600,400))!;
                TestSuite.Near(layout.LocalBounds.Width,expected.Width);TestSuite.Equal(resources.CacheStatistics.Fonts,1);
                layout.Draw(canvas);TestSuite.True(Pixels(surface).Any(v=>v!=0));TestSuite.Equal(resources.CacheStatistics.TextBlobs,1);
            });
        suite.Run("font and blob keys distinguish slants and unify legacy weight aliases",()=>
        {
            using var surface=SKSurface.Create(new SKImageInfo(300,100));using var resources=new SkiaResources();var c=new SkiaChartCanvas(surface.Canvas,resources);
            var legacy=new TextStyle(ColorRgba.White,24,"DejaVu Sans",true);c.MeasureText("same",legacy);
            c.MeasureText("same",Style(700));c.MeasureText("same",Style(700) with{Color=ColorRgba.Black});
            TestSuite.Equal(resources.CacheStatistics.Fonts,1);TestSuite.Equal(resources.CacheStatistics.TextBlobs,1);
            c.MeasureText("same",Style(700,FontSlant.Italic));c.MeasureText("same",Style(700,FontSlant.Oblique));
            c.MeasureText("same",Style(600));TestSuite.Equal(resources.CacheStatistics.Fonts,4);TestSuite.Equal(resources.CacheStatistics.TextBlobs,4);
        });
        suite.Run("italic and bold labels produce different actual raster glyphs",()=>
        {
            using var s=SKSurface.Create(new SKImageInfo(350,90));using var r=new SkiaResources();var c=new SkiaChartCanvas(s.Canvas,r);
            byte[] Render(TextStyle style){s.Canvas.Clear(SKColors.Transparent);c.DrawText("Ag italic 123",new(20,50),style);return Pixels(s);}
            var normal=Render(Style());var italic=Render(Style(400,FontSlant.Italic));var bold=Render(Style(700));
            TestSuite.True(!normal.SequenceEqual(italic)&&!normal.SequenceEqual(bold)&&!italic.SequenceEqual(bold));
        });
        suite.Run("direct canvas line endings have identical metrics pixels and canonical cache keys",()=>
        {
            using var s=SKSurface.Create(new SKImageInfo(350,180));using var r=new SkiaResources();var c=new SkiaChartCanvas(s.Canvas,r);var style=Style();
            var metrics=c.MeasureText("First\n\nLast\n",style);c.DrawText("First\n\nLast\n",new(20,40),style);var reference=Pixels(s);
            foreach(string text in new[]{"First\r\rLast\r","First\r\n\r\nLast\r\n"})
            {
                TestSuite.Equal(c.MeasureText(text,style),metrics);s.Canvas.Clear(SKColors.Transparent);c.DrawText(text,new(20,40),style);
                TestSuite.True(reference.SequenceEqual(Pixels(s)));TestSuite.Equal(r.CacheStatistics.TextBlobs,1);
            }
        });
        suite.Run("empty paragraphs use real face metrics and never draw a hidden sentinel glyph",()=>
        {
            using var s=SKSurface.Create(new SKImageInfo(160,100));using var r=new SkiaResources();var c=new SkiaChartCanvas(s.Canvas,r);var style=Style();
            var empty=c.MeasureText("",style);var full=c.MeasureText("Mg",style);TestSuite.Near(empty.Width,0);
            TestSuite.Near(empty.Height,full.Height);TestSuite.Near(empty.Ascent,full.Ascent);TestSuite.Near(empty.Descent,full.Descent);
            TestSuite.Near(c.MeasureText("\r\n",style).Height,2*empty.Height);
            c.DrawText("\n",new(100030,100040),style);TestSuite.True(Pixels(s).All(v=>v==0));
        });
        suite.Run("invalid typography is rejected before native resources including empty text",()=>
        {
            using var s=SKSurface.Create(new SKImageInfo(10,10));using var r=new SkiaResources();var c=new SkiaChartCanvas(s.Canvas,r);
            foreach(var invalid in new[]{Style() with{Weight=-1},Style() with{Weight=1001},Style() with{Slant=(FontSlant)99},Style() with{Size=double.NaN},Style() with{Size=double.MaxValue}})
                TestSuite.Throws<ArgumentOutOfRangeException>(()=>c.MeasureText("",invalid));
            TestSuite.Throws<ArgumentException>(()=>c.MeasureText("",Style() with{Family=" "}));
            TestSuite.Throws<ArgumentNullException>(()=>c.MeasureText(null!,Style()));
            TestSuite.Equal(r.CacheStatistics.Fonts,0);TestSuite.Equal(r.CacheStatistics.TextBlobs,0);
            r.Dispose();TestSuite.Throws<ObjectDisposedException>(()=>c.MeasureText("",Style()));
        });
        suite.Run("style changes rebuild cached scene and old annotation layouts retain typography",()=>
        {
            var a=new TextAnnotation{Text="Changing face",FontFamily="DejaVu Sans",FontSize=24,X1=2,Y1=3};using var m=Model(a);
            using var s=SKSurface.Create(new SKImageInfo(600,400));using var cache=new SkiaChartSceneCache();
            var frame=cache.Render(s.Canvas,m,600,400);var old=frame.FindTextAnnotation(a)!;var initial=Pixels(s);
            cache.Render(s.Canvas,m,600,400);TestSuite.True(cache.Statistics.LastFrameReusedScene);
            a.FontSlant=FontSlant.Italic;cache.Render(s.Canvas,m,600,400);TestSuite.True(!cache.Statistics.LastFrameReusedScene);
            TestSuite.True(!initial.SequenceEqual(Pixels(s)));a.FontWeight=800;
            cache.Render(s.Canvas,m,600,400);TestSuite.True(!cache.Statistics.LastFrameReusedScene);
            s.Canvas.Clear(SKColors.Transparent);using var resources=new SkiaResources();var c=new SkiaChartCanvas(s.Canvas,resources);old.Draw(c);var preserved=Pixels(s);
            a.FontWeight=400;a.FontSlant=FontSlant.Normal;
            var restored=TextAnnotationLayout.Measure(c,m,a,frame.Layout.PlotArea,new RectD(0,0,600,400))!;
            s.Canvas.Clear(SKColors.Transparent);restored.Draw(c);TestSuite.True(preserved.SequenceEqual(Pixels(s)));
        });
        suite.Run("warm typography measurement reuses native caches without managed allocation",()=>
        {
            using var s=SKSurface.Create(new SKImageInfo(100,100));using var r=new SkiaResources();var c=new SkiaChartCanvas(s.Canvas,r);var style=Style(600,FontSlant.Italic);
            c.MeasureText("Warm cached label",style);WarmMeasure(c,style);var windows=new long[3];long started=Stopwatch.GetTimestamp();
            for(int i=0;i<windows.Length;i++)windows[i]=WarmMeasure(c,style);
            Console.WriteLine("TYPOGRAPHY_ALLOCATION_WINDOWS "+string.Join(",",windows));
            TestSuite.True(windows.All(bytes=>bytes==0));TestSuite.Equal(r.CacheStatistics.Fonts,1);TestSuite.Equal(r.CacheStatistics.TextBlobs,1);
            var report=new{queriesPerWindow=10000,allocatedBytes=windows,elapsedMs=Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                scope="Warm canonical text measurement only. Excludes shaping misses, normalization of CR input, drawing, GPU and presentation."};
            Directory.CreateDirectory("artifacts/performance");File.WriteAllText("artifacts/performance/typography-cache.json",JsonSerializer.Serialize(report));
            Console.WriteLine("PERF_TYPOGRAPHY "+JsonSerializer.Serialize(report));
        });
    }
}
