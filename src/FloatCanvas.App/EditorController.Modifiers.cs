using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private OverlayModifiers gestureModifiers;
    private int? straightStrokeStart;
    private uint? modifierTime;
    private IList<PointD>? ActiveStroke => gesture=="draw"&&drawing is {Kind:ElementKind.Freehand or ElementKind.Highlighter} stroke
        ? (IList<PointD>)stroke.Points : gesture=="capture"&&CaptureShape==CaptureShape.FreehandLasso ? (IList<PointD>)capturePoints : null;

    internal void ModifiersChanged(OverlayModifiers modifiers,OverlayPoint? position=null,uint? time=null,bool? pointerLeftDown=null)
    {
        if(gesture is null||(gestureModifiers&OverlayModifiers.Shift)==(modifiers&OverlayModifiers.Shift))return;
        if(time is { } inputTime&&modifierTime is { } boundary&&unchecked((int)(inputTime-boundary))<0)return;
        modifierTime=time??modifierTime;
        if(pointerLeftDown==false&&!temporaryMove&&gesture!="capture-polygon")
        {gestureModifiers=modifiers;return;}
        var screen=position is { } point?new PointD(point.X-ScreenOrigin.X,point.Y-ScreenOrigin.Y):pointerScreen;
        PointD current;
        if(HasCaptureGesture)current=screen;
        else if(UsesBillboardDrawingInput)current=BillboardInput(screen);
        else if(!CanvasMapping.Create(Session.Document).TryToPlane(screen,out current))
        { if(IsPlanarStrokeDrawing)strokeInterrupted=true;RejectPointer();return; }
        pointerScreen=screen;
        if(IsPlanarStrokeDrawing&&!IsStrokeCandidateSafe(current,out _))
        {strokeInterrupted=true;RejectPointer();return;}
        if((modifiers&OverlayModifiers.Shift)!=0&&!(IsPlanarStrokeDrawing&&strokeInterrupted)&&ActiveStroke is { } points)
        {
            AppendStrokePoint(points,gesture=="draw"?current-start:current,force:true);
            straightStrokeStart=points.Count-1;
        }
        TrackGestureMovement();
        lastGestureMove=null;
        MoveSafely(current,modifiers);
    }

    private bool ConstrainBox(OverlayModifiers modifiers)=>ConstrainAspect^((modifiers&OverlayModifiers.Shift)!=0);
    private static PointD SquareEnd(PointD anchor,PointD point)
    {
        var delta=point-anchor;double side=Math.Max(Math.Abs(delta.X),Math.Abs(delta.Y));
        return anchor+new PointD(Math.CopySign(side,delta.X),Math.CopySign(side,delta.Y));
    }
    private static PointD EightDirectionEnd(PointD anchor,PointD point)
    {
        var delta=point-anchor;
        double angle=Math.Round(Math.Atan2(delta.Y,delta.X)/(Math.PI/4))*(Math.PI/4);
        double x=Math.Cos(angle),y=Math.Sin(angle),distance=delta.X*x+delta.Y*y;
        return anchor+new PointD(x*distance,y*distance);
    }
    private void UpdateStroke(IList<PointD> points,PointD point,OverlayModifiers modifiers)
    {
        bool straight=(modifiers&OverlayModifiers.Shift)!=0;
        if(straight||straightStrokeStart is not null)
        {
            straightStrokeStart??=points.Count-1;
            int endpoint=straightStrokeStart.Value+1;
            if(endpoint<points.Count)points[endpoint]=point;
            else if(gesture=="draw"||points.Count<MaximumCapturePoints)points.Add(point);
            if(!straight)straightStrokeStart=null;
        }
        else AppendStrokePoint(points,point);
    }
    private void AppendStrokePoint(IList<PointD> points,PointD point,bool force=false)
    {
        if(gesture!="draw"&&points.Count>=MaximumCapturePoints)return;
        if(points.Count==0){points.Add(point);return;}
        double distance=points[^1].DistanceTo(point);
        if(gesture=="draw"&&drawing is not null)
        {
            var mapping = billboardDrawing && billboardDepth is { } depth ?
                Projective2D.FromAffine(Affine2D.Scale(depth.Scale, depth.Scale)).Compose(ElementGeometry.LocalToPlane(drawing)) :
                CanvasMapping.Create(Session.Document).ElementToScreen(drawing);
            distance=mapping.TransformPoint(points[^1]).DistanceTo(mapping.TransformPoint(point));
        }
        if(distance>=(force?1e-9:.75))points.Add(point);
    }
}
