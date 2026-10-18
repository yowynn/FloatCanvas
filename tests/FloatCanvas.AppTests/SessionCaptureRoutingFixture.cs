using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Opt-in production WGC session exclusion with a separate-PID solid
/// background. Full desktop capture is sampled in memory only; reports contain RGB data.</summary>
internal static class SessionCaptureRoutingFixture
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;

    internal static void Run(Action<string,bool> check,DesktopSession session,OverlayHost host,EditorController editor,
        Window toolbar,string run,string scenario,Action<string> snapshot)
    {
        if(scenario=="session-capture-readiness")
        {
            LiveEffectReadinessFixture.Run(check,session,host,editor,toolbar,run,snapshot);
            return;
        }
        if(scenario=="session-capture-readiness-cancel")
        {
            LiveEffectPreparationRoutingFixture.Run(check,session,host,editor,toolbar,run,snapshot);
            return;
        }
        if(scenario!="session-capture-native")throw new ArgumentException("Unknown session capture scenario: "+scenario);
        var monitor=host.PrimaryMonitor;var native=host.Diagnostics.Single();
        check("session capture uses the actual primary output and a real editing lease",native.Bounds==monitor.Bounds && native.Bounds.Width>640 && host.IsEditing && host.IsKeyboardIsolationActive);
        check("session capture requires the modern capture-exclusion platform baseline",host.Capabilities.SupportsCaptureExclusion);
        long epoch=host.EditingSessionVersion;int focusLoss=0,leaseEnds=0;
        Action lost=()=>focusLoss++;Action<KeyboardLeaseEndReason> released=_=>leaseEnds++;
        host.EditingFocusLost+=lost;host.KeyboardIsolationReleased+=released;
        Process? background=null;LiveDesktopCapture? source=null;
        var colors=new List<PixelSample>();var cleanupErrors=new List<string>();
        string childDirectory=Path.Combine(run,"background");Directory.CreateDirectory(childDirectory);
        string phase="initial";bool red=false,blue=false;SKColor? externalMarker=null;
        int sequence=0;Action<SKSurface>? observe=null;
        object? layers=null;FieldInfo? layerGate=null;Func<bool>? originalLayerGate=null;
        int layerRefreshCalls=0;bool layerProbeObserved=false;
        try
        {
            editor.Cancel();editor.Session.Execute("isolated session capture setup",document=>document.Elements.Clear());editor.Session.SetSelection([]);
            int left=monitor.WorkArea.Left+90,top=monitor.WorkArea.Top+280;
            var start=new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Missing test executable."))
                {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=run};
            foreach(string arg in new[]{"--session-capture-background-helper","--background-directory",childDirectory,"--background-left",left.ToString(CultureInfo.InvariantCulture),"--background-top",top.ToString(CultureInfo.InvariantCulture)})start.ArgumentList.Add(arg);
            background=Process.Start(start) ?? throw new InvalidOperationException("Cannot start owned capture background helper.");
            string readyPath=Path.Combine(childDirectory,"ready.json");Wait(()=>File.Exists(readyPath)||background.HasExited,"separate-PID background metadata",5);
            if(background.HasExited)throw new InvalidOperationException("The owned background helper exited before publishing its window.");
            var ready=JsonSerializer.Deserialize<BackgroundReady>(CaptureFixtureIpc.ReadAllText(readyPath))!;
            nint childHandle=(nint)ready.Handle;
            GetWindowThreadProcessId(childHandle,out uint childPid);
            check("the solid capture background belongs to the separate owned child PID",ready.Process==background.Id && childPid==background.Id && childPid!=Environment.ProcessId &&
                IsWindowVisible(childHandle) && GetWindowDisplayAffinity(childHandle,out uint affinity) && affinity==0 && ready.Width==420 && ready.Height==260);
            OverlayCoordinates.RaiseToolWindow(native.DisplayHandle);OverlayCoordinates.RaiseToolWindow(native.InputHandle);
            OverlayCoordinates.RaiseToolWindow(new WindowInteropHelper(toolbar).Handle);
            var region=new RectD(ready.Left+75,ready.Top+65,180,80);
            int sampleX=(int)(region.Center.X-native.Bounds.Left),sampleY=(int)(region.Center.Y-native.Bounds.Top);
            var marker=new CanvasElement {Kind=ElementKind.Rectangle,Width=40,Height=40,
                Transform=Affine2D.Translation(monitor.WorkArea.Right-140,monitor.WorkArea.Top+360),Style=new(){Fill=0xFFFF00FF,StrokeWidth=0}};
            editor.Session.Add(marker,"own external-capture visibility marker");editor.Session.SetSelection([]);
            observe=surface=>
            {
                using var pixel=new SKBitmap(new SKImageInfo(1,1,SKColorType.Bgra8888,SKAlphaType.Premul));
                if(!surface.ReadPixels(pixel.Info,pixel.GetPixels(),pixel.RowBytes,sampleX,sampleY))throw new InvalidOperationException("Cannot read the own filtered overlay pixel.");
                var color=pixel.GetPixel(0,0);
                colors.Add(new(phase,Stopwatch.GetTimestamp(),color.Red,color.Green,color.Blue,color.Alpha,
                    host.IsEditing,host.IsKeyboardIsolationActive,host.EditingSessionVersion,editor.Session.SceneRevision,editor.Session.UndoCount));
                if(phase=="red" && color.Alpha>240 && color.Red>220 && color.Green<35 && color.Blue<35)red=true;
                if(phase=="blue" && color.Alpha>240 && color.Blue>220 && color.Green<35 && color.Red<35)blue=true;
            };
            host.Draw+=observe;
            phase="red";Command("Red");
            Await((Task)Invoke(session,"AddDesktopEffectAsync",region,ImageEffectKind.Blur,10d,0xFFEF4444u)!,"production live blur insertion");
            Wait(()=>FieldOrNull<Task<bool>>(session,"_sessionCaptureProbe") is {IsCompleted:true},"actual asynchronous session-exclusion probe",10);
            bool supported=FieldOrNull<Task<bool>>(session,"_sessionCaptureProbe")!.GetAwaiter().GetResult();
            check("the actual WGC display-session exclusion interface is available",supported);
            if(!supported)throw new NotSupportedException("This opt-in case requires WGC session-local exclusion; the legacy fallback is a separate case.");
            Wait(()=>red,"real WGC red background pixels",10);
            source=FieldOrNull<LiveDesktopCapture>(session,"_liveDesktopCapture");
            check("production live blur receives red pixels through session-local WGC exclusion",red && source?.UsesSessionWindowExclusion==true &&
                !FieldOrNull<bool?>(session,"_liveCaptureExcluded").GetValueOrDefault() && FieldOrNull<object>(session,"_desktopBackdrop") is not null);
            int count=editor.Session.Document.Elements.Count,undo=editor.Session.UndoCount;long revision=editor.Session.SceneRevision;
            var identities=editor.Session.Document.Elements.Select(element=>element.Id).ToArray();
            check("production insertion keeps the marker and adds one editable live blur",count==2 && editor.Session.Document.Elements.Any(element=>element.Kind==ElementKind.LiveBlur && element.AssetId is null));
            Invoke(session,"ToggleLayers");Pump(150);
            layers=FieldOrNull<object>(session,"_layers")!;layerGate=layers.GetType().GetField("canMutate",Private)!;
            originalLayerGate=(Func<bool>)layerGate.GetValue(layers)!;
            var actualLayerGate=originalLayerGate;
            layerGate.SetValue(layers,(Func<bool>)(()=>{layerRefreshCalls++;return actualLayerGate();}));
            layers.GetType().GetMethod("Refresh")!.Invoke(layers,null);
            layerProbeObserved=layerRefreshCalls>0;Pump(150);layerRefreshCalls=0;int framesBeforeBlue=colors.Count;
            phase="blue";Command("Blue");Wait(()=>blue,"real WGC blue background pixels",10);
            check("the same persistent WGC source updates live blur from red to blue",blue && ReferenceEquals(source,FieldOrNull<LiveDesktopCapture>(session,"_liveDesktopCapture")));
            check("real live-pixel frames do not rebuild the open production Layers panel",layerProbeObserved && colors.Count>framesBeforeBlue && layerRefreshCalls==0);
            layerGate.SetValue(layers,originalLayerGate);Invoke(session,"ToggleLayers");Pump(100);
            check("live frames preserve document identities revision and undo history",editor.Session.SceneRevision==revision && editor.Session.UndoCount==undo &&
                editor.Session.Document.Elements.Select(element=>element.Id).SequenceEqual(identities));
            check("session-local exclusion leaves every overlay HWND globally capturable",host.Diagnostics.All(pair=>AffinityIsZero(pair.DisplayHandle)&&AffinityIsZero(pair.InputHandle)));
            check("session capture preserves the toolbar edit epoch and input lease",host.IsEditing && host.IsKeyboardIsolationActive && host.EditingSessionVersion==epoch &&
                toolbar.IsVisible && focusLoss==0 && leaseEnds==0 && colors.All(sample=>sample.Editing&&sample.Lease&&sample.Epoch==epoch));
            SaveToolbar(toolbar,Path.Combine(run,"production-toolbar-session-capture.png"));snapshot("session-capture-blue-live-frame");

            using(var capture=new CaptureService())
            {
                // This is an independent ordinary DXGI request, with no WDA changes or
                // clean-capture scope. Do not persist/serialize its desktop PNG.
                var operation=capture.CaptureAsync(host.Monitors);Await(operation,"ordinary external-style DXGI capture",10);
                var desktop=operation.GetAwaiter().GetResult();using var bitmap=SKBitmap.Decode(desktop.PngBytes);
                var center=marker.Transform.TransformPoint(new(20,20));
                externalMarker=bitmap.GetPixel((int)(center.X-desktop.Bounds.Left),(int)(center.Y-desktop.Bounds.Top));
            }
            check("an ordinary independent DXGI capture still sees the magenta canvas marker",externalMarker is {Alpha:>240,Red:>220,Green:<35,Blue:>220});
            check("the independent capture does not break the session-local source or editing lease",ReferenceEquals(source,FieldOrNull<LiveDesktopCapture>(session,"_liveDesktopCapture")) &&
                host.IsEditing && host.IsKeyboardIsolationActive && host.EditingSessionVersion==epoch && focusLoss==0 && leaseEnds==0);

            Invoke(session,"ToggleCanvasVisibility");
            Wait(()=>!host.IsVisible && FieldOrNull<LiveDesktopCapture>(session,"_liveDesktopCapture") is null,"hidden canvas stops its production WGC source");
            Await(source!.Completion,"hidden production WGC worker shutdown",5);
            check("hiding stops the actual WGC worker without ending the editing lease",source.Completion.IsCompletedSuccessfully && !host.IsVisible && host.IsEditing && host.IsKeyboardIsolationActive);
            // Deterministically delay only the capability observer continuation. The
            // preceding source, colored frames and capture all used real WGC/DXGI.
            var pendingProbe=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer=(Task)Invoke(session,"ObserveSessionCaptureProbeAsync",pendingProbe.Task)!;
            Invoke(session,"SetEditing",false,false);pendingProbe.SetResult(true);Await(observer,"late production capability-observer continuation");Pump(120);
            check("a late asynchronous capability result cannot show a hidden canvas or reenter editing",!host.IsVisible && !host.IsEditing && !host.IsKeyboardIsolationActive &&
                !toolbar.IsVisible && FieldOrNull<LiveDesktopCapture>(session,"_liveDesktopCapture") is null);
            snapshot("session-capture-hidden-mode-exit");

            void Command(string color)
            {
                int requested=++sequence;AtomicJson(Path.Combine(childDirectory,"command.json"),new BackgroundCommand(requested,color,false));
                Wait(()=>AckMatches(requested,color),"owned background color acknowledgement",2);
            }
            bool AckMatches(int requested,string color)
            {
                string path=Path.Combine(childDirectory,"ack.json");if(!File.Exists(path))return false;
                var ack=JsonSerializer.Deserialize<BackgroundAck>(CaptureFixtureIpc.ReadAllText(path));return ack?.Sequence==requested && ack.Color==color;
            }
        }
        finally
        {
            if(observe is not null)host.Draw-=observe;
            if(layers is not null && layerGate is not null && originalLayerGate is not null)layerGate.SetValue(layers,originalLayerGate);
            source??=FieldOrNull<LiveDesktopCapture>(session,"_liveDesktopCapture");
            string? captureError=FieldOrNull<string>(session,"_liveCaptureError"),sourceError=source?.LastError?.ToString();
            try { Invoke(session,"StopLiveEffectsCapture"); } catch(Exception error){cleanupErrors.Add(error.ToString());}
            if(source is not null)try{Await(source.Completion,"owned WGC cleanup",5);}catch(Exception error){cleanupErrors.Add(error.ToString());}
            if(background is not null)
            {
                try
                {
                    AtomicJson(Path.Combine(childDirectory,"command.json"),new BackgroundCommand(++sequence,"",true));
                    Wait(()=>background.HasExited,"owned background helper shutdown",2);
                }
                catch(Exception error)
                {
                    cleanupErrors.Add(error.ToString());
                    if(!background.HasExited){background.Kill();background.WaitForExit(2000);}
                }
                finally{background.Dispose();}
            }
            host.EditingFocusLost-=lost;host.KeyboardIsolationReleased-=released;
            File.WriteAllText(Path.Combine(run,"session-capture-metrics.json"),JsonSerializer.Serialize(new
            {
                Pixels="Only single-pixel RGB samples from the own filtered overlay and own magenta marker. No desktop image saved.",
                ProbeContinuation="Final late-result scheduling is controlled; preceding WGC source and DXGI pixels are real.",
                NativeBounds=native.Bounds,RedObserved=red,BlueObserved=blue,ExternalMarker=externalMarker is {} color?new{color.Red,color.Green,color.Blue,color.Alpha}:null,
                UsesSessionExclusion=source?.UsesSessionWindowExclusion,InitialEpoch=epoch,FinalEpoch=host.EditingSessionVersion,FocusLosses=focusLoss,LeaseEnds=leaseEnds,
                LayerProbeObserved=layerProbeObserved,LayerRefreshCallsDuringPixelFrames=layerRefreshCalls,
                Error=captureError,SourceError=sourceError,WorkerCompleted=source?.Completion.IsCompleted,Frames=colors,CleanupErrors=cleanupErrors
            },new JsonSerializerOptions {WriteIndented=true}));
        }
    }

    internal static void RunBackgroundHelper(string[] args)
    {
        string Required(string flag){int at=Array.IndexOf(args,flag);return at>=0 && at+1<args.Length?args[at+1]:throw new ArgumentException("Missing "+flag);}
        string directory=Path.GetFullPath(Required("--background-directory"));Directory.CreateDirectory(directory);
        int left=int.Parse(Required("--background-left"),CultureInfo.InvariantCulture),top=int.Parse(Required("--background-top"),CultureInfo.InvariantCulture);
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var solid=new Border {Background=Brushes.Red};
        var window=new Window {Title="FloatCanvas owned session-capture background",Content=solid,Width=420,Height=260,
            WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowActivated=false,ShowInTaskbar=false,Topmost=true};
        window.SourceInitialized+=(_,_)=>
        {
            var handle=new WindowInteropHelper(window).Handle;
            SetWindowLongPtrW(handle,-20,GetWindowLongPtrW(handle,-20)|0x08000080);
        };
        window.Show();nint hwnd=new WindowInteropHelper(window).Handle;
        SetWindowPos(hwnd,new nint(-1),left,top,420,260,0x0010|0x0040);
        GetWindowRect(hwnd,out var rect);
        AtomicJson(Path.Combine(directory,"ready.json"),new BackgroundReady(Environment.ProcessId,(long)hwnd,rect.Left,rect.Top,rect.Right-rect.Left,rect.Bottom-rect.Top));
        var deadline=DateTime.UtcNow.AddSeconds(20);int last=0;string reason="Deadline";
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(25)};
        timer.Tick+=(_,_)=>
        {
            string commandPath=Path.Combine(directory,"command.json");
            if(File.Exists(commandPath))
            {
                var command=JsonSerializer.Deserialize<BackgroundCommand>(CaptureFixtureIpc.ReadAllText(commandPath));
                if(command is not null && command.Sequence>last)
                {
                    last=command.Sequence;
                    if(command.Stop){reason="Stop";timer.Stop();window.Close();app.Shutdown();return;}
                    solid.Background=command.Color switch {"Red"=>Brushes.Red,"Blue"=>Brushes.Blue,_=>throw new ArgumentException("Unsupported owned background color.")};
                    AtomicJson(Path.Combine(directory,"ack.json"),new BackgroundAck(command.Sequence,command.Color,Stopwatch.GetTimestamp()));
                }
            }
            if(DateTime.UtcNow>=deadline){timer.Stop();window.Close();app.Shutdown();}
        };
        try{timer.Start();app.Run();}
        finally{timer.Stop();AtomicJson(Path.Combine(directory,"stopped.json"),new{Process=Environment.ProcessId,Reason=reason,At=DateTimeOffset.Now});}
    }

    private sealed record BackgroundReady(int Process,long Handle,int Left,int Top,int Width,int Height);
    private sealed record BackgroundCommand(int Sequence,string Color,bool Stop);
    private sealed record BackgroundAck(int Sequence,string Color,long AppliedTimestamp);
    private sealed record PixelSample(string Phase,long Timestamp,byte Red,byte Green,byte Blue,byte Alpha,bool Editing,bool Lease,long Epoch,long Revision,int Undo);
    private static void AtomicJson(string path,object value)=>CaptureFixtureIpc.WriteJson(path,value);
    private static bool AffinityIsZero(nint window)=>GetWindowDisplayAffinity(window,out uint affinity)&&affinity==0;
    private static T? FieldOrNull<T>(object target,string name)=>(T?)target.GetType().GetField(name,Private)!.GetValue(target);
    private static object? Invoke(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Private)!.Invoke(target,args);
    private static void Await(Task task,string operation,int seconds=8){Wait(()=>task.IsCompleted,operation,seconds);task.GetAwaiter().GetResult();}
    private static void Wait(Func<bool> condition,string operation,int seconds=8)
    {long start=Stopwatch.GetTimestamp();while(!condition()){if(Stopwatch.GetElapsedTime(start).TotalSeconds>=seconds)throw new TimeoutException(operation);Pump(20);}}
    private static void Pump(int milliseconds)
    {var frame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=(_,_)=>frame.Continue=false;try{timer.Start();Dispatcher.PushFrame(frame);}finally{timer.Stop();}}
    private static void SaveToolbar(Window window,string path)
    {window.UpdateLayout();var dpi=VisualTreeHelper.GetDpi(window);var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(window.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);}
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{internal int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint window,out NativeRect rect);
    [DllImport("user32.dll")]private static extern bool GetWindowDisplayAffinity(nint window,out uint affinity);
    [DllImport("user32.dll")]private static extern nint GetWindowLongPtrW(nint window,int index);
    [DllImport("user32.dll")]private static extern nint SetWindowLongPtrW(nint window,int index,nint value);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(nint window,nint insertAfter,int x,int y,int width,int height,uint flags);
}
