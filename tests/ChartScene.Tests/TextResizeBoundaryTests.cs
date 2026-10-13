using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;
using ChartScene.Rendering;

public static class TextResizeBoundaryTests
{
    private sealed class Fixture:IDisposable
    {
        public ChartModel Model { get; }=new(){ShowLegend=false};
        public TextAnnotation Label { get; }=new(){Text="Resize this selected annotation",TextWrapping=AnnotationTextWrapping.Wrap,
            TextWrapWidth=160,FontSize=20,IsSelected=true,X1=.5,Y1=.5,XCoordinateMode=CoordinateMode.Relative,YCoordinateMode=CoordinateMode.Relative};
        public ChartController Controller { get; }
        public TextAnnotationLayout Layout=>Controller.Frame!.FindTextAnnotation(Label)!;
        public PointD Grip=>Layout.WidthGrip!.Value.Position;
        public Fixture()
        {
            Model.XAxis.AutoRange=Model.YAxis.AutoRange=AutoRange.Never;
            Model.XAxis.VisibleRange=Model.YAxis.VisibleRange=new(0,10);Model.Annotations.Add(Label);
            Controller=new(Model);Render();
        }
        public void Render()=>Controller.SetFrame(new ChartRenderer().Render(new RecordingCanvas(),Model,1000,700));
        public void Dispose(){Controller.Dispose();Model.Dispose();}
    }
    public static void Register(TestSuite suite)
    {
        foreach(bool stale in new[]{false,true})
            suite.Run("width grip double click is isolated from body editing and navigation stale="+stale,()=>
            {
                using var f=new Fixture();int edits=0;f.Controller.AnnotationEditRequested+=(_,_)=>edits++;
                var point=f.Grip;var range=f.Model.XAxis.VisibleRange;
                if(stale)f.Model.Invalidate();
                TestSuite.True(f.Controller.Process(new(ChartInputKind.DoubleClick,point)));
                TestSuite.Equal(edits,0);TestSuite.Equal(f.Label.TextWrapWidth,160d);TestSuite.Equal(f.Model.XAxis.VisibleRange,range);
                TestSuite.True(!f.Controller.History.CanUndo&&!f.Controller.HasCapture);
            });
        suite.Run("wrapped text body still opens its editor away from the width grip",()=>
        {
            using var f=new Fixture();int edits=0;f.Controller.AnnotationEditRequested+=(_,_)=>edits++;
            f.Controller.Process(new(ChartInputKind.DoubleClick,f.Layout.ToScreen(f.Layout.LocalBounds.Center)));
            TestSuite.Equal(edits,1);TestSuite.True(!f.Controller.History.CanUndo);
        });
        suite.Run("width grip double click cannot target removed or read-only annotations",()=>
        {
            using var f=new Fixture();var modifier=f.Controller.Modifiers.OfType<TextAnnotationResizeModifier>().Single();var point=f.Grip;
            f.Label.IsEditable=false;
            TestSuite.Equal(modifier.Handle(f.Controller,new(ChartInputKind.DoubleClick,point)),ModifierResult.Unhandled);
            f.Label.IsEditable=true;f.Model.Annotations.Remove(f.Label);
            TestSuite.Equal(modifier.Handle(f.Controller,new(ChartInputKind.DoubleClick,point)),ModifierResult.Unhandled);
        });
        suite.Run("second touch cancels width preview before normal pinch handoff",()=>
        {
            using var f=new Fixture();var p=f.Grip;var range=f.Model.XAxis.VisibleRange;
            f.Controller.Process(new(ChartInputKind.Down,p,1,ChartPointerKind.Touch));
            f.Controller.Process(new(ChartInputKind.Move,p+new PointD(20,0),1,ChartPointerKind.Touch));
            TestSuite.Near(f.Label.TextWrapWidth,180);
            f.Controller.Process(new(ChartInputKind.Down,p+new PointD(-100,0),2,ChartPointerKind.Touch));
            TestSuite.Equal(f.Label.TextWrapWidth,160d);TestSuite.True(f.Controller.HasCapture);
            TestSuite.True(!f.Controller.Modifiers.OfType<TextAnnotationResizeModifier>().Single().IsResizing);
            f.Controller.Process(new(ChartInputKind.Move,p+new PointD(-150,0),2,ChartPointerKind.Touch));
            TestSuite.True(f.Model.XAxis.VisibleRange.Size<range.Size);
            f.Controller.Process(new(ChartInputKind.Up,p+new PointD(-150,0),2,ChartPointerKind.Touch));
            f.Controller.History.Undo();TestSuite.Equal(f.Model.XAxis.VisibleRange,range);
            TestSuite.True(!f.Controller.History.CanUndo);TestSuite.Equal(f.Label.TextWrapWidth,160d);
        });
        suite.Run("foreign mouse cannot toggle legend or steal a captured pan",()=>
        {
            using var f=new Fixture();var data=new XyDataSeries();data.Append(0,0);data.Append(10,10);
            var series=new FastLineRenderableSeries{DataSeries=data,SeriesName="Do not toggle"};
            f.Model.ShowLegend=true;f.Model.RenderableSeries.Add(series);f.Render();
            var plot=f.Controller.Frame!.Layout.PlotArea;var start=new PointD(plot.X+20,plot.Y+20);
            var legend=f.Controller.Frame.LegendItems[0].Bounds.Center;var range=f.Model.XAxis.VisibleRange;
            f.Controller.Process(new(ChartInputKind.Down,start,1));TestSuite.True(f.Controller.HasCapture);
            f.Controller.Process(new(ChartInputKind.Down,legend,2));f.Controller.Process(new(ChartInputKind.Move,legend,2));
            f.Controller.Process(new(ChartInputKind.Up,legend,2));TestSuite.True(series.IsVisible&&f.Controller.HasCapture);
            TestSuite.Equal(f.Model.XAxis.VisibleRange,range);TestSuite.True(!f.Controller.History.CanUndo);
            f.Controller.Process(new(ChartInputKind.Move,start+new PointD(30,0),1));
            TestSuite.True(f.Model.XAxis.VisibleRange!=range);f.Controller.Cancel();
            TestSuite.Equal(f.Model.XAxis.VisibleRange,range);TestSuite.True(!f.Controller.History.CanUndo);
        });
        foreach(var anchor in Enum.GetValues<HorizontalAnchorPoint>())
            suite.Run("overflow text width grip follows requested width not overflowing word "+anchor,()=>
            {
                using var f=new Fixture();f.Label.Text="Supercalifragilisticexpialidocious";
                f.Label.TextWrapping=AnnotationTextWrapping.WrapWithOverflow;f.Label.TextWrapWidth=60;
                f.Label.HorizontalAnchorPoint=anchor;f.Render();
                double factor=anchor==HorizontalAnchorPoint.Center?2:anchor==HorizontalAnchorPoint.Right?-1:1;
                TestSuite.True(f.Layout.LocalBounds.Width>60);TestSuite.Near(f.Layout.ToLocal(f.Grip).X,60/factor);
                var p=f.Grip;f.Controller.Process(new(ChartInputKind.Down,p,1));
                f.Controller.Process(new(ChartInputKind.Up,p+new PointD(15,0),1));
                TestSuite.Near(f.Label.TextWrapWidth,60+15*factor);TestSuite.Equal(f.Label.X1,.5);TestSuite.Equal(f.Label.Y1,.5);
            });
    }
}
