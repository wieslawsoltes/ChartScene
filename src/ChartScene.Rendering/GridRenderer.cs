using ChartScene.Core;
using ChartScene.Charting;
using ChartScene.Data;

namespace ChartScene.Rendering;

public sealed partial class ChartRenderer
{
    private void DrawGridSeries(IChartCanvas c,RenderableSeries series,HeatmapGrid grid,SeriesProjection projection)
    {
        double min=double.PositiveInfinity,max=double.NegativeInfinity;
        foreach(double value in grid.Values.Span) if(double.IsFinite(value)) { min=Math.Min(min,value); max=Math.Max(max,value); }
        if(!double.IsFinite(min)) return;
        var range=series.ColorMapRange??DoubleRange.FromExtents(min,max);
        if(series.Kind==SeriesKind.Contour)
        {
            double[] levels=series.ContourLevels.Count>0?series.ContourLevels.Where(double.IsFinite).ToArray():Enumerable.Range(1,10).Select(i=>range.At(i/11.0)).ToArray();
            foreach(var segment in Contours.MarchingSquares(grid,levels))
            {
                PointD a=projection.ToScreen(segment.A.X,segment.A.Y),b=projection.ToScreen(segment.B.X,segment.B.Y);
                if(Geometry.ClipLine(projection.Plot,ref a,ref b)) c.DrawLine(a,b,new(Alpha(series.ColorMap.Sample(range.Fraction(segment.Level)),series.Opacity),series.StrokeThickness));
                _renderedPoints+=2;
            }
            return;
        }
        bool uniform=IsUniform(grid.XEdges.Span)&&IsUniform(grid.YEdges.Span)&&projection.XAxis.Scale!=AxisScale.Logarithmic&&projection.YAxis.Scale!=AxisScale.Logarithmic;
        if(uniform)
        {
            var key=(grid,series.ColorMap,range,series.Opacity);
            if(!_rasterCache.TryGetValue(key,out var image))
            {
                if(_rasterCache.Count>=8) _rasterCache.Clear();
                var pixels=new byte[checked(grid.Width*grid.Height*4)];
                for(int i=0;i<grid.Values.Length;i++)
                {
                    var color=Alpha(series.ColorMap.Sample(range.Fraction(grid.Values.Span[i])),series.Opacity);
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
            for(int y=0;y<grid.Height;y++) for(int x=0;x<grid.Width;x++)
            {
                double value=grid[x,y]; if(!double.IsFinite(value)) continue;
                PointD a=projection.ToScreen(grid.XEdges.Span[x],grid.YEdges.Span[y]),b=projection.ToScreen(grid.XEdges.Span[x+1],grid.YEdges.Span[y+1]);
                if(!a.IsFinite||!b.IsFinite) continue;
                var rectangle=RectD.FromPoints(a,b); if(!rectangle.Intersects(projection.Plot)) continue;
                DrawBoundedRect(c,rectangle,Alpha(series.ColorMap.Sample(range.Fraction(value)),series.Opacity),null,projection.Plot);
                _renderedPoints++;
            }
        }
    }
    private static bool IsUniform(ReadOnlySpan<double> edges)
    {
        double step=edges[1]-edges[0];
        for(int i=2;i<edges.Length;i++) if(Math.Abs((edges[i]-edges[i-1])-step)>Math.Max(1e-12,Math.Abs(step)*1e-10)) return false;
        return true;
    }
}

public readonly record struct ContourSegment(PointD A,PointD B,double Level);
public static class Contours
{
    /// <summary>Marching squares over cell centers, with an asymptotic determinant for saddle topology.</summary>
    public static IEnumerable<ContourSegment> MarchingSquares(HeatmapGrid grid,IReadOnlyList<double> levels)
    {
        double X(int i)=>(grid.XEdges.Span[i]+grid.XEdges.Span[i+1])/2;
        double Y(int i)=>(grid.YEdges.Span[i]+grid.YEdges.Span[i+1])/2;
        foreach(double level in levels)
        {
            if(!double.IsFinite(level)) throw new ArgumentException("Contour levels must be finite.");
            for(int y=0;y<grid.Height-1;y++) for(int x=0;x<grid.Width-1;x++)
            {
                double[] values=[grid[x,y],grid[x+1,y],grid[x+1,y+1],grid[x,y+1]];
                if(values.Any(v=>!double.IsFinite(v))) continue;
                PointD[] corners=[new(X(x),Y(y)),new(X(x+1),Y(y)),new(X(x+1),Y(y+1)),new(X(x),Y(y+1))];
                var crossings=new List<(int Edge,PointD Point)>();
                for(int edge=0;edge<4;edge++)
                {
                    int next=(edge+1)%4;
                    bool a=values[edge]>=level,b=values[next]>=level;
                    if(a==b) continue;
                    double t=(level-values[edge])/(values[next]-values[edge]);
                    crossings.Add((edge,corners[edge]+(corners[next]-corners[edge])*t));
                }
                if(crossings.Count==2) yield return new(crossings[0].Point,crossings[1].Point,level);
                else if(crossings.Count==4)
                {
                    double determinant=(values[0]-level)*(values[2]-level)-(values[1]-level)*(values[3]-level);
                    bool connect01=determinant>=0;
                    if(connect01)
                    {
                        yield return new(crossings[0].Point,crossings[1].Point,level);
                        yield return new(crossings[2].Point,crossings[3].Point,level);
                    }
                    else
                    {
                        yield return new(crossings[0].Point,crossings[3].Point,level);
                        yield return new(crossings[1].Point,crossings[2].Point,level);
                    }
                }
            }
        }
    }
}
