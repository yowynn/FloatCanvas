using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private AppUpdateWindow? _updatesWindow;

    private void ShowUpdates()
    {
        if (_updatesWindow is { IsVisible: true }) { ShowAppWindow(_updatesWindow, true); return; }
        if (DocumentOperationBusy) return;
        if (_host.IsEditing) SetEditing(false);
        var client = new GitHubUpdateClient();
        bool allUsers = InstallerRegistration.Scope(AppContext.BaseDirectory) == InstallationScope.AllUsers;
        string version = typeof(DesktopSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        var window = new AppUpdateWindow(version, client, ApplyUpdateAsync, allUsers || AppUpdateInstaller.CanUpdateDirectory(AppContext.BaseDirectory),
            includePrereleases: _settings.IncludePrereleaseUpdates, savePreviewPreference: enabled =>
            {
                bool previous = _settings.IncludePrereleaseUpdates;
                _settings.IncludePrereleaseUpdates = enabled;
                try { _settings.Save(); }
                catch { _settings.IncludePrereleaseUpdates = previous; throw; }
            }, prepareAndApplyUpdate: ApplyUpdateAsync, applyInstallerUpdate: allUsers ? ApplyInstallerUpdateAsync : null);
        _updatesWindow = window;
        ConfigureAppWindow(window, true, regular: true);
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Closed += (_, _) => { client.Dispose(); if (ReferenceEquals(_updatesWindow, window)) _updatesWindow = null; };
        ShowAppWindow(window, true);
    }

    private async Task ApplyInstallerUpdateAsync(PreparedInstallerUpdate update, System.Threading.CancellationToken cancellationToken, Action beginCommit)
    {
        if (DocumentOperationBusy) throw new InvalidOperationException(L.Get("Desktop.Busy"));
        CloseImageDrop(); _editor.Cancel(); _documentExitRequested = true;
        try
        {
            try { FlushToolState(commitDraft: true); await FlushRecoveryForUpdateAsync(); }
            catch (Exception ex) { throw new IOException(L.Get("Updates.ExitSaveFailed"), ex); }
            cancellationToken.ThrowIfCancellationRequested(); beginCommit();
            string monitor = ApplicationPaths.RequestedMonitorScope switch
            {
                FloatCanvas.Windows.OverlayMonitorScope.PrimaryScreen => "primary",
                FloatCanvas.Windows.OverlayMonitorScope.AllScreens => "all",
                _ => "auto"
            };
            await InstallerUpdateLauncher.LaunchAsync(update, AppContext.BaseDirectory, ApplicationPaths.ProfileDirectory, monitor, cancellationToken);
            _app.Shutdown();
        }
        catch { _documentExitRequested = false; throw; }
    }

    private async Task ApplyUpdateAsync(PreparedUpdate update)
        => await ApplyUpdateAsync(update, System.Threading.CancellationToken.None, () => { });

    private async Task ApplyUpdateAsync(PreparedUpdate update, System.Threading.CancellationToken cancellationToken, Action beginCommit)
    {
        if (DocumentOperationBusy) throw new InvalidOperationException(L.Get("Desktop.Busy"));
        CloseImageDrop(); _editor.Cancel(); _documentExitRequested = true;
        try
        {
            try { FlushToolState(commitDraft: true); await FlushRecoveryForUpdateAsync(); }
            catch (Exception ex) { throw new IOException(L.Get("Updates.ExitSaveFailed"), ex); }
            var arguments = new List<string> { "--profile", ApplicationPaths.ProfileDirectory, "--recover" };
            if (ApplicationPaths.RequestedMonitorScope is { } scope)
                arguments.Add(scope == FloatCanvas.Windows.OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens");
            string plan = await Task.Run(() => AppUpdateInstaller.Prepare(update, AppContext.BaseDirectory, ApplicationPaths.ProfileDirectory, arguments.ToArray(), cancellationToken), cancellationToken);
            try { cancellationToken.ThrowIfCancellationRequested(); beginCommit(); }
            catch
            {
                try { await Task.Run(() => AppUpdateInstaller.DiscardPreparation(plan)); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or InvalidOperationException) { }
                throw;
            }
            await Task.Run(() => AppUpdateInstaller.Launch(plan));
            _app.Shutdown();
        }
        catch { _documentExitRequested = false; throw; }
    }

    // An update restarts with --recover, so even an untouched blank launch needs a
    // new snapshot. Ordinary exit intentionally leaves older manual history intact.
    private async Task FlushRecoveryForUpdateAsync()
    {
        _documentRecoveryTimer?.Stop();
        await _documentRecoveryTask;
        _documentLifetime.Token.ThrowIfCancellationRequested();
        if (_documentStopped) throw new InvalidOperationException(L.Get("Desktop.Busy"));
        if (_documentRecoveryWritesBlocked) await PreserveRecoveryGenerationsAsync();
        PruneDocumentResources();
        _documentRecoveryTask = WriteUpdateRecoveryAsync();
        try { await _documentRecoveryTask; }
        finally { if (_documentRecoveryTask.IsCompleted) _documentRecoveryTask = Task.CompletedTask; }
    }

    private async Task WriteUpdateRecoveryAsync()
    {
        await RecoveryHistory.PreserveBeforeFirstWriteAsync(_documentLifetime.Token);
        var snapshot = SnapshotWorkspace();
        await SaveRecoverySnapshotAsync(snapshot);
        _documentRecoveryFingerprint = WorkspaceFingerprint(snapshot);
        RecoveryHistory.WorkspaceSaved();
    }

    private void NotifyUpdateResult()
    {
        _ = Task.Run(() =>
        {
            try { AppUpdateInstaller.CleanupCompletedJobs(ApplicationPaths.ProfileDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        });
        try
        {
            if (AppUpdateInstaller.ConsumeResult(ApplicationPaths.ProfileDirectory) is { Success: false } result)
                AppDialog.Show(_welcomeWindow, L.Get("Updates.InstallFailed") + "\n" + result.Error,
                    L.Get("Updates.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (IOException ex) { UpdateStatus(ex.Message); }
    }
}
