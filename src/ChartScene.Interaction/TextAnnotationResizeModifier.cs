using System.Collections.Specialized;
using System.ComponentModel;
using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Rendering;

namespace ChartScene.Interaction;

/// <summary>
/// Resizes a selected wrapped label using its frame-owned width handle. X1/Y1,
/// font size, and source text stay fixed. Interaction is owned by one UI thread.
/// </summary>
public sealed class TextAnnotationResizeModifier:ChartModifier,IChartCursorOwner
{
    private double _minimumWidth=16,_maximumWidth=4096,_hitRadius=8;
    private Session? _session;
    public double MinimumWidth { get=>_minimumWidth;set{Positive(value);if(value>_maximumWidth)throw new ArgumentOutOfRangeException(nameof(value));_minimumWidth=value;} }
    public double MaximumWidth { get=>_maximumWidth;set{Positive(value);if(value<_minimumWidth)throw new ArgumentOutOfRangeException(nameof(value));_maximumWidth=value;} }
    public double HitRadius { get=>_hitRadius;set{Positive(value);_hitRadius=value;} }
    public bool IsResizing=>_session is not null;
    public bool OwnsCursor=>IsResizing;
    private static void Positive(double value){if(!double.IsFinite(value)||value<=0)throw new ArgumentOutOfRangeException(nameof(value));}

    public override ModifierResult Handle(ChartController controller,ChartInput input)
    {
        if(_session is { } active)
        {
            if(input.PointerId!=active.PointerId||active.Writing)return ModifierResult.Handled;
            if(!IsEnabled||controller.Mode!=active.Mode||controller.Frame?.Layout.PlotArea!=active.Plot)
            { Cancel(controller);return ModifierResult.Handled; }
            if(input.Kind is ChartInputKind.Move or ChartInputKind.Up && input.Position.IsFinite)
            {
                double delta=active.Layout.ToLocal(input.Position).X-active.StartLocalX;
                // Returning to the press point must preserve the exact original double.
                // A pre-existing out-of-range programmatic width is not clamped by a stationary press.
                double width=delta==0?active.Before:Math.Clamp(active.Before+delta*active.Factor,_minimumWidth,_maximumWidth);
                if(double.IsFinite(width)&&width!=active.Expected)Write(active,width);
            }
            if(input.Kind==ChartInputKind.Up && ReferenceEquals(_session,active))
            {
                Release(active);
                if(active.Expected!=active.Before)
                    controller.History.RecordExecuted(new WidthCommand(controller.Model,active.Annotation,active.Wrapping,active.Before,active.Expected));
            }
            return ModifierResult.Handled;
        }
        if(!IsEnabled||controller.HasCapture||input.Kind is not (ChartInputKind.Down or ChartInputKind.DoubleClick)||!input.Position.IsFinite||controller.Frame is not { } frame
            ||input.Kind==ChartInputKind.Down&&frame.Statistics.ModelRevision!=controller.Model.Revision)return ModifierResult.Unhandled;
        // Frame order is paint order, including annotation layers. No reversed iterator or per-candidate measurement.
        for(int index=frame.TextAnnotations.Count-1;index>=0;index--)
        {
            var region=frame.TextAnnotations[index];var a=region.Annotation;
            if(!a.IsVisible||!a.IsEditable||!a.IsSelected||a.TextWrapping==AnnotationTextWrapping.NoWrap
                ||!region.Layout.HitWidthGrip(input.Position,_hitRadius)||!controller.Model.Annotations.Contains(a))continue;
            // A native double tap on an adorner is not a request to edit the body.
            // As with body editing, the painted grip remains authoritative while a
            // selection-only change is waiting for its next frame.
            if(input.Kind==ChartInputKind.DoubleClick)return ModifierResult.Handled;
            var session=new Session(this,controller,a,region.Layout,input);
            _session=session;session.Attach();
            controller.View.Cursor=null;controller.View.Hits=[];
            return ModifierResult.Capture;
        }
        return ModifierResult.Unhandled;
    }
    private void Write(Session session,double width)
    {
        session.Expected=width;session.Writing=true;
        try{session.Annotation.TextWrapWidth=width;}
        catch{Release(session);throw;}
        finally{session.Writing=false;}
        // Subscribers can synchronously cancel, detach, or supersede a preview.
        if(ReferenceEquals(_session,session)&&session.Annotation.TextWrapWidth!=width)Release(session);
    }
    private void Release(Session session)
    {
        session.Detach();
        if(ReferenceEquals(_session,session))_session=null;
    }
    public override void Cancel(ChartController controller)
    {
        if(_session is not { } session)return;
        Release(session); // detach BEFORE rollback notifications can reenter this modifier
        if(session.Annotation.TextWrapWidth==session.Expected&&controller.Model.Annotations.Contains(session.Annotation))
            session.Annotation.TextWrapWidth=session.Before;
    }
    private sealed class Session
    {
        private readonly TextAnnotationResizeModifier _owner;
        private readonly ChartModel _model;
        private readonly AxisModel _x,_y;
        public ChartAnnotation Annotation { get; }
        public TextAnnotationLayout Layout { get; }
        public RectD Plot { get; }
        public ChartInteractionMode Mode { get; }
        public AnnotationTextWrapping Wrapping { get; }
        public int PointerId { get; }
        public double StartLocalX { get; }
        public double Factor { get; }
        public double Before { get; }
        public double Expected { get; set; }
        public bool Writing { get; set; }
        public Session(TextAnnotationResizeModifier owner,ChartController c,ChartAnnotation a,TextAnnotationLayout layout,ChartInput input)
        {
            _owner=owner;_model=c.Model;Annotation=a;Layout=layout;Plot=c.Frame!.Layout.PlotArea;Mode=c.Mode;
            _x=_model.FindXAxis(a.XAxisId);_y=_model.FindYAxis(a.YAxisId);
            Before=Expected=a.TextWrapWidth;Wrapping=a.TextWrapping;PointerId=input.PointerId;
            StartLocalX=layout.ToLocal(input.Position).X;Factor=layout.WidthGrip!.Value.WidthPerPixel;
        }
        public void Attach()
        {
            Annotation.PropertyChanged+=AnnotationChanged;_x.PropertyChanged+=GeometryChanged;_y.PropertyChanged+=GeometryChanged;
            _model.Annotations.CollectionChanged+=CollectionChanged;_model.XAxes.CollectionChanged+=CollectionChanged;_model.YAxes.CollectionChanged+=CollectionChanged;
        }
        public void Detach()
        {
            Annotation.PropertyChanged-=AnnotationChanged;_x.PropertyChanged-=GeometryChanged;_y.PropertyChanged-=GeometryChanged;
            _model.Annotations.CollectionChanged-=CollectionChanged;_model.XAxes.CollectionChanged-=CollectionChanged;_model.YAxes.CollectionChanged-=CollectionChanged;
        }
        private void AnnotationChanged(object? sender,PropertyChangedEventArgs e)
        {
            if(Writing&&e.PropertyName==nameof(ChartAnnotation.TextWrapWidth)&&Annotation.TextWrapWidth==Expected)return;
            // An external annotation edit is authoritative; never roll it back on a later Cancel/Up.
            _owner.Release(this);
        }
        private void GeometryChanged(object? sender,PropertyChangedEventArgs e)=>_owner.Release(this);
        private void CollectionChanged(object? sender,NotifyCollectionChangedEventArgs e)=>_owner.Release(this);
    }
    private sealed class WidthCommand(ChartModel model,ChartAnnotation annotation,AnnotationTextWrapping wrapping,double before,double after):IUndoableCommand
    {
        private bool _invalid;
        public string Description=>"Resize annotation width";
        public void Execute()=>Apply(before,after);
        public void Undo()=>Apply(after,before);
        private void Apply(double expected,double value)
        {
            if(_invalid)return;
            if(!model.Annotations.Contains(annotation)||!annotation.IsEditable||annotation.TextWrapping!=wrapping||annotation.TextWrapWidth!=expected)
            { _invalid=true;return; }
            annotation.TextWrapWidth=value;
            if(annotation.TextWrapWidth!=value)_invalid=true;
        }
    }
}
