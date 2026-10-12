using System.Text.Json;
using System.Runtime.CompilerServices;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Interaction;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using SkiaSharp;

public static class TextAnnotationTests
{
    private static ChartModel Model(bool logarithmic=false)
    {
        var m=new ChartModel{ShowLegend=false};
        m.XAxis.AutoRange=m.YAxis.AutoRange=AutoRange.Never;
        m.XAxis.VisibleRange=m.YAxis.VisibleRange=logarithmic?new(1,100):new(0,10);
        if(logarithmic)m.XAxis.Scale=m.YAxis.Scale=AxisScale.Logarithmic;
        return m;
    }
    private static TextAnnotation Label()=>new(){Text="Wide label\nshort",X1=.5,Y1=.5,XCoordinateMode=CoordinateMode.Relative,YCoordinateMode=CoordinateMode.Relative,FontSize=20,TextPadding=5};
    private static TextAnnotationLayout Measure(ChartModel m,ChartAnnotation a)=>TextAnnotationLayout.Measure(new RecordingCanvas(),m,a,new(100,80,600,400),new(0,0,900,600))!;
    private static void Point(PointD actual,PointD expected){TestSuite.Near(actual.X,expected.X);TestSuite.Near(actual.Y,expected.Y);}
    [MethodImpl(MethodImplOptions.NoInlining|MethodImplOptions.NoOptimization)]
    private static long MeasureHits(TextAnnotationLayout layout,PointD point)
    {
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)if(layout.HitDistance(point)!=0)throw new Exception("Body missed");
        return GC.GetAllocatedBytesForCurrentThread()-before;
    }
    public static void Register(TestSuite suite)
    {
        foreach(var h in Enum.GetValues<HorizontalAnchorPoint>())foreach(var v in Enum.GetValues<VerticalAnchorPoint>())
            suite.Run($"text annotation measured anchors {h} {v}",()=>
            {
                using var m=Model();var a=Label();a.HorizontalAnchorPoint=h;a.VerticalAnchorPoint=v;
                var layout=Measure(m,a);var box=layout.LocalBounds;
                TestSuite.Near(box.Width,10*20*.56+10);TestSuite.Near(box.Height,48+10);
                TestSuite.Near(box.X,h==HorizontalAnchorPoint.Left?0:h==HorizontalAnchorPoint.Center?-box.Width/2:-box.Width);
                TestSuite.Near(box.Y,v==VerticalAnchorPoint.Top?0:v==VerticalAnchorPoint.Center?-box.Height/2:v==VerticalAnchorPoint.Bottom?-box.Height:-23);
                Point(layout.Anchor,new(400,280));
                TestSuite.Near(layout.HitDistance(layout.ToScreen(box.Center)),0);
            });
        foreach(double angle in new[]{-137.0,-90,0,30,90,179})
            suite.Run("text annotation rotation inverse and precise hit region "+angle,()=>
            {
                using var m=Model();var a=Label();a.HorizontalAnchorPoint=HorizontalAnchorPoint.Center;a.VerticalAnchorPoint=VerticalAnchorPoint.Center;a.RotationAngle=angle;
                var layout=Measure(m,a);var local=layout.LocalBounds;
                foreach(var p in new[]{local.Center,new PointD(local.X+1,local.Y+1),new PointD(local.Right-1,local.Bottom-1)})
                {Point(layout.ToLocal(layout.ToScreen(p)),p);TestSuite.Near(layout.HitDistance(layout.ToScreen(p)),0);}
                TestSuite.Near(layout.HitDistance(layout.ToScreen(new(local.Right+10,local.Center.Y))),10);
                var c=new RecordingCanvas();layout.Draw(c,true);TestSuite.Equal(c.SaveDepth,0);
                TestSuite.Equal(c.Commands.Count(x=>x.Kind=="text"),2);TestSuite.Equal(c.Commands.Count(x=>x.Kind=="rect"),4);
            });
        foreach(var alignment in Enum.GetValues<AnnotationTextAlignment>())
            suite.Run("multiline text alignment includes blank and CRLF lines "+alignment,()=>
            {
                using var m=Model();var a=Label();a.Text="123456\r\n\rX\n";a.LineSpacing=1.5;a.TextAlignment=alignment;
                var layout=Measure(m,a);TestSuite.Equal(layout.Lines.Count,4);
                TestSuite.True(layout.Lines.Select(l=>l.Text).SequenceEqual(new[]{"123456","","X",""}));
                double offset=alignment==AnnotationTextAlignment.Left?0:alignment==AnnotationTextAlignment.Center?28:56;
                TestSuite.Near(layout.Lines[2].Baseline.X-layout.Lines[0].Baseline.X,offset);
                TestSuite.Near(layout.Lines[3].Baseline.Y-layout.Lines[0].Baseline.Y,108);
            });
        suite.Run("typed text defaults to top left without breaking base baseline labels",()=>
        {
            using var m=Model();var typed=Label();typed.TextPadding=0;
            TestSuite.Equal(typed.VerticalAnchorPoint,VerticalAnchorPoint.Top);
            var legacy=new ChartAnnotation(AnnotationKind.Text){Text="Legacy",X1=.5,Y1=.5,XCoordinateMode=CoordinateMode.Relative,YCoordinateMode=CoordinateMode.Relative};
            Point(Measure(m,legacy).Lines[0].Baseline,Measure(m,legacy).Anchor);
        });
        suite.Run("text only uses its anchor on logarithmic axes",()=>
        {
            using var m=Model(true);var a=new TextAnnotation{Text="Visible anchor",X1=10,Y1=10,X2=0,Y2=0};m.Annotations.Add(a);
            var canvas=new RecordingCanvas();var frame=new ChartRenderer().Render(canvas,m,900,600);
            TestSuite.Equal(frame.TextAnnotations.Count,1);TestSuite.True(canvas.Commands.Any(c=>c.Text==a.Text));
        });
        suite.Run("text hit region rejects clipped pixels and respects surface clip",()=>
        {
            using var m=Model();var a=Label();a.X1=-.03;var layout=Measure(m,a);
            TestSuite.True(double.IsPositiveInfinity(layout.HitDistance(new(99,300))));
            a.ClipToPlot=false;layout=Measure(m,a);TestSuite.Near(layout.HitDistance(layout.ToScreen(layout.LocalBounds.Center)),0);
            TestSuite.True(double.IsPositiveInfinity(layout.HitDistance(new(-1,300))));
        });
        suite.Run("text invalid style assignments are atomic and observable",()=>
        {
            using var m=Model();var a=Label();m.Annotations.Add(a);long before=m.Revision;
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>a.RotationAngle=double.NaN);
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>a.TextPadding=-1);
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>a.LineSpacing=.5);
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>a.BorderThickness=double.PositiveInfinity);
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>a.HorizontalAnchorPoint=(HorizontalAnchorPoint)99);
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>a.VerticalAnchorPoint=(VerticalAnchorPoint)99);
            TestSuite.Throws<ArgumentException>(()=>a.FontFamily="  ");TestSuite.Equal(m.Revision,before);
            a.RotationAngle=390;TestSuite.Near(a.RotationAngle,30);before=m.Revision;a.RotationAngle=30;TestSuite.Equal(m.Revision,before);
            a.FontBold=true;TestSuite.True(m.Revision>before);
        });
        suite.Run("empty or off-domain text does not publish stale hit geometry",()=>
        {
            using var m=Model(true);var a=new TextAnnotation{Text="Empty",X1=10,Y1=10};m.Annotations.Add(a);var renderer=new ChartRenderer();
            TestSuite.Equal(renderer.Render(new RecordingCanvas(),m,900,600).TextAnnotations.Count,1);
            a.Text="";TestSuite.Equal(renderer.Render(new RecordingCanvas(),m,900,600).TextAnnotations.Count,0);
            a.Text="Invalid log anchor";a.X1=0;TestSuite.Equal(renderer.Render(new RecordingCanvas(),m,900,600).TextAnnotations.Count,0);
        });
        foreach(bool rotated in new[]{false,true})foreach(bool flipped in new[]{false,true})
            suite.Run($"painted text body drag and history rotated={rotated} flipped={flipped}",()=>
            {
                using var m=Model();if(rotated){m.XAxis.AxisAlignment=AxisAlignment.Left;m.YAxis.AxisAlignment=AxisAlignment.Bottom;}
                m.YAxis.FlippedCoordinates=flipped;var a=Label();a.RotationAngle=30;m.Annotations.Add(a);
                using var c=new ChartController(m);c.SetFrame(new ChartRenderer().Render(new RecordingCanvas(),m,900,600));
                var text=c.Frame!.TextAnnotations.Single().Layout;var start=text.ToScreen(new(text.LocalBounds.Right-8,text.LocalBounds.Center.Y));
                var delta=new PointD(20,14);var wanted=AnnotationGeometry.ToValues(m,a,c.Frame.Layout.PlotArea,text.Anchor+delta);
                c.Process(new(ChartInputKind.Down,start));TestSuite.True(a.IsSelected&&c.HasCapture);
                c.Process(new(ChartInputKind.Move,start+delta));c.Process(new(ChartInputKind.Up,start+delta));
                TestSuite.Near(a.X1,wanted.X);TestSuite.Near(a.Y1,wanted.Y);c.History.Undo();TestSuite.Near(a.X1,.5);TestSuite.Near(a.Y1,.5);
            });
        suite.Run("text double click requests its native editor instead of zoom extents",()=>
        {
            using var m=Model();var a=Label();m.Annotations.Add(a);using var c=new ChartController(m);
            c.SetFrame(new ChartRenderer().Render(new RecordingCanvas(),m,900,600));int edits=0;c.AnnotationEditRequested+=(_,item)=>{TestSuite.True(ReferenceEquals(item,a));edits++;};
            var layout=c.Frame!.TextAnnotations[0].Layout;var body=layout.ToScreen(layout.LocalBounds.Center);
            c.Process(new(ChartInputKind.Down,body));c.Process(new(ChartInputKind.Up,body));TestSuite.True(!c.History.CanUndo);
            c.Process(new(ChartInputKind.DoubleClick,body));
            TestSuite.Equal(edits,1);TestSuite.True(!c.History.CanUndo);TestSuite.Equal(m.XAxis.VisibleRange,new DoubleRange(0,10));
        });
        suite.Run("text hit regions are frame owned and refuse stale content",()=>
        {
            using var m=Model();var a=Label();m.Annotations.Add(a);var renderer=new ChartRenderer();
            var old=renderer.Render(new RecordingCanvas(),m,900,600);var body=old.TextAnnotations[0].Layout.Bounds.Center;
            a.Text="new";var next=renderer.Render(new RecordingCanvas(),m,900,600);
            TestSuite.True(!ReferenceEquals(old.TextAnnotations,next.TextAnnotations));TestSuite.Equal(old.TextAnnotations[0].Layout.Lines[0].Text,"Wide label");
            TestSuite.True(double.IsPositiveInfinity(AnnotationGeometry.HitDistance(m,a,old.Layout.PlotArea,body,old)));
            TestSuite.Throws<NotSupportedException>(()=>((IList<AnnotationTextRegion>)old.TextAnnotations).Clear());
        });
        suite.Run("text drawing preserves host state with background and border",()=>
        {
            using var m=Model();var a=Label();a.Background=ColorRgba.White;a.BorderColor=ColorRgba.Black;a.BorderThickness=1;m.Annotations.Add(a);
            using var surface=SKSurface.Create(new SKImageInfo(900,600));using var resources=new SkiaResources();
            int saves=surface.Canvas.SaveCount;var f=new ChartRenderer().Render(new SkiaChartCanvas(surface.Canvas,resources),m,900,600);
            TestSuite.Equal(surface.Canvas.SaveCount,saves);TestSuite.Equal(f.TextAnnotations.Count,1);
            using var image=surface.Snapshot();using var pixels=SKBitmap.FromImage(image);var center=f.TextAnnotations[0].Layout.Bounds.Center;
            TestSuite.True(pixels.GetPixel((int)center.X,(int)center.Y).Alpha>0);
        });
        suite.Run("text scene replay reuses measured geometry and invalidates typography",()=>
        {
            using var m=Model();var a=Label();a.FontBold=true;a.RotationAngle=30;a.Background=new(70,40,90);m.Annotations.Add(a);
            using var cache=new SkiaChartSceneCache();using var surface=SKSurface.Create(new SKImageInfo(900,600));
            var first=cache.Render(surface.Canvas,m,900,600);var view=new ChartViewState();
            var layout=first.TextAnnotations[0].Layout;var position=layout.ToScreen(layout.LocalBounds.Center);
            MeasureHits(layout,position); // exclude one-time JIT/tier instrumentation from the warmed interval
            long bytes=0;
            for(int pass=0;pass<3;pass++){long measured=MeasureHits(layout,position);TestSuite.Equal(measured,0L);bytes+=measured;}
            for(int i=0;i<40;i++)
            {
                view.Cursor=new(100+i*3,200);var replay=cache.Render(surface.Canvas,m,900,600,view);
                TestSuite.True(ReferenceEquals(first.TextAnnotations,replay.TextAnnotations));
            }
            TestSuite.Equal(cache.Statistics.SceneBuilds,1L);TestSuite.Equal(cache.Statistics.SceneReplays,40L);
            a.FontSize=28;var next=cache.Render(surface.Canvas,m,900,600);TestSuite.Equal(cache.Statistics.SceneBuilds,2L);
            TestSuite.True(next.TextAnnotations[0].Layout.Bounds.Width>layout.Bounds.Width);
            Directory.CreateDirectory("artifacts/performance");
            File.WriteAllText("artifacts/performance/text-annotations.json",JsonSerializer.Serialize(new{hitTests=30000,measuredWindows=3,hitTestBytes=bytes,unchangedSceneReplays=40,measurementRebuildsBeforeTypographyChange=1,
                scope="Cached measured-body hits and unchanged CPU Skia scene reuse only; excludes initial measurement, changed layout, host UI and GPU/presentation."}));
        });
    }
}
