using L = FloatCanvas.Localization.AppText;
using System.IO;
using System.Windows;

namespace FloatCanvas.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--apply-update") >= 0)
        {
            Environment.ExitCode = args.Length == 2 && args[0] == "--apply-update" ? AppUpdateInstaller.Run(args[1]) : 2;
            return;
        }
        ApplicationPaths.Configure(args);
        var profileId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ApplicationPaths.ProfileDirectory.ToUpperInvariant())))[..16];
        using var instance = new Mutex(true, "Local\\FloatCanvas.App." + profileId, out var created);
        if (!created) return;
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
        app.DispatcherUnhandledException += (_, e) =>
        {
            diagnostics.WriteUnhandled(e.Exception);
            if (args.Contains("--smoke")) app.Shutdown(1);
            else AppDialog.Show(null, e.Exception.Message, L.Get("App.OperationFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        DesktopSession? session = null;
        app.Startup += (_, _) =>
        {
            try
            {
                session = new DesktopSession(app);
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
        app.Exit += (_, _) => session?.Dispose();
        app.Run();
    }
}
