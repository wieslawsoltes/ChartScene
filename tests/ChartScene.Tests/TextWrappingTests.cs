using System.Globalization;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using SkiaSharp;

public static class TextWrappingTests
{
    private static TextAnnotation Label(string text,AnnotationTextWrapping wrap=AnnotationTextWrapping.Wrap,double width=70)=>
        new(){Text=text,TextWrapping=wrap,TextWrapWidth=width,FontSize=20,X1=.25,Y1=.25,
            XCoordinateMode=CoordinateMode.Relative,YCoordinateMode=CoordinateMode.Relative};
    private static ChartModel Model(TextAnnotation a)
    {
        var m=new ChartModel{ShowLegend=false};m.XAxis.AutoRange=m.YAxis.AutoRange=AutoRange.Never;
        m.XAxis.VisibleRange=m.YAxis.VisibleRange=new(0,10);m.Annotations.Add(a);return m;
    }
    private static TextAnnotationLayout? Measure(IChartCanvas c,ChartModel m,ChartAnnotation a)=>
        TextAnnotationLayout.Measure(c,m,a,new(0,0,1000,800),new(0,0,1200,1000));
    private static void Lines(TextAnnotationLayout? layout,params string[] expected)=>
        TestSuite.True(layout is not null&&layout.Lines.Select(l=>l.Text).SequenceEqual(expected));
    public static void Register(TestSuite suite)
    {
        suite.Run("annotation wrap properties validate atomically and invalidate once",()=>
        {
            var a=Label("text");using var m=Model(a);long revision=m.Revision;int events=0;
            a.PropertyChanged+=(_,_)=>events++;
            foreach(double bad in new[]{0d,-1,double.NaN,double.PositiveInfinity,double.NegativeInfinity})
                TestSuite.Throws<ArgumentOutOfRangeException>(()=>a.TextWrapWidth=bad);
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>a.TextWrapping=(AnnotationTextWrapping)42);
            TestSuite.Equal(events,0);TestSuite.Equal(m.Revision,revision);
            a.TextWrapWidth=70;a.TextWrapping=AnnotationTextWrapping.Wrap;TestSuite.Equal(events,0);
            a.TextWrapWidth=100;TestSuite.Equal(events,1);TestSuite.True(m.Revision>revision);
        });
        suite.Run("annotation no wrap preserves hard breaks and ignores soft width",()=>
        {
            var a=Label("one two\r\n\rthree\n",AnnotationTextWrapping.NoWrap,1);using var m=Model(a);
            var c=new MeasureCanvas();var l=Measure(c,m,a);Lines(l,"one two","","three","");
            TestSuite.Near(l!.LocalBounds.Width,70);TestSuite.Equal(c.Calls,4);
        });
        suite.Run("annotation word wrapping uses measured width without changing source",()=>
        {
            var a=Label("one two three");using var m=Model(a);long revision=m.Revision;
            var l=Measure(new MeasureCanvas(),m,a);Lines(l,"one two","three");
            TestSuite.Near(l!.LocalBounds.Width,70);TestSuite.Equal(a.Text,"one two three");TestSuite.Equal(m.Revision,revision);
        });
        suite.Run("annotation wrap breaks a long word and always makes progress",()=>
        {
            var a=Label("abcdefghij",width:30);using var m=Model(a);
            Lines(Measure(new MeasureCanvas(),m,a),"abc","def","ghi","j");
            a.TextWrapWidth=1;var l=Measure(new MeasureCanvas(),m,a);TestSuite.Equal(l!.Lines.Count,10);
            TestSuite.Near(l.LocalBounds.Width,10);
        });
        suite.Run("annotation overflow wrap retains an indivisible word",()=>
        {
            var a=Label("supercalifragilistic tiny word",AnnotationTextWrapping.WrapWithOverflow,40);using var m=Model(a);
            var l=Measure(new MeasureCanvas(),m,a);Lines(l,"supercalifragilistic","tiny","word");
            TestSuite.Near(l!.LocalBounds.Width,190);
        });
        foreach(string cluster in new[]{"e\u0301","\U0001F680","\U0001F1F5\U0001F1F1","\U0001F469\u200D\U0001F4BB","\u0E01\u0E33"})
            suite.Run("annotation soft wrap preserves grapheme "+cluster,()=>
            {
                var a=Label(cluster+cluster+cluster+"x",width:10);using var m=Model(a);
                Lines(Measure(new MeasureCanvas(),m,a),cluster,cluster,cluster,"x");
            });
        suite.Run("annotation soft wrapping preserves blank hard paragraphs",()=>
        {
            var a=Label("one two\r\n\rthree\n",width:30);using var m=Model(a);
            Lines(Measure(new MeasureCanvas(),m,a),"one","two","","thr","ee","");
            TestSuite.Equal(a.Text,"one two\r\n\rthree\n");
        });
        foreach(char separator in new[]{'\u00A0','\u2007','\u202F'})
            suite.Run("annotation no break space stays in overflow word "+(int)separator,()=>
            {
                string word="one"+separator+"two";var a=Label(word+" three",AnnotationTextWrapping.WrapWithOverflow,30);using var m=Model(a);
                Lines(Measure(new MeasureCanvas(),m,a),word,"three");
            });
        suite.Run("annotation soft separators do not erase explicit indentation",()=>
        {
            var a=Label("  one   two\n  three",width:70);using var m=Model(a);
            Lines(Measure(new MeasureCanvas(),m,a),"  one","two","  three");
        });
        suite.Run("annotation wrapped blank and empty content have explicit geometry",()=>
        {
            var a=Label("\n");using var m=Model(a);var l=Measure(new MeasureCanvas(),m,a);
            Lines(l,"","");TestSuite.Near(l!.LocalBounds.Width,70);a.Text="";TestSuite.True(Measure(new MeasureCanvas(),m,a) is null);
        });
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,-1d})
            suite.Run("annotation wrapping rejects invalid measured widths "+bad,()=>
            {
                var a=Label("one two three");using var m=Model(a);
                TestSuite.True(Measure(new MeasureCanvas(_=>new(bad,20,15,5)),m,a) is null);
            });
        foreach(var h in Enum.GetValues<HorizontalAnchorPoint>())
            suite.Run("annotation wrapped alignment rotation and hit geometry "+h,()=>
            {
                var a=Label("one two three");a.HorizontalAnchorPoint=h;a.VerticalAnchorPoint=VerticalAnchorPoint.Center;
                a.TextAlignment=AnnotationTextAlignment.Right;a.TextPadding=5;a.RotationAngle=37;using var m=Model(a);
                var c=new MeasureCanvas();var l=Measure(c,m,a)!;TestSuite.Near(l.LocalBounds.Width,80);
                TestSuite.Near(l.LocalBounds.Height,50);TestSuite.Near(l.HitDistance(l.ToScreen(l.LocalBounds.Center)),0);
                TestSuite.Near(l.ToLocal(l.Lines[1].Baseline).X,l.LocalBounds.X+5+20);
                l.Draw(c);TestSuite.True(c.Recorder.Commands.Where(x=>x.Kind=="text").Select(x=>x.Text).SequenceEqual(new[]{"one two","three"}));
                TestSuite.True(double.IsPositiveInfinity(l.HitDistance(new(-10,-10))));
            });
        suite.Run("annotation soft break remeasures contextual shaping without overflowing",()=>
        {
            var a=Label("abc def",width:30);using var m=Model(a);
            var c=new MeasureCanvas(text=>new(text switch{"abc "=>28,"abc"=>40,_=>text.Length*10},20,15,5));
            var l=Measure(c,m,a)!;Lines(l,"abc ","def");TestSuite.Near(l.LocalBounds.Width,30);
        });
        suite.Run("annotation invalid prefix measurement never publishes partial wrapped geometry",()=>
        {
            var a=Label("one two three",width:30);using var m=Model(a);
            var c=new MeasureCanvas(text=>new(text=="on"?double.NaN:text.Length*10,20,15,5));
            TestSuite.True(Measure(c,m,a) is null);TestSuite.Equal(a.Text,"one two three");
        });
        suite.Run("annotation wrapped layout owns immutable width and rendered lines",()=>
        {
            var a=Label("one two three");using var m=Model(a);var c=new MeasureCanvas();var old=Measure(c,m,a)!;
            a.TextWrapWidth=30;a.Text="changed";a.TextWrapping=AnnotationTextWrapping.NoWrap;
            Lines(old,"one two","three");TestSuite.Near(old.LocalBounds.Width,70);old.Draw(c);
            TestSuite.True(c.Recorder.Commands.Where(x=>x.Kind=="text").Select(x=>x.Text).SequenceEqual(new[]{"one two","three"}));
        });
        suite.Run("actual Skia wrapped text shares shaped measurements and visible pixels",()=>
        {
            var a=Label("Measured annotation words wrapped across lines",width:130);a.FontFamily="DejaVu Sans";a.TextPadding=4;
            using var m=Model(a);using var s=SKSurface.Create(new SKImageInfo(1200,1000));using var r=new SkiaResources();
            var c=new SkiaChartCanvas(s.Canvas,r);var l=Measure(c,m,a)!;TestSuite.True(l.Lines.Count>1);
            var style=new TextStyle(a.Stroke,a.FontSize,a.FontFamily){Weight=a.FontWeight,Slant=a.FontSlant};
            foreach(var line in l.Lines)TestSuite.True(c.MeasureText(line.Text,style).Width<=130);
            TestSuite.Near(l.LocalBounds.Width,138);l.Draw(c);
            using var image=s.Snapshot();using var bitmap=SKBitmap.FromImage(image);TestSuite.True(bitmap.Bytes.Any(b=>b!=0));
        });
        suite.Run("wrapped annotation scene replay invalidates on width and mode",()=>
        {
            var a=Label("A long annotation that is wrapped and retained",width:180);using var m=Model(a);
            using var s=SKSurface.Create(new SKImageInfo(800,600));using var cache=new SkiaChartSceneCache();
            var first=cache.Render(s.Canvas,m,800,600);var again=cache.Render(s.Canvas,m,800,600);
            TestSuite.True(cache.Statistics.LastFrameReusedScene&&ReferenceEquals(first.FindTextAnnotation(a),again.FindTextAnnotation(a)));
            a.TextWrapWidth=90;var narrow=cache.Render(s.Canvas,m,800,600);TestSuite.True(!cache.Statistics.LastFrameReusedScene);
            TestSuite.True(narrow.FindTextAnnotation(a)!.Lines.Count>first.FindTextAnnotation(a)!.Lines.Count);
            a.TextWrapping=AnnotationTextWrapping.NoWrap;var natural=cache.Render(s.Canvas,m,800,600);
            TestSuite.True(!cache.Statistics.LastFrameReusedScene);TestSuite.Equal(natural.FindTextAnnotation(a)!.Lines.Count,1);
        });
        suite.Run("narrow wrapping bounds shaped prefix work for a long unbroken paragraph",()=>
        {
            const int length=8192;var a=Label(new string('x',length),width:80);using var m=Model(a);var c=new MeasureCanvas();
            var l=Measure(c,m,a)!;TestSuite.Equal(l.Lines.Count,length/8);
            TestSuite.True(c.MeasuredCharacters<length*16L);TestSuite.True(c.Calls<l.Lines.Count*12);
            Directory.CreateDirectory("artifacts/performance");
            File.WriteAllText("artifacts/performance/annotation-wrap.json",JsonSerializer.Serialize(new{characters=length,lines=l.Lines.Count,
                measureCalls=c.Calls,measuredCharacters=c.MeasuredCharacters,scope="Cold wrapping query work with deterministic grapheme metrics, not timing, allocations or GPU performance"}));
        });
    }
    private sealed class MeasureCanvas(Func<string,TextMetrics>? measure=null):IChartCanvas
    {
        public RecordingCanvas Recorder { get; }=new();
        public long MeasuredCharacters { get; private set; }
        public int Calls { get; private set; }
        public TextMetrics MeasureText(string t,TextStyle s){Calls++;MeasuredCharacters+=t.Length;return measure?.Invoke(t)??new(StringInfo.ParseCombiningCharacters(t).Length*10,20,15,5);}
        public void Save()=>Recorder.Save();public void Restore()=>Recorder.Restore();
        public void Clip(RectD r)=>Recorder.Clip(r);public void Clear(ColorRgba c)=>Recorder.Clear(c);
        public void DrawLine(PointD a,PointD b,StrokeStyle s)=>Recorder.DrawLine(a,b,s);
        public void DrawPolyline(ReadOnlySpan<PointD> p,StrokeStyle s,bool closed=false)=>Recorder.DrawPolyline(p,s,closed);
        public void DrawPath(VectorPath p,ColorRgba? f,StrokeStyle? s=null)=>Recorder.DrawPath(p,f,s);
        public void DrawRectangle(RectD r,ColorRgba? f,StrokeStyle? s=null,double radius=0)=>Recorder.DrawRectangle(r,f,s,radius);
        public void DrawEllipse(RectD r,ColorRgba? f,StrokeStyle? s=null)=>Recorder.DrawEllipse(r,f,s);
        public void DrawMarkers(ReadOnlySpan<PointD> p,PointMarker m,double size,ColorRgba f,StrokeStyle? s=null)=>Recorder.DrawMarkers(p,m,size,f,s);
        public void DrawImage(RasterImage i,RectD r,bool x=false,bool y=false,bool interpolate=false)=>Recorder.DrawImage(i,r,x,y,interpolate);
        public void DrawText(string t,PointD p,TextStyle s,TextAlignment a=TextAlignment.Left,double angle=0)=>Recorder.DrawText(t,p,s,a,angle);
    }
}
