using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class InlineTextRuntimeFixture
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check,bool preview=false,bool properties=false,bool shifts=false)
    {
        if(Application.Current is not null)throw new InvalidOperationException("Run alone.");
        string profile=Path.Combine(Path.GetTempPath(),"FloatCanvas-InlineText",Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile",profile,"--primary-screen"]);
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var previousContext=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        nint foreground=GetForegroundWindow();var originalKeys=new byte[256];GetKeyboardState(originalKeys);
        DesktopSession? session=null;OverlayHost? host=null;
        try
        {
            var primary=((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null)!).First(m=>m.IsPrimary);
            var bounds=new PixelBounds(primary.WorkArea.Left+24,primary.WorkArea.Top+24,640,360);
            Func<IReadOnlyList<MonitorInfo>> provider=()=>[primary with {Bounds=bounds,WorkArea=bounds}];
            host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),Private,null,[OverlayMonitorScope.PrimaryScreen,provider,false],null)!;
            session=new DesktopSession(app,host);Get<System.Windows.Forms.NotifyIcon>(session,"_tray").Visible=false;
            var toolbar=Get<Window>(session,"_toolbar");toolbar.Show();Pump();
            host.SetVisible(true);Activate(new WindowInteropHelper(toolbar).Handle,()=>Invoke("SetEditing",true,false));Pump();
            Invoke("ActivateTool",EditorTool.Text);Invoke("CloseToolOptions");
            var editor=Get<EditorController>(session,"_editor");nint input=host.Diagnostics.Single().InputHandle;
            var dispatch=Get<Action<KeyboardInputPacket,Action>>(host,"_dispatchPrivateKey");
            Click(100,100);
            var edit=Get<InlineTextEdit>(session,"_inlineText");
            check("native canvas click creates an inline draft and no visible text dialog",editor.Session.IsTransactionActive&&editor.Session.Document.Elements.Count==1&&
                !app.Windows.Cast<Window>().Any(window=>window.IsVisible&&window.Title is "浮绘 · 编辑文字" or "FloatCanvas · Edit text"));
            check("the canvas text input takes native and logical keyboard focus",Get<CanvasTextInput>(session,"_canvasTextInput").IsActive);
            var caretTimer=Get<DispatcherTimer>(session,"_inlineCaretTimer");caretTimer.Stop();
            typeof(DesktopSession).GetField("_inlineCaretVisible",Private)!.SetValue(session,true);caretTimer.Start();
            var blinkFrame=new DispatcherFrame();
            var idleMoves=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(35)};
            idleMoves.Tick+=(_,_)=>Get<Action<OverlayPointerEvent>>(host,"Pointer")(new(OverlayPointerKind.Move,new(bounds.Left+110,bounds.Top+110),OverlayMouseButton.None,OverlayModifiers.None));
            var blinkStop=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(650)};
            blinkStop.Tick+=(_,_)=>blinkFrame.Continue=false;
            try{idleMoves.Start();blinkStop.Start();Dispatcher.PushFrame(blinkFrame);}
            finally{idleMoves.Stop();blinkStop.Stop();}
            Console.WriteLine($"INLINE_BLINK active={edit.IsActive} editing={host.IsEditing} visible={Get<bool>(session,"_inlineCaretVisible")} timer={caretTimer.IsEnabled} foreground={GetForegroundWindow():X}");
            check("inline caret blinks while idle mouse moves continue",edit.IsActive&&!Get<bool>(session,"_inlineCaretVisible"));
            if(Environment.GetEnvironmentVariable("FLOATCANVAS_ROUND2_PREVIEW_DIR") is { } textOutput)
            {
                Directory.CreateDirectory(textOutput);
                typeof(DesktopSession).GetField("_inlineCaretVisible",Private)!.SetValue(session,true);
                using var surface=SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(640,360));
                Invoke("Draw",surface);using var image=surface.Snapshot();using var data=image.Encode();
                using var file=File.Create(Path.Combine(textOutput,"inline-caret.png"));data.SaveTo(file);
            }
            Key(0x33,false);Key(0x33,true);
            Console.WriteLine($"INLINE_KEY active={edit.IsActive} editing={host.IsEditing} input={Get<CanvasTextInput>(session,"_canvasTextInput")?.IsActive} text={edit.Input.Text} foreground={GetForegroundWindow():X}");
            check("real private keyboard dispatch types into text without invoking stamp shortcuts",edit.Input.Text=="3"&&editor.Tool==EditorTool.Text);
            Key(0x0D,false,shift:true);Key(0x0D,true);
            check("Shift Enter inserts a newline and keeps inline editing active",edit.IsActive&&edit.Target.Text=="3\n");
            Key(0x34,false);Key(0x34,true);Key(0x0D,false);Key(0x0D,true);
            check("Enter finishes native text input and retains its selected canvas entity",Get<InlineTextEdit?>(session,"_inlineText") is null&&!editor.Session.IsTransactionActive&&
                editor.Session.Document.Elements.Single().Text=="3\n4"&&editor.Session.Selection.Count==1&&editor.Session.UndoCount==1);
            Click(350,180);edit=Get<InlineTextEdit>(session,"_inlineText");edit.Input.Text="second";
            Click(450,250);
            check("a canvas click outside the active text commits it and starts a distinct new layer",editor.Session.Document.Elements.Count==3&&editor.Session.UndoCount==2&&Get<InlineTextEdit>(session,"_inlineText").Target.Text=="");
            edit=Get<InlineTextEdit>(session,"_inlineText");edit.Input.Text="third";var thirdId=edit.Target.Id;
            var pointerRoute=Get<Action<OverlayPointerEvent>>(host,"Pointer");
            var inside=new OverlayPoint(bounds.Left+453,bounds.Top+253);
            pointerRoute(new(OverlayPointerKind.Down,inside,OverlayMouseButton.Left,OverlayModifiers.Control));
            pointerRoute(new(OverlayPointerKind.Up,inside,OverlayMouseButton.Left,OverlayModifiers.Control));Pump();
            check("production Ctrl-pointer route inside current text starts another layer",editor.Session.Document.Elements.Count==4&&editor.Session.UndoCount==3);
            Key(0x1B,false);Key(0x1B,true);
            check("native Escape cancels only the new draft and retains previous committed text",editor.Session.Document.Elements.Count==3&&Get<InlineTextEdit?>(session,"_inlineText") is null);
            Click(454,254,doubleClick:true);edit=Get<InlineTextEdit>(session,"_inlineText");
            check("native double click reopens the existing text without inserting another layer",edit.Target.Id==thirdId&&editor.Session.Document.Elements.Count==3);
            edit.Input.Text="third edited";
            Invoke("ActivateTool",EditorTool.Rectangle);Pump();
            check("switching tools commits inline text before the drawing controller cancels its old gesture",Get<InlineTextEdit?>(session,"_inlineText") is null&&editor.Session.Document.Elements.Any(e=>e.Text=="third edited")&&!editor.Session.IsTransactionActive);
            editor.Tool=EditorTool.Text;Click(120,285);
            edit=Get<InlineTextEdit>(session,"_inlineText");edit.Input.Text="commit on panel focus";
            Activate(new WindowInteropHelper(toolbar).Handle,()=>{toolbar.Activate();toolbar.Focus();});Pump();
            check("moving native focus to the toolbar commits the canvas text and closes its input surface",
                Get<InlineTextEdit?>(session,"_inlineText") is null&&Get<CanvasTextInput?>(session,"_canvasTextInput") is null&&
                !editor.Session.IsTransactionActive&&editor.Session.Document.Elements.Any(e=>e.Text=="commit on panel focus"));
            if(properties)Preview4PropertiesRuntimeFixture.Run(check,session,host,toolbar,bounds,(value,up)=>Key(value,up));
            if(shifts)Preview4ShiftRuntimeFixture.Run(check,session,host,bounds,(up,held,point)=>Key(0xA0,up,shift:held,position:point));
            if(preview)
            {
                Invoke("CloseToolOptions");
                editor.Session.Execute("Preview surface",document=>{document.Elements.Clear();document.Canvas.BackgroundColor=0xFFF7F8FA;});
                editor.Tool=EditorTool.Text;
                Invoke("ShowTextEditor",null!,new PointD(bounds.Left+90,bounds.Top+170));
                edit=Get<InlineTextEdit>(session,"_inlineText");
                edit.Target.Transform=edit.Target.Transform.Compose(Affine2D.Rotation(8));
                edit.Input.Text="画布内编辑\nSelect and type 👩‍🎨";
                Get<CanvasTextInput>(session,"_canvasTextInput").Focus();
                Console.WriteLine($"INLINE PREVIEW PID={Environment.ProcessId}");Console.Out.Flush();
                var previewFrame=new DispatcherFrame();var previewTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(90)};
                previewTimer.Tick+=(_,_)=>{previewTimer.Stop();previewFrame.Continue=false;};previewTimer.Start();Dispatcher.PushFrame(previewFrame);
            }
            return;

            object? Invoke(string name,params object[] args)=>typeof(DesktopSession).GetMethod(name,Private)!.Invoke(session,args);
            void Click(int x,int y,bool control=false,bool doubleClick=false)
            {
                SendMessage(input,doubleClick?0x203u:0x201u,control?9u:1u,Pack(x,y));Pump();
                SendMessage(input,0x202,control?8u:0u,Pack(x,y));Pump();
            }
            void Key(uint key,bool up,bool shift=false,bool control=false,OverlayPoint? position=null)
            {
                var state=new byte[256];state[0x10]=state[0xA0]=shift?(byte)0x80:(byte)0;
                state[0x11]=state[0xA2]=control?(byte)0x80:(byte)0;state[key]=up?(byte)0:(byte)0x80;
                var packet=(KeyboardInputPacket)Activator.CreateInstance(typeof(KeyboardInputPacket),Private,null,
                    [1L,1L,KeyboardInputPacketKind.Key,key,MapVirtualKey(key,0),0u,(uint)Environment.TickCount,up,false,GetForegroundWindow(),state,false,null,up?0x101u:0x100u],null)!;
                if(position is not null)typeof(KeyboardInputPacket).GetProperty(nameof(KeyboardInputPacket.PointerPosition))!.SetValue(packet,position);
                dispatch(packet,()=>{});Pump();
            }
        }
        finally
        {
            host?.ExitEditing();session?.Dispose();
            foreach(var window in app.Windows.Cast<Window>().ToArray())window.Close();Pump();
            SetKeyboardState(originalKeys);SynchronizationContext.SetSynchronizationContext(previousContext);
            if(foreground!=0)SetForegroundWindow(foreground);
        }
    }
    private static nint Pack(int x,int y)=>(nint)((y<<16)|(x&0xFFFF));
    private static T Get<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    private static void Activate(nint window,Action action)
    {
        uint from=GetCurrentThreadId(),to=GetWindowThreadProcessId(GetForegroundWindow(),out _);bool attached=from!=to&&AttachThreadInput(from,to,true);
        try{SetForegroundWindow(window);SetFocus(window);action();}finally{if(attached)AttachThreadInput(from,to,false);}
    }
    [DllImport("user32.dll")]private static extern nint SendMessage(nint window,uint message,nuint wParam,nint lParam);
    [DllImport("user32.dll")]private static extern uint MapVirtualKey(uint code,uint type);
    [DllImport("user32.dll")]private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")]private static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll")]private static extern bool AttachThreadInput(uint from,uint to,bool attach);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
}
