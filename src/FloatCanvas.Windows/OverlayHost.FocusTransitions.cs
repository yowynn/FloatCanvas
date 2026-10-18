using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

public sealed partial class OverlayHost
{
    private const uint CompleteFocusTransitionMessage = 0x8004;
    private readonly FocusTransitionState _focusTransitions = new();

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
            IsForegroundFromThisProcess() && (_keyboardIsolation is null || _keyboardIsolation.IsLeaseActive) &&
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
        bool inputReady = own && EnsureEditingKeyboardIsolation();
        _focusTransitions.Complete(token);
        if (IsEditing && (!own || !inputReady)) EndEditingAfterFocusLoss();
    }

    private bool EnsureEditingKeyboardIsolation()
    {
        if (_keyboardIsolation is null) return true;
        if (_keyboardIsolation.IsLeaseActive) return true;
        if (!_focusTransitions.CanResume(_keyboardLease, _keyboardIsolation.LastEndReason, IsForegroundFromThisProcess()))
            return false;
        try
        {
            if (!_keyboardIsolation.TryResumeForegroundLease(_keyboardLease,
                _toggleHotkey ?? HotkeyBinding.DefaultToggle, _hideHotkey ?? HotkeyBinding.DefaultHide, out var lease))
                return false;
            _keyboardLease = lease;
            _focusTransitions.ReplacedLease(lease);
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
        if (!IsForegroundFromThisProcess())
        {
            // The one already queued completion performs the final decision. No timer,
            // modal lifetime exemption or repeated foreground acquisition is introduced.
            if (!_focusTransitions.HasPending) EndEditingAfterFocusLoss();
        }
        else if (!EnsureEditingKeyboardIsolation() && IsEditing) EndEditingAfterFocusLoss();
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
