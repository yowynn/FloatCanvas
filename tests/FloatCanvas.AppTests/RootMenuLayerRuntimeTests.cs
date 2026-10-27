using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;

/// <summary>Real bottom-edge toolbar clicks, with every submenu kept closed.</summary>
internal static class RootMenuLayerRuntimeTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check,DesktopSession session,OverlayHost host,Window toolbar)
    {
        check("root menu fixture starts with both mouse buttons released",Mouse.LeftButton==MouseButtonState.Released&&Mouse.RightButton==MouseButtonState.Released);
        if(!GetCursorPos(out var cursor))throw new InvalidOperationException("Could not save the desktop cursor.");
        nint bar=new WindowInteropHelper(toolbar).Handle;
        var main=Field<OverlayContextMenu>(session,"_toolbarMenu");
        var more=Field<OverlayContextMenu>(session,"_moreToolsMenu");
        var moreTrigger=(FrameworkElement)more.PlacementTarget;
        var launcher=Field<ToolbarLauncher>(session,"_toolbarLauncher");
        var shelf=Field<StampShelfControl>(session,"_stampShelf");
        shelf.PrepareForShow();Pump();
        var slot=((StackPanel)shelf.Content).Children.OfType<Button>().ElementAt(1);
        var work=System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var dpi=VisualTreeHelper.GetDpi(toolbar);
        toolbar.Left=(work.Left+80)/dpi.DpiScaleX;
        toolbar.Top=(work.Bottom-toolbar.ActualHeight*dpi.DpiScaleY)/dpi.DpiScaleY;Pump();
        typeof(DesktopSession).GetMethod("SaveToolbarPosition",Private)!.Invoke(session,null);
        CanvasSettingsWindow? panel=null;
        OverlayContextMenu? opened=null;
        bool rightHeld=false,leftHeld=false;
        try
        {
            foreach(bool withPanel in new[]{false,true})
            {
                if(withPanel)
                {
                    panel=new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly()){Owner=toolbar};
                    typeof(DesktopSession).GetMethod("ConfigureAppWindow",Private)!.Invoke(session,[panel,false,false]);
                    panel.ShowActivated=false;panel.WindowStartupLocation=WindowStartupLocation.Manual;
                    panel.Height=420;panel.Left=toolbar.Left+100;panel.Top=toolbar.Top-panel.Height-12;
                    panel.Show();Pump();
                    check("the first-level canvas panel initially sits above the toolbar",Rank(new WindowInteropHelper(panel).Handle)<Rank(bar));
                }
                foreach(string kind in new[]{"main","more","stamp slot"})
                {
                    FrameworkElement target=kind=="main"?launcher:kind=="more"?moreTrigger:slot;
                    var point=target.PointToScreen(kind=="main"?new Point(60,70):new Point(target.ActualWidth/2,target.ActualHeight/2));
                    SetCursorPos((int)Math.Round(point.X),(int)Math.Round(point.Y));Pump();
                    var hit=GetAncestor(WindowFromPoint(new(){X=(int)Math.Round(point.X),Y=(int)Math.Round(point.Y)}),2);
                    GetWindowRect(bar,out var barRect);
                    NativeRect panelRect=default;if(panel is not null)GetWindowRect(new WindowInteropHelper(panel).Handle,out panelRect);
                    Console.WriteLine($"ROOT_TARGET kind={kind} point={point} hit={hit:X} toolbar={bar:X} barBounds={barRect.Left},{barRect.Top},{barRect.Right},{barRect.Bottom} panelBounds={panelRect.Left},{panelRect.Top},{panelRect.Right},{panelRect.Bottom} visible={target.IsVisible}");
                    check(kind+" physical press targets only its own toolbar",hit==bar);
                    bool right=kind!="more";
                    MouseInput(right?8u:2u);rightHeld=right;leftHeld=!right;Pump(180);
                    if(kind=="main") {opened=main;CheckRoot("held");}
                    MouseInput(right?16u:4u);rightHeld=leftHeld=false;Pump(250);
                    opened=kind=="main"?main:kind=="more"?more:(OverlayContextMenu)slot.ContextMenu;
                    CheckRoot("released");
                    if(kind=="main"&&!withPanel&&Environment.GetEnvironmentVariable("FLOATCANVAS_MENU_LAYER_SCREENSHOT") is {Length:>0} path)
                        Capture((HwndSource)PresentationSource.FromVisual(opened),path);
                    typeof(DesktopSession).GetMethod("RaiseVisibleToolWindows",Private)!.Invoke(session,null);Pump(180);
                    CheckRoot("restacked");
                    opened.IsOpen=false;opened=null;Pump();
                    void CheckRoot(string phase)
                    {
                        check($"{kind} remains open after {phase} (panel={withPanel})",opened?.IsOpen==true&&host.IsEditing);
                        nint popup=((HwndSource)PresentationSource.FromVisual(opened!)).Handle;
                        bool noChild=opened!.Items.OfType<MenuItem>().All(item=>!item.IsSubmenuOpen);
                        GetWindowRect(bar,out var toolbarBounds);
                        Console.WriteLine($"ROOT_LAYER backend={host.UsesSystemInput} kind={kind} panel={withPanel} phase={phase} rootRank={Rank(popup)} toolbarRank={Rank(bar)} noSubmenu={noChild}");
                        check($"{kind} is above its toolbar with no submenu at {phase} (panel={withPanel})",noChild&&Rank(popup)<Rank(bar));
                        check(kind+" keeps the toolbar at the screen bottom during "+phase,Math.Abs(toolbarBounds.Bottom-work.Bottom)<=1);
                        if(panel is not null)check(kind+" root is above the canvas panel at "+phase,Rank(popup)<Rank(new WindowInteropHelper(panel).Handle));
                    }
                }
            }
        }
        finally
        {
            if(rightHeld)MouseInput(16);if(leftHeld)MouseInput(4);
            if(opened is not null)opened.IsOpen=false;main.IsOpen=false;more.IsOpen=false;
            panel?.CloseForDispose();launcher.CancelPendingGestures();SetCursorPos(cursor.X,cursor.Y);Pump();
        }
        GetCursorPos(out var restored);
        check("root menu fixture restores the cursor and releases injected buttons",restored.X==cursor.X&&restored.Y==cursor.Y&&Mouse.LeftButton==MouseButtonState.Released&&Mouse.RightButton==MouseButtonState.Released);
    }
    private static void Capture(HwndSource source,string path)
    {
        GetWindowRect(source.Handle,out var rect);
        using var bitmap=new System.Drawing.Bitmap(rect.Right-rect.Left,rect.Bottom-rect.Top);
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.CopyFromScreen(rect.Left,rect.Top,0,0,bitmap.Size);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);
    }
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
    private static int Rank(nint handle){nint current=GetTopWindow(0);for(int i=0;i<8192&&current!=0;i++,current=GetWindow(current,2))if(current==handle)return i;return int.MaxValue;}
    private static void Pump(int milliseconds=30){var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=(_,_)=>frame.Continue=false;timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}}
    private static void MouseInput(uint flags){var input=new NativeInput {Data=new(){Mouse=new(){Flags=flags}}};if(SendInput(1,[input],Marshal.SizeOf<NativeInput>())!=1)throw new InvalidOperationException("Could not inject into the guarded root-menu fixture.");}
    [StructLayout(LayoutKind.Sequential)]private struct NativePoint{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct NativeInput{public uint Type;public InputData Data;}
    [StructLayout(LayoutKind.Explicit)]private struct InputData{[FieldOffset(0)]public MouseData Mouse;}
    [StructLayout(LayoutKind.Sequential)]private struct MouseData{public int X,Y;public uint Data,Flags,Time;public nuint Extra;}
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]private static extern nint GetAncestor(nint window,uint flags);
    [DllImport("user32.dll")]private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")]private static extern nint GetWindow(nint window,uint relation);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint window,out NativeRect bounds);
    [DllImport("user32.dll")]private static extern uint SendInput(uint count,NativeInput[] input,int size);
}
