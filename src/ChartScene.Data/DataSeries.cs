using ChartScene.Core;

namespace ChartScene.Data;

public interface IDataSeries
{
    int Count { get; }
    int ChannelCount { get; }
    long Revision { get; }
    string SeriesName { get; set; }
    event EventHandler? DataSeriesChanged;
    SeriesSnapshot Snapshot();
}

/// <summary>A stable read-only copy, cached until the next mutation. Readers never observe partial appends.</summary>
public sealed class SeriesSnapshot
{
    private readonly double[][] _columns;
    public int Count { get; }
    public int ChannelCount => _columns.Length;
    public long Revision { get; }
    public bool IsSortedAscending { get; }
    public SeriesSnapshot(double[][] columns, long revision, bool sorted)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Length < 2 || columns.Any(c => c is null || c.Length != columns[0].Length)) throw new ArgumentException("All channels must have equal lengths.");
        _columns = columns.Select(c => (double[])c.Clone()).ToArray();
        Count = columns[0].Length; Revision = revision; IsSortedAscending = sorted;
    }
    internal SeriesSnapshot(double[][] ownedColumns, long revision, bool sorted, bool takeOwnership)
    { _columns = ownedColumns; Count = ownedColumns[0].Length; Revision = revision; IsSortedAscending = sorted; }
    public ReadOnlyMemory<double> Channel(int index) => _columns[index];
    public double this[int channel, int index] => _columns[channel][index];
    public int LowerBound(double x)
    {
        if (!IsSortedAscending) throw new InvalidOperationException("Binary search requires sorted X data.");
        int lo = 0, hi = Count;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (_columns[0][mid] < x) lo = mid + 1; else hi = mid; }
        return lo;
    }
    public int UpperBound(double x)
    {
        if (!IsSortedAscending) throw new InvalidOperationException("Binary search requires sorted X data.");
        int lo = 0, hi = Count;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (_columns[0][mid] <= x) lo = mid + 1; else hi = mid; }
        return lo;
    }
    public (int Start, int End) VisibleIndices(DoubleRange range, bool neighbors = true)
    {
        if (!IsSortedAscending) return (0, Count);
        return (Math.Max(0, LowerBound(range.Min) - (neighbors ? 1 : 0)), Math.Min(Count, UpperBound(range.Max) + (neighbors ? 1 : 0)));
    }
    public DoubleRange? Extents(int channel, DoubleRange? xRange = null, bool positiveOnly = false)
    {
        var (start, end) = xRange is { } r ? VisibleIndices(r, false) : (0, Count);
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = start; i < end; i++)
        {
            if (xRange is { } xr && !xr.Contains(_columns[0][i])) continue;
            double v = _columns[channel][i];
            if (!double.IsFinite(v) || positiveOnly && v <= 0) continue;
            min = Math.Min(min, v); max = Math.Max(max, v);
        }
        return double.IsFinite(min) ? DoubleRange.FromExtents(min, max) : null;
    }
}

/// <summary>Columnar ring storage; all multi-channel changes are validated before an atomic mutation.</summary>
public class DataSeries : IDataSeries
{
    private readonly object _gate = new();
    private double[][] _columns;
    private int _head, _count, _fifoCapacity, _suspend;
    private long _revision;
    private bool _pending, _sorted = true, _acceptsUnsortedData;
    private SeriesSnapshot? _snapshot;
    public string SeriesName { get; set; } = "Series";
    public int ChannelCount => _columns.Length;
    public int Count { get { lock (_gate) return _count; } }
    public long Revision { get { lock (_gate) return _revision; } }
    public bool AcceptsUnsortedData
    {
        get { lock (_gate) return _acceptsUnsortedData; }
        set { lock (_gate) { if (!value && !_sorted) throw new InvalidOperationException("Sort or clear the data before disabling unsorted input."); _acceptsUnsortedData = value; } }
    }
    public int FifoCapacity
    {
        get { lock (_gate) return _fifoCapacity; }
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            bool notify;
            lock (_gate)
            {
                if (value == _fifoCapacity) return;
                int keep = value == 0 ? _count : Math.Min(_count, value);
                Reallocate(Math.Max(4, value == 0 ? keep : value), _count - keep, keep);
                _fifoCapacity = value; RecheckOrder(); notify = Changed();
            }
            Notify(notify);
        }
    }
    public event EventHandler? DataSeriesChanged;
    public DataSeries(int channelCount = 2, int capacity = 256)
    {
        if (channelCount < 2 || capacity < 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
        _columns = Enumerable.Range(0, channelCount).Select(_ => new double[Math.Max(4, capacity)]).ToArray();
    }
    public IDisposable SuspendUpdates()
    {
        lock (_gate) _suspend++;
        return new ActionDisposable(() =>
        {
            bool notify;
            lock (_gate) { _suspend--; notify = _suspend == 0 && _pending; if (notify) _pending = false; }
            Notify(notify);
        });
    }
    public void Append(params double[] values)
    {
        ValidatePoint(values);
        bool notify;
        lock (_gate)
        {
            ValidateNextX(values[0]);
            EnsureCapacity(_count + 1);
            if (_fifoCapacity > 0 && _count == _fifoCapacity) { _head = (_head + 1) % _columns[0].Length; _count--; }
            if (_count > 0 && values[0] < Read(0, _count-1)) _sorted = false;
            int index = Physical(_count++);
            for (int c = 0; c < ChannelCount; c++) _columns[c][index] = values[c];
            notify = Changed();
        }
        Notify(notify);
    }
    public void AppendRange(params double[][] columns)
    {
        ValidateColumns(columns);
        bool notify;
        lock (_gate)
        {
            if (columns[0].Length == 0) return;
            ValidateNextX(columns[0][0]);
            if (!_acceptsUnsortedData) ValidateOrder(columns[0]);
            EnsureCapacity(_count + columns[0].Length);
            for (int i = 0; i < columns[0].Length; i++)
            {
                if (_fifoCapacity > 0 && _count == _fifoCapacity) { _head = (_head + 1) % _columns[0].Length; _count--; }
                if (_count > 0 && columns[0][i] < Read(0, _count-1)) _sorted = false;
                int index = Physical(_count++);
                for (int c = 0; c < ChannelCount; c++) _columns[c][index] = columns[c][i];
            }
            notify = Changed();
        }
        Notify(notify);
    }
    public void Update(int index, params double[] values)
    {
        ValidatePoint(values); bool notify;
        lock (_gate)
        {
            CheckIndex(index);
            if (!_acceptsUnsortedData && (index > 0 && values[0] < Read(0,index-1) || index+1 < _count && values[0] > Read(0,index+1))) throw new ArgumentException("X values must remain sorted.");
            for (int c = 0; c < ChannelCount; c++) _columns[c][Physical(index)] = values[c];
            RecheckOrder(); notify = Changed();
        }
        Notify(notify);
    }
    public void InsertRange(int index, params double[][] columns)
    {
        ValidateColumns(columns); bool notify;
        lock (_gate)
        {
            if (index < 0 || index > _count) throw new ArgumentOutOfRangeException(nameof(index));
            if (columns[0].Length == 0) return;
            if (_fifoCapacity > 0) throw new InvalidOperationException("Indexed insertion is not available on a FIFO series; append or disable FIFO first.");
            if (!_acceptsUnsortedData)
            {
                ValidateOrder(columns[0]);
                if (index > 0 && columns[0][0] < Read(0,index-1) || index < _count && columns[0][^1] > Read(0,index)) throw new ArgumentException("Inserted X values violate ordering.");
            }
            int n = columns[0].Length;
            var replacement = Enumerable.Range(0,ChannelCount).Select(_ => new double[Math.Max(4,_count+n)]).ToArray();
            for (int c=0;c<ChannelCount;c++)
            {
                for (int i=0;i<index;i++) replacement[c][i]=Read(c,i);
                columns[c].CopyTo(replacement[c],index);
                for (int i=index;i<_count;i++) replacement[c][i+n]=Read(c,i);
            }
            _columns=replacement; _head=0; _count+=n; RecheckOrder(); notify=Changed();
        }
        Notify(notify);
    }
    public void RemoveRange(int index, int count)
    {
        bool notify;
        lock (_gate)
        {
            if (index < 0 || count < 0 || index > _count-count) throw new ArgumentOutOfRangeException(nameof(index));
            if (count==0) return;
            if (index==0) { _head=Physical(count); _count-=count; }
            else { for(int i=index;i<_count-count;i++) for(int c=0;c<ChannelCount;c++) _columns[c][Physical(i)]=Read(c,i+count); _count-=count; }
            RecheckOrder(); notify=Changed();
        }
        Notify(notify);
    }
    public void Clear()
    {
        bool notify;
        lock (_gate) { if (_count==0) return; _head=0; _count=0; _sorted=true; notify=Changed(); }
        Notify(notify);
    }
    public SeriesSnapshot Snapshot()
    {
        lock (_gate)
        {
            if (_snapshot is not null) return _snapshot;
            var result=Enumerable.Range(0,ChannelCount).Select(_=>new double[_count]).ToArray();
            for(int c=0;c<ChannelCount;c++) for(int i=0;i<_count;i++) result[c][i]=Read(c,i);
            return _snapshot=new(result,_revision,_sorted,true);
        }
    }
    private int Physical(int index)=>(_head+index)%_columns[0].Length;
    private double Read(int channel,int index)=>_columns[channel][Physical(index)];
    private void CheckIndex(int index) { if(index<0||index>=_count) throw new ArgumentOutOfRangeException(nameof(index)); }
    private void ValidateNextX(double x) { if(!_acceptsUnsortedData&&_count>0&&x<Read(0,_count-1)) throw new ArgumentException("X values must be sorted. Set AcceptsUnsortedData for scatter data."); }
    private static void ValidateOrder(double[] x) { for(int i=1;i<x.Length;i++) if(x[i]<x[i-1]) throw new ArgumentException("X values must be sorted."); }
    private void ValidatePoint(double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if(values.Length!=ChannelCount) throw new ArgumentException("Channel count mismatch.");
        for(int c=0;c<values.Length;c++) if(double.IsInfinity(values[c])||c==0&&!double.IsFinite(values[c])) throw new ArgumentException("X must be finite; other channels may contain NaN gaps but not infinity.");
    }
    private void ValidateColumns(double[][] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if(columns.Length!=ChannelCount||columns.Any(c=>c is null||c.Length!=columns[0].Length)) throw new ArgumentException("Channel count or length mismatch.");
        for(int c=0;c<ChannelCount;c++) foreach(double v in columns[c]) if(double.IsInfinity(v)||c==0&&!double.IsFinite(v)) throw new ArgumentException("Invalid non-finite data.");
    }
    private void EnsureCapacity(int requested)
    {
        if(_fifoCapacity>0) requested=Math.Min(requested,_fifoCapacity);
        if(requested<=_columns[0].Length) return;
        Reallocate(Math.Max(requested,checked(_columns[0].Length*2)),0,_count);
    }
    private void Reallocate(int capacity,int start,int count)
    {
        var next=Enumerable.Range(0,ChannelCount).Select(_=>new double[capacity]).ToArray();
        for(int c=0;c<ChannelCount;c++) for(int i=0;i<count;i++) next[c][i]=Read(c,start+i);
        _columns=next; _head=0; _count=count;
    }
    private void RecheckOrder() { _sorted=true; for(int i=1;i<_count;i++) if(Read(0,i)<Read(0,i-1)) { _sorted=false; break; } }
    private bool Changed() { _revision++; _snapshot=null; if(_suspend>0) { _pending=true; return false; } return true; }
    private void Notify(bool notify) { if(notify) DataSeriesChanged?.Invoke(this,EventArgs.Empty); }
}

public class XyDataSeries : DataSeries
{
    public XyDataSeries(int capacity=256):base(2,capacity) { }
    public void Append(double x,double y)=>base.Append(x,y);
    public void AppendRange(double[] x,double[] y)=>base.AppendRange(x,y);
    public void Update(int index,double x,double y)=>base.Update(index,x,y);
}
public sealed class XyDataSeries<TX,TY> : XyDataSeries
{
    public void Append(TX x,TY y)=>Append(Coordinate(x),Coordinate(y));
    public void AppendRange(IEnumerable<TX> x,IEnumerable<TY> y)=>AppendRange(x.Select(Coordinate).ToArray(),y.Select(Coordinate).ToArray());
    private static double Coordinate<T>(T value)=>value switch { DateTime dt=>new DateTimeOffset(dt.ToUniversalTime()).ToUnixTimeMilliseconds(), DateTimeOffset dt=>dt.ToUnixTimeMilliseconds(), TimeSpan t=>t.TotalMilliseconds, _=>Convert.ToDouble(value,System.Globalization.CultureInfo.InvariantCulture) };
}
public sealed class XyyDataSeries():DataSeries(3) { public void Append(double x,double y1,double y2)=>base.Append(x,y1,y2); }
public sealed class XyzDataSeries():DataSeries(3) { public void Append(double x,double y,double z)=>base.Append(x,y,z); }
public sealed class OhlcDataSeries():DataSeries(5)
{
    public void Append(double x,double open,double high,double low,double close)
    {
        if(low>Math.Min(open,close)||high<Math.Max(open,close)||low>high) throw new ArgumentException("OHLC extrema must contain open and close.");
        base.Append(x,open,high,low,close);
    }
}
public sealed class BoxPlotDataSeries():DataSeries(6)
{
    public void Append(double x,double minimum,double lowerQuartile,double median,double upperQuartile,double maximum)
    {
        if(minimum>lowerQuartile||lowerQuartile>median||median>upperQuartile||upperQuartile>maximum) throw new ArgumentException("Box plot statistics must be ordered.");
        base.Append(x,minimum,lowerQuartile,median,upperQuartile,maximum);
    }
}
public sealed class VectorFieldDataSeries():DataSeries(4) { public void Append(double x,double y,double dx,double dy)=>base.Append(x,y,dx,dy); }

/// <summary>Immutable uniform or non-uniform rectilinear cells, row-major Z with NaN transparency.</summary>
public sealed class HeatmapGrid
{
    private readonly double[] _x,_y,_z;
    public int Width=>_x.Length-1;
    public int Height=>_y.Length-1;
    public ReadOnlyMemory<double> XEdges=>_x;
    public ReadOnlyMemory<double> YEdges=>_y;
    public ReadOnlyMemory<double> Values=>_z;
    public double this[int x,int y]=>_z[checked(y*Width+x)];
    public HeatmapGrid(double[] xEdges,double[] yEdges,double[] values)
    {
        if(xEdges.Length<2||yEdges.Length<2||values.Length!=checked((xEdges.Length-1)*(yEdges.Length-1))) throw new ArgumentException("Grid dimensions do not match.");
        static void Edges(double[] a) { for(int i=0;i<a.Length;i++) if(!double.IsFinite(a[i])||i>0&&a[i]<=a[i-1]) throw new ArgumentException("Cell edges must be finite and strictly increasing."); }
        Edges(xEdges); Edges(yEdges);
        if(values.Any(double.IsInfinity)) throw new ArgumentException("Infinity is not a valid heatmap value.");
        _x=(double[])xEdges.Clone(); _y=(double[])yEdges.Clone(); _z=(double[])values.Clone();
    }
    public static HeatmapGrid Uniform(int width,int height,double xStart,double xStep,double yStart,double yStep,Func<int,int,double> value)
    {
        if(width<1||height<1||xStep<=0||yStep<=0) throw new ArgumentOutOfRangeException(nameof(width));
        return new(Enumerable.Range(0,width+1).Select(i=>xStart+i*xStep).ToArray(),Enumerable.Range(0,height+1).Select(i=>yStart+i*yStep).ToArray(),Enumerable.Range(0,checked(width*height)).Select(i=>value(i%width,i/width)).ToArray());
    }
}
