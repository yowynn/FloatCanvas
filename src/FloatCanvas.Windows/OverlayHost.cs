using L = FloatCanvas.Localization.AppText;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

/// <summary>
/// Owns a passive display window and a separate input window per monitor. Construct, use and dispose
/// on one UI thread running a Win32/WPF message pump. Every drawing/input callback runs on that thread.
/// </summary>
public sealed partial class OverlayHost : IDisposable
{
    private const string ClassName = "FloatCanvas.Overlay.v1";
    private const string NonRudePropertyName = "NonRudeHWND";
    private const int ToggleHotkeyId = 0x4641, HideHotkeyId = 0x4642;
    private static readonly WindowProcedure Procedure = Dispatch;
    private static readonly object ClassGate = new();
    private static bool _classRegistered;
    private readonly uint _threadId = GetCurrentThreadId();
    private readonly List<MonitorPair> _windows = [];
    private readonly WindowState _control;
    private readonly WinEventProcedure _foregroundProcedure;
    private readonly Func<IReadOnlyList<MonitorInfo>> _monitorProvider;
    private readonly WindowsCapabilities? _capabilities;
    private nint _foregroundHook;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private HotkeyBinding? _toggleHotkey, _hideHotkey;
    private nint _previousForeground;
    private OverlayMouseButton _pressedButton;
    private bool _releasingCapture, _refreshPosted, _disposed, _rendering;
    private long _focusCheckSession;
    private bool _captureActive;

    /// <summary>Own BGRA premultiplied raster target, fully cleared before each synchronous callback.
    /// Its canvas maps desktop physical pixels. Do not retain or dispose the surface.</summary>
    public event Action<SKSurface>? Draw;
    public event Action<OverlayPointerEvent>? Pointer;
    public event Action<int>? KeyPressed;
    public event Action<OverlayModifiers,OverlayPoint,uint>? ModifierKeysChanged;
    public event Action? ToggleRequested;
    public event Action? HideRequested;
    public event Action? MonitorsChanged;
    /// <summary>Input became active or monitor windows were rebuilt. Raise visible tools without changing keyboard focus.</summary>
    public event Action? InputActivated;
    /// <summary>Editing was released after focus or input isolation was lost. No foreground is restored.</summary>
    public event Action? EditingFocusLost;
    public event Action<Exception>? Error;

    public bool IsEditing { get; private set; }
    public bool IsVisible { get; private set; } = true;
    public OverlayMonitorScope Scope { get; private set; }
    public WindowsCapabilities Capabilities => _capabilities ?? WindowsCapabilities.Current;
    /// <summary>The active screen used for primary-screen placement, including the first-screen fallback.</summary>
    public MonitorInfo PrimaryMonitor => _monitors[0];
    /// <summary>Only the screens with overlay windows, and therefore the appropriate capture targets.</summary>
    public IReadOnlyList<MonitorInfo> Monitors => _monitors;
    public nint ControlHandle => _control.Handle;
    public nint CaptureHandle => GetCapture();
    private nint _pointerCursor;
    /// <summary>Use a caller-owned cursor for canvas input; zero restores the standard pointer.</summary>
    public void SetPointerCursor(nint cursor)
    {
        CheckThread(); _pointerCursor = cursor;
        if (IsEditing) SetCursor(cursor != 0 ? cursor : LoadCursorW(0, 32512));
    }
    public long PresentedFrameCount { get; private set; }
    public double LastRenderMilliseconds { get; private set; }
    /// <summary>Optional per-call timing. No timing records or output collections are allocated without a subscriber.</summary>
    public event Action<OverlayFrameTiming>? FrameSubmitted;
    /// <summary>Changes when an external foreground choice invalidates an in-flight edit request.</summary>
    public long ForegroundLossVersion { get; private set; }

    public IReadOnlyList<OverlayWindowDiagnostic> Diagnostics
    {
        get
        {
            CheckThread();
            using var dpi = new ThreadDpiAwarenessScope(Capabilities);
            return _windows.Select(pair => new OverlayWindowDiagnostic(pair.Display.Handle, pair.Input.Handle,
                ReadBounds(pair.Display.Handle), (long)GetWindowLongPtr(pair.Display.Handle, GwlExStyle),
                (long)GetWindowLongPtr(pair.Input.Handle, GwlExStyle), IsWindowVisible(pair.Display.Handle),
                IsWindowVisible(pair.Input.Handle))).ToArray();
        }
    }

    public static OverlayModifiers CurrentModifiers
    {
        get
        {
            var value = OverlayModifiers.None;
            if (GetKeyState(0x12) < 0) value |= OverlayModifiers.Alt;
            if (GetKeyState(0x11) < 0) value |= OverlayModifiers.Control;
            if (GetKeyState(0x10) < 0) value |= OverlayModifiers.Shift;
            if (GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0) value |= OverlayModifiers.Windows;
            return value;
        }
    }

    /// <summary>Current physical modifiers inside this host's private editing lease.
    /// Keyboard commands keep their packet-local CurrentModifiers for text/IME ordering.</summary>
    public OverlayModifiers InteractionModifiers => _keyboardIsolation is { } isolation && isolation.TryGetModifiers(null, out var modifiers)
        ? modifiers : CurrentModifiers;

    public OverlayHost() : this(OverlayMonitorScope.AllScreens) { }

    public OverlayHost(OverlayMonitorScope scope) : this(scope, EnumerateMonitors) { }

    // Hidden native-contract fixture: real monitor HWNDs without showing or activating a desktop surface.
    internal OverlayHost(OverlayMonitorScope scope, bool initiallyVisible) : this(scope, EnumerateMonitors, initiallyVisible) { }

    // Test seam for synthetic enumeration; public construction always enumerates real Windows screens.
    internal OverlayHost(OverlayMonitorScope scope, Func<IReadOnlyList<MonitorInfo>> monitorProvider, bool initiallyVisible = true)
        : this(scope, monitorProvider, initiallyVisible, WindowsCapabilities.Current) { }

    internal OverlayHost(OverlayMonitorScope scope, Func<IReadOnlyList<MonitorInfo>> monitorProvider,
        bool initiallyVisible, WindowsCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(monitorProvider);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        Scope = scope;
        IsVisible = initiallyVisible;
        _monitorProvider = monitorProvider;
        _capabilities = capabilities;
        if (!capabilities.IsSupported)
            throw new PlatformNotSupportedException(L.Get("Overlay.PlatformUnsupported"));
        RegisterWindowClass();
        using var dpi = new ThreadDpiAwarenessScope(Capabilities);
        _control = CreateNativeWindow(WindowKind.Control, null, new(0, 0, 1, 1), ExToolWindow | ExNoActivate);
        _foregroundProcedure = OnForegroundChanged;

        try
        {
            // Out-of-context delivery runs on our message-pump thread; no injection or polling.
            _foregroundHook = SetWinEventHook(3, 3, 0, _foregroundProcedure, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND
            if (_foregroundHook == 0) throw new InvalidOperationException(L.Get("Overlay.FocusMonitorFailed"));
            StartWindowOrderMonitoring();
            RebuildMonitors();
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Registers both keys as a transaction; a conflict restores the previous active bindings.</summary>
    public void RegisterHotkeys(HotkeyBinding toggle, HotkeyBinding hide)
    {
        CheckThread();
        ValidateHotkey(toggle);
        ValidateHotkey(hide);
        if (_keyboardIsolation is not null)
        {
            KeyboardInputIsolation.ValidateHotkey(toggle);
            KeyboardInputIsolation.ValidateHotkey(hide);
        }
        if (toggle == hide) throw new ArgumentException(L.Get("Hotkeys.BindingsMustDiffer"));
        if (_toggleHotkey == toggle && _hideHotkey == hide) return;
        var previousToggle = _toggleHotkey;
        var previousHide = _hideHotkey;
        UnregisterHotKey(ControlHandle, ToggleHotkeyId);
        UnregisterHotKey(ControlHandle, HideHotkeyId);
        var toggleRegistered = RegisterHotKey(ControlHandle, ToggleHotkeyId, toggle.Modifiers | 0x4000, toggle.VirtualKey);
        var failure = toggleRegistered ? 0 : Marshal.GetLastWin32Error();
        var hideRegistered = toggleRegistered && RegisterHotKey(ControlHandle, HideHotkeyId, hide.Modifiers | 0x4000, hide.VirtualKey);
        if (!hideRegistered)
        {
            if (toggleRegistered) failure = Marshal.GetLastWin32Error();
            UnregisterHotKey(ControlHandle, ToggleHotkeyId);
            UnregisterHotKey(ControlHandle, HideHotkeyId);
            var restoredToggle = previousToggle is null || RegisterHotKey(ControlHandle, ToggleHotkeyId,
                previousToggle.Value.Modifiers | 0x4000, previousToggle.Value.VirtualKey);
            var restoredHide = previousHide is null || RegisterHotKey(ControlHandle, HideHotkeyId,
                previousHide.Value.Modifiers | 0x4000, previousHide.Value.VirtualKey);
            if (!restoredToggle || !restoredHide)
            {
                _toggleHotkey = restoredToggle ? previousToggle : null;
                _hideHotkey = restoredHide ? previousHide : null;
                throw new Win32Exception(failure, L.Get("Hotkeys.ConflictAndRestoreFailed"));
            }
            throw new Win32Exception(failure, L.Get("Hotkeys.ConflictPreviousPreserved"));
        }
        _toggleHotkey = toggle;
        _hideHotkey = hide;
        _keyboardIsolation?.UpdateHotkeys(toggle, hide);
    }

    public void SetEditing(bool editing)
    {
        if (editing)
        {
            if (!TryEnterEditing(out var failure)) throw new InvalidOperationException(failure);
        }
        else ExitEditing();
    }

    /// <summary>Only enters editing after the input HWND actually owns foreground and keyboard focus.</summary>
    public bool TryEnterEditing(out string? failure)
    {
        CheckThread();
        CancelPendingCapture();
        if (_captureActive) { failure = L.Get("Capture.AlreadyInProgress"); return false; }
        return TryEnterEditingCore(out failure);
    }

    private bool TryEnterEditingCore(out string? failure)
    {
        EndDropReceptionCore();
        RequestWindowOrderCheck();
        using var dpi = new ThreadDpiAwarenessScope(Capabilities);
        failure = null;
        if (IsEditing && IsForegroundFromThisProcess() &&
            (_keyboardIsolation is null || _keyboardIsolation.IsLeaseActive)) return true;
        if (IsEditing) ExitEditingCore(restorePreviousForeground: false);
        CancelPointer();
        _previousForeground = GetForegroundWindow();


        foreach (var pair in _windows)
        {
            if (IsVisible) ShowWindowPreservingZOrder(pair.Display.Handle);
            else ShowWindow(pair.Display.Handle, 0);
            ShowWindow(pair.Input.Handle, 8);
            SetWindowPos(pair.Input.Handle, HwndTopmost, 0, 0, 0, 0, 0x13); // no move/size/activate
        }
        if (!ActivateInputAtCursor())
        {
            foreach (var pair in _windows) ShowWindow(pair.Input.Handle, 0);
            _previousForeground = 0;
            failure = L.Get("Overlay.FocusAcquisitionFailed");
            return false;
        }
        try { BeginKeyboardIsolation(); }
        catch (Exception error)
        {
            EndKeyboardIsolation();
            foreach (var pair in _windows) ShowWindow(pair.Input.Handle, 0);
            if (IsForegroundFromThisProcess() && _previousForeground != 0 && !OwnsWindow(_previousForeground))
                SetForegroundWindow(_previousForeground);
            _previousForeground = 0;
            failure = L.Format("Overlay.InputAcquisitionFailed", error.Message);
            return false;
        }
        IsEditing = true;
        _focusTransitions.BeginSession();
        Render();
        return true;
    }

    /// <summary>Releases input. Focus-loss exits must leave the user's newly chosen foreground alone.</summary>
    public void ExitEditing(bool restorePreviousForeground = true)
    {
        CheckThread();
        CancelPendingCapture();
        ExitEditingCore(restorePreviousForeground);
    }

    private void ExitEditingCore(bool restorePreviousForeground = true)
    {
        EndDropReceptionCore();
        if (!IsEditing) return;
        _focusTransitions.EndSession();
        EndKeyboardIsolation();
        CancelPointer();
        var wasForeground = IsForegroundFromThisProcess();
        IsEditing = false;
        foreach (var pair in _windows) ShowWindow(pair.Input.Handle, 0);
        if (restorePreviousForeground && _previousForeground != 0 && !OwnsWindow(_previousForeground) && wasForeground)
            SetForegroundWindow(_previousForeground);
        _previousForeground = 0;
    }

    public void SetVisible(bool visible)
    {
        CheckThread();
        if (_captureOperation is { } capture) capture.RestoreVisible = visible;
        CancelPendingCapture();
        SetVisibleCore(visible);
    }

    /// <summary>Applies the selected monitor set immediately without changing visibility or the editing lease.</summary>
    public void SetMonitorScope(OverlayMonitorScope scope)
    {
        CheckThread();
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (Scope == scope) return;
        var previous = Scope;
        Scope = scope;
        try { RebuildMonitors(); }
        catch { Scope = previous; throw; }
        MonitorsChanged?.Invoke();
    }

    private void SetVisibleCore(bool visible)
    {
        IsVisible = visible;
        // Keep both repeated requests and a real hidden-to-visible transition from
        // raising the canvas above the input carrier, toolbar or owned panels.
        // Capture scopes can change actual HWND visibility independently.
        foreach (var pair in _windows)
        {
            if (IsWindowVisible(pair.Display.Handle) == visible) continue;
            if (visible) ShowWindowPreservingZOrder(pair.Display.Handle);
            else ShowWindow(pair.Display.Handle, 0);
        }
        if (visible) Render();
        if (visible) RequestWindowOrderCheck();
    }

    /// <summary>Renders only when requested. Canvas coordinates are global desktop physical pixels.</summary>
    public void Render()
    {
        CheckThread();
        if (!IsVisible || _rendering) return;
        _rendering = true;
        var started = Stopwatch.GetTimestamp();
        var observer = FrameSubmitted;
        List<OverlayOutputTiming>? outputs = observer is null ? null : new(_windows.Count);
        bool succeeded = false;
        var completedBackend = OverlayRenderBackend.Cpu;
        try
        {
            using var dpi = new ThreadDpiAwarenessScope(Capabilities);
            var gpu = PrepareGpuRenderer();
            if (gpu is not null)
            {
                try
                {
                    using (gpu.MakeCurrent()) RenderOutputs(gpu);
                    completedBackend = OverlayRenderBackend.Gpu;
                    SetRenderBackend(OverlayRenderBackend.Gpu); GpuFrameCount++;
                }
                catch (Exception ex)
                {
                    FailGpu(ex); outputs?.Clear();
                    RenderOutputs(null); CpuFrameCount++;
                    completedBackend = OverlayRenderBackend.Cpu;
                }
            }
            else { RenderOutputs(null); SetRenderBackend(OverlayRenderBackend.Cpu); CpuFrameCount++; }
            LastCompletedRenderBackend = completedBackend;
            succeeded = true;
        }
        finally
        {
            long completed = Stopwatch.GetTimestamp();
            LastRenderMilliseconds = Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds;
            _rendering = false;
            if (observer is not null) observer(new(started, completed, succeeded, outputs!.AsReadOnly()));
        }

        void RenderOutputs(GpuRenderContext? gpu)
        {
            foreach (var pair in _windows)
            {
                long outputStarted = observer is null ? 0 : Stopwatch.GetTimestamp();
                var surface = gpu?.SurfaceFor(pair.Buffer) ?? pair.Buffer.Surface;
                var canvas = surface.Canvas;
                canvas.RestoreToCount(1);
                canvas.ResetMatrix();
                canvas.Clear(SKColors.Transparent);
                canvas.Save();
                canvas.Translate(-pair.Monitor.Bounds.Left, -pair.Monitor.Bounds.Top);
                Draw?.Invoke(surface);
                canvas.RestoreToCount(1);
                canvas.Flush();
                if (gpu is not null) gpu.ReadPixels(surface, pair.Buffer);
                long presentStarted = observer is null ? 0 : Stopwatch.GetTimestamp();
                pair.Buffer.Present(pair.Display.Handle, pair.Monitor.Bounds);
                PresentedFrameCount++;
                if (outputs is not null)
                {
                    long completed = Stopwatch.GetTimestamp();
                    outputs.Add(new(pair.Monitor.DeviceName, pair.Monitor.Bounds, outputStarted, completed,
                        Stopwatch.GetElapsedTime(outputStarted, presentStarted).TotalMilliseconds,
                        Stopwatch.GetElapsedTime(presentStarted, completed).TotalMilliseconds));
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        CheckThread();
        StopWindowOrderMonitoring();
        CancelPendingCapture();
        EndDropReceptionCore();
        _focusTransitions.EndSession();
        DisposeKeyboardIsolation();
        if (_foregroundHook != 0) { UnhookWinEvent(_foregroundHook); _foregroundHook = 0; }
        CancelPointer();
        IsEditing = false;
        IsVisible = false;
        if (_control.Handle != 0)
        {
            UnregisterHotKey(_control.Handle, ToggleHotkeyId);
            UnregisterHotKey(_control.Handle, HideHotkeyId);
        }
        DestroyPairs();
        DisposeImageDrops();
        ReleaseGpuRenderer();
        if (_control.Handle != 0) DestroyWindow(_control.Handle);
        _disposed = true;
    }

    private void CheckThread()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (GetCurrentThreadId() != _threadId) throw new InvalidOperationException(L.Get("Overlay.RequiresCreatorUiThread"));
    }

    private static void ValidateHotkey(HotkeyBinding key)
    {
        if (key.VirtualKey is 0 or > 0xFE || (key.Modifiers & ~15u) != 0)
            throw new ArgumentException(L.Get("Hotkeys.InvalidBinding"));
    }

    private static IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();
        MonitorEnumProcedure callback = (nint monitor, nint dc, ref Rect rect, nint data) =>
        {
            var info = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>(), Device = string.Empty };
            if (!GetMonitorInfoW(monitor, ref info)) return true;
            if (GetDpiForMonitor(monitor, 0, out var x, out var y) != 0) x = y = 96;
            monitors.Add(new(info.Device, info.Monitor.Bounds, info.Work.Bounds, x, y, (info.Flags & 1) != 0));
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0) || monitors.Count == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Overlay.DisplayLayoutReadFailed"));
        return monitors;
    }

    private void RebuildMonitors()
    {
        bool wasRebuilding = _windowOrderRebuilding;
        _windowOrderRebuilding = true;
        try { RebuildMonitorsCore(); }
        finally
        {
            _windowOrderRebuilding = wasRebuilding;
            if (!wasRebuilding) RequestWindowOrderCheck();
        }
    }

    private void RebuildMonitorsCore()
    {
        using var dpi = new ThreadDpiAwarenessScope(Capabilities);
        var monitors = OverlayMonitorSelection.Select(_monitorProvider(), Scope);
        if (_captureActive) CancelPendingCapture();
        var foreground = GetForegroundWindow();
        bool preserveToolFocus = IsEditing && IsForegroundFromThisProcess() && !OwnsWindow(foreground);
        EndDropReceptionCore();
        CancelPointer();
        var previousWindows = _windows.ToArray();
        var previousMonitors = _monitors;
        ReleaseMonitorSurfaces();
        _windows.Clear();
        _monitors = monitors;
        try
        {
            foreach (var monitor in monitors)
            {
                WindowState? display = null, input = null;
                DibBuffer? buffer = null;
                try
                {
                    display = CreateNativeWindow(WindowKind.Display, monitor, monitor.Bounds,
                        ExTopmost | ExToolWindow | ExLayered | ExTransparent | ExNoActivate);
                    input = CreateNativeWindow(WindowKind.Input, monitor, monitor.Bounds, ExTopmost | ExToolWindow | ExLayered | ExNoActivate);
                    if (!SetLayeredWindowAttributes(input.Handle, 0, 1, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    buffer = new(monitor.Bounds.Width, monitor.Bounds.Height);
                    _windows.Add(new(monitor, display, input, buffer));
                }
                catch
                {
                    buffer?.Dispose();
                    if (input?.Handle != 0 && input is not null) DestroyWindow(input.Handle);
                    if (display?.Handle != 0 && display is not null) DestroyWindow(display.Handle);
                    throw;
                }
            }
            RegisterImageDropTargets();
            Render();
            if (IsEditing)
            {
                // Establish the full stack while the new windows are hidden, so even the
                // first displayed frame stays below input and the owner's tool windows.
                foreach (var pair in _windows)
                    SetWindowPos(pair.Input.Handle, HwndTopmost, 0, 0, 0, 0, 0x13);
            }
            InputActivated?.Invoke();
            foreach (var pair in _windows)
            {
                if (IsVisible) ShowWindowPreservingZOrder(pair.Display.Handle);
                if (IsEditing) ShowWindowPreservingZOrder(pair.Input.Handle);
            }
        }
        catch
        {
            DestroyPairs();
            _windows.AddRange(previousWindows);
            _monitors = previousMonitors;
            throw;
        }
        // Commit only after every candidate HWND, first render and first show succeeded.
        // A failed attempt leaves the old visible/input windows and editing lease intact.
        foreach (var pair in previousWindows) DestroyMonitorPair(pair);
        if (IsEditing && !preserveToolFocus && !ActivateInputAtCursor()) EndEditingAfterFocusLoss();
    }

    private void DestroyPairs()
    {
        ReleaseMonitorSurfaces();
        foreach (var pair in _windows) DestroyMonitorPair(pair);
        _windows.Clear();
    }

    private void ReleaseMonitorSurfaces()
    {
        if (_gpuRenderer is { } gpu)
        {
            try { using (gpu.MakeCurrent()) gpu.ReleaseSurfaces(); }
            catch (Exception ex) { FailGpu(ex); }
        }
    }

    private void DestroyMonitorPair(MonitorPair pair)
    {
        RevokeImageDropTarget(pair.Input.Handle);
        DestroyWindow(pair.Input.Handle);
        DestroyWindow(pair.Display.Handle);
        pair.Buffer.Dispose();
    }

    private bool OwnsWindow(nint hwnd) => hwnd == ControlHandle || _windows.Any(pair => pair.Input.Handle == hwnd || pair.Display.Handle == hwnd);

    private bool ActivateInputAtCursor()
    {
        GetCursorPos(out var cursor);
        var active = _windows.FirstOrDefault(pair => pair.Monitor.Bounds.Contains(new(cursor.X, cursor.Y))) ?? _windows.First();
        SetForegroundWindow(active.Input.Handle);
        if (GetForegroundWindow() != active.Input.Handle) return false;
        SetFocus(active.Input.Handle);
        return GetForegroundWindow() == active.Input.Handle && GetFocus() == active.Input.Handle;
    }

    private static bool IsForegroundFromThisProcess()
    {
        var foreground = GetForegroundWindow();
        return foreground != 0 && GetWindowThreadProcessId(foreground, out var processId) != 0 && processId == Environment.ProcessId;
    }

    private void OnForegroundChanged(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint threadId, uint eventTime)
    {
        // Defer through our control window so own toolbars, dialogs and IME transitions can settle.
        // Never unwind managed exceptions across a native callback or activate another window here.
        if (_disposed) return;
        RequestWindowOrderCheck();
        if (_captureActive)
        {
            // Use the event's own target: reading only the latest foreground would miss
            // an external switch followed by a return before this callback is delivered.
            if (_captureOperation is { } capture)
            {
                bool ownProcessWindow = GetWindowThreadProcessId(hwnd, out var process) != 0 && process == Environment.ProcessId;
                capture.ObserveForeground(hwnd, eventTime, ownProcessWindow);
                ReportCapture(capture, "ForegroundEvent", capture.Reason, eventForeground: hwnd, eventTime: eventTime);
            }
            if (IsEditing && !IsForegroundFromThisProcess()) EndEditingAfterFocusLoss();
            return;
        }

        PostEditingFocusValidation();
    }

    private void EndEditingAfterFocusLoss()
    {
        ExitEditing(restorePreviousForeground: false);
        ForegroundLossVersion++;
        EditingFocusLost?.Invoke();
    }

    private void CancelPointer()
    {
        var hadCapture = _windows.Any(pair => pair.Input.Handle == GetCapture());
        var hadButton = _pressedButton != OverlayMouseButton.None;
        _releasingCapture = true;
        try { if (hadCapture) ReleaseCapture(); }
        finally { _releasingCapture = false; }
        _pressedButton = OverlayMouseButton.None;
        if (hadCapture || hadButton) RaisePointer(OverlayPointerKind.Cancel, OverlayMouseButton.None);
    }

    private void RaisePointer(OverlayPointerKind kind, OverlayMouseButton button, int wheel = 0, OverlayPoint? position = null,
        uint? messageTime = null)
    {
        using var dpi = new ThreadDpiAwarenessScope(Capabilities);
        GetCursorPos(out var point);
        var pointerPosition = position ?? new(point.X, point.Y);
        // Suppressed keys do not share the native mouse queue. Reading GetKeyState here
        // can see an older private packet, while the latest hook snapshot can include a
        // newer release. A button press uses its exact hook snapshot (including same-ms
        // ordering); continuous movement resolves its native event-time history.
        uint inputTime=messageTime??unchecked((uint)GetMessageTime());
        var modifiers = _keyboardIsolation is { } isolation && isolation.TryGetPointerModifiers(inputTime, pointerPosition, button,
            kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick, out var isolated)
            ? isolated : CurrentModifiers;
        Pointer?.Invoke(new(kind, pointerPosition, button, modifiers, wheel) {Time=inputTime});
    }

    private static OverlayPoint MessagePosition(WindowState state, uint message, nint lParam)
    {
        using var dpi = new ThreadDpiAwarenessScope(state.Owner.Capabilities);
        var point = new Point((short)((long)lParam & 0xFFFF), (short)(((long)lParam >> 16) & 0xFFFF));
        if (message != WmMouseWheel) ClientToScreen(state.Handle, ref point);
        return new(point.X, point.Y);
    }

    private nint OnMessage(WindowState state, uint message, nuint wParam, nint lParam)
    {
        if (state.Kind == WindowKind.Input && message == WmMouseMove) ObservePointerMessage(state, message, wParam, lParam);
        if (state.Kind == WindowKind.Input && (message is >= 0x100 and <= 0x109 or >= 0x200 and <= 0x20E))
            NotifyInputProgress();
        if (message == WmClose) return 0; // process lifetime is controlled through the tray/application
        if (message == WmEraseBackground) return 1;
        if (message == WmPaint)
        {
            var dc = BeginPaint(state.Handle, out var paint);
            if (state.Kind == WindowKind.Input && state.Monitor is not null)
            {
                var rect = new Rect { Right = state.Monitor.Bounds.Width, Bottom = state.Monitor.Bounds.Height };
                FillRect(dc, ref rect, GetStockObject(4)); // opaque surface at global alpha 1
            }
            EndPaint(state.Handle, ref paint);
            return 0;
        }
        if (state.Kind == WindowKind.Control)
        {
            if (HandleWindowOrderMessage(message, wParam)) return 0;
            if (HandleKeyboardControlMessage(message, wParam)) return 0;
            if (message == WmValidateEditingFocus)
            {
                long session = unchecked((long)wParam);
                if (_focusCheckSession == session) _focusCheckSession = 0;
                ValidateEditingFocus(session);
                return 0;
            }
            if (message == CompleteFocusTransitionMessage)
            {
                CompleteFocusTransition(new(unchecked((long)wParam), unchecked((long)lParam)));
                return 0;
            }
            if (message == WmHotkey)
            {
                if (_captureActive)
                {
                    if ((int)wParam == ToggleHotkeyId) CancelPendingCapture();
                    else if ((int)wParam == HideHotkeyId) SetVisible(false);
                    return 0;
                }
                if ((int)wParam == ToggleHotkeyId) ToggleRequested?.Invoke();
                else if ((int)wParam == HideHotkeyId) HideRequested?.Invoke();
                return 0;
            }
            if (message == WmRefreshMonitors)
            {
                _refreshPosted = false;
                RebuildMonitors();
                MonitorsChanged?.Invoke();
                return 0;
            }
        }
        if (message == WmDpiChanged && state.Handle == _captureGeometryWindow) return 0;
        if (message == WmDisplayChange || message == WmDpiChanged)
        {
            if (!_refreshPosted && !_disposed)
            {
                _refreshPosted = true;
                PostMessageW(ControlHandle, WmRefreshMonitors, 0, 0);
            }
            return 0;
        }
        if (state.Kind != WindowKind.Input) return DefWindowProcW(state.Handle, message, wParam, lParam);
        // Keep the original mouse message, but activate only after a new canvas press
        // passes the editing, modal, focus and capture checks below. OLE never activates.
        if (message == 0x21) return 3; // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (message == WmActivate && (wParam & 0xFFFF) != 0)
        {
            // Windows raises the activated full-screen input HWND, even when its pixels
            // are almost transparent. Keep app tool windows physically above it after
            // normal activation has assigned focus, without stealing that focus back.
            var result = DefWindowProcW(state.Handle, message, wParam, lParam);
            if (IsEditing) InputActivated?.Invoke();
            return result;
        }
        if (message is WmCancelMode or WmKillFocus || (message == WmCaptureChanged && !_releasingCapture))
        {
            if (_captureActive && message == WmCaptureChanged && !_releasingCapture)
                _captureOperation?.Cancel(CaptureRestoreStatus.Cancelled, "PointerCaptureLost");
            CancelPointer();
            if (message == WmKillFocus) PostEditingFocusValidation();
            return 0;
        }
        if (!IsEditing) return DefWindowProcW(state.Handle, message, wParam, lParam);
        if (message == 0x20) { SetCursor(_pointerCursor != 0 ? _pointerCursor : LoadCursorW(0, 32512)); return 1; }
        if(message is WmKeyDown or WmSysKeyDown or 0x101 or 0x105&&wParam is 0x10 or 0xA0 or 0xA1)
        {
            uint cursor=GetMessagePos();
            ModifierKeysChanged?.Invoke(CurrentModifiers,new((short)(cursor&0xFFFF),(short)(cursor>>16)),unchecked((uint)GetMessageTime()));
        }
        if (message is WmKeyDown or WmSysKeyDown) { KeyPressed?.Invoke((int)wParam); return 0; }
        if (message == WmMouseMove) { RaisePointer(OverlayPointerKind.Move, _pressedButton, position: MessagePosition(state, message, lParam)); return 0; }
        if (message == WmMouseWheel) { RaisePointer(OverlayPointerKind.Wheel, OverlayMouseButton.None, (short)((long)wParam >> 16), MessagePosition(state, message, lParam)); return 0; }
        var button = message switch
        {
            WmLeftDown or WmLeftUp or WmLeftDouble => OverlayMouseButton.Left,
            WmRightDown or WmRightUp or WmRightDouble => OverlayMouseButton.Right,
            WmMiddleDown or WmMiddleUp or WmMiddleDouble => OverlayMouseButton.Middle,
            _ => OverlayMouseButton.None
        };
        if (button != OverlayMouseButton.None)
        {
            if (message is WmLeftUp or WmRightUp or WmMiddleUp)
            {
                if (_pressedButton != button) return 0;
                RaisePointer(OverlayPointerKind.Up, button, position: MessagePosition(state, message, lParam));
                _pressedButton = OverlayMouseButton.None;
                _releasingCapture = true;
                try { ReleaseCapture(); }
                finally { _releasingCapture = false; }
            }
            else
            {
                if (!TryBeginCanvasPointer(state)) return 0;
                _pressedButton = button;
                RaisePointer(message is WmLeftDouble or WmRightDouble or WmMiddleDouble ? OverlayPointerKind.DoubleClick : OverlayPointerKind.Down, button,
                    position: MessagePosition(state, message, lParam));
            }
            return 0;
        }
        return DefWindowProcW(state.Handle, message, wParam, lParam);
    }

    private WindowState CreateNativeWindow(WindowKind kind, MonitorInfo? monitor, PixelBounds bounds, uint extendedStyle)
    {
        var state = new WindowState(this, kind, monitor);
        var reference = GCHandle.Alloc(state);
        var hwnd = CreateWindowExW(extendedStyle, ClassName, kind == WindowKind.Control ? "FloatCanvas Control" : $"FloatCanvas {kind}",
            WsPopup, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0, 0, GetModuleHandleW(null), GCHandle.ToIntPtr(reference));
        if (hwnd == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (!state.Freed) reference.Free();
            throw new Win32Exception(error, L.Get("Overlay.WindowCreationFailed"));
        }
        // The full monitor client area is an effect/input surface, not a fullscreen application.
        // Set the Shell's documented opt-out before any ShowWindow, including layout rebuilds.
        // https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist2-markfullscreenwindow
        if (!SetPropW(hwnd, NonRudePropertyName, 1))
        {
            var error = Marshal.GetLastPInvokeError();
            DestroyWindow(hwnd);
            throw new Win32Exception(error, L.Get("Overlay.WindowCreationFailed"));
        }
        return state;
    }

    private static void RegisterWindowClass()
    {
        lock (ClassGate)
        {
            if (_classRegistered) return;
            var value = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Style = 8, Procedure = Procedure,
                Instance = GetModuleHandleW(null), Cursor = LoadCursorW(0, 32512), ClassName = ClassName
            };
            if (RegisterClassExW(ref value) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _classRegistered = true;
        }
    }

    private static nint Dispatch(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        WindowState? state = null;
        var pointer = GetWindowLongPtr(hwnd, GwlpUserData);
        if (message == WmNcCreate)
        {
            pointer = Marshal.PtrToStructure<CreateStructure>(lParam).Parameter;
            SetWindowLongPtr(hwnd, GwlpUserData, pointer);
        }
        if (pointer != 0) { state = (WindowState)GCHandle.FromIntPtr(pointer).Target!; state.Handle = hwnd; }
        if (message == WmNcDestroy)
        {
            // SetProp's contract requires our property entries to be removed before WM_NCDESTROY returns.
            RemovePropW(hwnd, NonRudePropertyName);
            SetWindowLongPtr(hwnd, GwlpUserData, 0);
            if (state is not null) { state.Freed = true; state.Handle = 0; GCHandle.FromIntPtr(pointer).Free(); }
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }
        if (state is null) return DefWindowProcW(hwnd, message, wParam, lParam);
        try { return state.Owner.OnMessage(state, message, wParam, lParam); }
        catch (Exception error)
        {
            // Never unwind a managed exception through the native window procedure.
            try { state.Owner.CancelPointer(); state.Owner.Error?.Invoke(error); } catch { }
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private static PixelBounds ReadBounds(nint hwnd) => GetWindowRect(hwnd, out var rect) ? rect.Bounds : default;
    private enum WindowKind { Control, Display, Input }
    private sealed class WindowState(OverlayHost owner, WindowKind kind, MonitorInfo? monitor)
    {
        internal readonly OverlayHost Owner = owner;
        internal readonly WindowKind Kind = kind;
        internal readonly MonitorInfo? Monitor = monitor;
        internal nint Handle;
        internal bool Freed;
    }
    private sealed record MonitorPair(MonitorInfo Monitor, WindowState Display, WindowState Input, DibBuffer Buffer);

}
