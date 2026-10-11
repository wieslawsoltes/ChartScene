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

/// <summary>
/// Atomic columnar ring storage. Input buffers must remain unchanged until the synchronous
/// operation returns. Snapshots own their storage; notifications run after releasing the lock.
/// Exact adjacent-inversion counts make point edits and sorted FIFO eviction constant-time.
/// </summary>
public class DataSeries : IDataSeries
{
    private readonly object _gate = new();
    private readonly int _channelCount;
    private double[][] _columns;
    private int _head, _count, _fifoCapacity, _suspend, _inversions;
    private long _revision;
    private bool _pending, _acceptsUnsortedData;
    private SeriesSnapshot? _snapshot;
    public string SeriesName { get; set; } = "Series";
    public int ChannelCount => _channelCount;
    public int Count { get { lock (_gate) return _count; } }
    public int Capacity { get { lock (_gate) return _columns[0].Length; } }
    public long Revision { get { lock (_gate) return _revision; } }
    public bool AcceptsUnsortedData
    {
        get { lock (_gate) return _acceptsUnsortedData; }
        set
        {
            lock (_gate)
            {
                if (!value && _inversions != 0) throw new InvalidOperationException("Sort or clear the data before disabling unsorted input.");
                _acceptsUnsortedData = value;
            }
        }
    }
    public int FifoCapacity
    {
        get { lock (_gate) return _fifoCapacity; }
        set
        {
            if (value < 0 || value > Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(value));
            bool notify;
            lock (_gate)
            {
                if (value == _fifoCapacity) return;
                int keep = value == 0 ? _count : Math.Min(_count, value), start = _count - keep;
                int remainingInversions = _inversions == 0 ? 0 : CountEdges(start + 1, _count - 1);
                Reallocate(Math.Max(4, value == 0 ? keep : value), start, keep);
                _fifoCapacity = value; _inversions = remainingInversions; notify = Changed();
            }
            Notify(notify);
        }
    }
    public event EventHandler? DataSeriesChanged;
    public DataSeries(int channelCount = 2, int capacity = 256)
    {
        if (channelCount < 2) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (capacity < 0 || capacity > Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(capacity));
        _channelCount = channelCount;
        _columns = Allocate(Math.Max(4, capacity));
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
        ArgumentNullException.ThrowIfNull(values); AppendPoint(values);
    }
    /// <summary>Allocation-free point path used by typed series; validation precedes mutation.</summary>
    protected void AppendPoint(ReadOnlySpan<double> values)
    {
        ValidatePoint(values); bool notify;
        lock (_gate)
        {
            ValidateNextX(values[0]); EnsureCapacity(_fifoCapacity > 0 ? Math.Min((long)_count + 1, _fifoCapacity) : (long)_count + 1);
            if (_fifoCapacity > 0 && _count == _fifoCapacity) EvictPrefix(1);
            if (_count > 0 && values[0] < Read(0, _count - 1)) _inversions++;
            int physical = Physical(_count);
            for (int channel = 0; channel < ChannelCount; channel++) _columns[channel][physical] = values[channel];
            _count++; notify = Changed();
        }
        Notify(notify);
    }
    public void AppendRange(params double[][] columns)
    {
        int length = ValidateColumns(columns); if (length == 0) return;
        bool notify;
        lock (_gate)
        {
            ValidateNextX(columns[0][0]);
            if (!_acceptsUnsortedData) ValidateOrder(columns[0]);
            int skip = _fifoCapacity == 0 ? 0 : Math.Max(0, length - _fifoCapacity), retained = length - skip;
            long total = (long)_count + retained;
            EnsureCapacity(_fifoCapacity > 0 ? Math.Min(total, _fifoCapacity) : total);
            if (_fifoCapacity > 0) EvictPrefix((int)Math.Max(0, total - _fifoCapacity));
            if (_count > 0 && columns[0][skip] < Read(0, _count - 1)) _inversions++;
            _inversions += Descents(columns[0].AsSpan(skip, retained));
            for (int channel = 0; channel < ChannelCount; channel++) WriteRing(channel, _count, columns[channel].AsSpan(skip, retained));
            _count += retained; notify = Changed();
        }
        Notify(notify);
    }
    public void Update(int index, params double[] values)
    {
        ArgumentNullException.ThrowIfNull(values); UpdatePoint(index, values);
    }
    protected void UpdatePoint(int index, ReadOnlySpan<double> values)
    {
        ValidatePoint(values); bool notify;
        lock (_gate)
        {
            CheckIndex(index); ValidateReplacementOrder(index, values[0], values[0], 1);
            int removed = CountEdges(index, index + 1);
            int physical = Physical(index);
            for (int channel = 0; channel < ChannelCount; channel++) _columns[channel][physical] = values[channel];
            _inversions += CountEdges(index, index + 1) - removed;
            notify = Changed();
        }
        Notify(notify);
    }
    /// <summary>Atomically replaces an existing interval, without inserting or changing its length.</summary>
    public void UpdateRange(int index, params double[][] columns)
    {
        int length = ValidateColumns(columns); bool notify;
        lock (_gate)
        {
            CheckRange(index, length); if (length == 0) return;
            if (!_acceptsUnsortedData) ValidateOrder(columns[0]);
            ValidateReplacementOrder(index, columns[0][0], columns[0][^1], length);
            int removed = _inversions == 0 ? 0 : CountEdges(index, index + length);
            for (int channel = 0; channel < ChannelCount; channel++) WriteRing(channel, index, columns[channel]);
            _inversions += CountEdges(index, index + length) - removed;
            notify = Changed();
        }
        Notify(notify);
    }
    /// <summary>Replaces one channel over an existing interval, retaining all other channel values.</summary>
    public void UpdateChannelRange(int index, int channel, ReadOnlySpan<double> values)
    {
        if ((uint)channel >= (uint)ChannelCount) throw new ArgumentOutOfRangeException(nameof(channel));
        bool notify;
        lock (_gate)
        {
            CheckRange(index, values.Length); if (values.IsEmpty) return;
            Span<double> row = ChannelCount <= 64 ? stackalloc double[ChannelCount] : new double[ChannelCount];
            // Validate every affected row before any writes, including derived OHLC/box constraints.
            for (int i = 0; i < values.Length; i++)
            {
                for (int c = 0; c < ChannelCount; c++) row[c] = Read(c, index + i);
                row[channel] = values[i]; ValidatePoint(row);
            }
            if (channel == 0)
            {
                if (!_acceptsUnsortedData) ValidateOrder(values);
                ValidateReplacementOrder(index, values[0], values[^1], values.Length);
            }
            int removed = channel == 0 && _inversions > 0 ? CountEdges(index, index + values.Length) : 0;
            WriteRing(channel, index, values);
            if (channel == 0) _inversions += CountEdges(index, index + values.Length) - removed;
            notify = Changed();
        }
        Notify(notify);
    }
    /// <summary>
    /// Validates and replaces all columns in one publication, without an observable Clear/Append gap.
    /// FIFO keeps the newest capacity samples, but validates the complete submitted batch first.
    /// </summary>
    public void ReplaceAll(params double[][] columns)
    {
        int length = ValidateColumns(columns); bool notify;
        lock (_gate)
        {
            if (!_acceptsUnsortedData) ValidateOrder(columns[0]);
            if (length == 0 && _count == 0) return;
            int keep = _fifoCapacity == 0 ? length : Math.Min(length, _fifoCapacity), start = length - keep;
            var next = Allocate(Math.Max(4, _fifoCapacity == 0 ? keep : _fifoCapacity));
            for (int c = 0; c < ChannelCount; c++) columns[c].AsSpan(start, keep).CopyTo(next[c]);
            int inversions = Descents(columns[0].AsSpan(start, keep));
            _columns = next; _head = 0; _count = keep; _inversions = inversions; notify = Changed();
        }
        Notify(notify);
    }
    public void InsertRange(int index, params double[][] columns)
    {
        int length = ValidateColumns(columns); bool notify;
        lock (_gate)
        {
            if ((uint)index > (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            if (length == 0) return;
            if (_fifoCapacity > 0) throw new InvalidOperationException("Indexed insertion is not available on a FIFO series; append or disable FIFO first.");
            if (!_acceptsUnsortedData)
            {
                ValidateOrder(columns[0]);
                if (index > 0 && columns[0][0] < Read(0, index - 1) || index < _count && columns[0][^1] > Read(0, index)) throw new ArgumentException("Inserted X values violate ordering.");
            }
            long needed = (long)_count + length;
            if (needed > Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(columns), "Data exceeds the supported array length.");
            var replacement = Allocate(Math.Max(_columns[0].Length, (int)needed));
            for (int c = 0; c < ChannelCount; c++)
            {
                CopyRing(c, 0, replacement[c].AsSpan(0, index));
                columns[c].CopyTo(replacement[c], index);
                CopyRing(c, index, replacement[c].AsSpan(index + length, _count - index));
            }
            int inversions = _inversions - Edge(index) + Descents(columns[0]);
            if (index > 0 && columns[0][0] < Read(0, index - 1)) inversions++;
            if (index < _count && Read(0, index) < columns[0][^1]) inversions++;
            _columns = replacement; _head = 0; _count += length; _inversions = inversions; notify = Changed();
        }
        Notify(notify);
    }
    public void RemoveRange(int index, int count)
    {
        bool notify;
        lock (_gate)
        {
            CheckRange(index, count); if (count == 0) return;
            if (index == 0) EvictPrefix(count);
            else
            {
                int removed = _inversions == 0 ? 0 : CountEdges(index, index + count);
                int bridge = index + count < _count && Read(0, index + count) < Read(0, index - 1) ? 1 : 0;
                for (int i = index; i < _count - count; i++)
                    for (int c = 0; c < ChannelCount; c++) _columns[c][Physical(i)] = Read(c, i + count);
                _count -= count; _inversions += bridge - removed;
            }
            notify = Changed();
        }
        Notify(notify);
    }
    public void Clear()
    {
        bool notify;
        lock (_gate) { if (_count == 0) return; _head = _count = _inversions = 0; notify = Changed(); }
        Notify(notify);
    }
    public SeriesSnapshot Snapshot()
    {
        lock (_gate)
        {
            if (_snapshot is not null) return _snapshot;
            var result = new double[ChannelCount][];
            for (int c = 0; c < ChannelCount; c++) { result[c] = new double[_count]; CopyRing(c, 0, result[c]); }
            return _snapshot = new(result, _revision, _inversions == 0, true);
        }
    }
    /// <summary>Override for additional row invariants. Called for every mutating entry point before writes.</summary>
    protected virtual void ValidateRow(ReadOnlySpan<double> values) { }
    private void ValidatePoint(ReadOnlySpan<double> values)
    {
        if (values.Length != ChannelCount) throw new ArgumentException("Channel count mismatch.");
        for (int c = 0; c < values.Length; c++)
            if (double.IsInfinity(values[c]) || c == 0 && !double.IsFinite(values[c])) throw new ArgumentException("X must be finite; other channels may contain NaN gaps but not infinity.");
        ValidateRow(values);
    }
    private int ValidateColumns(double[][] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Length != ChannelCount || columns.Any(c => c is null)) throw new ArgumentException("Channel count mismatch or null column.");
        int length = columns[0].Length;
        if (columns.Any(c => c.Length != length)) throw new ArgumentException("Channel lengths must match.");
        Span<double> row = ChannelCount <= 64 ? stackalloc double[ChannelCount] : new double[ChannelCount];
        for (int i = 0; i < length; i++)
        {
            for (int c = 0; c < ChannelCount; c++) row[c] = columns[c][i];
            ValidatePoint(row);
        }
        return length;
    }
    private static void ValidateOrder(ReadOnlySpan<double> x)
    { for (int i = 1; i < x.Length; i++) if (x[i] < x[i - 1]) throw new ArgumentException("X values must be sorted."); }
    private void ValidateNextX(double x)
    { if (!_acceptsUnsortedData && _count > 0 && x < Read(0, _count - 1)) throw new ArgumentException("X values must be sorted. Enable AcceptsUnsortedData for unsorted input."); }
    private void ValidateReplacementOrder(int index, double first, double last, int length)
    {
        if (!_acceptsUnsortedData && (index > 0 && first < Read(0, index - 1) || index + length < _count && last > Read(0, index + length)))
            throw new ArgumentException("X values must remain sorted.");
    }
    private void CheckIndex(int index)
    { if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index)); }
    private void CheckRange(int index, int count)
    { if (index < 0 || count < 0 || count > _count || index > _count - count) throw new ArgumentOutOfRangeException(nameof(index)); }
    private static int Descents(ReadOnlySpan<double> x)
    { int count = 0; for (int i = 1; i < x.Length; i++) if (x[i] < x[i - 1]) count++; return count; }
    private int Edge(int index) => index > 0 && index < _count && Read(0, index) < Read(0, index - 1) ? 1 : 0;
    private int CountEdges(int first, int last)
    { int count = 0; for (int i = Math.Max(1, first); i <= Math.Min(_count - 1, last); i++) count += Edge(i); return count; }
    private void EvictPrefix(int count)
    {
        if (count == 0) return;
        if (count >= _count) { _head = _count = _inversions = 0; return; }
        if (_inversions > 0) _inversions -= CountEdges(1, count);
        _head = Physical(count); _count -= count;
    }
    private int Physical(int index)
    { int remaining = _columns[0].Length - _head; return index < remaining ? _head + index : index - remaining; }
    private double Read(int channel, int index) => _columns[channel][Physical(index)];
    private void CopyRing(int channel, int start, Span<double> destination)
    {
        if (destination.IsEmpty) return;
        int physical = Physical(start), first = Math.Min(destination.Length, _columns[channel].Length - physical);
        _columns[channel].AsSpan(physical, first).CopyTo(destination);
        _columns[channel].AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
    }
    private void WriteRing(int channel, int start, ReadOnlySpan<double> source)
    {
        if (source.IsEmpty) return;
        int physical = Physical(start), first = Math.Min(source.Length, _columns[channel].Length - physical);
        source[..first].CopyTo(_columns[channel].AsSpan(physical, first));
        source[first..].CopyTo(_columns[channel]);
    }
    private double[][] Allocate(int capacity)
    {
        var result = new double[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++) result[c] = new double[capacity];
        return result;
    }
    private void EnsureCapacity(long requested)
    {
        if (requested <= _columns[0].Length) return;
        if (requested > Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(requested), "Data exceeds the supported array length.");
        int capacity = (int)Math.Min(Array.MaxLength, Math.Max(requested, (long)_columns[0].Length * 2));
        Reallocate(capacity, 0, _count);
    }
    private void Reallocate(int capacity, int start, int count)
    {
        var next = Allocate(capacity);
        for (int c = 0; c < ChannelCount; c++) CopyRing(c, start, next[c].AsSpan(0, count));
        _columns = next; _head = 0; _count = count;
    }
    private bool Changed()
    { _revision++; _snapshot = null; if (_suspend > 0) { _pending = true; return false; } return true; }
    private void Notify(bool notify) { if (notify) DataSeriesChanged?.Invoke(this, EventArgs.Empty); }
}
