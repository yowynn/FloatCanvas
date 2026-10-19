using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class Preview4ShiftRuntimeFixture
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check,DesktopSession session,OverlayHost host,PixelBounds bounds,Action<bool,bool,OverlayPoint> shift)
    {
        var editor=Get<EditorController>(session,"_editor");
        editor.Session.Execute("Shift fixture",document=>document.Elements.Clear());
        Activate(EditorTool.Freehand);host.TryFocusEditingCanvas();
        var route=Get<Action<OverlayPointerEvent>>(host,"Pointer");
        var origin=new OverlayPoint(bounds.Left+100,bounds.Top+100);int undo=editor.Session.UndoCount;
        Pointer(OverlayPointerKind.Down,0,0);Pointer(OverlayPointerKind.Move,10,15);
        shift(false,true,Point(20,10));Pointer(OverlayPointerKind.Move,80,50,OverlayModifiers.Shift);Pointer(OverlayPointerKind.Move,100,30,OverlayModifiers.Shift);
        var path=editor.Session.Document.Elements.Single();
        check("Shift native: production key dispatch starts the line at the packet's pointer sample",path.Points.SequenceEqual([new PointD(0,0),new(10,15),new(20,10),new(100,30)]));
        shift(true,false,Point(110,50));
        check("Shift native: key release freezes its own endpoint before any following pointer event",path.Points[^1]==new PointD(110,50));
        Pointer(OverlayPointerKind.Move,120,70);Pointer(OverlayPointerKind.Up,120,70);
        check("Shift native: the following free curve commits with the line in one undo step",path.Points[^1]==new PointD(120,70)&&path.Points.Count==5&&editor.Session.UndoCount==undo+1&&!editor.Session.IsTransactionActive);
        Activate(EditorTool.CaptureRegion);editor.CaptureShape=CaptureShape.FreehandLasso;
        Pointer(OverlayPointerKind.Down,200,0);Pointer(OverlayPointerKind.Move,210,20);
        shift(false,true,Point(220,15));Pointer(OverlayPointerKind.Move,240,60,OverlayModifiers.Shift);Pointer(OverlayPointerKind.Move,260,40,OverlayModifiers.Shift);
        check("Shift native: the same private key route straightens a freehand capture preview",editor.CapturePreview!.Points.Count==4&&editor.CapturePreview.Points[2]==new PointD(origin.X+220,origin.Y+15));
        shift(true,false,Point(270,50));
        check("Shift native: capture release freezes its packet-time endpoint",editor.CapturePreview!.Points[^1]==new PointD(origin.X+270,origin.Y+50));
        route(new(OverlayPointerKind.Cancel,Point(270,50),OverlayMouseButton.None,OverlayModifiers.None));
        check("Shift native: cancelling a lasso discards its transient straight segment",!editor.HasCaptureGesture&&editor.Session.Document.Elements.Count==1&&!editor.Session.IsTransactionActive);
        Activate(EditorTool.Arrow);
        shift(false,true,Point(0,140));Pointer(OverlayPointerKind.Down,0,140,OverlayModifiers.Shift);Pointer(OverlayPointerKind.Move,80,200,OverlayModifiers.Shift);
        var arrow=editor.Session.Document.Elements.Last();
        check("Shift native: arrow preview follows the eight-direction constraint",Math.Abs(arrow.Points[^1].X-arrow.Points[^1].Y)<1e-6);
        shift(true,false,Point(80,200));
        check("Shift native: releasing Shift immediately restores the unsnapped arrow endpoint",arrow.Points[^1]==new PointD(80,60));
        Pointer(OverlayPointerKind.Up,80,200);
        return;

        void Activate(EditorTool tool)=>typeof(DesktopSession).GetMethod("ActivateTool",Private)!.Invoke(session,[tool]);
        OverlayPoint Point(double x,double y)=>new(origin.X+x,origin.Y+y);
        void Pointer(OverlayPointerKind kind,double x,double y,OverlayModifiers modifiers=OverlayModifiers.None)=>route(new(kind,Point(x,y),OverlayMouseButton.Left,modifiers));
    }
    private static T Get<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
}
