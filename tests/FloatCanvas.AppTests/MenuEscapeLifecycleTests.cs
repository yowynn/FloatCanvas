using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class MenuEscapeLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run menu Escape regression in a fresh process.");
        bool enhanced = Environment.GetEnvironmentVariable("FLOATCANVAS_MENU_TEST_BACKEND") == "enhanced";
        string transport=Environment.GetEnvironmentVariable("FLOATCANVAS_MENU_ESCAPE_TRANSPORT")??"bridge";
        bool deferredTransport=transport=="deferred",cancelledTransport=transport=="cancelled";
        bool queuedTransport=transport is "queue" or "cancelled";
        bool hwndTransport=deferredTransport||queuedTransport||transport=="hwnd";
        bool reportBoundaryTests=deferredTransport||cancelledTransport;
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-MenuEscapeRegression", enhanced ? "enhanced" : "ordinary");
        Directory.CreateDirectory(profile);
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        new UserSettings { Language = "en", Theme = "light", ShowFloatingLogo = false }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previousContext = SynchronizationContext.Current;
        var originalKeyboardState = new byte[256];
        if (!GetKeyboardState(originalKeyboardState)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        OverlayHost? host = null; DesktopSession? session = null;
        bool firstEscExited = false;
        int nativeDispatchDepth=0,deferredReports=0,deferredReplays=0,wpfNativeDown=0,wpfNativeUp=0;
        bool allReplaysAfterReturn=true;
        bool cancelCurrentDown=false;int cancelledReports=0;
        var deferredInputs=new HashSet<InputEventArgs>();
        var deferredKeys=new List<(bool Up,bool Matched,int Message,nint Flags)>();
        PreProcessInputEventHandler deferRawKeyboard=(_,args)=>
        {
            if(!reportBoundaryTests||deferredInputs.Contains(args.StagingItem.Input))return;
            var input=args.StagingItem.Input;
            if(input.RoutedEvent?.Name!="PreviewInputReport"||input.GetType().Name!="InputReportEventArgs")return;
            var report=input.GetType().GetProperty("Report",Private|BindingFlags.Public)?.GetValue(input);
            if(report?.GetType().Name!="RawKeyboardInputReport")return;
            var type=report.GetType();
            if(Convert.ToInt32(type.GetProperty("VirtualKey",Private|BindingFlags.Public)!.GetValue(report))!=0x1B)return;
            if(type.GetProperty("InputSource",Private|BindingFlags.Public)?.GetValue(report) is not HwndSource source)return;
            if(GetWindowThreadProcessId(source.Handle,out uint process)!=GetCurrentThreadId()||process!=Environment.ProcessId)return;
            string actions=type.GetProperty("Actions",Private|BindingFlags.Public)!.GetValue(report)!.ToString()!;
            if(!actions.Contains("KeyDown")&&!actions.Contains("KeyUp"))return;
            if(cancelledTransport)
            {
                if(cancelCurrentDown&&actions.Contains("KeyDown"))
                {
                    args.Cancel();cancelledReports++;
                    Console.WriteLine($"CANCELLED_RAW_DOWN timestamp={input.Timestamp} hwnd={source.Handle:X} attempts={cancelledReports}");
                }
                return;
            }
            args.Cancel();deferredReports++;deferredInputs.Add(input);
            Console.WriteLine($"DEFERRED_RAW_REPORT actions={actions} timestamp={input.Timestamp} hwnd={source.Handle:X} depth={nativeDispatchDepth}");
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,()=>
            {
                allReplaysAfterReturn&=nativeDispatchDepth==0;deferredReplays++;
                Console.WriteLine($"DEFERRED_RAW_REPLAY timestamp={input.Timestamp} hwnd={source.Handle:X} depth={nativeDispatchDepth}");
                InputManager.Current.ProcessInput(input);
            });
        };
        ProcessInputEventHandler observeDeferredKeys=(_,args)=>
        {
            if(!reportBoundaryTests||args.StagingItem.Input is not KeyEventArgs key||key.Key!=System.Windows.Input.Key.Escape)return;
            bool up=key.RoutedEvent==Keyboard.PreviewKeyUpEvent;
            if(!up&&key.RoutedEvent!=Keyboard.PreviewKeyDownEvent)return;
            var info=AppInputPolicy.InspectRepeat(key);
            deferredKeys.Add((up,info.NativeMatches,info.Message,info.Flags));
            Console.WriteLine($"DEFERRED_KEY up={up} wpfRepeat={key.IsRepeat} nativeMatched={info.NativeMatches} nativeRepeat={info.NativeRepeat} message={info.Message:X} hwnd={info.Window:X} flags={info.Flags:X}");
        };
        try
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(x => x.IsPrimary);
            var bounds = enhanced ? new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 30, 640, 360) : new PixelBounds(-12000, -12000, 640, 360);
            var monitor = primary with { Bounds = bounds, WorkArea = bounds };
            Func<IReadOnlyList<MonitorInfo>> provider = () => [monitor];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            if (enhanced) host.EnableSystemInput();
            session = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            (Field<object?>(host, "_keyboardIsolation") as IDisposable)?.Dispose();
            typeof(OverlayHost).GetField("_keyboardIsolation", Private)!.SetValue(host, null);
            var toolbar = Field<Window>(session, "_toolbar"); toolbar.Opacity = 0;
            var menu = Field<OverlayContextMenu>(session, "_toolbarMenu"); menu.Opacity = 0;
            var bridge = new PrivateKeyboardBridge(handle => host.Diagnostics.Any(pair => pair.InputHandle == handle));
            var dispatch = Field<Action<KeyboardInputPacket, Action>?>(host, "_dispatchPrivateKey") ?? ((KeyboardInputPacket packet, Action complete) => bridge.Process(packet, complete));
            // WPF invokes PreProcessInput in reverse registration order. The
            // production observer must retain provenance even when this later
            // consumer cancels/defers the report before that observer runs.
            if(reportBoundaryTests){InputManager.Current.PreProcessInput+=deferRawKeyboard;InputManager.Current.PostProcessInput+=observeDeferredKeys;}
            host.SetVisible(true);
            Enter();
            check("menu Escape fixture enters the production editing session", host.IsEditing);
            Key();
            check("one Escape exits a clean editing session", !host.IsEditing);
            bool floatingAction=Environment.GetEnvironmentVariable("FLOATCANVAS_MENU_ESCAPE_ACTION")=="floating";
            var settings=Field<UserSettings>(session,"_settings");
            var item = floatingAction?menu.Items.OfType<MenuItem>().Single(x => Equals(x.Header, L.Get("Menu.ShowFloatingLogo")) || Equals(x.Header, L.Get("Menu.HideFloatingLogo"))):(MenuItem)menu.Items[0];
            int clicks=0,round=0;var outcomes=new List<bool>();
            item.Click+=(_,_)=>{clicks++;Console.WriteLine($"MENU_COMMAND round={round} clicks={clicks} floating={settings.ShowPassThroughLauncher} expanded={settings.ToolbarExpanded}");};
            toolbar.AddHandler(Keyboard.PreviewKeyDownEvent,new KeyEventHandler((_,e)=>
            {
                if(e.Key!=System.Windows.Input.Key.Escape)return;
                var native=AppInputPolicy.InspectRepeat(e);
                var origin=e.OriginalSource as DependencyObject;
                var eventSource=origin is null?null:PresentationSource.FromDependencyObject(origin) as HwndSource;
                Console.WriteLine($"TOOLBAR_ESCAPE round={round} isRepeat={e.IsRepeat} policyRepeat={AppInputPolicy.IsRepeat(e)} handled={e.Handled} previousKey={PreviousKey()} editing={host.IsEditing} eventTime={e.Timestamp} currentMessageTime={GetMessageTime()} eventHwnd={eventSource?.Handle:X} nativeMatched={native.NativeMatches} rawMessage={native.Message:X} rawTime={native.MessageTime} rawHwnd={native.Window:X} rawKey={native.VirtualKey} rawLparam={native.Flags:X}");
            }),true);
            if(cancelledTransport){CancelledCases();return;}
            if(Environment.GetEnvironmentVariable("FLOATCANVAS_MENU_ESCAPE_ACTION")=="panel") {PanelCases();VerifyDeferred();return;}
            if(Environment.GetEnvironmentVariable("FLOATCANVAS_MENU_ESCAPE_ACTION")=="tool") {ToolCases();VerifyDeferred();return;}
            for(round=0;round<2;round++)
            {
                Enter();
                menu.IsOpen = true; Pump();clicks=0;
                check("production launcher menu is open before its command round "+round, menu.IsOpen);
                bool previousSetting=floatingAction?settings.ShowPassThroughLauncher:settings.ToolbarExpanded;
                Trace("before-command");
                if(Environment.GetEnvironmentVariable("FLOATCANVAS_MENU_ESCAPE_CLICK") == "direct")
                    typeof(MenuItem).GetMethod("OnClick", Private)!.Invoke(item, null);
                else Click(item);
                Pump();
                check("one real menu command executes exactly once round "+round, clicks==1&&(floatingAction?settings.ShowPassThroughLauncher:settings.ToolbarExpanded)!=previousSetting);
                check("the command visually closes the launcher menu round "+round, !menu.IsOpen);
                Trace("after-command");
                Key();outcomes.Add(!host.IsEditing);Trace("after-first-escape");
                if (host.IsEditing) { Key(); Trace("after-second-escape"); }
            }
            firstEscExited=outcomes.All(value=>value);
            Enter(); menu.IsOpen=true;Pump();clicks=0;
            check("held-key counterexample opens its real menu",menu.IsOpen);
            typeof(MenuItem).GetMethod("OnClick",Private)!.Invoke(item,null);Pump();
            check("held-key counterexample closes only its menu command",clicks==1&&!menu.IsOpen&&host.IsEditing);
            Key(repeat:true);Trace("after-genuine-repeat");
            check("a genuine held Escape repeat does not exit editing",host.IsEditing);
            Key();
            check("a fresh Escape after the held key is released exits editing",!host.IsEditing);
            VerifyDeferred();

            void Key(bool repeat=false)
            {
                foreach (bool up in new[] { false, true })
                {
                    if(up&&cancelledTransport)cancelCurrentDown=false;
                    var state = new byte[256]; if (!up) state[0x1B] = 128;
                    var packet = (KeyboardInputPacket)Activator.CreateInstance(typeof(KeyboardInputPacket), Private, null,
                        [1L, 1L, KeyboardInputPacketKind.Key, 0x1Bu, MapVirtualKey(0x1B, 0), 0u, (uint)Environment.TickCount,
                            up, !up&&repeat, GetForegroundWindow(), state, false, null, up ? 0x101u : 0x100u], null)!;
                    Console.WriteLine($"KEY_PACKET transport={transport} up={up} previousKeyBefore={PreviousKey()} focusHwnd={GetFocus():X} editing={host.IsEditing}");
                    if (hwndTransport) DispatchToFocusedWindow(packet);
                    else dispatch(packet, () => { });
                    Pump();
                    Console.WriteLine($"KEY_PACKET_DONE up={up} previousKeyAfter={PreviousKey()} focusHwnd={GetFocus():X} editing={host.IsEditing}");
                }
            }
            void DispatchToFocusedWindow(KeyboardInputPacket packet)
            {
                // Replay the enhanced native carrier's local WM_KEY path. This
                // intentionally bypasses the private bridge and does not repair
                // WPF's previous-key cache or synthesize a missing key-up target.
                var state=packet.CopyKeyboardState();var current=new byte[256];
                if(!GetKeyboardState(current))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                foreach(int button in new[]{1,2,4,5,6})state[button]=current[button];
                if(!SetKeyboardState(state))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                nint focus=GetFocus();
                if(focus==0){Console.WriteLine($"HWND_KEY_SKIPPED up={packet.IsKeyUp} reason=no-focus");return;}
                uint thread=GetWindowThreadProcessId(focus,out uint process);
                if(process!=Environment.ProcessId||thread!=GetCurrentThreadId())throw new InvalidOperationException("Keyboard fixture target must be this thread's owned HWND.");
                uint flags=packet.Flags;
                uint data=1|((packet.ScanCode&0xff)<<16)|((flags&1)<<24)|(((flags&0x20)!=0?1u:0u)<<29)|
                    ((packet.IsRepeat||packet.IsKeyUp?1u:0u)<<30)|((packet.IsKeyUp?1u:0u)<<31);
                uint message=packet.NativeMessage is 0x100 or 0x101 or 0x104 or 0x105?packet.NativeMessage:
                    packet.IsKeyUp?((flags&0x20)!=0?0x105u:0x101u):((flags&0x20)!=0?0x104u:0x100u);
                Console.WriteLine($"HWND_KEY hwnd={focus:X} message={message:X} key={packet.VirtualKey} lparam={data:X} queueTime={GetMessageTime()}");
                if(HwndSource.FromHwnd(focus) is not null){if(packet.IsKeyUp)wpfNativeUp++;else wpfNativeDown++;}
                nativeDispatchDepth++;
                try
                {
                    if(queuedTransport)
                    {
                        if(!PostMessage(focus,message,(nint)packet.VirtualKey,unchecked((nint)(int)data)))
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    }
                    else SendMessage(focus,message,(nint)packet.VirtualKey,unchecked((nint)(int)data));
                }
                finally{nativeDispatchDepth--;}
            }
            void CancelledCases()
            {
                Enter();Focus(toolbar);
                int beforeCancelled=deferredKeys.Count(value=>!value.Up);
                cancelCurrentDown=true;Key(repeat:true);
                check("one native down is permanently cancelled before key-event staging",cancelledReports>0&&deferredKeys.Count(value=>!value.Up)==beforeCancelled&&host.IsEditing);
                int beforeFresh=deferredKeys.Count;
                Key();
                var fresh=deferredKeys.Skip(beforeFresh).Where(value=>!value.Up).ToArray();
                check("fresh down after cancelled down and real up does not borrow the cancelled token",fresh.Length==1&&fresh[0].Matched&&(fresh[0].Message is 0x100 or 0x104)&&(fresh[0].Flags.ToInt64()&(1L<<30))==0);
                check("fresh down after a cancelled press exits editing normally",!host.IsEditing);
                Enter();Focus(toolbar);
                int beforeHeld=deferredKeys.Count;
                Key(repeat:true);
                var held=deferredKeys.Skip(beforeHeld).Where(value=>!value.Up).ToArray();
                check("later genuine repeat receives its own repeat provenance",held.Length==1&&held[0].Matched&&(held[0].Flags.ToInt64()&(1L<<30))!=0&&host.IsEditing);
                Key();check("fresh press after the genuine repeat is released remains usable",!host.IsEditing);
            }
            void VerifyDeferred()
            {
                if(!deferredTransport)return;
                check("real keyboard raw reports replay only after native WndProc returns",deferredReports>0&&deferredReplays==deferredReports&&allReplaysAfterReturn);
                var downs=deferredKeys.Where(value=>!value.Up).ToArray();var ups=deferredKeys.Where(value=>value.Up).ToArray();
                check("deferred key-down events retain their own native provenance",downs.Length==wpfNativeDown&&downs.All(value=>value.Matched&&value.Message is 0x100 or 0x104));
                check("deferred key-up events consume up provenance without borrowing down",ups.Length==wpfNativeUp&&ups.Length>0&&ups.All(value=>value.Matched&&value.Message is 0x101 or 0x105));
            }
            void PanelCases()
            {
                // Establish the same stale WPF key through an actual prior exit,
                // never by assigning framework keyboard fields.
                Enter();menu.IsOpen=true;Pump();clicks=0;
                typeof(MenuItem).GetMethod("OnClick",Private)!.Invoke(item,null);Pump();Key();
                check("panel history warmup exits through the real WPF menu-owner route",!host.IsEditing);
                Enter();
                var options=Field<Window>(session,"_toolOptionsWindow");options.Opacity=0;
                options.AddHandler(Keyboard.PreviewKeyDownEvent,new KeyEventHandler((_,e)=>
                {
                    if(e.Key!=System.Windows.Input.Key.Escape)return;
                    var native=AppInputPolicy.InspectRepeat(e);
                    var origin=e.OriginalSource as DependencyObject;
                    var eventSource=origin is null?null:PresentationSource.FromDependencyObject(origin) as HwndSource;
                    Console.WriteLine($"PANEL_ESCAPE round={round} isRepeat={e.IsRepeat} policyRepeat={AppInputPolicy.IsRepeat(e)} handled={e.Handled} previousKey={PreviousKey()} visible={options.IsVisible} editing={host.IsEditing} eventTime={e.Timestamp} currentMessageTime={GetMessageTime()} eventSource={origin?.GetType().Name} eventHwnd={eventSource?.Handle:X} nativeMatched={native.NativeMatches} rawMessage={native.Message:X} rawTime={native.MessageTime} rawHwnd={native.Window:X} rawKey={native.VirtualKey} rawLparam={native.Flags:X}");
                }),true);
                var firstCloses=new List<bool>();
                for(round=0;round<2;round++)
                {
                    Invoke("OpenToolOptions");Pump();
                    Focus(options);
                    Pump();
                    check("panel blank-focus uses its visible real Window round "+round,options.IsVisible&&host.IsEditing&&ReferenceEquals(Keyboard.FocusedElement,options));
                    Trace("panel-before-escape");Key();firstCloses.Add(!options.IsVisible);Trace("panel-after-first-escape");
                    if(options.IsVisible){Key();Trace("panel-after-second-escape");}
                    check("closing properties retains editing round "+round,!options.IsVisible&&host.IsEditing);
                }
                check("first Escape closes each newly focused tool panel without exiting editing",firstCloses.All(value=>value));
                Invoke("OpenToolOptions");Pump();Focus(options);Key(repeat:true);
                check("a genuine held Escape repeat leaves the tool panel open",options.IsVisible&&host.IsEditing);
                Key();
                check("a fresh Escape after release closes the tool panel only",!options.IsVisible&&host.IsEditing);
            }
            void ToolCases()
            {
                var firstExits=new List<bool>();
                for(round=0;round<2;round++)
                {
                    Enter();SwitchTool();Trace("tool-before-escape");Key();
                    firstExits.Add(!host.IsEditing);Trace("tool-after-first-escape");
                    if(host.IsEditing){Key();Trace("tool-after-second-escape");}
                }
                Enter();SwitchTool();Key(repeat:true);
                check("held Escape does not exit after only switching tools",host.IsEditing);
                Key();check("released and freshly pressed Escape exits after only switching tools",!host.IsEditing);
                check("one Escape exits after tool changes across editing sessions",firstExits.All(value=>value));
            }
            void SwitchTool()
            {
                var editor=Field<EditorController>(session,"_editor");
                var target=editor.Tool==EditorTool.Highlighter?EditorTool.Freehand:EditorTool.Highlighter;
                var button=Field<Dictionary<EditorTool,Button>>(session,"_toolbarToolButtons")[target];
                int toolClicks=0;RoutedEventHandler observed=(_,_)=>toolClicks++;button.Click+=observed;
                try
                {
                    Focus(toolbar);
                    var device=(MouseDevice)typeof(ToolOptionsInputFixture).GetMethod("ControlledMouse",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[button])!;
                    try{InputManager.Current.ProcessInput(new MouseButtonEventArgs(device,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent,Source=button});Pump();}
                    finally{ToolOptionsInputFixture.ReleaseControlledMouse(device);}
                    button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonUpEvent});Pump();
                    if(toolClicks==0){typeof(Button).GetMethod("OnClick",Private)!.Invoke(button,null);Pump();}
                }
                finally{button.Click-=observed;}
                check("real tool button switches tools without opening properties round "+round,toolClicks==1&&editor.Tool==target&&!Field<Window>(session,"_toolOptionsWindow").IsVisible&&!menu.IsOpen);
            }
            void Focus(Window window)
            {
                typeof(NativeInputRoutingFixture).GetMethod("ActivateFixtureWindow",BindingFlags.Static|BindingFlags.NonPublic)!
                    .Invoke(null,[new WindowInteropHelper(window).Handle,(Action)(()=>Keyboard.Focus(window))]);Pump();
            }
            void Trace(string phase)
            {
                var scope = Field<OverlayWindowScope?>(session, "_overlayWindows");
                var windows = scope is null ? null : Field<ICollection>(scope, "windows");
                var focused = Keyboard.FocusedElement as DependencyObject;
                var editor=Field<EditorController>(session,"_editor");
                var clear=Field<NewCanvasButton?>(session,"_newCanvasButton");
                Console.WriteLine($"MENU_ESCAPE round={round} phase={phase} enhanced={enhanced} editing={host.IsEditing} menu={menu.IsOpen} baseMenu={((ContextMenu)menu).IsOpen} menuMode={typeof(InputManager).GetProperty("IsInMenuMode", Private | BindingFlags.Public)?.GetValue(InputManager.Current)} ownMenuMode={typeof(MenuBase).GetProperty("IsMenuMode",Private)?.GetValue(menu)} scopeWindows={windows?.Count ?? 0} focus={focused?.GetType().Name} logicalFocus={FocusManager.GetFocusedElement(menu)?.GetType().Name} focusSource={(focused is null ? null : PresentationSource.FromDependencyObject(focused))?.GetType().Name} captured={Mouse.Captured?.GetType().Name} nativeCapture={GetCapture():X} transaction={editor.Session.IsTransactionActive} marquee={editor.Marquee} confirming={clear?.IsConfirming} options={typeof(DesktopSession).GetProperty("IsToolOptionsOpen",Private)?.GetValue(session)} color={Field<ToolOptionsPanel>(session,"_toolOptionsPanel").IsColorPickerOpen} previousKey={PreviousKey()}");
            }
            void Click(MenuItem target)
            {
                var device=(MouseDevice)typeof(ToolOptionsInputFixture).GetMethod("ControlledMouse",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[target])!;
                try
                {
                    InputManager.Current.ProcessInput(new MouseButtonEventArgs(device,Environment.TickCount,MouseButton.Left)
                        {RoutedEvent=Mouse.PreviewMouseDownEvent,Source=target});Pump();Trace("mouse-down");
                }
                finally {ToolOptionsInputFixture.ReleaseControlledMouse(device);}
                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                    {RoutedEvent=UIElement.MouseLeftButtonUpEvent});Pump();
                if(clicks==0) {typeof(MenuItem).GetMethod("OnClick",Private)!.Invoke(target,null);Pump();}
            }
            object? Invoke(string name, params object[] values) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, values);
            object? PreviousKey()=>typeof(KeyboardDevice).GetField("_previousKey",Private)?.GetValue(Keyboard.PrimaryDevice);
            void Enter()
            {
                toolbar.Left = toolbar.Top = -12000; toolbar.ShowActivated = false; toolbar.Show(); Pump();
                typeof(NativeInputRoutingFixture).GetMethod("ActivateFixtureWindow", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [new WindowInteropHelper(toolbar).Handle, (Action)(() => Invoke("SetEditing", true, false))]);
                for(int settle=0;settle<3;settle++){Thread.Sleep(20);Pump();}
            }
        }
        finally
        {
            if(reportBoundaryTests){InputManager.Current.PreProcessInput-=deferRawKeyboard;InputManager.Current.PostProcessInput-=observeDeferredKeys;}
            session?.Dispose(); if (session is null) host?.Dispose();
            foreach (var window in app.Windows.Cast<Window>().ToArray()) window.Close();
            SetKeyboardState(originalKeyboardState);
            Pump(); SynchronizationContext.SetSynchronizationContext(previousContext); app.Shutdown();
        }
        check("first Escape after an automatically closed menu exits editing", firstEscExited);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint key, uint mode);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern int GetMessageTime();
    [DllImport("user32.dll",SetLastError=true)] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window,uint message,nint wparam,nint lparam);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool PostMessage(nint window,uint message,nint wparam,nint lparam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
