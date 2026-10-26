using System.Windows;
using System.Windows.Interop;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private static void ShowAppWindow(Window window, bool activate = false)
        => SessionWindowCoordinator.Show(window, activate);

    private void ShowWelcomeWindow(Window window, bool activate)
    {
        nint foreground = SessionWindowCoordinator.CurrentForeground;
        ShowAppWindow(window);
        if (!activate) return;
        // Menu mode and the popup's queued focus return must finish before a
        // normal window requests the real foreground from an enhanced overlay.
        _app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!_disposed && !_host.IsEditing && window.IsVisible && ReferenceEquals(_welcomeWindow, window))
            {
                bool activated = SessionWindowCoordinator.ActivateRegularWindowForUserAction(window, foreground);
                _diagnostics.Write("UI.WelcomeForeground", new { Activated = activated, ExpectedForeground = foreground.ToInt64(), Foreground = SessionWindowCoordinator.CurrentForeground.ToInt64() });
            }
        }));
    }

    private readonly HashSet<Window> _regularWindows = [];
    private readonly HashSet<Window> _editingDialogs = [];

    private void CloseRegularWindows()
    {
        foreach (var window in _regularWindows.ToArray()) window.Close();
    }

    private void CloseEditingDialogs()
    {
        foreach (var window in _editingDialogs.ToArray()) window.Close();
    }

    private void ConfigureAppWindow(Window window, bool dialog = false, bool regular = false)
    {
        window.Icon = AppIcon.WindowImage;
        window.Topmost = !regular;
        window.ShowInTaskbar = regular;
        if (regular)
        {
            OverlayWindowScope.UseRegularWindow(window);
            _regularWindows.Add(window);
            window.Closed += (_, _) => _regularWindows.Remove(window);
            _windowLayout.Track(window);
            if (dialog) AppWindowChrome.ApplyDialog(window); else AppWindowChrome.Apply(window);
            return;
        }
        if (!ReferenceEquals(window, _toolbar))
        {
            // Welcome and tray-opened dialogs may precede the first editing session.
            // WPF requires the owner to have a native source even while it remains hidden.
            new WindowInteropHelper(_toolbar).EnsureHandle();
            window.Owner ??= _toolbar;
        }
        if (ReferenceEquals(window, _welcomeWindow)) _windowLayout.BindRoot(window);
        else _windowLayout.Track(window);
        _windowCoordinator?.Register(window);
        if (dialog) AppWindowChrome.ApplyDialog(window); else AppWindowChrome.Apply(window);
        void ScheduleRaise()
        {
            long version = _host.EditingSessionVersion;
            // Re-entering SetWindowPos during the first IsVisibleChanged notification
            // can replace WPF's initial width with its provisional one-pixel HWND size.
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (window.IsVisible && !_disposed && _host.EditingSessionVersion == version) RaiseVisibleToolWindows();
            });
        }
        window.SourceInitialized += (_, _) => ScheduleRaise();
        window.IsVisibleChanged += (_, _) => ScheduleRaise();
    }

    private void ShowSessionDialog(Window window)
    {
        bool editing = _host.IsEditing;
        ConfigureAppWindow(window, true, regular: !editing);
        if (!editing) { ShowAppWindow(window, true); return; }
        _editingDialogs.Add(window);
        _host.SuspendApplicationHotkeys(true);
        try { SessionWindowCoordinator.ShowModal(window); }
        finally
        {
            _editingDialogs.Remove(window);
            try { _host.SuspendApplicationHotkeys(false); }
            catch (Exception error) { UpdateStatus(error.Message); }
        }
    }

    private void ShowHelp(bool shortcuts)
    {
        var window = AppHelp.CreateTextWindow(FloatCanvas.Localization.AppText.Get(shortcuts ? "Settings.Shortcuts" : "Audit.UserGuide"), AppHelp.ReadGuide(shortcuts), OpenExternalLink, regular: !_host.IsEditing);
        ShowSessionDialog(window);
    }

    private void ShowReportIssue()
        => ShowSessionDialog(new ReportIssueWindow(UsesEnhancedOverlay, () => OpenExternalLink("https://github.com/yowynn/FloatCanvas/issues/new"), regular: !_host.IsEditing));

    private void OpenExternalLink(string url)
    {
        SetEditing(false);
        _app.Dispatcher.BeginInvoke(new Action(() => ExternalLink.Open(url)));
    }

    private async Task RestartApplicationAsync()
    {
        _documentExitRequested = true;
        try
        {
            FlushToolState(commitDraft: true);
            await FlushRecoveryForUpdateAsync();
            var start = new System.Diagnostics.ProcessStartInfo(ApplicationPaths.LauncherPath) { UseShellExecute = false };
            start.ArgumentList.Add("--wait-for-exit"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--profile"); start.ArgumentList.Add(ApplicationPaths.ProfileDirectory); start.ArgumentList.Add("--recover");
            if (ApplicationPaths.RequestedMonitorScope is { } scope)
                start.ArgumentList.Add(scope == FloatCanvas.Windows.OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens");
            System.Diagnostics.Process.Start(start);
            _app.Shutdown();
        }
        catch { _documentExitRequested = false; throw; }
    }
}
