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
    private readonly Controls.Button rootCanvasMenu = new() { Width = ToolbarUpperRow.ButtonSize, Height = ToolbarUpperRow.ButtonSize, MinWidth = 0, MinHeight = 0,
        Padding = new(3), Margin = new(2,0,2,0), VerticalAlignment = Wpf.VerticalAlignment.Center, HorizontalContentAlignment = Wpf.HorizontalAlignment.Center };
    private readonly Controls.ScrollViewer breadcrumbViewport;
    private readonly Controls.ScrollViewer headerViewport = new() { VerticalScrollBarVisibility = Controls.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Controls.ScrollBarVisibility.Disabled };
    private readonly Controls.TextBlock status = new() { TextWrapping = Wpf.TextWrapping.Wrap, FontSize = 12 };
    private readonly Controls.TextBlock statistics = new() { FontSize = 11, Margin = new(0, 6, 0, 0), TextWrapping = Wpf.TextWrapping.Wrap };
    private readonly Controls.TextBlock empty = new() { Text = L.Get("Layers.Empty"), TextWrapping = Wpf.TextWrapping.Wrap, Margin = new(12) };
    private EditorSession? displayedSession, renameSession;
    private SceneDocument? renameDocument;
    private Guid? renameId;
    private bool refreshing, renameComposing;
    private string? renameOriginal;
    private Guid[] shownScopePath = [];
    private RootCanvasKind shownRootCanvas = RootCanvasKind.Main;
    internal bool IsRenaming => renameId is not null;
    internal bool IndependentPreview => groupContext?.Mode == GroupCanvasMode.Temporary;
    internal bool IsReadOnly => getSession().IsReadOnly;
    private bool scopeLocked;
    internal bool CanWrite => canMutate() && !getSession().IsReadOnly && !scopeLocked;
    internal IReadOnlyList<LayerTreeRow> VisibleNodes => visibleRows;
    public event Action? Dismissed;
    public event Action<int>? CommandKey;
    public event Action? PrefabResourcesRequested;
    public event Action? CreatePrefabRequested;
    public event Action<Guid>? EditPrefabRequested;
    public event Action<Guid>? DetachPrefabRequested;
    public event Action<Guid>? ExportPrefabRequested;
    public event Action<RootCanvasKind>? RootCanvasRequested;
    private readonly Controls.WrapPanel editContext = new() { Orientation = Controls.Orientation.Horizontal };
    private string? editContextName;
    private bool editContextSource;
    private Action? completeContextAction, cancelContextAction;
    private readonly Controls.Button locateSelection = new() { Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new(0), Margin = new(3, 0, 3, 0), Visibility = Wpf.Visibility.Collapsed };

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
            item.IsCheckable = true;
            rootMenu.Opened += (_, _) => item.IsChecked = getSession().ActiveRootCanvas == root;
            item.Click += (_, _) => { rootCanvasChoice.SelectedValue = root; };
            rootMenu.Items.Add(item);
        }
        rootCanvasMenu.ContextMenu = rootMenu;
        rootCanvasMenu.Click += (_, _) => { rootMenu.PlacementTarget = rootCanvasMenu; rootMenu.IsOpen = true; };
        rootCanvasMenu.SetResourceReference(Controls.Control.StyleProperty, "CanvasToolbarInlineButton");
        rootCanvasMenu.Content = RootIcon(RootCanvasKind.Main);
        var navigation = new Controls.DockPanel { Margin = new(2,0,2,0) };
        Controls.DockPanel.SetDock(rootCanvasMenu, Controls.Dock.Left); navigation.Children.Add(rootCanvasMenu);
        breadcrumbViewport = new Controls.ScrollViewer { Content = breadcrumbs, VerticalScrollBarVisibility = Controls.ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = Controls.ScrollBarVisibility.Hidden, Height = 32, ClipToBounds = true };
        breadcrumbViewport.SizeChanged += (_, _) => SizeBreadcrumbs();
        navigation.Children.Add(breadcrumbViewport);
        var navigationFrame = new RoundedSurfaceBorder { Child = navigation, CornerRadius = new(6), BorderThickness = new(1), Height = 34 };
        navigationFrame.SetResourceReference(Controls.Border.BackgroundProperty, "CanvasInput");
        navigationFrame.SetResourceReference(Controls.Border.BorderBrushProperty, "CanvasBorder");
        header.Children.Add(navigationFrame);
        var actions = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal, HorizontalAlignment = Wpf.HorizontalAlignment.Right };
        locateSelection.SetResourceReference(Controls.Control.StyleProperty, "CanvasCaptionButton");
        locateSelection.Content = CommandGlyphs.Create(CommandGlyphs.LocateLayer);
        locateSelection.ToolTip = L.Get("Layers.LocateSelectionHint");
        Automation.AutomationProperties.SetName(locateSelection, L.Get("Layers.LocateSelection"));
        locateSelection.Click += (_, _) => LocateSelection();
        var resources = new Controls.Button { Content = IconPath(Instance), Width = 30, Height = 30, MinWidth = 0, MinHeight = 0, Padding = new(0), ToolTip = L.Get("Prefab.Resources"), Margin = new(6, 0, 0, 0) };
        Automation.AutomationProperties.SetName(resources, L.Get("Prefab.Resources"));
        resources.Click += (_, _) => PrefabResourcesRequested?.Invoke();
        CreateGroupContextSite();
        actions.Children.Add(groupContextSite!); actions.Children.Add(resources);
        var details = new Controls.DockPanel { Margin = new(0, 4, 0, 0), Height = 30 };
        Controls.DockPanel.SetDock(actions, Controls.Dock.Right); details.Children.Add(actions);
        details.Children.Add(groupStorage); header.Children.Add(details); header.Children.Add(scopeNotice); header.Children.Add(scopeNoticeActions);
        groupCanvasChoice.Width = 62; groupCanvasChoice.Height = 28; groupCanvasChoice.Margin = new(3,0,3,0);
        var titleActions = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal };
        titleActions.Children.Add(locateSelection); titleActions.Children.Add(groupCanvasChoice);
        AppWindowChrome.SetHeaderActions(this, titleActions);
        headerViewport.Content = header; body.Children.Add(headerViewport);
        // Short windows scroll their controls instead of pushing the layer list out.
        void SizeHeader() => headerViewport.MaxHeight = Math.Max(40, body.ActualHeight - status.ActualHeight - statistics.ActualHeight - 110);
        body.SizeChanged += (_, _) => SizeHeader(); status.SizeChanged += (_, _) => SizeHeader(); statistics.SizeChanged += (_, _) => SizeHeader();
        var listSite = new Controls.Grid(); listSite.Children.Add(layers); listSite.Children.Add(empty);
        Controls.Grid.SetRow(listSite, 2); body.Children.Add(listSite);
        viewMoveTarget.Click += (_, _) => (viewMoveTarget.Tag as Action)?.Invoke();
        var footer = new Controls.StackPanel(); footer.Children.Add(statistics); footer.Children.Add(status); footer.Children.Add(viewMoveTarget);
        statistics.SetResourceReference(Controls.TextBlock.ForegroundProperty, "CanvasMuted");
        var statusStyle = new Wpf.Style(typeof(Controls.TextBlock));
        var noMessage = new Wpf.Trigger { Property = Controls.TextBlock.TextProperty, Value = "" };
        noMessage.Setters.Add(new Wpf.Setter(VisibilityProperty, Wpf.Visibility.Collapsed)); statusStyle.Triggers.Add(noMessage); status.Style = statusStyle;
        status.Margin = new(0, 6, 0, 0); status.SetResourceReference(Controls.TextBlock.ForegroundProperty, "CanvasMuted");
        Controls.Grid.SetRow(footer, 3); body.Children.Add(footer); Content = body;
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
        using var sceneRead = SceneGraph.BeginReadScope();
        if (dragIds is not null && !IsDragCurrent) CancelDrag();
        // Busy document commands block mutations, not a read-only refresh after
        // they remove or replace the currently displayed scope.
        if (dragIds is not null) return;
        var session = getSession(); if (session.IsTransactionActive) return;
        RefreshRows(session);
        // Prepare only the viewport containers before first display; no native HWND is needed.
        if (Wpf.PresentationSource.FromVisual(layers) is null)
        {
            var size = new Wpf.Size(Math.Max(200, Width - 28), Math.Max(100, Height - 110));
            layers.Measure(size); layers.Arrange(new Wpf.Rect(new(), size)); layers.UpdateLayout();
        }
    }

    private object? rowsStamp;
    private void RefreshRows(EditorSession session)
    {
        refreshing = true;
        try
        {
            if (moveTargetValid?.Invoke() == false) { viewMoveTarget.Visibility = Wpf.Visibility.Collapsed; viewMoveTarget.Tag = null; moveTargetValid = null; }
            object stamp = (session, session.Document, session.PreviewRevision, session.EditScopeVersion, L.Preference, canMutate(), editContextName, groupContext?.Mode);
            if (Equals(rowsStamp, stamp)) { SyncSelection(session); RefreshStatistics(session); RefreshBreadcrumbs(session); return; }
            rowsStamp = stamp;
            var graph = SceneGraph.Read(session.Document);
            scopeLocked = session.EditScopeId is { } scopeId && graph.IsLocked(scopeId);
            var scopePath = session.EditScopePath.Select(group => group.Id).ToArray();
            bool scopeChanged = !ReferenceEquals(displayedSession, session) || shownRootCanvas != session.ActiveRootCanvas || !shownScopePath.SequenceEqual(scopePath);
            shownRootCanvas = session.ActiveRootCanvas;
            shownScopePath = scopePath;
            rootCanvasChoice.SelectedValue = session.ActiveRootCanvas;
            rootCanvasMenu.ToolTip = L.Get("RootCanvas." + session.ActiveRootCanvas) + " · " + L.Get(editContextName is null ? "RootCanvas.Select" : "RootCanvas.ReturnUnavailable");
            Controls.ToolTipService.SetShowOnDisabled(rootCanvasMenu, true);
            rootCanvasMenu.Content = RootIcon(session.ActiveRootCanvas);
            rootCanvasMenu.IsEnabled = editContextName is null && canMutate() && !session.IsTransactionActive;
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
                    element?.Kind, element?.WindowSource is not null, graph.AncestorIds(id).Select(graph.FindGroup).FirstOrDefault(g => g?.Visible == false) is { } hiddenGroup ? GroupName(hiddenGroup) : null,
                    element?.Kind == ElementKind.Symbol ? SymbolGeometry.Shape(element.Symbol, element.CustomSymbol) : null);
                desired.Add(row);
            }
            bool same = ReferenceEquals(displayedSession, session) && visibleRows.Select(r => r.Id).SequenceEqual(desired.Select(r => r.Id));
            displayedSession = session; rows.Clear(); foreach (var row in desired) rows.Add(row.Id, row);
            if (!same)
            {
                var wanted = desired.Select(r => r.Id).ToHashSet();
                for (int i = visibleRows.Count - 1; i >= 0; i--) if (!wanted.Contains(visibleRows[i].Id)) visibleRows.RemoveAt(i);
                var existing = visibleRows.Select(r => r.Id).ToHashSet();
                for (int i = 0; i < desired.Count; i++)
                {
                    if (i < visibleRows.Count && ReferenceEquals(visibleRows[i], desired[i])) continue;
                    int old = existing.Contains(desired[i].Id) ? visibleRows.IndexOf(desired[i]) : -1;
                    if (old >= 0) visibleRows.Move(old, i); else visibleRows.Insert(i, desired[i]);
                }
            }
            SyncSelection(session);
            RefreshStatistics(session);
            empty.Text = L.Format("Layers.EmptyScope", editContextName is not null || session.EditScopeId is not null ? breadcrumbsScopeName(session) : L.Get("RootCanvas." + session.ActiveRootCanvas));
            empty.Visibility = desired.Count == 0 ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
            RefreshBreadcrumbs(session);
            if (scopeChanged) { if (visibleRows.Count > 0) layers.ScrollIntoView(visibleRows[0]); breadcrumbViewport.ScrollToLeftEnd(); }
        }
        finally { refreshing = false; }
    }

    private void SyncSelection(EditorSession session)
    {
        foreach (var row in layers.SelectedItems.Cast<LayerTreeRow>().Where(r => !session.Selection.Contains(r.Id) || !rows.ContainsKey(r.Id)).ToArray()) layers.SelectedItems.Remove(row);
        var selected = layers.SelectedItems.Cast<LayerTreeRow>().Select(r => r.Id).ToHashSet();
        foreach (var row in visibleRows) if (session.Selection.Contains(row.Id) && !selected.Contains(row.Id)) layers.SelectedItems.Add(row);
        RefreshLocateSelection(session);
    }

    private void RefreshStatistics(EditorSession session)
    {
        // DTO collection counts are O(1); a selection change must not traverse the scene.
        var document = session.Document;
        string text = L.Format("Layers.Statistics", document.Elements.Count + document.Groups.Count + document.Instances.Count,
            visibleRows.Count, session.Selection.Count);
        if (SelectionOutsideList(session) > 0)
            text = L.Format("Layers.CrossLevelStatistics", document.Elements.Count + document.Groups.Count + document.Instances.Count,
                visibleRows.Count, SelectionScopeText(session));
        if (statistics.Text != text) statistics.Text = text;
        statistics.ToolTip = L.Get("Layers.StatisticsHint");
        Automation.AutomationProperties.SetName(statistics, text);
    }

    private int SelectionOutsideList(EditorSession session) => session.Selection.Count(id => !rows.ContainsKey(id));
    private string SelectionScopeText(EditorSession session)
    {
        int outside = SelectionOutsideList(session);
        return L.Format("Layers.SelectionScope", session.Selection.Count, session.Selection.Count - outside, outside);
    }

    private string breadcrumbsScopeName(EditorSession session) => session.EditScopePath.LastOrDefault() is { } group ? GroupName(group) : editContextName ?? L.Get("Layers.RootScope");
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
        if (!canMutate() || getSession().IsTransactionActive || !rows.TryGetValue(id, out var row) || row.ParentId != getSession().EditScopeId) return;
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
        ChangeScope(parent);
        if (session.EditScopeId != current && FindNode(current) is not null) SelectNode(current);
    }
    internal OverlayContextMenu CreateContextMenu(Guid id)
    {
        var menu = new OverlayContextMenu();
        var scopeHint = new Controls.TextBlock { FontSize = 11, Margin = new(10, 5, 10, 5), ToolTip = L.Get("Layers.BatchScopeHint") };
        scopeHint.SetResourceReference(Controls.TextBlock.ForegroundProperty, "CanvasMuted");
        var scopeItem = new Controls.MenuItem { Header = scopeHint, Icon = IconPath("M2,4 L8,1 L14,4 L8,7 Z M2,8 L8,11 L14,8 M2,12 L8,15 L14,12"),
            IsEnabled = false, IsHitTestVisible = false, Visibility = Wpf.Visibility.Collapsed };
        menu.Items.Add(scopeItem);
        Controls.MenuItem Add(string key, string icon, string shortcut, Action action)
        {
            var item = new Controls.MenuItem { Header = L.Get(key), Icon = IconPath(icon), InputGestureText = shortcut, Tag = key };
            item.Click += (_, _) => action(); menu.Items.Add(item); return item;
        }
        Controls.MenuItem AddCommand(string key, string icon, string commandId, Action action)
        {
            var item = Add(key, icon, EditorCommandCatalog.Hint(commandId), action);
            menu.Opened += (_, _) => item.InputGestureText = EditorCommandCatalog.Hint(commandId);
            return item;
        }
        foreach (var (key, visible, value) in new[] { ("Layers.HideSelected", true, false), ("Layers.ShowSelected", true, true), ("Layers.LockSelected", false, true), ("Layers.UnlockSelected", false, false) })
        {
            var flag = Add(key, visible ? Eye : Lock, "", () =>
            {
                LayerFlagResult result = new(0,0,0);
                if (RunLayerCommand(s => result = s.SetNodesFlagDetailed(s.Selection, visible, value), flags: true))
                    status.Text = L.Format("LayerExperience.FlagResult", result.Changed, result.Unchanged, result.Skipped);
            });
            menu.Opened += (_, _) => flag.IsEnabled = CanRunSelectionCommand(key);
        }
        Add("Layers.MoveTo", Folder, "", ShowMoveDialog);
        var enter = Add("Layers.EnterGroup", Folder, "Enter", () => EnterGroup(id));
        var rename = AddCommand("Layers.Rename", "M2,11 V14 H5 L13,6 L10,3 Z", "Selection.Rename", () => BeginRename(id));
        var convertEmoji = Add("Emoji.ConvertText", LayerTypeIcon.Geometry(ElementKind.Text), "", () => ConvertEmojiToText(id));
        var collect = Add("Layers.CollectGroup", "M2,3 H14 M4,7 H12 M6,11 H10", "", () => RunLayerCommand(s => s.CollectLegacyGroup(id)));
        var editPrefab = Add("Prefab.EditDefinition", Instance, "Enter", () => EnterGroup(id));
        var detachPrefab = Add("Prefab.Detach", Folder, "", () => DetachPrefabRequested?.Invoke(id));
        var createPrefab = Add("Prefab.CreateFromSelection", Instance, "", () => CreatePrefabRequested?.Invoke());
        var exportPrefab = Add("Prefab.Export", "M8,10 V1 M4,5 L8,1 L12,5 M2,9 V15 H14 V9", "", () =>
        {
            if (new SceneGraph(getSession().Document).FindInstance(id) is { } instance) ExportPrefabRequested?.Invoke(instance.DefinitionId);
        });
        menu.Items.Add(new Controls.Separator());
        AddCommand("Inspector.Duplicate", "M1,1 H10 V10 M5,5 H14 V14 H5 Z", "Edit.Duplicate", () => RunLayerCommand(s => s.DuplicateSelection(), operationKind: LayerOperation.Duplicate));
        AddCommand("Inspector.Delete", "M3,4 H13 M5,4 V14 H11 V4 M6,1 H10", "Edit.Delete", () => RunLayerCommand(s => s.DeleteSelection(), operationKind: LayerOperation.Delete));
        menu.Items.Add(new Controls.Separator());
        Add("Inspector.BringToFront", "M3,9 L8,4 L13,9 M8,4 V14 M2,1 H14", "", () => RunLayerCommand(s => s.ReorderSelection(LayerOrder.Front)));
        Add("Inspector.SendToBack", "M3,7 L8,12 L13,7 M8,2 V12 M2,15 H14", "", () => RunLayerCommand(s => s.ReorderSelection(LayerOrder.Back)));
        Add("Layers.MoveOut", "M3,8 H13 M7,3 L2,8 L7,13", "", () => MoveOut(id));
        menu.Items.Add(new Controls.Separator());
        AddCommand("Layers.Group", "M1,1 H8 V8 H1 Z M6,6 H14 V14 H6 Z", "Edit.Group", () => RunLayerCommand(s => s.GroupSelection(), operationKind: LayerOperation.Group));
        AddCommand("Layers.Ungroup", "M1,1 H6 V6 H1 Z M9,9 H14 V14 H9 Z", "Edit.Ungroup", () => RunLayerCommand(s => s.UngroupSelection(), operationKind: LayerOperation.Ungroup));
        menu.Opened += (_, _) =>
        {
            var session = getSession();
            if (!session.Selection.Contains(id) && canMutate() && !session.IsTransactionActive) session.SetSelection([id]);
            scopeHint.Text = SelectionScopeText(session);
            scopeItem.Visibility = SelectionOutsideList(session) > 0 ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
            var row = FindNode(id); bool allowed = canMutate() && !session.IsTransactionActive && row is not null;
            foreach (var item in menu.Items.OfType<Controls.MenuItem>().Where(item => item.Tag is string)) item.IsEnabled = allowed && CanRunSelectionCommand(item.Tag as string);
            rename.IsEnabled = allowed && CanWrite && !row!.AncestorLocked;
            bool singleEmoji = row?.Kind == ElementKind.Emoji && session.Selection.SetEquals([id]);
            convertEmoji.Visibility = singleEmoji ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
            convertEmoji.IsEnabled = singleEmoji && allowed && CanWrite && !row!.EffectiveLocked;
            enter.Visibility = row?.IsGroup == true ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed; enter.IsEnabled = allowed && session.CanBrowseNode(id);
            collect.Visibility = row?.IsLegacy == true ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed; collect.ToolTip = L.Get("Layers.CollectHint"); collect.IsEnabled = allowed && CanWrite && !row!.EffectiveLocked;
            editPrefab.Visibility = detachPrefab.Visibility = exportPrefab.Visibility = row?.IsInstance == true ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
            editPrefab.IsEnabled = allowed && session.CanBrowseNode(id) && EditPrefabRequested is not null; detachPrefab.IsEnabled = allowed && CanWrite && !row!.EffectiveLocked && DetachPrefabRequested is not null; exportPrefab.IsEnabled = allowed && ExportPrefabRequested is not null;
            createPrefab.IsEnabled &= CreatePrefabRequested is not null;
            menu.Items.OfType<Controls.MenuItem>().Single(i => Equals(i.Tag, "Layers.MoveOut")).IsEnabled = allowed && CanWrite && session.EditScopeId is not null && session.Selection.Any(node => !session.IsNodeLocked(node));
        };
        return menu;
    }
    private void ConvertEmojiToText(Guid id)
    {
        var session = getSession();
        if (!ReferenceEquals(session,displayedSession) || !CanWrite || session.IsTransactionActive || !session.Selection.SetEquals([id])) return;
        ConvertEmojiToText(session,id);
    }
    internal static bool ConvertEmojiToText(EditorSession session,Guid id)
    {
        var graph=SceneGraph.Read(session.Document);
        if(graph.IsLocked(id) || graph.FindElement(id) is not { Kind:ElementKind.Emoji } element) return false;
        using var renderer=new FloatCanvas.Rendering.SceneRenderer();
        session.Execute(L.Get("Emoji.ConvertText"),_=>
        {
            element.Kind=ElementKind.Text;
            element.Style.FontFamily=FloatCanvas.Rendering.EmojiFontSources.SystemFamilyName;
            element.Style.PrimaryColor|=0xff000000;
            element.TextAutoWidth=false;element.TextFrameFixed=true;
            TextFrameLayout.Fit(element,element.Height,renderer);
        });
        return true;
    }
    private void MoveOut(Guid id)
    {
        RunLayerCommand(session =>
        {
            var graph = new SceneGraph(session.Document); var parent = graph.ParentId(id);
            if (parent is not null) session.MoveNodes(session.Selection, graph.ParentId(parent.Value));
        }, operationKind: LayerOperation.Move);
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
            session.SetNodesFlag([id], visibility, value);
        }
        catch (Exception ex) { status.Text = ex.Message; }
        Refresh();
    }
    private bool RunLayerCommand(Action<EditorSession> operation, bool flags = false, LayerOperation operationKind = LayerOperation.Reorder)
    {
        try
        {
            var session = getSession();
            if (!CanWrite || session.IsTransactionActive || !ReferenceEquals(session, displayedSession)) throw new InvalidOperationException(L.Get(session.IsReadOnly ? "GroupCanvas.ReadOnly" : "Inspector.BusyLayersRetained"));
            var before = new SceneGraph(session.Document);
            if (session.Selection.Any(id => before.FindElement(id) is null && before.FindGroup(id) is null && before.FindInstance(id) is null))
                throw new InvalidOperationException(L.Get("Layers.StaleMove"));
            if (session.Selection.Count == 0) return false;
            CommitRename();
            var analysis = flags ? null : session.AnalyzeLayerOperation(operationKind);
            if (analysis?.BlockReason is { } blocked) throw new InvalidOperationException(L.Get(blocked));
            long version = session.ContentVersion;
            operation(session); Refresh();
            status.Text = flags ? "" : L.Format("LayerExperience.FlagResult", session.ContentVersion == version ? 0 : analysis!.Eligible.Count,
                analysis!.Unchanged.Count + (session.ContentVersion == version ? analysis.Eligible.Count : 0), analysis.Skipped.Count);
            return true;
        }
        catch (Exception ex) { status.Text = ex.Message; return false; }
    }

    private void OnKeyDown(object sender, Input.KeyEventArgs e)
    {
        var key = e.Key == Input.Key.System ? e.SystemKey : e.Key; var modifiers = Input.Keyboard.Modifiers;
        var source = e.OriginalSource as Wpf.DependencyObject;
        if (e.Handled || (modifiers & Input.ModifierKeys.Windows) != 0 || EditorShortcutPolicy.OwnsMenu(source)) return;
        if (key == Input.Key.Escape && !renameComposing && !AppInputPolicy.IsRepeat(e) && AppWindowChrome.TryCloseHelp(this)) { e.Handled = true; return; }
        if (IsRenaming)
        {
            int projectKey = Input.KeyInterop.VirtualKeyFromKey(key);
            if (!renameComposing && EditorShortcutPolicy.CanRouteProjectCommandFromText(projectKey, (FloatCanvas.Windows.OverlayModifiers)modifiers))
            { e.Handled = true; if (!AppInputPolicy.IsRepeat(e) && canMutate()) { CommitRename(); CommandKey?.Invoke(projectKey); } }
            return;
        }
        var navigationSource = source ?? Input.Keyboard.FocusedElement as Wpf.DependencyObject;
        bool localInput = EditorShortcutPolicy.OwnsLocalEditingKeys(navigationSource);
        bool listInput = navigationSource is null || IsListSource(navigationSource);
        if (modifiers == Input.ModifierKeys.None && key is Input.Key.Enter or Input.Key.Left or Input.Key.Right or Input.Key.Back &&
            (localInput || EditorShortcutPolicy.OwnsNumericKey(Input.KeyInterop.VirtualKeyFromKey(key), modifiers, navigationSource))) return;
        int command = Input.KeyInterop.VirtualKeyFromKey(key);
        var commandId = EditorCommandCatalog.Resolve(command, (FloatCanvas.Windows.OverlayModifiers)modifiers)?.Id;
        if (listInput && commandId is ("Selection.Rename" or "Edit.Delete") &&
            EditorShortcutPolicy.ShouldRoute(command, modifiers, null, localInput: localInput))
        {
            e.Handled = true;
            if (!AppInputPolicy.IsRepeat(e))
            {
                if (commandId == "Edit.Delete") RouteCommandKey(command, modifiers);
                else if (layers.SelectedItems.Count == 1 && layers.SelectedItem is LayerTreeRow rename) BeginRename(rename.Id);
            }
            return;
        }
        if (listInput && modifiers == Input.ModifierKeys.None && layers.SelectedItem is LayerTreeRow selected)
        {
            if (key == Input.Key.Enter && (selected.IsGroup || selected.IsInstance)) { e.Handled = true; if (!AppInputPolicy.IsRepeat(e)) EnterGroup(selected.Id); return; }
            if (key == Input.Key.Right && (selected.IsGroup || selected.IsInstance))
            { e.Handled = true; if (!AppInputPolicy.IsRepeat(e)) EnterGroup(selected.Id); return; }
        }
        if (modifiers == Input.ModifierKeys.None && key is Input.Key.Left or Input.Key.Back && (navigationSource is null || IsListSource(navigationSource)))
        { e.Handled = true; if (!AppInputPolicy.IsRepeat(e)) LeaveGroup(); return; }
        if (key == Input.Key.Escape)
        {
            e.Handled = true;
            if (!AppInputPolicy.IsRepeat(e) && !AppWindowChrome.TryCloseHelp(this))
            {
                if (dragIds is not null) CancelDrag();
                else { SessionWindowCoordinator.Hide(this); Dismissed?.Invoke(); }
            }
            return;
        }
        if (!EditorShortcutPolicy.ShouldRoute(command, modifiers, source, preserveListNavigation: listInput, localInput: localInput)) return;
        e.Handled = true;
        if (!AppInputPolicy.IsRepeat(e) || EditorShortcutPolicy.AllowsRepeat(command, (FloatCanvas.Windows.OverlayModifiers)modifiers)) RouteCommandKey(command, modifiers);
    }
    private bool IsListSource(Wpf.DependencyObject? source)
    {
        if (!EditorShortcutPolicy.OwnsList(source)) return false;
        while (source is not null)
        {
            if (ReferenceEquals(source, layers)) return true;
            source = source is Media.Visual or Media.Media3D.Visual3D ? Media.VisualTreeHelper.GetParent(source) ?? Wpf.LogicalTreeHelper.GetParent(source) : Wpf.LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    private bool CanRunSelectionCommand(string? command)
    {
        var session = getSession();
        if (!CanWrite || session.IsTransactionActive) return false;
        var operation = command switch
        {
            "Inspector.Duplicate" => LayerOperation.Duplicate, "Layers.Group" => LayerOperation.Group,
            "Layers.Ungroup" => LayerOperation.Ungroup, "Layers.HideSelected" => LayerOperation.Hide,
            "Layers.ShowSelected" => LayerOperation.Show, "Layers.LockSelected" => LayerOperation.Lock,
            "Layers.UnlockSelected" => LayerOperation.Unlock, "Layers.MoveTo" => LayerOperation.Move,
            "Inspector.Delete" => LayerOperation.Delete, _ => LayerOperation.Reorder
        };
        return session.AnalyzeLayerOperation(operation).CanExecute;
    }
    private bool RouteCommandKey(int key, Input.ModifierKeys modifiers)
    {
        if (IsRenaming || !EditorShortcutPolicy.ShouldRoute(key,modifiers,null)) return false;
        string? command = EditorCommandCatalog.Resolve(key,(FloatCanvas.Windows.OverlayModifiers)modifiers)?.Id switch
        { "Edit.Delete"=>"Inspector.Delete", "Edit.Duplicate"=>"Inspector.Duplicate", "Edit.Group"=>"Layers.Group", "Edit.Ungroup"=>"Layers.Ungroup", _=>null };
        if (command is not null && !CanRunSelectionCommand(command)) return true;
        if (canMutate()) CommandKey?.Invoke(key); return true;
    }
    private bool CanRouteCommandKey(int key, bool control) => !IsRenaming && EditorShortcutPolicy.ShouldRoute(key,
        control ? Input.ModifierKeys.Control : Input.ModifierKeys.None, null);
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
            session.RenameLayer(id, name); status.Text = "";
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
