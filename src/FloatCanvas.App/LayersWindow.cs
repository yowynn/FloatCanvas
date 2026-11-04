using System.Collections.ObjectModel;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Automation = System.Windows.Automation;

namespace FloatCanvas.App;

/// <summary>A virtualized view of layer ownership; mutations stay in editor node commands.</summary>
internal sealed partial class LayersWindow : Wpf.Window
{
    private readonly Func<EditorSession> getSession;
    private readonly Func<bool> canMutate;
    private readonly LayerTreeListBox layers;
    private readonly ObservableCollection<LayerTreeRow> visibleRows = [];
    private readonly Dictionary<Guid, LayerTreeRow> rows = [];
    private readonly Controls.StackPanel breadcrumbs = new() { Orientation = Controls.Orientation.Horizontal };
    private readonly SegmentedChoice rootCanvasChoice = new() { Margin = new(0, 0, 0, 6) };
    private readonly Controls.Button rootCanvasMenu = new() { Width = 28, Height = 28, MinWidth = 0, Padding = new(0), Margin = new(0, 0, 4, 0) };
    private IReadOnlyList<(string Name, Action Navigate)> ancestorPath = [];
    private readonly Controls.ScrollViewer breadcrumbViewport;
    private readonly Controls.Button parentScope;
    private readonly Controls.ScrollViewer headerViewport = new() { VerticalScrollBarVisibility = Controls.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Controls.ScrollBarVisibility.Disabled };
    private readonly Controls.TextBlock status = new() { TextWrapping = Wpf.TextWrapping.Wrap, FontSize = 12 };
    private readonly Controls.TextBlock empty = new() { Text = L.Get("Layers.Empty"), TextWrapping = Wpf.TextWrapping.Wrap, Margin = new(12) };
    private EditorSession? displayedSession, renameSession;
    private SceneDocument? renameDocument;
    private Guid? renameId;
    private bool refreshing, renameComposing;
    private string? renameOriginal;
    private Guid[] shownScopePath = [];
    private RootCanvasKind shownRootCanvas = RootCanvasKind.Main;
    internal bool IsRenaming => renameId is not null;
    internal bool IsReadOnly => getSession().IsReadOnly;
    internal bool CanWrite => canMutate() && !getSession().IsReadOnly;
    internal IReadOnlyList<LayerTreeRow> VisibleNodes => visibleRows;
    public event Action? Dismissed;
    public event Action<int>? CommandKey;
    public event Action? PrefabResourcesRequested;
    public event Action? CreatePrefabRequested;
    public event Action<Guid>? EditPrefabRequested;
    public event Action<Guid>? DetachPrefabRequested;
    public event Action<Guid>? ExportPrefabRequested;
    public event Action? ContextEscapeRequested;
    public event Action<RootCanvasKind>? RootCanvasRequested;
    private readonly Controls.WrapPanel editContext = new() { Orientation = Controls.Orientation.Horizontal };
    private string? editContextName;
    private bool editContextSource;
    private Action? completeContextAction, cancelContextAction;
    private Controls.Button? addGroup;

    public LayersWindow(Func<EditorSession> getSession, Func<bool>? canMutate = null)
    {
        this.getSession = getSession; this.canMutate = canMutate ?? (() => true);
        layers = new(this) { SelectionMode = Controls.SelectionMode.Extended, BorderThickness = new(0), ItemsSource = visibleRows };
        layers.SetResourceReference(Controls.Control.BackgroundProperty, "CanvasSurface");
        layers.SetResourceReference(Controls.Control.ForegroundProperty, "CanvasText");
        Controls.VirtualizingPanel.SetIsVirtualizing(layers, true);
        Controls.VirtualizingPanel.SetVirtualizationMode(layers, Controls.VirtualizationMode.Recycling);
        Controls.ScrollViewer.SetCanContentScroll(layers, true);
        Controls.ScrollViewer.SetHorizontalScrollBarVisibility(layers, Controls.ScrollBarVisibility.Disabled);
        layers.ItemsPanel = new Controls.ItemsPanelTemplate(new Wpf.FrameworkElementFactory(typeof(Controls.VirtualizingStackPanel)));
        layers.ItemTemplate = new Wpf.DataTemplate { VisualTree = new Wpf.FrameworkElementFactory(typeof(LayerTreeRowPresenter)) };
        Title = L.Get("Layers.Title"); Width = 340; Height = 540; MinWidth = 280; MinHeight = 280;
        Topmost = true; ShowInTaskbar = true; Icon = AppIcon.WindowImage;
        var body = new Controls.Grid { Margin = WindowLayout.ContentMargin(4) };
        body.SetResourceReference(Controls.Panel.BackgroundProperty, "CanvasSurface");
        body.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new(1, Wpf.GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        var header = new Controls.StackPanel { Margin = new(0, 0, 0, 6) };
        foreach (var root in new[] { RootCanvasKind.Foreground, RootCanvasKind.Main, RootCanvasKind.Background })
        {
            rootCanvasChoice.Add(L.Get("RootCanvas." + root), root, SegmentedChoicePreviews.Icon("canvas"));
            rootCanvasChoice.Items[^1].ToolTip = L.Get("RootCanvas." + root + "Hint");
        }
        Automation.AutomationProperties.SetName(rootCanvasChoice, L.Get("RootCanvas.Select"));
        rootCanvasChoice.SelectionChanged += (_, _) => ChangeRootCanvas();
        var rootMenu = new OverlayContextMenu();
        foreach (var root in new[] { RootCanvasKind.Main, RootCanvasKind.Foreground, RootCanvasKind.Background })
        {
            var item = new Controls.MenuItem { Header = L.Get("RootCanvas." + root), Icon = RootIcon(root) };
            item.Click += (_, _) => { rootCanvasChoice.SelectedValue = root; };
            rootMenu.Items.Add(item);
        }
        rootCanvasMenu.ContextMenu = rootMenu;
        rootCanvasMenu.Click += (_, _) => { rootMenu.PlacementTarget = rootCanvasMenu; rootMenu.IsOpen = true; };
        rootCanvasMenu.Content = RootIcon(RootCanvasKind.Main);
        var navigation = new Controls.DockPanel();
        parentScope = new Controls.Button { Content = IconPath("M11,3 L6,8 L11,13"), Width = 30, Height = 30, MinWidth = 0, MinHeight = 0,
            Padding = new(0), ToolTip = L.Get("Layers.ParentScope"), Margin = new(0, 0, 4, 0) };
        Automation.AutomationProperties.SetName(parentScope, L.Get("Layers.ParentScope"));
        parentScope.Click += (_, _) => LeaveGroup();
        Controls.DockPanel.SetDock(rootCanvasMenu, Controls.Dock.Left); navigation.Children.Add(rootCanvasMenu);
        breadcrumbViewport = new Controls.ScrollViewer { Content = breadcrumbs, VerticalScrollBarVisibility = Controls.ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = Controls.ScrollBarVisibility.Hidden, Height = 30, ClipToBounds = true };
        breadcrumbViewport.SizeChanged += (_, _) => SizeBreadcrumbs();
        navigation.Children.Add(breadcrumbViewport); header.Children.Add(navigation);
        var actions = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal, HorizontalAlignment = Wpf.HorizontalAlignment.Right };
        var add = new Controls.Button { Content = "+", Width = 30, Height = 30, MinWidth = 0, MinHeight = 0, Padding = new(0), ToolTip = L.Get("Layers.NewGroup"), Margin = new(6, 0, 0, 0) };
        addGroup = add;
        Automation.AutomationProperties.SetName(add, L.Get("Layers.NewGroup"));
        add.Click += (_, _) => CreateGroup();
        var resources = new Controls.Button { Content = IconPath(Instance), Width = 30, Height = 30, MinWidth = 0, MinHeight = 0, Padding = new(0), ToolTip = L.Get("Prefab.Resources"), Margin = new(6, 0, 0, 0) };
        Automation.AutomationProperties.SetName(resources, L.Get("Prefab.Resources"));
        resources.Click += (_, _) => PrefabResourcesRequested?.Invoke(); actions.Children.Add(resources); actions.Children.Add(add);
        var details = new Controls.DockPanel { Margin = new(0, 4, 0, 0), Height = 30 };
        Controls.DockPanel.SetDock(actions, Controls.Dock.Right); details.Children.Add(actions);
        details.Children.Add(CreateGroupContextSite()); header.Children.Add(details);
        groupCanvasChoice.Width = 64; groupCanvasChoice.Height = 28; groupCanvasChoice.Margin = new(0, 0, 6, 0);
        AppWindowChrome.SetHeaderActions(this, groupCanvasChoice);
        headerViewport.Content = header; body.Children.Add(headerViewport);
        // Short windows scroll their controls instead of pushing the layer list out.
        void SizeHeader() => headerViewport.MaxHeight = Math.Max(40, body.ActualHeight - status.ActualHeight - 110);
        body.SizeChanged += (_, _) => SizeHeader(); status.SizeChanged += (_, _) => SizeHeader();
        var listSite = new Controls.Grid(); listSite.Children.Add(layers); listSite.Children.Add(empty);
        Controls.Grid.SetRow(listSite, 2); body.Children.Add(listSite);
        status.Margin = new(0, 6, 0, 0); status.SetResourceReference(Controls.TextBlock.ForegroundProperty, "CanvasMuted");
        Controls.Grid.SetRow(status, 3); body.Children.Add(status); Content = body;
        Automation.AutomationProperties.SetName(layers, L.Get("Inspector.LayerList"));
        AppWindowChrome.Apply(this); AppWindowChrome.SetHelp(this, L.Get("Layers.Help"), L.Get("Layers.TreeHelp"));
        layers.SelectionChanged += (_, _) => SelectRows();
        Closing += (_, e) => { e.Cancel = true; CommitRename(); CancelDrag(); SessionWindowCoordinator.Hide(this); Dismissed?.Invoke(); };
        Deactivated += (_, _) => { if (SessionWindowCoordinator.Find(this)?.IsInputMethodSwitchPending != true) { CommitRename(); CancelDrag(); } };
        IsVisibleChanged += (_, _) => { if (IsVisible) PrepareForShow(); else { CommitRename(); CancelDrag(); } };
        AppInputPolicy.AttachToolbarToggle(this, () => CommandKey?.Invoke(0x09), () => !IsRenaming && this.canMutate());
        PreviewKeyDown += OnKeyDown;
    }

    public void Refresh() { if (IsVisible) PrepareForShow(); }
    internal void PrepareForShow()
    {
        if (!canMutate() || dragIds is not null) return;
        var session = getSession(); if (session.IsTransactionActive) return;
        RefreshRows(session);
        // Prepare only the viewport containers before first display; no native HWND is needed.
        if (Wpf.PresentationSource.FromVisual(layers) is null)
        {
            var size = new Wpf.Size(Math.Max(200, Width - 28), Math.Max(100, Height - 110));
            layers.Measure(size); layers.Arrange(new Wpf.Rect(new(), size)); layers.UpdateLayout();
        }
    }

    private void RefreshRows(EditorSession session)
    {
        refreshing = true;
        try
        {
            var graph = new SceneGraph(session.Document);
            var scopePath = session.EditScopePath.Select(group => group.Id).ToArray();
            bool scopeChanged = !ReferenceEquals(displayedSession, session) || shownRootCanvas != session.ActiveRootCanvas || !shownScopePath.SequenceEqual(scopePath);
            shownRootCanvas = session.ActiveRootCanvas;
            shownScopePath = scopePath;
            rootCanvasChoice.SelectedValue = session.ActiveRootCanvas;
            rootCanvasMenu.ToolTip = L.Get("RootCanvas." + session.ActiveRootCanvas);
            rootCanvasMenu.Content = RootIcon(session.ActiveRootCanvas);
            rootCanvasMenu.IsEnabled = editContextName is null;
            rootCanvasChoice.Visibility = editContextName is null ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
            rootCanvasChoice.IsEnabled = canMutate() && !session.IsTransactionActive && editContextName is null;
            if (renameId is { } editing && (!ReferenceEquals(renameSession, session) || !ReferenceEquals(renameDocument, session.Document) ||
                graph.ParentId(editing) != session.EditScopeId ||
                graph.FindElement(editing) is null && graph.FindGroup(editing) is null && graph.FindInstance(editing) is null)) CancelRename();
            var desired = new List<LayerTreeRow>();
            var parent = session.EditScopeId;
            foreach (var id in graph.EnumerateChildren(parent).Reverse())
            {
                if (SceneCanvasRoots.Resolve(session.Document, id, graph) != session.ActiveRootCanvas) continue;
                var group = graph.FindGroup(id); var element = graph.FindElement(id); var instance = graph.FindInstance(id);
                var definition = instance is null ? null : session.Document.Definitions.FirstOrDefault(item => item.Id == instance.DefinitionId);
                if (!rows.TryGetValue(id, out var row) || !ReferenceEquals(displayedSession, session)) row = new(this, id);
                row.Update(group?.Name ?? element?.Name ?? instance?.Name, group is not null ? GroupName(group) : instance is not null ? InstanceName(instance, definition) : InspectorWindow.ElementName(element!),
                    parent, 0, group is not null, group?.OrderMode == GroupOrderMode.LegacyPassThrough,
                    group?.Visible ?? instance?.Visible ?? element!.Visible, group?.Locked ?? instance?.Locked ?? element!.Locked,
                    graph.IsVisible(id), graph.IsLocked(id), parent is { } ancestor && graph.IsLocked(ancestor), instance is not null, definition?.Source?.IsLinked == true,
                    element?.Kind, element?.WindowSource is not null);
                desired.Add(row);
            }
            bool same = ReferenceEquals(displayedSession, session) && visibleRows.Select(r => r.Id).SequenceEqual(desired.Select(r => r.Id));
            displayedSession = session; rows.Clear(); foreach (var row in desired) rows.Add(row.Id, row);
            if (!same) { visibleRows.Clear(); foreach (var row in desired) visibleRows.Add(row); }
            layers.SelectedItems.Clear();
            foreach (var row in visibleRows.Where(r => session.Selection.Contains(r.Id))) layers.SelectedItems.Add(row);
            empty.Visibility = desired.Count == 0 ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
            RefreshBreadcrumbs(session);
            parentScope.IsEnabled = session.EditScopeId is not null || completeContextAction is not null;
            if (addGroup is not null) addGroup.IsEnabled = CanWrite && !session.IsTransactionActive;
            if (scopeChanged) { if (visibleRows.Count > 0) layers.ScrollIntoView(visibleRows[0]); breadcrumbViewport.ScrollToLeftEnd(); }
        }
        finally { refreshing = false; }
    }

    private void RefreshBreadcrumbs(EditorSession session)
    {
        breadcrumbs.Children.Clear();
        void Add(string name, Guid? scope)
        {
            if (breadcrumbs.Children.Count > 0) breadcrumbs.Children.Add(new Controls.TextBlock { Text = "›", Margin = new(3, 0, 3, 0), VerticalAlignment = Wpf.VerticalAlignment.Center });
            var button = new Controls.Button { Content = new Controls.TextBlock { Text = name, TextTrimming = Wpf.TextTrimming.CharacterEllipsis }, Tag = new ScopeTarget(scope), Padding = new(5, 3, 5, 3), MinWidth = 0, MinHeight = 28,
                IsEnabled = scope != session.EditScopeId && (scope is null || session.IsReadOnly || !session.IsNodeLocked(scope.Value)), ToolTip = name + "\n" + L.Get("Layers.ScopeHint") };
            Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => ChangeScope(scope); breadcrumbs.Children.Add(button);
        }
        foreach (var ancestor in ancestorPath)
        {
            if (breadcrumbs.Children.Count > 0) breadcrumbs.Children.Add(new Controls.TextBlock { Text = "›", VerticalAlignment = Wpf.VerticalAlignment.Center });
            var button = new Controls.Button { Content = new Controls.TextBlock { Text = ancestor.Name, TextTrimming = Wpf.TextTrimming.CharacterEllipsis }, ToolTip = ancestor.Name, Padding = new(5, 3, 5, 3), MinWidth = 0, MinHeight = 28 };
            Automation.AutomationProperties.SetName(button, ancestor.Name);
            button.Click += (_, _) => { CommitRename(); ancestor.Navigate(); };
            breadcrumbs.Children.Add(button);
        }
        Add(editContextName ?? L.Get(session.ActiveRootCanvas == RootCanvasKind.Main ? "Layers.RootScope" : "RootCanvas." + session.ActiveRootCanvas), null);
        foreach (var group in session.EditScopePath) Add(GroupName(group), group.Id);
        SizeBreadcrumbs();
    }
    private void SizeBreadcrumbs()
    {
        var buttons = breadcrumbs.Children.OfType<Controls.Button>().ToArray();
        if (buttons.Length == 0 || breadcrumbViewport.ActualWidth <= 0) return;
        double width = Math.Max(16, (breadcrumbViewport.ActualWidth - (buttons.Length - 1) * 14 - 4) / buttons.Length);
        foreach (var button in buttons)
        {
            button.MaxWidth = width;
            if (button.Content is Controls.TextBlock label) label.MaxWidth = Math.Max(1, width - 14);
        }
        breadcrumbViewport.ScrollToLeftEnd();
    }
    private static string GroupName(CanvasGroup group) => string.IsNullOrWhiteSpace(group.Name) ? L.Get("Layers.UntitledGroup") : group.Name;
    private static Wpf.FrameworkElement RootIcon(RootCanvasKind root) => IconPath(root switch
    {
        RootCanvasKind.Foreground => "M2,2 H12 V12 H2 Z M5,14 H14 V5",
        RootCanvasKind.Background => "M4,4 H14 V14 H4 Z M2,11 V2 H11",
        _ => "M2,3 H14 V13 H2 Z M5,6 H11 V10 H5 Z"
    });
    private static string InstanceName(PrefabInstance instance, PrefabDefinition? definition) => !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name :
        !string.IsNullOrWhiteSpace(definition?.Name) ? definition.Name : L.Get("Prefab.DefaultName");
    internal LayerTreeRow? FindNode(Guid id) => rows.GetValueOrDefault(id);
    internal Controls.ListBoxItem? Container(Guid id) => rows.TryGetValue(id, out var row) ? layers.ItemContainerGenerator.ContainerFromItem(row) as Controls.ListBoxItem : null;
    internal void EnterGroup(Guid id)
    {
        if (!canMutate() || getSession().IsTransactionActive || !rows.TryGetValue(id, out var row) || row.ParentId != getSession().EditScopeId || row.EffectiveLocked && !IsReadOnly) return;
        if (row.IsInstance) { CommitRename(); EditPrefabRequested?.Invoke(id); }
        else if (row.IsGroup) ChangeScope(id);
    }
    internal void SetEditContext(string? name, Action? complete = null, Action? cancel = null, bool source = false)
    {
        bool same = editContextName == name && editContextSource == source && (completeContextAction is null) == (complete is null) && (cancelContextAction is null) == (cancel is null);
        completeContextAction = complete; cancelContextAction = cancel;
        if (same) return;
        editContextName = name; editContextSource = source;
        editContext.Children.Clear();
        RefreshRows(getSession());
    }
    internal void SetAncestorPath(IReadOnlyList<(string Name, Action Navigate)> path)
    { ancestorPath = path; RefreshBreadcrumbs(getSession()); }
    private void ChangeScope(Guid? id)
    {
        if (!canMutate() || getSession().IsTransactionActive) return;
        CommitRename(); CancelDrag();
        try
        {
            var session = getSession();
            var returningChild = session.EditScopePath.FirstOrDefault(group => group.ParentId == id)?.Id;
            session.SetEditScope(id); RefreshRows(session);
            if (returningChild is { } child && FindNode(child) is not null) SelectNode(child);
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }
    private void ChangeRootCanvas()
    {
        if (refreshing || rootCanvasChoice.SelectedValue is not RootCanvasKind root) return;
        var session = getSession();
        if (root == session.ActiveRootCanvas) return;
        if (!canMutate() || session.IsTransactionActive || editContextName is not null) { RefreshRows(session); return; }
        CommitRename(); CancelDrag();
        try
        {
            if (RootCanvasRequested is { } requested) requested(root); else session.SetRootCanvas(root);
            RefreshRows(getSession());
        }
        catch (Exception ex) { status.Text = ex.Message; RefreshRows(getSession()); }
    }
    private void LeaveGroup()
    {
        var session = getSession();
        if (session.EditScopeId is null && canMutate() && !session.IsTransactionActive)
        { CommitRename(); completeContextAction?.Invoke(); return; }
        if (session.EditScopeId is not { } current || !canMutate() || session.IsTransactionActive) return;
        var graph = new SceneGraph(session.Document); Guid? parent = graph.ParentId(current);
        while (!session.IsReadOnly && parent is { } locked && graph.IsLocked(locked)) parent = graph.ParentId(locked);
        ChangeScope(parent);
        if (session.EditScopeId != current && FindNode(current) is not null) SelectNode(current);
    }
    private void CreateGroup()
    {
        if (!CanWrite || getSession().IsTransactionActive) return;
        CommitRename();
        try
        {
            var session = getSession(); if (session.EditScopeId is { } scope && session.IsNodeLocked(scope)) return;
            var id = session.CreateEmptyGroup(); RefreshRows(session); BeginRename(id);
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    internal OverlayContextMenu CreateContextMenu(Guid id)
    {
        var menu = new OverlayContextMenu();
        Controls.MenuItem Add(string key, string icon, string shortcut, Action action)
        {
            var item = new Controls.MenuItem { Header = L.Get(key), Icon = IconPath(icon), InputGestureText = shortcut, Tag = key };
            item.Click += (_, _) => action(); menu.Items.Add(item); return item;
        }
        var enter = Add("Layers.EnterGroup", Folder, "Enter", () => EnterGroup(id));
        var rename = Add("Layers.Rename", "M2,11 V14 H5 L13,6 L10,3 Z", "F2", () => BeginRename(id));
        var collect = Add("Layers.CollectGroup", "M2,3 H14 M4,7 H12 M6,11 H10", "", () => RunLayerCommand(s => s.CollectLegacyGroup(id)));
        var editPrefab = Add("Prefab.EditDefinition", Instance, "Enter", () => EnterGroup(id));
        var detachPrefab = Add("Prefab.Detach", Folder, "", () => DetachPrefabRequested?.Invoke(id));
        var createPrefab = Add("Prefab.CreateFromSelection", Instance, "", () => CreatePrefabRequested?.Invoke());
        var exportPrefab = Add("Prefab.Export", "M8,10 V1 M4,5 L8,1 L12,5 M2,9 V15 H14 V9", "", () =>
        {
            if (new SceneGraph(getSession().Document).FindInstance(id) is { } instance) ExportPrefabRequested?.Invoke(instance.DefinitionId);
        });
        menu.Items.Add(new Controls.Separator());
        Add("Inspector.Duplicate", "M1,1 H10 V10 M5,5 H14 V14 H5 Z", "Ctrl+D", () => RunLayerCommand(s => s.DuplicateSelection()));
        Add("Inspector.Delete", "M3,4 H13 M5,4 V14 H11 V4 M6,1 H10", "Delete", () => RunLayerCommand(s => s.DeleteSelection()));
        menu.Items.Add(new Controls.Separator());
        Add("Inspector.BringToFront", "M3,9 L8,4 L13,9 M8,4 V14 M2,1 H14", "", () => RunLayerCommand(s => s.ReorderSelection(LayerOrder.Front)));
        Add("Inspector.SendToBack", "M3,7 L8,12 L13,7 M8,2 V12 M2,15 H14", "", () => RunLayerCommand(s => s.ReorderSelection(LayerOrder.Back)));
        Add("Layers.MoveOut", "M3,8 H13 M7,3 L2,8 L7,13", "", () => MoveOut(id));
        menu.Items.Add(new Controls.Separator());
        Add("Layers.Group", "M1,1 H8 V8 H1 Z M6,6 H14 V14 H6 Z", "Ctrl+G", () => RunLayerCommand(s => s.GroupSelection()));
        Add("Layers.Ungroup", "M1,1 H6 V6 H1 Z M9,9 H14 V14 H9 Z", "Ctrl+Shift+G", () => RunLayerCommand(s => s.UngroupSelection()));
        menu.Opened += (_, _) =>
        {
            var session = getSession();
            if (!session.Selection.Contains(id) && canMutate() && !session.IsTransactionActive) session.SetSelection([id]);
            var row = FindNode(id); bool allowed = canMutate() && !session.IsTransactionActive && row is not null;
            foreach (var item in menu.Items.OfType<Controls.MenuItem>()) item.IsEnabled = allowed && CanWrite && !row!.EffectiveLocked;
            rename.IsEnabled = allowed && CanWrite && !row!.AncestorLocked;
            enter.Visibility = row?.IsGroup == true ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed; enter.IsEnabled = allowed && (session.IsReadOnly || !row!.EffectiveLocked);
            collect.Visibility = row?.IsLegacy == true ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed; collect.ToolTip = L.Get("Layers.CollectHint");
            editPrefab.Visibility = detachPrefab.Visibility = exportPrefab.Visibility = row?.IsInstance == true ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
            editPrefab.IsEnabled = allowed && (session.IsReadOnly || !row!.EffectiveLocked) && EditPrefabRequested is not null; detachPrefab.IsEnabled &= DetachPrefabRequested is not null; exportPrefab.IsEnabled &= ExportPrefabRequested is not null;
            createPrefab.IsEnabled &= CreatePrefabRequested is not null;
            menu.Items.OfType<Controls.MenuItem>().Single(i => Equals(i.Tag, "Layers.MoveOut")).IsEnabled = allowed && CanWrite && row!.ParentId is not null && !row.EffectiveLocked;
        };
        return menu;
    }
    private void MoveOut(Guid id)
    {
        RunLayerCommand(session =>
        {
            var graph = new SceneGraph(session.Document); var parent = graph.ParentId(id);
            if (parent is not null) session.MoveNodes(session.Selection, graph.ParentId(parent.Value));
        });
    }
    internal void SelectForContext(Guid id)
    {
        if (canMutate() && !getSession().IsTransactionActive && rows.TryGetValue(id, out var row) && row.ParentId == getSession().EditScopeId && !getSession().Selection.Contains(id)) getSession().SetSelection([id]);
        Refresh();
    }
    private void SelectRows()
    {
        if (refreshing) return; var session = getSession();
        if (!canMutate() || session.IsTransactionActive || !ReferenceEquals(session, displayedSession))
        {
            status.Text = L.Get("Inspector.BusySelectionRetained"); refreshing = true;
            try { layers.SelectedItems.Clear(); foreach (var row in visibleRows.Where(r => displayedSession?.Selection.Contains(r.Id) == true)) layers.SelectedItems.Add(row); }
            finally { refreshing = false; }
            return;
        }
        session.SetSelection(layers.SelectedItems.Cast<LayerTreeRow>().Select(r => r.Id)); Refresh();
    }
    internal void SetLayerFlag(Guid id, bool visibility, bool value)
    {
        if (refreshing) return;
        try
        {
            var session = getSession();
            if (!CanWrite || !ReferenceEquals(session, displayedSession) || session.IsTransactionActive) throw new InvalidOperationException(L.Get(session.IsReadOnly ? "GroupCanvas.ReadOnly" : "Inspector.BusyLayersRetained"));
            if (!rows.TryGetValue(id, out var row) || row.ParentId != session.EditScopeId) return;
            if (new SceneGraph(session.Document).ParentId(id) is { } parent && session.IsNodeLocked(parent)) return;
            if (visibility) session.SetNodeVisible(id, value); else session.SetNodeLocked(id, value);
        }
        catch (Exception ex) { status.Text = ex.Message; }
        Refresh();
    }
    private void RunLayerCommand(Action<EditorSession> operation)
    {
        try
        {
            var session = getSession();
            if (!CanWrite || session.IsTransactionActive || !ReferenceEquals(session, displayedSession)) throw new InvalidOperationException(L.Get(session.IsReadOnly ? "GroupCanvas.ReadOnly" : "Inspector.BusyLayersRetained"));
            if (session.Selection.Any(id => !rows.TryGetValue(id, out var row) || row.ParentId != session.EditScopeId)) return;
            if (session.Selection.Count == 0 || session.Selection.All(id => session.IsNodeLocked(id))) return;
            CommitRename(); operation(session); Refresh();
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    private void OnKeyDown(object sender, Input.KeyEventArgs e)
    {
        var key = e.Key == Input.Key.System ? e.SystemKey : e.Key; var modifiers = Input.Keyboard.Modifiers;
        var source = e.OriginalSource as Wpf.DependencyObject;
        if (e.Handled || (modifiers & (Input.ModifierKeys.Alt | Input.ModifierKeys.Windows)) != 0 || EditorShortcutPolicy.OwnsMenu(source)) return;
        if (IsRenaming)
        {
            int projectKey = Input.KeyInterop.VirtualKeyFromKey(key);
            if (!renameComposing && EditorShortcutPolicy.IsProjectCommand(projectKey, (FloatCanvas.Windows.OverlayModifiers)modifiers))
            { e.Handled = true; if (!AppInputPolicy.IsRepeat(e) && canMutate()) { CommitRename(); CommandKey?.Invoke(projectKey); } }
            return;
        }
        var navigationSource = source ?? Input.Keyboard.FocusedElement as Wpf.DependencyObject;
        bool localInput = EditorShortcutPolicy.OwnsLocalEditingKeys(source) || EditorShortcutPolicy.OwnsLocalEditingKeys(Input.Keyboard.FocusedElement as Wpf.DependencyObject);
        // Window preview handlers run before a focused button or segmented choice.
        // Let those controls receive their own activation and navigation keys.
        if (modifiers == Input.ModifierKeys.None && key is Input.Key.Enter or Input.Key.Left or Input.Key.Right or Input.Key.Back &&
            (EditorShortcutPolicy.OwnsLocalEditingKeys(navigationSource) || IsButtonSource(navigationSource))) return;
        if (key == Input.Key.F2 && modifiers == Input.ModifierKeys.None && layers.SelectedItem is LayerTreeRow rename)
        { e.Handled = true; if (!AppInputPolicy.IsRepeat(e)) BeginRename(rename.Id); return; }
        if (modifiers == Input.ModifierKeys.None && layers.SelectedItem is LayerTreeRow selected)
        {
            if (key == Input.Key.Enter && (selected.IsGroup || selected.IsInstance)) { e.Handled = true; if (!AppInputPolicy.IsRepeat(e)) EnterGroup(selected.Id); return; }
            if (key == Input.Key.Right && (selected.IsGroup || selected.IsInstance))
            { e.Handled = true; if (!AppInputPolicy.IsRepeat(e)) EnterGroup(selected.Id); return; }
        }
        if (modifiers == Input.ModifierKeys.None && key is Input.Key.Left or Input.Key.Back)
        { e.Handled = true; if (!AppInputPolicy.IsRepeat(e)) LeaveGroup(); return; }
        if (key == Input.Key.Escape)
        {
            e.Handled = true;
            if (!AppInputPolicy.IsRepeat(e) && !AppWindowChrome.TryCloseHelp(this))
            {
                if (dragIds is not null) CancelDrag();
                else if (getSession().EditScopeId is not null) LeaveGroup();
                else if (editContext.Children.Count > 0 && ContextEscapeRequested is not null) ContextEscapeRequested.Invoke();
                else { SessionWindowCoordinator.Hide(this); Dismissed?.Invoke(); }
            }
            return;
        }
        bool ctrl = (modifiers & Input.ModifierKeys.Control) != 0;
        if (ctrl && key == Input.Key.G && !localInput)
        { e.Handled = true; if (!AppInputPolicy.IsRepeat(e)) RunLayerCommand(s => { if ((modifiers & Input.ModifierKeys.Shift) != 0) s.UngroupSelection(); else s.GroupSelection(); }); return; }
        int command = Input.KeyInterop.VirtualKeyFromKey(key);
        if (!EditorShortcutPolicy.ShouldRoute(command, modifiers, source, preserveListNavigation: true, localInput: localInput)) return;
        e.Handled = true;
        if (!AppInputPolicy.IsRepeat(e) || EditorShortcutPolicy.AllowsRepeat(command, (FloatCanvas.Windows.OverlayModifiers)modifiers)) RouteCommandKey(command, ctrl);
    }
    private static bool IsButtonSource(Wpf.DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Primitives.ButtonBase) return true;
            source = source is Media.Visual or Media.Media3D.Visual3D ? Media.VisualTreeHelper.GetParent(source) ?? Wpf.LogicalTreeHelper.GetParent(source) : Wpf.LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    private bool RouteCommandKey(int key, bool control)
    { if (!CanRouteCommandKey(key, control)) return false; if (canMutate()) CommandKey?.Invoke(key); return true; }
    private bool CanRouteCommandKey(int key, bool control) => !IsRenaming && (!getSession().IsReadOnly || control && key is 0x41 or 0x43) && EditorShortcutPolicy.ShouldRoute(key,
        control ? Input.ModifierKeys.Control : Input.ModifierKeys.None, null, preserveListNavigation: true);
    private void SelectNode(Guid id)
    {
        getSession().SetSelection([id]); RefreshRows(getSession());
        if (!rows.TryGetValue(id, out var row)) return;
        layers.ScrollIntoView(row);
        if (IsActive) Container(id)?.Focus();
    }

    internal bool BeginRename(Guid id)
    {
        if (renameId == id) return true; CommitRename(); var session = getSession();
        if (!CanWrite || session.IsTransactionActive || !ReferenceEquals(session, displayedSession) || !rows.TryGetValue(id, out var row) || row.AncestorLocked) return false;
        var graph = new SceneGraph(session.Document);
        if (graph.FindElement(id) is null && graph.FindGroup(id) is null && graph.FindInstance(id) is null) return false;
        renameSession = session; renameDocument = session.Document; renameId = id; renameOriginal = row.SourceName; renameComposing = false;
        row.DraftName = row.Name; row.Renaming = true; layers.ScrollIntoView(row);
        if (IsVisible) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        { if (renameId == id) LayerTreeRowPresenter.Find(Container(id))?.FocusEditor(); }));
        return true;
    }
    internal void RenameComposition(bool value) => renameComposing = value;
    internal bool HandleRenameKey(Input.Key key)
    {
        if (!IsRenaming || renameComposing) return false;
        if (key == Input.Key.Enter) { CommitRename(); return true; }
        if (key == Input.Key.Escape) { CancelRename(); Refresh(); return true; }
        return false;
    }
    internal void RenameLostFocus(Guid id)
    { if (renameId == id && SessionWindowCoordinator.Find(this)?.IsInputMethodSwitchPending != true) CommitRename(); }
    private void CommitRename()
    {
        if (renameId is not { } id || renameSession is not { } session || !rows.TryGetValue(id, out var row)) return;
        string name = row.DraftName; string? original = renameOriginal; var document = renameDocument; CancelRename();
        try
        {
            if (!CanWrite || session.IsTransactionActive || !ReferenceEquals(session, getSession()) || !ReferenceEquals(session.Document, document)) return;
            var graph = new SceneGraph(session.Document); string? currentName = graph.FindGroup(id)?.Name ?? graph.FindElement(id)?.Name ?? graph.FindInstance(id)?.Name;
            if (currentName != original || string.IsNullOrWhiteSpace(original) && name == row.Name) return;
            session.RenameLayer(id, name);
        }
        catch (Exception ex) { status.Text = ex.Message; }
        Refresh();
    }
    private void CancelRename()
    {
        var id = renameId; renameId = null; renameSession = null; renameDocument = null; renameOriginal = null; renameComposing = false;
        if (id is { } value && rows.TryGetValue(value, out var row)) row.Renaming = false;
    }
    internal static System.Windows.Shapes.Path IconPath(string data)
    {
        var path = new System.Windows.Shapes.Path { Data = Media.Geometry.Parse(data), Width = 16, Height = 16, StrokeThickness = 1.4,
            StrokeStartLineCap = Media.PenLineCap.Round, StrokeEndLineCap = Media.PenLineCap.Round, StrokeLineJoin = Media.PenLineJoin.Round, Stretch = Media.Stretch.Uniform };
        path.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "CanvasText"); return path;
    }
    private sealed record ScopeTarget(Guid? Id);
    internal const string Folder = "M1,4 V2 H6 L8,4 H15 V14 H1 Z";
    internal const string Instance = "M8,1 L15,5 V12 L8,16 L1,12 V5 Z M1,5 L8,9 L15,5 M8,9 V16";
    internal const string LinkedInstance = "M8,1 L15,5 V12 L8,16 L1,12 V5 Z M1,5 L8,9 L15,5 M8,9 V16 M5,7 L11,10";
    internal const string Eye = "M1,8 C5,2 11,2 15,8 C11,14 5,14 1,8 Z M8,5 A3,3 0 1 1 8,11 A3,3 0 1 1 8,5";
    internal const string EyeOff = "M1,8 C5,2 11,2 15,8 C11,14 5,14 1,8 M2,2 L14,14";
    internal const string Lock = "M4,7 V4 A4,4 0 0 1 12,4 V7 M3,7 H13 V15 H3 Z M8,10 V12";
    internal const string Unlock = "M6,7 V4 A4,4 0 0 1 14,4 M3,7 H13 V15 H3 Z M8,10 V12";
}
