using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class Preview4ColorRuntimeFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run this fixture alone.");
        string profile = Path.Combine(Path.GetTempPath(),"FloatCanvas-Preview4ColorRuntime",Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile",profile,"--primary-screen"]);
        var app = new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        nint foreground = GetForegroundWindow(); DesktopSession? session = null; OverlayHost? host = null;
        try
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null)!).First(m=>m.IsPrimary);
            var bounds = new PixelBounds(primary.WorkArea.Left+24,primary.WorkArea.Top+24,640,360);
            Func<IReadOnlyList<MonitorInfo>> provider=()=>[primary with {Bounds=bounds,WorkArea=bounds}];
            host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),Private,null,[OverlayMonitorScope.PrimaryScreen,provider,false],null)!;
            session=new DesktopSession(app,host);
            Get<System.Windows.Forms.NotifyIcon>(session,"_tray").Visible=false;
            var toolbar=Get<Window>(session,"_toolbar");toolbar.Show();Pump();
            host.SetVisible(true); Activate(new WindowInteropHelper(toolbar).Handle,()=>Invoke("SetEditing",true,false));Pump();
            Invoke("OpenToolOptions"); Pump();
            var options=Get<Window>(session,"_toolOptionsWindow");
            var panel=Get<ToolOptionsPanel>(session,"_toolOptionsPanel");
            var field=Get<ColorField>(panel,"foreground"); var picker=Get<ColorPickerPanel>(field,"picker");
            field.OpenPicker(true);Pump();
            Console.WriteLine($"BEFORE MODAL picker={field.IsPickerOpen} options={options.IsVisible} editing={host.IsEditing} focused={System.Windows.Input.Keyboard.FocusedElement}");
            bool retained=false, modalOwned=false;
            var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(120)};
            timer.Tick+=(_,_)=>
            {
                var dialog=app.Windows.Cast<Window>().FirstOrDefault(w=>w.Title=="Color modal regression");
                if(dialog is null)return;
                timer.Stop(); retained=field.IsPickerOpen;
                modalOwned=ReferenceEquals(dialog.Owner,options)&&!IsWindowEnabled(new WindowInteropHelper(options).Handle);
                Console.WriteLine($"IN MODAL retained={retained} owned={modalOwned} editing={host.IsEditing} options={options.IsVisible}");
                SessionWindowCoordinator.SetModalResult(dialog,false);
            };
            timer.Start();
            NamePrompt.Show(picker,"Color modal regression","Test",owner:options);Pump();
            Console.WriteLine($"AFTER MODAL retained={retained} owned={modalOwned} open={field.IsPickerOpen} editing={host.IsEditing}");
            check("native name dialog retains the originating color popup and correct modal ownership",retained&&modalOwned&&field.IsPickerOpen&&host.IsEditing);
            field.ClosePicker();
            typeof(DesktopSession).GetField("_toolOptionsPinned",Private)!.SetValue(session,true);
            Invoke("ToggleLayers");Pump();
            var layers=Get<Window>(session,"_layers");
            Invoke("BeginEyedropper",field);Pump();
            check("eyedropper hides the previously visible panels while retaining the toolbar",!options.IsVisible&&!layers.IsVisible&&toolbar.IsVisible&&host.IsEditing);
            check("eyedropper uses a dedicated pointed cursor",Get<nint>(host,"_pointerCursor")==EyedropperCursor.Handle&&EyedropperCursor.Handle!=0);
            Invoke("HandleCanvasPointer",new OverlayPointerEvent(OverlayPointerKind.Move,new(bounds.Left+40,bounds.Top+80),OverlayMouseButton.None,OverlayModifiers.None));
            check("eyedropper consumes canvas movement without starting a drawing transaction",!Get<EditorController>(session,"_editor").Session.IsTransactionActive);
            Invoke("OnKey",0x1B);Pump();
            Console.WriteLine($"RESTORE options={options.IsVisible} layers={layers.IsVisible} picker={field.IsPickerOpen} editing={host.IsEditing} target={Get<ColorField?>(session,"_eyedropper") is not null} busy={Get<bool>(session,"_documentBusyValue")}");
            check("Escape restores both panels and reopens the originating secondary picker",options.IsVisible&&layers.IsVisible&&field.IsPickerOpen&&host.IsEditing);
            check("finishing the eyedropper restores the canvas cursor",Get<nint>(host,"_pointerCursor")==0);
            field.ClosePicker();
            typeof(DesktopSession).GetField("_toolOptionsPinned",Private)!.SetValue(session,false);
            Invoke("BeginEyedropper",field);Pump();
            var colorTarget=new Window {Width=100,Height=80,Left=200,Top=180,WindowStyle=WindowStyle.None,
                Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x23,0x9B,0x65)),
                ShowActivated=false,Topmost=true,ShowInTaskbar=false};
            colorTarget.Show();Pump();DwmFlush();
            var pixel=colorTarget.PointToScreen(new(50,40));
            var pointer=new OverlayPoint(pixel.X,pixel.Y);
            var dispatch=Get<Action<OverlayPointerEvent>>(host,"Pointer");
            dispatch(new OverlayPointerEvent(OverlayPointerKind.Down,pointer,OverlayMouseButton.Left,OverlayModifiers.None));
            check("eyedropper waits for the original canvas press to finish before restoring panels",!options.IsVisible&&!field.IsPickerOpen);
            dispatch(new OverlayPointerEvent(OverlayPointerKind.Up,pointer,OverlayMouseButton.Left,OverlayModifiers.None));Pump();
            colorTarget.Close();
            Console.WriteLine($"SAMPLED color={field.Value:X8} options={options.IsVisible} picker={field.IsPickerOpen} editing={host.IsEditing}");
            check("actual displayed pixel sampling applies the screen color and restores the secondary picker",field.Value==0xFF239B65&&options.IsVisible&&field.IsPickerOpen&&host.IsEditing);
            check("the restored unpinned color popup receives keyboard focus",picker.IsKeyboardFocusWithin);
            field.ClosePicker();
            return;

            object? Invoke(string name,params object[] arguments)=>typeof(DesktopSession).GetMethod(name,Private)!.Invoke(session,arguments);
        }
        finally
        {
            host?.ExitEditing();session?.Dispose();
            foreach(var window in app.Windows.Cast<Window>().ToArray())window.Close();Pump();
            if(foreground!=0)SetForegroundWindow(foreground);
        }
    }
    private static T Get<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    private static void Activate(nint window,Action action)
    {
        uint from=GetCurrentThreadId(),to=GetWindowThreadProcessId(GetForegroundWindow(),out _);bool attached=from!=to&&AttachThreadInput(from,to,true);
        try{SetForegroundWindow(window);SetFocus(window);action();}finally{if(attached)AttachThreadInput(from,to,false);}
    }
    [DllImport("user32.dll")]private static extern bool AttachThreadInput(uint from,uint to,bool attach);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]private static extern bool IsWindowEnabled(nint window);
    [DllImport("dwmapi.dll")]private static extern int DwmFlush();
}

