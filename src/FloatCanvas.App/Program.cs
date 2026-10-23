using L = FloatCanvas.Localization.AppText;
using System.IO;
using System.Windows;

namespace FloatCanvas.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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
        var profileId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ApplicationPaths.ProfileDirectory.ToUpperInvariant())))[..16];
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
        diagnostics.Write("App.Startup", new { Executable = Environment.ProcessPath,
            AssemblyId = typeof(Program).Assembly.ManifestModule.ModuleVersionId,
            WindowsAssemblyId = typeof(FloatCanvas.Windows.OverlayHost).Assembly.ManifestModule.ModuleVersionId,
            RenderingAssemblyId = typeof(FloatCanvas.Rendering.SceneRenderer).Assembly.ManifestModule.ModuleVersionId,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription });
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        { if (e.ExceptionObject is Exception exception) diagnostics.WriteUnhandled(exception); };
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        DesktopSession? session = null;
        OverlayBackendProtection? protection = null;
        bool recovering = false;
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
        async void RecoverDesktop(string reason)
        {
            if (recovering || session is null || app.Dispatcher.HasShutdownStarted) return;
            recovering = true;
            try
            {
                var previous = session;
                var state = await previous.PrepareDesktopFallbackAsync();
                session = null;
                previous.Dispose();
                var replacement = new DesktopSession(app, null, protection, state);
                session = replacement;
                replacement.DesktopFallbackRequested += RecoverDesktop;
                while (pendingProjects.TryDequeue(out var pending)) replacement.QueueProjectOpen(pending);
                replacement.Start(args);
            }
            catch (OperationCanceledException) when (app.Dispatcher.HasShutdownStarted) { }
            catch (Exception error) when (OverlayBackendProtection.Recoverable(error))
            {
                protection?.Disable(error.Message);
                diagnostics.Write("Input.DesktopFallbackFailed", exception: error);
                if (!app.Dispatcher.HasShutdownStarted)
                    AppDialog.Show(null, L.Get("OverlaySafety.RecoverFailed") + "\n\n" + error.Message,
                        L.Get("App.OperationFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
                if (session is null) app.Shutdown(1);
            }
            finally { recovering = false; }
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
                AppTheme.InitializeStartup();
                protection = new OverlayBackendProtection(ApplicationPaths.ProfileDirectory,
                    args.Contains("--desktop-mode"), args.Contains("--retry-fullscreen"));
                session = new DesktopSession(app, null, protection);
                session.DesktopFallbackRequested += RecoverDesktop;
                while (pendingProjects.TryDequeue(out var pending)) session.QueueProjectOpen(pending);
                session.Start(args);
            }
            catch (Exception ex)
            {
                diagnostics.Write("App.StartupFailed", exception: ex);
                if (!args.Contains("--smoke"))
                    AppDialog.Show(null, ex.ToString(), L.Get("App.StartupFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
                else Console.Error.WriteLine(ex);
                app.Shutdown(1);
            }
        };
        app.Exit += (_, _) =>
        {
            session?.Dispose();
            protection?.Complete();
        };
        app.Run();
    }
}
