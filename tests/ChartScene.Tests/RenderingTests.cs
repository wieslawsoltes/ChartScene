using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Rendering;

public static partial class AdditionalTests
{
    static partial void RegisterRendering(TestSuite suite)
    {
        foreach(var kind in Enum.GetValues<SeriesKind>()) suite.Run($"render geometry {kind}",()=>
        {
            using var model=TestModels.Create(kind); var canvas=new RecordingCanvas(); var frame=new ChartRenderer().Render(canvas,model,900,600);
            TestSuite.Equal(frame.Diagnostics.Count,0); TestSuite.True(frame.Statistics.RenderedPoints>0); TestSuite.Equal(canvas.SaveDepth,0);
            TestSuite.True(canvas.Commands.All(command=>command.Points.All(p=>p.IsFinite))); TestSuite.True(frame.Layout.PlotArea.Width>500);
        });
        suite.Run("polygon clip all edges",()=> { var p=PolygonClipper.Clip([new(-10,-10),new(20,-10),new(20,20),new(-10,20)],new(0,0,10,10)); TestSuite.Equal(p.Length,4); TestSuite.True(p.All(new RectD(0,0,10,10).Contains)); });
        suite.Run("polygon clip rejects NaN",()=> { TestSuite.Equal(PolygonClipper.Clip([new(0,0),new(double.NaN,1),new(2,2)],new(0,0,10,10)).Length,0); });
        suite.Run("NaN gaps do not connect",()=>
        {
            using var m=TestModels.Create(SeriesKind.Line); var d=new XyDataSeries(); d.AppendRange([0,1,2,3,4],[0,1,double.NaN,1,0]); m.RenderableSeries[0].DataSeries=d; m.ShowLegend=false;
            var c=new RecordingCanvas(); new ChartRenderer().Render(c,m,800,500);
            var paths=c.Commands.Where(command=>command.Kind=="polyline"&&command.Color==m.RenderableSeries[0].Stroke).ToArray(); TestSuite.Equal(paths.Length,2);
        });
        suite.Run("million point render resamples",()=>
        {
            using var m=new ChartModel(); var d=new XyDataSeries(); double[] x=new double[1_000_000],y=new double[x.Length]; for(int i=0;i<x.Length;i++) { x[i]=i; y[i]=Math.Sin(i*.01); } d.AppendRange(x,y); m.RenderableSeries.Add(new FastLineRenderableSeries{DataSeries=d});
            var f=new ChartRenderer().Render(new RecordingCanvas(),m,1000,600); TestSuite.Equal(f.Statistics.SourcePoints,1_000_000L); TestSuite.True(f.Statistics.RenderedPoints<5000);
        });
        suite.Run("band crossing creates two signed fills",()=>
        {
            using var m=new ChartModel{ShowLegend=false}; var d=new XyyDataSeries(); d.Append(0,0,10); d.Append(1,10,0); var s=new FastBandRenderableSeries{DataSeries=d}; m.RenderableSeries.Add(s);
            var c=new RecordingCanvas(); new ChartRenderer().Render(c,m,800,500); TestSuite.True(c.Commands.Any(v=>v.Kind=="path"&&v.Color==s.Fill)); TestSuite.True(c.Commands.Any(v=>v.Kind=="path"&&v.Color==s.FillY1));
        });
        suite.Run("uniform heatmap uses one raster draw",()=> { using var m=TestModels.Create(SeriesKind.Heatmap); var c=new RecordingCanvas(); new ChartRenderer().Render(c,m,800,500); TestSuite.Equal(c.Commands.Count(v=>v.Kind=="image"),1); });
        suite.Run("nonuniform heatmap retains cells",()=>
        {
            using var m=TestModels.Create(SeriesKind.Heatmap); m.RenderableSeries[0].HeatmapGrid=new([0,1,4],[0,2,3],[1,2,3,4]); var c=new RecordingCanvas(); var f=new ChartRenderer().Render(c,m,800,500); TestSuite.Equal(c.Commands.Count(v=>v.Kind=="image"),0); TestSuite.Equal(f.Statistics.RenderedPoints,4L);
        });
        suite.Run("contour plane topology",()=> { var g=HeatmapGrid.Uniform(3,3,0,1,0,1,(x,y)=>x); var segments=Contours.MarchingSquares(g,[.75]).ToArray(); TestSuite.Equal(segments.Length,2); TestSuite.True(segments.All(s=>Math.Abs(s.A.X-1.25)<1e-12&&Math.Abs(s.B.X-1.25)<1e-12)); });
        suite.Run("contour gaps omitted",()=> { var g=HeatmapGrid.Uniform(2,2,0,1,0,1,(x,y)=>x==0&&y==0?double.NaN:x+y); TestSuite.Equal(Contours.MarchingSquares(g,[1]).Count(),0); });
        suite.Run("annotation relative coordinates inverse",()=>
        {
            using var m=new ChartModel(); var a=new BoxAnnotation {XCoordinateMode=CoordinateMode.Relative,YCoordinateMode=CoordinateMode.Relative}; var p=new RectD(50,40,400,200); var screen=AnnotationGeometry.ToScreen(m,a,p,.25,.75); TestSuite.Equal(screen,new PointD(150,190)); var data=AnnotationGeometry.ToValues(m,a,p,screen); TestSuite.Near(data.X,.25); TestSuite.Near(data.Y,.75);
        });
        foreach(var kind in Enum.GetValues<AnnotationKind>()) suite.Run($"annotation geometry {kind}",()=>
        {
            using var m=TestModels.Create(SeriesKind.Line); var a=new ChartAnnotation(kind){X1=.2,Y1=.2,X2=.8,Y2=.8,XCoordinateMode=CoordinateMode.Relative,YCoordinateMode=CoordinateMode.Relative,Text="reference",IsSelected=true}; a.Points.Add(new(.1,.1)); a.Points.Add(new(.5,.3)); a.Points.Add(new(.8,.7)); m.Annotations.Add(a); var c=new RecordingCanvas(); new ChartRenderer().Render(c,m,800,500); TestSuite.Equal(c.SaveDepth,0);
        });
        suite.Run("multiple axes allocate independent bounds",()=> { using var m=TestModels.Create(SeriesKind.Line); m.YAxes.Add(new NumericAxis{Id="Y2",AxisAlignment=AxisAlignment.Right,AxisTitle="secondary"}); var c=new RecordingCanvas(); var f=new ChartRenderer().Render(c,m,900,600); TestSuite.Equal(f.Layout.Axes.Count,3); TestSuite.True(f.Layout.Axes[2].Bounds.X>=f.Layout.PlotArea.Right); });
        suite.Run("tiny viewport safely skips geometry",()=> { using var m=TestModels.Create(SeriesKind.Line); var f=new ChartRenderer().Render(new RecordingCanvas(),m,1,1); TestSuite.True(f.Layout.PlotArea.IsEmpty); });
        suite.Run("legend retains hidden series toggle bounds",()=> { using var m=TestModels.Create(SeriesKind.Line); m.RenderableSeries[0].IsVisible=false; var f=new ChartRenderer().Render(new RecordingCanvas(),m,900,600); TestSuite.Equal(f.LegendItems.Count,1); });
        suite.Run("invalid model is diagnosed rather than rendered",()=> { using var m=TestModels.Create(SeriesKind.Line); m.RenderableSeries[0].YAxisId="unknown"; var f=new ChartRenderer().Render(new RecordingCanvas(),m,900,600); TestSuite.True(f.Diagnostics.Count>0); });
    }
}
public static class TestModels
{
    public static ChartModel Create(SeriesKind kind)
    {
        var m=new ChartModel {Title=kind.ToString(),Subtitle="Geometry regression"};
        var s=new RenderableSeries(kind){SeriesName=kind.ToString(),Fill=new(88,188,207,90),Stroke=new(88,188,207),StrokeY1=new(233,168,91),FillY1=new(233,168,91,80)};
        if(kind is SeriesKind.Heatmap or SeriesKind.Contour) s.HeatmapGrid=HeatmapGrid.Uniform(12,10,0,1,0,1,(x,y)=>Math.Sin(x*.4)+Math.Cos(y*.3));
        else if(kind is SeriesKind.Candlestick or SeriesKind.Ohlc)
        {
            var d=new OhlcDataSeries(); for(int i=0;i<12;i++) d.Append(i,10+i,14+i,8+i,11+i); s.DataSeries=d;
        }
        else if(kind is SeriesKind.BoxPlot or SeriesKind.GroupedBoxPlot)
        {
            var d=new BoxPlotDataSeries(); for(int i=0;i<8;i++) d.Append(i,1+i,2+i,3+i,4+i,5+i); s.DataSeries=d;
        }
        else if(kind==SeriesKind.VectorField)
        {
            var d=new VectorFieldDataSeries{AcceptsUnsortedData=true}; for(int y=0;y<8;y++) for(int x=0;x<8;x++) d.Append(x,y,Math.Sin(y),Math.Cos(x)); s.DataSeries=d;
        }
        else if(kind==SeriesKind.ErrorBars)
        {
            var d=new DataSeries(4); for(int i=0;i<12;i++) d.Append(i,5+Math.Sin(i),4+Math.Sin(i),6+Math.Sin(i)); s.DataSeries=d;
        }
        else if(kind is SeriesKind.Band or SeriesKind.DigitalBand or SeriesKind.Bubble or SeriesKind.TernaryLine or SeriesKind.TernaryScatter)
        {
            var d=new DataSeries(3); for(int i=0;i<30;i++) d.Append(i+1,20+10*Math.Sin(i*.4),30+10*Math.Cos(i*.3)); s.DataSeries=d;
            if(kind is SeriesKind.TernaryLine or SeriesKind.TernaryScatter) m.CoordinateSystem=CoordinateSystem.Ternary;
        }
        else
        {
            var d=new XyDataSeries(); int count=kind is SeriesKind.Pie or SeriesKind.Donut?5:100;
            for(int i=0;i<count;i++) d.Append(kind is SeriesKind.PolarLine or SeriesKind.Radar?i*360.0/(count-1):i,3+2*Math.Sin(i*.13)); s.DataSeries=d;
            if(kind is SeriesKind.PolarLine or SeriesKind.Radar) m.CoordinateSystem=CoordinateSystem.Polar;
        }
        m.RenderableSeries.Add(s); return m;
    }
}
