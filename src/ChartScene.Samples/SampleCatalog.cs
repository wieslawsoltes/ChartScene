using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Charting;
using ChartScene.Numerics;

namespace ChartScene.Samples;

public sealed record ChartExample(string Id,string Title,string Category,string Description,Func<ChartModel> Create,string Source)
{
    public override string ToString()=>Title;
}

/// <summary>The application and browser regression suite consume these same independently reusable examples.</summary>
public static class SampleCatalog
{
    private static readonly Lazy<IReadOnlyList<ChartExample>> Catalog = new(Build);
    public static IReadOnlyList<ChartExample> Examples => Catalog.Value;
    public static ChartExample Find(string id)=>Examples.FirstOrDefault(e=>e.Id.Equals(id,StringComparison.OrdinalIgnoreCase))??Examples[0];
    private static IReadOnlyList<ChartExample> Build()
    {
        var examples=new List<ChartExample>
        {
            new("overview","Scientific signals","Getting started","Three continuously sampled signals share a numeric viewport. Drag to pan, use the wheel to zoom, or Shift-drag a selection. The legend controls series visibility.",()=>Signals(SeriesKind.Line),"var chart = new ChartModel();\nvar data = new XyDataSeries<double, double>();\ndata.Append(0, Math.Sin(0));\nchart.RenderableSeries.Add(new FastLineRenderableSeries\n{\n    DataSeries = data, SeriesName = \"Signal\"\n});\nvar view = new ChartSurface { Model = chart };"),
            new("live","Real-time FIFO signals","Performance","A bounded 2,000-sample ring buffer receives atomic batches. Start the stream to append data while keeping the most recent ten seconds in view. Pause before inspecting a fixed viewport.",Live,"var data = new XyDataSeries { FifoCapacity = 2000 };\nusing (data.SuspendUpdates())\n{\n    data.Append(time, value);\n}\nchart.XAxis.VisibleRange = new(time - 10, time);"),
            new("million","One million samples","Performance","One million original samples remain in the reusable data series. Screen-space min/max resampling preserves each bucket's endpoints and extrema; the status bar distinguishes original samples from rendered samples.",Million,"var data = new XyDataSeries(1_000_000);\ndata.AppendRange(xValues, yValues);\nchart.RenderableSeries.Add(new FastLineRenderableSeries\n{\n    DataSeries = data, IsResamplingEnabled = true\n});"),
            new("fft","Frequency spectrum","Scientific","A Hann-windowed, one-sided FFT resolves 50 Hz, 120 Hz and 240 Hz components. The numerical package includes radix-2 and Bluestein transforms and normalizes the window's coherent gain.",Spectrum,"double[] spectrum = Fourier.AmplitudeSpectrum(\n    samples, WindowFunction.Hann);\nvar series = new RenderableSeries(SeriesKind.Impulse);"),
            new("logarithmic","Logarithmic axes","Axes and layout","Logarithmic transforms, ticks, cursor values and wheel anchors are evaluated in logarithmic space. Non-positive samples are excluded from the logarithmic range.",Logarithmic,"chart.XAxis.Scale = AxisScale.Logarithmic;\nchart.YAxis.Scale = AxisScale.Logarithmic;\nchart.XAxis.VisibleRange = new(0.01, 1000);"),
            new("datetime","Date and time axis","Axes and layout","Timestamps use UTC Unix milliseconds. Tick generation chooses time intervals and aligns long spans to calendar boundaries rather than treating months as fixed-duration bins.",DateTimeExample,"chart.XAxes.Clear();\nchart.XAxes.Add(new DateTimeAxis\n{\n    Id = \"X\", AxisTitle = \"UTC time\"\n});\ndata.Append(DateTimeOffset.UtcNow, value);"),
            new("multiple-axes","Multiple independent axes","Axes and layout","Temperature and pressure share a time axis but use separately colored left and right value axes. Each series resolves its axes by ID, and axes can be dragged independently.",MultipleAxes,"chart.YAxes.Add(new NumericAxis\n{\n    Id = \"Pressure\", AxisAlignment = AxisAlignment.Right\n});\npressureSeries.YAxisId = \"Pressure\";"),
            new("flipped","Rotated and flipped axes","Axes and layout","X data is laid out vertically and Y horizontally. The same coordinate calculators drive drawing, hit testing, panning and selection.",Flipped,"chart.XAxis.AxisAlignment = AxisAlignment.Left;\nchart.YAxis.AxisAlignment = AxisAlignment.Bottom;\nchart.XAxis.FlippedCoordinates = true;"),
            new("aspect-ratio","Equal-scale scientific plot","Axes and layout","An aspect ratio constraint preserves the relationship between physical X and Y units as the plot resizes. Fit mode expands a range instead of cropping the current data.",AspectRatio,"chart.AspectRatio = 1;\nchart.AspectRatioMode = AspectRatioMode.Fit;"),
            new("annotations","Editable annotations","Interaction","Drag the line or box to move it, or drag a selected endpoint to resize it. Annotation changes, creation and deletion participate in undo and redo. Coordinates can be data-relative, plot-relative or logical pixels.",Annotations,"chart.Annotations.Add(new BoxAnnotation\n{\n    X1 = 3, X2 = 5, Y1 = -1, Y2 = 1,\n    Text = \"Analysis window\", IsEditable = true\n});"),
            new("palette","Per-point palette provider","Styling","A reusable palette provider assigns colors from source values. Palette-enabled paths retain original sample indices so threshold transitions are not silently reassigned during resampling.",Palette,"series.PaletteProvider = new ThresholdPaletteProvider(\n    0, ColorRgba.Parse(\"#58BCCF\"),\n    ColorRgba.Parse(\"#E9A85B\"));"),
            new("percent-stacked","100% stacked columns","Statistical","Positive and negative values are accumulated separately by actual X coordinate. Percent stacking normalizes each signed stack, and missing values remain gaps.",PercentStacked,"series.StackMode = StackMode.Percent;\nseries.StackedGroupId = \"Composition\";"),
            new("financial","Candlesticks and Bollinger bands","Financial","OHLC candles share the chart with 20-period Bollinger bands and a moving average. The axis uses UTC dates and the cursor exposes open, high, low and close values.",Financial,"var bands = Indicators.BollingerBands(close, 20, 2);\nvar candles = new FastCandlestickRenderableSeries\n{\n    DataSeries = ohlc\n};"),
            new("nonuniform-heatmap","Non-uniform heatmap","Scientific","Rectilinear cell edges may have unequal spacing. Each cell is projected independently, preserving its data-space area and NaN transparency.",NonuniformHeatmap,"var grid = new HeatmapGrid(xEdges, yEdges, values);\nseries.HeatmapGrid = grid;\nseries.ColorMap = ColorMap.Thermal;")
        };
        foreach(var kind in Enum.GetValues<SeriesKind>())
        {
            var captured=kind;
            string title=Titles.GetValueOrDefault(kind,kind.ToString());
            string category=kind switch
            {
                SeriesKind.Candlestick or SeriesKind.Ohlc=>"Financial",
                SeriesKind.BoxPlot or SeriesKind.GroupedBoxPlot or SeriesKind.StackedColumn or SeriesKind.StackedBar or SeriesKind.StackedMountain=>"Statistical",
                SeriesKind.Heatmap or SeriesKind.Contour or SeriesKind.VectorField or SeriesKind.TernaryScatter or SeriesKind.TernaryLine=>"Scientific",
                SeriesKind.PolarLine or SeriesKind.Radar or SeriesKind.Pie or SeriesKind.Donut=>"Polar and circular",
                _=>"2D chart types"
            };
            examples.Add(new(kind.ToString().ToLowerInvariant(),title,category,Descriptions.GetValueOrDefault(kind,$"{title} use the shared chart model, coordinate transforms, themes, selection, annotations and export pipeline. All geometry comes from the same reusable renderer used by the browser and desktop controls."),()=>CreateKind(captured),$"var series = new RenderableSeries(SeriesKind.{kind})\n{{\n    DataSeries = data,\n    SeriesName = \"{title}\",\n    Stroke = ColorRgba.Parse(\"#58BCCF\")\n}};\nchart.RenderableSeries.Add(series);"));
        }
        return examples.AsReadOnly();
    }
    private static readonly Dictionary<SeriesKind,string> Titles=new()
    {
        [SeriesKind.Line]="Line chart",[SeriesKind.DigitalLine]="Digital step line",[SeriesKind.Spline]="Monotone spline",[SeriesKind.Mountain]="Mountain area",[SeriesKind.SplineMountain]="Spline mountain",[SeriesKind.Scatter]="Scatter markers",[SeriesKind.Bubble]="Bubble color map",[SeriesKind.Column]="Grouped columns",[SeriesKind.Bar]="Horizontal bars",[SeriesKind.Impulse]="Impulse / stem chart",[SeriesKind.Band]="Crossing band fills",[SeriesKind.DigitalBand]="Digital band chart",[SeriesKind.ErrorBars]="Error bars",[SeriesKind.Candlestick]="Candlestick chart",[SeriesKind.Ohlc]="OHLC bars",[SeriesKind.BoxPlot]="Box-and-whisker plot",[SeriesKind.GroupedBoxPlot]="Grouped box plots",[SeriesKind.Heatmap]="Uniform heatmap",[SeriesKind.Contour]="Contour lines",[SeriesKind.VectorField]="Vector field",[SeriesKind.StackedColumn]="Stacked columns",[SeriesKind.StackedMountain]="Stacked mountain",[SeriesKind.StackedBar]="Stacked horizontal bars",[SeriesKind.PolarLine]="Polar line chart",[SeriesKind.Radar]="Radar chart",[SeriesKind.Pie]="Pie chart",[SeriesKind.Donut]="Donut chart",[SeriesKind.TernaryScatter]="Ternary scatter",[SeriesKind.TernaryLine]="Ternary line"
    };
    private static readonly Dictionary<SeriesKind,string> Descriptions=new()
    {
        [SeriesKind.Band]="The renderer detects crossings between the two Y channels and splits each interval at the crossing before applying the corresponding fill. NaN samples break both boundaries.",
        [SeriesKind.VectorField]="Two vector components drive arrow direction and data-space length. Magnitude or direction can select a color, and screen-space spacing limits visual overdraw without changing source data.",
        [SeriesKind.Contour]="Marching squares interpolates crossings over the heatmap's cell centers. An asymptotic determinant chooses saddle connectivity; cells touching NaN values are omitted.",
        [SeriesKind.Spline]="Shape-preserving cubic Hermite interpolation limits slopes to avoid overshoot between monotone samples. Duplicate X coordinates fall back to straight connections rather than producing undefined tangents.",
        [SeriesKind.TernaryScatter]="Three non-negative channels are normalized to barycentric coordinates. The triangle retains its geometric aspect ratio, with each vertex representing a pure component."
    };
    private static ChartModel Model(string title,string x="Time (s)",string y="Amplitude")
    {
        var model=new ChartModel{Title=title,Subtitle="ChartScene • reusable scientific charting"};
        model.XAxis.AxisTitle=x; model.YAxis.AxisTitle=y; return model;
    }
    private static RenderableSeries Series(ChartModel model,SeriesKind kind,IDataSeries data,string name,int color=0)
    {
        var stroke=model.Theme.Palette[color%model.Theme.Palette.Count];
        var result=new RenderableSeries(kind){DataSeries=data,SeriesName=name,Stroke=stroke,Fill=stroke.WithAlpha(105),StrokeY1=model.Theme.Palette[(color+1)%model.Theme.Palette.Count],FillY1=model.Theme.Palette[(color+1)%model.Theme.Palette.Count].WithAlpha(75)};
        model.RenderableSeries.Add(result); return result;
    }
    public static ChartModel Signals(SeriesKind kind)
    {
        var model=Model("Scientific signals");
        for(int channel=0;channel<3;channel++)
        {
            var data=new XyDataSeries(); int count=kind is SeriesKind.Spline or SeriesKind.SplineMountain?70:2400;
            double[] x=new double[count],y=new double[count];
            for(int i=0;i<count;i++) { x[i]=i*12.0/(count-1); y[i]=Signal(x[i],channel); }
            data.AppendRange(x,y); var series=Series(model,kind,data,new[]{"Carrier","Modulated","Reference"}[channel],channel);
            if(kind is SeriesKind.Spline or SeriesKind.SplineMountain) { series.PointMarker=PointMarker.Ellipse; series.MarkerSize=4; }
        }
        return model;
    }
    public static double Signal(double time,int channel)=>channel switch
    {
        0=>Math.Sin(time*3)*(.75+.2*Math.Cos(time*.8)),
        1=>.55*Math.Sin(time*3+.5)+.22*Math.Sin(time*13),
        _=>.35*Math.Cos(time*1.6)-.5
    };
    public static ChartModel CreateKind(SeriesKind kind)
    {
        if(kind is SeriesKind.Line or SeriesKind.DigitalLine or SeriesKind.Spline or SeriesKind.Mountain or SeriesKind.SplineMountain) { var signals=Signals(kind); signals.Title=Titles[kind]; return signals; }
        var model=Model(Titles.GetValueOrDefault(kind,kind.ToString()),"X","Y");
        var random=new Random(731);
        switch(kind)
        {
            case SeriesKind.Heatmap: case SeriesKind.Contour:
                model.ShowLegend=false;
                model.RenderableSeries.Add(new(kind){SeriesName="Field intensity",HeatmapGrid=HeatmapGrid.Uniform(160,120,-4,.05,-3,.05,(x,y)=>Math.Sin((x-80)*.06)*Math.Cos((y-60)*.065)*Math.Exp(-((x-80)*(x-80)+(y-60)*(y-60))/8000.0)),ColorMap=ColorMap.Thermal,StrokeThickness=1.4});
                break;
            case SeriesKind.VectorField:
            {
                var data=new VectorFieldDataSeries{AcceptsUnsortedData=true};
                for(int y=-10;y<=10;y++) for(int x=-14;x<=14;x++) { double length=Math.Sqrt(x*x+y*y)+1; data.Append(x,y,-y/length,x/length); }
                var series=Series(model,kind,data,"Rotational field"); series.VectorScale=.8; series.ArrowHeadLength=7; series.VectorMinimumSpacing=10; break;
            }
            case SeriesKind.Candlestick: case SeriesKind.Ohlc:
            {
                var data=new OhlcDataSeries(); double previous=100;
                for(int i=0;i<80;i++) { double close=previous+(random.NextDouble()-.45)*4; data.Append(i,previous,Math.Max(previous,close)+random.NextDouble()*2,Math.Min(previous,close)-random.NextDouble()*2,close); previous=close; }
                var series=Series(model,kind,data,"Price"); series.Stroke=model.Theme.Palette[3]; series.Fill=series.Stroke.WithAlpha(190); series.StrokeY1=model.Theme.Palette[5]; series.FillY1=series.StrokeY1.WithAlpha(160); break;
            }
            case SeriesKind.BoxPlot: case SeriesKind.GroupedBoxPlot:
                for(int group=0;group<(kind==SeriesKind.GroupedBoxPlot?3:1);group++)
                {
                    var data=new BoxPlotDataSeries();
                    for(int i=0;i<8;i++) { double median=5+i*.7+group*.6,spread=1+random.NextDouble(); data.Append(i,median-2*spread,median-spread,median,median+spread,median+2*spread); }
                    Series(model,kind,data,$"Population {(char)('A'+group)}",group);
                }
                model.XAxis.AxisTitle="Experiment"; model.YAxis.AxisTitle="Response"; break;
            case SeriesKind.Band: case SeriesKind.DigitalBand:
            {
                var data=new XyyDataSeries(); for(int i=0;i<250;i++) { double x=i*.05; data.Append(x,Math.Sin(x*2),.6*Math.Cos(x*1.4)); }
                Series(model,kind,data,"Upper / lower signal"); break;
            }
            case SeriesKind.ErrorBars:
            {
                var data=new DataSeries(4); for(int i=0;i<30;i++) { double y=2+Math.Sin(i*.25),error=.15+random.NextDouble()*.35; data.Append(i,y,y-error,y+error); }
                Series(model,kind,data,"Mean ± confidence interval").MarkerSize=5; break;
            }
            case SeriesKind.Scatter: case SeriesKind.Bubble:
                for(int group=0;group<(kind==SeriesKind.Scatter?3:1);group++)
                {
                    var data=new DataSeries(kind==SeriesKind.Bubble?3:2){AcceptsUnsortedData=true};
                    for(int i=0;i<(kind==SeriesKind.Bubble?100:700);i++)
                    {
                        double u=Math.Max(1e-12,random.NextDouble()),v=random.NextDouble(),radius=Math.Sqrt(-2*Math.Log(u)),x=radius*Math.Cos(2*Math.PI*v)+group*2,y=radius*Math.Sin(2*Math.PI*v)+group*.8;
                        if(kind==SeriesKind.Bubble) data.Append(x,y,1+random.NextDouble()*20); else data.Append(x,y);
                    }
                    var series=Series(model,kind,data,$"Cluster {group+1}",group); series.PointMarker=group==0?PointMarker.Ellipse:group==1?PointMarker.Triangle:PointMarker.Diamond; series.MarkerSize=kind==SeriesKind.Bubble?7:4; series.Opacity=.8;
                }
                break;
            case SeriesKind.Pie: case SeriesKind.Donut:
            {
                model.ShowLegend=false; var data=new XyDataSeries(); data.AppendRange([0,1,2,3,4],[34,26,18,14,8]);
                var series=Series(model,kind,data,"Composition"); series.PointLabels=["Research","Engineering","Production","Quality","Other"]; break;
            }
            case SeriesKind.PolarLine: case SeriesKind.Radar:
                model.CoordinateSystem=CoordinateSystem.Polar; model.YAxis.VisibleRange=new(0,10); model.YAxis.AutoRange=AutoRange.Never;
                for(int channel=0;channel<2;channel++)
                {
                    var data=new XyDataSeries(); int count=kind==SeriesKind.Radar?7:361;
                    for(int i=0;i<count;i++) { double angle=i*360.0/(count-1); data.Append(angle,kind==SeriesKind.Radar?5+2*Math.Cos(angle*Math.PI/180+channel):5+3*Math.Sin(angle*Math.PI/60+channel)); }
                    Series(model,kind,data,channel==0?"Measured":"Reference",channel).PointMarker=kind==SeriesKind.Radar?PointMarker.Ellipse:PointMarker.None;
                }
                break;
            case SeriesKind.TernaryScatter: case SeriesKind.TernaryLine:
                model.CoordinateSystem=CoordinateSystem.Ternary;
                for(int group=0;group<3;group++)
                {
                    var data=new XyzDataSeries{AcceptsUnsortedData=true};
                    for(int i=0;i<(kind==SeriesKind.TernaryLine?30:250);i++) { double a=10+random.NextDouble()*30+(group==0?40:0),b=10+random.NextDouble()*30+(group==1?40:0),z=10+random.NextDouble()*30+(group==2?40:0); data.Append(a,b,z); }
                    Series(model,kind,data,$"Mixture {group+1}",group).MarkerSize=4;
                }
                break;
            case SeriesKind.Column: case SeriesKind.Bar: case SeriesKind.StackedColumn: case SeriesKind.StackedBar: case SeriesKind.StackedMountain:
                for(int group=0;group<3;group++)
                {
                    var data=new XyDataSeries(); int count=kind==SeriesKind.StackedMountain?120:12;
                    for(int i=0;i<count;i++) data.Append(i,3+group+2*Math.Sin(i*.35+group));
                    var series=Series(model,kind,data,$"Region {(char)('A'+group)}",group); series.Fill=series.Stroke.WithAlpha(180); series.ShowDataLabels=count<20&&group==2;
                }
                model.XAxis.AxisTitle=kind is SeriesKind.Bar or SeriesKind.StackedBar?"Value":"Period";
                model.YAxis.AxisTitle=kind is SeriesKind.Bar or SeriesKind.StackedBar?"Period":"Value";
                break;
            default:
            {
                var data=new XyDataSeries(); for(int i=0;i<100;i++) data.Append(i,Math.Sin(i*.16)*Math.Exp(-i*.015));
                Series(model,kind,data,"Impulse response").PointMarker=PointMarker.Ellipse; break;
            }
        }
        return model;
    }
    private static ChartModel Live()
    {
        var model=Model("Real-time FIFO signals");
        for(int channel=0;channel<3;channel++)
        {
            var data=new XyDataSeries{FifoCapacity=2000};
            for(int i=0;i<2000;i++) data.Append(i*.005,Signal(i*.005,channel));
            Series(model,SeriesKind.Line,data,$"Channel {channel+1}",channel);
        }
        model.XAxis.VisibleRange=new(0,10); model.XAxis.AutoRange=AutoRange.Never; model.YAxis.VisibleRange=new(-1.5,1.5); model.YAxis.AutoRange=AutoRange.Never;
        return model;
    }
    private static ChartModel Million()
    {
        var model=Model("One million samples","Sample index","Amplitude"); var data=new XyDataSeries(1_000_000);
        double[] x=new double[1_000_000],y=new double[x.Length];
        for(int i=0;i<x.Length;i++) { x[i]=i; y[i]=Math.Sin(i*.00007)+.3*Math.Sin(i*.017)+.08*Math.Sin(i*.73); }
        y[543210]=3; y[543211]=-3; data.AppendRange(x,y); Series(model,SeriesKind.Line,data,"1,000,000 original samples"); return model;
    }
    private static ChartModel Spectrum()
    {
        const int count=4096; const double rate=2048;
        double[] samples=Enumerable.Range(0,count).Select(i=>Math.Sin(2*Math.PI*50*i/rate)+.6*Math.Sin(2*Math.PI*120*i/rate)+.25*Math.Sin(2*Math.PI*240*i/rate)).ToArray();
        double[] spectrum=Fourier.AmplitudeSpectrum(samples); var data=new XyDataSeries(); data.AppendRange(Enumerable.Range(0,spectrum.Length).Select(i=>i*rate/count).ToArray(),spectrum);
        var model=Model("Frequency spectrum","Frequency (Hz)","Amplitude"); Series(model,SeriesKind.Impulse,data,"Hann-windowed FFT"); model.XAxis.VisibleRange=new(0,300); model.XAxis.AutoRange=AutoRange.Never; return model;
    }
    private static ChartModel Logarithmic()
    {
        var model=Model("Logarithmic axes","Frequency (Hz)","Response");
        for(int j=0;j<3;j++) { var data=new XyDataSeries(); for(int i=0;i<500;i++) { double x=Math.Pow(10,-2+i*.01); data.Append(x,Math.Pow(x,.5+j*.25)/(1+x*.05)); } Series(model,SeriesKind.Line,data,$"Response {j+1}",j); }
        model.XAxis.Scale=AxisScale.Logarithmic; model.YAxis.Scale=AxisScale.Logarithmic; return model;
    }
    private static ChartModel DateTimeExample()
    {
        var model=Signals(SeriesKind.Line); model.Title="Date and time axis"; double origin=new DateTimeOffset(2026,1,1,9,0,0,TimeSpan.Zero).ToUnixTimeMilliseconds();
        foreach(var series in model.RenderableSeries) { var old=series.DataSeries!.Snapshot(); var data=new XyDataSeries(); data.AppendRange(Enumerable.Range(0,old.Count).Select(i=>origin+old[0,i]*60_000).ToArray(),old.Channel(1).ToArray()); series.DataSeries=data; }
        model.XAxis.Scale=AxisScale.DateTime; model.XAxis.AxisTitle="UTC time"; return model;
    }
    private static ChartModel MultipleAxes()
    {
        var model=Model("Multiple independent axes","Time (s)","Temperature (°C)");
        model.YAxis.Color=model.Theme.Palette[0]; model.YAxes.Add(new NumericAxis{Id="Pressure",AxisAlignment=AxisAlignment.Right,AxisTitle="Pressure (kPa)",Color=model.Theme.Palette[1],DrawMajorGridLines=false,DrawMinorGridLines=false});
        var temperature=new XyDataSeries(); var pressure=new XyDataSeries();
        for(int i=0;i<1000;i++) { double t=i*.02; temperature.Append(t,22+3*Math.Sin(t*.5)); pressure.Append(t,1000+150*Math.Cos(t*.4)); }
        Series(model,SeriesKind.Line,temperature,"Temperature"); Series(model,SeriesKind.Line,pressure,"Pressure",1).YAxisId="Pressure"; return model;
    }
    private static ChartModel Flipped()
    {
        var model=Signals(SeriesKind.Line); model.Title="Rotated and flipped axes"; model.XAxis.AxisAlignment=AxisAlignment.Left; model.XAxis.FlippedCoordinates=true; model.YAxis.AxisAlignment=AxisAlignment.Bottom; return model;
    }
    private static ChartModel AspectRatio()
    {
        var model=Model("Equal-scale scientific plot","X (mm)","Y (mm)"); var data=new XyDataSeries{AcceptsUnsortedData=true};
        for(int i=0;i<=720;i++) { double t=i*Math.PI/360; data.Append(Math.Cos(t)*10,Math.Sin(t)*10); }
        Series(model,SeriesKind.Line,data,"10 mm radius"); model.AspectRatio=1; return model;
    }
    private static ChartModel Annotations()
    {
        var model=Signals(SeriesKind.Line); model.Title="Editable annotations";
        model.Annotations.Add(new BoxAnnotation{X1=3,X2=5,Y1=-1,Y2=1,Text="Analysis window",IsSelected=true});
        model.Annotations.Add(new HorizontalLineAnnotation{Y1=.8,Y2=.8,Text="Upper threshold",Stroke=model.Theme.Palette[1],DashArray=[5,4]});
        model.Annotations.Add(new ChartAnnotation(AnnotationKind.Arrow){X1=8,Y1=1.1,X2=7.4,Y2=.1,Text="Transient",Stroke=model.Theme.Palette[2]});
        model.Annotations.Add(new TextAnnotation{X1=.02,Y1=.96,Text="Annotations remain editable in data or relative coordinates",XCoordinateMode=CoordinateMode.Relative,YCoordinateMode=CoordinateMode.Relative,Stroke=model.Theme.Muted,FontSize=11});
        return model;
    }
    private static ChartModel Palette()
    {
        var model=Signals(SeriesKind.Line); while(model.RenderableSeries.Count>1) { model.RenderableSeries[^1].Dispose(); model.RenderableSeries.RemoveAt(model.RenderableSeries.Count-1); }
        model.Title="Per-point palette provider"; model.RenderableSeries[0].PaletteProvider=new ThresholdPaletteProvider(0,model.Theme.Palette[0],model.Theme.Palette[1]); return model;
    }
    private static ChartModel PercentStacked()
    {
        var model=CreateKind(SeriesKind.StackedColumn); model.Title="100% stacked columns";
        foreach(var series in model.RenderableSeries) series.StackMode=StackMode.Percent;
        model.YAxis.AxisTitle="Contribution (%)"; return model;
    }
    private static ChartModel Financial()
    {
        var model=CreateKind(SeriesKind.Candlestick); model.Title="Candlesticks and Bollinger bands";
        var candles=model.RenderableSeries[0]; var old=candles.DataSeries!.Snapshot(); var data=new OhlcDataSeries();
        double origin=new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero).ToUnixTimeMilliseconds();
        double[] x=Enumerable.Range(0,old.Count).Select(i=>origin+i*86_400_000.0).ToArray(),close=old.Channel(4).ToArray();
        for(int i=0;i<old.Count;i++) data.Append(x[i],old[1,i],old[2,i],old[3,i],old[4,i]); candles.DataSeries=data;
        var bands=Indicators.BollingerBands(close); var bandData=new XyyDataSeries(); bandData.AppendRange(x,bands.Upper,bands.Lower);
        var band=Series(model,SeriesKind.Band,bandData,"Bollinger ±2σ",2); band.Fill=band.Stroke.WithAlpha(25); band.StrokeThickness=1;
        model.RenderableSeries.Move(model.RenderableSeries.IndexOf(band),0);
        var average=new XyDataSeries(); average.AppendRange(x,bands.Middle); Series(model,SeriesKind.Line,average,"20-period SMA",1);
        model.XAxis.Scale=AxisScale.DateTime; model.XAxis.AxisTitle="Date (UTC)"; model.YAxis.AxisTitle="Price"; return model;
    }
    private static ChartModel NonuniformHeatmap()
    {
        var model=Model("Non-uniform heatmap","X","Y"); model.ShowLegend=false;
        double[] x=Enumerable.Range(0,41).Select(i=>Math.Pow(i/40.0,2)*10).ToArray(),y=Enumerable.Range(0,31).Select(i=>Math.Pow(i/30.0,1.4)*8).ToArray();
        var values=new double[40*30]; for(int row=0;row<30;row++) for(int col=0;col<40;col++) values[row*40+col]=Math.Sin(x[col])*Math.Cos(y[row]);
        model.RenderableSeries.Add(new(SeriesKind.Heatmap){HeatmapGrid=new(x,y,values),SeriesName="Non-uniform field",ColorMap=ColorMap.Diverging}); return model;
    }
}
