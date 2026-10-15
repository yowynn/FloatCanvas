using L = FloatCanvas.Localization.AppText;
using System.IO;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal static class ApplicationPaths
{
    public static string ProfileDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatCanvas");
    public static OverlayMonitorScope? RequestedMonitorScope { get; private set; }
    internal static OverlayMonitorScope? ParseMonitorScope(string[] args)
    {
        bool primary = args.Contains("--primary-screen"), all = args.Contains("--all-screens");
        if (primary && all) throw new ArgumentException(L.Get("App.ConflictingScopes"));
        return primary ? OverlayMonitorScope.PrimaryScreen : all ? OverlayMonitorScope.AllScreens : null;
    }
    public static void Configure(string[] args)
    {
        RequestedMonitorScope = ParseMonitorScope(args);
        int index = Array.IndexOf(args, "--profile");
        if (index >= 0)
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1])) throw new ArgumentException(L.Get("App.ProfileRequired"));
            ProfileDirectory = Path.GetFullPath(args[index + 1]);
        }
        else if (args.Contains("--smoke"))
            ProfileDirectory = Path.Combine(Path.GetTempPath(), "FloatCanvas-Smoke", Guid.NewGuid().ToString("N"));
    }
}
