using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed record ScopeNavigationEntry(string Key, string Name, string Glyph, Action Navigate,
    Guid? LocalScope = null, bool IsLocal = false, Func<bool>? Exists = null, Func<bool>? CanNavigate = null, Func<string>? ReadName = null, Func<bool>? IsLocalNow = null)
{
    internal string DisplayName => ReadName?.Invoke() ?? Name;
    internal bool Available => (Exists?.Invoke() ?? true) && (CanNavigate?.Invoke() ?? true);
    internal bool Local => IsLocalNow?.Invoke() ?? IsLocal;
}

internal sealed record ScopeNavigationPath(IReadOnlyList<ScopeNavigationEntry> Entries, int CurrentIndex)
{
    internal static ScopeNavigationPath From(EditingScopeDescription description) => description.Navigation ?? new(
        description.Ancestors.Select((a,i) => new ScopeNavigationEntry(i.ToString(), a.Name, i == 0 ? ScopeNavigation.RootGlyph : LayersWindow.Folder, a.Navigate))
            .Append(new("current", description.CurrentName, LayersWindow.Folder, () => { })).ToArray(), description.Ancestors.Count);
}

/// <summary>Retain one explored branch; going up keeps it, diverging replaces its suffix.</summary>
internal sealed class ScopeNavigationHistory
{
    private object? tree;
    private List<ScopeNavigationEntry> branch = [];
    internal ScopeNavigationPath Update(object treeKey, IReadOnlyList<ScopeNavigationEntry> current)
    {
        bool prefix = Equals(tree, treeKey) && branch.Count >= current.Count && current.Select((entry,i) => entry.Key == branch[i].Key).All(equal => equal);
        branch = prefix ? current.Concat(branch.Skip(current.Count).TakeWhile(e => e.Exists?.Invoke() ?? true)).ToList() : [.. current];
        tree = treeKey;
        return new(branch.ToArray(), current.Count - 1);
    }
}

internal static class ScopeNavigation
{
    internal static string ViewKey(ScopeNavigationPath path, string? mode, string state, bool canToggle) =>
        string.Join('\0', new[] { L.Preference, path.CurrentIndex.ToString(), mode ?? "", state, canToggle.ToString() }
            .Concat(path.Entries.Select(e => string.Join('\0', e.Key, e.DisplayName, e.Glyph, e.Available.ToString(), e.Local.ToString()))));
    internal const string RootGlyph = "M2,3 H14 V13 H2 Z M5,6 H11 V10 H5 Z";
    internal static string RootGlyphFor(FloatCanvas.Core.RootCanvasKind root) => root switch
    {
        FloatCanvas.Core.RootCanvasKind.Foreground => "M2,2 H12 V12 H2 Z M5,14 H14 V5",
        FloatCanvas.Core.RootCanvasKind.Background => "M4,4 H14 V14 H4 Z M2,11 V2 H11",
        _ => RootGlyph
    };
    internal static FrameworkElement Icon(string glyph, int depth)
    {
        // Match the standard menu's 18 DIP icon column. The artwork itself stays
        // centered on the label; adding a depth badge must not move its axis.
        var icon = new Grid { Width = 18, Height = 18, IsHitTestVisible = false };
        var artwork = LayersWindow.IconPath(glyph);
        artwork.HorizontalAlignment = HorizontalAlignment.Center; artwork.VerticalAlignment = VerticalAlignment.Center; icon.Children.Add(artwork);
        if (depth > 0)
        {
            var number = new TextBlock { Text = depth.ToString(), FontSize = 8, FontWeight = FontWeights.Bold, Margin = new(1,0,0,0) };
            number.SetResourceReference(TextBlock.ForegroundProperty, "CanvasText");
            var badge = new Border { Child = number, CornerRadius = new(2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            badge.SetResourceReference(Border.BackgroundProperty, "CanvasSurface"); icon.Children.Add(badge);
        }
        return icon;
    }
    internal static OverlayContextMenu Menu(ScopeNavigationPath path, string? mode = null, Action? toggle = null, Func<bool>? canToggle = null)
    {
        var menu = new OverlayContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (mode is not null)
        {
            bool independent = mode == L.Get("GroupCanvas.Temporary");
            var action = new MenuItem { Header = L.Format("ScopeNavigation.SwitchTo", L.Get(independent ? "GroupCanvas.RootProject" : "GroupCanvas.Temporary")),
                Icon = CanvasModeVisuals.Icon(!independent, 18), Tag = "scope-mode",
                IsEnabled = toggle is not null && (canToggle?.Invoke() ?? true) };
            action.ToolTip = L.Get(action.IsEnabled ? independent ? "GroupCanvas.SwitchIndependent" : "GroupCanvas.SwitchProject" : "GroupCanvas.SwitchUnavailable");
            AutomationProperties.SetName(action,L.Format("ScopeNavigation.SwitchTo",L.Get(independent ? "GroupCanvas.RootProject" : "GroupCanvas.Temporary")));
            AutomationProperties.SetItemStatus(action,mode);
            ToolTipService.SetShowOnDisabled(action, true);
            action.Click += (_, _) => { if (canToggle?.Invoke() ?? true) toggle?.Invoke(); }; menu.Items.Add(action);
            menu.Items.Add(new Separator());
        }
        for (int i = 0; i < path.Entries.Count; i++)
        {
            int index = i; var entry = path.Entries[i];
            bool current = i == path.CurrentIndex, future = i > path.CurrentIndex;
            string state = L.Get(current ? "ScopeNavigation.Current" : future ? "ScopeNavigation.History" : "ScopeNavigation.Ancestor");
            var label = new TextBlock { Text = entry.DisplayName, MaxWidth = 360, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal, FontStyle = future ? FontStyles.Italic : FontStyles.Normal };
            label.SetResourceReference(TextBlock.ForegroundProperty, current ? "CanvasAccent" : future ? "CanvasMuted" : "CanvasText");
            var item = new MenuItem { Header = label, Icon = Icon(entry.Glyph, i), InputGestureText = current ? "✓ " + state : future ? "↳ " + state : "",
                ToolTip = entry.DisplayName + "\n" + state, Tag = entry.Key };
            AutomationProperties.SetName(item, entry.DisplayName); AutomationProperties.SetItemStatus(item, state);
            item.IsEnabled = current || entry.Available;
            item.Click += (_, _) => { if (index != path.CurrentIndex && entry.Available) entry.Navigate(); };
            menu.Items.Add(item);
        }
        return menu;
    }
}
