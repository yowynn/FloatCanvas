using System.Runtime.InteropServices;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

/// <summary>Thread-affine system input delegation. The foreground HWND remains a real external window.</summary>
internal sealed class SystemOverlayInput : IDisposable
{
    private nint native;
    internal nint Owner { get; }
    internal nint Target { get; private set; }
    internal bool Requested=>Status.Requested!=0;
    internal bool Acknowledged=>Status.Acknowledged!=0;
    internal bool WaitingForEntryKeys=>Status.Waiting!=0;
    private InputStatus Status { get {Check(FcSystemGetStatus(native,out var status));return status;} }
    internal SystemOverlayInput()
    {Check(FcSystemCreate(-32000,-32000,1,1,out native,out var owner));Owner=owner;}
    internal void SetSafetyJournal(string path)=>Check(FcSystemSafetyJournal(native,path));
    internal void SetSafetyEnabled(bool enabled)=>Check(FcSystemSafetyPolicy(native,enabled));
    internal void Begin(nint foreground,nint receiver)
    {
        Target=foreground;
        try
        {
            Check(FcSystemReceiver(native,receiver));
            Check(FcSystemBegin(native,foreground));
        }
        catch{Target=0;throw;}
    }
    internal void End(){if(native==0)return;Check(FcSystemEnd(native));Target=0;}
    internal void SetReceiver(nint receiver)=>Check(FcSystemReceiver(native,receiver));
    internal void FocusCanvas(nint canvas)=>Check(FcSystemFocusCanvas(native,canvas));
    internal bool IsCurrentChange(nuint generation)=>Status.Generation==unchecked((uint)generation);
    internal bool OwnsInput
    {
        get
        {
            var status=Status;
            var foreground=GetForegroundWindow();
            return status.Requested!=0 && Target!=0 &&
                (foreground==Target || status.Pid==Environment.ProcessId) &&
                GetWindowThreadProcessId(foreground,out uint pid)!=0 && pid==status.Pid;
        }
    }
    internal static nint CreateWindow(uint extended,string name,string title,uint style,int x,int y,int width,int height,nint parameter)
    {
        nint result=FcSystemCreateHostedWindow(extended,name,title,style,x,y,width,height,parameter);
        return result;
    }
    public void Dispose(){if(native==0)return;End();FcSystemDestroy(native);native=0;}
    private static void Check(int result) { if(result<0)throw new SystemOverlayException(result); }
    [StructLayout(LayoutKind.Sequential)] private struct InputStatus {public uint Generation,Pid;public int Requested,Acknowledged,Waiting,HResult;}
    [DllImport("FloatCanvas.SystemOverlay.dll",ExactSpelling=true)] private static extern int FcSystemCreate(int x,int y,int width,int height,out nint host,out nint window);
    [DllImport("FloatCanvas.SystemOverlay.dll",ExactSpelling=true)] private static extern int FcSystemReceiver(nint host,nint receiver);
    [DllImport("FloatCanvas.SystemOverlay.dll",ExactSpelling=true,CharSet=CharSet.Unicode)] private static extern int FcSystemSafetyJournal(nint host,string path);
    [DllImport("FloatCanvas.SystemOverlay.dll",ExactSpelling=true)] private static extern int FcSystemSafetyPolicy(nint host,[MarshalAs(UnmanagedType.Bool)] bool enabled);
    [DllImport("FloatCanvas.SystemOverlay.dll",ExactSpelling=true)] private static extern int FcSystemFocusCanvas(nint host,nint canvas);
    [DllImport("FloatCanvas.SystemOverlay.dll",ExactSpelling=true)] private static extern int FcSystemBegin(nint host,nint foreground);
    [DllImport("FloatCanvas.SystemOverlay.dll",ExactSpelling=true)] private static extern int FcSystemEnd(nint host);
    [DllImport("FloatCanvas.SystemOverlay.dll",ExactSpelling=true)] private static extern int FcSystemGetStatus(nint host,out InputStatus status);
    [DllImport("FloatCanvas.SystemOverlay.dll",ExactSpelling=true)] private static extern void FcSystemDestroy(nint host);
    [DllImport("FloatCanvas.SystemOverlay.dll",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern nint FcSystemCreateHostedWindow(uint extended,string name,string title,uint style,int x,int y,int width,int height,nint parameter);
}
