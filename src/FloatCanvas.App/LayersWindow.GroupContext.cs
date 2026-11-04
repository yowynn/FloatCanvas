using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class LayersWindow
{
    private GroupCanvasContext? groupContext;
    private bool synchronizingGroupContext;
    private readonly TextBlock groupName = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock groupStorage = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock groupSource = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer groupSourceViewport = new() { MaxHeight = 42, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 3, 0, 3) };
    private readonly TextBlock groupCanvasNotice = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
    private readonly SegmentedChoice groupCanvasChoice = new() { Margin = new(0, 5, 0, 0) };
    private readonly Button groupExport = new() { MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 0, Margin = new(0, 4, 6, 0), Padding = new(6, 3, 6, 3) };
    private readonly Button groupDetach = new() { MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 0, Margin = new(0, 4, 0, 0), Padding = new(6, 3, 6, 3) };
    private readonly Button groupEmbed = new() { MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 0, Margin = new(0, 4, 6, 0), Padding = new(6, 3, 6, 3) };
    private Border? groupContextSite;
    internal GroupCanvasContext? CurrentGroupContext => groupContext;

    private FrameworkElement CreateGroupContextSite()
    {
        var body = new DockPanel();
        groupSourceViewport.Content = groupSource;
        groupCanvasChoice.IconOnly = true;
        groupCanvasChoice.Add(L.Get("GroupCanvas.Temporary"), GroupCanvasMode.Temporary, SegmentedChoicePreviews.Icon("canvas"));
        groupCanvasChoice.Add(L.Get("GroupCanvas.RootProject"), GroupCanvasMode.RootProject, SegmentedChoicePreviews.Icon("combined"));
        groupCanvasChoice.Items[0].ToolTip = L.Get("GroupCanvas.TemporaryHint");
        groupCanvasChoice.Items[1].ToolTip = L.Get("GroupCanvas.RootHint");
        AutomationProperties.SetName(groupCanvasChoice, L.Get("GroupCanvas.Display"));
        groupCanvasChoice.SelectionChanged += (_, _) => ChangeGroupCanvasMode();
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
        groupEmbed.Click += (_, _) => InvokeGroupAction(context => context.Embed);
        groupExport.Click += (_, _) => InvokeGroupAction(context => context.Export);
        groupDetach.Click += (_, _) => InvokeGroupAction(context => context.Detach);
        actions.Children.Add(groupEmbed); actions.Children.Add(groupExport); actions.Children.Add(groupDetach);
        DockPanel.SetDock(actions, Dock.Right); body.Children.Add(actions); body.Children.Add(groupStorage);
        foreach (var text in new[] { groupStorage, groupSource, groupCanvasNotice }) text.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
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
            groupName.Text = groupStorage.Text = groupSource.Text = groupCanvasNotice.Text = "";
            groupName.ToolTip = groupSource.ToolTip = null;
            groupSourceViewport.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(groupSourceViewport, "");
            groupExport.IsEnabled = groupDetach.IsEnabled = false;
            SynchronizeGroupCanvasMode(); return;
        }
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
        groupStorage.ToolTip = external ? groupSource.Text + "\n" + L.Get("GroupCanvas.ReadOnlyNotice") : groupCanvasNotice.Text;
        groupEmbed.Visibility = context.Embed is null ? Visibility.Collapsed : Visibility.Visible;
        groupExport.Visibility = !context.IsRootContext && context.Storage is GroupStorageKind.Ordinary or GroupStorageKind.Embedded ? Visibility.Visible : Visibility.Collapsed;
        groupDetach.Visibility = !context.IsRootContext && context.Storage is GroupStorageKind.Embedded or GroupStorageKind.ExternalFile ? Visibility.Visible : Visibility.Collapsed;
        groupExport.IsEnabled = context.Export is not null; groupDetach.IsEnabled = context.Detach is not null;
        groupCanvasNotice.Text = L.Get(context.IsReadOnly ? "GroupCanvas.ReadOnlyNotice" : context.Mode == GroupCanvasMode.RootProject ? "GroupCanvas.RootNotice" : "GroupCanvas.TemporaryNotice");
        SynchronizeGroupCanvasMode();
    }

    private void SynchronizeGroupCanvasMode()
    {
        synchronizingGroupContext = true;
        try
        {
            groupCanvasChoice.SelectedValue = groupContext?.Mode;
            if (groupContext is not { } context) { foreach (var item in groupCanvasChoice.Items) item.IsEnabled = false; return; }
            groupCanvasChoice.Items[0].IsEnabled = context.Mode == GroupCanvasMode.Temporary || context.Temporary is not null;
            groupCanvasChoice.Items[1].IsEnabled = context.Mode == GroupCanvasMode.RootProject || context.Root is not null;
        }
        finally { synchronizingGroupContext = false; }
    }

    private void ChangeGroupCanvasMode()
    {
        if (synchronizingGroupContext) return;
        if (groupContext is not { } context) { SynchronizeGroupCanvasMode(); return; }
        if (groupCanvasChoice.SelectedValue is not GroupCanvasMode requested || requested == context.Mode) return;
        // The owning session supplies the accepted mode after its pending-edit guards.
        try { InvokeGroupAction(current => requested == GroupCanvasMode.Temporary ? current.Temporary : current.Root); }
        finally { SynchronizeGroupCanvasMode(); }
    }

    private void InvokeGroupAction(Func<GroupCanvasContext, Action?> action)
    {
        try { CommitRename(); if (groupContext is { } context) action(context)?.Invoke(); }
        catch (Exception ex) { status.Text = ex.Message; }
    }
}
