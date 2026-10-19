using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Windows;

internal static class MonitorScopeSettingsTests
{
    public static void Run(Action<string, bool> check)
    {
        check("Fresh profiles default to all-screen drawing", !new UserSettings().PrimaryScreenOnly);
        check("Older settings without screen scope default to all screens", !JsonSerializer.Deserialize<UserSettings>("{}")!.PrimaryScreenOnly);
        var allScreens = JsonSerializer.Deserialize<UserSettings>("{\"PrimaryScreenOnly\":false}")!;
        check("Explicit all-screen preference survives reading", !allScreens.PrimaryScreenOnly);
        check("All-screen preference round trips", !JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(allScreens))!.PrimaryScreenOnly);
        var primaryScreen = JsonSerializer.Deserialize<UserSettings>("{\"PrimaryScreenOnly\":true}")!;
        check("Explicit primary-screen preference survives reading", primaryScreen.PrimaryScreenOnly);
        check("Primary-screen preference round trips", JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(primaryScreen))!.PrimaryScreenOnly);
        check("No command-line override leaves saved preference in charge", ApplicationPaths.ParseMonitorScope([]) is null);
        check("Primary override is explicit", ApplicationPaths.ParseMonitorScope(["--primary-screen"]) == OverlayMonitorScope.PrimaryScreen);
        check("All-screen override is explicit", ApplicationPaths.ParseMonitorScope(["--all-screens"]) == OverlayMonitorScope.AllScreens);
        check("Profile arguments do not change scope", ApplicationPaths.ParseMonitorScope(["--profile", "own-test-profile"]) is null);
        bool conflict = false;
        try { ApplicationPaths.ParseMonitorScope(["--primary-screen", "--all-screens"]); }
        catch (ArgumentException) { conflict = true; }
        check("Conflicting screen overrides fail explicitly", conflict);
    }
}
