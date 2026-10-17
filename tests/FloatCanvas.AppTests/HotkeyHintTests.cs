using FloatCanvas.App;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;
using Controls = System.Windows.Controls;
using Peers = System.Windows.Automation.Peers;

/// <summary>Actual hint controls and automation peers, detached from any HWND or input stream.</summary>
internal static class HotkeyHintTests
{
    internal static void Run(Action<string, bool> check)
    {
        string preference = L.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" }) Verify(language, check);
        }
        finally { L.Initialize(preference); }
    }

    private static void Verify(string language, Action<string, bool> check)
    {
        L.Initialize(language);
        var icon = new System.Windows.Shapes.Path();
        var button = new Controls.Button { Content = icon };
        var welcome = new Controls.TextBlock();
        var peer = new Peers.ButtonAutomationPeer(button);
        var toggle = new HotkeyBinding(6, 0x47); // Ctrl+Shift+G
        var hide = new HotkeyBinding(6, 0x48); // Ctrl+Shift+H
        string label = language == "zh-CN" ? "穿透 · Ctrl+Shift+G" : "Pass through · Ctrl+Shift+G";
        void Check(string name, bool condition) => check(language + " hotkey hints " + name, condition);

        DesktopSession.RefreshHotkeyHints(button, welcome, toggle, hide);
        Check("show the saved custom toggle on first creation", Equals(button.ToolTip, label));
        Check("expose the same custom toggle to automation", peer.GetName() == label);
        Check("keep the toolbar icon as content", ReferenceEquals(button.Content, icon));
        Check("show both active custom bindings in an existing welcome", welcome.Text.Contains("Ctrl+Shift+G") &&
            welcome.Text.Contains("Ctrl+Shift+H") && !welcome.Text.Contains("Ctrl+Alt+F"));

        // Refresh the same real controls after a successful settings change, rather than
        // recreate them and accidentally hide a stale tooltip/automation-name defect.
        DesktopSession.RefreshHotkeyHints(button, welcome, HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide);
        string defaultLabel = language == "zh-CN" ? "穿透 · Ctrl+`" : "Pass through · Ctrl+`";
        Check("replace the existing tooltip after bindings change", Equals(button.ToolTip, defaultLabel));
        Check("replace the existing automation name after bindings change", peer.GetName() == defaultLabel);
        Check("replace both welcome bindings without stale custom text", welcome.Text.Contains("Ctrl+`") &&
            welcome.Text.Contains("Ctrl+Shift+`") && !welcome.Text.Contains("Ctrl+Shift+G") && !welcome.Text.Contains("Ctrl+Shift+H"));

        DesktopSession.RefreshHotkeyHints(button, null, toggle, hide);
        Check("refresh after the welcome is closed", Equals(button.ToolTip, label) && peer.GetName() == label);
        Check("do not change the initialized interface language", L.Preference == language && L.CurrentLanguage == language);
    }
}
