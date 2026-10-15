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
    private const int ToggleHotkeyId = 0x4641, HideHotkeyId = 0x4642;
    private static readonly WindowProcedure Procedure = Dispatch;
    private static readonly object ClassGate = new();
    private static bool _classRegistered;
    private readonly uint _threadId = GetCurrentThreadId();
    private readonly List<MonitorPair> _windows = [];
    private readonly WindowState _control;
    private readonly WinEventProcedure _foregroundProcedure;
    private readonly Func<IReadOnlyList<MonitorInfo>> _monitorProvider;
    private nint _foregroundHook;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private HotkeyBinding? _toggleHotkey, _hideHotkey;
    private nint _previousForeground;
    private OverlayMouseButton _pressedButton;
    private bool _releasingCapture, _refreshPosted, _focusCheckPosted, _disposed, _rendering;
    private bool _captureActive;
    private nint _captureForeground;
    private bool _captureFocusMoved;

    /// <summary>Own BGRA premultiplied raster target, fully cleared before each synchronous callback.
    /// Its canvas maps desktop physical pixels. Do not retain or dispose the surface.</summary>
    public event Action<SKSurface>? Draw;
    public event Action<OverlayPointerEvent>? Pointer;
    public event Action<int>? KeyPressed;
    public event Action? ToggleRequested;
    public event Action? HideRequested;
    public event Action? MonitorsChanged;
    /// <summary>Editing was released after focus or input isolation was lost. No foreground is restored.</summary>
    public event Action? EditingFocusLost;
    public event Action<Exception>? Error;

    public bool IsEditing { get; private set; }
    public bool IsVisible { get; private set; } = true;
    public OverlayMonitorScope Scope { get; }
    /// <summary>The active screen used for primary-screen placement, including the first-screen fallback.</summary>
    public MonitorInfo PrimaryMonitor => _monitors[0];
    /// <summary>Only the screens with overlay windows, and therefore the appropriate capture targets.</summary>
    public IReadOnlyList<MonitorInfo> Monitors => _monitors;
    public nint ControlHandle => _control.Handle;
    public nint CaptureHandle => GetCapture();
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
            using var dpi = new PhysicalDpiScope();
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

    public OverlayHost() : this(OverlayMonitorScope.AllScreens) { }

    public OverlayHost(OverlayMonitorScope scope) : this(scope, EnumerateMonitors) { }

    // Test seam for synthetic enumeration; public construction always enumerates real Windows screens.
    internal OverlayHost(OverlayMonitorScope scope, Func<IReadOnlyList<MonitorInfo>> monitorProvider)
    {
        ArgumentNullException.ThrowIfNull(monitorProvider);
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        Scope = scope;
        _monitorProvider = monitorProvider;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19045))
            throw new PlatformNotSupportedException(L.Get("Overlay.PlatformUnsupported"));
        RegisterWindowClass();
        using var dpi = new PhysicalDpiScope();
        _control = CreateNativeWindow(WindowKind.Control, null, new(0, 0, 1, 1), ExToolWindow | ExNoActivate);
        _foregroundProcedure = OnForegroundChanged;
        try
        {
            // Out-of-context delivery runs on our message-pump thread; no injection or polling.
            _foregroundHook = SetWinEventHook(3, 3, 0, _foregroundProcedure, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND
            if (_foregroundHook == 0) throw new InvalidOperationException(L.Get("Overlay.FocusMonitorFailed"));
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
        using var dpi = new PhysicalDpiScope();
        failure = null;
        if (IsEditing && IsForegroundFromThisProcess() &&
            (_keyboardIsolation is null || _keyboardIsolation.IsLeaseActive)) return true;
        if (IsEditing) ExitEditing(restorePreviousForeground: false);
        CancelPointer();
        _previousForeground = GetForegroundWindow();
        IsVisible = true;
        foreach (var pair in _windows)
        {
            ShowWindow(pair.Display.Handle, 8); // SW_SHOWNA
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
        Render();
        return true;
    }

    /// <summary>Releases input. Focus-loss exits must leave the user's newly chosen foreground alone.</summary>
    public void ExitEditing(bool restorePreviousForeground = true)
    {
        CheckThread();
        if (!IsEditing) return;
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
        if (!visible) SetEditing(false);
        IsVisible = visible;
        // SW_SHOWNA also changes z order. A scene switch may call this while the
        // toolbar is already visible; re-showing an opaque display would cover it.
        // Reconcile actual HWND visibility (capture scopes can change it directly)
        // without turning an idempotent visibility request into a raise operation.
        foreach (var pair in _windows)
            if (IsWindowVisible(pair.Display.Handle) != visible) ShowWindow(pair.Display.Handle, visible ? 8 : 0);
        if (visible) Render();
    }

    /// <summary>
    /// Temporarily excludes and hides this host's windows and optional application tool windows.
    /// Dispose on the creating UI thread after capture, including failure/cancellation paths.
    /// The timestamp can be passed to CaptureService to reject frames presented before hiding completed.
    /// </summary>
    public CaptureVisibilityScope HideForCapture(IEnumerable<nint>? additionalWindows = null)
    {
        CheckThread();
        if (_captureActive) throw new InvalidOperationException(L.Get("Capture.AlreadyInProgress"));
        var editing = IsEditing;
        var visible = IsVisible;
        var handles = _windows.SelectMany(pair => new[] { pair.Display.Handle, pair.Input.Handle })
            .Concat(additionalWindows ?? []).Where(handle => handle != 0).Distinct().ToArray();
        _captureActive = true;
        _captureForeground = 0;
        _captureFocusMoved = false;
        try
        {
            return new CaptureVisibilityScope(handles, () =>
            {
                if (_disposed) return;
                CheckThread();
                // The capture continuation may run before a queued foreground WinEvent.
                // Unknown or changed external foreground means there is no safe restore lease.
                var foreground = GetForegroundWindow();
                if (_captureForeground == 0 || (foreground != _captureForeground && !IsForegroundFromThisProcess()))
                    _captureFocusMoved = true;
                _captureActive = false;
                SetVisible(visible);
                if (_captureFocusMoved)
                {
                    // The scope has restored its original visibility, including input HWNDs.
                    foreach (var pair in _windows) ShowWindow(pair.Input.Handle, 0);
                    EndEditingAfterFocusLoss();
                }
                else if (editing && !TryEnterEditing(out _)) EndEditingAfterFocusLoss();
            }, () =>
            {
                SetEditing(false); SetVisible(false);
                _captureForeground = GetForegroundWindow();
            });
        }
        catch { _captureActive = false; throw; }
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
        try
        {
            using var dpi = new PhysicalDpiScope();
            foreach (var pair in _windows)
            {
                long outputStarted = observer is null ? 0 : Stopwatch.GetTimestamp();
                var canvas = pair.Buffer.Surface.Canvas;
                canvas.RestoreToCount(1);
                canvas.ResetMatrix();
                canvas.Clear(SKColors.Transparent);
                canvas.Save();
                canvas.Translate(-pair.Monitor.Bounds.Left, -pair.Monitor.Bounds.Top);
                Draw?.Invoke(pair.Buffer.Surface);
                canvas.RestoreToCount(1);
                canvas.Flush();
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
            succeeded = true;
        }
        finally
        {
            long completed = Stopwatch.GetTimestamp();
            LastRenderMilliseconds = Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds;
            _rendering = false;
            if (observer is not null) observer(new(started, completed, succeeded, outputs!.AsReadOnly()));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        CheckThread();
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
        using var dpi = new PhysicalDpiScope();
        CancelPointer();
        var monitors = OverlayMonitorSelection.Select(_monitorProvider(), Scope);
        DestroyPairs();
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
                    input = CreateNativeWindow(WindowKind.Input, monitor, monitor.Bounds, ExTopmost | ExToolWindow | ExLayered);
                    if (!SetLayeredWindowAttributes(input.Handle, 0, 1, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    buffer = new(monitor.Bounds.Width, monitor.Bounds.Height);
                    _windows.Add(new(monitor, display, input, buffer));
                    if (IsVisible) ShowWindow(display.Handle, 8);
                    if (IsEditing) ShowWindow(input.Handle, 8);
                }
                catch
                {
                    buffer?.Dispose();
                    if (input?.Handle != 0 && input is not null) DestroyWindow(input.Handle);
                    if (display?.Handle != 0 && display is not null) DestroyWindow(display.Handle);
                    throw;
                }
            }
            Render();
            if (IsEditing && !ActivateInputAtCursor()) EndEditingAfterFocusLoss();
        }
        catch { DestroyPairs(); IsEditing = false; throw; }
    }

    private void DestroyPairs()
    {
        foreach (var pair in _windows)
        {
            DestroyWindow(pair.Input.Handle);
            DestroyWindow(pair.Display.Handle);
            pair.Buffer.Dispose();
        }
        _windows.Clear();
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
        if (_captureActive)
        {
            var foreground = GetForegroundWindow();
            if (_captureForeground != 0 && foreground != 0 && foreground != _captureForeground && !IsForegroundFromThisProcess())
                _captureFocusMoved = true;
            return;
        }
        if (!IsEditing || _focusCheckPosted) return;
        _focusCheckPosted = PostMessageW(ControlHandle, WmValidateEditingFocus, 0, 0);
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

    private void RaisePointer(OverlayPointerKind kind, OverlayMouseButton button, int wheel = 0, OverlayPoint? position = null)
    {
        using var dpi = new PhysicalDpiScope();
        GetCursorPos(out var point);
        Pointer?.Invoke(new(kind, position ?? new(point.X, point.Y), button, CurrentModifiers, wheel));
    }

    private static OverlayPoint MessagePosition(WindowState state, uint message, nint lParam)
    {
        using var dpi = new PhysicalDpiScope();
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
            if (HandleKeyboardControlMessage(message, wParam)) return 0;
            if (message == WmValidateEditingFocus)
            {
                _focusCheckPosted = false;
                if (IsEditing && !IsForegroundFromThisProcess()) EndEditingAfterFocusLoss();
                return 0;
            }
            if (message == WmHotkey)
            {
                if (_captureActive) return 0;
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
        if (message is WmCancelMode or WmKillFocus || (message == WmCaptureChanged && !_releasingCapture))
        {
            CancelPointer();
            if (message == WmKillFocus && IsEditing && !_focusCheckPosted)
                _focusCheckPosted = PostMessageW(ControlHandle, WmValidateEditingFocus, 0, 0);
            return 0;
        }
        if (!IsEditing) return DefWindowProcW(state.Handle, message, wParam, lParam);
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
                if (_pressedButton != OverlayMouseButton.None) return 0;
                _pressedButton = button;
                SetCapture(state.Handle);
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

    private readonly struct PhysicalDpiScope : IDisposable
    {
        private readonly nint _previous;
        public PhysicalDpiScope() => _previous = SetThreadDpiAwarenessContext(DpiPerMonitorV2);
        public void Dispose() { if (_previous != 0) SetThreadDpiAwarenessContext(_previous); }
    }
}
