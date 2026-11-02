using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal enum PrefabResourceAction { Create, Place, Edit, Rename, Export, Import, Link, Check, Refresh, Relink, Embed, EditSource, RemoveUnused }
internal sealed record PrefabResourceRequest(PrefabResourceAction Action, Guid? DefinitionId, EditorSession Owner, long OwnerRevision);
internal sealed record PrefabResourceRow(Guid Id, string Name, string Summary, bool Linked);

/// <summary>One virtualized list of the owning project's shared resources.</summary>
internal sealed class PrefabResourcesWindow : Window
{
    private readonly Func<EditorSession> getOwner;
    private readonly Func<bool> canMutate;
    private readonly Func<EditorSession, PrefabDefinition, string?>? getLinkStatus;
    private readonly ObservableCollection<PrefabResourceRow> rows = [];
    private readonly ListBox list = new() { MinHeight = 72, HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new(0) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private readonly Dictionary<PrefabResourceAction, Button> buttons = [];
    private EditorSession? displayedOwner;
    private long displayedRevision = -1;
    private bool busy;
    internal IReadOnlyList<PrefabResourceRow> Rows => rows;
    internal bool IsBusy => busy;
    internal Guid? SelectedDefinitionId => (list.SelectedItem as PrefabResourceRow)?.Id;
    internal Func<PrefabResourceRequest, Task>? Execute { get; set; }
    public event Action? Dismissed;
    public event Action<int>? CommandKey;

    internal PrefabResourcesWindow(Func<EditorSession> getOwner, Func<bool>? canMutate = null, Func<EditorSession, PrefabDefinition, string?>? getLinkStatus = null)
    {
        this.getOwner = getOwner; this.canMutate = canMutate ?? (() => true); this.getLinkStatus = getLinkStatus;
        Title = L.Get("Prefab.Resources"); Width = 440; Height = 540; MinWidth = 330; MinHeight = 300;
        Topmost = true; ShowInTaskbar = true; Icon = AppIcon.WindowImage;
        var body = new DockPanel { Margin = WindowLayout.ContentMargin(4) };
        body.SetResourceReference(Panel.BackgroundProperty, "CanvasSurface");
        var actions = new WrapPanel { Margin = new(0, 0, 0, 8) };
        void Add(PrefabResourceAction action, string key)
        {
            var button = new Button { Content = L.Get(key), MinHeight = PropertyPanelLayout.ControlHeight, Padding = new(9, 3, 9, 3), Margin = new(0, 0, 6, 6), Tag = action };
            button.Click += async (_, _) => await PerformAsync(action);
            buttons.Add(action, button); actions.Children.Add(button);
        }
        Add(PrefabResourceAction.Create, "Prefab.CreateFromSelection"); Add(PrefabResourceAction.Import, "Prefab.Import");
        Add(PrefabResourceAction.Link, "Prefab.LinkFile"); Add(PrefabResourceAction.RemoveUnused, "Prefab.RemoveUnused");
        DockPanel.SetDock(actions, Dock.Top); body.Children.Add(actions);
        var statusViewport = new ScrollViewer { Content = status, MaxHeight = 40,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        DockPanel.SetDock(statusViewport, Dock.Bottom); body.Children.Add(statusViewport);
        var selectionActions = new WrapPanel { Margin = new(0, 6, 0, 0) };
        void SelectionButton(PrefabResourceAction action, string key)
        {
            var button = new Button { Content = L.Get(key), MinHeight = PropertyPanelLayout.ControlHeight, Padding = new(9, 3, 9, 3), Margin = new(0, 0, 6, 6), Tag = action };
            button.Click += async (_, _) => await PerformAsync(action);
            buttons.Add(action, button); selectionActions.Children.Add(button);
        }
        SelectionButton(PrefabResourceAction.Place, "Prefab.Place"); SelectionButton(PrefabResourceAction.Edit, "Prefab.EditDefinition");
        SelectionButton(PrefabResourceAction.Rename, "Prefab.RenameDefinition");
        SelectionButton(PrefabResourceAction.Export, "Prefab.Export"); SelectionButton(PrefabResourceAction.Refresh, "Prefab.Refresh");
        SelectionButton(PrefabResourceAction.Relink, "Prefab.Relink"); SelectionButton(PrefabResourceAction.Embed, "Prefab.Embed");
        SelectionButton(PrefabResourceAction.EditSource, "Prefab.EditSource"); SelectionButton(PrefabResourceAction.Check, "Prefab.Check");
        var actionViewport = new ScrollViewer { Content = selectionActions, MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        DockPanel.SetDock(actionViewport, Dock.Bottom); body.Children.Add(actionViewport);
        void ResizeActions()
        {
            if (body.ActualHeight <= 0) return;
            actionViewport.MaxHeight = Math.Max(PropertyPanelLayout.ControlHeight,
                (body.ActualHeight - actions.ActualHeight - actions.Margin.Top - actions.Margin.Bottom - statusViewport.ActualHeight) * .4);
        }
        body.SizeChanged += (_, _) => ResizeActions(); actions.SizeChanged += (_, _) => ResizeActions(); statusViewport.SizeChanged += (_, _) => ResizeActions();
        list.SetResourceReference(Control.BackgroundProperty, "CanvasSurface"); list.SetResourceReference(Control.ForegroundProperty, "CanvasText");
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true); list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var item = new FrameworkElementFactory(typeof(StackPanel)); item.SetValue(FrameworkElement.MarginProperty, new Thickness(6));
        var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PrefabResourceRow.Name)));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); item.AppendChild(name);
        var summary = new FrameworkElementFactory(typeof(TextBlock)); summary.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PrefabResourceRow.Summary)));
        summary.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(PrefabResourceRow.Summary)));
        summary.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); summary.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0)); item.AppendChild(summary);
        list.ItemTemplate = new DataTemplate { VisualTree = item }; list.ItemsSource = rows;
        list.SelectionChanged += (_, _) => { status.Text = ""; RefreshActions(); };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.MouseDoubleClick += async (_, e) => { if (list.SelectedItem is not null) { e.Handled = true; await PerformAsync(PrefabResourceAction.Edit); } };
        body.Children.Add(list); Content = body;
        System.Windows.Automation.AutomationProperties.SetName(list, L.Get("Prefab.Resources"));
        status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        AppWindowChrome.Apply(this); AppWindowChrome.SetHelp(this, L.Get("Prefab.Resources"), L.Get("Prefab.ResourcesHelp"));
        Closing += (_, e) => { e.Cancel = true; SessionWindowCoordinator.Hide(this); Dismissed?.Invoke(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
        AppInputPolicy.AttachToolbarToggle(this, () => CommandKey?.Invoke(0x09), () => !busy);
        PreviewKeyDown += async (_, e) =>
        {
            if (EditorShortcutPolicy.OwnsLocalEditingKeys(e.OriginalSource as DependencyObject)) return;
            if (e.Key == Key.Escape) { e.Handled = true; if (!AppWindowChrome.TryCloseHelp(this)) { SessionWindowCoordinator.Hide(this); Dismissed?.Invoke(); } }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; if (!e.IsRepeat) await PerformAsync(PrefabResourceAction.Edit); }
            else if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; if (!e.IsRepeat) await PerformAsync(PrefabResourceAction.Rename); }
        };
    }

    internal void Select(Guid definitionId)
    {
        list.SelectedItem = rows.FirstOrDefault(row => row.Id == definitionId);
        if (list.SelectedItem is not null) list.ScrollIntoView(list.SelectedItem);
    }
    public void Refresh()
    {
        if (busy) return;
        var owner = getOwner(); var selected = ReferenceEquals(owner, displayedOwner) ? SelectedDefinitionId : null;
        if (ReferenceEquals(owner, displayedOwner) && displayedRevision == owner.PreviewRevision) { RefreshActions(); return; }
        status.Text = "";
        var references = owner.Document.Instances.Concat(owner.Document.Definitions.SelectMany(definition => definition.Graph.Instances))
            .GroupBy(instance => instance.DefinitionId).ToDictionary(group => group.Key, group => group.Count());
        var next = owner.Document.Definitions.Select(definition => new PrefabResourceRow(definition.Id,
            string.IsNullOrWhiteSpace(definition.Name) ? L.Get("Prefab.DefaultName") : definition.Name,
            L.Format("Prefab.ResourceSummary", L.Get(definition.Source?.IsLinked == true ? "Prefab.Linked" : "Prefab.Embedded"), references.GetValueOrDefault(definition.Id)) +
                (definition.Source?.IsLinked == true ? "\n" + L.Format(definition.Source.IsRelative ? "GroupCanvas.RelativeSource" : "GroupCanvas.AbsoluteSource", definition.Source.Locator) : "") +
                (definition.Source?.IsLinked == true && getLinkStatus?.Invoke(owner, definition) is { Length: > 0 } state ? "\n" + state : ""),
            definition.Source?.IsLinked == true)).ToArray();
        if (!rows.SequenceEqual(next)) { rows.Clear(); foreach (var row in next) rows.Add(row); }
        displayedOwner = owner; displayedRevision = owner.PreviewRevision; if (selected is { } id) Select(id);
        if (rows.Count == 0) status.Text = L.Get("Prefab.EmptyResources");
        else if (status.Text == L.Get("Prefab.EmptyResources")) status.Text = "";
        RefreshActions();
    }
    private void RefreshActions()
    {
        bool allowed = !busy && canMutate() && !getOwner().IsTransactionActive && Execute is not null;
        var selected = list.SelectedItem as PrefabResourceRow;
        foreach (var (action, button) in buttons)
        {
            bool global = action is PrefabResourceAction.Create or PrefabResourceAction.Import or PrefabResourceAction.Link or PrefabResourceAction.RemoveUnused;
            bool linked = action is PrefabResourceAction.Check or PrefabResourceAction.Refresh or PrefabResourceAction.Relink or PrefabResourceAction.Embed or PrefabResourceAction.EditSource;
            button.Visibility = linked && selected?.Linked != true ? Visibility.Collapsed : Visibility.Visible;
            button.IsEnabled = allowed && (global || selected is not null) && (!linked || selected?.Linked == true) &&
                (action != PrefabResourceAction.Create || getOwner().Selection.Count > 0);
        }
    }
    internal async Task PerformAsync(PrefabResourceAction action, Guid? definitionId = null)
    {
        var owner = getOwner();
        if (busy || !canMutate() || Execute is null || owner.IsTransactionActive) return;
        if (!ReferenceEquals(owner, displayedOwner)) { Refresh(); return; }
        definitionId ??= SelectedDefinitionId;
        if (action is not (PrefabResourceAction.Create or PrefabResourceAction.Import or PrefabResourceAction.Link or PrefabResourceAction.RemoveUnused) && definitionId is null) return;
        status.Text = ""; busy = true; RefreshActions();
        try { await Execute(new(action, definitionId, owner, owner.SceneRevision)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { SetStatus(ex.Message, owner, definitionId); }
        catch (OperationCanceledException) { }
        finally { busy = false; Refresh(); }
    }
    internal void SetStatus(string message, EditorSession? owner = null, Guid? definitionId = null)
    {
        if (owner is not null && !ReferenceEquals(owner, getOwner()) || definitionId is { } id && SelectedDefinitionId != id) return;
        status.Text = message;
    }
    internal void InvalidateResourceStatus() { displayedRevision = -1; Refresh(); }
}
