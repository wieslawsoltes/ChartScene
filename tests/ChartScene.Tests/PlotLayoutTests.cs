using System.Diagnostics;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using SkiaSharp;

public static class PlotLayoutTests
{
    private static ChartModel Model(bool wide = false)
    {
        var model = new ChartModel { ShowLegend = false, Title = "Signals", Subtitle = "Aligned axes" };
        var data = new XyDataSeries(); data.AppendRange([0,1,2],[1,3,2]);
        model.RenderableSeries.Add(new FastLineRenderableSeries { DataSeries = data });
        model.AutoRangeAxes(); model.XAxis.AutoRange = model.YAxis.AutoRange = AutoRange.Never;
        model.YAxis.TextFormatting = wide ? "0.000000000" : "0.#"; return model;
    }
    public static void Register(TestSuite suite)
    {
        suite.Run("plot margins validate finite nonnegative reservations",()=>
        {
            foreach(double v in new[]{-1,double.NaN,double.PositiveInfinity})
            {
                TestSuite.Throws<ArgumentOutOfRangeException>(()=>new PlotMargins(v,0,0,0));
                TestSuite.Throws<ArgumentOutOfRangeException>(()=>new PlotMargins(0,v,0,0));
                TestSuite.Throws<ArgumentOutOfRangeException>(()=>new PlotMargins(0,0,v,0));
                TestSuite.Throws<ArgumentOutOfRangeException>(()=>new PlotMargins(0,0,0,v));
            }
            TestSuite.Equal(PlotMargins.Max(new(1,8,3,6),new(5,2,7,4)),new PlotMargins(5,8,7,6));
        });
        foreach(var alignment in Enum.GetValues<PlotAlignment>()) suite.Run("plot group alignment "+alignment,()=>
        {
            using var group=new PlotLayoutGroup(alignment); using var a=group.Add();using var b=group.Add();
            a.Report(new(10,20,30,40));b.Report(new(40,30,20,10));
            var expected=new PlotMargins(alignment.HasFlag(PlotAlignment.Horizontal)?40:0,alignment.HasFlag(PlotAlignment.Vertical)?30:0,
                alignment.HasFlag(PlotAlignment.Horizontal)?30:0,alignment.HasFlag(PlotAlignment.Vertical)?40:0);
            TestSuite.Equal(a.MinimumMargins,expected);TestSuite.Equal(b.MinimumMargins,expected);
        });
        suite.Run("plot group shrinking uses natural not already expanded margins",()=>
        {
            using var group=new PlotLayoutGroup();using var a=group.Add();using var b=group.Add();
            a.Report(new(40,10,10,10));b.Report(new(120,10,20,10));TestSuite.Near(a.MinimumMargins.Left,120);
            b.Report(new(25,10,5,10));TestSuite.Equal(a.MinimumMargins,new PlotMargins(40,0,10,0));
            a.Dispose();TestSuite.Equal(b.MinimumMargins,new PlotMargins(25,0,5,0));
        });
        suite.Run("plot group membership and mode changes publish consistent state",()=>
        {
            using var group=new PlotLayoutGroup();using var a=group.Add();a.Report(new(40,30,20,10));
            using var b=group.Add();TestSuite.Equal(a.MinimumMargins,b.MinimumMargins);
            int events=0;EventHandler observer=(_,_)=>{events++;TestSuite.Equal(a.MinimumMargins,b.MinimumMargins);};a.Changed+=observer;
            b.Report(new(60,50,30,20));group.Alignment=PlotAlignment.Both;
            TestSuite.Equal(events,2);TestSuite.Equal(a.MinimumMargins,new PlotMargins(60,50,30,20));a.Changed-=observer;
        });
        suite.Run("plot group reentrant update and detach is not lost",()=>
        {
            using var group=new PlotLayoutGroup();using var a=group.Add();using var b=group.Add();
            bool entered=false;a.Changed+=(_,_)=>{if(entered)return;entered=true;b.Report(new(80,20,30,10));};
            a.Report(new(20,10,10,10));TestSuite.Near(a.MinimumMargins.Left,80);
            a.Changed+=(_,_)=>b.Dispose();a.Report(new(90,10,40,10));TestSuite.Equal(group.Statistics.Members,1);
            TestSuite.Near(a.MinimumMargins.Left,90);
        });
        suite.Run("plot group disposal is idempotent and stops callbacks",()=>
        {
            var group=new PlotLayoutGroup();var member=group.Add();int count=0;member.Changed+=(_,_)=>count++;
            member.Report(new(10,10,10,10));group.Dispose();group.Dispose();member.Dispose();TestSuite.Equal(count,1);
            TestSuite.Throws<ObjectDisposedException>(()=>member.Report(default));
            TestSuite.Throws<ObjectDisposedException>(()=>group.Add());
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>new PlotLayoutGroup((PlotAlignment)8));
        });
        suite.Run("plot group equal reports perform no layout passes or allocations",()=>
        {
            using var group=new PlotLayoutGroup();using var a=group.Add();using var b=group.Add();
            var input=new PlotMargins(80,50,20,40);a.Report(input);b.Report(input);
            for(int i=0;i<50000;i++)a.Report(input);
            var before=group.Statistics;long start=Stopwatch.GetTimestamp(),bytes=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<10000;i++)a.Report(input);
            long allocated=GC.GetAllocatedBytesForCurrentThread()-bytes;double ms=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var after=group.Statistics;TestSuite.Equal(allocated,0L);TestSuite.Equal(after.AggregatePasses,before.AggregatePasses);TestSuite.Equal(after.Notifications,before.Notifications);
            Directory.CreateDirectory("artifacts/performance");
            File.WriteAllText("artifacts/performance/plot-layout.json",JsonSerializer.Serialize(new{reports=10000,allocatedBytes=allocated,aggregatePasses=after.AggregatePasses-before.AggregatePasses,notifications=after.Notifications-before.Notifications,milliseconds=ms,scope="Warm equal natural-margin reports only; excludes measurement, drawing, GPU and presentation."},new JsonSerializerOptions{WriteIndented=true}));
        });
        suite.Run("horizontal group aligns unequal labels without changing models",()=>
        {
            using var a=Model();using var b=Model(true);using var group=new PlotLayoutGroup();using var x=group.Add();using var y=group.Add();
            var canvas=new RecordingCanvas();var naturalA=ChartLayout.Measure(canvas,a,900,500);var naturalB=ChartLayout.Measure(canvas,b,900,500);
            TestSuite.True(naturalB.PlotArea.X>naturalA.PlotArea.X);x.Report(naturalA.NaturalMargins);y.Report(naturalB.NaturalMargins);
            long revA=a.Revision,revB=b.Revision;var alignedA=ChartLayout.Measure(canvas,a,900,500,x.MinimumMargins);var alignedB=ChartLayout.Measure(canvas,b,900,500,y.MinimumMargins);
            TestSuite.Near(alignedA.PlotArea.X,alignedB.PlotArea.X);TestSuite.Near(alignedA.PlotArea.Right,alignedB.PlotArea.Right);
            TestSuite.Equal(alignedA.NaturalMargins,naturalA.NaturalMargins);TestSuite.Equal(a.Revision,revA);TestSuite.Equal(b.Revision,revB);
            var p=alignedA.Projection(a,a.RenderableSeries[0]);var screen=p.ToScreen(1,2);var restored=p.ToData(screen);TestSuite.Near(restored.X,1);TestSuite.Near(restored.Y,2);
        });
        suite.Run("side by side charts align titles and top bottom axes",()=>
        {
            using var a=Model();using var b=Model();b.Subtitle="";b.XAxis.AxisAlignment=AxisAlignment.Top;
            var canvas=new RecordingCanvas();using var group=new PlotLayoutGroup(PlotAlignment.Vertical);using var x=group.Add();using var y=group.Add();
            x.Report(ChartLayout.Measure(canvas,a,600,500).NaturalMargins);y.Report(ChartLayout.Measure(canvas,b,600,500).NaturalMargins);
            var left=ChartLayout.Measure(canvas,a,600,500,x.MinimumMargins);var right=ChartLayout.Measure(canvas,b,600,500,y.MinimumMargins);
            TestSuite.Near(left.PlotArea.Y,right.PlotArea.Y);TestSuite.Near(left.PlotArea.Bottom,right.PlotArea.Bottom);
        });
        suite.Run("multiple right axes shrink independently after removal",()=>
        {
            using var a=Model();using var b=Model();var extra=new NumericAxis{Id="Right",AxisAlignment=AxisAlignment.Right,AxisTitle="Right axis",TextFormatting="0.0000000"};b.YAxes.Add(extra);
            using var group=new PlotLayoutGroup();using var x=group.Add();using var y=group.Add();var canvas=new RecordingCanvas();
            x.Report(ChartLayout.Measure(canvas,a,900,500).NaturalMargins);y.Report(ChartLayout.Measure(canvas,b,900,500).NaturalMargins);double old=x.MinimumMargins.Right;
            b.YAxes.Remove(extra);y.Report(ChartLayout.Measure(canvas,b,900,500).NaturalMargins);TestSuite.True(x.MinimumMargins.Right<old);
            var layout=ChartLayout.Measure(canvas,a,900,500,x.MinimumMargins);
            TestSuite.True(layout.Axes.All(axis=>axis.Bounds.X>=0&&axis.Bounds.Right<=900));
        });
        suite.Run("oversized natural axes and explicit margins stay within the surface",()=>
        {
            using var model=Model();for(int i=0;i<10;i++)model.YAxes.Add(new NumericAxis{Id="Axis"+i,AxisTitle="Long",TextFormatting="0.000000"});
            var layout=ChartLayout.Measure(new RecordingCanvas(),model,200,150);TestSuite.True(layout.PlotArea.IsEmpty);
            TestSuite.True(layout.PlotArea.X<=200&&layout.PlotArea.Y<=150);TestSuite.Equal(layout.Axes.Count,0);
            var explicitLayout=ChartLayout.Measure(new RecordingCanvas(),model,900,500,new(double.MaxValue,double.MaxValue,double.MaxValue,double.MaxValue));
            TestSuite.True(explicitLayout.PlotArea.IsEmpty);TestSuite.True(double.IsFinite(explicitLayout.PlotArea.X));
            var frame=new ChartRenderer().Render(new RecordingCanvas(),model,200,150);TestSuite.Equal(frame.Statistics.RenderedPoints,0L);
        });
        suite.Run("invalid layout sizes diagnosed before geometry",()=>
        {
            using var model=Model();foreach(double invalid in new[]{-1,double.NaN,double.PositiveInfinity})
            {TestSuite.Throws<ArgumentOutOfRangeException>(()=>ChartLayout.Measure(new RecordingCanvas(),model,invalid,500));TestSuite.Throws<ArgumentOutOfRangeException>(()=>ChartLayout.Measure(new RecordingCanvas(),model,900,invalid));}
            TestSuite.True(ChartLayout.Measure(new RecordingCanvas(),model,0,0).PlotArea.IsEmpty);
        });
        suite.Run("noncartesian layouts respect explicit plot margins",()=>
        {
            using var model=Model();foreach(var coordinate in new[]{CoordinateSystem.Polar,CoordinateSystem.Ternary})
            {
                model.CoordinateSystem=coordinate;var layout=ChartLayout.Measure(new RecordingCanvas(),model,900,500,new(100,80,90,70));
                TestSuite.Equal(layout.PlotArea,new RectD(100,80,710,350));TestSuite.Equal(layout.NaturalMargins.Left,42.0);
            }
        });
        suite.Run("Skia cache keys include per-view layout margins",()=>
        {
            using var model=Model();using var cache=new SkiaChartSceneCache();using var surface=SKSurface.Create(new SKImageInfo(900,500));
            var m=new PlotMargins(180,90,70,50);cache.Render(surface.Canvas,model,900,500);
            var first=cache.Render(surface.Canvas,model,900,500,minimumMargins:m);TestSuite.True(!cache.Statistics.LastFrameReusedScene);
            cache.Render(surface.Canvas,model,900,500,minimumMargins:m);TestSuite.True(cache.Statistics.LastFrameReusedScene);
            TestSuite.Near(first.Layout.PlotArea.X,180);cache.Render(surface.Canvas,model,900,500);TestSuite.True(!cache.Statistics.LastFrameReusedScene);
        });
        suite.Run("aligned Skia scene replay matches direct rendering pixel for pixel",()=>
        {
            using var model=Model(true);using var cache=new SkiaChartSceneCache();using var resources=new SkiaResources();
            using var direct=SKSurface.Create(new SKImageInfo(900,500));using var replay=SKSurface.Create(new SKImageInfo(900,500));
            var margins=new PlotMargins(200,80,80,80);var view=new ChartViewState{Cursor=new(300,200)};
            new ChartRenderer().Render(new SkiaChartCanvas(direct.Canvas,resources),model,900,500,view,margins);
            for(int pass=0;pass<2;pass++)cache.Render(replay.Canvas,model,900,500,view,margins);
            using var a=direct.Snapshot();using var b=replay.Snapshot();using var pa=a.PeekPixels();using var pb=b.PeekPixels();
            TestSuite.True(pa.GetPixelSpan().SequenceEqual(pb.GetPixelSpan()));
        });
    }
}
