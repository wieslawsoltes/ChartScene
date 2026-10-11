using System.Diagnostics;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using ChartScene.Samples;
using SkiaSharp;

public static class SceneCacheTests
{
    private static SKSurface Surface(int width=640,int height=400)=>SKSurface.Create(new SKImageInfo(width,height,SKColorType.Rgba8888,SKAlphaType.Premul));
    private static byte[] Pixels(SKSurface surface)
    { using var image=surface.Snapshot(); using var pixmap=image.PeekPixels(); return pixmap.GetPixelSpan().ToArray(); }
    private static ChartModel Model()
    {
        var m=new ChartModel{ShowLegend=false}; var d=new XyDataSeries(); d.AppendRange([0,1,2],[1,3,2]);
        m.RenderableSeries.Add(new FastLineRenderableSeries{DataSeries=d}); m.AutoRangeAxes(); return m;
    }
    public static void Register(TestSuite suite)
    {
        foreach(var example in SampleCatalog.Examples) suite.Run("scene replay pixel equivalence "+example.Id,()=>
        {
            using var model=example.Create(); model.AutoRangeAxes();
            using var cache=new SkiaChartSceneCache(); using var direct=Surface(); using var replay=Surface();
            using var resources=new SkiaResources(); var renderer=new ChartRenderer();
            var frame=renderer.Render(new SkiaChartCanvas(direct.Canvas,resources),model,640,400);
            var view=new ChartViewState{Cursor=frame.Layout.PlotArea.Center};
            // Compare both initial recording and warmed replay, including the transient overlay.
            for(int pass=0;pass<3;pass++)
            {
                renderer.Render(new SkiaChartCanvas(direct.Canvas,resources),model,640,400,view);
                cache.Render(replay.Canvas,model,640,400,view);
                if(!Pixels(direct).AsSpan().SequenceEqual(Pixels(replay))) throw new Exception($"Display-list replay changed pixels for {example.Id}, pass {pass}.");
            }
        });
        suite.Run("scene cache retains vector fidelity at different host scales",()=>
        {
            using var model=Model(); using var cache=new SkiaChartSceneCache(); using var resources=new SkiaResources();
            var renderer=new ChartRenderer();
            foreach(float scale in new[]{1f,1.5f,2f})
            {
                using var direct=Surface((int)(640*scale),(int)(400*scale)); using var replay=Surface((int)(640*scale),(int)(400*scale));
                direct.Canvas.Scale(scale); replay.Canvas.Scale(scale);
                renderer.Render(new SkiaChartCanvas(direct.Canvas,resources),model,640,400);
                cache.Render(replay.Canvas,model,640,400);
                TestSuite.True(Pixels(direct).AsSpan().SequenceEqual(Pixels(replay)));
            }
            TestSuite.Equal(cache.Statistics.SceneBuilds,1L); TestSuite.Equal(cache.Statistics.SceneReplays,2L);
        });
        suite.Run("scene recording invalidates on changed model inputs",()=>
        {
            using var model=Model(); using var cache=new SkiaChartSceneCache(); using var surface=Surface();
            void Draw()=>cache.Render(surface.Canvas,model,640,400);
            Draw(); Draw(); TestSuite.True(cache.Statistics.LastFrameReusedScene);
            void Changes(Action action)
            { long before=cache.Statistics.SceneBuilds; action(); Draw(); TestSuite.Equal(cache.Statistics.SceneBuilds,before+1); }
            Changes(()=>((XyDataSeries)model.RenderableSeries[0].DataSeries!).Update(1,1,4));
            Changes(()=>model.RenderableSeries[0].Stroke=ColorRgba.White);
            Changes(()=>model.XAxis.VisibleRange=new(0,4));
            Changes(()=>model.YAxis.TextFormatting="0.00");
            Changes(()=>model.Theme=ChartTheme.Light);
            var annotation=new BoxAnnotation{X1=.25,X2=.5,Y1=1,Y2=2};
            Changes(()=>model.Annotations.Add(annotation));
            Changes(()=>annotation.XCoordinateMode=CoordinateMode.Relative);
            Changes(()=>annotation.Layer=AnnotationLayer.BelowSeries);
            Changes(()=>annotation.ClipToPlot=false);
            Changes(()=>annotation.DashArray=[2,3]);
            Changes(()=>annotation.ShowLabel=false);
            Changes(()=>model.XAxis.ZoomExtentsRange=new(0,5));
            Changes(()=>model.XAxis.Labels=["one","two"]);
            long builds=cache.Statistics.SceneBuilds; cache.Render(surface.Canvas,model,630,400);
            TestSuite.Equal(cache.Statistics.SceneBuilds,builds+1);
            using var replacement=Model(); cache.Render(surface.Canvas,replacement,630,400);
            TestSuite.Equal(cache.Statistics.SceneBuilds,builds+2);
        });
        suite.Run("scene replay does not retain old cursor or rubber band",()=>
        {
            using var model=Model(); using var cache=new SkiaChartSceneCache(); using var direct=Surface(); using var replay=Surface(); using var resources=new SkiaResources();
            var renderer=new ChartRenderer(); var first=cache.Render(replay.Canvas,model,640,400);
            var p=first.Layout.PlotArea;
            foreach(var view in new[]{new ChartViewState{Cursor=p.Center},new ChartViewState{RubberBand=new(p.X+10,p.Y+10,50,50)},new ChartViewState()})
            {
                cache.Render(replay.Canvas,model,640,400,view);
                renderer.Render(new SkiaChartCanvas(direct.Canvas,resources),model,640,400,view);
                TestSuite.True(Pixels(direct).AsSpan().SequenceEqual(Pixels(replay)));
            }
            TestSuite.Equal(cache.Statistics.SceneBuilds,1L);
        });
        suite.Run("scene cache bypasses dynamic providers and selected indices",()=>
        {
            using var model=Model(); using var cache=new SkiaChartSceneCache(); using var surface=Surface();
            void CheckBypass()
            { cache.Render(surface.Canvas,model,640,400); cache.Render(surface.Canvas,model,640,400); TestSuite.True(!cache.Statistics.LastFrameReusedScene); }
            model.RenderableSeries[0].PaletteProvider=new ThresholdPaletteProvider(0,ColorRgba.Black,ColorRgba.White); CheckBypass();
            model.RenderableSeries[0].PaletteProvider=null; model.RenderableSeries[0].DataLabelProvider=new NumericDataLabelProvider(); CheckBypass();
            model.RenderableSeries[0].DataLabelProvider=null; model.XAxis.LabelProvider=new Labels(); CheckBypass();
            model.XAxis.LabelProvider=null; model.RenderableSeries[0].SelectedIndices.Add(1); CheckBypass();
        });
        suite.Run("scene cache observes in-place palette and identity changes",()=>
        {
            using var model=Model(); var colors=model.Theme.Palette.ToArray(); model.Theme=model.Theme with{Palette=colors};
            using var cache=new SkiaChartSceneCache(); using var surface=Surface();
            cache.Render(surface.Canvas,model,640,400); colors[0]=ColorRgba.White;
            cache.Render(surface.Canvas,model,640,400); TestSuite.True(!cache.Statistics.LastFrameReusedScene);
            model.RenderableSeries[0].Id="new-id"; cache.Render(surface.Canvas,model,640,400); TestSuite.True(!cache.Statistics.LastFrameReusedScene);
        });
        suite.Run("scene cache cleanup and error paths preserve host state",()=>
        {
            using var model=Model(); var cache=new SkiaChartSceneCache(); using var surface=Surface();
            int saves=surface.Canvas.SaveCount;
            cache.Render(surface.Canvas,model,640,400); cache.Invalidate();
            cache.Render(surface.Canvas,model,640,400); TestSuite.Equal(cache.Statistics.SceneBuilds,2L);
            TestSuite.Equal(surface.Canvas.SaveCount,saves);
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>cache.Render(surface.Canvas,model,double.NaN,400));
            cache.Dispose();cache.Dispose();TestSuite.Throws<ObjectDisposedException>(()=>cache.Render(surface.Canvas,model,640,400));
            // Disposing the cache never disposes the caller's canvas/surface.
            surface.Canvas.Clear(SKColors.Red); TestSuite.Equal(surface.Canvas.SaveCount,saves);
        });
        suite.Run("axis and annotation input lists cannot bypass invalidation",()=>
        {
            using var model=Model();var annotation=new LineAnnotation();model.Annotations.Add(annotation);
            string[] labels=["A"];model.XAxis.Labels=labels; labels[0]="B";TestSuite.Equal(model.XAxis.Labels[0],"A");
            double[] dash=[2,3];annotation.DashArray=dash;dash[0]=99;var returned=annotation.DashArray; returned[0]=99;TestSuite.Near(annotation.DashArray[0],2);
            long before=model.Revision;annotation.DashArray=[2,3];TestSuite.Equal(model.Revision,before);
            TestSuite.Throws<ArgumentException>(()=>annotation.DashArray=[-1,2]);
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>annotation.Layer=(AnnotationLayer)99);
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>model.XAxis.ZoomExtentsRange=default(DoubleRange));
            TestSuite.Equal(model.Revision,before);
        });
        suite.Run("scene replay CPU and allocation evidence",Benchmark);
    }
    private sealed class Labels:IAxisLabelProvider
    { public string Format(double value,AxisModel axis,bool cursor=false)=>value.ToString("G4"); }
    private static void Benchmark()
    {
        using var model=SampleCatalog.Find("million").Create();model.AutoRangeAxes();
        using var cache=new SkiaChartSceneCache();using var resources=new SkiaResources();using var surface=Surface(1000,600);
        var renderer=new ChartRenderer();var view=new ChartViewState();
        var frame=cache.Render(surface.Canvas,model,1000,600,view);
        for(int i=0;i<5;i++) { cache.Render(surface.Canvas,model,1000,600,view);renderer.Render(new SkiaChartCanvas(surface.Canvas,resources),model,1000,600,view); }
        double[] full=new double[21],replay=new double[21];long fullBytes=0,replayBytes=0;var timer=new Stopwatch();
        for(int i=0;i<full.Length;i++)
        {
            view.Cursor=new(frame.Layout.PlotArea.X+20+i*10,frame.Layout.PlotArea.Center.Y);
            long start=GC.GetAllocatedBytesForCurrentThread();timer.Restart();renderer.Render(new SkiaChartCanvas(surface.Canvas,resources),model,1000,600,view);
            full[i]=timer.Elapsed.TotalMilliseconds;fullBytes+=GC.GetAllocatedBytesForCurrentThread()-start;
            start=GC.GetAllocatedBytesForCurrentThread();timer.Restart();cache.Render(surface.Canvas,model,1000,600,view);
            replay[i]=timer.Elapsed.TotalMilliseconds;replayBytes+=GC.GetAllocatedBytesForCurrentThread()-start;
        }
        TestSuite.Equal(cache.Statistics.SceneBuilds,1L);Array.Sort(full);Array.Sort(replay);
        var report=new{sourcePoints=1000000,iterations=21,fullP50Ms=full[10],replayP50Ms=replay[10],
            fullManagedBytesPerFrame=fullBytes/21,replayManagedBytesPerFrame=replayBytes/21,cache.Statistics,
            scope="CPU Skia raster fixture with moving cursor. Both paths include raster drawing; full path already uses indexed resampling. No GPU/FPS/presentation claim."};
        Directory.CreateDirectory("artifacts/performance");File.WriteAllText("artifacts/performance/scene-replay.json",JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PERF_SCENE "+JsonSerializer.Serialize(report));
        TestSuite.True(replayBytes<fullBytes/2);
    }
}
