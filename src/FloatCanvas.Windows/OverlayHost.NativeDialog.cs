using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

public sealed partial class OverlayHost
{
    private int _nativeDialogDepth;

    /// <summary>
    /// Hands input to an application's native modal dialog without ending its editing session.
    /// No external input is intercepted during the dialog. The caller must dispose after the
    /// native modal loop returns; an explicit mode exit invalidates any pending restoration.
    /// </summary>
    public IDisposable? BeginNativeDialog()
    {
        CheckThread();
        if (_systemInput is null || !IsEditing) return null;
        var scope = new NativeDialogTransition(this, EditingSessionVersion);
        if (_nativeDialogDepth++ != 0) return scope;
        try
        {
            KillTimer(ControlHandle, SystemInputTimer);
            _systemInputReady = false;
            CancelPointer();
            EndKeyboardIsolation();
            _systemInput.End();
            foreach (var pair in _windows) ShowWindow(pair.Input.Handle, 0);
            return scope;
        }
        catch
        {
            _nativeDialogDepth = 0;
            EndEditingAfterFocusLoss();
            throw;
        }
    }

    private void ReturnFromNativeDialog(long session)
    {
        if (_disposed || !IsEditing || EditingSessionVersion != session) return;
        CheckThread();
        if (--_nativeDialogDepth != 0) return;
        try
        {
            // The dialog may have activated our owner, or the user may have switched apps.
            // Use the actual foreground now, never reactivate the old game/window.
            RequestSystemInput(GetForegroundWindow());
        }
        catch (Exception error)
        {
            EndEditingAfterFocusLoss();
            Error?.Invoke(error);
        }
    }

    private sealed class NativeDialogTransition(OverlayHost owner, long session) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.ReturnFromNativeDialog(session);
        }
    }
}
