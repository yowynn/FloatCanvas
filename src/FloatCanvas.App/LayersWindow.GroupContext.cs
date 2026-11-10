using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class LayersWindow
{
    private GroupCanvasContext? groupContext;

    private readonly TextBlock groupName = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock groupStorage = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock groupSource = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer groupSourceViewport = new() { MaxHeight = 42, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 3, 0, 3) };
    private readonly TextBlock groupCanvasNotice = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
    private readonly CanvasModeSwitch groupCanvasChoice = new();
    private readonly Button groupExport = new() { MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 0, Margin = new(0, 4, 6, 0), Padding = new(6, 3, 6, 3) };
    private readonly Button groupDetach = new() { MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 0, Margin = new(0, 4, 0, 0), Padding = new(6, 3, 6, 3) };
    private readonly Button groupEmbed = new() { MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 0, Margin = new(0, 4, 6, 0), Padding = new(6, 3, 6, 3) };
    private readonly Button retrySource = new() { MinHeight = 26, Margin = new(3,0,0,0) };
    private readonly Button independentHint = new() { MinHeight = 26, Margin = new(3,0,0,0) };
    private readonly TextBlock scopeNotice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly StackPanel scopeNoticeActions = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private Border? groupContextSite;
    internal GroupCanvasContext? CurrentGroupContext => groupContext;

    private FrameworkElement CreateGroupContextSite()
    {
        var body = new DockPanel();
        groupSourceViewport.Content = groupSource;
        groupCanvasChoice.Click += (_, _) => InvokeGroupAction(context => context.Mode == GroupCanvasMode.Temporary ? context.Root : context.Temporary);
        groupStorage.ToolTip = groupCanvasNotice;
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        void ActionIcon(Button button, string label, string glyph)
        {
            button.Content = IconPath(glyph); button.ToolTip = L.Get(label);
            AutomationProperties.SetName(button, L.Get(label));
            button.Width = button.Height = 28; button.MinHeight = 0; button.Margin = new(3, 0, 0, 0); button.Padding = new(4);
        }
        ActionIcon(groupExport, "GroupCanvas.Export", "M8,11 V2 M4,6 L8,2 L12,6 M2,10 V14 H14 V10");
        ActionIcon(groupDetach, "GroupCanvas.Detach", "M6,3 H2 V7 M10,13 H14 V9 M2,3 L6,7 M14,13 L10,9");
        ActionIcon(groupEmbed, "Prefab.Embed", "M8,2 V10 M4,6 L8,10 L12,6 M2,10 V14 H14 V10");
        groupEmbed.ToolTip = L.Get("GroupCanvas.EmbedHint");
        groupDetach.ToolTip = L.Get("GroupCanvas.DetachHint");
        groupEmbed.Click += (_, _) => InvokeGroupAction(context => context.Embed);
        groupExport.Click += (_, _) => InvokeGroupAction(context => context.Export);
        groupDetach.Click += (_, _) => InvokeGroupAction(context => context.Detach);
        retrySource.Content = L.Get("LayerExperience.RetrySource"); retrySource.Click += (_,_) => InvokeGroupAction(c => c.Retry);
        independentHint.Content = L.Get("GroupCanvas.Temporary"); independentHint.Click += (_,_) => InvokeGroupAction(c => c.Temporary);
        scopeNoticeActions.Children.Add(retrySource); scopeNoticeActions.Children.Add(independentHint);
        actions.Children.Add(groupEmbed); actions.Children.Add(groupExport); actions.Children.Add(groupDetach);
        body.Children.Add(actions);
        foreach (var text in new[] { groupStorage, groupSource, groupCanvasNotice, scopeNotice }) text.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        groupContextSite = new Border { Child = body, Padding = new(0), Visibility = Visibility.Collapsed };
        groupContextSite.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        AutomationProperties.SetName(groupContextSite, L.Get("GroupCanvas.Context"));
        return groupContextSite;
    }

    internal void SetGroupContext(GroupCanvasContext? context)
    {
        // This card configures the entered scope, never a merely selected child.
        if (context is not { IsEntered: true } and not { IsRootContext: true }) context = null;
        // Replace callbacks even when the displayed values are unchanged.
        groupContext = context;
        if (groupContextSite is null) return;
        groupContextSite.Visibility = context is null ? Visibility.Collapsed : Visibility.Visible;
        if (context is null)
        {
            scopeNotice.Text = ""; scopeNotice.Visibility = scopeNoticeActions.Visibility = Visibility.Collapsed;
            groupName.Text = groupStorage.Text = groupSource.Text = groupCanvasNotice.Text = "";
            groupName.ToolTip = groupSource.ToolTip = null;
            groupSourceViewport.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(groupSourceViewport, "");
            groupExport.IsEnabled = groupDetach.IsEnabled = false;
            SynchronizeGroupCanvasMode(); return;
        }
        scopeNotice.Text = context.Notice ?? ""; scopeNotice.Visibility = context.Notice is null ? Visibility.Collapsed : Visibility.Visible;
        retrySource.Visibility = context.Retry is null ? Visibility.Collapsed : Visibility.Visible;
        independentHint.Visibility = (context.Notice == L.Get("LayerExperience.NoOccurrence") || context.Notice == L.Get("LayerExperience.Unplaced")) ? Visibility.Visible : Visibility.Collapsed;
        scopeNoticeActions.Visibility = retrySource.Visibility == Visibility.Visible || independentHint.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        groupName.Text = context.Name; groupName.ToolTip = context.Name;
        groupStorage.Visibility = context.IsRootContext ? Visibility.Collapsed : Visibility.Visible;
        groupStorage.Text = L.Get(context.Storage switch
        {
            GroupStorageKind.Embedded => "GroupCanvas.Embedded",
            GroupStorageKind.ExternalFile => "GroupCanvas.ExternalFile",
            _ => "GroupCanvas.Ordinary"
        });
        bool external = context.Storage == GroupStorageKind.ExternalFile;
        groupSource.Text = external ? context.SourcePath ?? L.Get("GroupCanvas.UnknownSource") : "";
        if (external) groupStorage.Text += " · " + System.IO.Path.GetFileName(groupSource.Text);
        groupSource.ToolTip = groupSource.Text;
        AutomationProperties.SetName(groupSourceViewport, groupSource.Text);
        groupSourceViewport.Visibility = Visibility.Collapsed;

        groupEmbed.IsEnabled = !context.LockedBrowsing;
        groupEmbed.Visibility = context.Embed is null ? Visibility.Collapsed : Visibility.Visible;
        groupExport.Visibility = !context.IsRootContext && context.Storage is GroupStorageKind.Ordinary or GroupStorageKind.Embedded ? Visibility.Visible : Visibility.Collapsed;
        groupDetach.Visibility = !context.IsRootContext && context.Storage is GroupStorageKind.Embedded or GroupStorageKind.ExternalFile ? Visibility.Visible : Visibility.Collapsed;
        groupExport.IsEnabled = context.Export is not null; groupDetach.IsEnabled = context.Detach is not null && !context.LockedBrowsing;
        groupCanvasNotice.Text = context.Notice ?? L.Get(context.IsReadOnly ? context.IsOfflineSnapshot ? "GroupCanvas.ReadOnlyNotice" : "GroupCanvas.LinkedNotice" : context.Mode == GroupCanvasMode.RootProject ? "GroupCanvas.RootNotice" : "GroupCanvas.TemporaryNotice");
        if (external) groupStorage.Text = L.Get(context.IsOfflineSnapshot ? "GroupCanvas.SnapshotStatus" : "GroupCanvas.LinkedStatus");
        groupStorage.ToolTip = (external ? groupSource.Text + "\n" : "") + groupCanvasNotice.Text;
        SynchronizeGroupCanvasMode();
    }

    private void SynchronizeGroupCanvasMode()
    {
        var context = groupContext;
        groupCanvasChoice.Synchronize(context?.Mode ?? GroupCanvasMode.RootProject,
            canMutate() && !getSession().IsTransactionActive && context is not null &&
            (context.Mode == GroupCanvasMode.Temporary ? context.Root : context.Temporary) is not null,
            L.Get(getSession().IsTransactionActive || !canMutate() ? "Inspector.BusyLayersRetained" : "GroupCanvas.SwitchUnavailable"));
    }

    private void InvokeGroupAction(Func<GroupCanvasContext, Action?> action)
    {
        try { CommitRename(); if (groupContext is { } context) action(context)?.Invoke(); }
        catch (Exception ex) { status.Text = ex.Message; }
    }
}
