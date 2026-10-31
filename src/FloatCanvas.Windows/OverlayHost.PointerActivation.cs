using static FloatCanvas.Windows.NativeMethods;
using System.Runtime.InteropServices;

namespace FloatCanvas.Windows;

public sealed partial class OverlayHost
{
    /// <summary>
    /// Optional synchronous, read-only application gate for a new canvas gesture. Called
    /// on this host's creating UI thread, including after reentrant native focus changes.
    /// Null permits native-only hosts. False blocks the press without ending a modal
    /// window's keyboard isolation; exceptions cancel editing and report through Error.
    /// </summary>
    public Func<bool>? CanBeginCanvasGesture { get; set; }

    private bool _startingCanvasPointer;

    /// <summary>
    /// Transfers the same physical press previously captured by an application Popup. The
    /// caller must release that Popup's capture first and mark the WPF press handled on success.
    /// No global input is generated: one private Down acquires the exact canvas HWND's
    /// capture, and the original physical Move/Up continue through its normal native route.
    /// Point and messageTime must describe the original press, never a later cursor sample.
    /// </summary>
    public bool TryForwardCapturedCanvasPress(OverlayPoint point, OverlayMouseButton button, uint messageTime, int clickCount = 1)
    {
        CheckThread();
        if (button is not (OverlayMouseButton.Left or OverlayMouseButton.Right or OverlayMouseButton.Middle) ||
            clickCount < 1 || !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            point.X < int.MinValue || point.X > int.MaxValue || point.Y < int.MinValue || point.Y > int.MaxValue ||
            !IsEditing || !OwnsEditingInput() ||
            (_keyboardIsolation is not null && !_keyboardIsolation.IsLeaseActive) ||
            _startingCanvasPointer || _pressedButton != OverlayMouseButton.None || GetCapture() != 0 ||
            HasDisabledOwnOwner(GetForegroundWindow())) return false;
        var state = _windows.FirstOrDefault(pair => pair.Monitor.Bounds.Contains(point))?.Input;
        if (state is null || WindowFromPhysicalPoint(new((int)Math.Round(point.X), (int)Math.Round(point.Y))) != state.Handle ||
            !TryBeginCanvasPointer(state)) return false;
        _pressedButton = button;
        try
        {
            RaisePointer((clickCount & 1) == 0 ? OverlayPointerKind.DoubleClick : OverlayPointerKind.Down, button,
                position: point, messageTime: messageTime);
            return true;
        }
        catch (Exception error)
        {
            CancelPointer();
            Error?.Invoke(error);
            return false;
        }
    }

    private bool TryBeginCanvasPointer(WindowState state)
    {
        // Never steal an existing canvas/toolbar/popup gesture, even across monitors.
        if (_startingCanvasPointer || _pressedButton != OverlayMouseButton.None) return false;
        _startingCanvasPointer = true;
        try
        {
            if (!CanStartCanvasPointer(state) || GetCapture() != 0) return false;
            if(_systemInput is not null)
            {
                if(!_systemInput.Acknowledged || !TryFocusEditingWindow(state.Handle)) return false;
                SetCapture(state.Handle);
                return GetCapture()==state.Handle && GetFocus()==state.Handle && CanStartCanvasPointer(state);
            }

            // Use this press's exact input HWND, not the cursor's potentially new screen.
            // Each native call may synchronously run application focus handlers.
            SetForegroundWindow(state.Handle);
            if (!CanStartCanvasPointer(state)) return false;
            if (GetForegroundWindow() != state.Handle) return FailCanvasPointerStart();

            SetFocus(state.Handle);
            if (!CanStartCanvasPointer(state)) return false;
            if (GetForegroundWindow() != state.Handle || GetFocus() != state.Handle)
                return FailCanvasPointerStart();
            if (GetCapture() != 0) return false;

            SetCapture(state.Handle);
            if (!CanStartCanvasPointer(state))
            {
                CancelPointer();
                return false;
            }
            if (GetCapture() != state.Handle || GetForegroundWindow() != state.Handle || GetFocus() != state.Handle)
                return FailCanvasPointerStart();
            return true;
        }
        catch (Exception error)
        {
            // This callback boundary stays inside our WM_* handler: never let a gate or
            // app event exception fall back to DefWindowProc's activation behavior.
            try { FailCanvasPointerStart(); } catch { }
            try { Error?.Invoke(error); } catch { }
            return false;
        }
        finally { _startingCanvasPointer = false; }
    }

    private bool CanStartCanvasPointer(WindowState state)
    {
        if (_disposed || !IsEditing) return false;
        if (!OwnsEditingInput()) return FailCanvasPointerStart();
        if (!IsCanvasPointerAvailable(state)) return false;
        // Recheck business/modal denial even on an activation failure, before deciding
        // to release isolation. LostFocus can synchronously open an owned modal window.
        bool applicationAllows = CanBeginCanvasGesture?.Invoke() != false;
        bool modalBlocked = HasDisabledOwnOwner(GetForegroundWindow());
        // A denial must not conceal a real external foreground change during the gate.
        if (!OwnsEditingInput() || !EnsureEditingKeyboardIsolation())
            return FailCanvasPointerStart();
        return applicationAllows && !modalBlocked && IsCanvasPointerAvailable(state);
    }

    private bool IsCanvasPointerAvailable(WindowState state) =>
        !_disposed && IsEditing && !_captureActive && !IsReceivingDrop && !IsOleDragOver &&
        !state.Freed && state.Handle != 0 && _windows.Any(pair => ReferenceEquals(pair.Input, state)) &&
        IsWindowVisible(state.Handle) && IsWindowEnabled(state.Handle);

    private static bool HasDisabledOwnOwner(nint foreground)
    {
        // Native dialogs need not share a class name. An owned foreground window with
        // a disabled same-process owner is a modal barrier, including nested dialogs.
        var visited = new HashSet<nint>();
        for (nint window = foreground; window != 0; window = GetWindow(window, 4)) // GW_OWNER
        {
            if (!visited.Add(window)) return true;
            if (GetWindowThreadProcessId(window, out var processId) == 0 || processId != Environment.ProcessId) return false;
            if (!IsWindowEnabled(window)) return true;
        }
        return false;
    }

    private bool FailCanvasPointerStart()
    {
        if (IsEditing) EndEditingAfterFocusLoss();
        else CancelPointer();
        return false;
    }

    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(Point point);
}
