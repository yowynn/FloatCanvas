using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;

/// <summary>Explicit guarded physical mouse gate; restores the cursor and releases injected buttons.</summary>
internal static class AvatarRightReleaseTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check,DesktopSession session,OverlayHost host,Window toolbar)
    {
        var launcher=(ToolbarLauncher)typeof(DesktopSession).GetField("_toolbarLauncher",Private)!.GetValue(session)!;
        var menu=(OverlayContextMenu)typeof(DesktopSession).GetField("_toolbarMenu",Private)!.GetValue(session)!;
        check("physical mouse fixture starts with both buttons released",Mouse.LeftButton==MouseButtonState.Released&&Mouse.RightButton==MouseButtonState.Released);
        if(!GetCursorPos(out var originalCursor))throw new InvalidOperationException("Could not save the desktop cursor.");
        bool rightHeld=false,leftHeld=false;
        var handle=new WindowInteropHelper(toolbar).Handle;
        var screen=launcher.PointToScreen(new Point(60,70));
        try
        {
            menu.IsOpen=false; launcher.CancelPendingGestures(); Pump();
            if(Environment.GetEnvironmentVariable("FLOATCANVAS_MENU_INITIAL_FOCUS")=="surface")
            {
                typeof(OverlayWindowScope).GetMethod("FocusSurface",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[toolbar]);Pump();
            }
            Console.WriteLine($"RIGHT_RELEASE initial focus={Keyboard.FocusedElement?.GetType().Name}");
            SetCursorPos((int)Math.Round(screen.X),(int)Math.Round(screen.Y)); Pump();
            var hit=WindowFromPoint(new NativePoint {X=(int)Math.Round(screen.X),Y=(int)Math.Round(screen.Y)});
            check("physical right-click fixture targets only its own avatar HWND",GetAncestor(hit,2)==handle);
            MouseInput(0x0008); rightHeld=true; Pump(1000);
            check("avatar fixture keeps the actual right button held throughout opening",(GetAsyncKeyState(2)&0x8000)!=0);
            Console.WriteLine($"RIGHT_RELEASE down menu={menu.IsOpen} focus={Keyboard.FocusedElement?.GetType().Name} right={Mouse.RightButton} capture={Mouse.Captured?.GetType().Name}");
            check("native avatar right-down immediately opens the actual main menu",menu.IsOpen);
            check("main menu watches native foreground changes for its open lifetime",(nint)typeof(OverlayContextMenu).GetField("foregroundHook",Private)!.GetValue(menu)! != 0);
            MouseInput(0x0010); rightHeld=false; Pump();
            Console.WriteLine($"RIGHT_RELEASE up menu={menu.IsOpen} focus={Keyboard.FocusedElement?.GetType().Name} right={Mouse.RightButton} editing={host.IsEditing}");
            check("releasing the same avatar right press leaves its main menu open",menu.IsOpen&&host.IsEditing);
            var file=menu.Items.OfType<System.Windows.Controls.MenuItem>().First(item=>item.HasItems);
            var headerPoint=file.PointToScreen(new Point(40,file.ActualHeight/2));
            SetCursorPos((int)Math.Round(headerPoint.X)+3,(int)Math.Round(headerPoint.Y));Pump(50);
            SetCursorPos((int)Math.Round(headerPoint.X),(int)Math.Round(headerPoint.Y));
            WaitUntil(()=>file.IsMouseOver,1000);
            WaitUntil(()=>file.IsSubmenuOpen,SystemParameters.MenuShowDelay+1000);
            Console.WriteLine($"RIGHT_RELEASE hover menu={menu.IsOpen} nested={file.IsSubmenuOpen} over={file.IsMouseOver} role={file.Role} target={Mouse.DirectlyOver?.GetType().Name}");
            check("right-opened main menu still opens its nested menu on hover",menu.IsOpen&&file.IsSubmenuOpen);
            Console.WriteLine($"RIGHT_RELEASE hover capture={Mouse.Captured?.GetType().Name} focus={Keyboard.FocusedElement?.GetType().Name}");
            var eyes=(System.Windows.Controls.Button)typeof(DesktopSession).GetField("_canvasVisibilityButton",Private)!.GetValue(session)!;
            var eyesPoint=eyes.PointToScreen(new Point(eyes.ActualWidth/2,eyes.ActualHeight/2));
            bool visible=host.IsVisible;
            SetCursorPos((int)Math.Round(eyesPoint.X),(int)Math.Round(eyesPoint.Y));Pump();
            check("outside-click fixture targets its own toolbar button",GetAncestor(WindowFromPoint(new NativePoint {X=(int)Math.Round(eyesPoint.X),Y=(int)Math.Round(eyesPoint.Y)}),2)==handle);
            MouseInput(0x0002);leftHeld=true;Pump();MouseInput(0x0004);leftHeld=false;Pump();
            check("one physical outside click closes both menus and executes the toolbar action",!menu.IsOpen&&!file.IsSubmenuOpen&&host.IsVisible!=visible);
            eyes.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Pump();
            menu.IsOpen=true;Pump();
            var receiver=new Window {Title="FloatCanvas menu focus receiver",Width=180,Height=100,ShowActivated=true,ShowInTaskbar=false};
            OverlayWindowScope.UseRegularWindow(receiver);
            try {receiver.Show();receiver.Activate();Pump();check("moving focus to another actual window still closes the main menu",!menu.IsOpen);}
            finally {receiver.Close();}
        }
        finally {if(rightHeld)MouseInput(0x0010);if(leftHeld)MouseInput(0x0004);SetCursorPos(originalCursor.X,originalCursor.Y);menu.IsOpen=false;launcher.CancelPendingGestures(); }
        GetCursorPos(out var restored);
        check("physical mouse fixture restores the original cursor and releases its buttons",restored.X==originalCursor.X&&restored.Y==originalCursor.Y&&Mouse.LeftButton==MouseButtonState.Released&&Mouse.RightButton==MouseButtonState.Released);
        check("closing the menu releases its native foreground listener",(nint)typeof(OverlayContextMenu).GetField("foregroundHook",Private)!.GetValue(menu)! == 0);
    }
    private static void Pump()
    {
        var frame=new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false); Dispatcher.PushFrame(frame);
    }
    private static void Pump(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>frame.Continue=false;timer.Start();try {Dispatcher.PushFrame(frame);}finally {timer.Stop();}
    }
    private static void WaitUntil(Func<bool> ready,int timeout)
    {
        long deadline=Environment.TickCount64+timeout;
        while(!ready()&&Environment.TickCount64<deadline)Pump(20);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint {public int X,Y;}
    private static void MouseInput(uint flags)
    {
        var input=new NativeInput {Data=new() {Mouse=new() {Flags=flags}}};
        if(SendInput(1,[input],Marshal.SizeOf<NativeInput>())!=1)throw new InvalidOperationException("Could not inject into the guarded avatar fixture.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput {public uint Type;public InputData Data;}
    [StructLayout(LayoutKind.Explicit)] private struct InputData {[FieldOffset(0)]public MouseData Mouse;}
    [StructLayout(LayoutKind.Sequential)] private struct MouseData {public int X,Y;public uint Data,Flags,Time;public nuint Extra;}
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd,uint flags);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count,NativeInput[] input,int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
