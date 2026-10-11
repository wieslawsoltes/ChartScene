using ChartScene.Core;
using ChartScene.Charting;
using ChartScene.Data;

namespace ChartScene.Rendering;

public sealed partial class ChartRenderer
{
    private void DrawGridSeries(IChartCanvas c,RenderableSeries series,HeatmapGrid grid,SeriesProjection projection)
    {
        var scale=HeatmapColorScale.Capture(series);
        if(!scale.HasValues) return;
        var range=scale.Range;
        if(series.Kind==SeriesKind.Contour)
        {
            double[] levels=series.ContourLevels.Count>0?series.ContourLevels.Where(double.IsFinite).ToArray():Enumerable.Range(1,10).Select(i=>scale.ValueAt(i/11.0)).ToArray();
            foreach(var segment in ContourGeometryCache.Capture(grid,levels))
            {
                PointD a=projection.ToScreen(segment.A.X,segment.A.Y),b=projection.ToScreen(segment.B.X,segment.B.Y);
                if(Geometry.ClipLine(projection.Plot,ref a,ref b)) c.DrawLine(a,b,new(scale.ColorAt(segment.Level),series.StrokeThickness));
                _renderedPoints+=2;
            }
            return;
        }
        bool uniform=GridVisibility.IsUniform(grid)&&projection.XAxis.Scale!=AxisScale.Logarithmic&&projection.YAxis.Scale!=AxisScale.Logarithmic;
        if(uniform)
        {
            var key=(grid,series.ColorMap,range,series.ColorScaleMode,series.Opacity);
            if(!_rasterCache.TryGetValue(key,out var image))
            {
                if(_rasterCache.Count>=8) _rasterCache.Clear();
                var pixels=new byte[checked(grid.Width*grid.Height*4)];
                for(int i=0;i<grid.Values.Length;i++)
                {
                    var color=scale.ColorAt(grid.Values.Span[i]);
                    pixels[4*i]=color.R; pixels[4*i+1]=color.G; pixels[4*i+2]=color.B; pixels[4*i+3]=color.A;
                }
                image=new(grid.Width,grid.Height,pixels); _rasterCache[key]=image;
            }
            PointD first=projection.ToScreen(grid.XEdges.Span[0],grid.YEdges.Span[0]),last=projection.ToScreen(grid.XEdges.Span[^1],grid.YEdges.Span[^1]);
            if(first.IsFinite&&last.IsFinite) c.DrawImage(image,RectD.FromPoints(first,last),first.X>last.X,first.Y>last.Y);
            _renderedPoints+=(long)grid.Width*grid.Height;
        }
        else
        {
            var cells=GridVisibility.VisibleCells(grid,projection.XAxis.VisibleRange,projection.YAxis.VisibleRange);
            for(int y=cells.FirstRow;y<cells.EndRow;y++) for(int x=cells.FirstColumn;x<cells.EndColumn;x++)
            {
                double value=grid[x,y]; if(!double.IsFinite(scale.Fraction(value))) continue;
                PointD a=projection.ToScreen(grid.XEdges.Span[x],grid.YEdges.Span[y]),b=projection.ToScreen(grid.XEdges.Span[x+1],grid.YEdges.Span[y+1]);
                if(!a.IsFinite||!b.IsFinite) continue;
                var rectangle=RectD.FromPoints(a,b); if(!rectangle.Intersects(projection.Plot)) continue;
                DrawBoundedRect(c,rectangle,scale.ColorAt(value),null,projection.Plot);
                _renderedPoints++;
            }
        }
    }

}

public readonly record struct ContourSegment(PointD A,PointD B,double Level);
public static class Contours
{
    /// <summary>Allocation-free cell evaluation; gaps are omitted and saddle topology uses an asymptotic determinant.</summary>
    public static IEnumerable<ContourSegment> MarchingSquares(HeatmapGrid grid,IReadOnlyList<double> levels)
    {
        ArgumentNullException.ThrowIfNull(grid); ArgumentNullException.ThrowIfNull(levels);
        foreach(double level in levels)
        {
            if(!double.IsFinite(level)) throw new ArgumentException("Contour levels must be finite.");
            for(int y=0;y<grid.Height-1;y++) for(int x=0;x<grid.Width-1;x++)
            {
                int count=Cell(grid,x,y,level,out var first,out var second);
                if(count>0) yield return first;
                if(count>1) yield return second;
            }
        }
    }
    private static int Cell(HeatmapGrid grid,int x,int y,double level,out ContourSegment first,out ContourSegment second)
    {
        first=second=default;
        double a=grid[x,y],b=grid[x+1,y],c=grid[x+1,y+1],d=grid[x,y+1];
        if(!double.IsFinite(a)||!double.IsFinite(b)||!double.IsFinite(c)||!double.IsFinite(d)) return 0;
        int mask=(a>=level?1:0)|(b>=level?2:0)|(c>=level?4:0)|(d>=level?8:0);
        if(mask is 0 or 15) return 0;
        double x0=grid.XEdges.Span[x]+(grid.XEdges.Span[x+1]-grid.XEdges.Span[x])/2;
        double x1=grid.XEdges.Span[x+1]+(grid.XEdges.Span[x+2]-grid.XEdges.Span[x+1])/2;
        double y0=grid.YEdges.Span[y]+(grid.YEdges.Span[y+1]-grid.YEdges.Span[y])/2;
        double y1=grid.YEdges.Span[y+1]+(grid.YEdges.Span[y+2]-grid.YEdges.Span[y+1])/2;
        Span<double> values=stackalloc double[]{a,b,c,d};
        Span<PointD> corners=stackalloc PointD[]{new(x0,y0),new(x1,y0),new(x1,y1),new(x0,y1)};
        Span<PointD> crossings=stackalloc PointD[4]; int count=0;
        for(int edge=0;edge<4;edge++)
        {
            int next=(edge+1)%4;
            if((values[edge]>=level)==(values[next]>=level)) continue;
            double denominator=values[next]-values[edge];
            double t=double.IsFinite(denominator)?(level-values[edge])/denominator:
                (level*.5-values[edge]*.5)/(values[next]*.5-values[edge]*.5);
            crossings[count++]=corners[edge]+(corners[next]-corners[edge])*t;
        }
        if(count==2) { first=new(crossings[0],crossings[1],level); return 1; }
        if(count!=4) return 0;
        double maximum=Math.Max(Math.Abs(level),Math.Max(Math.Max(Math.Abs(a),Math.Abs(b)),Math.Max(Math.Abs(c),Math.Abs(d))));
        // Normalize before subtraction/products so finite large-value saddles do not overflow.
        double aa=a/maximum-level/maximum,bb=b/maximum-level/maximum,cc=c/maximum-level/maximum,dd=d/maximum-level/maximum;
        bool connect01=aa*cc-bb*dd>=0;
        first=new(crossings[0],crossings[connect01?1:3],level);
        second=connect01?new(crossings[2],crossings[3],level):new(crossings[1],crossings[2],level);
        return 2;
    }
}
