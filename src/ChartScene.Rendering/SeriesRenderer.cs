using ChartScene.Core;
using ChartScene.Charting;
using ChartScene.Data;
using ChartScene.Numerics;

namespace ChartScene.Rendering;

public sealed partial class ChartRenderer
{
    private void DrawSeries(IChartCanvas c,ChartModel model,RenderableSeries series,ChartLayout layout,StackedValues? stack)
    {
        var projection=layout.Projection(model,series);
        if(series.Kind is SeriesKind.Heatmap or SeriesKind.Contour)
        {
            if(series.HeatmapGrid is { } grid) DrawGridSeries(c,series,grid,projection);
            return;
        }
        if(series.DataSeries is not { } source) return;
        var data=source.Snapshot(); if(data.Count==0) return;
        if(series.IsCircular) { DrawPie(c,model,series,data,layout.PlotArea); return; }
        int[] indices=VisibleIndices(model,series,data,projection);
        if(indices.Length==0) return;
        _renderedPoints+=indices.Length;
        switch(series.Kind)
        {
            case SeriesKind.Column: case SeriesKind.Bar: case SeriesKind.StackedColumn: case SeriesKind.StackedBar:
            case SeriesKind.Candlestick: case SeriesKind.Ohlc: case SeriesKind.BoxPlot: case SeriesKind.GroupedBoxPlot:
            case SeriesKind.ErrorBars: case SeriesKind.Impulse:
                DrawDiscrete(c,model,series,data,indices,projection,stack); break;
            case SeriesKind.Band: case SeriesKind.DigitalBand:
                DrawBand(c,series,data,indices,projection); break;
            case SeriesKind.VectorField:
                DrawVectors(c,series,data,indices,projection); break;
            case SeriesKind.Scatter: case SeriesKind.Bubble: case SeriesKind.TernaryScatter:
                DrawScatter(c,series,data,indices,projection); break;
            default:
                DrawContinuous(c,model,series,data,indices,projection,stack); break;
        }
        if(series.ShowDataLabels) DrawDataLabels(c,model,series,data,indices,projection,stack);
    }
    private int[] VisibleIndices(ChartModel model,RenderableSeries series,SeriesSnapshot data,SeriesProjection p)
    {
        bool cartesian=model.CoordinateSystem==CoordinateSystem.Cartesian&&series.Kind is not (SeriesKind.PolarLine or SeriesKind.Radar or SeriesKind.TernaryLine or SeriesKind.TernaryScatter);
        if(!cartesian) return Enumerable.Range(0,data.Count).ToArray();
        bool reduce=series.Kind is SeriesKind.Line or SeriesKind.DigitalLine or SeriesKind.Mountain or SeriesKind.Impulse;
        if(reduce&&series.IsResamplingEnabled&&series.PaletteProvider is null)
            return SampleIndices(series,data,p.XAxis.VisibleRange,Math.Max(1,(int)(p.XAxis.IsVertical?p.Plot.Height:p.Plot.Width)),p.XAxis.Scale==AxisScale.Logarithmic);
        var(start,end)=data.VisibleIndices(p.XAxis.VisibleRange);
        return Enumerable.Range(start,end-start).ToArray();
    }
    private void DrawContinuous(IChartCanvas c,ChartModel model,RenderableSeries s,SeriesSnapshot d,int[] indices,SeriesProjection p,StackedValues? stack)
    {
        bool digital=s.Kind==SeriesKind.DigitalLine;
        bool spline=s.Kind is SeriesKind.Spline or SeriesKind.SplineMountain;
        bool mountain=s.Kind is SeriesKind.Mountain or SeriesKind.SplineMountain or SeriesKind.StackedMountain;
        bool radar=s.Kind==SeriesKind.Radar;
        bool polar=model.CoordinateSystem==CoordinateSystem.Polar||s.Kind is SeriesKind.PolarLine or SeriesKind.Radar;
        var run=new List<int>();
        void Flush()
        {
            if(run.Count==0) return;
            double Upper(int i)=>stack is null?d[s.YChannel,i]:stack.Upper[i];
            double Lower(int i)=>stack is null?(p.YAxis.Scale==AxisScale.Logarithmic&&s.ZeroLineY<=0?p.YAxis.VisibleRange.Min:s.ZeroLineY):stack.Lower[i];
            List<PointD> upper=BuildCurve(run,d,Upper,p,spline,digital,polar&&s.PolarConnectionMode==PolarConnectionMode.Arc&&!radar);
            if(mountain)
            {
                var lower=BuildCurve(run,d,Lower,p,spline,digital,false); lower.Reverse();
                DrawPolygon(c,upper.Concat(lower),Alpha(s.Fill,s.Opacity),null,p.Plot);
            }
            if(radar&&upper.Count>2) DrawPolygon(c,upper,Alpha(s.Fill,s.Opacity),null,p.Plot);
            var stroke=new StrokeStyle(Alpha(s.Stroke,s.Opacity),s.StrokeThickness+(s.IsSelected?1.5:0));
            if(s.PaletteProvider is null) DrawClippedPolyline(c,upper,stroke,p.Plot,radar);
            else
            {
                for(int j=1;j<run.Count;j++)
                {
                    int i=run[j]; var segment=BuildCurve([run[j-1],i],d,Upper,p,false,digital,polar&&s.PolarConnectionMode==PolarConnectionMode.Arc);
                    DrawClippedPolyline(c,segment,stroke with {Color=Alpha(s.PaletteProvider.GetStroke(s,d,i)??s.Stroke,s.Opacity)},p.Plot);
                }
            }
            if(s.PointMarker!=PointMarker.None)
            {
                var markers=run.Select(i=>p.ToScreen(d[0,i],Upper(i),d.ChannelCount>2?d[2,i]:0)).Where(p.Plot.Inflate(s.MarkerSize).Contains).ToArray();
                c.DrawMarkers(markers,s.PointMarker,s.MarkerSize,Alpha(s.Stroke,s.Opacity));
            }
            foreach(int i in run.Where(s.SelectedIndices.Contains))
            {
                var point=p.ToScreen(d[0,i],Upper(i),d.ChannelCount>2?d[2,i]:0);
                if(point.IsFinite&&p.Plot.Contains(point)) c.DrawEllipse(new(point.X-5,point.Y-5,10,10),null,new(model.Theme.Foreground,2));
            }
            run.Clear();
        }
        foreach(int i in indices)
        {
            double value=stack is null?d[s.YChannel,i]:stack.Upper[i];
            var point=p.ToScreen(d[0,i],value,d.ChannelCount>2?d[2,i]:0);
            if(!point.IsFinite||mountain&&stack is not null&&!double.IsFinite(stack.Lower[i])) { Flush(); continue; }
            run.Add(i);
        }
        Flush();
    }
    private static List<PointD> BuildCurve(IReadOnlyList<int> run,SeriesSnapshot d,Func<int,double> y,SeriesProjection p,bool spline,bool digital,bool polarArc)
    {
        var result=new List<PointD>(run.Count*2);
        MonotoneSpline? interpolator=null;
        if(spline&&run.Count>=2)
        {
            double[] x=run.Select(i=>d[0,i]).ToArray(),values=run.Select(y).ToArray();
            if(x.Zip(x.Skip(1),(a,b)=>a<b).All(v=>v)&&values.All(double.IsFinite)) interpolator=new(x,values);
        }
        for(int j=0;j<run.Count;j++)
        {
            int i=run[j]; double x=d[0,i],value=y(i),z=d.ChannelCount>2?d[2,i]:0;
            var point=p.ToScreen(x,value,z);
            if(j>0)
            {
                int previous=run[j-1]; double px=d[0,previous],py=y(previous);
                if(digital) result.Add(p.ToScreen(x,py));
                else if(interpolator is not null||polarArc)
                {
                    PointD last=p.ToScreen(px,py,d.ChannelCount>2?d[2,previous]:0);
                    double distance=Math.Sqrt((point-last).LengthSquared);
                    int subdivisions=Math.Clamp((int)Math.Ceiling(Math.Min(192,distance)/3),2,64);
                    if(polarArc) subdivisions=Math.Max(subdivisions,(int)Math.Min(128,Math.Ceiling(Math.Abs(x-px)/3)));
                    for(int k=1;k<subdivisions;k++)
                    {
                        double t=(double)k/subdivisions,xx=px+(x-px)*t,yy=interpolator is null?py+(value-py)*t:interpolator.Evaluate(xx);
                        var q=p.ToScreen(xx,yy,z); if(q.IsFinite) result.Add(q);
                    }
                }
            }
            if(point.IsFinite) result.Add(point);
        }
        return result;
    }
    private void DrawScatter(IChartCanvas c,RenderableSeries s,SeriesSnapshot d,int[] indices,SeriesProjection p)
    {
        var marker=s.PointMarker==PointMarker.None?PointMarker.Ellipse:s.PointMarker;
        bool bubbles=s.Kind==SeriesKind.Bubble;
        DoubleRange? colors=s.ColorMapRange??(bubbles?d.Extents(2):null);
        var batch=new List<PointD>();
        foreach(int i in indices)
        {
            var point=p.ToScreen(d[0,i],d[1,i],d.ChannelCount>2?d[2,i]:0);
            if(!point.IsFinite) continue;
            double size=bubbles?(double.IsFinite(d[2,i])?Math.Max(2,Math.Sqrt(Math.Abs(d[2,i]))*s.MarkerSize):0):s.MarkerSize;
            if(size<=0) continue;
            size=Math.Min(size,Math.Max(p.Plot.Width,p.Plot.Height)*2);
            var bounds=new RectD(point.X-size/2,point.Y-size/2,size,size);
            if(!bounds.Intersects(p.Plot)) continue;
            var fill=Alpha(s.PaletteProvider?.GetFill(s,d,i)??(bubbles&&colors is { } range?s.ColorMap.Sample(range.Fraction(d[2,i])):s.Stroke),s.Opacity);
            var stroke=Alpha(s.PaletteProvider?.GetStroke(s,d,i)??s.Stroke,s.Opacity);
            if(!bubbles&&s.PaletteProvider is null) batch.Add(point);
            else c.DrawMarkers([point],marker,size,fill,new(stroke,s.StrokeThickness));
            _hits.Add(new(s,i,bounds,point,size/2));
        }
        if(batch.Count>0) c.DrawMarkers(batch.ToArray(),marker,s.MarkerSize,Alpha(s.Stroke,s.Opacity));
        foreach(int i in indices.Where(s.SelectedIndices.Contains))
        {
            var point=p.ToScreen(d[0,i],d[1,i],d.ChannelCount>2?d[2,i]:0);
            if(point.IsFinite&&p.Plot.Contains(point)) c.DrawEllipse(new(point.X-s.MarkerSize/2-3,point.Y-s.MarkerSize/2-3,s.MarkerSize+6,s.MarkerSize+6),null,new(ColorRgba.White,2));
        }
    }
    private void DrawDiscrete(IChartCanvas c,ChartModel model,RenderableSeries s,SeriesSnapshot d,int[] indices,SeriesProjection p,StackedValues? stack)
    {
        bool IsColumn(RenderableSeries t)=>t.Kind is SeriesKind.Column or SeriesKind.Bar or SeriesKind.StackedColumn or SeriesKind.StackedBar;
        var peers=model.RenderableSeries.Where(t=>t.IsVisible&&t.XAxisId==s.XAxisId&&t.YAxisId==s.YAxisId&&t.IsHorizontal==s.IsHorizontal&&((s.Kind==SeriesKind.GroupedBoxPlot&&t.Kind==s.Kind)||(IsColumn(s)&&IsColumn(t)))).ToArray();
        string Slot(RenderableSeries a)=>a.IsStacked?"stack:"+a.StackedGroupId:"series:"+a.Id;
        string[] slots=peers.Select(Slot).Distinct().ToArray();
        int slot=Math.Max(0,Array.IndexOf(slots,Slot(s))),slotCount=Math.Max(1,slots.Length);
        double baseline=p.YAxis.Scale==AxisScale.Logarithmic&&s.ZeroLineY<=0?p.YAxis.VisibleRange.Min:s.ZeroLineY;
        foreach(int i in indices)
        {
            double x=d[0,i],y=stack is null?d[s.YChannel,i]:stack.Upper[i],lo=stack is null?baseline:stack.Lower[i];
            if(!double.IsFinite(y)||!double.IsFinite(lo)) continue;
            var point=p.ToScreen(x,y); var bottom=p.ToScreen(x,lo);
            if(!point.IsFinite||!bottom.IsFinite) continue;
            bool horizontal=p.XAxis.IsVertical;
            double category=horizontal?point.Y:point.X;
            double Category(double xx) { var q=p.ToScreen(xx,y); return horizontal?q.Y:q.X; }
            double width;
            if(d.Count<=1) width=(horizontal?p.Plot.Height:p.Plot.Width)*.5;
            else
            {
                double left=i>0?Math.Abs(category-Category(d[0,i-1])):double.PositiveInfinity;
                double right=i+1<d.Count?Math.Abs(Category(d[0,i+1])-category):double.PositiveInfinity;
                width=Math.Min(left,right); if(!double.IsFinite(width)||width<=0) width=Math.Max(2,(horizontal?p.Plot.Height:p.Plot.Width)/Math.Max(1,d.Count));
            }
            width=Math.Clamp(width*s.DataPointWidth,1,Math.Max(p.Plot.Width,p.Plot.Height));
            double cell=width/slotCount,offset=-width/2+cell*(slot+.5),half=cell*.48;
            category+=offset;
            PointD At(double value)=>horizontal?new(p.ToScreen(x,value).X,category):new(category,p.ToScreen(x,value).Y);
            var color=Alpha(s.PaletteProvider?.GetStroke(s,d,i)??s.Stroke,s.Opacity);
            var fill=Alpha(s.PaletteProvider?.GetFill(s,d,i)??s.Fill,s.Opacity);
            var stroke=new StrokeStyle(color,s.StrokeThickness+(s.IsSelected?1:0));
            void Line(PointD a,PointD b) { if(Geometry.ClipLine(p.Plot,ref a,ref b)) c.DrawLine(a,b,stroke); }
            void Cap(double value,double size)
            {
                PointD q=At(value); if(horizontal) Line(new(q.X,q.Y-size),new(q.X,q.Y+size)); else Line(new(q.X-size,q.Y),new(q.X+size,q.Y));
            }
            RectD Body(double a,double b)
            {
                PointD pa=At(a),pb=At(b);
                return horizontal?new(Math.Min(pa.X,pb.X),category-half,Math.Max(1,Math.Abs(pa.X-pb.X)),2*half):new(category-half,Math.Min(pa.Y,pb.Y),2*half,Math.Max(1,Math.Abs(pa.Y-pb.Y)));
            }
            var bounds=Body(lo,y);
            switch(s.Kind)
            {
                case SeriesKind.Candlestick: case SeriesKind.Ohlc:
                {
                    if(Enumerable.Range(1,4).Any(ch=>!double.IsFinite(d[ch,i]))) continue;
                    bool up=d[4,i]>=d[1,i]; stroke=stroke with{Color=Alpha(up?s.Stroke:s.StrokeY1,s.Opacity)}; fill=Alpha(up?s.Fill:s.FillY1,s.Opacity);
                    Line(At(d[2,i]),At(d[3,i]));
                    if(s.Kind==SeriesKind.Candlestick) DrawBoundedRect(c,Body(d[1,i],d[4,i]),fill,stroke,p.Plot);
                    else
                    {
                        var open=At(d[1,i]); var close=At(d[4,i]);
                        Line(horizontal?new(open.X,open.Y-half):new(open.X-half,open.Y),open);
                        Line(close,horizontal?new(close.X,close.Y+half):new(close.X+half,close.Y));
                    }
                    bounds=Body(d[3,i],d[2,i]); break;
                }
                case SeriesKind.BoxPlot: case SeriesKind.GroupedBoxPlot:
                    if(Enumerable.Range(1,5).Any(ch=>!double.IsFinite(d[ch,i]))) continue;
                    Line(At(d[1,i]),At(d[5,i])); Cap(d[1,i],half*.65); Cap(d[5,i],half*.65);
                    DrawBoundedRect(c,Body(d[2,i],d[4,i]),fill,stroke,p.Plot); Cap(d[3,i],half); bounds=Body(d[1,i],d[5,i]); break;
                case SeriesKind.ErrorBars:
                    if(!double.IsFinite(d[2,i])||!double.IsFinite(d[3,i])) continue;
                    Line(At(d[2,i]),At(d[3,i])); Cap(d[2,i],Math.Max(3,s.MarkerSize)); Cap(d[3,i],Math.Max(3,s.MarkerSize));
                    if(p.Plot.Contains(point)) c.DrawMarkers([point],s.PointMarker==PointMarker.None?PointMarker.Ellipse:s.PointMarker,s.MarkerSize,color);
                    bounds=Body(d[2,i],d[3,i]); break;
                case SeriesKind.Impulse:
                    Line(point,bottom); if(s.PointMarker!=PointMarker.None&&p.Plot.Contains(point)) c.DrawMarkers([point],s.PointMarker,s.MarkerSize,color); break;
                default: DrawBoundedRect(c,bounds,fill.A==0?color:fill,stroke,p.Plot); break;
            }
            if(bounds.Intersects(p.Plot)) _hits.Add(new(s,i,bounds,At(y)));
        }
    }
    private static void DrawBoundedRect(IChartCanvas c,RectD rectangle,ColorRgba? fill,StrokeStyle? stroke,RectD clip)
    {
        if(!rectangle.Intersects(clip)) return;
        PointD[] points=[new(rectangle.X,rectangle.Y),new(rectangle.Right,rectangle.Y),new(rectangle.Right,rectangle.Bottom),new(rectangle.X,rectangle.Bottom)];
        if(fill is { } color) DrawPolygon(c,points,color,null,clip);
        if(stroke is { } line) DrawClippedPolyline(c,points,line,clip,true);
    }
    private void DrawBand(IChartCanvas c,RenderableSeries s,SeriesSnapshot d,int[] indices,SeriesProjection p)
    {
        for(int j=1;j<indices.Length;j++)
        {
            int a=indices[j-1],b=indices[j];
            if(!double.IsFinite(d[1,a])||!double.IsFinite(d[2,a])||!double.IsFinite(d[1,b])||!double.IsFinite(d[2,b])) continue;
            double xa=d[0,a],xb=d[0,b],a1=d[1,a],a2=d[2,a],b1=d[1,b],b2=d[2,b];
            if(s.Kind==SeriesKind.DigitalBand)
            {
                var points=new[]{p.ToScreen(xa,a1),p.ToScreen(xb,a1),p.ToScreen(xb,a2),p.ToScreen(xa,a2)};
                DrawPolygon(c,points,Alpha(a1>=a2?s.Fill:s.FillY1,s.Opacity),null,p.Plot);
                DrawClippedPolyline(c,[p.ToScreen(xa,a1),p.ToScreen(xb,a1),p.ToScreen(xb,b1)],new(Alpha(s.Stroke,s.Opacity),s.StrokeThickness),p.Plot);
                DrawClippedPolyline(c,[p.ToScreen(xa,a2),p.ToScreen(xb,a2),p.ToScreen(xb,b2)],new(Alpha(s.StrokeY1,s.Opacity),s.StrokeThickness),p.Plot);
            }
            else
            {
                double da=a1-a2,db=b1-b2;
                if(da*db<0)
                {
                    double t=da/(da-db),x=xa+(xb-xa)*t,y=a1+(b1-a1)*t; var cross=p.ToScreen(x,y);
                    DrawPolygon(c,[p.ToScreen(xa,a1),cross,p.ToScreen(xa,a2)],Alpha(da>=0?s.Fill:s.FillY1,s.Opacity),null,p.Plot);
                    DrawPolygon(c,[cross,p.ToScreen(xb,b1),p.ToScreen(xb,b2)],Alpha(db>=0?s.Fill:s.FillY1,s.Opacity),null,p.Plot);
                }
                else DrawPolygon(c,[p.ToScreen(xa,a1),p.ToScreen(xb,b1),p.ToScreen(xb,b2),p.ToScreen(xa,a2)],Alpha(da>=0?s.Fill:s.FillY1,s.Opacity),null,p.Plot);
                DrawClippedPolyline(c,[p.ToScreen(xa,a1),p.ToScreen(xb,b1)],new(Alpha(s.Stroke,s.Opacity),s.StrokeThickness),p.Plot);
                DrawClippedPolyline(c,[p.ToScreen(xa,a2),p.ToScreen(xb,b2)],new(Alpha(s.StrokeY1,s.Opacity),s.StrokeThickness),p.Plot);
            }
        }
    }
    private void DrawVectors(IChartCanvas c,RenderableSeries s,SeriesSnapshot d,int[] indices,SeriesProjection p)
    {
        double max=0;
        foreach(int i in indices) if(double.IsFinite(d[2,i])&&double.IsFinite(d[3,i])) max=Math.Max(max,double.Hypot(d[2,i],d[3,i]));
        var occupied=new HashSet<(int,int)>(); double spacing=Math.Max(1,s.VectorMinimumSpacing);
        foreach(int i in indices)
        {
            double dx=d[2,i],dy=d[3,i]; if(!double.IsFinite(dx)||!double.IsFinite(dy)) continue;
            var start=p.ToScreen(d[0,i],d[1,i]); if(!start.IsFinite||!p.Plot.Contains(start)) continue;
            if(s.VectorMinimumSpacing>0&&!occupied.Add(((int)((start.X-p.Plot.X)/spacing),(int)((start.Y-p.Plot.Y)/spacing)))) continue;
            var end=p.ToScreen(d[0,i]+dx*s.VectorScale,d[1,i]+dy*s.VectorScale);
            if(!end.IsFinite) continue;
            PointD delta=end-start; double length=Math.Sqrt(delta.LengthSquared),magnitude=double.Hypot(dx,dy);
            if(length<=1e-12) { c.DrawEllipse(new(start.X-1,start.Y-1,2,2),s.Stroke); continue; }
            if(s.VectorLengthMode!=VectorLengthMode.DataUnits)
            {
                double target=s.VectorLengthMode==VectorLengthMode.FixedPixels?s.VectorScale:magnitude*s.VectorScale;
                end=start+delta*(target/length);
            }
            double fraction=s.VectorColorMode==VectorColorMode.ByDirection?(Math.Atan2(dy,dx)+Math.PI)/(2*Math.PI):max>0?magnitude/max:0;
            ColorRgba color=Alpha(s.VectorColorMode==VectorColorMode.Solid?s.Stroke:s.ColorMap.Sample(fraction),s.Opacity);
            DrawArrow(c,start,end,new(color,s.StrokeThickness),s.ArrowHeadLength,s.ArrowHeadWidth,s.IsArrowHeadFilled,p.Plot);
            _hits.Add(new(s,i,RectD.FromPoints(start,end).Inflate(4),end));
        }
    }
    internal static void DrawArrow(IChartCanvas c,PointD start,PointD end,StrokeStyle stroke,double headLength,double headWidth,bool filled,RectD clip)
    {
        PointD direction=end-start; double length=Math.Sqrt(direction.LengthSquared);
        if(length<=1e-12||!double.IsFinite(length)) return;
        direction*=1/length; headLength=Math.Clamp(headLength,0,length*.8); headWidth=Math.Max(0,headWidth);
        var back=end-direction*headLength; var normal=new PointD(-direction.Y,direction.X)*headWidth;
        DrawClippedPolyline(c,[start,filled?back:end],stroke,clip);
        if(filled) DrawPolygon(c,[end,back+normal,back-normal],stroke.Color,null,clip);
        else DrawClippedPolyline(c,[back+normal,end,back-normal],stroke,clip);
    }
    private void DrawPie(IChartCanvas c,ChartModel model,RenderableSeries s,SeriesSnapshot d,RectD plot)
    {
        double total=0; for(int i=0;i<d.Count;i++) if(double.IsFinite(d[1,i])&&d[1,i]>0) total+=d[1,i];
        if(total<=0||!double.IsFinite(total)) return;
        double radius=Math.Min(plot.Width,plot.Height)*.43,inner=s.Kind==SeriesKind.Donut?radius*Math.Clamp(s.DonutHoleRatio,0,.95):0,angle=-Math.PI/2;
        for(int i=0;i<d.Count;i++)
        {
            double value=d[1,i]; if(!double.IsFinite(value)||value<=0) continue;
            double sweep=value/total*2*Math.PI; int steps=Math.Max(2,(int)Math.Ceiling(sweep*radius/3));
            var polygon=new List<PointD>();
            PointD At(double a,double r)=>new(plot.Center.X+Math.Cos(a)*r,plot.Center.Y+Math.Sin(a)*r);
            for(int j=0;j<=steps;j++) polygon.Add(At(angle+sweep*j/steps,radius));
            if(inner>0) for(int j=steps;j>=0;j--) polygon.Add(At(angle+sweep*j/steps,inner)); else polygon.Add(plot.Center);
            ColorRgba color=Alpha(s.PaletteProvider?.GetFill(s,d,i)??model.Theme.Palette[i%model.Theme.Palette.Count],s.Opacity);
            DrawPolygon(c,polygon,color,new(model.Theme.PlotBackground,1.5),plot);
            double middle=angle+sweep/2; var marker=At(middle,(radius+inner)/2);
            _hits.Add(new(s,i,new(marker.X-10,marker.Y-10,20,20),marker));
            if(sweep>.12)
            {
                string label=i<s.PointLabels.Count?s.PointLabels[i]:$"{100*value/total:0.#}%";
                var point=At(middle,radius+16);
                c.DrawText(label,new(point.X,point.Y+4),new(model.Theme.Foreground,11),Math.Cos(middle)>.2?TextAlignment.Left:Math.Cos(middle)<-.2?TextAlignment.Right:TextAlignment.Center);
            }
            angle+=sweep; _renderedPoints++;
        }
        if(inner>0)
        {
            c.DrawText(total.ToString("G6",System.Globalization.CultureInfo.InvariantCulture),new(plot.Center.X,plot.Center.Y+2),new(model.Theme.Foreground,24,"Arial",true),TextAlignment.Center);
            c.DrawText(s.SeriesName,new(plot.Center.X,plot.Center.Y+23),new(model.Theme.Muted,11),TextAlignment.Center);
        }
    }
    private static void DrawDataLabels(IChartCanvas c,ChartModel model,RenderableSeries s,SeriesSnapshot d,int[] indices,SeriesProjection p,StackedValues? stack)
    {
        var occupied=new List<RectD>(); var provider=s.DataLabelProvider??new NumericDataLabelProvider();
        foreach(int i in indices)
        {
            string? label=provider.GetLabel(s,d,i); if(string.IsNullOrEmpty(label)) continue;
            var point=p.ToScreen(d[0,i],stack is null?d[s.YChannel,i]:stack.Upper[i],d.ChannelCount>2?d[2,i]:0); if(!point.IsFinite||!p.Plot.Contains(point)) continue;
            var style=new TextStyle(model.Theme.Foreground,10); var metrics=c.MeasureText(label,style);
            var rect=new RectD(point.X-metrics.Width/2,point.Y-metrics.Height-7,metrics.Width,metrics.Height);
            if(occupied.Any(r=>r.Intersects(rect.Inflate(2)))) continue;
            occupied.Add(rect); c.DrawText(label,new(point.X,point.Y-7),style,TextAlignment.Center);
        }
    }
    internal static void DrawClippedPolyline(IChartCanvas c,IReadOnlyList<PointD> points,StrokeStyle stroke,RectD clip,bool closed=false)
    {
        if(points.Count<2||stroke.Width<=0||stroke.Color.A==0) return;
        var run=new List<PointD>();
        void Flush() { if(run.Count>=2) c.DrawPolyline(run.ToArray(),stroke); run.Clear(); }
        int count=closed?points.Count+1:points.Count;
        for(int i=1;i<count;i++)
        {
            PointD a=points[(i-1)%points.Count],b=points[i%points.Count];
            if(!Geometry.ClipLine(clip,ref a,ref b)) { Flush(); continue; }
            if(run.Count>0&&(run[^1]-a).LengthSquared>1e-12) Flush();
            if(run.Count==0) run.Add(a); run.Add(b);
        }
        Flush();
    }
    internal static void DrawPolygon(IChartCanvas c,IEnumerable<PointD> points,ColorRgba? fill,StrokeStyle? stroke,RectD clip)
    {
        PointD[] polygon=PolygonClipper.Clip(points,clip); if(polygon.Length<3) return;
        var path=new VectorPath().MoveTo(polygon[0]); for(int i=1;i<polygon.Length;i++) path.LineTo(polygon[i]); path.Close(); c.DrawPath(path,fill,stroke);
    }
}

public static class PolygonClipper
{
    public static PointD[] Clip(IEnumerable<PointD> points,RectD rectangle)
    {
        List<PointD> polygon=points.ToList(); if(polygon.Any(p=>!p.IsFinite)) return [];
        for(int edge=0;edge<4;edge++)
        {
            if(polygon.Count==0) break;
            var output=new List<PointD>();
            bool Inside(PointD p)=>edge switch {0=>p.X>=rectangle.X,1=>p.X<=rectangle.Right,2=>p.Y>=rectangle.Y,_=>p.Y<=rectangle.Bottom};
            PointD Intersection(PointD a,PointD b)
            {
                if(edge<2) { double x=edge==0?rectangle.X:rectangle.Right,t=(x-a.X)/(b.X-a.X); return new(x,a.Y+(b.Y-a.Y)*t); }
                double y=edge==2?rectangle.Y:rectangle.Bottom,u=(y-a.Y)/(b.Y-a.Y); return new(a.X+(b.X-a.X)*u,y);
            }
            PointD previous=polygon[^1]; bool previousInside=Inside(previous);
            foreach(var current in polygon)
            {
                bool currentInside=Inside(current);
                if(currentInside!=previousInside) output.Add(Intersection(previous,current));
                if(currentInside) output.Add(current);
                previous=current; previousInside=currentInside;
            }
            polygon=output;
        }
        return polygon.Where(p=>p.IsFinite).ToArray();
    }
}
