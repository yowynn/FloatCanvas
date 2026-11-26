using System.Windows;
using System.Windows.Interop;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private PassThroughLauncherWindow? _passThroughLauncher;
    private string? _systemInputEntryHint;

    private void ShowSystemInputEntryBlocked(IReadOnlyList<uint> keys)
    {
        var stateWrites=KeyboardStateTrace.Recent(keys);
        // The native pending request has already been cancelled. Hide editing UI
        // without switching documents or taking focus from the external window.
        _overlayWindows?.ClosePopups();
        _windowCoordinator?.SuspendEditingWindows();
        ClearProjectionHover(); EndCanvasCalibration(restoreWindows: false);
        CancelSnapshotCapture("EntryBlocked", userInitiated: false);
        CloseImageDrop(); _editor.Cancel(); EndEyedropper(restore: false);
        _toolbar.Hide(); _inspector?.Hide(); _layers?.Hide(); _stampWindow?.Hide(); CloseToolOptions();

        string names = FormatSystemInputEntryBlockers(keys);
        _systemInputEntryHint = names.Length == 0 ? L.Get("Input.EntryRecovery.Unknown") : L.Format("Input.EntryRecovery.Blocked", names);
        _diagnostics.Write("Input.EntryBlocked", new { BlockerCount = keys.Distinct().Count(), Blockers=keys,
            NativeTrace=_host.LastSystemInputEntryTrace, StateWrites=stateWrites });
        UpdateStatus(_systemInputEntryHint);
        RefreshPassThroughLauncher(); RequestRender();
        _tray.ShowBalloonTip(6000, L.Get("Input.EntryRecovery.Title"), _systemInputEntryHint, System.Windows.Forms.ToolTipIcon.Info);
    }

    private void ClearSystemInputEntryHint()
    {
        _systemInputEntryHint = null;
        _passThroughLauncher?.SetEntryHint(null);
    }

    internal static string FormatSystemInputEntryBlockers(IReadOnlyList<uint> keys)
    {
        uint Normalize(uint key) => key switch
        {
            0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12,
            0x5C => 0x5B, _ => key
        };
        string Name(uint key) => key switch
        {
            0x01 => L.Get("Input.EntryRecovery.MouseLeft"), 0x02 => L.Get("Input.EntryRecovery.MouseRight"),
            0x04 => L.Get("Input.EntryRecovery.MouseMiddle"), 0x05 => L.Get("Input.EntryRecovery.MouseX1"),
            0x06 => L.Get("Input.EntryRecovery.MouseX2"), 0x10 => L.Get("Input.EntryRecovery.Shift"),
            0x11 => L.Get("Input.EntryRecovery.Control"), 0x12 => L.Get("Input.EntryRecovery.Alt"),
            0x5B => L.Get("Input.EntryRecovery.Windows"), _ => HotkeyDisplay.KeyName(key)
        };
        var names = keys.Select(Normalize).Distinct().Select(Name).ToArray();
        var shown = names.Take(8);
        if (names.Length > 8) shown = shown.Append(L.Format("Input.EntryRecovery.More", names.Length - 8));
        return string.Join(L.Get("Input.EntryRecovery.Separator"), shown);
    }

    private void ToggleFloatingLogo()
    {
        bool previous=_settings.ShowPassThroughLauncher;
        _settings.ShowPassThroughLauncher=!previous;
        try { _settings.Save(); }
        catch { _settings.ShowPassThroughLauncher=previous; throw; }
        RefreshPassThroughLauncher();
    }

    private void RefreshPassThroughLauncher()
    {
        if (_disposed || _toolbar is null) return;
        RefreshMascotVisibility(_host.IsVisible);
        if (!_settings.ShowPassThroughLauncher || _host.IsSystemScreenshotActive)
        { _passThroughLauncher?.Hide(); return; }
        // Keep the passive HWND out of the handoff even while the toolbar is waiting
        // for input acknowledgement or its first frame.
        if (_host.IsEditing)
        {
            _passThroughLauncher?.HideForEditing();
            return;
        }
        // Windows may refuse an in-place downgrade of a reused enhanced-band HWND.
        if (_passThroughLauncher is { } existing &&
            new WindowInteropHelper(existing).Handle is var existingHandle && existingHandle != 0 &&
            OverlayCoordinates.TryGetWindowBand(existingHandle, out uint band) && band != 1)
        {
            existing.Close();
            _passThroughLauncher = null;
        }
        if (_passThroughLauncher is null)
        {
            _passThroughLauncher = new();
            ToolbarWindowBounds.Attach(_passThroughLauncher, () => _host.Monitors);
            _passThroughLauncher.EnterEditingRequested += () => RunUiAction(() => SetEditing(true));
            _passThroughLauncher.ToggleVisibilityRequested += () => RunUiAction(ToggleCanvasVisibility);
            _passThroughLauncher.PositionChanged += origin =>
            {
                _toolbarOriginEstablished = true;
                _settings.ToolbarX = origin.X; _settings.ToolbarY = origin.Y;
            };
        }
        if (_settings.ToolbarX is null || _settings.ToolbarY is null)
        {
            var surface = (FrameworkElement)_toolbar.Content;
            surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var primary = _host.PrimaryMonitor;
            var initial = SessionWindowLayout.ToolbarOrigin(primary, surface.DesiredSize.Width, surface.DesiredSize.Height, null, null);
            _settings.ToolbarX = initial.X; _settings.ToolbarY = initial.Y;
        }
        var origin = new OverlayPoint(_settings.ToolbarX.Value, _settings.ToolbarY.Value);
        _passThroughLauncher.SetEntryHint(_systemInputEntryHint);
        _passThroughLauncher.ShowAt(origin, _host.IsVisible);
        if (SessionWindowLayout.TryOrigin(_passThroughLauncher, out var actual))
        {
            if (Math.Abs(actual.X - origin.X) <= 1 && Math.Abs(actual.Y - origin.Y) <= 1)
                _toolbarOriginEstablished = true;
            _settings.ToolbarX = actual.X; _settings.ToolbarY = actual.Y;
        }
    }
}
