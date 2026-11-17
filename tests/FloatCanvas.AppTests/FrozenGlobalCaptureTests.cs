using System.IO;
using System.Reflection;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class FrozenGlobalCaptureTests
{
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check,string output)
    {
        var app=Application.Current??new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        using var bitmap=new SKBitmap(320,240);bitmap.Erase(SKColors.Gold);
        using var image=SKImage.FromBitmap(bitmap);using var png=image.Encode(SKEncodedImageFormat.Png,100);
        var desktop=new CapturedDesktop(new(0,0,320,240),png.ToArray());
        foreach(bool perspective in new[]{false,true})
        foreach(var shape in Enum.GetValues<CaptureShape>())
        {
            var scene=new SceneDocument {Viewport=new(0,0,320,240)};
            scene.Canvas.Projection.Enabled=perspective;
            if(perspective)
            {
                scene.Canvas.Projection.Matrix=new(1,0,0,0,1,0,.001,.0003,1);
                scene.Canvas.Projection.Quad=[new(0,0),new(320,0),new(280,200),new(0,240)];
            }
            var parameters=new ToolParameters {Tool=EditorTool.CaptureRegion,CaptureShape=shape,CaptureSource=CaptureSource.Background,CaptureBorderWidth=3};
            var picker=new FrozenRegionCaptureWindow(desktop,scene,parameters,CanvasMapping.Create(scene),default,false,BillboardCreationWorkflow.ScreenFirst);
            if(!perspective && shape==CaptureShape.Rectangle)
            {
                picker.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,new Action(()=>
                {
                    check("frozen selector shows an opaque crosshair surface",picker.IsVisible && picker.Cursor==System.Windows.Input.Cursors.Cross && picker.ActualWidth>0 && picker.ActualHeight>0);
                    var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(picker);
                    check("frozen selector covers the captured physical screen bounds",Math.Abs(picker.ActualWidth*dpi.DpiScaleX-320)<1 && Math.Abs(picker.ActualHeight*dpi.DpiScaleY-240)<1);
                    Select(picker);
                }));
                picker.ShowDialog();
            }
            else Select(picker);
            check($"frozen selector completes {shape} perspective={perspective}",picker.Selection is { } selected&&selected.Shape==shape&&selected.BorderWidth==3);
            if(shape is CaptureShape.Rectangle or CaptureShape.Ellipse)
                check($"frozen selector preserves plane geometry {shape} perspective={perspective}",perspective== (picker.Selection!.PlaneToScreen is not null));
            var raster=SnapshotImageCommand.Render(scene,[],picker.Selection!,desktop,[scene.Viewport]);
            check("frozen selection renders its captured pixels",raster.HasVisiblePixels);
            picker.Close();
        }
        ApplicationPaths.Configure(["--profile",Path.Combine(output,"profile-"+Guid.NewGuid().ToString("N"))]);
        new UserSettings {PreferGpuAcceleration=false,ShowFloatingLogo=false}.Save();
        Func<IReadOnlyList<MonitorInfo>> monitors=()=>[new("frozen-global",new(0,0,320,240),new(0,0,320,240),96,96,true)];
        using var host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),Hidden,null,[OverlayMonitorScope.PrimaryScreen,monitors,false],null)!;
        using var session=new DesktopSession(app,host);
        Field<System.Windows.Forms.NotifyIcon>(session,"_tray").Visible=false;
        var editor=Field<EditorController>(session,"_editor");editor.Tool=EditorTool.Freehand;
        Set(session,"_captureGlobalDesktop",(Func<Task<CapturedDesktop>>)(()=>throw new InvalidOperationException("capture failure fixture")));
        bool failed = false;
        try { Await((Task)Call(session,"CaptureGlobalRegionAsync")!); }
        catch (InvalidOperationException) { failed = true; }
        check("failed capture releases its pending command state", failed && Field<object?>(session,"_globalCapture") is null && !Field<bool>(session,"_documentBusyValue"));
        Set(session,"_captureGlobalDesktop",(Func<Task<CapturedDesktop>>)(()=>Task.FromResult(desktop)));
        foreach(bool visible in new[]{false,true})
        {
            typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host,visible);
            editor.SetCanvasView(CanvasMapping.CreateViewing(editor.Session.Document, new CanvasViewTransform(2, new(90, 70))), new(15, 25), new object());
            Set(session,"_selectGlobalRegion",(Func<FrozenRegionCaptureWindow,bool?>)(picker=>
            {
                check("selection precedes edit entry and does not reveal hidden canvas",!host.IsEditing&&host.IsVisible==visible&&editor.Tool==EditorTool.Freehand);
                check("pass-through capture ignores stale editing navigation", picker.Controller.CurrentCanvasMapping.PlaneToScreen == CanvasMapping.CreateViewing(editor.Session.Document, CanvasViewTransform.Identity).PlaneToScreen);
                picker.Close();return false;
            }));
            Await((Task)Call(session,"CaptureGlobalRegionAsync")!);
            check("cancelled global capture preserves visibility mode and tool",!host.IsEditing&&host.IsVisible==visible&&editor.Tool==EditorTool.Freehand&&editor.Session.Document.Elements.Count==0);
        }
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host,true);
        foreach(bool visible in new[]{false,true})
        {
            typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host,visible);
            int count=editor.Session.Document.Elements.Count;
            Set(session,"_selectGlobalRegion",(Func<FrozenRegionCaptureWindow,bool?>)(picker=>{Select(picker);picker.Close();return true;}));
            Await((Task)Call(session,"CaptureGlobalRegionAsync")!);
            check("completed global capture reveals canvas inserts once and preserves tool",host.IsVisible&&host.IsEditing&&editor.Tool==EditorTool.Freehand&&editor.Session.Document.Elements.Count==count+1);
        }
    }
    private static void Select(FrozenRegionCaptureWindow picker)
    {
        var editor=picker.Controller;
        void Pointer(OverlayPointerKind kind,double x,double y)=>editor.Pointer(new(kind,new(x,y),kind==OverlayPointerKind.Move?OverlayMouseButton.None:OverlayMouseButton.Left,0));
        if(editor.CaptureShape==CaptureShape.PolygonLasso)
        {
            foreach(var p in new[]{new PointD(30,30),new(170,30),new(170,130),new(30,130)}) {Pointer(OverlayPointerKind.Down,p.X,p.Y);Pointer(OverlayPointerKind.Up,p.X,p.Y);}
            editor.HandleCaptureKey(0x0D);
        }
        else
        {
            Pointer(OverlayPointerKind.Down,30,30);Pointer(OverlayPointerKind.Move,170,30);Pointer(OverlayPointerKind.Move,170,130);
            if(editor.CaptureShape==CaptureShape.FreehandLasso)Pointer(OverlayPointerKind.Move,30,130);
            Pointer(OverlayPointerKind.Up,170,130);
        }
    }
    private static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,Hidden)!.GetValue(value)!;
    private static void Set(object value,string name,object data)=>value.GetType().GetField(name,Hidden)!.SetValue(value,data);
    private static object? Call(object value,string name)=>value.GetType().GetMethod(name,Hidden)!.Invoke(value,null);
    private static void Await(Task task)
    {
        while(!task.IsCompleted) {var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>frame.Continue=false));System.Windows.Threading.Dispatcher.PushFrame(frame);Thread.Sleep(1);}
        task.GetAwaiter().GetResult();
    }
}
