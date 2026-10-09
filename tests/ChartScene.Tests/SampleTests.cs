using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using ChartScene.Samples;

public static class SampleTests
{
    public static void Register(TestSuite suite)
    {
        suite.Run("sample catalog initialized and uniquely named",()=>
        {
            TestSuite.True(SampleCatalog.Examples.Count>=40);
            TestSuite.Equal(SampleCatalog.Examples.Select(e=>e.Id).Distinct().Count(),SampleCatalog.Examples.Count);
        });
        foreach(var example in SampleCatalog.Examples) suite.Run($"application sample {example.Id}",()=>
        {
            using var model=example.Create();
            var frame=new ChartRenderer().Render(new RecordingCanvas(),model,1000,600);
            TestSuite.Equal(frame.Diagnostics.Count,0); TestSuite.True(frame.Statistics.RenderedPoints>0);
        });
        suite.Run("horizontal bars fit category and value independently",()=>
        {
            using var model=SampleCatalog.Find("bar").Create(); model.ZoomExtents();
            TestSuite.True(model.YAxis.VisibleRange.Contains(0)&&model.YAxis.VisibleRange.Contains(11));
            TestSuite.True(model.XAxis.VisibleRange.Max<12&&model.XAxis.VisibleRange.Contains(7));
            var frame=new ChartRenderer().Render(new RecordingCanvas(),model,900,600);
            TestSuite.Equal(frame.HitRegions.Count,36);
        });
        suite.Run("stacked horizontal bars fit all signed totals",()=>
        {
            using var model=SampleCatalog.Find("stackedbar").Create(); model.ZoomExtents();
            var stacks=StackCalculator.Calculate(model);
            foreach(var stack in stacks.Values) foreach(double y in stack.Upper) TestSuite.True(model.XAxis.VisibleRange.Contains(y));
            TestSuite.True(model.YAxis.VisibleRange.Contains(11));
        });
        suite.Run("Y-only fitting preserves X viewport",()=>
        {
            using var model=new ChartModel(); var data=new XyDataSeries(); data.AppendRange([0,1,2,3],[100,1,2,200]); model.RenderableSeries.Add(new FastLineRenderableSeries{DataSeries=data});
            model.XAxis.VisibleRange=new(.5,2.5); model.ZoomExtents(true);
            TestSuite.Equal(model.XAxis.VisibleRange,new DoubleRange(.5,2.5)); TestSuite.True(model.YAxis.VisibleRange.Max<3);
        });
        suite.Run("explicit sample viewport retained by auto-ranging",()=>
        {
            using var model=SampleCatalog.Find("fft").Create(); model.AutoRangeAxes(); TestSuite.Equal(model.XAxis.VisibleRange,new DoubleRange(0,300));
        });
    }
}
