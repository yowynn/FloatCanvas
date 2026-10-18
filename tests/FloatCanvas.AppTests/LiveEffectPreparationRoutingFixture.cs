using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Controlled capability/readiness completion timing around actual production
/// preparation and cancellation. Pixel performance is covered by the separate real-WGC fixture.</summary>
internal static class LiveEffectPreparationRoutingFixture
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check,DesktopSession session,OverlayHost host,EditorController editor,
        Window toolbar,string run,Action<string> snapshot)
    {
        Process? external=null;Task<bool>? originalProbe=Field<Task<bool>>(session,"_sessionCaptureProbe");
        var outcomes=new List<object>();
        try
        {
            foreach(string reason in new[]{"hide","hide-show","tool","tool-away-back","document","external"})
            {
                if(!host.IsVisible)Invoke(session,"ToggleCanvasVisibility");
                nint toolbarHandle=new WindowInteropHelper(toolbar).Handle;
                if(!SetForegroundWindow(toolbarHandle) && GetForegroundWindow()!=toolbarHandle)throw new InvalidOperationException("Cannot enter the owned cancellation test foreground.");
                Wait(()=>GetForegroundWindow()==toolbarHandle,"owned cancellation toolbar foreground",2);
                if(!host.IsEditing)Invoke(session,"SetEditing",true,false);
                Wait(()=>host.IsEditing && host.IsKeyboardIsolationActive,"owned cancellation edit lease",2);
                editor.Cancel();editor.Tool=EditorTool.Blur;
                editor.Session.Execute("empty preparation fixture",document=>document.Elements.Clear());editor.Session.SetSelection([]);
                Invoke(session,"StopLiveEffectsCapture");
                var delayedProbe=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                SetField(session,"_sessionCaptureProbe",delayedProbe.Task);
                nint receiverHandle=0;
                if(reason=="external")
                {
                    var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=run};
                    foreach(string arg in new[]{"--focus-external-helper","--focus-helper-directory",run})start.ArgumentList.Add(arg);
                    external=Process.Start(start) ?? throw new InvalidOperationException("Cannot start owned cancellation receiver.");
                    string readyPath=Path.Combine(run,"external-ready.json");Wait(()=>File.Exists(readyPath),"owned cancellation receiver metadata",3);
                    using var metadata=JsonDocument.Parse(CaptureFixtureIpc.ReadAllText(readyPath));
                    if(metadata.RootElement.GetProperty("Process").GetInt32()!=external.Id)throw new InvalidDataException("Wrong cancellation receiver process.");
                    receiverHandle=(nint)metadata.RootElement.GetProperty("Handle").GetInt64();
                    AllowSetForegroundWindow((uint)external.Id);
                }
                int initialCount=editor.Session.Document.Elements.Count,initialUndo=editor.Session.UndoCount;
                long initialEpoch=host.EditingSessionVersion;
                var region=new RectD(host.PrimaryMonitor.WorkArea.Left+100,host.PrimaryMonitor.WorkArea.Top+300,160,80);
                var operation=(Task)Invoke(session,"AddDesktopEffectAsync",region,ImageEffectKind.Blur,10d,0xFFEF4444u)!;
                object pending=Field<object>(session,"_liveEffectPreparation") ?? throw new InvalidOperationException("The production live preparation did not suspend before its first frame.");
                var ready=(TaskCompletionSource<bool>)pending.GetType().GetProperty("Ready",Private)!.GetValue(pending)!;
                check(reason+" starts a real preparation outside the document and undo stack",!operation.IsCompleted && !ready.Task.IsCompleted &&
                    editor.Session.Document.Elements.Count==initialCount && editor.Session.UndoCount==initialUndo);
                if(reason=="hide")Invoke(session,"ToggleCanvasVisibility");
                else if(reason=="hide-show")
                {Invoke(session,"ToggleCanvasVisibility");Invoke(session,"ToggleCanvasVisibility");}
                else if(reason=="tool")editor.Tool=EditorTool.Rectangle;
                else if(reason=="tool-away-back")
                {editor.Tool=EditorTool.Rectangle;editor.Tool=EditorTool.Blur;}
                else if(reason=="document")editor.Session.ReplaceDocument(new(){Viewport=editor.Session.Document.Viewport});
                else
                {
                    if(!SetForegroundWindow(receiverHandle) && GetForegroundWindow()!=receiverHandle)throw new InvalidOperationException("Cannot activate the owned external cancellation receiver.");
                    Wait(()=>GetForegroundWindow()==receiverHandle,"actual independent receiver foreground",2);
                    Wait(()=>!host.IsEditing && !host.IsKeyboardIsolationActive,"production external-focus cancellation",2);
                }
                // Deliberately deliver a late completion to the captured old request. No
                // fabricated bitmap enters the renderer, and this signal follows cancellation.
                bool lateReadyAccepted=ready.TrySetResult(true);delayedProbe.TrySetResult(true);
                Await(operation,"cancelled production live preparation",2);
                Await((Task)Invoke(session,"ObserveSessionCaptureProbeAsync",delayedProbe.Task)!,"late capability observer",2);Pump(100);
                int expectedUndo=reason=="document"?0:initialUndo;
                check(reason+" late readiness cannot insert an effect or add an undo entry",editor.Session.Document.Elements.Count==0 &&
                    editor.Session.UndoCount==expectedUndo && Field<object>(session,"_liveEffectPreparation") is null);
                if(reason=="hide")check("late readiness leaves the canvas explicitly hidden",!host.IsVisible);
                else if(reason=="external")check("late readiness preserves the real external foreground and released lease",GetForegroundWindow()==receiverHandle &&
                    !host.IsEditing && !host.IsKeyboardIsolationActive && !toolbar.IsVisible);
                else check(reason+" cancellation preserves the healthy edit session",host.IsEditing && host.IsKeyboardIsolationActive && toolbar.IsVisible && host.EditingSessionVersion==initialEpoch);
                outcomes.Add(new{Reason=reason,Controlled="Only capability/readiness completion timing; actual production request and cancellation paths.",LateReadyAccepted=lateReadyAccepted,
                    Count=editor.Session.Document.Elements.Count,Undo=editor.Session.UndoCount,Epoch=host.EditingSessionVersion,Editing=host.IsEditing,Lease=host.IsKeyboardIsolationActive,Visible=host.IsVisible,
                    Foreground=(long)GetForegroundWindow(),Receiver=(long)receiverHandle});
                snapshot("live-preparation-cancelled-"+reason);
            }
        }
        finally
        {
            SetField(session,"_sessionCaptureProbe",originalProbe);Invoke(session,"StopLiveEffectsCapture");
            if(external is not null)
            {
                File.WriteAllText(Path.Combine(run,"external-exit"),"exit");
                try{Wait(()=>external.HasExited,"owned preparation receiver cleanup",2);}
                finally{if(!external.HasExited){external.Kill();external.WaitForExit(2000);}external.Dispose();}
            }
            File.WriteAllText(Path.Combine(run,"live-preparation-cancel-metrics.json"),JsonSerializer.Serialize(outcomes,new JsonSerializerOptions{WriteIndented=true}));
        }
    }
    private static T? Field<T>(object target,string name)=>(T?)target.GetType().GetField(name,Private)!.GetValue(target);
    private static void SetField(object target,string name,object? value)=>target.GetType().GetField(name,Private)!.SetValue(target,value);
    private static object? Invoke(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Private)!.Invoke(target,args);
    private static void Await(Task task,string operation,int seconds){Wait(()=>task.IsCompleted,operation,seconds);task.GetAwaiter().GetResult();}
    private static void Wait(Func<bool> condition,string operation,int seconds){long start=Stopwatch.GetTimestamp();while(!condition()){if(Stopwatch.GetElapsedTime(start).TotalSeconds>=seconds)throw new TimeoutException(operation);Pump(10);}}
    private static void Pump(int milliseconds){var frame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=(_,_)=>frame.Continue=false;try{timer.Start();Dispatcher.PushFrame(frame);}finally{timer.Stop();}}
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]private static extern bool AllowSetForegroundWindow(uint process);
}
