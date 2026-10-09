using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;
using ChartScene.Rendering;

public static partial class AdditionalTests
{
    static partial void RegisterInteraction(TestSuite suite)
    {
        static ChartController Controller(ChartModel m)
        {
            var c=new ChartController(m); c.SetFrame(new ChartRenderer().Render(new RecordingCanvas(),m,900,600)); return c;
        }
        suite.Run("wheel zoom preserves anchor",()=>
        {
            using var m=TestModels.Create(SeriesKind.Line); using var c=Controller(m); var p=c.Frame!.Layout.PlotArea; var anchor=new PointD(p.X+p.Width*.25,p.Y+p.Height*.7); double before=m.XAxis.VisibleRange.At(.25),size=m.XAxis.VisibleRange.Size;
            c.Process(new(ChartInputKind.Wheel,anchor,WheelDelta:120)); TestSuite.True(m.XAxis.VisibleRange.Size<size); TestSuite.Near(m.XAxis.VisibleRange.At(.25),before); TestSuite.True(c.History.CanUndo); c.History.Undo(); TestSuite.Near(m.XAxis.VisibleRange.Size,size);
        });
        suite.Run("pan uses original viewport not accumulated drift",()=>
        {
            using var m=TestModels.Create(SeriesKind.Line); using var c=Controller(m); var p=c.Frame!.Layout.PlotArea; var before=m.XAxis.VisibleRange; var start=p.Center;
            c.Process(new(ChartInputKind.Down,start)); c.Process(new(ChartInputKind.Move,start+new PointD(p.Width*.1,0))); c.Process(new(ChartInputKind.Move,start+new PointD(p.Width*.2,0))); c.Process(new(ChartInputKind.Up,start+new PointD(p.Width*.2,0)));
            TestSuite.Near(m.XAxis.VisibleRange.Min,before.Min-before.Size*.2); TestSuite.True(!c.HasCapture); c.History.Undo(); TestSuite.Equal(m.XAxis.VisibleRange,before); c.History.Redo(); TestSuite.Near(m.XAxis.VisibleRange.Min,before.Min-before.Size*.2);
        });
        suite.Run("cancel restores captured viewport",()=>
        {
            using var m=TestModels.Create(SeriesKind.Line); using var c=Controller(m); var before=m.XAxis.VisibleRange; var start=c.Frame!.Layout.PlotArea.Center;
            c.Process(new(ChartInputKind.Down,start)); c.Process(new(ChartInputKind.Move,start+new PointD(50,30))); c.Cancel(); TestSuite.Equal(m.XAxis.VisibleRange,before); TestSuite.True(!c.HasCapture);
        });
        suite.Run("rectangle zoom crops both axes",()=>
        {
            using var m=TestModels.Create(SeriesKind.Line); using var c=Controller(m); c.Mode=ChartInteractionMode.ZoomRectangle; var p=c.Frame!.Layout.PlotArea; double width=m.XAxis.VisibleRange.Size,height=m.YAxis.VisibleRange.Size;
            var a=new PointD(p.X+p.Width*.2,p.Y+p.Height*.2); var b=new PointD(p.X+p.Width*.7,p.Y+p.Height*.7);
            c.Process(new(ChartInputKind.Down,a)); c.Process(new(ChartInputKind.Move,b)); TestSuite.True(c.View.RubberBand is not null); c.Process(new(ChartInputKind.Up,b)); TestSuite.Near(m.XAxis.VisibleRange.Size,width*.5); TestSuite.Near(m.YAxis.VisibleRange.Size,height*.5); TestSuite.True(c.View.RubberBand is null);
        });
        suite.Run("legend toggle is undoable",()=>
        {
            using var m=TestModels.Create(SeriesKind.Line); using var c=Controller(m); c.Process(new(ChartInputKind.Down,c.Frame!.LegendItems[0].Bounds.Center)); TestSuite.True(!m.RenderableSeries[0].IsVisible); c.History.Undo(); TestSuite.True(m.RenderableSeries[0].IsVisible);
        });
        suite.Run("log wheel uses log space",()=>
        {
            using var m=TestModels.Create(SeriesKind.Line); m.XAxis.Scale=AxisScale.Logarithmic; m.XAxis.VisibleRange=new(1,1000); m.XAxis.AutoRange=AutoRange.Never; using var c=Controller(m); var anchor=c.Frame!.Layout.PlotArea.Center;
            c.ZoomAt(anchor,.5); TestSuite.Near(m.XAxis.ScaleRange.Size,1.5); TestSuite.Near(m.XAxis.ScaleRange.Center,1.5);
        });
        suite.Run("touch pinch zoom",()=>
        {
            using var m=TestModels.Create(SeriesKind.Line); using var c=Controller(m); var center=c.Frame!.Layout.PlotArea.Center; double before=m.XAxis.VisibleRange.Size;
            c.Process(new(ChartInputKind.Down,center-new PointD(50,0),1,ChartPointerKind.Touch)); c.Process(new(ChartInputKind.Down,center+new PointD(50,0),2,ChartPointerKind.Touch)); c.Process(new(ChartInputKind.Move,center+new PointD(150,0),2,ChartPointerKind.Touch));
            TestSuite.Near(m.XAxis.VisibleRange.Size,before*.5); c.Process(new(ChartInputKind.Up,center+new PointD(150,0),2,ChartPointerKind.Touch)); TestSuite.True(c.History.CanUndo); c.History.Undo(); TestSuite.Near(m.XAxis.VisibleRange.Size,before);
        });
        suite.Run("line hit exact source point",()=>
        {
            using var m=TestModels.Create(SeriesKind.Line); using var c=Controller(m); var s=m.RenderableSeries[0]; var d=s.DataSeries!.Snapshot(); var point=c.Frame!.Layout.Projection(m,s).ToScreen(d[0,25],d[1,25]); var hit=c.HitTester.Query(m,point,3).First(); TestSuite.Equal(hit.DataIndex,25); TestSuite.Near(hit.Y,d[1,25]);
        });
        suite.Run("rollover interpolation",()=>
        {
            using var m=new ChartModel(); var d=new XyDataSeries(); d.AppendRange([0,10],[0,20]); var s=new FastLineRenderableSeries{DataSeries=d}; m.RenderableSeries.Add(s); using var c=Controller(m); var point=c.Frame!.Layout.Projection(m,s).ToScreen(4,8); var hit=c.HitTester.Query(m,point,10,true,true).Single(); TestSuite.Near(hit.X,4); TestSuite.Near(hit.Y,8);
        });
        suite.Run("heatmap hit identifies cell value",()=>
        {
            using var m=TestModels.Create(SeriesKind.Heatmap); using var c=Controller(m); var s=m.RenderableSeries[0]; var point=c.Frame!.Layout.Projection(m,s).ToScreen(2.5,3.5); var hit=c.HitTester.Query(m,point).Single(); TestSuite.Equal(hit.DataIndex,3*12+2); TestSuite.True(hit.Details!.StartsWith("Z="));
        });
        suite.Run("donut center is not hit",()=>
        {
            using var m=TestModels.Create(SeriesKind.Donut); using var c=Controller(m); TestSuite.Equal(c.HitTester.Query(m,c.Frame!.Layout.PlotArea.Center).Count,0);
        });
        suite.Run("annotation creation undo and redo",()=>
        {
            using var m=TestModels.Create(SeriesKind.Line); using var c=Controller(m); c.Mode=ChartInteractionMode.DrawAnnotation; var p=c.Frame!.Layout.PlotArea; var start=new PointD(p.X+50,p.Y+50); var end=start+new PointD(100,80);
            c.Process(new(ChartInputKind.Down,start)); c.Process(new(ChartInputKind.Move,end)); c.Process(new(ChartInputKind.Up,end)); TestSuite.Equal(m.Annotations.Count,1); TestSuite.True(m.Annotations[0].X2>m.Annotations[0].X1); c.History.Undo(); TestSuite.Equal(m.Annotations.Count,0); c.History.Redo(); TestSuite.Equal(m.Annotations.Count,1);
        });
        suite.Run("annotation dragging and cancel",()=>
        {
            using var m=TestModels.Create(SeriesKind.Line); var a=new BoxAnnotation{X1=.2,Y1=.2,X2=.4,Y2=.4,XCoordinateMode=CoordinateMode.Relative,YCoordinateMode=CoordinateMode.Relative}; m.Annotations.Add(a); using var c=Controller(m); var p=c.Frame!.Layout.PlotArea; var start=AnnotationGeometry.ToScreen(m,a,p,.3,.3);
            c.Process(new(ChartInputKind.Down,start)); c.Process(new(ChartInputKind.Move,start+new PointD(p.Width*.1,0))); TestSuite.Near(a.X1,.3); c.Cancel(); TestSuite.Near(a.X1,.2);
        });
        suite.Run("range synchronization units and unsubscribe",()=>
        {
            var meters=new NumericAxis{VisibleRange=new(0,10)}; var centimeters=new NumericAxis{VisibleRange=new(0,1)}; using var group=new RangeSyncGroup("distance"); using var one=group.Add(meters); var two=group.Add(centimeters,x=>x/100,x=>x*100); TestSuite.Equal(centimeters.VisibleRange,new DoubleRange(0,1000)); meters.VisibleRange=new(2,8); TestSuite.Equal(centimeters.VisibleRange,new DoubleRange(200,800)); centimeters.VisibleRange=new(300,700); TestSuite.Equal(meters.VisibleRange,new DoubleRange(3,7)); two.Dispose(); meters.VisibleRange=new(0,1); TestSuite.Equal(centimeters.VisibleRange,new DoubleRange(300,700));
        });
        suite.Run("deferred range synchronization",()=>
        {
            var a=new NumericAxis(); var b=new NumericAxis(); using var group=new RangeSyncGroup("group"); using var one=group.Add(a); using var two=group.Add(b); using(group.SuspendUpdates()) { a.VisibleRange=new(10,20); a.VisibleRange=new(20,30); TestSuite.Equal(b.VisibleRange,DoubleRange.Unit); } TestSuite.Equal(b.VisibleRange,new DoubleRange(20,30));
        });
        suite.Run("viewport animation deterministic clock",()=>
        {
            using var m=new ChartModel(); var animator=new ViewportAnimator(); animator.Begin(m,new Dictionary<AxisModel,DoubleRange>{{m.XAxis,new(10,20)}},100,200); TestSuite.Equal(m.XAxis.VisibleRange,DoubleRange.Unit); animator.Tick(300); TestSuite.Equal(m.XAxis.VisibleRange,new DoubleRange(10,20)); TestSuite.True(!animator.IsRunning);
        });
        suite.Run("bounded undo history",()=>
        {
            int value=0; var history=new UndoManager{Capacity=2}; for(int i=0;i<3;i++) history.Execute(new DelegateCommand("increment",()=>value++,()=>value--)); history.Undo(); history.Undo(); TestSuite.True(!history.Undo()); TestSuite.Equal(value,1);
        });
    }
}
