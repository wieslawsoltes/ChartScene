using System.Diagnostics;
using ChartScene.Core;
using ChartScene.Charting;
using ChartScene.Data;

namespace ChartScene.Rendering;

/// <summary>One renderer per view. Geometry is computed in double precision; no UI framework is required.</summary>
public sealed partial class ChartRenderer
{
    private long _renderedPoints;
    private readonly List<HitRegion> _hits=[];
    private readonly List<LegendItem> _legend=[];
    private readonly Dictionary<(HeatmapGrid Grid,ColorMap Map,DoubleRange Range,HeatmapColorScaleMode Mode,double Opacity),RasterImage> _rasterCache=[];
    public ChartFrame Render(IChartCanvas target,ChartModel model,double width,double height,ChartViewState? view=null)
    {
        BeginResamplingFrame();
        var timer=Stopwatch.StartNew(); var canvas=new CountingCanvas(target); _renderedPoints=0; _hits.Clear(); _legend.Clear();
        canvas.Clear(model.Theme.Background);
        var errors=model.Validate();
        if(errors.Count>0)
        {
            canvas.DrawText("Chart configuration error",new(16,28),new(model.Theme.Foreground,16,"Arial",true));
            for(int i=0;i<errors.Count;i++) canvas.DrawText(errors[i],new(16,52+i*20),new(model.Theme.Muted,12));
            return new(new(new(0,0,width,height),new(0,0,0,0),[]),new(0,0,canvas.Commands,timer.Elapsed.TotalMilliseconds,model.Revision),[],[],errors);
        }
        model.AutoRangeAxes();
        var layout=ChartLayout.Measure(canvas,model,width,height);
        if(layout.PlotArea.IsEmpty) return new(layout,new(0,0,canvas.Commands,timer.Elapsed.TotalMilliseconds,model.Revision),[],[],[]);
        model.ApplyAspectRatio(layout.PlotArea.Width,layout.PlotArea.Height);
        layout=ChartLayout.Measure(canvas,model,width,height);
        DrawBackground(canvas,model,layout);
        DrawAnnotations(canvas,model,layout,AnnotationLayer.BelowSeries);
        var stacks=StackCalculator.Calculate(model);
        canvas.Save(); canvas.Clip(layout.PlotArea);
        try
        {
            foreach(var series in model.RenderableSeries.Where(s=>s.IsVisible))
                DrawSeries(canvas,model,series,layout,stacks.GetValueOrDefault(series));
        }
        finally { canvas.Restore(); }
        DrawAxes(canvas,model,layout);
        DrawAnnotations(canvas,model,layout,AnnotationLayer.AboveSeries);
        if(model.ShowLegend) DrawLegend(canvas,model,layout);
        if(view is not null) DrawInteraction(canvas,model,layout,view);
        long source=model.RenderableSeries.Where(s=>s.IsVisible).Sum(s=>(long)(s.DataSeries?.Count??(s.HeatmapGrid is { } g?g.Width*g.Height:0)));
        return new(layout,new(source,_renderedPoints,canvas.Commands,timer.Elapsed.TotalMilliseconds,model.Revision),_hits.ToArray(),_legend.ToArray(),[]);
    }
    private static void DrawBackground(IChartCanvas c,ChartModel m,ChartLayout l)
    {
        var p=l.PlotArea; var theme=m.Theme;
        c.DrawRectangle(p,theme.PlotBackground);
        if(!string.IsNullOrEmpty(m.Title)) c.DrawText(m.Title,new(16,24),new(theme.Foreground,16,"Arial",true));
        if(!string.IsNullOrEmpty(m.Subtitle)) c.DrawText(m.Subtitle,new(16,44),new(theme.Muted,11));
        if(m.RenderableSeries.Any(s=>s.IsVisible&&s.IsCircular)) return;
        if(m.CoordinateSystem==CoordinateSystem.Polar) { DrawPolarGrid(c,m,p); return; }
        if(m.CoordinateSystem==CoordinateSystem.Ternary) { DrawTernaryGrid(c,m,p); return; }
        c.Save(); c.Clip(p);
        foreach(var a in l.Axes)
        {
            foreach(var tick in a.Ticks)
            {
                if(tick.IsMajor?!a.Axis.DrawMajorGridLines:!a.Axis.DrawMinorGridLines) continue;
                double v=a.Calculator.GetCoordinate(tick.Value); if(!double.IsFinite(v)) continue;
                var stroke=new StrokeStyle(tick.IsMajor?theme.GridMajor:theme.GridMinor,tick.IsMajor?1:.7);
                if(a.Axis.IsVertical) c.DrawLine(new(p.X,v),new(p.Right,v),stroke); else c.DrawLine(new(v,p.Y),new(v,p.Bottom),stroke);
            }
        }
        c.Restore();
    }
    private static void DrawAxes(IChartCanvas c,ChartModel m,ChartLayout l)
    {
        if(m.CoordinateSystem!=CoordinateSystem.Cartesian||m.RenderableSeries.Any(s=>s.IsVisible&&s.IsCircular)) return;
        var p=l.PlotArea;
        foreach(var a in l.Axes)
        {
            var axis=a.Axis; var color=axis.Color??m.Theme.Muted; var style=new TextStyle(color,11); var line=new StrokeStyle(axis.Color??m.Theme.Border,1);
            bool vertical=axis.IsVertical,left=axis.AxisAlignment==AxisAlignment.Left,top=axis.AxisAlignment==AxisAlignment.Top;
            double edge=vertical?(left?a.Bounds.Right:a.Bounds.X):(top?a.Bounds.Bottom:a.Bounds.Y);
            if(vertical) c.DrawLine(new(edge,p.Y),new(edge,p.Bottom),line); else c.DrawLine(new(p.X,edge),new(p.Right,edge),line);
            double lastEnd=double.NegativeInfinity;
            foreach(var tick in a.Ticks.OrderBy(t=>a.Calculator.GetCoordinate(t.Value)))
            {
                if(!tick.IsMajor) continue;
                double v=a.Calculator.GetCoordinate(tick.Value); if(!double.IsFinite(v)) continue;
                double direction=(left||top)?-1:1;
                if(vertical) c.DrawLine(new(edge,v),new(edge+direction*4,v),line); else c.DrawLine(new(v,edge),new(v,edge+direction*4),line);
                var metrics=c.MeasureText(tick.Label,style);
                double start=vertical?v-metrics.Height/2:v-metrics.Width/2,end=vertical?v+metrics.Height/2:v+metrics.Width/2;
                if(start<lastEnd+4) continue; lastEnd=end;
                if(vertical) c.DrawText(tick.Label,new(edge+direction*8,v+(metrics.Ascent-metrics.Descent)/2),style,left?TextAlignment.Right:TextAlignment.Left);
                else c.DrawText(tick.Label,new(v,top?edge-9:edge+metrics.Ascent+7),style,TextAlignment.Center);
            }
            if(!string.IsNullOrEmpty(axis.AxisTitle))
            {
                if(vertical) c.DrawText(axis.AxisTitle,new(left?a.Bounds.X+12:a.Bounds.Right-5,p.Center.Y),new(color,12),TextAlignment.Center,-90);
                else c.DrawText(axis.AxisTitle,new(p.Center.X,top?a.Bounds.Y+12:a.Bounds.Bottom-2),new(color,12),TextAlignment.Center);
            }
        }
    }
    private static void DrawPolarGrid(IChartCanvas c,ChartModel m,RectD p)
    {
        double radius=Math.Min(p.Width,p.Height)/2; var center=p.Center;
        for(int i=1;i<=5;i++)
        {
            double r=radius*i/5;
            c.DrawEllipse(new(center.X-r,center.Y-r,2*r,2*r),null,new(m.Theme.GridMajor));
            c.DrawText(m.YAxis.Format(m.YAxis.FromScale(m.YAxis.ScaleRange.At(i/5.0))),new(center.X+r+3,center.Y-3),new(m.Theme.Muted,10));
        }
        for(int i=0;i<12;i++)
        {
            double angle=i*Math.PI/6; var end=new PointD(center.X+Math.Cos(angle)*radius,center.Y-Math.Sin(angle)*radius);
            c.DrawLine(center,end,new(m.Theme.GridMajor));
            c.DrawText($"{i*30}°",new(center.X+Math.Cos(angle)*(radius+17),center.Y-Math.Sin(angle)*(radius+17)+4),new(m.Theme.Muted,10),TextAlignment.Center);
        }
    }
    private static void DrawTernaryGrid(IChartCanvas c,ChartModel m,RectD p)
    {
        var r=SeriesProjection.TernaryBounds(p); var a=new PointD(r.X,r.Bottom); var b=new PointD(r.Right,r.Bottom); var d=new PointD(r.Center.X,r.Y);
        c.DrawPolyline([a,b,d],new(m.Theme.Border),true);
        for(int i=1;i<10;i++)
        {
            double t=i/10.0;
            c.DrawLine(a+(d-a)*t,b+(d-b)*t,new(m.Theme.GridMajor));
            c.DrawLine(a+(b-a)*t,d+(b-d)*t,new(m.Theme.GridMajor));
            c.DrawLine(b+(a-b)*t,d+(a-d)*t,new(m.Theme.GridMajor));
            c.DrawText((i*10).ToString(),new(a.X+r.Width*t,a.Y+16),new(m.Theme.Muted,10),TextAlignment.Center);
        }
        c.DrawText("A",new(a.X-15,a.Y+5),new(m.Theme.Foreground,13));
        c.DrawText("B",new(b.X+8,b.Y+5),new(m.Theme.Foreground,13));
        c.DrawText("C",new(d.X,d.Y-10),new(m.Theme.Foreground,13),TextAlignment.Center);
    }
    private void DrawLegend(IChartCanvas c,ChartModel m,ChartLayout l)
    {
        var all=m.RenderableSeries.ToArray(); if(all.Length==0) return;
        double rowHeight=20,width=Math.Max(120,all.Select(s=>c.MeasureText(s.SeriesName,new(m.Theme.Foreground,11)).Width+60).Max());
        int rows=Math.Max(1,Math.Min(all.Length,(int)(l.PlotArea.Height/2/rowHeight)));
        int columns=(int)Math.Ceiling((double)all.Length/rows);
        double x=Math.Max(l.PlotArea.X+8,l.PlotArea.Right-columns*width-8),y=l.PlotArea.Y+8;
        c.DrawRectangle(new(x-5,y-4,columns*width+6,rows*rowHeight+8),m.Theme.Background.WithAlpha(220),new(m.Theme.Border),3);
        for(int i=0;i<all.Length;i++)
        {
            var s=all[i]; int col=i/rows,row=i%rows; var r=new RectD(x+col*width,y+row*rowHeight,width,rowHeight); _legend.Add(new(s,r));
            c.DrawRectangle(new(r.X+2,r.Y+4,10,10),s.IsVisible?s.Stroke:null,new(s.Stroke));
            if(s.IsVisible) c.DrawPolyline([new(r.X+4,r.Y+9),new(r.X+6,r.Y+11),new(r.X+10,r.Y+6)],new(m.Theme.Background,1.2));
            c.DrawLine(new(r.X+18,r.Y+9),new(r.X+34,r.Y+9),new(s.Stroke,s.IsSelected?3:1.5));
            c.DrawText(s.SeriesName,new(r.X+40,r.Y+13),new(s.IsVisible?m.Theme.Foreground:m.Theme.Muted,11));
        }
    }
    private static void DrawInteraction(IChartCanvas c,ChartModel m,ChartLayout l,ChartViewState state)
    {
        var p=l.PlotArea;
        if(state.RubberBand is { } selection) c.DrawRectangle(selection,m.Theme.Accent.WithAlpha(35),new(m.Theme.Accent,1,[4,3]));
        if(state.Cursor is not { } point||!p.Contains(point)) return;
        c.Save(); c.Clip(p);
        if(state.ShowCursor)
        {
            c.DrawLine(new(point.X,p.Y),new(point.X,p.Bottom),new(m.Theme.Muted,1,[3,3]));
            c.DrawLine(new(p.X,point.Y),new(p.Right,point.Y),new(m.Theme.Muted,1,[3,3]));
        }
        foreach(var hit in state.Hits) c.DrawEllipse(new(hit.Position.X-4,hit.Position.Y-4,8,8),hit.Series.Stroke,new(m.Theme.Foreground,1));
        c.Restore();
        if(!state.ShowRollover||state.Hits.Count==0) return;
        var lines=state.Hits.Select(h=>$"{h.Series.SeriesName}   {m.FindXAxis(h.Series.XAxisId).Format(h.X,true)}   {m.FindYAxis(h.Series.YAxisId).Format(h.Y,true)}{(h.Details is null?"":"  "+h.Details)}").ToArray();
        double w=Math.Min(p.Width-12,lines.Select(s=>c.MeasureText(s,new(m.Theme.Foreground,11)).Width).DefaultIfEmpty(80).Max()+20),h=lines.Length*21+12;
        double x=Math.Clamp(point.X+16,p.X+4,Math.Max(p.X+4,p.Right-w-4)),y=Math.Clamp(point.Y+15,p.Y+4,Math.Max(p.Y+4,p.Bottom-h-4));
        c.DrawRectangle(new(x,y,w,h),m.Theme.Background.WithAlpha(245),new(m.Theme.Border),4);
        for(int i=0;i<lines.Length;i++) c.DrawText(lines[i],new(x+10,y+20+i*21),new(state.Hits[i].Series.Stroke,11));
    }
    private static ColorRgba Alpha(ColorRgba c,double opacity)=>c.WithAlpha((byte)Math.Clamp(Math.Round(c.A*opacity),0,255));
}
