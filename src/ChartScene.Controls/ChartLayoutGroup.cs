using ChartScene.Core;
using ChartScene.Rendering;
using Microsoft.UI.Xaml;

namespace ChartScene.Controls;

/// <summary>
/// Synchronizes plot-edge reservations for sibling Uno charts, without changing their data,
/// ranges or models. Horizontal alignment is for vertically stacked charts; Vertical alignment
/// is for side-by-side charts. Their outer widths/heights must agree to align both edges.
/// </summary>
public sealed class ChartLayoutGroup : IDisposable
{
    private readonly List<Link> _links = [];
    private bool _disposed;
    public PlotLayoutGroup Layout { get; }
    public int Count => _links.Count;
    public ChartLayoutGroup(PlotAlignment alignment = PlotAlignment.Horizontal) => Layout = new(alignment);
    public IDisposable Add(ChartSurface surface)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(surface);
        if (_links.Any(link => ReferenceEquals(link.Surface,surface))) throw new ArgumentException("The chart already belongs to this group.",nameof(surface));
        var link = new Link(this,surface); _links.Add(link); return new ActionDisposable(link.Dispose);
    }
    private sealed class Link : IDisposable
    {
        private readonly ChartLayoutGroup _owner;
        private readonly long _modelToken, _marginToken, _visibilityToken;
        private PlotLayoutGroup.Member? _member;
        private bool _disposed;
        public ChartSurface Surface { get; }
        public Link(ChartLayoutGroup owner, ChartSurface surface)
        {
            _owner=owner; Surface=surface;
            // Reserve membership even while unloaded, preventing two groups from fighting
            // over the same view. The control never retains a reference to the group/model.
            surface.ClaimLayout(this);
            _modelToken=surface.RegisterPropertyChangedCallback(ChartSurface.ModelProperty,(_,_)=>Reset());
            _marginToken=surface.RegisterPropertyChangedCallback(ChartSurface.MinimumPlotMarginsProperty,(_,_)=>Report());
            _visibilityToken=surface.RegisterPropertyChangedCallback(UIElement.VisibilityProperty,(_,_)=>Reset());
            surface.Loaded+=Loaded; surface.Unloaded+=Unloaded; surface.RenderCompleted+=Rendered;
            try { Attach(); } catch { Dispose(); throw; }
        }
        private void Loaded(object sender,RoutedEventArgs e)=>Attach();
        private void Unloaded(object sender,RoutedEventArgs e)=>Detach();
        private void Rendered(object? sender,ChartFrame frame)=>Report();
        private void Attach()
        {
            if(_disposed||!Surface.IsLoaded||Surface.Visibility!=Visibility.Visible||_member is not null)return;
            _member=_owner.Layout.Add(); _member.Changed+=MarginsChanged; Apply(); Report();
        }
        private void Detach()
        {
            if(_member is not null){_member.Changed-=MarginsChanged;_member.Dispose();_member=null;}
            Surface.SetGroupMargins(this,default);
        }
        private void Reset(){Detach();Attach();}
        private void Report()
        {
            if(_disposed||_member is null||Surface.LastFrame is not { } frame)return;
            // A new measurement always excludes this group's existing expanded margins.
            _member.Report(PlotMargins.Max(frame.Layout.NaturalMargins,Surface.MinimumPlotMargins));
        }
        private void MarginsChanged(object? sender,EventArgs e)=>Apply();
        private void Apply()=>Surface.SetGroupMargins(this,_member?.MinimumMargins??default);
        public void Dispose()
        {
            if(_disposed)return;_disposed=true;
            Surface.Loaded-=Loaded;Surface.Unloaded-=Unloaded;Surface.RenderCompleted-=Rendered;
            Surface.UnregisterPropertyChangedCallback(ChartSurface.ModelProperty,_modelToken);
            Surface.UnregisterPropertyChangedCallback(ChartSurface.MinimumPlotMarginsProperty,_marginToken);
            Surface.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty,_visibilityToken);
            Detach();Surface.ReleaseLayout(this);_owner._links.Remove(this);
        }
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        foreach(var link in _links.ToArray())link.Dispose();
        Layout.Dispose();
    }
}
