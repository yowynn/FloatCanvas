using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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
using SkiaSharp;

/// <summary>Opt-in real production live-filter first-frame and restart regression.
/// Only the owned overlay's single-pixel samples and timing metadata are retained.</summary>
internal static class LiveEffectReadinessFixture
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;

    internal static void Run(Action<string,bool> check,DesktopSession session,OverlayHost host,EditorController editor,
        Window toolbar,string run,Action<string> snapshot)
    {
        var monitor=host.PrimaryMonitor;var native=host.Diagnostics.Single();
        check("readiness uses the actual output and the production editing lease",native.Bounds==monitor.Bounds && native.Bounds.Width>640 && host.IsEditing && host.IsKeyboardIsolationActive);
        long epoch=host.EditingSessionVersion;int leaseEnds=0,focusLosses=0,sequence=0;
        Action lost=()=>focusLosses++;Action<KeyboardLeaseEndReason> released=_=>leaseEnds++;
        host.EditingFocusLost+=lost;host.KeyboardIsolationReleased+=released;
        string childDirectory=Path.Combine(run,"background");Directory.CreateDirectory(childDirectory);
        Process? background=null;Action<SKSurface>? observe=null;
        var phases=new List<ProbePhase>();var pixels=new List<PixelSample>();
        var sources=new List<(LiveDesktopCapture Source,Action Handler)>();
        var readyEvents=new ConcurrentQueue<ReadyNotice>();var cleanupErrors=new List<string>();
        long latestNotifiedFrameTimestamp=0;
        ProbePhase? active=null;bool usesSessionExclusion=false;
        try
        {
            editor.Cancel();editor.Session.Execute("isolated readiness setup",document=>document.Elements.Clear());editor.Session.SetSelection([]);
            var start=new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Missing owned test executable."))
                {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=run};
            foreach(string arg in new[]{"--session-capture-background-helper","--background-directory",childDirectory,
                "--background-left",(monitor.WorkArea.Left+90).ToString(CultureInfo.InvariantCulture),
                "--background-top",(monitor.WorkArea.Top+280).ToString(CultureInfo.InvariantCulture)})start.ArgumentList.Add(arg);
            background=Process.Start(start) ?? throw new InvalidOperationException("Cannot start owned readiness background.");
            string readyPath=Path.Combine(childDirectory,"ready.json");Wait(()=>File.Exists(readyPath)||background.HasExited,"owned readiness background metadata",5);
            if(background.HasExited)throw new InvalidOperationException("Owned readiness background exited before publishing its window.");
            var ready=JsonSerializer.Deserialize<BackgroundReady>(CaptureFixtureIpc.ReadAllText(readyPath))!;
            GetWindowThreadProcessId((nint)ready.Handle,out uint childPid);
            check("readiness background is a visible independent-PID window",ready.Process==background.Id && childPid==background.Id &&
                childPid!=Environment.ProcessId && IsWindowVisible((nint)ready.Handle));
            OverlayCoordinates.RaiseToolWindow(native.DisplayHandle);OverlayCoordinates.RaiseToolWindow(native.InputHandle);
            OverlayCoordinates.RaiseToolWindow(new WindowInteropHelper(toolbar).Handle);
            var region=new RectD(ready.Left+75,ready.Top+65,180,80);
            int x=(int)(region.Center.X-native.Bounds.Left),y=(int)(region.Center.Y-native.Bounds.Top);
            observe=surface=>
            {
                ObserveSource();
                if(active is not {} phase)return;
                using var pixel=new SKBitmap(new SKImageInfo(1,1,SKColorType.Bgra8888,SKAlphaType.Premul));
                if(!surface.ReadPixels(pixel.Info,pixel.GetPixels(),pixel.RowBytes,x,y))throw new InvalidOperationException("Cannot read the owned readiness pixel.");
                var color=pixel.GetPixel(0,0);long now=Stopwatch.GetTimestamp();
                bool present=editor.Session.Document.Elements.Any(element=>element.Kind is ElementKind.LiveBlur or ElementKind.LivePixelate);
                pixels.Add(new(phase.Name,now,color.Red,color.Green,color.Blue,color.Alpha,present,
                    Field<object>(session,"_desktopBackdrop") is not null,host.IsEditing,host.IsKeyboardIsolationActive,
                    host.EditingSessionVersion,editor.Session.SceneRevision,editor.Session.UndoCount,Interlocked.Read(ref latestNotifiedFrameTimestamp)));
                if(phase.FirstExpectedTimestamp==0)
                {
                    if(present && IsOpaqueGray(color))phase.GrayFramesBeforeExpected++;
                    if(present && Matches(color,phase.Color))phase.FirstExpectedTimestamp=now;
                }
            };
            host.Draw+=observe;
            foreach(var kind in new[]{ImageEffectKind.Blur,ImageEffectKind.Pixelate})
            {
                active=null;
                var old=Field<LiveDesktopCapture>(session,"_liveDesktopCapture");
                Invoke(session,"StopLiveEffectsCapture");if(old is not null)Await(old.Completion,"previous owned readiness capture stop",5);
                editor.Cancel();editor.Session.Execute("fresh readiness filter",document=>document.Elements.Clear());editor.Session.SetSelection([]);
                string prefix=kind==ImageEffectKind.Blur?"blur":"pixelate";
                var creation=Begin(prefix+"-create","Red","Create");Command(creation);
                Await((Task)Invoke(session,"AddDesktopEffectAsync",region,kind,10d,0xFFEF4444u)!,"production "+prefix+" creation",10);
                Wait(()=>creation.FirstExpectedTimestamp!=0,"first valid "+prefix+" red pixel",10);End(creation);
                var source=Field<LiveDesktopCapture>(session,"_liveDesktopCapture");
                usesSessionExclusion=source?.UsesSessionWindowExclusion==true;
                check(prefix+" first valid color uses actual WGC session exclusion",usesSessionExclusion);
                if(!usesSessionExclusion)throw new NotSupportedException("Readiness requires actual WGC session exclusion; no fallback is counted as success.");
                // No background command is issued here. A matching already accepted ROI
                // must be reusable even if a static desktop supplies no fresh WGC frame.
                if(kind==ImageEffectKind.Blur)
                {
                    Pump(150);
                    int beforeCachedUndo=editor.Session.UndoCount;
                    var cached=Begin("blur-cached-same-roi","Red","CachedCreate");
                    Await((Task)Invoke(session,"AddDesktopEffectAsync",region,kind,10d,0xFFEF4444u)!,"same-ROI cached live creation without a desktop change",2);
                    Wait(()=>cached.FirstExpectedTimestamp!=0,"cached same-ROI red output",2);End(cached);
                    check("an accepted static same-ROI backdrop permits immediate next creation",editor.Session.Document.Elements.Count==2 && editor.Session.UndoCount==beforeCachedUndo+1 &&
                        Stopwatch.GetElapsedTime(cached.BoundaryTimestamp,cached.EndTimestamp).TotalSeconds<2);
                    var undone=Begin("blur-after-undo","Red","Undo");
                    editor.Session.Undo();Pump(50);
                    Wait(()=>undone.FirstExpectedTimestamp!=0,"valid cached filter pixels after annotation undo",2);End(undone);
                    check("undoing a same-canvas edit does not flash a gray filter",undone.GrayFramesBeforeExpected==0);
                }
                int count=editor.Session.Document.Elements.Count,undo=editor.Session.UndoCount;long revision=editor.Session.SceneRevision;
                for(int restart=1;restart<=2;restart++)
                {
                    active=null;Invoke(session,"ToggleCanvasVisibility");
                    Wait(()=>!host.IsVisible && Field<LiveDesktopCapture>(session,"_liveDesktopCapture") is null,"hidden readiness source stop");
                    source= sources.Last().Source;Await(source.Completion,"hidden readiness worker completion",5);
                    var shown=Begin(prefix+"-show-"+restart,restart==1?"Blue":"Red","Show");
                    Command(shown);active=null;
                    shown.BoundaryTimestamp=Stopwatch.GetTimestamp();active=shown;
                    Invoke(session,"ToggleCanvasVisibility");Wait(()=>shown.FirstExpectedTimestamp!=0,"first valid restarted "+prefix+" pixel",10);End(shown);
                }
                for(int pulse=1;pulse<=6;pulse++)
                {
                    var change=Begin(prefix+"-pulse-"+pulse,pulse%2==1?"Blue":"Red","Pulse");
                    Command(change);Wait(()=>change.FirstExpectedTimestamp!=0,"live "+prefix+" color follow",3);End(change);
                }
                check(prefix+" pixel updates preserve the same single object and undo history",count==1 && editor.Session.Document.Elements.Count==count &&
                    editor.Session.SceneRevision==revision && editor.Session.UndoCount==undo);
            }
            active=null;snapshot("live-filter-readiness-finished");
            check("live filter creation never presents an opaque gray placeholder",phases.Where(phase=>phase.Operation is "Create" or "CachedCreate").All(phase=>phase.FirstExpectedTimestamp!=0 && phase.GrayFramesBeforeExpected==0));
            check("live filter hide/show never presents an opaque gray placeholder",phases.Where(phase=>phase.Operation=="Show").All(phase=>phase.FirstExpectedTimestamp!=0 && phase.GrayFramesBeforeExpected==0));
            check("every controlled red/blue transition reaches actual filter pixels",phases.Count==20 && phases.All(phase=>phase.FirstExpectedTimestamp!=0));
            check("readiness preserves toolbar editing epoch and keyboard lease",host.IsEditing && host.IsKeyboardIsolationActive && host.EditingSessionVersion==epoch &&
                toolbar.IsVisible && leaseEnds==0 && focusLosses==0 && pixels.All(pixel=>pixel.Editing && pixel.Lease && pixel.Epoch==epoch));

            ProbePhase Begin(string name,string color,string operation)
            {var phase=new ProbePhase {Name=name,Color=color,Operation=operation,BoundaryTimestamp=Stopwatch.GetTimestamp()};phases.Add(phase);active=phase;return phase;}
            void End(ProbePhase phase){phase.EndTimestamp=Stopwatch.GetTimestamp();active=null;}
            void Command(ProbePhase phase)
            {
                int requested=++sequence;phase.CommandTimestamp=Stopwatch.GetTimestamp();
                AtomicJson(Path.Combine(childDirectory,"command.json"),new BackgroundCommand(requested,phase.Color,false));
                BackgroundAck? ack=null;
                Wait(()=>
                {
                    string path=Path.Combine(childDirectory,"ack.json");if(!File.Exists(path))return false;
                    ack=JsonSerializer.Deserialize<BackgroundAck>(CaptureFixtureIpc.ReadAllText(path));return ack?.Sequence==requested && ack.Color==phase.Color;
                },"owned readiness color acknowledgement",2);
                phase.AppliedTimestamp=ack!.AppliedTimestamp;
            }
            void ObserveSource()
            {
                var current=Field<LiveDesktopCapture>(session,"_liveDesktopCapture");
                if(current is null || sources.Any(item=>ReferenceEquals(item.Source,current)))return;
                int id=sources.Count+1;
                Action handler=()=>
                {
                    // Snapshot metadata without claiming/disposing the production frame.
                    // Reflection keeps this same harness executable against the frozen baseline.
                    ReadyNotice notice;
                    try
                    {
                        lock(Field<object>(current,"_gate")!)
                        {
                            var frame=Field<LiveDesktopFrame>(current,"_latest");
                            notice=new(id,Stopwatch.GetTimestamp(),Volatile.Read(ref active)?.Name,frame?.GeneratedAtTimestamp,
                                Optional<long>(current,"FrameArrivalCount"),Optional<long>(current,"SamplingPassCount"),
                                Optional<int>(current,"SamplingFramesPerSecond"),frame?.Outputs.Select(output=>(object)new
                                {
                                    output.LastPresentTimestamp,
                                    Timing=output.GetType().GetProperty("Timing")?.GetValue(output)
                                }).ToArray(),null);
                        }
                    }
                    catch(Exception error){notice=new(id,Stopwatch.GetTimestamp(),null,null,null,null,null,null,error.ToString());}
                    if(notice.GeneratedTimestamp is {} generated)Interlocked.Exchange(ref latestNotifiedFrameTimestamp,generated);
                    readyEvents.Enqueue(notice);
                };
                sources.Add((current,handler));current.FrameReady+=handler;
            }
        }
        finally
        {
            active=null;if(observe is not null)host.Draw-=observe;
            foreach(var item in sources)item.Source.FrameReady-=item.Handler;
            try{Invoke(session,"StopLiveEffectsCapture");foreach(var item in sources)Await(item.Source.Completion,"owned readiness cleanup",5);}
            catch(Exception error){cleanupErrors.Add(error.ToString());}
            if(background is not null)
            {
                try{AtomicJson(Path.Combine(childDirectory,"command.json"),new BackgroundCommand(++sequence,"",true));Wait(()=>background.HasExited,"owned readiness helper stop",2);}
                catch(Exception error){cleanupErrors.Add(error.ToString());if(!background.HasExited){background.Kill();background.WaitForExit(2000);}}
                finally{background.Dispose();}
            }
            host.EditingFocusLost-=lost;host.KeyboardIsolationReleased-=released;
            var arrivals=readyEvents.ToArray();
            File.WriteAllText(Path.Combine(run,"live-readiness-metrics.json"),JsonSerializer.Serialize(new
            {
                Pixels="Only one own filter pixel per production draw. No desktop image saved.",
                Timing="Cross-process QPC. AppliedTimestamp acknowledges WPF Background assignment, not DWM presentation. FrameReady intervals are observed notifications after subscription, not configured FPS. Optional source metadata is absent on the frozen old production DLL; LatestNotifiedFrameTimestamp correlates a notification, not proof that exact frame was presented.",
                Stopwatch.Frequency,UsesSessionExclusion=usesSessionExclusion,InitialEpoch=epoch,FinalEpoch=host.EditingSessionVersion,LeaseEnds=leaseEnds,FocusLosses=focusLosses,
                Phases=phases.Select(phase=>new{phase.Name,phase.Operation,phase.Color,phase.BoundaryTimestamp,phase.CommandTimestamp,phase.AppliedTimestamp,phase.FirstExpectedTimestamp,
                    phase.EndTimestamp,phase.GrayFramesBeforeExpected,BoundaryToExpectedMs=Elapsed(phase.BoundaryTimestamp,phase.FirstExpectedTimestamp),
                    CommandToExpectedMs=Elapsed(phase.CommandTimestamp,phase.FirstExpectedTimestamp),AppliedToExpectedMs=Elapsed(phase.AppliedTimestamp,phase.FirstExpectedTimestamp)}),
                SourceNotificationIntervals=arrivals.GroupBy(item=>item.Source).Select(group=>new{Source=group.Key,Intervals=Stats(Intervals(group.Select(item=>item.Timestamp)))}),
                PulseCommandToExpected=Stats(phases.Where(phase=>phase.Operation=="Pulse").Select(phase=>Elapsed(phase.CommandTimestamp,phase.FirstExpectedTimestamp)).OfType<double>()),
                Frames=pixels,FrameReady=arrivals,CleanupErrors=cleanupErrors
            },new JsonSerializerOptions{WriteIndented=true}));
        }
    }

    private sealed class ProbePhase
    {
        public string Name {get;init;}="";public string Color {get;init;}="";public string Operation {get;init;}="";
        public long BoundaryTimestamp {get;set;}public long CommandTimestamp {get;set;}public long AppliedTimestamp {get;set;}
        public long FirstExpectedTimestamp {get;set;}public long EndTimestamp {get;set;}public int GrayFramesBeforeExpected {get;set;}
    }
    private sealed record BackgroundReady(int Process,long Handle,int Left,int Top,int Width,int Height);
    private sealed record BackgroundCommand(int Sequence,string Color,bool Stop);
    private sealed record BackgroundAck(int Sequence,string Color,long AppliedTimestamp);
    private sealed record ReadyNotice(int Source,long Timestamp,string? Phase,long? GeneratedTimestamp,long? ArrivalCount,long? SamplingPassCount,int? ConfiguredSamplingCap,object[]? Outputs,string? Error);
    private sealed record PixelSample(string Phase,long Timestamp,byte Red,byte Green,byte Blue,byte Alpha,bool EffectPresent,bool BackdropAvailable,bool Editing,bool Lease,long Epoch,long Revision,int Undo,long LatestNotifiedFrameTimestamp);
    private static bool Matches(SKColor color,string expected)=>color.Alpha>240 && color.Green<35 && (expected=="Red"?color.Red>220 && color.Blue<35:color.Blue>220 && color.Red<35);
    private static bool IsOpaqueGray(SKColor color)=>color.Alpha>240 && Math.Max(color.Red,Math.Max(color.Green,color.Blue))-Math.Min(color.Red,Math.Min(color.Green,color.Blue))<25 && color.Red is >40 and <180;
    private static double? Elapsed(long begin,long end)=>begin>0 && end>=begin?(end-begin)*1000d/Stopwatch.Frequency:null;
    private static IEnumerable<double> Intervals(IEnumerable<long> timestamps){long previous=0;foreach(long now in timestamps){if(previous!=0)yield return(now-previous)*1000d/Stopwatch.Frequency;previous=now;}}
    private static object Stats(IEnumerable<double> values){double[] ordered=values.Order().ToArray();return new{Count=ordered.Length,MinimumMs=ordered.Length==0?(double?)null:ordered[0],MedianMs=ordered.Length==0?(double?)null:ordered[ordered.Length/2],P95Ms=ordered.Length==0?(double?)null:ordered[(int)Math.Floor((ordered.Length-1)*.95)],MaximumMs=ordered.Length==0?(double?)null:ordered[^1]};}
    private static T? Field<T>(object target,string name)=>(T?)target.GetType().GetField(name,Private)!.GetValue(target);
    private static T? Optional<T>(object target,string name) where T:struct=>(T?)target.GetType().GetProperty(name)?.GetValue(target);
    private static object? Invoke(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Private)!.Invoke(target,args);
    private static void AtomicJson(string path,object value)=>CaptureFixtureIpc.WriteJson(path,value);
    private static void Await(Task task,string operation,int seconds=8){Wait(()=>task.IsCompleted,operation,seconds);task.GetAwaiter().GetResult();}
    private static void Wait(Func<bool> condition,string operation,int seconds=8){long start=Stopwatch.GetTimestamp();while(!condition()){if(Stopwatch.GetElapsedTime(start).TotalSeconds>=seconds)throw new TimeoutException(operation);Pump(10);}}
    private static void Pump(int milliseconds){var frame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=(_,_)=>frame.Continue=false;try{timer.Start();Dispatcher.PushFrame(frame);}finally{timer.Stop();}}
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint process);
}
