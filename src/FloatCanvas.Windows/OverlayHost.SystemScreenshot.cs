using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

/// <summary>One owned UI visual, in physical desktop coordinates. The receiver disposes its pixels.</summary>
public sealed record OverlayVisualSnapshot(nint Handle, PixelBounds Bounds, SKImage Pixels) : IDisposable
{
    public void Dispose() => Pixels.Dispose();
}

public sealed partial class OverlayHost
{
    private const nuint SystemScreenshotTimer = 0x464D;
    private List<(WindowState Window, DibBuffer Buffer)>? _systemScreenshot;
    private nint[] _screenshotToolHandles = [];
    private nint _screenshotPeer;
    private long _screenshotStarted;
    private bool _screenshotPeerShown;
    /// <summary>Opt into system screenshot handoff after UI initialization. Return owned,
    /// bottom-to-top snapshots of visible tools on the UI thread; the host disposes them.</summary>
    public Func<IReadOnlyList<OverlayVisualSnapshot>>? CaptureToolVisuals { get; set; }
    /// <summary>A frozen ordinary-band view is temporarily representing this host.</summary>
    public bool IsSystemScreenshotActive => _systemScreenshot is not null;
    /// <summary>Restore only tools which the application still logically considers visible.</summary>
    public event Action<IReadOnlyList<nint>>? SystemScreenshotFinished;

    private bool TryBeginSystemScreenshot(bool releaseInput = true)
    {
        if (_systemScreenshot is not null) return true;
        if (_systemInput is null || CaptureToolVisuals is null || _captureActive || _disposed) return false;
        using var dpi = new ThreadDpiAwarenessScope(Capabilities);
        nint foreground = GetForegroundWindow();
        var rows = ReadWindowOrder();
        if (rows is null) return false;
        var peer = rows.LastOrDefault(row => IsSystemScreenshotWindow(row.Handle));
        if (peer.Handle == 0 && !IsSystemScreenshotWindow(foreground)) return false;
        // Foreground notifications can precede the overlay's first ShowWindow.
        // Conversely, delegated input can leave global foreground on the original
        // receiver while the system screenshot UI is already visible.
        return StartSystemScreenshot(peer.Handle != 0 ? peer.Handle : foreground, releaseInput);
    }

    private bool StartSystemScreenshot(nint peer, bool releaseInput)
    {
        try
        {
            BeginDesktopScreenshot(peer);
            return true;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or OutOfMemoryException)
        {
            // Presentation is optional; a failed allocation must never prevent input release.
            Trace.TraceWarning("System screenshot presentation failed: {0}", error);
            return false;
        }
        finally
        {
            if (releaseInput && IsEditing && _nativeDialogDepth == 0) CompleteEditingFocusLoss();
        }
    }

    private bool IsSystemScreenshotWindow(nint window)
    {
        if (window == 0 || GetWindowThreadProcessId(window, out uint process) == 0 || process == Environment.ProcessId ||
            !GetWindowRect(window, out var rect) || !_monitors.Any(m => rect.Left <= m.Bounds.Left && rect.Top <= m.Bounds.Top &&
                rect.Right >= m.Bounds.Right && rect.Bottom >= m.Bounds.Bottom)) return false;
        var name = new StringBuilder(128); GetClassNameW(window, name, name.Capacity);
        if (name.ToString() is not ("SnipOverlayRootWindow" or "XamlWindow" or "ScreenClippingWindow")) return false;
        // Normal Snipping Tool editor windows must not initiate this presentation.
        if (((long)GetWindowLongPtr(window, GwlExStyle) & ExTopmost) == 0) return false;
        try
        {
            using var candidate = Process.GetProcessById((int)process);
            if (candidate.ProcessName is not ("SnippingTool" or "ScreenClippingHost")) return false;
            if (name.ToString() != "XamlWindow") return true;
            // The editor is also XamlWindow. Require the capture host's root,
            // including its hidden pre-show state, rather than mistaking a pinned
            // maximized editor for an active screenshot.
            var visited = new HashSet<nint>();
            for (nint root = GetTopWindow(0); root != 0 && visited.Add(root); root = GetWindow(root, 2))
            {
                if (GetWindowThreadProcessId(root, out uint owner) == 0 || owner != process) continue;
                var rootName = new StringBuilder(128); GetClassNameW(root, rootName, rootName.Capacity);
                if (rootName.ToString() is "SnipOverlayRootWindow" or "ScreenClippingWindow") return true;
            }
            return false;
        }
        catch (ArgumentException) { return false; }
        catch (Win32Exception) { return false; }
    }

    private void BeginDesktopScreenshot(nint peer)
    {
        if (_systemScreenshot is not null) return;
        using var dpi = new ThreadDpiAwarenessScope(Capabilities);
        IReadOnlyList<OverlayVisualSnapshot> tools = CaptureToolVisuals?.Invoke() ?? [];
        var presentation = new List<(WindowState Window, DibBuffer Buffer)>();
        try
        {
            foreach (var pair in _windows)
            {
                var bounds = pair.Monitor.Bounds;
                var window = CreateNativeWindow(WindowKind.Display, pair.Monitor, bounds,
                    ExTopmost | ExToolWindow | ExLayered | ExTransparent | ExNoActivate, ordinaryDesktop: true);
                DibBuffer? buffer = null;
                try
                {
                    buffer = new(bounds.Width, bounds.Height);
                    var canvas = buffer.Surface.Canvas;
                    canvas.Clear(SKColors.Transparent);
                    if (IsWindowVisible(pair.Display.Handle)) canvas.DrawSurface(pair.Buffer.Surface, 0, 0);
                    canvas.Translate(-bounds.Left, -bounds.Top);
                    foreach (var tool in tools) canvas.DrawImage(tool.Pixels, tool.Bounds.Left, tool.Bounds.Top, new SKSamplingOptions(SKFilterMode.Nearest));
                    canvas.Flush(); buffer.Present(window.Handle, bounds);
                    presentation.Add((window, buffer));
                }
                catch { buffer?.Dispose(); DestroyWindow(window.Handle); throw; }
            }
            // All frames are prepared before any visibility changes. The ordinary-band
            // snapshot remains below the system peer; no foreground/input operation occurs.
            foreach (var item in presentation)
                if (!SetWindowPos(item.Window.Handle, IsWindowVisible(peer) ? peer : HwndTopmost, 0, 0, 0, 0, 0x253))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            _systemScreenshot = presentation;
            _screenshotPeer = peer; _screenshotStarted = Environment.TickCount64; _screenshotPeerShown = IsWindowVisible(peer);
            _screenshotToolHandles = tools.Select(tool => tool.Handle).Where(handle => handle != 0 &&
                GetWindowThreadProcessId(handle, out uint process) != 0 && process == Environment.ProcessId).Distinct().ToArray();
            foreach (var pair in _windows) ShowWindow(pair.Display.Handle, 0);
            foreach (var handle in _screenshotToolHandles) ShowWindow(handle, 0);
            if (SetTimer(ControlHandle, SystemScreenshotTimer, 50, 0) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            if (_systemScreenshot is not null) EndSystemScreenshot(restore: true);
            else foreach (var item in presentation) { DestroyWindow(item.Window.Handle); item.Buffer.Dispose(); }
            throw;
        }
        finally { foreach (var tool in tools) tool.Dispose(); }
    }

    private bool HandleSystemScreenshotMessage(uint message, nuint wParam)
    {
        if (message != 0x113 || wParam != SystemScreenshotTimer) return false;
        if (_systemScreenshot is null) return true;
        var rows = ReadWindowOrder();
        if (rows is null) return true;
        var peer = rows.LastOrDefault(row => row.Process != Environment.ProcessId && IsSystemScreenshotWindow(row.Handle));
        if (peer.Handle != 0)
        {
            _screenshotPeerShown = true;
            foreach (var item in _systemScreenshot)
                if (_screenshotPeer != peer.Handle || rows.Any(row => row.Handle == item.Window.Handle && row.Rank < peer.Rank))
                    _ = SetWindowPos(item.Window.Handle, peer.Handle, 0, 0, 0, 0, 0x213);
            _screenshotPeer = peer.Handle;
        }
        else if (_screenshotPeerShown || Environment.TickCount64 - _screenshotStarted >= 2000)
            EndSystemScreenshot(restore: true);
        return true;
    }

    private void EndSystemScreenshot(bool restore)
    {
        KillTimer(ControlHandle, SystemScreenshotTimer);
        var presentation = _systemScreenshot; _systemScreenshot = null;
        if (presentation is null) return;
        var tools = _screenshotToolHandles; _screenshotToolHandles = []; _screenshotPeer = 0;
        try
        {
            if (restore && !_disposed)
            {
                Render();
                foreach (var pair in _windows)
                    if (IsVisible || IsPreviewVisible) ShowWindowPreservingZOrder(pair.Display.Handle);
            }
            SystemScreenshotFinished?.Invoke(tools);
        }
        finally { foreach (var item in presentation) { DestroyWindow(item.Window.Handle); item.Buffer.Dispose(); } }
    }
}
