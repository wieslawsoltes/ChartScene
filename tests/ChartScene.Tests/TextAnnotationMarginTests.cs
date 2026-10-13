using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Interaction;
using ChartScene.Rendering;

public static class TextAnnotationMarginTests
{
    public static void Register(TestSuite suite)
    {
        suite.Run("unclipped text outside plot takes precedence over axis dragging",()=>
        {
            using var m=new ChartModel{ShowLegend=false};m.XAxis.AutoRange=m.YAxis.AutoRange=AutoRange.Never;m.XAxis.VisibleRange=m.YAxis.VisibleRange=new(0,10);var a=new TextAnnotation{Text="outside",X1=0,Y1=.5,XCoordinateMode=CoordinateMode.Relative,YCoordinateMode=CoordinateMode.Relative,
                HorizontalAnchorPoint=HorizontalAnchorPoint.Right,ClipToPlot=false,TextPadding=2};m.Annotations.Add(a);
            using var c=new ChartController(m);c.SetFrame(new ChartRenderer().Render(new RecordingCanvas(),m,900,600));
            var layout=c.Frame!.TextAnnotations[0].Layout;var point=layout.ToScreen(layout.LocalBounds.Center);
            TestSuite.True(c.Frame.Layout.Bounds.Contains(point)&&!c.Frame.Layout.PlotArea.Contains(point));
            var x=m.XAxis.VisibleRange;var y=m.YAxis.VisibleRange;
            c.Process(new(ChartInputKind.Down,point));TestSuite.True(a.IsSelected&&c.HasCapture);
            c.Process(new(ChartInputKind.Move,point+new PointD(14,10)));c.Process(new(ChartInputKind.Up,point+new PointD(14,10)));
            TestSuite.Equal(m.XAxis.VisibleRange,x);TestSuite.Equal(m.YAxis.VisibleRange,y);TestSuite.True(a.X1>0&&a.Y1>.5);
            c.History.Undo();TestSuite.Near(a.X1,0);TestSuite.Near(a.Y1,.5);
        });
    }
}
