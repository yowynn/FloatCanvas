using System.Windows.Controls;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal readonly record struct MonitorScopeChoice(OverlayMonitorScope Scope, string? DeviceName = null);

/// <summary>Retains the user's pending preference when a temporary display fallback changes the visible selection.</summary>
internal sealed class MonitorScopeSelection
{
    private readonly ComboBox combo;
    private bool refreshing;
    internal MonitorScopeChoice Pending { get; private set; }

    internal MonitorScopeSelection(ComboBox combo, MonitorScopeChoice initial)
    {
        this.combo = combo; Pending = initial;
        combo.SelectionChanged += (_, _) => { if (!refreshing) Pending = MonitorScopeControl.Selected(combo); };
    }

    internal void Refresh(IReadOnlyList<MonitorInfo> monitors)
    {
        refreshing = true;
        try { MonitorScopeControl.Refresh(combo, monitors, Pending); }
        finally { refreshing = false; }
    }
}

internal static class MonitorScopeControl
{
    internal static MonitorScopeChoice Saved(UserSettings settings) => !string.IsNullOrWhiteSpace(settings.MonitorDeviceName)
        ? new(OverlayMonitorScope.SpecificScreen, settings.MonitorDeviceName)
        : new(settings.PrimaryScreenOnly ? OverlayMonitorScope.PrimaryScreen : OverlayMonitorScope.AllScreens);

    internal static MonitorScopeChoice Selected(ComboBox combo) => combo.SelectedItem is ComboBoxItem { Tag: MonitorScopeChoice choice }
        ? choice : new(OverlayMonitorScope.AllScreens);

    internal static void Refresh(ComboBox combo, IReadOnlyList<MonitorInfo> monitors, MonitorScopeChoice selected)
    {
        combo.Items.Clear();
        void Add(string label, MonitorScopeChoice choice)
        {
            var item = new ComboBoxItem { Content = label, Tag = choice };
            combo.Items.Add(item);
            if (choice.Scope == selected.Scope && string.Equals(choice.DeviceName, selected.DeviceName, StringComparison.OrdinalIgnoreCase))
                combo.SelectedItem = item;
        }
        Add(L.Get("Settings.AllScreens"), new(OverlayMonitorScope.AllScreens));
        Add(L.Get("Settings.PrimaryOnly"), new(OverlayMonitorScope.PrimaryScreen));
        if (monitors.Count > 1)
        {
            combo.Items.Add(new Separator());
            for (int i = 0; i < monitors.Count; i++)
                Add(L.Format("Settings.NumberedScreen", i + 1), new(OverlayMonitorScope.SpecificScreen, monitors[i].DeviceName));
        }
        if (combo.SelectedIndex < 0) combo.SelectedIndex = selected.Scope == OverlayMonitorScope.AllScreens ? 0 : 1;
    }
}
