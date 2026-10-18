using L = FloatCanvas.Localization.AppText;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

public enum CaptureVisibilityStrategy { ExcludeVisible, HideVisuals, LegacyInputOffscreen }

/// <summary>Owns only temporary capture affinity/visual geometry. It never enters/exits editing or activates a window.</summary>
public sealed class CaptureVisibilityScope : IDisposable
{
    private sealed class WindowSnapshot(nint handle, bool input)
    {
        internal readonly nint Handle = handle;
        internal readonly bool Input = input;
        internal readonly bool Visible = IsWindowVisible(handle);
        internal uint? Affinity;
        internal bool AffinityChanged, Hidden, Moved;
        internal PixelBounds Bounds;
    }
    private readonly uint _threadId = GetCurrentThreadId();
    private readonly List<WindowSnapshot> _original = [];
    private readonly Func<bool, CaptureRestoreResult> _completeHost;
    private readonly Func<bool> _canContinue;
    private readonly Action<nint, PixelBounds> _moveInput;
    private readonly Action<nint> _releaseInputCapture;
    private readonly Action<Exception> _inputRestoreFailed;
    private readonly WindowsCapabilities _capabilities;
    private nint _mouseCapture;
    private bool _disposed, _constructionFailed;
    public long NotBeforeTimestamp => FrameBoundary.Timestamp;
    public CaptureFrameBoundary FrameBoundary { get; private set; }
    public CaptureVisibilityStrategy Strategy { get; private set; } = CaptureVisibilityStrategy.ExcludeVisible;
    public CaptureRestoreStatus Status { get; private set; } = CaptureRestoreStatus.Pending;
    public CaptureRestoreReceipt? Continuation { get; private set; }
    // Fault injection at cleanup only; production always uses the native DPI scope.
    internal Func<IDisposable>? RestoreDpiScopeFactory { get; set; }

    internal CaptureVisibilityScope(IEnumerable<nint> visualWindows, IEnumerable<nint> inputWindows,
        Func<bool, CaptureRestoreResult> completeHost, Action<CaptureVisibilityStrategy, nint> ready,
        WindowsCapabilities capabilities, Func<bool> canContinue, bool forceLegacy,
        Action<nint, PixelBounds> moveInput, Action<nint> releaseInputCapture, Action<Exception> inputRestoreFailed)
    {
        _completeHost = completeHost; _canContinue = canContinue; _capabilities = capabilities;
        _moveInput = moveInput; _releaseInputCapture = releaseInputCapture;
        _inputRestoreFailed = inputRestoreFailed;
        try
        {
            using var dpi = new ThreadDpiAwarenessScope(capabilities);
            var inputs = inputWindows.Where(h => h != 0).ToHashSet();
            foreach (var handle in visualWindows.Concat(inputs).Where(h => h != 0).Distinct())
            {
                if (!IsWindow(handle) || GetWindowThreadProcessId(handle, out var process) == 0 || process != Environment.ProcessId)
                    throw new Win32Exception(1400, L.Get("Capture.ExclusionStateReadFailed"));
                var snapshot = new WindowSnapshot(handle, inputs.Contains(handle));
                if (GetWindowDisplayAffinity(handle, out var affinity)) snapshot.Affinity = affinity;
                _original.Add(snapshot);
            }
            if (!forceLegacy && capabilities.SupportsCaptureExclusion)
            {
                foreach (var item in _original)
                {
                    if (item.Affinity is not null && (item.Affinity == 0x11 || SetWindowDisplayAffinity(item.Handle, 0x11)))
                    { item.AffinityChanged = item.Affinity != 0x11; continue; }
                    // A visual that cannot be excluded may be hidden only if it is not active.
                    // The active input HWND is never hidden, including on an affinity failure.
                    if (item.Input || item.Handle == GetForegroundWindow())
                        throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Capture.ExclusionFailed"));
                    HideVisual(item); Strategy = CaptureVisibilityStrategy.HideVisuals;
                }
            }
            else
            {
                Strategy = CaptureVisibilityStrategy.LegacyInputOffscreen;
                foreach (var item in _original.Where(item => !item.Input))
                {
                    if (item.Visible && item.Handle == GetForegroundWindow())
                        throw new InvalidOperationException("Legacy capture requires the existing native input foreground; it never transfers focus from an active panel.");
                    HideVisual(item);
                }
                var activeInput = _original.FirstOrDefault(item => item.Input && item.Visible && item.Handle == GetForegroundWindow());
                if (_original.Any(item => item.Input && item.Visible) && activeInput is null)
                    throw new InvalidOperationException("Legacy capture cannot relocate an input carrier without its existing foreground.");
                if (activeInput is not null)
                {
                    if (GetCapture() != 0) throw new InvalidOperationException("Legacy capture requires the original pointer gesture to finish first.");
                    SetCapture(activeInput.Handle);
                    if (GetCapture() != activeInput.Handle) throw new InvalidOperationException("Legacy capture could not retain its native mouse capture.");
                    _mouseCapture = activeInput.Handle;
                    int right = checked(GetSystemMetrics(76) + GetSystemMetrics(78) + 32);
                    int top = GetSystemMetrics(77);
                    foreach (var item in _original.Where(item => item.Input && item.Visible))
                    {
                        if (!GetWindowRect(item.Handle, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        item.Bounds = rect.Bounds; item.Moved = true;
                        _moveInput(item.Handle, new(right, top, item.Bounds.Width, item.Bounds.Height));
                    }
                }
            }
            // DwmFlush is a caller-composition boundary, not a blanket pixel guarantee.
            // CaptureService must create a new duplication session after this typed boundary.
            Marshal.ThrowExceptionForHR(DwmFlush());
            FrameBoundary = new(Stopwatch.GetTimestamp(), CaptureFrameRequirement.CurrentDesktopFromNewDuplication);
            ready(Strategy, _mouseCapture);
        }
        catch { _constructionFailed = true; Dispose(); throw; }
    }

    private static void HideVisual(WindowSnapshot item)
    { if (item.Visible) { ShowWindow(item.Handle, 0); item.Hidden = true; } }

    public void Dispose()
    {
        if (_disposed) return;
        if (GetCurrentThreadId() != _threadId) throw new InvalidOperationException(L.Get("Capture.RestoreRequiresUiThread"));
        _disposed = true; List<Exception>? errors = null;
        bool permit = false;
        try { permit = _canContinue(); }
        catch (Exception error) { (errors ??= []).Add(error); }
        IDisposable? dpi = null;
        try { dpi = RestoreDpiScopeFactory?.Invoke() ?? new ThreadDpiAwarenessScope(_capabilities); }
        catch (Exception error) { (errors ??= []).Add(error); }
        try
        {
            foreach (var item in _original)
            {
                if (!IsWindow(item.Handle)) continue;
                try
                {
                    if (item.AffinityChanged && !SetWindowDisplayAffinity(item.Handle, item.Affinity!.Value))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Capture.ExclusionStateRestoreFailed"));
                    // Geometry belongs to this scope. Restoring it never shows/activates a hidden input window.
                    if (item.Moved)
                    {
                        try { _moveInput(item.Handle, item.Bounds); }
                        catch (Exception error) { _inputRestoreFailed(error); throw; }
                    }
                    // Restore only a visual actually hidden by this scope. A never-hidden HWND may
                    // have been hidden/closed by a real user action and must never be resurrected.
                    if (item.Hidden && permit) ShowWindow(item.Handle, 8);
                }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
        }
        finally
        {
            try { dpi?.Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
            try
            {
                var result = _completeHost(!_constructionFailed && permit && errors is null);
                Status = result.Status; Continuation = errors is null ? result.Receipt : null;
            }
            catch (Exception error) { (errors ??= []).Add(error); }
            finally
            {
                try { if (_mouseCapture != 0) _releaseInputCapture(_mouseCapture); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
        }
        if (_constructionFailed) { Status = CaptureRestoreStatus.Failed; Continuation = null; }
        if (errors is not null)
        {
            Status = CaptureRestoreStatus.Failed; Continuation = null;
            throw new AggregateException(L.Get("Capture.WindowRestoreErrors"), errors);
        }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
