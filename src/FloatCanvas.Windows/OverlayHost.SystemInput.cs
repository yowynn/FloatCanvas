using static FloatCanvas.Windows.NativeMethods;
using System.Runtime.InteropServices;

namespace FloatCanvas.Windows;

public sealed partial class OverlayHost
{
    private SystemOverlayInput? _systemInput;
    private const nuint SystemInputTimer=0x4648;
    private long _systemInputDeadline;
    private bool _systemInputReady;
    public bool UsesSystemInput=>_systemInput is not null;
    public nint SystemWindowOwner=>_systemInput?.Owner??0;
    public event Action? SystemInputUnavailable;
    /// <summary>Use in an isolated startup probe. Never requests input or displays a window.</summary>
    public static void ProbeSystemInput() { using var probe = new SystemOverlayInput(); }
    /// <summary>Enable before constructing tool windows or entering editing. Existing document/rendering routes are retained.</summary>
    public void EnableSystemInput()
        => EnableSystemInput(null);
    /// <summary>The journal path is retained for compatibility. Safety controls only the explicit release chord, never process termination.</summary>
    public void EnableSystemInput(string? safetyJournalPath, bool enableSafety = true)
    {
        CheckThread();
        if(IsEditing || _keyboardIsolation is not null) throw new InvalidOperationException("Configure system input before editing or keyboard isolation.");
        if(_systemInput is not null) return;
        _systemInput=new();
        try
        {
            if(!enableSafety)_systemInput.SetSafetyEnabled(false);
            if(safetyJournalPath is not null)_systemInput.SetSafetyJournal(safetyJournalPath);
            RebuildMonitors();
        }
        catch
        {
            var failed = _systemInput;
            _systemInput = null; // Rebuild already restored the ordinary desktop HWNDs.
            failed.Dispose();
            throw;
        }
    }
    /// <summary>Actual input ownership, independent of which process owns global foreground.</summary>
    public bool HasEditingInput=>IsEditing && (_systemInput is { } system?system.OwnsInput&&system.Acknowledged:IsForegroundFromThisProcess());
    private bool OwnsEditingInput()=>_systemInput?.OwnsInput??IsForegroundFromThisProcess();
    public bool TryFocusEditingWindow(nint handle)
    {
        CheckThread();
        if(!HasEditingInput || GetWindowThreadProcessId(handle,out uint process)!=_threadId || process!=Environment.ProcessId ||
            !IsWindowVisible(handle) || !IsWindowEnabled(handle)) return false;
        if(_systemInput is null) return SetForegroundWindow(handle) && GetForegroundWindow()==handle;
        SetActiveWindow(handle);SetFocus(handle);return GetFocus()==handle && OwnsEditingInput();
    }
    private bool TryEnterSystemEditing(out string? failure)
    {
        failure=null;
        if(IsEditing && OwnsEditingInput()) return true;
        if(IsEditing) ExitEditingCore(false);
        var foreground=GetForegroundWindow();
        try
        {
            foreach(var pair in _windows) if(IsVisible)ShowWindowPreservingZOrder(pair.Display.Handle);
            _systemInputReady=false;
            IsEditing=true;_focusTransitions.BeginSession();
            RequestSystemInput(foreground);
            if(_disposed || !IsEditing){_systemInput?.End();failure="Editing request cancelled.";return false;}
            Render();return true;
        }
        catch(Exception error)
        {
            if(_disposed){failure=error.Message;return false;}
            _systemInput?.End();
            IsEditing=false;_focusTransitions.EndSession();KillTimer(ControlHandle,SystemInputTimer);
            foreach(var pair in _windows) ShowWindow(pair.Input.Handle,0);
            failure=error.Message;return false;
        }
    }
    private void RequestSystemInput(nint foreground)
    {
        GetCursorPos(out var cursor);
        var active=_windows.FirstOrDefault(pair=>pair.Monitor.Bounds.Contains(new(cursor.X,cursor.Y)))??_windows.First();
        _systemInputReady=false;
        _systemInput!.Begin(foreground,active.Input.Handle);
        if(_disposed || !IsEditing)return;
        _systemInputDeadline=Environment.TickCount64+2000;
        if(SetTimer(ControlHandle,SystemInputTimer,100,0)==0)throw new System.ComponentModel.Win32Exception();
    }
    private void SystemInputReady()
    {
        if(_nativeDialogDepth!=0)return;
        KillTimer(ControlHandle,SystemInputTimer);
        if(!HasEditingInput){EndEditingAfterFocusLoss();return;}
        if(_systemInputReady)return;
        _systemInputReady=true;
        foreach(var pair in _windows)ShowWindow(pair.Input.Handle,8);
        GetCursorPos(out var cursor);
        var active=_windows.FirstOrDefault(pair=>pair.Monitor.Bounds.Contains(new(cursor.X,cursor.Y)))??_windows.First();
        TryFocusEditingWindow(active.Input.Handle);
        InputActivated?.Invoke();
    }
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint window);
    // Canvas HWNDs dispatch tool shortcuts. Actual text uses its separate WPF editor.
    private static void DisableCanvasInputMethod(nint window)=>ImmAssociateContext(window,0);
    [DllImport("imm32.dll")]private static extern nint ImmAssociateContext(nint window,nint context);
}
