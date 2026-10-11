using System.Globalization;
using ChartScene.Core;

namespace ChartScene.Charting;

public enum AxisScale { Numeric, Logarithmic, DateTime, TimeSpan, Category }
public enum AxisAlignment { Left, Right, Top, Bottom }
public enum AutoRange { Never, Once, Always }
public readonly record struct AxisTick(double Value,string Label,bool IsMajor=true);
public interface IAxisLabelProvider { string Format(double value,AxisModel axis,bool cursor=false); }

public class AxisModel : ObservableObject
{
    private string _id="DefaultAxisId",_title="",_format="",_cursorFormat="";
    private DoubleRange _visibleRange=DoubleRange.Unit;
    private AxisScale _scale;
    private AxisAlignment _alignment=AxisAlignment.Bottom;
    private bool _flipped,_visible=true,_majorGrid=true,_minorGrid=true;
    private double _logBase=10,_growLower=.05,_growUpper=.05,_majorDelta=double.NaN;
    private AutoRange _autoRange=AutoRange.Once;
    private ColorRgba? _color;
    private IReadOnlyList<string> _labels=Array.Empty<string>();
    private IAxisLabelProvider? _labelProvider;
    private DoubleRange? _zoomExtentsRange;
    public string Id { get=>_id; set { ArgumentException.ThrowIfNullOrWhiteSpace(value); Set(ref _id,value); } }
    public string AxisTitle { get=>_title; set=>Set(ref _title,value??""); }
    public string TextFormatting { get=>_format; set=>Set(ref _format,value??""); }
    public string CursorTextFormatting { get=>_cursorFormat; set=>Set(ref _cursorFormat,value??""); }
    public DoubleRange VisibleRange
    {
        get=>_visibleRange;
        set { if(!value.IsValid||Scale==AxisScale.Logarithmic&&value.Min<=0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _visibleRange,value); }
    }
    public DoubleRange? ZoomExtentsRange
    {
        get=>_zoomExtentsRange;
        set { if(value is { } range && (!range.IsValid || Scale==AxisScale.Logarithmic && range.Min<=0)) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _zoomExtentsRange,value); }
    }
    public AxisScale Scale
    {
        get=>_scale;
        set { if(value==AxisScale.Logarithmic&&_visibleRange.Min<=0) _visibleRange=new(1,10); Set(ref _scale,value); }
    }
    public AxisAlignment AxisAlignment { get=>_alignment; set=>Set(ref _alignment,value); }
    public bool IsVertical=>AxisAlignment is AxisAlignment.Left or AxisAlignment.Right;
    public bool FlippedCoordinates { get=>_flipped; set=>Set(ref _flipped,value); }
    public bool IsVisible { get=>_visible; set=>Set(ref _visible,value); }
    public bool DrawMajorGridLines { get=>_majorGrid; set=>Set(ref _majorGrid,value); }
    public bool DrawMinorGridLines { get=>_minorGrid; set=>Set(ref _minorGrid,value); }
    public ColorRgba? Color { get=>_color; set=>Set(ref _color,value); }
    public AutoRange AutoRange { get=>_autoRange; set=>Set(ref _autoRange,value); }
    public double LogarithmicBase { get=>_logBase; set { if(!double.IsFinite(value)||value<=1) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _logBase,value); } }
    public double GrowByLower { get=>_growLower; set { if(!double.IsFinite(value)||value<0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _growLower,value); } }
    public double GrowByUpper { get=>_growUpper; set { if(!double.IsFinite(value)||value<0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _growUpper,value); } }
    public double MajorDelta { get=>_majorDelta; set { if(!double.IsNaN(value)&&(!double.IsFinite(value)||value<=0)) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _majorDelta,value); } }
    public IReadOnlyList<string> Labels
    {
        get=>_labels;
        set { string[] labels=value?.Select(v=>v??"").ToArray()??[]; if(!_labels.SequenceEqual(labels)) Set(ref _labels,Array.AsReadOnly(labels)); }
    }
    public IAxisLabelProvider? LabelProvider { get=>_labelProvider; set=>Set(ref _labelProvider,value); }
    internal bool HasAutoRanged { get; set; }
    public double ToScale(double value)=>Scale==AxisScale.Logarithmic?(value>0?Math.Log(value,LogarithmicBase):double.NaN):value;
    public double FromScale(double value)=>Scale==AxisScale.Logarithmic?Math.Pow(LogarithmicBase,value):value;
    public DoubleRange ScaleRange=>new(ToScale(VisibleRange.Min),ToScale(VisibleRange.Max));
    public void SetScaleRange(DoubleRange range)=>VisibleRange=new(FromScale(range.Min),FromScale(range.Max));
    public string Format(double value,bool cursor=false)
    {
        if(LabelProvider is not null) return LabelProvider.Format(value,this,cursor);
        string format=cursor&&!string.IsNullOrEmpty(CursorTextFormatting)?CursorTextFormatting:TextFormatting;
        if(Scale==AxisScale.Category)
        {
            int index=(int)Math.Round(value);
            return index>=0&&index<Labels.Count?Labels[index]:value.ToString("0",CultureInfo.InvariantCulture);
        }
        if(Scale==AxisScale.DateTime)
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds((long)value).UtcDateTime.ToString(string.IsNullOrEmpty(format)?(VisibleRange.Size<86_400_000?"HH:mm:ss":"dd MMM yyyy"):format,CultureInfo.InvariantCulture); }
            catch(ArgumentOutOfRangeException) { return value.ToString("G6",CultureInfo.InvariantCulture); }
        }
        if(Scale==AxisScale.TimeSpan)
        {
            try { return TimeSpan.FromMilliseconds(value).ToString(string.IsNullOrEmpty(format)?@"hh\:mm\:ss":format,CultureInfo.InvariantCulture); }
            catch(OverflowException) { return value.ToString("G6",CultureInfo.InvariantCulture); }
        }
        return value.ToString(string.IsNullOrEmpty(format)?"G6":format,CultureInfo.InvariantCulture);
    }
}
public sealed class NumericAxis:AxisModel { }
public sealed class LogarithmicNumericAxis:AxisModel { public LogarithmicNumericAxis()=>Scale=AxisScale.Logarithmic; }
public sealed class DateTimeAxis:AxisModel { public DateTimeAxis()=>Scale=AxisScale.DateTime; }
public sealed class CategoryDateTimeAxis:AxisModel { public CategoryDateTimeAxis()=>Scale=AxisScale.Category; }
public sealed class TimeSpanAxis:AxisModel { public TimeSpanAxis()=>Scale=AxisScale.TimeSpan; }

/// <summary>Immutable double-precision coordinate transform; convert to floats only after viewport projection.</summary>
public readonly struct CoordinateCalculator
{
    private readonly double _min,_size,_pixelStart,_pixelSize,_logBase;
    private readonly bool _log,_reverse;
    public CoordinateCalculator(AxisModel axis,double pixelStart,double pixelSize)
    {
        if(!double.IsFinite(pixelStart)||!double.IsFinite(pixelSize)||pixelSize<=0) throw new ArgumentOutOfRangeException(nameof(pixelSize));
        _log=axis.Scale==AxisScale.Logarithmic; _logBase=axis.LogarithmicBase;
        _min=axis.ToScale(axis.VisibleRange.Min); _size=axis.ToScale(axis.VisibleRange.Max)-_min;
        _pixelStart=pixelStart; _pixelSize=pixelSize; _reverse=axis.IsVertical^axis.FlippedCoordinates;
    }
    public double GetCoordinate(double value)
    {
        if(_log) value=value>0?Math.Log(value,_logBase):double.NaN;
        double fraction=(value-_min)/_size;
        return _pixelStart+(_reverse?1-fraction:fraction)*_pixelSize;
    }
    public double GetDataValue(double pixel)
    {
        double fraction=(pixel-_pixelStart)/_pixelSize;
        double value=_min+(_reverse?1-fraction:fraction)*_size;
        return _log?Math.Pow(_logBase,value):value;
    }
}

public static class TickGenerator
{
    public static IReadOnlyList<AxisTick> Generate(AxisModel axis,double pixelLength)
    {
        int target=Math.Clamp((int)(pixelLength/(axis.IsVertical?60:100)),2,30);
        if(axis.Scale==AxisScale.Logarithmic) return LogTicks(axis,target);
        if(axis.Scale==AxisScale.DateTime&&axis.VisibleRange.Size/target>20*86_400_000.0) return CalendarTicks(axis,target);
        double step=double.IsFinite(axis.MajorDelta)?axis.MajorDelta:NiceStep(axis.VisibleRange.Size/target);
        if(axis.Scale==AxisScale.Category) step=Math.Max(1,Math.Ceiling(step));
        if(axis.Scale is AxisScale.DateTime or AxisScale.TimeSpan&&!double.IsFinite(axis.MajorDelta))
        {
            double desired=axis.VisibleRange.Size/target;
            double[] steps=[1,5,10,50,100,250,500,1000,5000,10000,15000,30000,60000,300000,600000,900000,1800000,3600000,10800000,21600000,43200000,86400000,172800000,604800000,1209600000];
            step=steps.FirstOrDefault(s=>s>=desired,step);
        }
        var ticks=new List<AxisTick>();
        if(!double.IsFinite(step)||step<=0) return ticks;
        double first=Math.Ceiling(axis.VisibleRange.Min/step)*step;
        for(int i=0;i<2048;i++)
        {
            double value=first+i*step;
            if(!double.IsFinite(value)||value>axis.VisibleRange.Max+step*1e-9) break;
            if(value>=axis.VisibleRange.Min-step*1e-9) ticks.Add(new(value,axis.Format(Math.Abs(value)<step*1e-12?0:value)));
            if(axis.Scale!=AxisScale.Category)
                for(int j=1;j<5;j++) { double minor=value+j*step/5; if(minor>=axis.VisibleRange.Min&&minor<=axis.VisibleRange.Max) ticks.Add(new(minor,"",false)); }
        }
        return ticks.OrderBy(t=>t.Value).ToArray();
    }
    public static double NiceStep(double raw)
    {
        if(raw<=0||!double.IsFinite(raw)) return 1;
        double magnitude=Math.Pow(10,Math.Floor(Math.Log10(raw)));
        if(magnitude==0) return raw;
        double f=raw/magnitude;
        return (f<=1?1:f<=2?2:f<=2.5?2.5:f<=5?5:10)*magnitude;
    }
    private static IReadOnlyList<AxisTick> LogTicks(AxisModel axis,int target)
    {
        var r=axis.ScaleRange; var ticks=new List<AxisTick>();
        if(r.Size<1)
        {
            double step=NiceStep(axis.VisibleRange.Size/target),first=Math.Ceiling(axis.VisibleRange.Min/step)*step;
            for(int i=0;i<2048;i++) { double v=first+i*step; if(v>axis.VisibleRange.Max) break; if(v>0) ticks.Add(new(v,axis.Format(v))); }
            return ticks;
        }
        int stride=Math.Max(1,(int)Math.Ceiling(r.Size/target));
        int firstExp=(int)Math.Floor(r.Min),lastExp=(int)Math.Ceiling(r.Max);
        for(int e=firstExp;e<=lastExp&&ticks.Count<4096;e++)
        {
            double major=Math.Pow(axis.LogarithmicBase,e);
            if(e%stride==0&&axis.VisibleRange.Contains(major)) ticks.Add(new(major,axis.Format(major)));
            if(axis.LogarithmicBase==10&&stride==1) for(int k=2;k<10;k++) { double minor=major*k; if(axis.VisibleRange.Contains(minor)) ticks.Add(new(minor,"",false)); }
        }
        return ticks;
    }
    private static IReadOnlyList<AxisTick> CalendarTicks(AxisModel axis,int target)
    {
        var ticks=new List<AxisTick>();
        try
        {
            var first=DateTimeOffset.FromUnixTimeMilliseconds((long)axis.VisibleRange.Min);
            double months=axis.VisibleRange.Size/target/(30.436875*86_400_000);
            int stride=months<=1?1:months<=3?3:months<=6?6:12*(int)Math.Ceiling(months/12);
            int absolute=first.Year*12+first.Month-1; int aligned=(int)Math.Ceiling(absolute/(double)stride)*stride;
            for(int i=0;i<2048;i++)
            {
                int month=aligned+i*stride,year=month/12;
                if(year<1||year>9999) break;
                var dt=new DateTimeOffset(year,month%12+1,1,0,0,0,TimeSpan.Zero); double value=dt.ToUnixTimeMilliseconds();
                if(value>axis.VisibleRange.Max) break;
                if(value>=axis.VisibleRange.Min) ticks.Add(new(value,string.IsNullOrEmpty(axis.TextFormatting)?dt.ToString(stride>=12?"yyyy":"MMM yyyy",CultureInfo.InvariantCulture):axis.Format(value)));
            }
        }
        catch(ArgumentOutOfRangeException) { }
        return ticks;
    }
}
