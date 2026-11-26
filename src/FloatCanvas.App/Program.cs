using L = FloatCanvas.Localization.AppText;
using System.IO;
using System.Windows;

namespace FloatCanvas.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--wait-for-exit")
        {
            if (!int.TryParse(args[1], out int processId) || processId == Environment.ProcessId) return;
            try { using var parent = System.Diagnostics.Process.GetProcessById(processId); if (!parent.WaitForExit(30000)) return; }
            catch (ArgumentException) { }
            args = args[2..];
        }
        if (args is ["--unregister-integration"] or ["--register-integration"])
        {
            try
            {
                if (args[0] == "--unregister-integration") WindowsShellIntegration.Current().UnregisterOwnedIntegration();
                else WindowsShellIntegration.Current().RegisterProjectType();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { AppDialog.Show(null, ex.Message, L.Get("App.OperationFailed"), MessageBoxButton.OK, MessageBoxImage.Error); Environment.ExitCode = 1; }
            return;
        }
        if (args is ["--probe-system-overlay"])
        {
            Environment.ExitCode = OverlayBackendProtection.RunProbe();
            return;
        }
        if (Array.IndexOf(args, "--apply-update") >= 0)
        {
            Environment.ExitCode = args.Length == 2 && args[0] == "--apply-update" ? AppUpdateInstaller.Run(args[1]) : 2;
            return;
        }
        string? projectPath;
        try { ApplicationPaths.Configure(args); projectPath = ProjectLaunchRequest.Parse(args); }
        catch (ArgumentException ex)
        {
            AppDialog.Show(null, ex.Message, L.Get("App.StartupFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.ExitCode = 2;
            return;
        }
        var profileId = ApplicationPaths.ProfileIdentity(ApplicationPaths.ProfileDirectory);
        using var instance = new Mutex(true, "Local\\FloatCanvas.App." + profileId, out var created);
        if (!created)
        {
            if (projectPath is not null && !ProjectActivation.ForwardAsync(profileId, projectPath).GetAwaiter().GetResult())
            {
                AppDialog.Show(null, L.Get("Shell.ForwardFailed"), L.Get("App.OperationFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.ExitCode = 1;
            }
            return;
        }
        var diagnostics = new ApplicationDiagnostics(ApplicationPaths.ProfileDirectory);
        void LogStartupStage(string stage, object? data = null) =>
            diagnostics.Write("App.Startup.Stage", new { Stage = stage, UptimeMilliseconds = Environment.TickCount64, Data = data });
        LogStartupStage("DiagnosticsCreated");
        void LogEmojiBoundsProfile()
        {
            var mismatches = FloatCanvas.Rendering.EmojiBoundsProfile.FingerprintMismatches();
            if (mismatches.Count > 0) diagnostics.Write("Emoji.BoundsProfileMismatch", mismatches);
        }
        LogEmojiBoundsProfile();
        FloatCanvas.Rendering.EmojiFontSources.Changed += LogEmojiBoundsProfile;
        diagnostics.Write("App.Startup", new { Executable = Environment.ProcessPath,
            WindowsStartup = args.Contains("--startup"), SystemUptimeMilliseconds = Environment.TickCount64,
            AssemblyId = typeof(Program).Assembly.ManifestModule.ModuleVersionId,
            WindowsAssemblyId = typeof(FloatCanvas.Windows.OverlayHost).Assembly.ManifestModule.ModuleVersionId,
            RenderingAssemblyId = typeof(FloatCanvas.Rendering.SceneRenderer).Assembly.ManifestModule.ModuleVersionId,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription });
        LogStartupStage("ApplicationCreating");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        { if (e.ExceptionObject is Exception exception) diagnostics.WriteUnhandled(exception); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { EmojiHistoryStore.Shared.Flush(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
            diagnostics.Write("App.ProcessExit", new { Environment.ExitCode });
        };
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LogStartupStage("ApplicationCreated");
        app.Dispatcher.ShutdownStarted += (_, _) => diagnostics.Write("App.Dispatcher.ShutdownStarted");
        app.Dispatcher.ShutdownFinished += (_, _) => diagnostics.Write("App.Dispatcher.ShutdownFinished");
        CompatibleConfiguration.ConfirmRepair=(path,fields)=>ConfigurationStartup.ConfirmWrite(path,fields,diagnostics);
        DesktopSession? session = null;
        OverlayBackendProtection? protection = null;
        bool switchingOverlay = false;
        var pendingProjects = new Queue<string>();
        app.DispatcherUnhandledException += (_, e) =>
        {
            diagnostics.WriteUnhandled(e.Exception);
            if (session?.TryRequestDesktopFallback(e.Exception) == true) { e.Handled = true; return; }
            if (session?.UsesEnhancedOverlay == true) protection?.Disable(e.Exception.Message);
            if (args.Contains("--smoke")) app.Shutdown(1);
            else AppDialog.Show(null, e.Exception.Message, L.Get("App.OperationFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        async void SwitchOverlay(OverlayBackendMode target, string? failure)
        {
            if (switchingOverlay || session is not { } previous || app.Dispatcher.HasShutdownStarted) return;
            switchingOverlay = true;
            try
            {
                var replacement = await previous.SwitchOverlayAsync(target, failure, () => session = null);
                if (ReferenceEquals(replacement, previous)) return;
                session = replacement;
                protection = replacement.BackendProtection;
                replacement.OverlaySwitchRequested += SwitchOverlay;
                while (pendingProjects.TryDequeue(out var pending)) replacement.QueueProjectOpen(pending);
                replacement.Start(args);
            }
            catch (OperationCanceledException) when (session is not null || app.Dispatcher.HasShutdownStarted) { }
            catch (Exception error) when (OverlayBackendProtection.Recoverable(error))
            {
                if (failure is not null) protection?.Disable(error.Message);
                diagnostics.Write(failure is null ? "Input.OverlayModeSwitchFailed" : "Input.DesktopFallbackFailed", new { Target = target.ToString() }, exception: error);
                if (!app.Dispatcher.HasShutdownStarted)
                    AppDialog.Show(null, L.Get(failure is null ? "OverlaySafety.SwitchFailed" : "OverlaySafety.RecoverFailed") + "\n\n" + error.Message,
                        L.Get("App.OperationFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
                if (session is null) app.Shutdown(1);
            }
            finally { switchingOverlay = false; }
        }
        using var activation = new ProjectActivation(profileId, async path =>
            await app.Dispatcher.InvokeAsync(() =>
            {
                if (app.Dispatcher.HasShutdownStarted) return false;
                if (session is not null) return session.QueueProjectOpen(path);
                pendingProjects.Enqueue(path);
                return true;
            }));
        app.Startup += (_, _) =>
        {
            try
            {
                LogStartupStage("StartupHandlerBegin");
                if(!ConfigurationStartup.Prepare(diagnostics,interactive:!args.Contains("--smoke"))){app.Shutdown(1);return;}
                LogStartupStage("ConfigurationPrepared");
                AppTheme.InitializeStartup();
                LogStartupStage("ThemeInitialized");
                protection = new OverlayBackendProtection(ApplicationPaths.ProfileDirectory,
                    OverlayBackendModes.Resolve(UserSettings.Load().OverlayMode, args));
                LogStartupStage("ProtectionCreated", new { Mode = protection.Mode.ToString() });
                session = new DesktopSession(app, null, protection);
                LogStartupStage("SessionConstructed");
                session.OverlaySwitchRequested += SwitchOverlay;
                while (pendingProjects.TryDequeue(out var pending)) session.QueueProjectOpen(pending);
                LogStartupStage("SessionStartEntering");
                session.Start(args);
            }
            catch (Exception ex)
            {
                diagnostics.Write("App.StartupFailed", exception: ex);
                if (!args.Contains("--smoke"))
                    AppDialog.Show(null, protection?.Mode == OverlayBackendMode.Enhanced
                        ? L.Get("OverlaySafety.ForcedFailure") + "\n\n" + ex.Message
                        : L.Format("Configuration.StartupFailed",ApplicationPaths.ProfileDirectory), L.Get("App.StartupFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
                else Console.Error.WriteLine(ex);
                app.Shutdown(1);
            }
        };
        app.Exit += (_, e) =>
        {
            diagnostics.Write("App.Exit.Begin", new { e.ApplicationExitCode });
            try
            {
                session?.Dispose();
                diagnostics.Write("App.Exit.ProtectionComplete.Begin");
                protection?.Complete();
                diagnostics.Write("App.Exit.Complete");
            }
            catch (Exception error) { diagnostics.Write("App.Exit.Failed", exception: error); throw; }
        };
        app.Run();
        diagnostics.Write("App.Run.Returned", new { Environment.ExitCode });
    }
}
