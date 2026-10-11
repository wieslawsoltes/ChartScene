using ChartScene.Core;
using ChartScene.Data;

namespace ChartScene.Charting;

public enum SeriesKind { Line,DigitalLine,Spline,Mountain,SplineMountain,Scatter,Bubble,Column,Bar,Impulse,Band,DigitalBand,ErrorBars,Candlestick,Ohlc,BoxPlot,GroupedBoxPlot,Heatmap,Contour,VectorField,StackedColumn,StackedMountain,StackedBar,PolarLine,Radar,Pie,Donut,TernaryScatter,TernaryLine }
public enum PointMarker { None,Ellipse,Square,Triangle,Diamond,Cross,Plus }
public enum StackMode { Absolute,Percent }
public enum VectorColorMode { Solid,ByMagnitude,ByDirection }
public enum VectorLengthMode { DataUnits,MagnitudePixels,FixedPixels }
public enum PolarConnectionMode { Straight,Arc }

public class RenderableSeries : ObservableObject,IDisposable
{
    private IDataSeries? _data;
    private HeatmapGrid? _grid;
    private string _name="Series",_xAxisId="X",_yAxisId="Y",_group="default";
    private bool _visible=true,_selected,_labels;
    private ColorRgba _stroke=ChartTheme.Dark.Palette[0],_fill=ChartTheme.Dark.Palette[0].WithAlpha(85),_strokeY1=ChartTheme.Dark.Palette[1],_fillY1=ChartTheme.Dark.Palette[1].WithAlpha(70);
    private double _thickness=1.5,_markerSize=6,_pointWidth=.72,_baseline,_opacity=1;
    private PointMarker _marker;
    private ColorMap _colorMap=ColorMap.Thermal;
    private DoubleRange? _colorMapRange;
    private HeatmapColorScaleMode _colorScaleMode;
    public SeriesKind Kind { get; }
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public string SeriesName { get=>_name; set=>Set(ref _name,value??""); }
    public string XAxisId { get=>_xAxisId; set=>Set(ref _xAxisId,value); }
    public string YAxisId { get=>_yAxisId; set=>Set(ref _yAxisId,value); }
    public string StackedGroupId { get=>_group; set=>Set(ref _group,value??"default"); }
    public bool IsVisible { get=>_visible; set=>Set(ref _visible,value); }
    public bool IsSelected { get=>_selected; set=>Set(ref _selected,value); }
    public bool ShowDataLabels { get=>_labels; set=>Set(ref _labels,value); }
    public ColorRgba Stroke { get=>_stroke; set=>Set(ref _stroke,value); }
    public ColorRgba Fill { get=>_fill; set=>Set(ref _fill,value); }
    public ColorRgba StrokeY1 { get=>_strokeY1; set=>Set(ref _strokeY1,value); }
    public ColorRgba FillY1 { get=>_fillY1; set=>Set(ref _fillY1,value); }
    public double StrokeThickness { get=>_thickness; set { CheckNonnegative(value); Set(ref _thickness,value); } }
    public double MarkerSize { get=>_markerSize; set { CheckNonnegative(value); Set(ref _markerSize,value); } }
    public double DataPointWidth { get=>_pointWidth; set { if(!double.IsFinite(value)||value<=0||value>1) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _pointWidth,value); } }
    public double ZeroLineY { get=>_baseline; set { if(!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _baseline,value); } }
    public double Opacity { get=>_opacity; set { if(!double.IsFinite(value)||value<0||value>1) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _opacity,value); } }
    public PointMarker PointMarker { get=>_marker; set=>Set(ref _marker,value); }
    public ColorMap ColorMap { get=>_colorMap; set { ArgumentNullException.ThrowIfNull(value); Set(ref _colorMap,value); } }
    public DoubleRange? ColorMapRange
    {
        get=>_colorMapRange;
        set
        {
            if(value is { } range && (!range.IsValid || _colorScaleMode==HeatmapColorScaleMode.Logarithmic && range.Min<=0))
                throw new ArgumentOutOfRangeException(nameof(value));
            Set(ref _colorMapRange,value);
        }
    }
    /// <summary>Color normalization for heatmap and contour series, independent of spatial axis scales.</summary>
    public HeatmapColorScaleMode ColorScaleMode
    {
        get=>_colorScaleMode;
        set
        {
            if(!Enum.IsDefined(value) || value==HeatmapColorScaleMode.Logarithmic && _colorMapRange is { Min: <=0 })
                throw new ArgumentOutOfRangeException(nameof(value));
            Set(ref _colorScaleMode,value);
        }
    }
    public StackMode StackMode { get; set; }
    public VectorColorMode VectorColorMode { get; set; }=VectorColorMode.ByMagnitude;
    public VectorLengthMode VectorLengthMode { get; set; }=VectorLengthMode.DataUnits;
    public double VectorScale { get; set; }=1;
    public double ArrowHeadLength { get; set; }=6;
    public double ArrowHeadWidth { get; set; }=4;
    public double VectorMinimumSpacing { get; set; }=8;
    public bool IsArrowHeadFilled { get; set; }=true;
    public bool IsResamplingEnabled { get; set; }=true;
    public PolarConnectionMode PolarConnectionMode { get; set; }=PolarConnectionMode.Arc;
    public double DonutHoleRatio { get; set; }=.55;
    public IReadOnlyList<double> ContourLevels { get; set; }=Array.Empty<double>();
    public IReadOnlyList<string> PointLabels { get; set; }=Array.Empty<string>();
    public IPaletteProvider? PaletteProvider { get; set; }
    public IDataLabelProvider? DataLabelProvider { get; set; }
    public HashSet<int> SelectedIndices { get; }=[];
    public IDataSeries? DataSeries
    {
        get=>_data;
        set
        {
            if(ReferenceEquals(_data,value)) return;
            if(value is not null&&value.ChannelCount<RequiredChannels) throw new ArgumentException($"{Kind} requires {RequiredChannels} channels.");
            if(_data is not null) _data.DataSeriesChanged-=OnDataChanged;
            _data=value;
            if(_data is not null) _data.DataSeriesChanged+=OnDataChanged;
            RaisePropertyChanged();
        }
    }
    public HeatmapGrid? HeatmapGrid { get=>_grid; set=>Set(ref _grid,value); }
    public int RequiredChannels=>Kind switch {SeriesKind.Candlestick or SeriesKind.Ohlc=>5,SeriesKind.BoxPlot or SeriesKind.GroupedBoxPlot=>6,SeriesKind.VectorField or SeriesKind.ErrorBars=>4,SeriesKind.Band or SeriesKind.DigitalBand or SeriesKind.Bubble or SeriesKind.TernaryLine or SeriesKind.TernaryScatter=>3,_=>2};
    public int YChannel=>Kind switch {SeriesKind.Candlestick or SeriesKind.Ohlc=>4,SeriesKind.BoxPlot or SeriesKind.GroupedBoxPlot=>3,_=>1};
    public bool IsStacked=>Kind is SeriesKind.StackedColumn or SeriesKind.StackedMountain or SeriesKind.StackedBar;
    public bool IsHorizontal=>Kind is SeriesKind.Bar or SeriesKind.StackedBar;
    public bool IsCircular=>Kind is SeriesKind.Pie or SeriesKind.Donut;
    public RenderableSeries(SeriesKind kind=SeriesKind.Line)=>Kind=kind;
    public void Invalidate()=>RaisePropertyChanged("Content");
    private static void CheckNonnegative(double value) { if(!double.IsFinite(value)||value<0) throw new ArgumentOutOfRangeException(nameof(value)); }
    private void OnDataChanged(object? sender,EventArgs e)=>RaisePropertyChanged("DataRevision");
    public void Dispose() { if(_data is not null) _data.DataSeriesChanged-=OnDataChanged; }
}

// Familiar names describe this library's own independently implemented models, not binary-compatible WPF types.
public sealed class FastLineRenderableSeries():RenderableSeries(SeriesKind.Line);
public sealed class FastMountainRenderableSeries():RenderableSeries(SeriesKind.Mountain);
public sealed class FastColumnRenderableSeries():RenderableSeries(SeriesKind.Column);
public sealed class FastBandRenderableSeries():RenderableSeries(SeriesKind.Band);
public sealed class XyScatterRenderableSeries():RenderableSeries(SeriesKind.Scatter);
public sealed class FastBubbleRenderableSeries():RenderableSeries(SeriesKind.Bubble);
public sealed class FastCandlestickRenderableSeries():RenderableSeries(SeriesKind.Candlestick);
public sealed class FastOhlcRenderableSeries():RenderableSeries(SeriesKind.Ohlc);
public sealed class FastImpulseRenderableSeries():RenderableSeries(SeriesKind.Impulse);
public sealed class FastHeatMapRenderableSeries():RenderableSeries(SeriesKind.Heatmap);
public sealed class VectorFieldRenderableSeries():RenderableSeries(SeriesKind.VectorField);
public sealed class StackedColumnRenderableSeries():RenderableSeries(SeriesKind.StackedColumn);
public sealed class StackedMountainRenderableSeries():RenderableSeries(SeriesKind.StackedMountain);
public sealed class StackedBoxPlotRenderableSeries():RenderableSeries(SeriesKind.GroupedBoxPlot);
