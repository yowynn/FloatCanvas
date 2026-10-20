using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private readonly Queue<string> _projectOpenRequests = new();
    private bool _shellReady, _openingShellProject, _startupPending, _quietStartup;
    private bool _shellIntegrationAllowed;

    internal bool QueueProjectOpen(string path)
    {
        if (_disposed || _documentExitRequested) return false;
        _projectOpenRequests.Enqueue(ProjectLaunchRequest.NormalizePath(path));
        ScheduleProjectOpenDrain(DrainProjectOpenRequestsAsync);
        return true;
    }

    // An async drain can show a modal dialog before its first await. Defer that
    // entire prefix so the activation callback can return its acknowledgement.
    internal static void ScheduleProjectOpenDrain(Func<Task> drain) =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background, new Action(() => _ = drain()));

    private async Task DrainProjectOpenRequestsAsync()
    {
        if (!_shellReady || _openingShellProject || _disposed) return;
        _openingShellProject = true;
        try
        {
            while (_projectOpenRequests.Count != 0 && !_disposed && !_documentExitRequested)
            {
                if (DocumentOperationBusy || System.Windows.Interop.ComponentDispatcher.IsThreadModal)
                {
                    await Task.Delay(100, _documentLifetime.Token);
                    continue;
                }
                if (!_welcomeManual) _welcomeWindow?.Hide();
                await OpenProjectPathAsync(_projectOpenRequests.Dequeue());
            }
        }
        catch (OperationCanceledException) when (_documentLifetime.IsCancellationRequested) { }
        finally { _openingShellProject = false; }
    }

    private void RegisterShellIntegration()
    {
        if (!_shellIntegrationAllowed) return;
        try
        {
            var shell = WindowsShellIntegration.Current();
            shell.RegisterProjectType();
            if (shell.StartupEnabled) shell.SetStartup(true); // Refresh a portable executable's moved location.
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _diagnostics.Write("Shell.RegistrationFailed", exception: ex);
            UpdateStatus(L.Get("Shell.RegistrationFailed") + ex.Message);
        }
    }

    private async Task CompleteStartupAsync()
    {
        if (!_startupPending || _disposed) return;
        _startupPending = false;
        RegisterShellIntegration();
        _shellReady = true;
        if (_projectOpenRequests.Count != 0) await DrainProjectOpenRequestsAsync();
        else if (!_quietStartup) ShowWelcome();
        AppUpdateInstaller.AcknowledgeStartup();
        NotifyUpdateResult();
    }
}
