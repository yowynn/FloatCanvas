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
    private const uint PresentationInputMessage = 0x8006;
    private bool _presentationTapKeyboard, _presentationTapPointer;
    private long _presentationTapEpoch;

    /// <summary>Passive metadata from the existing hooks in both editing and pass-through modes, delivered on the owner UI thread.</summary>
    public event Action<PresentationObservedBatch>? PresentationInputObserved;

    public void ConfigurePresentationInputTap(bool keyboard, bool pointer, long epoch)
    {
        CheckThread();
        if (epoch < 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        _presentationTapKeyboard = keyboard; _presentationTapPointer = pointer; _presentationTapEpoch = epoch;
        _keyboardIsolation?.ConfigurePresentationInputTap(keyboard, pointer, epoch, PresentationInputMessage);
    }

    public bool IsPresentationKeyboardAvailable => _keyboardIsolation is { IsThreadAlive: true, Failure: null };

    public void ObserveOrdinaryKeyboardMessage(nint window, uint message, nuint wParam, nint lParam, uint time)
    {
        if (message is not (0x100 or 0x101 or 0x104 or 0x105) || wParam is 0 or >= 255 ||
            _systemInput is not null || !IsEditing || _keyboardIsolation is not { } isolation || !OwnsWindow(window) ||
            !GetCurrentInputMessageSource(out var source) || source.DeviceType != 1 || source.OriginId is not (1 or 2)) return;
        uint data = unchecked((uint)lParam);
        uint flags = ((data >> 24) & 1) | (source.OriginId == 2 ? 0x10u : 0u);
        isolation.ObserveOrdinaryKeyboardMessage((uint)wParam, (data >> 16) & 255, flags, time, message);
    }

    public void ObserveOrdinaryRawPresentationKeys(PresentationObservedBatch batch)
    {
        if (_systemInput is not null || !IsEditing || _keyboardIsolation is not { } isolation) return;
        foreach (var key in batch.Changes)
        {
            if (key.InputOwner != PresentationInputOwner.FloatCanvas ||
                key.Kind is not (PresentationObservedKind.RawKeyDown or PresentationObservedKind.RawKeyUp)) continue;
            uint message = key.Kind == PresentationObservedKind.RawKeyUp ? 0x101u : 0x100u;
            isolation.ObserveOrdinaryKeyboardMessage((uint)key.VirtualKey, (uint)key.ScanCode, (key.KeyFlags >> 1) & 1,
                unchecked((uint)key.Milliseconds), message, raw: true);
        }
    }

    public bool IsKeyboardIsolationEnabled => _keyboardIsolation is not null;
    public bool IsKeyboardIsolationActive => _keyboardIsolation is { } isolation
        ? isolation.IsLeaseActive && (_systemInput?.Acknowledged ?? true)
        : _systemInput?.Acknowledged == true;
    public event Action<KeyboardLeaseEndReason>? KeyboardIsolationReleased;

    /// <summary>
    /// Called only by the owner UI thread after real progress, such as handling input or
    /// completing a render. Windows can defer low-priority WM_TIMER during continuous work.
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
        if (_dispatchPrivateKey is not null) throw new InvalidOperationException(L.Get("Keyboard.IsolationAlreadyEnabled"));
        PrepareKeyboardIsolation();
        _dispatchPrivateKey = dispatcher;
    }

    /// <summary>Prepares the hooks without an input lease or a live editor dispatcher.</summary>
    public void PrepareKeyboardIsolation()
    {
        CheckThread();
        if (IsEditing) throw new InvalidOperationException(L.Get("Keyboard.ExitEditingBeforeConfigure"));
        if (_keyboardIsolation is not null) return;
        _keyboardIsolation = KeyboardInputIsolation.StartAsync(ControlHandle, KeyboardPacketMessage).GetAwaiter().GetResult();
        _keyboardIsolation.ConfigurePresentationInputTap(_presentationTapKeyboard, _presentationTapPointer, _presentationTapEpoch, PresentationInputMessage);
    }

    private void BeginKeyboardIsolation()
    {
        if (_keyboardIsolation is null) return;
        var toggle = _toggleHotkey ?? HotkeyBinding.DefaultToggle;
        var hide = _hideHotkey ?? HotkeyBinding.DefaultHide;
        _keyboardLease = _systemInput is { } system
            ? _keyboardIsolation.BeginDelegatedLease(toggle, hide, system.Target)
            : _keyboardIsolation.BeginLease(toggle, hide);
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
        if (message == PresentationInputMessage)
        {
            var source = _keyboardIsolation;
            if (source is not null && source.TryDrainPresentationInput(unchecked((uint)wParam), out var batch))
            {
                try { PresentationInputObserved?.Invoke(batch!); }
                catch { /* A visual subscriber cannot interrupt the editing input dispatcher. */ }
            }
            return true;
        }
        if (message == 0x113 && wParam == InputMethodFocusTimer) { CheckInputMethodContext(); return true; }
        if (message == 0x113 && wParam == KeyboardHeartbeatTimer)
        {
            if (IsEditing && _nativeDialogDepth == 0 && _systemInput is { } system)
            {
                if (!system.OwnsInput) { EndEditingAfterFocusLoss(); return true; }
                if (!system.Acknowledged) { CheckSystemInputEntry(); return true; }
            }
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
            if (packet.LeaseId != _keyboardLease && !(_keyboardLease == 0 && packet.IsLocalBalance)) return true;
            switch (packet.Kind)
            {
                case KeyboardInputPacketKind.Key:
                    if (packet.IsLocalBalance || (HasEditingInput && isolation.IsLeaseActive))
                        _dispatchPrivateKey?.Invoke(packet, CompleteOnce);
                    break;
                case KeyboardInputPacketKind.Command:
                    if (IsEditing && _commandHotkeyIds.TryGetValue((int)packet.VirtualKey, out var command) &&
                        _commandHotkeys.TryGetValue(command,out var binding) && binding.VirtualKey==packet.ScanCode && binding.Modifiers==(uint)packet.Modifiers) CommandHotkeyRequested?.Invoke(command);
                    break;
                case KeyboardInputPacketKind.Toggle:
                    if (IsEditing) ToggleRequested?.Invoke();
                    break;
                case KeyboardInputPacketKind.Hide:
                    if (IsEditing) HideRequested?.Invoke();
                    break;
                case KeyboardInputPacketKind.LeaseEnded:
                    if (_nativeDialogDepth != 0 && packet.EndReason == KeyboardLeaseEndReason.Explicit) break;
                    if (IsEditing)
                    {
                        var reason = packet.EndReason ?? KeyboardLeaseEndReason.HookFailure;
                        if (reason == KeyboardLeaseEndReason.ForegroundLost && TryKeepInputMethodContext()) break;
                        if (reason == KeyboardLeaseEndReason.ForegroundLost && _focusTransitions.HasPending)
                        {
                            if (IsForegroundFromThisProcess() && EnsureEditingKeyboardIsolation()) break;
                            if (!IsForegroundFromThisProcess()) break; // the bounded completion rechecks once
                        }
                        _focusTransitions.Cancel();
                        if (!IsEditing) break; // a failed recovery has already released this session
                        EndEditingAfterFocusLoss();
                        KeyboardIsolationReleased?.Invoke(reason);
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
        StopInputMethodContext();
        if (_keyboardIsolation is null) return;
        KillTimer(ControlHandle, KeyboardHeartbeatTimer);
        _keyboardIsolation.Dispose(); _keyboardIsolation = null; _dispatchPrivateKey = null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(nint window, nuint id);
}
