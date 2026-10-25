using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;

/// <summary>Opt-in real HWND/WPF integration gate. Uses an isolated profile and an owned receiver;
/// contains no target injection, screenshot renderer, fake foreground or synthetic key routing.</summary>
internal static class SystemOverlayProof
{
    private static readonly JsonSerializerOptions JsonOptions=new() { Converters={new HandleConverter()} };
    private sealed class HandleConverter : JsonConverter<nint>
    {
        public override nint Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>checked((nint)reader.GetInt64());
        public override void Write(Utf8JsonWriter writer,nint value,JsonSerializerOptions options)=>writer.WriteNumberValue(value.ToInt64());
    }
    private const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    private static string Evidence => Environment.GetEnvironmentVariable("FLOATCANVAS_SYSTEM_OVERLAY_EVIDENCE")
        ?? Path.Combine(Path.GetTempPath(),"FloatCanvas-SystemOverlayProof");
    private static void Record(string name,object? data=null)
    {
        Directory.CreateDirectory(Evidence);
        File.AppendAllText(Path.Combine(Evidence,$"proof-{Environment.ProcessId}.jsonl"),JsonSerializer.Serialize(new
        { Utc=DateTimeOffset.UtcNow,Pid=Environment.ProcessId,Event=name,Data=data },JsonOptions)+Environment.NewLine);
    }

    internal static void Receiver()
    {
        var app=new Application { ShutdownMode=ShutdownMode.OnMainWindowClose };
        var text=new TextBlock { Text="FloatCanvas System Input Receiver\n点击这里使本窗口成为前台，然后 Ctrl+Alt+Shift+F8。\n编辑期间本窗口不应收到按键；退出后恢复。",FontSize=24,Margin=new Thickness(30) };
        string label=Environment.GetEnvironmentVariable("FLOATCANVAS_SYSTEM_OVERLAY_RECEIVER_LABEL")??"";
        var window=new Window { Title="FloatCanvas System Input Receiver"+label,Width=1200,Height=700,Content=text,Background=Brushes.DarkSlateGray,Foreground=Brushes.White };
        int keys=0,clicks=0;
        window.Activated+=(_,_)=>Record("receiver-activated");
        window.Deactivated+=(_,_)=>
        {
            nint foreground=GetForegroundWindow();
            uint thread=GetWindowThreadProcessId(foreground,out uint process);
            var name=new System.Text.StringBuilder(128);GetClassNameW(foreground,name,name.Capacity);
            Record("receiver-deactivated",new {Foreground=foreground,Process=process,Thread=thread,Class=name.ToString()});
        };
        window.PreviewKeyDown+=(_,e)=>{++keys;Record("receiver-key",new { Key=e.Key.ToString(),keys,Foreground=GetForegroundWindow() });};
        window.PreviewMouseDown+=(_,_)=>{++clicks;Record("receiver-pointer",new { clicks,Foreground=GetForegroundWindow() });};
        window.Show();
        nint handle=new WindowInteropHelper(window).Handle;
        var statusTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(200) };
        statusTimer.Tick+=(_,_)=>text.Text=$"FloatCanvas System Input Receiver\n前台仍是下层接收器：{GetForegroundWindow()==handle}\n接收到按键：{keys}；点击：{clicks}\nCtrl+Alt+Shift+F8：进入/退出；F9（同修饰键）：结束覆盖测试。";
        statusTimer.Start();
        Record("receiver-ready",new { Handle=handle });
        try { app.Run(); } finally {statusTimer.Stop();}
    }
    internal static int Production(bool measurePerformance=false,bool nativeDialogOnly=false)
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        new UserSettings {Toggle=new(7,measurePerformance?0x79u:0x77u),Hide=new(7,measurePerformance?0x7Au:0x76u),Theme="light",Language="zh-CN",ShowFloatingLogo=false}.Save();
        using var host=new OverlayHost(OverlayMonitorScope.PrimaryScreen);
        host.EnableSystemInput();
        using var session=new DesktopSession(app,host);
        foreach(var diagnostic in host.Diagnostics)
        {
            long style=GetWindowLongPtrW(diagnostic.InputHandle,-20).ToInt64();
            SetWindowLongPtrW(diagnostic.InputHandle,-20,(nint)((style&~0x80L)|0x40000L));
            SetWindowTextW(diagnostic.InputHandle,"FloatCanvas Canvas Integration Test");
        }
        session.Start(["--startup","--profile",ApplicationPaths.ProfileDirectory]);
        var toolbar=(Window)typeof(DesktopSession).GetField("_toolbar",Private)!.GetValue(session)!;
        if(Environment.GetCommandLineArgs().Contains("--system-overlay-document-dialog-interaction"))
        {
            string folder=Path.Combine(Evidence,"dialog-files");Directory.CreateDirectory(folder);
            var document=new FloatCanvas.Core.SceneDocument();
            document.Elements.Add(new(){Kind=FloatCanvas.Core.ElementKind.Rectangle,Width=123,Height=45});
            new FloatCanvas.Storage.ProjectStore().SaveAsync(Path.Combine(folder,"owned-canvas.floatcanvas"),document,new FloatCanvas.Core.AssetLibrary()).GetAwaiter().GetResult();
        }
        if(Environment.GetCommandLineArgs().Contains("--system-overlay-document-dialog"))
        {
            void State(string stage)=>Record("document-command-"+stage,new {toolbar.IsEnabled,host.IsEditing,host.HasEditingInput,host.EditingSessionVersion,Foreground=GetForegroundWindow(),Focus=GetFocus()});
            toolbar.IsEnabledChanged+=(_,_)=>State("toolbar-enabled");
            host.EditingFocusLost+=()=>State("focus-lost");
        }
        using var performance=measurePerformance?new SystemOverlayPerformanceRecorder(host,toolbar,Evidence,()=>host.HasEditingInput):null;
        using var performanceRun=performance is not null
            ?new SystemOverlayPerformanceRun(app,host,toolbar,performance,Evidence,nativeDialogOnly,
                ()=>typeof(DesktopSession).GetMethod("ToggleEditing",Private)!.Invoke(session,null),
                async save=>
                {
                    await (Task)typeof(DesktopSession).GetMethod(save?"SaveProjectAsync":"OpenProjectAsync",Private)!
                        .Invoke(session,save?[true]:null)!;
                    var active=typeof(DesktopSession).GetProperty("Active",Private)!.GetValue(session)!;
                    var state=typeof(DesktopSession).GetMethod("FileState",Private)!.Invoke(session,[active])!;
                    return (string?)state.GetType().GetField("Path",Private|BindingFlags.Public)!.GetValue(state);
                }):null;
        if(measurePerformance)
        {
            // Expose the real owned toolbar for guarded automation; retain its band and content.
            toolbar.Title="FloatCanvas Performance Toolbar";
            nint handle=new WindowInteropHelper(toolbar).Handle;
            SetWindowLongPtrW(handle,-20,(nint)((GetWindowLongPtrW(handle,-20).ToInt64()&~0x80L)|0x40000L));
        }
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(500)};
        string? previous=null;
        timer.Tick+=(_,_)=>
        {
            if(measurePerformance)return; // Avoid periodic log I/O during timing samples.
            var active=(FloatCanvas.Core.EditorSession)typeof(DesktopSession).GetProperty("Active",Private)!.GetValue(session)!;
            string snapshot=JsonSerializer.Serialize(new {host.IsEditing,host.HasEditingInput,host.IsKeyboardIsolationActive,
                host.PresentedFrameCount,host.LastRenderMilliseconds,Elements=active.Document.Elements.Count,
                Foreground=GetForegroundWindow(),Focus=GetFocus(),Tool=((FloatCanvas.App.EditorController)typeof(DesktopSession).GetField("_editor",Private)!.GetValue(session)!).Tool.ToString()},JsonOptions);
            if(snapshot!=previous){Record("production-state",JsonDocument.Parse(snapshot).RootElement);previous=snapshot;}
        };
        ThreadMessageEventHandler shutdown=(ref MSG msg,ref bool handled)=>
        {
            if(msg.message!=0x312)return;
            if(msg.wParam==0x4644){handled=true;app.Shutdown();}
            if(performance is not null && msg.wParam is >=0x4650 and <=0x4652)
            {
                handled=true;
                if(msg.wParam==0x4652)performance.Save();
                else Console.WriteLine($"PERFORMANCE_PHASE {(msg.wParam==0x4650?"canvas":"toolbar")} started={performance.Begin(msg.wParam==0x4650?"canvas":"toolbar")}");
            }
            if(msg.wParam==0x4646)
            {handled=true;((OverlayContextMenu)typeof(DesktopSession).GetField("_toolbarMenu",Private)!.GetValue(session)!).IsOpen=true;Record("production-menu-opened");}
            if(msg.wParam==0x4647)
            {
                handled=true;
                var toolbar=(Window)typeof(DesktopSession).GetField("_toolbar",Private)!.GetValue(session)!;
                var dialog=AppDialog.Create(toolbar,"真实模态窗口承载验证","FloatCanvas Modal Gate",MessageBoxButton.OK);
                nint foreground=GetForegroundWindow();
                var closer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(500)};
                closer.Tick+=(_,_)=>
                {
                    closer.Stop();nint hwnd=new WindowInteropHelper(dialog.Window).Handle;
                    Record("production-modal-state",new {ExpectedForeground=foreground,Foreground=GetForegroundWindow(),host.HasEditingInput,
                        Band=GetWindowBand(hwnd,out uint band)?band:uint.MaxValue,Modal=ComponentDispatcher.IsThreadModal});
                    SessionWindowCoordinator.SetModalResult(dialog.Window,false);
                };
                closer.Start();try{SessionWindowCoordinator.ShowModal(dialog.Window);}finally{closer.Stop();}
            }
            if(msg.wParam==0x4645)
            {
                handled=true;
                var diagnostic=host.Diagnostics[0];
                int x=diagnostic.Bounds.Width/5,y=diagnostic.Bounds.Height/3;
                nint Position(int px,int py)=>(nint)((uint)(ushort)px|((uint)(ushort)py<<16));
                SendMessageW(diagnostic.InputHandle,0x201,1,Position(x,y));
                for(int step=1;step<=15;step++)SendMessageW(diagnostic.InputHandle,0x200,1,Position(x+step*15,y+step*8));
                SendMessageW(diagnostic.InputHandle,0x202,0,Position(x+225,y+120));
                Record("production-private-message-stroke",new {host.IsEditing,host.HasEditingInput,Foreground=GetForegroundWindow()});
            }
        };
        ComponentDispatcher.ThreadPreprocessMessage+=shutdown;
        if(!RegisterHotKey(host.ControlHandle,0x4644,0x4007,measurePerformance?0x7Bu:0x78u))throw new System.ComponentModel.Win32Exception();
        if(!RegisterHotKey(host.ControlHandle,0x4645,0x4007,0x75))throw new System.ComponentModel.Win32Exception();
        if(!RegisterHotKey(host.ControlHandle,0x4646,0x4007,0x74))throw new System.ComponentModel.Win32Exception();
        if(!RegisterHotKey(host.ControlHandle,0x4647,0x4007,0x73))throw new System.ComponentModel.Win32Exception();
        if(performance is not null)
            foreach(var binding in new[]{(Id:0x4650,Key:0x72u),(Id:0x4651,Key:0x71u),(Id:0x4652,Key:0x70u)})
                if(!RegisterHotKey(host.ControlHandle,binding.Id,0x4007,binding.Key))throw new System.ComponentModel.Win32Exception();
        Record("production-ready",new {host.UsesSystemInput,host.SystemWindowOwner,Windows=host.Diagnostics,Profile=ApplicationPaths.ProfileDirectory});timer.Start();
        try{return app.Run();}
        finally{timer.Stop();UnregisterHotKey(host.ControlHandle,0x4644);UnregisterHotKey(host.ControlHandle,0x4645);UnregisterHotKey(host.ControlHandle,0x4646);UnregisterHotKey(host.ControlHandle,0x4647);foreach(int id in new[]{0x4650,0x4651,0x4652})UnregisterHotKey(host.ControlHandle,id);ComponentDispatcher.ThreadPreprocessMessage-=shutdown;Record("production-stopped");}
    }
    private static uint Band(Visual? visual)
    {
        if(visual is null||PresentationSource.FromVisual(visual) is not HwndSource source) return uint.MaxValue;
        return GetWindowBand(source.Handle,out uint band)?band:uint.MaxValue;
    }
    internal static void AuxiliaryGate()
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        using var host=HiddenHost();
        using var session=new DesktopSession(app,host);
        string[] commands=["OpenToolOptions","ToggleInspector","ToggleLayers"];
        int stage=-2;Exception? failure=null;nint foreground=0;
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(750)};
        var deadline=Stopwatch.StartNew();
        ThreadMessageEventHandler begin=(ref MSG msg,ref bool handled)=>
        {
            if(msg.message!=0x312 || msg.wParam!=0x4648)return;
            handled=true;
            if(stage!=-2)return;
            foreground=GetForegroundWindow();GetWindowThreadProcessId(foreground,out uint pid);
            string targetName=Process.GetProcessById((int)pid).ProcessName;
            if(pid==Environment.ProcessId || (targetName!="FloatCanvas.AppTests" && targetName!="FullscreenProbe"))
            {failure=new InvalidOperationException("Activate an owned receiver/probe before triggering the gate.");app.Shutdown();return;}
            deadline.Restart();stage=-1;Record("aux-begin",new {Foreground=foreground});
        };
        ComponentDispatcher.ThreadPreprocessMessage+=begin;
        if(!RegisterHotKey(host.ControlHandle,0x4648,0x4007,0x79))throw new System.ComponentModel.Win32Exception();
        Console.WriteLine("AUXILIARY_READY Ctrl+Alt+Shift+F10 on owned receiver/probe");
        timer.Tick+=(_,_)=>
        {
            try
            {
                if(stage==-2){if(deadline.ElapsedMilliseconds>45000)throw new TimeoutException("Auxiliary gate was not triggered");return;}
                if(stage==-1)
                {
                    if(!(bool)typeof(DesktopSession).GetMethod("SetEditing",Private)!.Invoke(session,[true,false])!)throw new InvalidOperationException("Editing entry failed");
                    stage=0;return;
                }
                if(!host.HasEditingInput)
                {
                    if(deadline.ElapsedMilliseconds<2500 && stage==0)return;
                    throw new InvalidOperationException("Delegation was lost while opening auxiliary window "+stage);
                }
                if(GetForegroundWindow()!=foreground)throw new InvalidOperationException("Auxiliary window changed global foreground at stage "+stage);
                if(stage==commands.Length){app.Shutdown();return;}
                string command=commands[stage++];
                Record("aux-before",new {command,Foreground=GetForegroundWindow()});
                typeof(DesktopSession).GetMethod(command,Private)!.Invoke(session,null);
                Record("aux-after",new {command,Foreground=GetForegroundWindow(),host.HasEditingInput});
                Console.WriteLine($"AUXILIARY {command} foregroundPreserved={GetForegroundWindow()==foreground} inputReady={host.HasEditingInput}");
                if(GetForegroundWindow()!=foreground)throw new InvalidOperationException(command+" changed global foreground");
            }
            catch(Exception error){failure=error;app.Shutdown();}
        };
        timer.Start();try{app.Run();}finally{timer.Stop();UnregisterHotKey(host.ControlHandle,0x4648);ComponentDispatcher.ThreadPreprocessMessage-=begin;}
        if(failure is not null)throw new InvalidOperationException("Production auxiliary-window gate failed",failure);
    }
    internal static void ConfinementObservation()
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        using var host=HiddenHost();
        using var session=new DesktopSession(app,host);
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
        var clock=Stopwatch.StartNew();
        Exception? failure=null;bool entered=false;long began=0;
        timer.Tick+=(_,_)=>
        {
            try
            {
                if(!entered)
                {
                    nint target=GetForegroundWindow();GetWindowThreadProcessId(target,out uint pid);
                    if(pid==Environment.ProcessId || Process.GetProcessById((int)pid).MainWindowTitle!="FloatCanvas Relative Input Receiver")
                    {if(clock.ElapsedMilliseconds>30000)throw new TimeoutException("Activate the owned relative-input receiver");return;}
                    GetClipCursor(out var before);
                    if(before.Right-before.Left!=1 || before.Bottom-before.Top!=1)return;
                    entered=true;began=clock.ElapsedMilliseconds;host.SetVisible(true);
                    if(!(bool)typeof(DesktopSession).GetMethod("SetEditing",Private)!.Invoke(session,[true,false])!)throw new InvalidOperationException("Editing entry failed");
                    return;
                }
                if(!host.HasEditingInput)
                {if(clock.ElapsedMilliseconds-began>3000)throw new InvalidOperationException("No input acknowledgment");return;}
                if(clock.ElapsedMilliseconds-began<1000)return;
                GetClipCursor(out var clip);
                bool locked=clip.Right-clip.Left<=1 || clip.Bottom-clip.Top<=1;
                // Game Bar can expose a freely moving pointer while the target's
                // successful ClipCursor/SetCursorPos calls still report 1px here.
                // This is an observation, never a mouse-takeover pass/fail gate.
                Console.WriteLine($"CONFINEMENT_OBSERVATION inputOwned={host.HasEditingInput} clip={clip.Left},{clip.Top},{clip.Right},{clip.Bottom} remainsConfined={locked} pointerTakeover=not-measured");
                app.Shutdown();
            }
            catch(Exception error){failure=error;app.Shutdown();}
        };
        timer.Start();Console.WriteLine("CONFINEMENT_READY activate owned Relative Input Receiver; automatic enter/observe/exit; no pointer acceptance verdict");
        try{app.Run();}finally{timer.Stop();host.ExitEditing();}
        if(failure is not null)throw failure;
    }
    internal static void PopupGate()
    {
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var text=new TextBox { Text="popup gate",Width=200,Height=40 };
        var root=new StackPanel { Background=Brushes.White }; root.Children.Add(text);
        using var host=HiddenHost();
        using var windows=new OverlayWindowScope(host.SystemWindowOwner);
        AppTheme.Apply("light");
        var owner=new Window {Content=root,Left=-20000,Top=-20000,Width=500,Height=180};
        OverlayWindowScope.Prepare(owner);owner.Show();Pump();
        var menu=new OverlayContextMenu { PlacementTarget=text,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem {Header="真实弹窗层级"});
        var group=new MenuItem {Header="真实子菜单"};
        var command=new MenuItem {Header="子命令"}; group.Items.Add(command);menu.Items.Add(group);
        try
        {
            menu.IsOpen=true; Pump();
            nint handle=(PresentationSource.FromVisual(menu) as HwndSource)?.Handle??0;
            uint actual=Band(menu);
            if(!menu.IsOpen) throw new InvalidOperationException("Hosting closed the real WPF menu");
            Record("popup-band-gate",new { Handle=handle,Band=actual,Owner=GetWindow(handle,4),Foreground=GetForegroundWindow() });
            Console.WriteLine($"POPUP_BAND expected=16 actual={actual} hwnd={handle} owner={GetWindow(handle,4)}");
            if(actual!=16) throw new InvalidOperationException("WPF popup is below the system-band host");
            group.IsSubmenuOpen=true;Pump();
            uint submenuBand=Band(command);
            Console.WriteLine($"SUBMENU_BAND expected=16 actual={submenuBand} open={group.IsSubmenuOpen}");
            if(!group.IsSubmenuOpen || submenuBand!=16) throw new InvalidOperationException("Submenu hosting failed");
            group.IsSubmenuOpen=false;
        }
        finally { menu.IsOpen=false;Pump(); }
        var combo=new ComboBox {ItemsSource=new[]{"第一项","第二项"},SelectedIndex=0};
        var comboWindow=new Window {Content=combo,Width=300,Height=90,Left=-20000,Top=-20000};
        OverlayWindowScope.Prepare(comboWindow);comboWindow.Show();Pump();
        try
        {
            combo.IsDropDownOpen=true;Pump();
            var item=(ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(1);
            uint comboBand=Band(item);
            Console.WriteLine($"COMBO_BAND expected=16 actual={comboBand} open={combo.IsDropDownOpen}");
            if(!combo.IsDropDownOpen || comboBand!=16) throw new InvalidOperationException("ComboBox hosting failed");
        }
        finally {combo.IsDropDownOpen=false;comboWindow.Close();Pump();}
        var color=new ColorField(ColorRole.Foreground);
        var pickerWindow=new Window {Content=color,Width=420,Height=100,Left=-20000,Top=-20000};
        OverlayWindowScope.Prepare(pickerWindow);pickerWindow.Show();Pump();
        try
        {
            color.OpenPicker();Pump();
            var display=(PopupHandle)typeof(ColorField).GetField("popupDisplay",Private)!.GetValue(color)!;
            uint pickerBand=Band(display.Child as Visual);
            Console.WriteLine($"PICKER_BAND expected=16 actual={pickerBand} open={display.IsOpen}");
            if(!display.IsOpen || pickerBand!=16)throw new InvalidOperationException("Color picker hosting failed");
            using(PopupChildDialog.RetainFor(display.Child!))
                if(!display.StaysOpen)throw new InvalidOperationException("Picker child-dialog retention failed");
            if(display.StaysOpen)throw new InvalidOperationException("Picker retention did not restore outside-close behavior");
            color.SetUserValue(0xFF123456);Pump();
            if(color.Value!=0xFF123456 || display.IsOpen)throw new InvalidOperationException("Picker commit/close failed");
            AppWindowChrome.ApplyDialog(pickerWindow);
            AppWindowChrome.SetHelp(pickerWindow,"承载测试","帮助内容保持");pickerWindow.ApplyTemplate();Pump();
            var help=PopupHandle.For((System.Windows.Controls.Primitives.Popup)pickerWindow.Template.FindName("HelpPopup",pickerWindow));
            help.IsOpen=true;Pump();uint helpBand=Band(help.Child as Visual);
            Console.WriteLine($"HELP_BAND expected=16 actual={helpBand} open={help.IsOpen}");
            if(!help.IsOpen || helpBand!=16 || !AppWindowChrome.TryCloseHelp(pickerWindow))throw new InvalidOperationException("Help popup hosting failed");
        }
        finally{color.ClosePicker();pickerWindow.Close();Pump();}
        var create=new NewCanvasButton(()=>true,()=>Task.CompletedTask);
        root.Children.Add(create);owner.UpdateLayout();Pump();
        create.ActivateAsync().GetAwaiter().GetResult();Pump();
        var confirmation=typeof(NewCanvasButton).GetField("confirmationDisplay",Private)?.GetValue(create) as PopupHandle;
        uint confirmationBand=Band((confirmation?.Child??create.ConfirmationPopup.Child) as Visual);
        bool confirmationOpen=confirmation?.IsOpen??create.ConfirmationPopup.IsOpen;
        Console.WriteLine($"CONFIRMATION_BAND expected=16 actual={confirmationBand} open={confirmationOpen}");
        try
        {
            if(!create.IsConfirming || !confirmationOpen || confirmationBand!=16)throw new InvalidOperationException("New-canvas confirmation is below the toolbar");
            nint bubbleHandle=((HwndSource)PresentationSource.FromVisual((Visual)confirmation!.Child!)).Handle;
            if(SendMessageW(bubbleHandle,0x84,0,0)!=-1)throw new InvalidOperationException("Confirmation bubble captures pointer input");
            var bubbleSource=(HwndSource)PresentationSource.FromVisual((Visual)confirmation.Child!);
            GetClientRect(bubbleHandle,out var client);
            var desired=bubbleSource.CompositionTarget.TransformToDevice.Transform((Vector)confirmation.Child!.DesiredSize);
            Console.WriteLine($"CONFIRMATION_SIZE client={client.Right}x{client.Bottom} required={Math.Ceiling(desired.X)}x{Math.Ceiling(desired.Y)}");
            if(client.Right<Math.Ceiling(desired.X) || client.Bottom<Math.Ceiling(desired.Y))
                throw new InvalidOperationException("Popup client clips the confirmation's measured content and margins");
            var matrix=bubbleSource.CompositionTarget.TransformToDevice;
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(client.Right,client.Bottom,96*matrix.M11,96*matrix.M22,PixelFormats.Pbgra32);
            bitmap.Render((Visual)confirmation.Child);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Evidence);
            using(var output=File.Create(Path.Combine(Evidence,"confirmation-popup.png")))encoder.Save(output);
            if(InputMethod.GetIsInputMethodEnabled(owner) || !InputMethod.GetIsInputMethodEnabled(text))throw new InvalidOperationException("IME is not limited to actual text editors");
        }
        finally{create.CancelConfirmation();}
        owner.Close();
    }
    private static OverlayHost HiddenHost()
    {
        var host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),Private,null,
            [OverlayMonitorScope.PrimaryScreen,false],null)!;
        try{host.EnableSystemInput();return host;}catch{host.Dispose();throw;}
    }
    internal static void ImeFocusGate()
    {
        _=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        using var host=HiddenHost();
        var focusErrors=new List<Exception>();
        host.Error+=error=>{focusErrors.Add(error);Console.WriteLine($"IME_NATIVE_ERROR {error}");};
        using var scope=new OverlayWindowScope(host.SystemWindowOwner);
        AppTheme.Apply("light");
        var input=new TextBox {Text="text focus lifecycle"};
        var window=new Window {Content=input,Left=-20000,Top=-20000,Width=400,Height=120};
        OverlayWindowScope.Prepare(window);window.Show();Pump();
        nint hwnd=new WindowInteropHelper(window).Handle;
        try
        {
            nint canvas=host.Diagnostics[0].InputHandle;
            nint drawingDocument=0;
            for(int cycle=0;cycle<12;cycle++)
            {
                SetActiveWindow(hwnd);SetFocus(hwnd);input.Focus();Keyboard.Focus(input);Pump();
                nint textDocument=TextServicesProbe.FocusedDocument();
                Console.WriteLine($"IME_TEXT_SETUP cycle={cycle} focused={input.IsKeyboardFocused} textDocument={textDocument:X}");
                if(!input.IsKeyboardFocused || textDocument==0 || textDocument==drawingDocument)throw new InvalidOperationException("Real WPF text-service setup failed");
                if(cycle%2==0)Keyboard.ClearFocus(); // Both explicit clear and normal HWND focus loss.
                SetFocus(canvas);
                Pump();
                nint imc=ImmGetContext(canvas);if(imc!=0)ImmReleaseContext(canvas,imc);
                nint currentDocument=TextServicesProbe.FocusedDocument();
                bool leaked=currentDocument==textDocument;
                Console.WriteLine($"IME_CANVAS_FOCUS cycle={cycle} nativeFocus={GetFocus()==canvas} immDisabled={imc==0} retainsTextDocument={leaked} expected=false");
                if(GetFocus()!=canvas || imc!=0 || leaked)throw new InvalidOperationException("Native drawing focus retains the WPF text input context");
                if(drawingDocument!=0 && currentDocument!=drawingDocument)throw new InvalidOperationException("Canvas TSF document is not reused");
                drawingDocument=currentDocument;
                if(focusErrors.Count!=0)throw new AggregateException(focusErrors);
            }
            SetActiveWindow(hwnd);SetFocus(hwnd);input.Focus();Keyboard.Focus(input);Pump();
            window.Content=null;window.Close();SetFocus(canvas);Pump();
            if(GetFocus()!=canvas || TextServicesProbe.FocusedDocument()!=drawingDocument)throw new InvalidOperationException("Closing a text editor retains its TSF context on the canvas");
            Console.WriteLine("IME_EDITOR_CLOSED nativeFocus=true canvasDocumentRestored=true");
        }
        finally{if(window.IsVisible)window.Close();}
    }
    internal static void OptionsFocusGate()
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        using var host=HiddenHost();
        using var scope=new OverlayWindowScope(host.SystemWindowOwner);
        AppTheme.Apply("light");
        var editor=new EditorController(new FloatCanvas.Core.EditorSession()){Tool=EditorTool.Freehand};
        var panel=new ToolOptionsPanel(editor,new(Path.Combine(ApplicationPaths.ProfileDirectory,"presets.json")),new(Path.Combine(ApplicationPaths.ProfileDirectory,"colors.json")));
        var fields=(Dictionary<string,NumericValueField>)typeof(ToolOptionsPanel).GetField("numericFields",Private)!.GetValue(panel)!;
        var blank=new Border{Child=panel};
        var window=new Window{Content=blank,Width=600,Height=750};
        AppWindowChrome.Apply(window);
        var input=fields["StrokeWidth"].Input;
        panel.Refresh();window.Left=-20000;window.Top=-20000;window.Show();Pump();
        nint hwnd=new WindowInteropHelper(window).Handle;
        SetActiveWindow(hwnd);SetFocus(hwnd);input.Focus();Keyboard.Focus(input);Pump();
        if(!input.IsKeyboardFocused)throw new InvalidOperationException("The real brush width editor could not be focused");
        input.Text="12";
        Pump();input.Focus();Keyboard.Focus(input);Pump();
        if(!input.IsKeyboardFocused)throw new InvalidOperationException("The numeric edit did not retain its active editor");
        bool numericIme=InputMethod.GetIsInputMethodEnabled(input);
        if(InputMethod.GetIsInputMethodEnabled(fields["StrokeWidth"].Slider))throw new InvalidOperationException("A non-text numeric slider enables IME");
        var viewer=(ScrollViewer)panel.Content;
        viewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
            {RoutedEvent=Mouse.PreviewMouseDownEvent,Source=viewer});
        viewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
            {RoutedEvent=Mouse.MouseDownEvent,Source=viewer});
        Pump();
        bool retained=input.IsKeyboardFocused;
        bool routes=ToolOptionsKeyboardRoute.ShouldRoute(0x42,ModifierKeys.None,Keyboard.FocusedElement as DependencyObject);
        bool focusedIme=Keyboard.FocusedElement is DependencyObject focus && InputMethod.GetIsInputMethodEnabled(focus);
        bool finalViewer=ReferenceEquals(Keyboard.FocusedElement,viewer);
        Console.WriteLine($"OPTIONS_BLANK numericIme={numericIme} retainsTextFocus={retained} routesBrush={routes} focusedIme={focusedIme} finalViewer={finalViewer}");
        var text=new TextBox{Text="Chinese text remains enabled"};
        viewer.Content=text;Pump();text.Focus();Keyboard.Focus(text);Pump();
        bool textEnabled=InputMethod.GetIsInputMethodEnabled(text);
        nint textDocument=TextServicesProbe.FocusedDocument();
        viewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
            {RoutedEvent=Mouse.PreviewMouseDownEvent,Source=viewer});
        viewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
            {RoutedEvent=Mouse.MouseDownEvent,Source=viewer});Pump();
        bool staleDocument=TextServicesProbe.FocusedDocument()==textDocument;
        Console.WriteLine($"OPTIONS_TEXT textEnabled={textEnabled} textFocused={text.IsKeyboardFocused} retainsTextDocument={staleDocument}");
        window.Close();
        if(numericIme || retained || !routes || focusedIme || !finalViewer || !textEnabled || staleDocument)throw new InvalidOperationException("Tool-options blank press retains a text/IME shortcut target");
    }
    internal static void WindowContract()
    {
        _=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        using var host=HiddenHost();
        using var session=new DesktopSession(Application.Current,host);
        var toolbar=(Window)typeof(DesktopSession).GetField("_toolbar",Private)!.GetValue(session)!;
        nint handle=new WindowInteropHelper(toolbar).EnsureHandle();
        uint toolbarBand=GetWindowBand(handle,out uint band)?band:uint.MaxValue;
        bool hidden=!toolbar.IsVisible && host.Diagnostics.All(pair=>!pair.InputVisible);
        Console.WriteLine($"WINDOW_CONTRACT band={toolbarBand} editing={host.IsEditing} inputReady={host.HasEditingInput} hidden={hidden} legacyHook={host.IsKeyboardIsolationEnabled}");
        if(toolbarBand!=16 || host.IsEditing || host.HasEditingInput || !hidden || host.IsKeyboardIsolationEnabled)
            throw new InvalidOperationException("Production window/input creation contract failed");
        foreach(var input in host.Diagnostics)
        {
            nint context=ImmGetContext(input.InputHandle);
            if(context!=0)ImmReleaseContext(input.InputHandle,context);
            Console.WriteLine($"CANVAS_IME contextDisabled={context==0} expected=true");
            if(context!=0)throw new InvalidOperationException("Drawing canvas still accepts IME composition instead of tool shortcuts");
        }
    }
    private static void Pump()
    {
        var frame=new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);
        Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("imm32.dll")] private static extern nint ImmGetContext(nint window);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(nint window,nint context);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool SetWindowTextW(nint hwnd,string text);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd,uint message,nuint wparam,nint lparam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassNameW(nint window,System.Text.StringBuilder name,int count);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd,uint flags);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(nint window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window,int id);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window,out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window,int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window,int index,nint value);
    [DllImport("user32.dll")] private static extern bool GetWindowBand(nint window,out uint band);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window,uint command);
}
