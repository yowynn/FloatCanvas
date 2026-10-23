using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using FloatCanvas.Windows;

/// <summary>Opt-in timings on production input/render paths. No forced rendering or per-event file writes.</summary>
internal sealed class SystemOverlayPerformanceRecorder : IDisposable
{
    private readonly OverlayHost host;
    private readonly Window toolbar;
    private readonly HwndSource toolbarSource;
    private readonly string output;
    private readonly Func<bool> ownsInput;
    private readonly List<Sample> samples=[];
    private readonly List<Phase> phases=[];
    private readonly List<FrameSubmission> frames=[];
    private readonly List<PointerStart> pointers=[];
    private readonly Dictionary<InputEventArgs,long> wpfInputs=[];
    private string? phase;
    private long phaseFrames,phaseLayouts,layouts,toolbarInput,toolbarSequence,submittedToolbarSequence;
    private double toolbarQueueAge;
    private long phasePointerMoves,phaseLeftMoves,phaseFramesWithoutPointer;
    private readonly Dictionary<uint,int> phasePointerOrigins=[];
    private bool disposed;
    private sealed record Sample(string Phase,string Kind,double Milliseconds,uint Origin=0);
    private sealed record Phase(string Name,long CanvasFrames,long Layouts,long PointerMoves,long LeftMoves,long FramesWithoutPointer,Dictionary<uint,int> Origins);
    private sealed record PointerStart(long Timestamp,double QueueAge,uint Origin);
    private sealed record FrameSubmission(int PhaseIndex,string Phase,long StartedQpc,long CompletedQpc);

    internal SystemOverlayPerformanceRecorder(OverlayHost host,Window toolbar,string directory,Func<bool> ownsInput)
    {
        this.host=host;this.toolbar=toolbar;
        this.ownsInput=ownsInput;
        output=Path.Combine(directory,$"performance-{Environment.ProcessId}.json");
        toolbarSource=HwndSource.FromHwnd(new WindowInteropHelper(toolbar).EnsureHandle());
        toolbarSource.AddHook(ToolbarMessage);
        host.PointerMessageObserved+=PointerObserved;host.FrameSubmitted+=FrameSubmitted;
        InputManager.Current.PreProcessInput+=BeforeWpfInput;
        InputManager.Current.PostProcessInput+=AfterWpfInput;
        toolbar.LayoutUpdated+=LayoutUpdated;
    }
    internal bool Begin(string name)
    {
        if(!ownsInput())return false;
        EndPhase();phase=name;phaseFrames=host.PresentedFrameCount;phaseLayouts=layouts;
        phasePointerMoves=phaseLeftMoves=phaseFramesWithoutPointer=0;phasePointerOrigins.Clear();
        return true;
    }
    private void EndPhase()
    {
        if(phase is not null)phases.Add(new(phase,host.PresentedFrameCount-phaseFrames,layouts-phaseLayouts,phasePointerMoves,phaseLeftMoves,phaseFramesWithoutPointer,new(phasePointerOrigins)));
        phase=null;pointers.Clear();wpfInputs.Clear();toolbarInput=0;
    }
    private void PointerObserved(OverlayPointerMessageDiagnostic input)
    {
        if(phase=="canvas")
        {
            phasePointerMoves++;
            if((input.WParam&1)!=0)phaseLeftMoves++;
            phasePointerOrigins[input.SourceOriginId]=phasePointerOrigins.GetValueOrDefault(input.SourceOriginId)+1;
        }
        if(phase!="canvas" || !ownsInput() ||
            !(input.Message is 0x201 or 0x202 || input.Message==0x200 && (input.WParam&1)!=0))return;
        pointers.Add(new(input.Timestamp,QueueAge(input.NativeMessageTime),input.SourceOriginId));
    }
    private void FrameSubmitted(OverlayFrameTiming frame)
    {
        if(phase is null)return;
        if(!frame.Succeeded)throw new InvalidOperationException("Performance probe observed a failed canvas frame.");
        frames.Add(new(phases.Count,phase,frame.StartedTimestamp,frame.CompletedTimestamp));
        samples.Add(new(phase,"canvas-draw-and-submit",Elapsed(frame.StartedTimestamp,frame.CompletedTimestamp)));
        foreach(var part in frame.Outputs)
        {
            samples.Add(new(phase,"canvas-draw-and-flush",part.DrawAndFlushMilliseconds));
            samples.Add(new(phase,"native-pixel-submit",part.PresentMilliseconds));
        }
        if(phase!="canvas")return;
        if(pointers.Count==0)phaseFramesWithoutPointer++;
        foreach(var input in pointers)
        {
            double processing=Elapsed(input.Timestamp,frame.CompletedTimestamp);
            samples.Add(new(phase,"message-entry-to-submit",processing,input.Origin));
            if(input.QueueAge is >=0 and <=1000)
                samples.Add(new(phase,"message-queue-to-submit",input.QueueAge+processing,input.Origin));
        }
        pointers.Clear();
    }
    private void BeforeWpfInput(object sender,PreProcessInputEventArgs args)
    {
        if(phase is not null && args.StagingItem.Input is MouseEventArgs input)
            wpfInputs[input]=Stopwatch.GetTimestamp();
    }
    private void AfterWpfInput(object sender,ProcessInputEventArgs args)
    {
        if(wpfInputs.Remove(args.StagingItem.Input,out long started) && phase is not null)
            samples.Add(new(phase,"wpf-mouse-processing",Elapsed(started,Stopwatch.GetTimestamp())));
    }
    private void LayoutUpdated(object? sender,EventArgs args)=>layouts++;
    private nint ToolbarMessage(nint window,int message,nint wp,nint lp,ref bool handled)
    {
        if(phase!="toolbar")return 0;
        if(message==0x200 && ((long)wp&1)!=0)
        {toolbarInput=Stopwatch.GetTimestamp();toolbarQueueAge=QueueAge(GetMessageTime());toolbarSequence++;}
        if(message==0x47 && lp!=0 && toolbarInput!=0 && submittedToolbarSequence!=toolbarSequence)
        {
            var position=Marshal.PtrToStructure<WindowPosition>(lp);
            if((position.Flags&2)!=0)return 0; // SWP_NOMOVE
            double elapsed=Elapsed(toolbarInput,Stopwatch.GetTimestamp());
            samples.Add(new(phase,"toolbar-message-to-position",elapsed));
            if(toolbarQueueAge is >=0 and <=1000)
                samples.Add(new(phase,"toolbar-queue-to-position",toolbarQueueAge+elapsed));
            submittedToolbarSequence=toolbarSequence;
        }
        return 0;
    }
    internal void Save()
    {
        EndPhase();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var groups=samples.GroupBy(value=>(value.Phase,value.Kind)).Select(group=>new
        {
            group.Key.Phase,group.Key.Kind,Count=group.Count(),
            MedianMs=Percentile(group.Select(value=>value.Milliseconds),.5),
            P95Ms=Percentile(group.Select(value=>value.Milliseconds),.95),
            MaximumMs=group.Max(value=>value.Milliseconds),
            Origins=group.GroupBy(value=>value.Origin).Select(origin=>new{Origin=origin.Key,Count=origin.Count()})
        });
        File.WriteAllText(output,JsonSerializer.Serialize(new
        {
            Process=Environment.ProcessId,Resolution=host.Monitors.Select(monitor=>new
            {monitor.DeviceName,monitor.Bounds,monitor.DpiX,monitor.DpiY,DisplayRefreshHz=RefreshRate(monitor.DeviceName)}),
            Phases=phases,Metrics=groups,Samples=samples,FrameSubmissions=frames,QpcFrequency=Stopwatch.Frequency,
            SubmissionCadence=frames.GroupBy(frame=>frame.PhaseIndex).Select(group=>
            {
                var sequence=group.OrderBy(frame=>frame.CompletedQpc).ToArray();
                var intervals=sequence.Zip(sequence.Skip(1),(a,b)=>Elapsed(a.CompletedQpc,b.CompletedQpc)).ToArray();
                double duration=Elapsed(sequence[0].CompletedQpc,sequence[^1].CompletedQpc);
                return new {PhaseIndex=group.Key,Phase=sequence[0].Phase,Frames=sequence.Length,
                    FirstQpc=sequence[0].CompletedQpc,LastQpc=sequence[^1].CompletedQpc,DurationMs=duration,
                    AverageSubmitRateHz=duration>0?(double?)(intervals.Length*1000d/duration):null,
                    IntervalP95Ms=intervals.Length>0?(double?)Percentile(intervals,.95):null,
                    IntervalsAbove33Ms=intervals.Count(interval=>interval>33)};
            }),
            Scope="Native message queue/processing to submission and toolbar position commit. Queue ages have millisecond resolution. Not input-to-photon, physical input isolation, or measured DWM/display FPS. No rendering is forced by this recorder. A useful P95 requires enough actual movement samples."
        },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PERFORMANCE_SAVED {output}");
    }
    private static double Percentile(IEnumerable<double> values,double percentile)
    {
        double[] ordered=values.Order().ToArray();
        return ordered[(int)Math.Ceiling((ordered.Length-1)*percentile)];
    }
    private static double Elapsed(long start,long end)=>Stopwatch.GetElapsedTime(start,end).TotalMilliseconds;
    private static double QueueAge(int messageTime)=>unchecked((uint)Environment.TickCount64-(uint)messageTime);
    private static uint RefreshRate(string device)
    {var mode=new DisplayMode{Size=220};return EnumDisplaySettingsW(device,-1,ref mode)?mode.Frequency:0;}
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        host.PointerMessageObserved-=PointerObserved;host.FrameSubmitted-=FrameSubmitted;
        toolbarSource.RemoveHook(ToolbarMessage);toolbar.LayoutUpdated-=LayoutUpdated;
        InputManager.Current.PreProcessInput-=BeforeWpfInput;InputManager.Current.PostProcessInput-=AfterWpfInput;
        Save();
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition {public nint Window,After;public int X,Y,Width,Height;public uint Flags;}
    [StructLayout(LayoutKind.Explicit,Size=220,CharSet=CharSet.Unicode)] private struct DisplayMode
    {[FieldOffset(68)] public ushort Size;[FieldOffset(184)] public uint Frequency;}
    [DllImport("user32.dll")] private static extern int GetMessageTime();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string device,int mode,ref DisplayMode settings);
}
