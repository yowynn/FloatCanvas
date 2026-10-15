using L = FloatCanvas.Localization.AppText;
using System.ComponentModel;
using System.Runtime.InteropServices;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

public sealed partial class OverlayHost
{
    private const uint KeyboardPacketMessage = 0x8003;
    private const nuint KeyboardHeartbeatTimer = 0x4643;
    private KeyboardInputIsolation? _keyboardIsolation;
    private Action<KeyboardInputPacket, Action>? _dispatchPrivateKey;
    private long _keyboardLease;

    public bool IsKeyboardIsolationEnabled => _keyboardIsolation is not null;
    public bool IsKeyboardIsolationActive => _keyboardIsolation?.IsLeaseActive == true;
    public event Action<KeyboardLeaseEndReason>? KeyboardIsolationReleased;

    /// <summary>
    /// Called only by the owner UI thread when it actually processes input. WM_TIMER alone
    /// is insufficient: Windows can defer low-priority timers during continuous drawing.
    /// A blocked UI handler still stops this heartbeat and is released by the watchdog.
    /// </summary>
    public void NotifyInputProgress()
    {
        CheckThread();
        if (IsEditing) _keyboardIsolation?.Heartbeat();
    }

    /// <summary>
    /// Installs the private keyboard route while pass-through is active. The dispatcher runs
    /// on this UI thread and may complete a packet at a nested modal boundary. It must never
    /// reinsert it into the global input stream. Native-only hosts need not enable this route.
    /// </summary>
    public void EnableKeyboardIsolation(Action<KeyboardInputPacket, Action> dispatcher)
    {
        CheckThread(); ArgumentNullException.ThrowIfNull(dispatcher);
        if (IsEditing) throw new InvalidOperationException(L.Get("Keyboard.ExitEditingBeforeConfigure"));
        if (_keyboardIsolation is not null) throw new InvalidOperationException(L.Get("Keyboard.IsolationAlreadyEnabled"));
        _dispatchPrivateKey = dispatcher;
        try { _keyboardIsolation = KeyboardInputIsolation.StartAsync(ControlHandle, KeyboardPacketMessage).GetAwaiter().GetResult(); }
        catch { _dispatchPrivateKey = null; throw; }
    }

    private void BeginKeyboardIsolation()
    {
        if (_keyboardIsolation is null) return;
        _keyboardLease = _keyboardIsolation.BeginLease(_toggleHotkey ?? HotkeyBinding.DefaultToggle,
            _hideHotkey ?? HotkeyBinding.DefaultHide);
        if (SetTimer(ControlHandle, KeyboardHeartbeatTimer, 100, 0) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Keyboard.MonitorStartFailed"));
    }

    private void EndKeyboardIsolation()
    {
        if (_keyboardIsolation is null) return;
        KillTimer(ControlHandle, KeyboardHeartbeatTimer);
        _keyboardIsolation.EndLease();
    }

    private bool HandleKeyboardControlMessage(uint message, nuint wParam)
    {
        if (message == 0x113 && wParam == KeyboardHeartbeatTimer)
        {
            _keyboardIsolation?.Heartbeat();
            // A failed notification cannot carry its own LeaseEnded packet to this UI.
            // The recovered UI must still stop claiming editing before accepting input.
            if (IsEditing && _keyboardIsolation is { IsLeaseActive: false, Failure: not null } failed)
            {
                var reason = failed.LastEndReason ?? KeyboardLeaseEndReason.HookFailure;
                EndEditingAfterFocusLoss();
                KeyboardIsolationReleased?.Invoke(reason);
            }
            return true;
        }
        if (message != KeyboardPacketMessage) return false;
        var isolation = _keyboardIsolation;
        if (isolation is null || !isolation.TryDequeue(out var packet) || packet is null) return true;
        NotifyInputProgress();
        bool completed = false;
        void CompleteOnce()
        {
            if (completed) return;
            completed = true; isolation.CompletePacket(packet.Sequence);
        }
        try
        {
            // A previous lease can finish after rapid explicit re-entry. Its pending release
            // must not clear modifiers or close the new editing session.
            if (packet.LeaseId != _keyboardLease) return true;
            switch (packet.Kind)
            {
                case KeyboardInputPacketKind.Key:
                    if (packet.IsLocalBalance || (IsEditing && isolation.IsLeaseActive))
                        _dispatchPrivateKey?.Invoke(packet, CompleteOnce);
                    break;
                case KeyboardInputPacketKind.Toggle:
                    if (IsEditing) ToggleRequested?.Invoke();
                    break;
                case KeyboardInputPacketKind.Hide:
                    if (IsEditing) HideRequested?.Invoke();
                    break;
                case KeyboardInputPacketKind.LeaseEnded:
                    if (IsEditing)
                    {
                        EndEditingAfterFocusLoss();
                        KeyboardIsolationReleased?.Invoke(packet.EndReason ?? KeyboardLeaseEndReason.HookFailure);
                    }
                    break;
            }
        }
        catch (Exception error)
        {
            isolation.EndLease(KeyboardLeaseEndReason.HookFailure);
            if (IsEditing) EndEditingAfterFocusLoss();
            KeyboardIsolationReleased?.Invoke(KeyboardLeaseEndReason.HookFailure);
            Error?.Invoke(error);
        }
        finally { CompleteOnce(); }
        return true;
    }

    private void DisposeKeyboardIsolation()
    {
        if (_keyboardIsolation is null) return;
        KillTimer(ControlHandle, KeyboardHeartbeatTimer);
        _keyboardIsolation.Dispose(); _keyboardIsolation = null; _dispatchPrivateKey = null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(nint window, nuint id);
}
