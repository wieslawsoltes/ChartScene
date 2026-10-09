using System.Numerics;
using System.Text.Json;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Numerics;

var suite=new TestSuite();
suite.Run("range validation",()=> { TestSuite.Throws<ArgumentOutOfRangeException>(()=>new DoubleRange(1,1)); TestSuite.Throws<ArgumentOutOfRangeException>(()=>new DoubleRange(double.NaN,1)); TestSuite.Near(new DoubleRange(-1,1).Zoom(.5).Size,1); });
suite.Run("range degenerate expansion",()=> { foreach(double x in new[]{0.0,1,-1,1e20,-1e20}) TestSuite.True(DoubleRange.FromExtents(x,x).Contains(x)); });
suite.Run("range inverse",()=> { var r=new DoubleRange(-1e5,1e8); for(int i=0;i<100;i++) TestSuite.Near(r.Fraction(r.At(i/100.0)),i/100.0); });
suite.Run("color roundtrip",()=> { TestSuite.Equal(ColorRgba.Parse("#1234AB80"),new ColorRgba(18,52,171,128)); TestSuite.Equal(ColorRgba.Lerp(ColorRgba.Black,ColorRgba.White,0),ColorRgba.Black); });
suite.Run("line clipping",()=> { var a=new PointD(-100,5); var b=new PointD(100,5); TestSuite.True(Geometry.ClipLine(new(0,0,10,10),ref a,ref b)); TestSuite.Near(a.X,0); TestSuite.Near(b.X,10); });
suite.Run("line clipping outside",()=> { var a=new PointD(-100,-5); var b=new PointD(100,-5); TestSuite.True(!Geometry.ClipLine(new(0,0,10,10),ref a,ref b)); });
suite.Run("segment projection",()=> { TestSuite.Near(Geometry.DistanceToSegment(new(5,4),new(0,0),new(10,0),out double t),4); TestSuite.Near(t,.5); });
suite.Run("append and cached snapshot",()=> { var d=new XyDataSeries(); d.Append(1,2); var a=d.Snapshot(); TestSuite.True(ReferenceEquals(a,d.Snapshot())); d.Append(2,3); TestSuite.Equal(a.Count,1); TestSuite.Equal(d.Count,2); TestSuite.True(!ReferenceEquals(a,d.Snapshot())); });
suite.Run("snapshot public input isolated",()=> { double[][] c=[[1,2],[3,4]]; var s=new SeriesSnapshot(c,1,true); c[0][0]=100; TestSuite.Near(s[0,0],1); });
suite.Run("append range atomic validation",()=> { var d=new XyDataSeries(); d.Append(1,1); TestSuite.Throws<ArgumentException>(()=>d.AppendRange([2,3],[2,double.PositiveInfinity])); TestSuite.Equal(d.Count,1); TestSuite.Throws<ArgumentException>(()=>d.AppendRange([3,2],[3,2])); TestSuite.Equal(d.Count,1); });
suite.Run("nested suspended notifications",()=> { var d=new XyDataSeries(); int events=0; d.DataSeriesChanged+=(_,_)=>events++; using(d.SuspendUpdates()) { d.Append(1,1); using(d.SuspendUpdates()) d.Append(2,2); TestSuite.Equal(events,0); } TestSuite.Equal(events,1); });
suite.Run("suspension disposal idempotent",()=> { var d=new XyDataSeries(); var scope=d.SuspendUpdates(); d.Append(1,2); scope.Dispose(); scope.Dispose(); d.Append(2,3); TestSuite.Equal(d.Count,2); });
suite.Run("FIFO wrap and shrink",()=> { var d=new XyDataSeries { FifoCapacity=3 }; for(int i=0;i<20;i++) d.Append(i,i*2); var s=d.Snapshot(); TestSuite.Equal(s.Count,3); TestSuite.Near(s[0,0],17); TestSuite.Near(s[1,2],38); d.FifoCapacity=2; TestSuite.Near(d.Snapshot()[0,0],18); d.FifoCapacity=0; d.Append(20,40); TestSuite.Equal(d.Count,3); });
suite.Run("FIFO bulk greater than capacity",()=> { var d=new XyDataSeries {FifoCapacity=5}; d.AppendRange(Enumerable.Range(0,100).Select(i=>(double)i).ToArray(),Enumerable.Range(0,100).Select(i=>(double)-i).ToArray()); TestSuite.Equal(d.Count,5); TestSuite.Near(d.Snapshot()[0,0],95); });
suite.Run("random FIFO model",()=> { var rng=new Random(123); var d=new XyDataSeries { FifoCapacity=17 }; var expected=new List<double>(); for(int i=0;i<1000;i++) { d.Append(i,i); expected.Add(i); while(expected.Count>17) expected.RemoveAt(0); if(rng.Next(7)==0) { int n=rng.Next(expected.Count+1); d.RemoveRange(0,n); expected.RemoveRange(0,n); } var s=d.Snapshot(); TestSuite.Equal(s.Count,expected.Count); for(int j=0;j<s.Count;j++) TestSuite.Near(s[0,j],expected[j]); } });
suite.Run("update ordering and version",()=> { var d=new XyDataSeries(); d.AppendRange([1,2,3],[1,2,3]); long rev=d.Revision; TestSuite.Throws<ArgumentException>(()=>d.Update(1,4,9)); TestSuite.Equal(d.Revision,rev); d.Update(1,2,9); TestSuite.Near(d.Snapshot()[1,1],9); });
suite.Run("insert and remove",()=> { var d=new XyDataSeries(); d.AppendRange([0,4],[0,4]); d.InsertRange(1,[1,2,3],[2,4,6]); TestSuite.Equal(d.Count,5); d.RemoveRange(1,3); TestSuite.Near(d.Snapshot()[0,1],4); });
suite.Run("unsorted data",()=> { var d=new XyDataSeries {AcceptsUnsortedData=true}; d.AppendRange([3,1,2],[1,2,3]); TestSuite.True(!d.Snapshot().IsSortedAscending); TestSuite.Throws<InvalidOperationException>(()=>d.Snapshot().LowerBound(2)); TestSuite.Throws<InvalidOperationException>(()=>d.AcceptsUnsortedData=false); });
suite.Run("concurrent appends",()=> { var d=new XyDataSeries {AcceptsUnsortedData=true}; Parallel.For(0,1000,i=>d.Append(i,-i)); var s=d.Snapshot(); TestSuite.Equal(s.Count,1000); for(int i=0;i<s.Count;i++) TestSuite.Near(s[0,i],-s[1,i]); });
suite.Run("binary visible bounds",()=> { var d=new XyDataSeries(); d.AppendRange([0,1,1,2,3,4],[0,1,2,3,4,5]); var s=d.Snapshot(); TestSuite.Equal(s.LowerBound(1),1); TestSuite.Equal(s.UpperBound(1),3); TestSuite.Equal(s.VisibleIndices(new(1,2),false),(1,4)); });
suite.Run("NaN extent exclusion",()=> { var d=new XyDataSeries(); d.AppendRange([0,1,2],[double.NaN,-3,5]); TestSuite.Equal(d.Snapshot().Extents(1)!.Value,new DoubleRange(-3,5)); });
suite.Run("typed coordinates",()=> { var d=new XyDataSeries<DateTimeOffset,double>(); d.Append(DateTimeOffset.UnixEpoch,3); TestSuite.Near(d.Snapshot()[0,0],0); });
suite.Run("OHLC invariants",()=> { var d=new OhlcDataSeries(); d.Append(0,10,12,8,11); TestSuite.Throws<ArgumentException>(()=>d.Append(1,10,9,8,11)); TestSuite.Equal(d.Count,1); });
suite.Run("box plot invariants",()=> { var d=new BoxPlotDataSeries(); d.Append(0,1,2,3,4,5); TestSuite.Throws<ArgumentException>(()=>d.Append(1,1,4,3,2,5)); });
suite.Run("heatmap dimensions and isolation",()=> { var h=HeatmapGrid.Uniform(3,2,0,1,0,2,(x,y)=>x+y*3); TestSuite.Near(h[2,1],5); TestSuite.Near(h.YEdges.Span[2],4); TestSuite.Throws<ArgumentException>(()=>new HeatmapGrid([0,0],[0,1],[1])); });
suite.Run("minmax preserves spikes and gaps",()=> { var d=new XyDataSeries(); var x=Enumerable.Range(0,10000).Select(i=>(double)i).ToArray(); var y=new double[10000]; y[5123]=1000; y[5124]=-1000; y[6000]=double.NaN; d.AppendRange(x,y); int[] selected=Resampling.MinMax(d.Snapshot(),1,new(0,9999),100); TestSuite.True(selected.Contains(5123)&&selected.Contains(5124)&&selected.Contains(6000)); TestSuite.True(selected.SequenceEqual(selected.Order())); TestSuite.Equal(selected[0],0); TestSuite.Equal(selected[^1],9999); TestSuite.True(selected.Length<500); });
suite.Run("LTTB endpoints and order",()=> { var d=new XyDataSeries(); for(int i=0;i<1000;i++) d.Append(i,Math.Sin(i*.01)); var indices=Resampling.Lttb(d.Snapshot(),1,50); TestSuite.Equal(indices.Length,50); TestSuite.Equal(indices[0],0); TestSuite.Equal(indices[^1],999); TestSuite.True(indices.Distinct().Count()==indices.Length); TestSuite.True(indices.SequenceEqual(indices.Order())); });
suite.Run("statistics stable mean variance",()=> { var s=Statistics.Describe([1,2,3,4,5,double.NaN]); TestSuite.Equal(s.Count,5); TestSuite.Near(s.Mean,3); TestSuite.Near(s.Variance,2.5); TestSuite.Near(Statistics.Quantile([1,2,3,4],.5),2.5); });
suite.Run("regression",()=> { var(slope,intercept,r2)=Statistics.LinearRegression([1,2,3,4],[3,5,7,9]); TestSuite.Near(slope,2); TestSuite.Near(intercept,1); TestSuite.Near(r2,1); });
suite.Run("SMA gaps recover",()=> { var v=Indicators.SimpleMovingAverage([1,2,3,double.NaN,4,5,6],3); TestSuite.Near(v[2],2); TestSuite.True(double.IsNaN(v[3])&&double.IsNaN(v[5])); TestSuite.Near(v[6],5); });
suite.Run("EMA and RSI",()=> { TestSuite.Near(Indicators.ExponentialMovingAverage([1,2,3],3)[2],2.25); double[] rsi=Indicators.RelativeStrengthIndex(Enumerable.Range(0,30).Select(i=>(double)i).ToArray()); TestSuite.Near(rsi[^1],100); });
suite.Run("Bollinger population deviation",()=> { var b=Indicators.BollingerBands([1,2,3],3,2); TestSuite.Near(b.Middle[2],2); TestSuite.Near(b.Upper[2],2+2*Math.Sqrt(2.0/3)); });
suite.Run("MACD constant input",()=> { var result=Indicators.Macd(Enumerable.Repeat(42.0,100).ToArray()); TestSuite.Near(result.Macd[^1],0); TestSuite.Near(result.Histogram[^1],0); });
foreach(int length in new[]{1,2,3,7,16,17,64,257}) suite.Run($"FFT roundtrip N={length}",()=> { var input=Enumerable.Range(0,length).Select(i=>new Complex(Math.Sin(i*.7),Math.Cos(i*.3))).ToArray(); var data=(Complex[])input.Clone(); Fourier.Transform(data); Fourier.Transform(data,true); for(int i=0;i<length;i++) TestSuite.True((data[i]-input[i]).Magnitude<1e-9); });
suite.Run("FFT agrees with direct transform",()=> { int n=11; var input=Enumerable.Range(0,n).Select(i=>new Complex(i*i%7,0)).ToArray(); var fast=(Complex[])input.Clone(); Fourier.Transform(fast); for(int k=0;k<n;k++) { Complex sum=Complex.Zero; for(int j=0;j<n;j++) sum+=input[j]*Complex.FromPolarCoordinates(1,-2*Math.PI*j*k/n); TestSuite.True((fast[k]-sum).Magnitude<1e-9); } });
suite.Run("one-sided spectrum amplitude",()=> { var samples=Enumerable.Range(0,256).Select(i=>3*Math.Sin(2*Math.PI*16*i/256)).ToArray(); var a=Fourier.AmplitudeSpectrum(samples,WindowFunction.Rectangular); TestSuite.Near(a[16],3,1e-10); TestSuite.True(a.Where((v,i)=>i!=16).Max()<1e-10); });
suite.Run("monotone spline avoids overshoot",()=> { var s=new MonotoneSpline([0,1,2,3],[0,1,1,2]); double prev=0; for(int i=0;i<=300;i++) { double v=s.Evaluate(i/100.0); TestSuite.True(v>=prev-1e-12&&v>=0&&v<=2); prev=v; } TestSuite.Near(s.Evaluate(1.5),1); });

// Additional assemblies register tests through partial test files as the implementation grows.
AdditionalTests.Register(suite);
suite.Finish();

public static partial class AdditionalTests
{
    public static void Register(TestSuite suite) { RegisterCharting(suite); RegisterRendering(suite); RegisterInteraction(suite); RegisterThreeD(suite); RegisterSerialization(suite); }
    static partial void RegisterCharting(TestSuite suite);
    static partial void RegisterRendering(TestSuite suite);
    static partial void RegisterInteraction(TestSuite suite);
    static partial void RegisterThreeD(TestSuite suite);
    static partial void RegisterSerialization(TestSuite suite);
}
public sealed class TestSuite
{
    private readonly List<object> _results=[]; private int _failed;
    public void Run(string name,Action test)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try { test(); _results.Add(new{name,passed=true,milliseconds=watch.Elapsed.TotalMilliseconds}); Console.WriteLine($"PASS {name}"); }
        catch(Exception e) { _failed++; _results.Add(new{name,passed=false,error=e.ToString(),milliseconds=watch.Elapsed.TotalMilliseconds}); Console.Error.WriteLine($"FAIL {name}: {e}"); }
    }
    public void Finish()
    {
        Directory.CreateDirectory("artifacts");
        File.WriteAllText("artifacts/core-tests.json",JsonSerializer.Serialize(new{total=_results.Count,failed=_failed,tests=_results},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{_results.Count-_failed}/{_results.Count} tests passed");
        if(_failed>0) Environment.Exit(1);
    }
    public static void True(bool value) { if(!value) throw new Exception("Assertion failed."); }
    public static void Equal<T>(T actual,T expected) { if(!EqualityComparer<T>.Default.Equals(actual,expected)) throw new Exception($"Expected {expected}; got {actual}."); }
    public static void Near(double actual,double expected,double tolerance=1e-10) { if(!double.IsFinite(actual)||Math.Abs(actual-expected)>tolerance*Math.Max(1,Math.Abs(expected))) throw new Exception($"Expected {expected:R}; got {actual:R}."); }
    public static void Throws<T>(Action action) where T:Exception { try { action(); } catch(T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
}
