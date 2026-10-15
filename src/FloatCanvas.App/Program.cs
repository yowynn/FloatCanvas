using L = FloatCanvas.Localization.AppText;
using System.IO;
using System.Windows;

namespace FloatCanvas.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationPaths.Configure(args);
        var profileId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ApplicationPaths.ProfileDirectory.ToUpperInvariant())))[..16];
        using var instance = new Mutex(true, "Local\\FloatCanvas.App." + profileId, out var created);
        if (!created) return;
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            var directory = ApplicationPaths.ProfileDirectory;
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "errors.log"), $"{DateTimeOffset.Now:O} {e.Exception}\n");
            if (args.Contains("--smoke")) app.Shutdown(1);
            else System.Windows.MessageBox.Show(e.Exception.Message, L.Get("App.OperationFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                if (!args.Contains("--smoke"))
                    System.Windows.MessageBox.Show(ex.ToString(), L.Get("App.StartupFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
                else Console.Error.WriteLine(ex);
                app.Shutdown(1);
            }
        };
        app.Exit += (_, _) => session?.Dispose();
        app.Run();
    }
}
