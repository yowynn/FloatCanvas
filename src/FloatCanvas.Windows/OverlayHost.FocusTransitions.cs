using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

public sealed partial class OverlayHost
{
    private const uint CompleteFocusTransitionMessage = 0x8004;
    private readonly FocusTransitionState _focusTransitions = new();
    private const nuint InputMethodFocusTimer = 0x4645;
    private bool _inputMethodFocusPending;
    private nint _inputMethodReturnWindow;
    private long _inputMethodReturnUntil;

    /// <summary>A bounded Win+Space return candidate, never permission to isolate an external window's input.</summary>
    public bool IsInputMethodSwitchPending
    {
        get
        {
            CheckThread();
            if (!IsEditing || _captureActive || _keyboardIsolation is null) return false;
            // Native foreground return can precede WPF's IsActive update and its queued
            // Deactivated check. This brief own-window-only guard never permits external input.
            if (_keyboardIsolation.IsLeaseActive && GetForegroundWindow() == _inputMethodReturnWindow &&
                Environment.TickCount64 < _inputMethodReturnUntil) return true;
            if (!_keyboardIsolation.TryGetInputMethodSwitch(_keyboardLease, out var origin)) return false;
            return GetForegroundWindow() == origin || !IsForegroundFromThisProcess();
        }
    }

    /// <summary>Changes whenever an editing session starts or ends. Queued work must retain its original value.</summary>
    public long EditingSessionVersion => _focusTransitions.SessionVersion;

    /// <summary>
    /// Marks one synchronous show/hide/close/return-focus boundary on the creating UI thread.
    /// It never activates a window and expires on the next control-window turn, even if a
    /// nested modal pump runs before Dispose. Do not use it as a modal lifetime exemption.
    /// </summary>
    public IDisposable BeginInternalWindowTransition()
    {
        CheckThread();
        var token = _focusTransitions.Begin(_keyboardLease);
        PostTransitionCompletion(token);
        return new InternalWindowTransition(this, token);
    }

    /// <summary>
    /// Last-resort return target after an internal owner/toolbar chain has become unavailable.
    /// Requires an existing editing lease and own foreground, no native/WPF capture or modal
    /// owner barrier, and the application's current canvas gate. Does not enter editing or
    /// renew isolation; a denial never activates a window or changes the interaction mode.
    /// </summary>
    public bool TryFocusEditingCanvas()
    {
        CheckThread();
        bool Available() => IsEditing && IsVisible && !_captureActive && !IsReceivingDrop && !IsOleDragOver &&
            !_startingCanvasPointer && _pressedButton == OverlayMouseButton.None && GetCapture() == 0 &&
            OwnsEditingInput() && (_keyboardIsolation is null || _keyboardIsolation.IsLeaseActive) &&
            _windows.Count != 0 && _windows.All(pair => IsWindowVisible(pair.Input.Handle) && IsWindowEnabled(pair.Input.Handle)) &&
            !HasDisabledOwnOwner(GetForegroundWindow());
        if (!Available() || CanBeginCanvasGesture?.Invoke() == false || !Available()) return false;
        return ActivateInputAtCursor();
    }

    private void PostEditingFocusValidation()
    {
        if (!IsEditing || _focusCheckSession == EditingSessionVersion) return;
        var session = EditingSessionVersion;
        if (PostMessageW(ControlHandle, WmValidateEditingFocus, unchecked((nuint)session), 0))
            _focusCheckSession = session;
    }

    private void PostTransitionCompletion(FocusTransitionToken token)
    {
        if (!_focusTransitions.IsCurrent(token)) return;
        if(_systemInput is not null)
        {
            _focusTransitions.Complete(token);
            if(IsEditing && !OwnsEditingInput()) EndEditingAfterFocusLoss();
            return;
        }
        if (!PostMessageW(ControlHandle, CompleteFocusTransitionMessage,
            unchecked((nuint)token.SessionVersion), unchecked((nint)token.TransitionVersion)))
        {
            _focusTransitions.Cancel();
            EndEditingAfterFocusLoss();
        }
    }

    private void CompleteFocusTransition(FocusTransitionToken token)
    {
        if (!_focusTransitions.IsCurrent(token)) return;
        // Re-read the real foreground after the caller's synchronous owner/target restoration.
        // External input has already failed open on the hook thread throughout this boundary.
        bool own = IsForegroundFromThisProcess();
        if (!own && TryKeepInputMethodContext()) { _focusTransitions.Complete(token); return; }
        bool inputReady = own && EnsureEditingKeyboardIsolation();
        _focusTransitions.Complete(token);
        if (IsEditing && (!own || !inputReady)) EndEditingAfterFocusLoss();
    }

    private bool EnsureEditingKeyboardIsolation()
    {
        if (_keyboardIsolation is null) return true;
        if (_keyboardIsolation.IsLeaseActive) return true;
        bool inputMethod = _keyboardIsolation.TryGetInputMethodSwitch(_keyboardLease, out var origin) &&
            GetForegroundWindow() == origin;
        if (!inputMethod && !_focusTransitions.CanResume(_keyboardLease, _keyboardIsolation.LastEndReason, IsForegroundFromThisProcess()))
            return false;
        try
        {
            var toggle = _toggleHotkey ?? HotkeyBinding.DefaultToggle;
            var hide = _hideHotkey ?? HotkeyBinding.DefaultHide;
            long lease;
            bool resumed = inputMethod
                ? _keyboardIsolation.TryResumeInputMethodLease(_keyboardLease, toggle, hide, out lease)
                : _keyboardIsolation.TryResumeForegroundLease(_keyboardLease, toggle, hide, out lease);
            if (!resumed)
                return false;
            _keyboardLease = lease;
            _focusTransitions.ReplacedLease(lease);
            if (inputMethod) KeepInputMethodReturnGuard(origin);
            return true;
        }
        catch (Exception error)
        {
            _focusTransitions.Cancel();
            _keyboardIsolation.EndLease(KeyboardLeaseEndReason.HookFailure);
            if (IsEditing) EndEditingAfterFocusLoss();
            KeyboardIsolationReleased?.Invoke(KeyboardLeaseEndReason.HookFailure);
            Error?.Invoke(error);
            return false;
        }
    }

    private void ValidateEditingFocus(long sessionVersion)
    {
        if (!IsEditing || sessionVersion != EditingSessionVersion) return;
        if(_systemInput is not null)
        {if(!OwnsEditingInput()) EndEditingAfterFocusLoss();return;}
        if (!IsForegroundFromThisProcess())
        {
            if (TryKeepInputMethodContext()) return;
            // Outside the explicit IME candidate, only the already queued internal
            // transition completion may defer this decision; never reacquire foreground.
            if (!_focusTransitions.HasPending) EndEditingAfterFocusLoss();
        }
        else if (!EnsureEditingKeyboardIsolation() && IsEditing) EndEditingAfterFocusLoss();
        else if (_inputMethodFocusPending)
        {
            KeepInputMethodReturnGuard(GetForegroundWindow());
            StopInputMethodContext(returned: true);
        }
    }

    private bool TryKeepInputMethodContext()
    {
        if (!IsInputMethodSwitchPending) return false;
        if (IsForegroundFromThisProcess())
        {
            if (!EnsureEditingKeyboardIsolation()) return false;
            KeepInputMethodReturnGuard(GetForegroundWindow());
            StopInputMethodContext(returned: true);
            return true;
        }
        if (!_inputMethodFocusPending)
            _inputMethodFocusPending = SetTimer(ControlHandle, InputMethodFocusTimer, 50, 0) != 0;
        return _inputMethodFocusPending;
    }

    private void CheckInputMethodContext()
    {
        KillTimer(ControlHandle, InputMethodFocusTimer);
        _inputMethodFocusPending = false;
        if (IsEditing && !TryKeepInputMethodContext()) ValidateEditingFocus(EditingSessionVersion);
    }

    private void KeepInputMethodReturnGuard(nint window)
    { _inputMethodReturnWindow = window; _inputMethodReturnUntil = Environment.TickCount64 + 250; }

    private void StopInputMethodContext(bool returned = false)
    {
        KillTimer(ControlHandle, InputMethodFocusTimer);
        _inputMethodFocusPending = false;
        if (!returned) { _inputMethodReturnWindow = 0; _inputMethodReturnUntil = 0; }
        _keyboardIsolation?.CancelInputMethodSwitch();
    }

    private sealed class InternalWindowTransition(OverlayHost owner, FocusTransitionToken token) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (owner._disposed) return;
            owner.CheckThread();
            owner.PostTransitionCompletion(token);
        }
    }
}
