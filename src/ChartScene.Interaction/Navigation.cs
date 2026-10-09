using System.ComponentModel;
using ChartScene.Core;
using ChartScene.Charting;

namespace ChartScene.Interaction;

public interface IUndoableCommand { string Description { get; } void Execute(); void Undo(); }
public sealed class DelegateCommand(string description,Action execute,Action undo):IUndoableCommand
{
    public string Description=>description;
    public void Execute()=>execute();
    public void Undo()=>undo();
}
public sealed class UndoManager
{
    private readonly List<IUndoableCommand> _undo=[];
    private readonly List<IUndoableCommand> _redo=[];
    public int Capacity { get; set; }=100;
    public bool CanUndo=>_undo.Count>0;
    public bool CanRedo=>_redo.Count>0;
    public event EventHandler? Changed;
    public void Execute(IUndoableCommand command) { command.Execute(); RecordExecuted(command); }
    public void RecordExecuted(IUndoableCommand command)
    {
        _undo.Add(command); _redo.Clear();
        while(_undo.Count>Math.Max(1,Capacity)) _undo.RemoveAt(0);
        Changed?.Invoke(this,EventArgs.Empty);
    }
    public bool Undo() { if(!CanUndo) return false; var command=_undo[^1]; command.Undo(); _undo.RemoveAt(_undo.Count-1); _redo.Add(command); Changed?.Invoke(this,EventArgs.Empty); return true; }
    public bool Redo() { if(!CanRedo) return false; var command=_redo[^1]; command.Execute(); _redo.RemoveAt(_redo.Count-1); _undo.Add(command); Changed?.Invoke(this,EventArgs.Empty); return true; }
    public void Clear() { _undo.Clear(); _redo.Clear(); Changed?.Invoke(this,EventArgs.Empty); }
}

public sealed class ViewportSnapshot
{
    public IReadOnlyDictionary<AxisModel,DoubleRange> Ranges { get; }
    public ViewportSnapshot(ChartModel model)=>Ranges=model.XAxes.Concat(model.YAxes).ToDictionary(a=>a,a=>a.VisibleRange);
    public void Restore() { foreach(var pair in Ranges) { pair.Key.AutoRange=AutoRange.Never; pair.Key.VisibleRange=pair.Value; } }
    public bool Matches(ViewportSnapshot other)=>Ranges.Count==other.Ranges.Count&&Ranges.All(p=>other.Ranges.TryGetValue(p.Key,out var range)&&range==p.Value);
}

/// <summary>All registered axes synchronize through canonical units; propagation is guarded against feedback.</summary>
public sealed class RangeSyncGroup:IDisposable
{
    private sealed record Registration(AxisModel Axis,Func<double,double> ToCanonical,Func<double,double> FromCanonical,PropertyChangedEventHandler Handler);
    private readonly List<Registration> _axes=[];
    private bool _updating,_disposed;
    private int _suspend;
    private DoubleRange? _pending;
    public string Id { get; }
    public RangeSyncGroup(string id)=>Id=id;
    public IDisposable Add(AxisModel axis,Func<double,double>? toCanonical=null,Func<double,double>? fromCanonical=null,bool synchronizeImmediately=true)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(_axes.Any(r=>ReferenceEquals(r.Axis,axis))) throw new ArgumentException("Axis already belongs to this synchronization group.");
        Func<double,double> to=toCanonical??(x=>x),from=fromCanonical??(x=>x);
        PropertyChangedEventHandler handler=(_,e)=> { if(e.PropertyName==nameof(AxisModel.VisibleRange)) Publish(axis,to); };
        var registration=new Registration(axis,to,from,handler);
        DoubleRange? existing=_axes.Count>0?Transform(_axes[0].Axis.VisibleRange,_axes[0].ToCanonical):null;
        _axes.Add(registration); axis.PropertyChanged+=handler;
        if(synchronizeImmediately&&existing is { } range) { _updating=true; try { axis.VisibleRange=Transform(range,from); } finally { _updating=false; } }
        return new ActionDisposable(()=> { axis.PropertyChanged-=handler; _axes.Remove(registration); });
    }
    public IDisposable SuspendUpdates()
    {
        _suspend++;
        return new ActionDisposable(()=> { if(--_suspend==0&&_pending is { } range) { _pending=null; Apply(range); } });
    }
    public void SetCanonicalRange(DoubleRange range)
    {
        if(_suspend>0) { _pending=range; return; }
        Apply(range);
    }
    private void Publish(AxisModel axis,Func<double,double> convert)
    {
        if(_updating||_disposed) return;
        SetCanonicalRange(Transform(axis.VisibleRange,convert));
    }
    private void Apply(DoubleRange range)
    {
        // Validate all transformed intervals before any axis changes, preventing partial synchronization.
        var updates=_axes.Select(r=>(r.Axis,Range:Transform(range,r.FromCanonical))).ToArray();
        if(updates.Any(u=>u.Axis.Scale==AxisScale.Logarithmic&&u.Range.Min<=0)) throw new ArgumentOutOfRangeException(nameof(range),"Synchronized range is invalid for a logarithmic axis.");
        _updating=true;
        try { foreach(var update in updates) update.Axis.VisibleRange=update.Range; }
        finally { _updating=false; }
    }
    private static DoubleRange Transform(DoubleRange range,Func<double,double> convert)
    {
        double a=convert(range.Min),b=convert(range.Max);
        return new(Math.Min(a,b),Math.Max(a,b));
    }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true;
        foreach(var r in _axes) r.Axis.PropertyChanged-=r.Handler;
        _axes.Clear();
    }
}

/// <summary>Deterministic clock-driven viewport transitions. The host supplies timestamps and invalidation.</summary>
public sealed class ViewportAnimator
{
    private readonly List<(AxisModel Axis,DoubleRange Start,DoubleRange Target)> _axes=[];
    private double _start,_duration;
    public bool IsRunning { get; private set; }
    public void Begin(ChartModel model,IReadOnlyDictionary<AxisModel,DoubleRange> targets,double nowMilliseconds,double durationMilliseconds=250)
    {
        if(!double.IsFinite(durationMilliseconds)||durationMilliseconds<0) throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        _axes.Clear();
        foreach(var target in targets) _axes.Add((target.Key,target.Key.ScaleRange,new(target.Key.ToScale(target.Value.Min),target.Key.ToScale(target.Value.Max))));
        _start=nowMilliseconds; _duration=durationMilliseconds; IsRunning=true; Tick(nowMilliseconds);
    }
    public bool Tick(double nowMilliseconds)
    {
        if(!IsRunning) return false;
        double t=_duration==0?1:Math.Clamp((nowMilliseconds-_start)/_duration,0,1),eased=1-Math.Pow(1-t,3);
        foreach(var a in _axes) { a.Axis.AutoRange=AutoRange.Never; a.Axis.SetScaleRange(new(a.Start.Min+(a.Target.Min-a.Start.Min)*eased,a.Start.Max+(a.Target.Max-a.Start.Max)*eased)); }
        if(t>=1) IsRunning=false;
        return IsRunning;
    }
    public void Cancel()=>IsRunning=false;
}
