using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Charting;
using ChartScene.Rendering;

namespace ChartScene.Interaction;

/// <summary>Screen-space broad phase rebuilt from a rendered frame. Large regions use a separate overflow list.</summary>
public sealed class HitTestIndex
{
    private readonly Dictionary<(int X,int Y),List<HitRegion>> _cells=[];
    private readonly List<HitRegion> _large=[];
    private const double CellSize=48;
    public HitTestIndex(IEnumerable<HitRegion> regions)
    {
        foreach(var region in regions)
        {
            var r=region.Bounds; int x0=Cell(r.X),x1=Cell(r.Right),y0=Cell(r.Y),y1=Cell(r.Bottom);
            if((long)(x1-x0+1)*(y1-y0+1)>64) { _large.Add(region); continue; }
            for(int y=y0;y<=y1;y++) for(int x=x0;x<=x1;x++)
            {
                if(!_cells.TryGetValue((x,y),out var bucket)) _cells[(x,y)]=bucket=[];
                bucket.Add(region);
            }
        }
    }
    private static int Cell(double value)=>(int)Math.Clamp(Math.Floor(value/CellSize),int.MinValue+1.0,int.MaxValue-1.0);
    public IEnumerable<HitRegion> Query(PointD point,double radius)
    {
        var visited=new HashSet<HitRegion>();
        for(int y=Cell(point.Y-radius);y<=Cell(point.Y+radius);y++) for(int x=Cell(point.X-radius);x<=Cell(point.X+radius);x++)
            if(_cells.TryGetValue((x,y),out var bucket)) foreach(var region in bucket) if(visited.Add(region)&&region.Bounds.Inflate(radius).Contains(point)) yield return region;
        foreach(var region in _large) if(region.Bounds.Inflate(radius).Contains(point)) yield return region;
    }
}

public sealed class ChartHitTester
{
    private ChartFrame? _frame;
    private HitTestIndex? _index;
    public void SetFrame(ChartFrame frame) { _frame=frame; _index=new(frame.HitRegions); }
    public IReadOnlyList<SeriesHit> Query(ChartModel model,PointD point,double radius=10,bool nearestX=false,bool interpolate=false)
    {
        if(_frame is null||!_frame.Layout.PlotArea.Contains(point)) return [];
        var output=new Dictionary<RenderableSeries,SeriesHit>();
        if(!nearestX&&_index is not null)
        {
            foreach(var hit in _index.Query(point,radius))
            {
                if(hit.Series.DataSeries is not { } source) continue;
                var d=source.Snapshot(); if(hit.DataIndex>=d.Count) continue;
                double distance=hit.Radius>0?Math.Max(0,Math.Sqrt((point-hit.Position).LengthSquared)-hit.Radius):DistanceToRectangle(point,hit.Bounds);
                if(distance>radius) continue;
                var value=new SeriesHit(hit.Series,hit.DataIndex,d[0,hit.DataIndex],d[hit.Series.YChannel,hit.DataIndex],hit.Position,distance,Details(hit.Series,d,hit.DataIndex));
                if(!output.TryGetValue(hit.Series,out var old)||value.Distance<old.Distance) output[hit.Series]=value;
            }
        }
        var stacks=StackCalculator.Calculate(model);
        foreach(var series in model.RenderableSeries.Where(s=>s.IsVisible))
        {
            var projection=_frame.Layout.Projection(model,series);
            if(series.HeatmapGrid is { } grid)
            {
                PointD data=projection.ToData(point);
                int x=CellAt(grid.XEdges.Span,data.X),y=CellAt(grid.YEdges.Span,data.Y);
                if(x>=0&&y>=0&&double.IsFinite(grid[x,y]))
                {
                    var center=new PointD((grid.XEdges.Span[x]+grid.XEdges.Span[x+1])/2,(grid.YEdges.Span[y]+grid.YEdges.Span[y+1])/2);
                    output[series]=new(series,y*grid.Width+x,center.X,center.Y,projection.ToScreen(center.X,center.Y),0,$"Z={grid[x,y]:G6}");
                }
                continue;
            }
            if(series.DataSeries is not { } source||source.Count==0) continue;
            var snapshot=source.Snapshot();
            if(series.IsCircular)
            {
                var hit=PieHit(series,snapshot,_frame.Layout.PlotArea,point); if(hit is not null) output[series]=hit;
                continue;
            }
            if(!nearestX&&output.ContainsKey(series)) continue;
            var coordinates=projection.ToData(point);
            bool cartesian=model.CoordinateSystem==CoordinateSystem.Cartesian&&series.Kind is not (SeriesKind.PolarLine or SeriesKind.Radar or SeriesKind.TernaryLine or SeriesKind.TernaryScatter);
            int start=0,end=snapshot.Count;
            if(cartesian&&snapshot.IsSortedAscending)
            {
                if(nearestX)
                {
                    int position=snapshot.LowerBound(coordinates.X); start=Math.Max(0,position-1); end=Math.Min(snapshot.Count,position+2);
                }
                else
                {
                    var a=projection.ToData(new(point.X-radius,point.Y-radius)); var b=projection.ToData(new(point.X+radius,point.Y+radius));
                    start=Math.Max(0,snapshot.LowerBound(Math.Min(a.X,b.X))-1); end=Math.Min(snapshot.Count,snapshot.UpperBound(Math.Max(a.X,b.X))+1);
                }
            }
            StackedValues? stacked=stacks.GetValueOrDefault(series);
            double Y(int i)=>stacked is null?snapshot[series.YChannel,i]:stacked.Upper[i];
            SeriesHit? best=null;
            for(int i=start;i<end;i++)
            {
                double y=Y(i); var pixel=projection.ToScreen(snapshot[0,i],y,snapshot.ChannelCount>2?snapshot[2,i]:0);
                if(!pixel.IsFinite) continue;
                double distance=nearestX&&cartesian?Math.Abs(projection.XAxis.IsVertical?pixel.Y-point.Y:pixel.X-point.X):Math.Sqrt((pixel-point).LengthSquared);
                var candidate=new SeriesHit(series,i,snapshot[0,i],snapshot[series.YChannel,i],pixel,distance,Details(series,snapshot,i));
                if(best is null||distance<best.Distance) best=candidate;
                bool continuous=series.Kind is SeriesKind.Line or SeriesKind.DigitalLine or SeriesKind.Spline or SeriesKind.Mountain or SeriesKind.SplineMountain or SeriesKind.Band or SeriesKind.DigitalBand or SeriesKind.StackedMountain or SeriesKind.PolarLine or SeriesKind.Radar or SeriesKind.TernaryLine;
                if(!continuous||i+1>=snapshot.Count) continue;
                double nextY=Y(i+1); var next=projection.ToScreen(snapshot[0,i+1],nextY,snapshot.ChannelCount>2?snapshot[2,i+1]:0);
                if(!next.IsFinite) continue;
                double segmentDistance=Geometry.DistanceToSegment(point,pixel,next,out double t);
                if(interpolate&&cartesian&&snapshot[0,i+1]>snapshot[0,i]&&coordinates.X>=snapshot[0,i]&&coordinates.X<=snapshot[0,i+1])
                {
                    double fraction=(coordinates.X-snapshot[0,i])/(snapshot[0,i+1]-snapshot[0,i]);
                    double interpolated=series.Kind is SeriesKind.DigitalLine or SeriesKind.DigitalBand?snapshot[series.YChannel,i]:snapshot[series.YChannel,i]+fraction*(snapshot[series.YChannel,i+1]-snapshot[series.YChannel,i]);
                    var screen=projection.ToScreen(coordinates.X,interpolated);
                    best=new(series,i,coordinates.X,interpolated,screen,nearestX?0:Math.Sqrt((screen-point).LengthSquared),"interpolated");
                    if(nearestX) break;
                }
                else if(!nearestX&&segmentDistance<(best?.Distance??double.PositiveInfinity))
                    best=new(series,t<.5?i:i+1,snapshot[0,t<.5?i:i+1],snapshot[series.YChannel,t<.5?i:i+1],pixel+(next-pixel)*t,segmentDistance);
            }
            if(best is not null&&(nearestX||best.Distance<=radius)) output[series]=best;
        }
        return output.Values.OrderBy(hit=>hit.Distance).ToArray();
    }
    private static int CellAt(ReadOnlySpan<double> edges,double value)
    {
        if(!double.IsFinite(value)||value<edges[0]||value>edges[^1]) return -1;
        int lo=0,hi=edges.Length-1;
        while(lo+1<hi) { int mid=(lo+hi)/2; if(edges[mid]<=value) lo=mid; else hi=mid; }
        return Math.Min(lo,edges.Length-2);
    }
    private static double DistanceToRectangle(PointD p,RectD r)=>double.Hypot(Math.Max(r.X-p.X,Math.Max(0,p.X-r.Right)),Math.Max(r.Y-p.Y,Math.Max(0,p.Y-r.Bottom)));
    private static string? Details(RenderableSeries s,SeriesSnapshot d,int i)=>s.Kind switch
    {
        SeriesKind.Candlestick or SeriesKind.Ohlc=>$"O {d[1,i]:G5} H {d[2,i]:G5} L {d[3,i]:G5} C {d[4,i]:G5}",
        SeriesKind.Bubble=>$"Z {d[2,i]:G5}",
        SeriesKind.Band or SeriesKind.DigitalBand=>$"Y₂ {d[2,i]:G5}",
        SeriesKind.VectorField=>$"ΔX {d[2,i]:G5} ΔY {d[3,i]:G5}",
        SeriesKind.BoxPlot or SeriesKind.GroupedBoxPlot=>$"min {d[1,i]:G5} Q1 {d[2,i]:G5} Q3 {d[4,i]:G5} max {d[5,i]:G5}",
        _=>null
    };
    private static SeriesHit? PieHit(RenderableSeries s,SeriesSnapshot data,RectD plot,PointD point)
    {
        double radius=Math.Min(plot.Width,plot.Height)*.43,inner=s.Kind==SeriesKind.Donut?radius*Math.Clamp(s.DonutHoleRatio,0,.95):0;
        double distance=Math.Sqrt((point-plot.Center).LengthSquared); if(distance<inner||distance>radius) return null;
        double total=0; for(int i=0;i<data.Count;i++) if(double.IsFinite(data[1,i])&&data[1,i]>0) total+=data[1,i];
        if(total<=0) return null;
        double angle=Math.Atan2(point.Y-plot.Center.Y,point.X-plot.Center.X)+Math.PI/2; if(angle<0) angle+=2*Math.PI;
        double accumulated=0;
        for(int i=0;i<data.Count;i++)
        {
            if(!double.IsFinite(data[1,i])||data[1,i]<=0) continue;
            accumulated+=data[1,i]/total*2*Math.PI;
            if(angle<=accumulated) return new(s,i,data[0,i],data[1,i],point,0,$"{data[1,i]/total:P1}");
        }
        return null;
    }
}
