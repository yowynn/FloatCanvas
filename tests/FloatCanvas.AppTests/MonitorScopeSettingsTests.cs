using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Windows;
using System.Windows.Controls;

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
        var specific = new UserSettings { MonitorDeviceName = "secondary" };
        check("Specific monitor preference round trips independently of legacy primary flag",
            MonitorScopeControl.Saved(JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(specific))!) == new MonitorScopeChoice(OverlayMonitorScope.SpecificScreen, "secondary"));
        MonitorInfo main = new("main", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96, 96, true);
        MonitorInfo secondary = new("secondary", new(-1280, 0, 1280, 720), new(-1280, 0, 1280, 680), 144, 144, false);
        var combo = new ComboBox();
        MonitorScopeControl.Refresh(combo, [main, secondary], MonitorScopeControl.Saved(specific));
        check("Scope options put all screens first and primary second before a separator", combo.Items.Count == 5 &&
            ((ComboBoxItem)combo.Items[0]).Tag is MonitorScopeChoice { Scope: OverlayMonitorScope.AllScreens } &&
            ((ComboBoxItem)combo.Items[1]).Tag is MonitorScopeChoice { Scope: OverlayMonitorScope.PrimaryScreen } && combo.Items[2] is Separator);
        check("Numbered display selection retains the device identity", MonitorScopeControl.Selected(combo) == new MonitorScopeChoice(OverlayMonitorScope.SpecificScreen, "secondary"));
        MonitorScopeControl.Refresh(combo, [secondary, main], MonitorScopeControl.Selected(combo));
        check("Topology reordering preserves selected device rather than an ordinal index", combo.SelectedIndex == 3 && MonitorScopeControl.Selected(combo).DeviceName == "secondary");
        MonitorScopeControl.Refresh(combo, [main], MonitorScopeControl.Selected(combo));
        check("Single display offers only all and primary with a valid fallback", combo.Items.Count == 2 && combo.SelectedIndex == 1 && MonitorScopeControl.Selected(combo).Scope == OverlayMonitorScope.PrimaryScreen);
        var pendingCombo = new ComboBox();
        var pending = new MonitorScopeSelection(pendingCombo, MonitorScopeControl.Saved(specific));
        pending.Refresh([main, secondary]);
        pending.Refresh([main]);
        check("Disconnect displays fallback without silently replacing the saved specific preference", pendingCombo.SelectedIndex == 1 && pending.Pending == new MonitorScopeChoice(OverlayMonitorScope.SpecificScreen, "secondary"));
        pending.Refresh([secondary, main]);
        check("Reconnect restores the chosen device in the open scope control", pendingCombo.SelectedIndex == 3 && MonitorScopeControl.Selected(pendingCombo) == pending.Pending);
        pendingCombo.SelectedIndex = 4;
        check("An explicit unsaved display edit updates the pending preference", pending.Pending == new MonitorScopeChoice(OverlayMonitorScope.SpecificScreen, "main"));
        pending.Refresh([secondary]);
        pending.Refresh([main, secondary]);
        check("A topology change retains an unsaved device edit rather than the previous saved preference", pendingCombo.SelectedIndex == 3 && pending.Pending.DeviceName == "main");
        pendingCombo.SelectedIndex = 0;
        pending.Refresh([main]); pending.Refresh([main, secondary]);
        check("An explicit all-screen edit is also retained through disconnect and reconnect", pendingCombo.SelectedIndex == 0 && pending.Pending.Scope == OverlayMonitorScope.AllScreens && pending.Pending.DeviceName is null);
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
