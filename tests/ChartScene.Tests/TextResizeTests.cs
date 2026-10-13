using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Interaction;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using SkiaSharp;

public static class TextResizeTests
{
    private sealed class Fixture:IDisposable
    {
        public ChartModel Model { get; }=new(){ShowLegend=false};
        public TextAnnotation Label { get; }=new(){Text="Measured annotation with several words",FontSize=20,TextWrapWidth=160,
            TextWrapping=AnnotationTextWrapping.Wrap,TextPadding=8,IsSelected=true,X1=.5,Y1=.5,
            XCoordinateMode=CoordinateMode.Relative,YCoordinateMode=CoordinateMode.Relative};
        public ChartController Controller { get; }
        public TextAnnotationResizeModifier Resize { get; }
        public TextAnnotationLayout Layout=>Controller.Frame!.FindTextAnnotation(Label)!;
        public Fixture()
        {
            Model.XAxis.AutoRange=Model.YAxis.AutoRange=AutoRange.Never;Model.XAxis.VisibleRange=Model.YAxis.VisibleRange=new(0,10);
            Model.Annotations.Add(Label);Controller=new(Model);Resize=Controller.Modifiers.OfType<TextAnnotationResizeModifier>().Single();Render();
        }
        public void Render(double width=1000)=>Controller.SetFrame(new ChartRenderer().Render(new RecordingCanvas(),Model,width,700));
        public PointD Grip=>Layout.WidthGrip!.Value.Position;
        public PointD Offset(double localX,double localY=0)=>Layout.ToScreen(Layout.ToLocal(Grip)+new PointD(localX,localY));
        public void Send(ChartInputKind kind,PointD p,int id=1)=>Controller.Process(new(kind,p,id));
        public void Dispose(){Controller.Dispose();Model.Dispose();}
    }
    private sealed class ObservedData:DataSeries,IDataSeries
    {
        public long Reads;
        SeriesSnapshot IDataSeries.Snapshot(){Reads++;return Snapshot();}
    }
    [MethodImpl(MethodImplOptions.NoInlining|MethodImplOptions.NoOptimization)]
    private static long HitWindow(TextAnnotationLayout layout,PointD grip)
    {
        long start=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)if(!layout.HitWidthGrip(grip))throw new Exception("Handle missed");
        return GC.GetAllocatedBytesForCurrentThread()-start;
    }
    public static void Register(TestSuite suite)
    {
        foreach(var anchor in Enum.GetValues<HorizontalAnchorPoint>())foreach(double angle in new[]{-90d,0,37,180})
            suite.Run($"width grip preserves anchor and exact history {anchor} {angle}",()=>
            {
                using var f=new Fixture();f.Label.HorizontalAnchorPoint=anchor;f.Label.RotationAngle=angle;f.Render();
                var oldAnchor=f.Layout.Anchor;var start=f.Grip;var end=f.Offset(20,15);
                double expected=160+20*(anchor==HorizontalAnchorPoint.Center?2:anchor==HorizontalAnchorPoint.Right?-1:1);
                f.Send(ChartInputKind.Down,start);TestSuite.True(f.Resize.IsResizing&&f.Controller.HasCapture);
                f.Send(ChartInputKind.Move,end);f.Render();f.Send(ChartInputKind.Up,end);
                TestSuite.Near(f.Label.TextWrapWidth,expected);TestSuite.Equal(f.Layout.Anchor,oldAnchor);
                TestSuite.Equal(f.Label.X1,.5);TestSuite.Equal(f.Label.Y1,.5);TestSuite.Equal(f.Label.FontSize,20d);
                TestSuite.True(!f.Resize.IsResizing&&f.Controller.History.CanUndo);
                f.Controller.History.Undo();TestSuite.Equal(f.Label.TextWrapWidth,160d);TestSuite.True(!f.Controller.History.CanUndo);
                f.Controller.History.Redo();TestSuite.Near(f.Label.TextWrapWidth,expected);
            });
        foreach(var mode in Enum.GetValues<CoordinateMode>())
            suite.Run("width grip changes pixels not data coordinates "+mode,()=>
            {
                using var f=new Fixture();f.Label.XCoordinateMode=f.Label.YCoordinateMode=mode;
                f.Model.XAxis.AxisAlignment=AxisAlignment.Left;f.Model.YAxis.AxisAlignment=AxisAlignment.Bottom;
                f.Model.XAxis.FlippedCoordinates=true;f.Model.YAxis.Scale=AxisScale.Logarithmic;f.Model.YAxis.VisibleRange=new(1,100);
                double value=mode==CoordinateMode.Relative?.5:mode==CoordinateMode.Pixel?180:5;
                f.Label.X1=f.Label.Y1=value;f.Render();var end=f.Offset(35);f.Send(ChartInputKind.Down,f.Grip);f.Send(ChartInputKind.Up,end);
                TestSuite.Near(f.Label.TextWrapWidth,195);TestSuite.Equal(f.Label.X1,value);TestSuite.Equal(f.Label.Y1,value);
            });
        suite.Run("stationary and return-to-start width gestures preserve redo and exact doubles",()=>
        {
            using var f=new Fixture();double original=160.123456789;f.Label.TextWrapWidth=original;f.Render();var p=f.Grip;
            int dummy=0;f.Controller.History.Execute(new DelegateCommand("unrelated",()=>dummy=1,()=>dummy=0));f.Controller.History.Undo();
            f.Send(ChartInputKind.Down,p);f.Send(ChartInputKind.Move,f.Offset(10));f.Send(ChartInputKind.Move,p);f.Send(ChartInputKind.Up,p);
            TestSuite.Equal(f.Label.TextWrapWidth,original);TestSuite.True(!f.Controller.History.CanUndo&&f.Controller.History.CanRedo);
            f.Controller.History.Redo();TestSuite.Equal(dummy,1);
        });
        suite.Run("width gesture clamps moving requests but leaves stationary custom widths alone",()=>
        {
            using var f=new Fixture();f.Resize.MaximumWidth=220;f.Resize.MinimumWidth=30;var p=f.Grip;
            f.Send(ChartInputKind.Down,p);f.Send(ChartInputKind.Move,f.Offset(500));TestSuite.Equal(f.Label.TextWrapWidth,220d);
            f.Send(ChartInputKind.Move,f.Offset(-500));TestSuite.Equal(f.Label.TextWrapWidth,30d);f.Controller.Cancel();TestSuite.Equal(f.Label.TextWrapWidth,160d);
            f.Label.TextWrapWidth=10;f.Render();p=f.Grip;f.Send(ChartInputKind.Down,p);f.Send(ChartInputKind.Up,p);TestSuite.Equal(f.Label.TextWrapWidth,10d);
        });
        suite.Run("width resize options reject invalid values atomically",()=>
        {
            var r=new TextAnnotationResizeModifier();
            foreach(double bad in new[]{0d,-1,double.NaN,double.PositiveInfinity})
            {
                TestSuite.Throws<ArgumentOutOfRangeException>(()=>r.HitRadius=bad);
                TestSuite.Throws<ArgumentOutOfRangeException>(()=>r.MinimumWidth=bad);
                TestSuite.Throws<ArgumentOutOfRangeException>(()=>r.MaximumWidth=bad);
            }
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>r.MinimumWidth=5000);
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>r.MaximumWidth=1);
            TestSuite.Equal(r.MinimumWidth,16d);TestSuite.Equal(r.MaximumWidth,4096d);TestSuite.Equal(r.HitRadius,8d);
        });
        suite.Run("width resize ignores invalid and foreign pointer updates",()=>
        {
            using var f=new Fixture();var p=f.Grip;var end=f.Offset(30);
            f.Send(ChartInputKind.Down,p);f.Send(ChartInputKind.Move,new(double.NaN,0));f.Send(ChartInputKind.Down,end,2);
            f.Send(ChartInputKind.Move,end,2);f.Send(ChartInputKind.Up,end,2);
            TestSuite.Equal(f.Label.TextWrapWidth,160d);TestSuite.True(f.Resize.IsResizing&&f.Controller.HasCapture);
            f.Send(ChartInputKind.Up,end);TestSuite.Near(f.Label.TextWrapWidth,190);TestSuite.True(!f.Controller.HasCapture);
        });
        suite.Run("width resize Escape cancels only its preview",()=>
        {
            using var f=new Fixture();var p=f.Grip;f.Send(ChartInputKind.Down,p);f.Send(ChartInputKind.Move,f.Offset(30));
            f.Controller.Process(new(ChartInputKind.Key,p,Key:"Escape"));TestSuite.Equal(f.Label.TextWrapWidth,160d);
            TestSuite.True(!f.Resize.IsResizing&&!f.Controller.HasCapture&&!f.Controller.History.CanUndo);
        });
        foreach(string change in new[]{"width","text","rotation","mode","hidden","readonly","selection","remove","axis","axes"})
            suite.Run("external change supersedes width preview "+change,()=>
            {
                using var f=new Fixture();var p=f.Grip;var end=f.Offset(30);f.Send(ChartInputKind.Down,p);f.Send(ChartInputKind.Move,end);
                switch(change)
                {
                    case "width":f.Label.TextWrapWidth=77;break;
                    case "text":f.Label.Text="external";break;
                    case "rotation":f.Label.RotationAngle=70;break;
                    case "mode":f.Label.TextWrapping=AnnotationTextWrapping.NoWrap;break;
                    case "hidden":f.Label.IsVisible=false;break;
                    case "readonly":f.Label.IsEditable=false;break;
                    case "selection":f.Label.IsSelected=false;break;
                    case "remove":f.Model.Annotations.Remove(f.Label);break;
                    case "axis":f.Model.XAxis.VisibleRange=new(2,8);break;
                    case "axes":f.Model.XAxes.Add(new NumericAxis{Id="other"});break;
                }
                TestSuite.True(!f.Resize.IsResizing);double preserved=f.Label.TextWrapWidth;
                f.Send(ChartInputKind.Move,end);f.Controller.Cancel();TestSuite.Equal(f.Label.TextWrapWidth,preserved);TestSuite.True(!f.Controller.History.CanUndo);
            });
        suite.Run("reentrant width observer cannot be overwritten by move cancel or undo",()=>
        {
            using var f=new Fixture();bool once=true;
            f.Model.Changed+=(_,_)=>{if(once&&f.Label.TextWrapWidth!=160){once=false;f.Label.TextWrapWidth=81;}};
            f.Render();var p=f.Grip;f.Send(ChartInputKind.Down,p);f.Send(ChartInputKind.Move,f.Offset(30));f.Send(ChartInputKind.Up,p);f.Controller.Cancel();
            TestSuite.Equal(f.Label.TextWrapWidth,81d);TestSuite.True(!f.Controller.History.CanUndo&&!f.Resize.IsResizing);
        });
        suite.Run("reentrant controller cancellation rolls back width and never revives capture",()=>
        {
            using var f=new Fixture();bool once=true;
            f.Model.Changed+=(_,_)=>{if(once&&f.Label.TextWrapWidth!=160){once=false;f.Controller.Cancel();}};
            f.Send(ChartInputKind.Down,f.Grip);f.Send(ChartInputKind.Move,f.Offset(30));
            TestSuite.Equal(f.Label.TextWrapWidth,160d);TestSuite.True(!f.Controller.HasCapture&&!f.Resize.IsResizing&&!f.Controller.History.CanUndo);
        });
        suite.Run("stale width history cannot overwrite external width or revive on redo",()=>
        {
            using var f=new Fixture();f.Send(ChartInputKind.Down,f.Grip);f.Send(ChartInputKind.Up,f.Offset(20));
            f.Label.TextWrapWidth=75;f.Controller.History.Undo();TestSuite.Equal(f.Label.TextWrapWidth,75d);
            f.Label.TextWrapWidth=160;f.Controller.History.Redo();TestSuite.Equal(f.Label.TextWrapWidth,160d);
        });
        suite.Run("multiple width gestures share ordered undo redo",()=>
        {
            using var f=new Fixture();foreach(int delta in new[]{10,20,30}){f.Render();f.Send(ChartInputKind.Down,f.Grip);f.Send(ChartInputKind.Up,f.Offset(delta));}
            TestSuite.Near(f.Label.TextWrapWidth,220);foreach(int width in new[]{190,170,160}){f.Controller.History.Undo();TestSuite.Near(f.Label.TextWrapWidth,width);}
            foreach(int width in new[]{170,190,220}){f.Controller.History.Redo();TestSuite.Near(f.Label.TextWrapWidth,width);}
        });
        suite.Run("width resize refuses stale and clipped frame handles",()=>
        {
            using var f=new Fixture();var p=f.Grip;f.Label.Text="different";
            TestSuite.Equal(f.Resize.Handle(f.Controller,new(ChartInputKind.Down,p)),ModifierResult.Unhandled);
            f.Label.X1=1.2;f.Render();p=f.Grip;TestSuite.True(!f.Layout.HitWidthGrip(p));
            TestSuite.Equal(f.Resize.Handle(f.Controller,new(ChartInputKind.Down,p)),ModifierResult.Unhandled);
        });
        suite.Run("width handle is selected only and absent for unwrapped or readonly text",()=>
        {
            using var f=new Fixture();f.Label.IsSelected=false;f.Render();
            TestSuite.Equal(f.Resize.Handle(f.Controller,new(ChartInputKind.Down,f.Grip)),ModifierResult.Unhandled);
            f.Label.TextWrapping=AnnotationTextWrapping.NoWrap;f.Render();TestSuite.True(f.Layout.WidthGrip is null);
            f.Label.TextWrapping=AnnotationTextWrapping.Wrap;f.Label.IsEditable=false;f.Render();TestSuite.True(f.Layout.WidthGrip is null);
        });
        suite.Run("retained width handle is immutable and draws at the hit-tested position",()=>
        {
            using var f=new Fixture();var old=f.Layout;var grip=old.WidthGrip;var canvas=new RecordingCanvas();old.Draw(canvas,true);
            var marker=canvas.Commands.Single(c=>c.Kind=="ellipse");TestSuite.Near(marker.Points[0].X,grip!.Value.Position.X-4);
            f.Label.TextWrapWidth=90;f.Render();TestSuite.Equal(old.WidthGrip,grip);TestSuite.True(f.Layout.WidthGrip!=grip);
            old.Draw(new RecordingCanvas(),true);TestSuite.Throws<ArgumentOutOfRangeException>(()=>old.HitWidthGrip(grip.Value.Position,0));
        });
        suite.Run("width handle layout change and disabling release preview safely",()=>
        {
            using var f=new Fixture();f.Send(ChartInputKind.Down,f.Grip);f.Send(ChartInputKind.Move,f.Offset(20));f.Render(800);
            f.Send(ChartInputKind.Move,f.Grip);TestSuite.True(!f.Resize.IsResizing);TestSuite.Equal(f.Label.TextWrapWidth,160d);f.Controller.Cancel();
            f.Render();f.Send(ChartInputKind.Down,f.Grip);f.Send(ChartInputKind.Move,f.Offset(20));f.Resize.IsEnabled=false;
            f.Send(ChartInputKind.Up,f.Grip);TestSuite.Equal(f.Label.TextWrapWidth,160d);TestSuite.True(!f.Controller.History.CanUndo);
        });
        suite.Run("width gesture disposal rolls back and detaches observers",()=>
        {
            using var f=new Fixture();f.Send(ChartInputKind.Down,f.Grip);f.Send(ChartInputKind.Move,f.Offset(30));f.Controller.Dispose();
            TestSuite.Equal(f.Label.TextWrapWidth,160d);f.Label.TextWrapWidth=200;TestSuite.True(!f.Resize.IsResizing);
        });
        suite.Run("captured width moves avoid million-point rollover snapshots",()=>
        {
            using var f=new Fixture();var data=new ObservedData();const int count=1000000;
            data.AppendRange(Enumerable.Range(0,count).Select(i=>(double)i).ToArray(),Enumerable.Repeat(5d,count).ToArray());
            f.Model.RenderableSeries.Add(new(){DataSeries=data});f.Render();var p=f.Grip;var end=f.Offset(1);
            f.Send(ChartInputKind.Down,p);long before=data.Reads;
            for(int i=0;i<100;i++)f.Send(ChartInputKind.Move,i%2==0?end:p);
            long reads=data.Reads-before;TestSuite.Equal(reads,0L);TestSuite.True(f.Controller.View.Cursor is null);f.Controller.Cancel();
            var windows=new long[3];HitWindow(f.Layout,p);for(int i=0;i<3;i++){windows[i]=HitWindow(f.Layout,p);TestSuite.Equal(windows[i],0L);}
            Directory.CreateDirectory("artifacts/performance");File.WriteAllText("artifacts/performance/text-width-resize.json",JsonSerializer.Serialize(new{sourcePoints=count,
                moves=100,rolloverSnapshotReads=reads,hitQueriesPerWindow=10000,hitAllocatedBytes=windows,scope="Captured input without frame rendering; cached handle-hit allocation. Excludes relayout, UI, GPU, and presentation."}));
        });
        suite.Run("actual Skia selected width grip survives retained scene replay",()=>
        {
            using var f=new Fixture();using var surface=SKSurface.Create(new SKImageInfo(1000,700));using var cache=new SkiaChartSceneCache();
            var first=cache.Render(surface.Canvas,f.Model,1000,700);var second=cache.Render(surface.Canvas,f.Model,1000,700);
            TestSuite.True(cache.Statistics.LastFrameReusedScene&&ReferenceEquals(first.FindTextAnnotation(f.Label),second.FindTextAnnotation(f.Label)));
            var p=second.FindTextAnnotation(f.Label)!.WidthGrip!.Value.Position;using var image=surface.Snapshot();using var bitmap=SKBitmap.FromImage(image);
            bool ink=false;for(int y=(int)p.Y-5;y<=(int)p.Y+5;y++)for(int x=(int)p.X-5;x<=(int)p.X+5;x++)
                if(bitmap.GetPixel(x,y).Red!=bitmap.GetPixel((int)p.X,(int)p.Y).Red)ink=true;
            TestSuite.True(ink);f.Label.TextWrapWidth=200;cache.Render(surface.Canvas,f.Model,1000,700);TestSuite.True(!cache.Statistics.LastFrameReusedScene);
        });
    }
}
