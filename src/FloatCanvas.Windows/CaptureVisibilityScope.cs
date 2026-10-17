using L = FloatCanvas.Localization.AppText;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

/// <summary>A UI-thread scope restoring all temporary window visibility and capture affinity changes.</summary>
public sealed class CaptureVisibilityScope : IDisposable
{
    private readonly uint _threadId = GetCurrentThreadId();
    private readonly List<(nint Handle, uint Affinity, bool Visible, bool Topmost, int ZRank)> _original = [];
    private readonly Action _restoreHost;
    private readonly bool _excludeCapture;
    private bool _disposed;
    public long NotBeforeTimestamp { get; }

    internal CaptureVisibilityScope(IEnumerable<nint> handles, Action restoreHost, Action hideHost)
        : this(handles, restoreHost, hideHost, WindowsCapabilities.Current) { }

    internal CaptureVisibilityScope(IEnumerable<nint> handles, Action restoreHost, Action hideHost, WindowsCapabilities capabilities)
    {
        _restoreHost = restoreHost;
        _excludeCapture = capabilities.SupportsCaptureExclusion;
        try
        {
            var windows = handles.Where(handle => handle != 0).Distinct().ToArray();
            var zRanks = ReadZRanks(windows);
            foreach (var handle in windows)
            {
                if (!GetWindowDisplayAffinity(handle, out var affinity))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Capture.ExclusionStateReadFailed"));
                _original.Add((handle, affinity, IsWindowVisible(handle),
                    ((long)GetWindowLongPtr(handle, GwlExStyle) & ExTopmost) != 0,
                    zRanks.GetValueOrDefault(handle, int.MaxValue)));
                if (_excludeCapture && !SetWindowDisplayAffinity(handle, 0x11))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Capture.ExclusionFailed"));
                ShowWindow(handle, 0);
            }
            hideHost();
            // Reject any desktop composition completed while only a prefix of our windows was
            // hidden. The first acceptable frame must follow the final hide operation.
            NotBeforeTimestamp = Stopwatch.GetTimestamp();
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (GetCurrentThreadId() != _threadId) throw new InvalidOperationException(L.Get("Capture.RestoreRequiresUiThread"));
        _disposed = true;
        List<Exception>? errors = null;
        foreach (var item in _original)
        {
            // Monitor changes or application shutdown may have destroyed a captured window already.
            if (!IsWindow(item.Handle)) continue;
            if (_excludeCapture && !SetWindowDisplayAffinity(item.Handle, item.Affinity))
                (errors ??= []).Add(new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Capture.ExclusionStateRestoreFailed")));
            ShowWindow(item.Handle, item.Visible ? 8 : 0);
        }
        try { _restoreHost(); }
        catch (Exception error) { (errors ??= []).Add(error); }
        // Host restoration may raise/activate its input HWND after the tools have
        // already been re-shown. Restore the original relative stack last, without
        // activation or re-showing chrome hidden by the host's focus-loss cleanup.
        // A foreign foreground choice must not trigger another topmost raise.
        if (GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundProcess) != 0 && foregroundProcess == Environment.ProcessId)
        {
            foreach (var item in _original.OrderBy(item => item.Topmost).ThenByDescending(item => item.ZRank))
            {
                if (!item.Visible || !IsWindow(item.Handle) || !IsWindowVisible(item.Handle) ||
                    GetWindowThreadProcessId(item.Handle, out var process) == 0 || process != Environment.ProcessId) continue;
                // Restore each original topmost group from bottom to top. Keeping
                // owner z order avoids moving a toolbar above its owned dialog.
                if (!SetWindowPos(item.Handle, item.Topmost ? HwndTopmost : 0, 0, 0, 0, 0, 0x213))
                    (errors ??= []).Add(new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Capture.WindowOrderRestoreFailed")));
            }
        }
        if (errors is not null) throw new AggregateException(L.Get("Capture.WindowRestoreErrors"), errors);
    }

    private static Dictionary<nint, int> ReadZRanks(IEnumerable<nint> windows)
    {
        var remaining = windows.ToHashSet();
        var ranks = new Dictionary<nint, int>();
        var visited = new HashSet<nint>();
        var window = GetTopWindow(0);
        for (int rank = 0; window != 0 && remaining.Count > 0 && rank < 16384 && visited.Add(window);
            rank++, window = GetWindow(window, 2)) // GW_HWNDNEXT
            if (remaining.Remove(window)) ranks.Add(window, rank);
        return ranks;
    }
}
