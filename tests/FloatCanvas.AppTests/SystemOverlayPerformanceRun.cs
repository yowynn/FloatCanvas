using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;

/// <summary>Opt-in, bounded workload against our own production canvas and toolbar HWNDs.
/// Posted messages measure the render pipeline, not hardware isolation or display latency.</summary>
internal sealed class SystemOverlayPerformanceRun : IDisposable
{
    private readonly DispatcherTimer ready = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Application app;
    private readonly OverlayHost host;
    private readonly Window toolbar;
    private readonly SystemOverlayPerformanceRecorder recorder;
    private readonly string directory;
    private readonly bool nativeDialogOnly;
    private readonly bool interactiveDialog=Environment.GetCommandLineArgs().Any(value=>value is "--system-overlay-native-dialog-interaction" or "--system-overlay-document-dialog-interaction");
    private readonly Action enter;
    private readonly Func<bool,Task<string?>> documentDialog;
    private readonly bool productionDialog=Environment.GetCommandLineArgs().Any(value=>value is "--system-overlay-document-dialog" or "--system-overlay-document-dialog-interaction");
    private readonly Stopwatch waiting=Stopwatch.StartNew();
    private bool entryRequested;
    private readonly List<object> results = [];
    private bool disposed;

    internal SystemOverlayPerformanceRun(Application app, OverlayHost host, Window toolbar,
        SystemOverlayPerformanceRecorder recorder, string directory,bool nativeDialogOnly,Action enter,Func<bool,Task<string?>> documentDialog)
    {
        this.app=app; this.host=host; this.toolbar=toolbar; this.recorder=recorder; this.directory=directory;
        this.nativeDialogOnly=nativeDialogOnly;
        this.enter=enter;
        this.documentDialog=documentDialog;
        ready.Tick += RunWhenReady;
        ready.Start();
    }

    private async void RunWhenReady(object? sender, EventArgs args)
    {
        nint foreground=GetForegroundWindow();
        GetWindowThreadProcessId(foreground,out uint process);
        bool selected=uint.TryParse(Environment.GetEnvironmentVariable("FLOATCANVAS_SYSTEM_OVERLAY_TEST_TARGET_PID"),out uint expected)&&expected!=Environment.ProcessId;
        if(!host.HasEditingInput)
        {
            if(waiting.Elapsed>(interactiveDialog?TimeSpan.FromSeconds(45):TotalWait)){ready.Stop();Write("invalid-interrupted",new {Expected=expected,Actual=process});app.Shutdown(2);}
            else if(selected&&process==expected&&!entryRequested&&waiting.Elapsed>TimeSpan.FromSeconds(1))
            {entryRequested=true;enter();}
            return;
        }
        ready.Stop();
        if (!selected || expected!=process)
        {
            Write("rejected-target",new {Expected=expected,Actual=process});
            host.SetEditing(false);app.Shutdown(2);
            return;
        }
        int exitCode=0;
        try
        {
            await Task.Delay(500);
            RequireLease(foreground);
            if(!nativeDialogOnly)
            {
            var input=host.Diagnostics[0];
            // Warm the original editor/render path before collecting a simple stroke.
            await Stroke(input.InputHandle,input.Bounds.Width,input.Bounds.Height,30);
            await Task.Delay(300);
            if (!recorder.Begin("canvas")) throw new InvalidOperationException("Missing editing input");
            var pace=await Stroke(input.InputHandle,input.Bounds.Width,input.Bounds.Height,240);
            await Task.Delay(300);
            recorder.Save();
            Write("paced-own-message-stroke",new {pace,ForegroundPreserved=GetForegroundWindow()==foreground,host.HasEditingInput});
            RequireLease(foreground);

            // Exercise the same native position operation as OverlayWindowDrag, with
            // no fake mouse state. This checks repaint independence, not input FPS.
            nint handle=new WindowInteropHelper(toolbar).Handle;
            if(!GetWindowRect(handle,out var initial))throw new InvalidOperationException("Missing toolbar bounds");
            recorder.Begin("toolbar-position");
            var moves=new List<double>();
            for(int i=0;i<180;++i)
            {
                long start=Stopwatch.GetTimestamp();
                if(!SetWindowPos(handle,0,initial.Left+(int)(80*Math.Sin(i*.05)),initial.Top+40,0,0,0x215))
                    throw new System.ComponentModel.Win32Exception();
                moves.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                await Task.Delay(16);
            }
            SetWindowPos(handle,0,initial.Left,initial.Top,0,0,0x215);
            await Task.Delay(200);
            recorder.Save();
            Write("toolbar-position-calls",new {Count=moves.Count,P95Ms=Percentile(moves,.95),MaximumMs=moves.Max(),
                ForegroundPreserved=GetForegroundWindow()==foreground,Scope="Native position calls, not injected mouse/WPF processing or display FPS"});
            RequireLease(foreground);
            }
            await NativeFileDialog(foreground,false);
            foreground=GetForegroundWindow();
            RequireLease(foreground);
            await NativeFileDialog(foreground,true);
            if(productionDialog)
            {
                if(!interactiveDialog)
                {
                    await SwitchReceiver();
                    await NativeFileDialog(GetForegroundWindow(),false);
                    await NativeFileDialog(GetForegroundWindow(),true);
                }
                await NativeDialogLifetime();
            }
            else await TargetWindowClose(foreground);
        }
        catch(OperationCanceledException error){exitCode=2;Write("invalid-interrupted",new {Error=error.Message});}
        catch(Exception error){exitCode=1;Write("failed",new {Error=error.ToString()});}
        finally
        {
            host.SetEditing(false);
            Write("released",new {host.IsEditing,host.HasEditingInput,Foreground=GetForegroundWindow().ToInt64()});
            app.Shutdown(exitCode);
        }
    }
    private static readonly TimeSpan TotalWait=TimeSpan.FromSeconds(15);

    private static async Task<object> Stroke(nint window,int width,int height,int count)
    {
        // Only the verified HWND owned by this host receives these messages. No SendInput,
        // hooks, foreground changes, or messages to the external foreground application.
        GetWindowThreadProcessId(window,out uint owner);
        if(owner!=Environment.ProcessId)throw new InvalidOperationException("Stroke target is not ours");
        var produced=new List<double>();
        await Task.Run(async ()=>
        {
            var clock=Stopwatch.StartNew();
            nint Point(int step)=>(nint)((uint)(ushort)(width/5+step*3)|((uint)(ushort)(height/3+Math.Round(90*Math.Sin(step*.045)))<<16));
            if(!PostMessageW(window,0x201,1,Point(0)))throw new System.ComponentModel.Win32Exception();
            for(int step=1;step<=count;++step)
            {
                double remaining=step*(1000d/60)-clock.Elapsed.TotalMilliseconds;
                if(remaining>0)await Task.Delay(TimeSpan.FromMilliseconds(remaining));
                produced.Add(clock.Elapsed.TotalMilliseconds);
                if(!PostMessageW(window,0x200,1,Point(step)))throw new System.ComponentModel.Win32Exception();
            }
            if(!PostMessageW(window,0x202,0,Point(count)))throw new System.ComponentModel.Win32Exception();
        });
        var intervals=produced.Zip(produced.Skip(1),(a,b)=>b-a).ToArray();
        return new {Count=count,DurationMs=produced.Last(),IntervalMedianMs=Percentile(intervals,.5),
            IntervalP95Ms=Percentile(intervals,.95),Scope="Own posted WM_MOUSE messages at a requested 60 Hz; not physical mouse input"};
    }

    private async Task NativeFileDialog(nint foreground,bool save)
    {
        bool observed=false,preserved=false;
        long session=host.EditingSessionVersion;
        bool interrupted=false;
        string? previousInput=null;
        var closer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(500)};
        var deadline=Stopwatch.StartNew();
        closer.Tick+=(_,_)=>
        {
            nint dialog=0;
            EnumThreadWindows(GetCurrentThreadId(),(window,_)=>
            {
                var name=new StringBuilder(64);GetClassNameW(window,name,name.Capacity);
                if(IsWindowVisible(window)&&name.ToString()=="#32770"){dialog=window;return false;}
                return true;
            },0);
            if(dialog!=0)
            {
                if(interactiveDialog&&!observed)
                {
                    // Make this owned test dialog discoverable by the guarded
                    // automation API. Keep its band, owner and no-activation policy.
                    nint style=GetWindowLongPtrW(dialog,-20);
                    SetWindowLongPtrW(dialog,-20,(style&~(nint)0x80)|0x40000);
                }
                observed=true;
                preserved=host.IsEditing&&host.EditingSessionVersion==session&&!host.HasEditingInput&&
                    host.Diagnostics.All(window=>!IsWindowVisible(window.InputHandle))&&GetAncestor(GetFocus(),2)==dialog;
                nint actual=GetForegroundWindow();GetWindowThreadProcessId(actual,out uint actualProcess);
                nint mouseActivate=SendMessageW(dialog,0x21,unchecked((nuint)foreground),(nint)(1|(0x201<<16)));
                if(previousInput is null)Write("native-file-dialog",new {Save=save,ForegroundPreserved=GetForegroundWindow()==foreground,Foreground=actual.ToInt64(),ForegroundProcess=actualProcess,OwnDialogForeground=actual==dialog,host.IsEditing,host.HasEditingInput,MouseActivateResult=mouseActivate.ToInt64(),
                    Focus=GetFocus().ToInt64(),FocusRoot=GetAncestor(GetFocus(),2).ToInt64(),
                    Band=GetWindowBand(dialog,out uint band)?band:uint.MaxValue,Dialog=dialog.ToInt64(),Scope=productionDialog?
                        "Real OpenProjectAsync/SaveProjectAsync; edit intent retained with overlay input suspended; cancel only":
                        "Own native file dialog focus and mouse-activate policy; cancel only, no file read or write; not physical input acceptance"});
                var input=new StringBuilder(512);GetWindowTextW(GetFocus(),input,input.Capacity);
                string state=JsonSerializer.Serialize(new {Save=save,Focus=GetFocus().ToInt64(),Root=GetAncestor(GetFocus(),2).ToInt64(),Text=input.ToString(),ForegroundPreserved=actual==foreground,host.HasEditingInput});
                if(interactiveDialog&&state!=previousInput)Write("native-dialog-interaction",JsonDocument.Parse(state).RootElement);
                previousInput=state;
                interrupted=!host.IsEditing;
                if(!interactiveDialog||interrupted||deadline.Elapsed>TimeSpan.FromSeconds(60))
                {PostMessageW(dialog,0x10,0,0);closer.Stop();}
            }
            else if(deadline.Elapsed>TimeSpan.FromSeconds(10)){closer.Stop();throw new TimeoutException("Native file dialog was not observed");}
        };
        closer.Start();
        try
        {
            string folder=Path.Combine(directory,"dialog-files");Directory.CreateDirectory(folder);
            string source=Path.Combine(folder,"FloatCanvas-owned-input-check.txt");
            File.WriteAllText(source,"Owned native picker input fixture. No user document.\n");
            Microsoft.Win32.FileDialog dialog=save
                ? new Microsoft.Win32.SaveFileDialog {Title="FloatCanvas native save interaction",FileName="FloatCanvas-new-output.txt",InitialDirectory=folder}
                : new Microsoft.Win32.OpenFileDialog {Title="FloatCanvas native open interaction",CheckFileExists=true,InitialDirectory=folder};
            bool? result=null;
            string? selectedFile=null;
            if(productionDialog)selectedFile=await documentDialog(save);
            else result=SessionWindowCoordinator.ShowNativeModal(toolbar,()=>dialog.ShowDialog(toolbar));
            await AwaitResume(session);
            if(!toolbar.IsEnabled)throw new InvalidOperationException("Document command left the toolbar disabled");
            Write("native-file-dialog-returned",new {Save=save,Result=result,FileName=selectedFile??dialog.FileName,ForegroundPreserved=GetForegroundWindow()==foreground,host.HasEditingInput});
            if(interactiveDialog&&!interrupted&&((!productionDialog&&result!=true)||!string.Equals(Path.GetDirectoryName(selectedFile??dialog.FileName),folder,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Native picker did not return a selected path in the owned test directory");
            if(interactiveDialog&&productionDialog)
            {
                var loaded=await new FloatCanvas.Storage.ProjectStore().LoadAsync(selectedFile!);
                if(loaded.Document.Elements.Count!=1||loaded.Document.Elements[0].Width!=123)
                    throw new InvalidOperationException("The selected or saved project did not retain the owned canvas");
                Write("native-document-roundtrip",new {Save=save,FileName=selectedFile,Elements=loaded.Document.Elements.Count,Width=loaded.Document.Elements[0].Width});
            }
        }
        finally{closer.Stop();}
        if(interrupted)throw new InvalidOperationException("Native document command exited editing");
        if(!observed||!preserved)throw new InvalidOperationException("Native file dialog lost its edit intent, focused control, or input suspension");
    }

    private async Task NativeDialogLifetime()
    {
        long session=host.EditingSessionVersion;
        using(var outer=host.BeginNativeDialog())
        {
            using(host.BeginNativeDialog()){}
            if(!host.IsEditing||host.HasEditingInput)throw new InvalidOperationException("Nested native scope resumed input early");
        }
        await AwaitResume(session);
        try {SessionWindowCoordinator.ShowNativeModal(toolbar,()=>throw new IOException("Owned native modal failure"));}
        catch(IOException){}
        await AwaitResume(session);
        SessionWindowCoordinator.ShowNativeModal(toolbar,()=>{host.SetEditing(false);return false;});
        await Task.Delay(200);
        if(host.IsEditing||host.HasEditingInput||host.Diagnostics.Any(window=>IsWindowVisible(window.InputHandle)))
            throw new InvalidOperationException("An expired native modal scope resurrected editing after explicit exit");
        Write("native-dialog-lifetime",new {Nested=true,ExceptionReturn=true,ExplicitExit=true,host.IsEditing,host.HasEditingInput});
    }
    private async Task SwitchReceiver()
    {
        if(!uint.TryParse(Environment.GetEnvironmentVariable("FLOATCANVAS_SYSTEM_OVERLAY_TEST_SECOND_PID"),out uint expected))return;
        host.SetEditing(false);
        Write("await-second-receiver",new {Expected=expected});
        var timeout=Stopwatch.StartNew();
        uint actual;
        do
        {
            GetWindowThreadProcessId(GetForegroundWindow(),out actual);
            if(actual==expected)break;
            await Task.Delay(50);
        }while(timeout.Elapsed<TimeSpan.FromSeconds(15));
        if(actual!=expected)throw new OperationCanceledException("Second owned receiver was not selected");
        enter();
        await AwaitResume(host.EditingSessionVersion);
        GetWindowThreadProcessId(GetForegroundWindow(),out actual);
        if(actual!=expected)throw new OperationCanceledException("Second receiver entry was interrupted");
        Write("second-receiver-ready",new {Expected=expected,Actual=actual,host.IsEditing,host.HasEditingInput});
    }
    private async Task AwaitResume(long session)
    {
        var timeout=Stopwatch.StartNew();
        while(host.IsEditing&&!host.HasEditingInput&&timeout.Elapsed<TimeSpan.FromSeconds(3))await Task.Delay(20);
        if(!host.HasEditingInput||host.EditingSessionVersion!=session)
            throw new InvalidOperationException("Native modal scope did not restore the same editing session");
    }

    private async Task TargetWindowClose(nint target)
    {
        RequireLease(target);
        GetWindowThreadProcessId(target,out uint process);
        var title=new StringBuilder(128);GetWindowTextW(target,title,title.Capacity);
        using var receiver=Process.GetProcessById((int)process);
        if(title.ToString()!="FloatCanvas System Input Receiver"||receiver.MainModule?.FileName!=Environment.ProcessPath)
            throw new InvalidOperationException("Lifecycle target is not our receiver");
        PostMessageW(target,0x10,0,0);
        var timer=Stopwatch.StartNew();
        while((IsWindow(target)||host.IsEditing)&&timer.Elapsed<TimeSpan.FromSeconds(3))await Task.Delay(20);
        bool hidden=host.Diagnostics.All(window=>!IsWindowVisible(window.InputHandle));
        Write("target-close-release",new {TargetClosed=!IsWindow(target),host.IsEditing,host.HasEditingInput,InputsHidden=hidden,Milliseconds=timer.Elapsed.TotalMilliseconds});
        if(IsWindow(target)||host.IsEditing||host.HasEditingInput||!hidden)
            throw new InvalidOperationException("Target close did not release editing and hide interactive surfaces");
    }

    private void RequireLease(nint foreground)
    {if(disposed||!host.HasEditingInput||GetForegroundWindow()!=foreground)throw new OperationCanceledException("Foreground or editing ownership changed during measurement");}
    private void Write(string name,object data)
    {
        results.Add(new {Event=name,Data=data});
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,$"workload-{Environment.ProcessId}.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
    }
    private static double Percentile(IEnumerable<double> values,double fraction)
    {var sorted=values.Order().ToArray();return sorted[(int)Math.Ceiling((sorted.Length-1)*fraction)];}
    public void Dispose(){disposed=true;ready.Stop();ready.Tick-=RunWhenReady;}
    [StructLayout(LayoutKind.Sequential)] private struct Rect {public int Left,Top,Right,Bottom;}
    private delegate bool EnumWindow(nint window,nint parameter);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool PostMessageW(nint window,uint message,nuint wp,nint lp);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out Rect rect);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread,EnumWindow callback,nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassNameW(nint window,StringBuilder text,int maximum);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowBand(nint window,out uint band);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window,uint flags);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window,uint message,nuint wp,nint lp);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(nint window,StringBuilder text,int count);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window,int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window,int index,nint value);
}
