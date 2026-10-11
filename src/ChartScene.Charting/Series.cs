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
    private StackMode _stackMode;
    private VectorColorMode _vectorColor=VectorColorMode.ByMagnitude;
    private VectorLengthMode _vectorLength=VectorLengthMode.DataUnits;
    private double _vectorScale=1,_headLength=6,_headWidth=4,_vectorSpacing=8,_donutRatio=.55;
    private bool _filledHead=true,_resampling=true;
    private PolarConnectionMode _polarConnection=PolarConnectionMode.Arc;
    private IReadOnlyList<double> _contourLevels=Array.Empty<double>();
    private IReadOnlyList<string> _pointLabels=Array.Empty<string>();
    private IPaletteProvider? _paletteProvider;
    private IDataLabelProvider? _dataLabelProvider;
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
    public StackMode StackMode { get=>_stackMode; set { CheckEnum(value); Set(ref _stackMode,value); } }
    public VectorColorMode VectorColorMode { get=>_vectorColor; set { CheckEnum(value); Set(ref _vectorColor,value); } }
    public VectorLengthMode VectorLengthMode { get=>_vectorLength; set { CheckEnum(value); Set(ref _vectorLength,value); } }
    public double VectorScale { get=>_vectorScale; set { if(!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _vectorScale,value); } }
    public double ArrowHeadLength { get=>_headLength; set { CheckNonnegative(value); Set(ref _headLength,value); } }
    public double ArrowHeadWidth { get=>_headWidth; set { CheckNonnegative(value); Set(ref _headWidth,value); } }
    public double VectorMinimumSpacing { get=>_vectorSpacing; set { CheckNonnegative(value); Set(ref _vectorSpacing,value); } }
    public bool IsArrowHeadFilled { get=>_filledHead; set=>Set(ref _filledHead,value); }
    public bool IsResamplingEnabled { get=>_resampling; set=>Set(ref _resampling,value); }
    public PolarConnectionMode PolarConnectionMode { get=>_polarConnection; set { CheckEnum(value); Set(ref _polarConnection,value); } }
    public double DonutHoleRatio
    {
        get=>_donutRatio;
        set { if(!double.IsFinite(value)||value<0||value>1) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _donutRatio,value); }
    }
    /// <summary>Owned immutable level list. Assign a replacement to invalidate contour rendering.</summary>
    public IReadOnlyList<double> ContourLevels
    {
        get=>_contourLevels;
        set
        {
            double[] copy=value?.ToArray()??[];
            if(copy.Any(v=>!double.IsFinite(v))) throw new ArgumentException("Contour levels must be finite.",nameof(value));
            if(!_contourLevels.SequenceEqual(copy)) Set(ref _contourLevels,Array.AsReadOnly(copy));
        }
    }
    public IReadOnlyList<string> PointLabels
    {
        get=>_pointLabels;
        set
        {
            string[] copy=value?.Select(v=>v??"").ToArray()??[];
            if(!_pointLabels.SequenceEqual(copy)) Set(ref _pointLabels,Array.AsReadOnly(copy));
        }
    }
    public IPaletteProvider? PaletteProvider { get=>_paletteProvider; set=>Set(ref _paletteProvider,value); }
    public IDataLabelProvider? DataLabelProvider { get=>_dataLabelProvider; set=>Set(ref _dataLabelProvider,value); }
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
    private static void CheckEnum<T>(T value) where T:struct,Enum { if(!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); }
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
