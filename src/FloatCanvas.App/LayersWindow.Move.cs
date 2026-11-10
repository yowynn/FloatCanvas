using System.Windows;
using System.Windows.Controls;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class LayersWindow
{
    private void ShowMoveDialog()
    {
        CommitRename();
        var session = getSession();
        if (!CanWrite || session.IsTransactionActive || session.Selection.Count == 0) return;
        var document = session.Document; long revision = session.SceneRevision, scope = session.EditScopeVersion;
        var selected = session.Selection.ToArray(); var graph = new SceneGraph(document);
        var moving = selected.Where(id => !graph.IsLocked(id)).ToArray();
        if (moving.Length == 0) return;
        var dialog = new Window { Title = L.Get("Layers.MoveTo"), Width = 420, Height = 480, MinWidth = 300, MinHeight = 300, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var search = new TextBox { ToolTip = L.Get("Layers.SearchTarget"), Margin = new(0, 0, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(search, L.Get("Layers.SearchTarget"));
        var targets = new TreeView { BorderThickness = new(1) };
        targets.SetResourceReference(Control.BackgroundProperty, "CanvasSurface");
        targets.SetResourceReference(Control.ForegroundProperty, "CanvasText");
        targets.SetResourceReference(Control.BorderBrushProperty, "CanvasBorder"); var notice = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
        var apply = new Button { Content = L.Get("Layers.MoveTo"), IsEnabled = false, MinHeight = 30, IsDefault = true };
        var body = new DockPanel { Margin = new(12) };
        var searchLabel = new TextBlock { Text = L.Get("Layers.SearchTarget"), Margin = new(0,0,0,4) };
        DockPanel.SetDock(searchLabel, Dock.Top); body.Children.Add(searchLabel);
        DockPanel.SetDock(search, Dock.Top); body.Children.Add(search);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var refreshTargets = new Button { Content = L.Get("LayerExperience.RefreshTargets"), MinHeight = 30, Margin = new(0,0,8,0), Visibility = Visibility.Collapsed };
        refreshTargets.Click += (_, _) => { dialog.Close(); Dispatcher.BeginInvoke((Action)ShowMoveDialog); };
        actions.Children.Add(refreshTargets);
        var cancel = new Button { Content = L.Get("Dialog.Cancel"), IsCancel = true, MinHeight = 30, Margin = new(0,0,8,0) };
        actions.Children.Add(cancel); actions.Children.Add(apply);
        DockPanel.SetDock(actions, Dock.Bottom); body.Children.Add(actions);
        DockPanel.SetDock(notice, Dock.Bottom); body.Children.Add(notice); body.Children.Add(targets);
        dialog.Content = body; AppWindowChrome.ApplyDialog(dialog);
        var choices = new List<(RootCanvasKind Root, Guid? Parent, string Name, string? Reason)>();
        foreach (var root in new[] { RootCanvasKind.Foreground, RootCanvasKind.Main, RootCanvasKind.Background })
        {
            string name = L.Get("RootCanvas." + root);
            Add(root, null, name);
            if (root != session.ActiveRootCanvas) continue;
            foreach (var group in document.Groups.Where(g => SceneCanvasRoots.Resolve(document, g.Id, graph) == root))
            {
                var path = graph.AncestorIds(group.Id).Reverse().Select(id => GroupName(graph.FindGroup(id)!)).Append(GroupName(group));
                Add(root, group.Id, name + " › " + string.Join(" › ", path));
            }
        }
        void Add(RootCanvasKind root, Guid? parent, string name)
        {
            string? reason = root != session.ActiveRootCanvas && editContextName is not null ? L.Get("RootCanvas.ReturnUnavailable") : parent == session.EditScopeId && root == session.ActiveRootCanvas ? L.Get("Layers.CurrentTarget") :
                parent is { } id && (moving.Contains(id) || moving.Any(node => graph.IsAncestor(node, id))) ? L.Get("LayerExperience.CyclicTarget") : parent is { } locked && graph.IsLocked(locked) ? L.Get("LayerExperience.LockedTarget") : null;
            choices.Add((root, parent, name, reason));
        }
        var children = choices.Where(c => c.Parent is not null).GroupBy(c => graph.ParentId(c.Parent!.Value) ?? Guid.Empty).ToDictionary(g => g.Key, g => g.ToArray());
        var expanded = session.EditScopePath.Select(g => g.Id).ToHashSet();
        System.Windows.Controls.VirtualizingPanel.SetIsVirtualizing(targets, true);
        System.Windows.Controls.VirtualizingPanel.SetVirtualizationMode(targets, System.Windows.Controls.VirtualizationMode.Recycling);
        System.Windows.Controls.ScrollViewer.SetCanContentScroll(targets, true);
        void Populate()
        {
            var selectedTarget = (targets.SelectedItem as TreeViewItem)?.Tag;
            targets.Items.Clear(); apply.IsEnabled = false;
            bool filter = !string.IsNullOrWhiteSpace(search.Text);
            var matches = new HashSet<Guid>();
            if (filter) foreach (var choice in choices.Where(c => c.Parent is not null && c.Name.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)))
            { var id = choice.Parent!.Value; matches.Add(id); foreach (var ancestor in graph.AncestorIds(id)) matches.Add(ancestor); }
            TreeViewItem? Build((RootCanvasKind Root, Guid? Parent, string Name, string? Reason) choice)
            {
                if (filter && choice.Parent is { } match && !matches.Contains(match)) return null;
                var item = new TreeViewItem { Tag = choice, IsExpanded = filter || choice.Parent is null || expanded.Contains(choice.Parent.Value) };
                var label = choice.Parent is { } groupId ? GroupName(graph.FindGroup(groupId)!) : choice.Name;
                var header = new DockPanel(); var icon = ScopeNavigation.Icon(choice.Parent is null ? ScopeNavigation.RootGlyphFor(choice.Root) : Folder, 0); icon.Margin = new(0,0,5,0); DockPanel.SetDock(icon, Dock.Left); header.Children.Add(icon);
                header.Children.Add(new TextBlock { Text = search.Text.Length > 0 ? choice.Name : label, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = choice.Reason is null ? 1 : .55 }); item.Header = header;
                item.SetResourceReference(Control.ForegroundProperty, "CanvasText");
                System.Windows.Automation.AutomationProperties.SetItemStatus(item, choice.Reason ?? "");
                item.ToolTip = choice.Name + (choice.Reason is null ? "" : "\n" + choice.Reason);
                item.Expanded += (_, e) => { if (!filter && choice.Parent is { } id) expanded.Add(id); e.Handled = true; };
                item.Collapsed += (_, e) => { if (!filter && choice.Parent is { } id) expanded.Remove(id); e.Handled = true; };
                if (Equals(selectedTarget, choice)) item.IsSelected = true;
                if (choice.Root == session.ActiveRootCanvas && children.TryGetValue(choice.Parent ?? Guid.Empty, out var descendants))
                {
                    void Load() { item.Items.Clear(); foreach (var child in descendants) if (Build(child) is { } branch) item.Items.Add(branch); }
                    if (item.IsExpanded) Load();
                    else { var placeholder = new TreeViewItem(); item.Items.Add(placeholder); item.Expanded += (_,_) => { if (item.Items.Contains(placeholder)) Load(); }; }
                }
                return choice.Name.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase) || item.Items.Count > 0 ? item : null;
            }
            foreach (var choice in choices.Where(c => c.Parent is null))
                if (Build(choice) is { } item) targets.Items.Add(item);
        }
        var searchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); Populate(); };
        search.TextChanged += (_, _) => { apply.IsEnabled = false; searchTimer.Stop(); searchTimer.Start(); };
        dialog.Closed += (_, _) => searchTimer.Stop();
        targets.SelectedItemChanged += (_, _) =>
        {
            apply.IsEnabled = targets.SelectedItem is TreeViewItem { Tag: ValueTuple<RootCanvasKind, Guid?, string, string?> { Item4: null } };
            notice.Text = targets.SelectedItem is TreeViewItem { Tag: ValueTuple<RootCanvasKind, Guid?, string, string?> target }
                ? target.Item4 ?? ((target.Item1 != session.ActiveRootCanvas ? L.Get("Layers.CrossCanvasNotice") : "") +
                    (moving.Any(id => graph.FindGroup(id)?.OrderMode == GroupOrderMode.LegacyPassThrough) ? "\n" + L.Get("Layers.CollectHint") : "")) : "";
        };
        apply.Click += (_, _) =>
        {
            if (targets.SelectedItem is not TreeViewItem { Tag: ValueTuple<RootCanvasKind, Guid?, string, string?> target }) return;
            if (target.Item4 is not null) return;
            if (!CanWrite || !ReferenceEquals(session, getSession()) || !ReferenceEquals(document, session.Document) || session.SceneRevision != revision || session.EditScopeVersion != scope || !session.Selection.SetEquals(selected) || session.IsTransactionActive)
            { notice.Text = L.Get("Layers.StaleMove"); refreshTargets.Visibility = Visibility.Visible; apply.IsEnabled = false; return; }
            try
            {
                session.MoveLayerNodes(moving, target.Item1, target.Item2);
                session.SetSelection(selected.Except(moving));
                Refresh(); status.Text = L.Format("Layers.Moved", moving.Length, target.Item3, selected.Length - moving.Length);
                long generation = session.DocumentGeneration, history = session.HistoryNavigationVersion;
                var retained = moving.ToHashSet();
                moveTargetValid = () =>
                {
                    if (!ReferenceEquals(session, getSession()) || session.DocumentGeneration != generation || session.HistoryNavigationVersion != history) return false;
                    var current = new SceneGraph(session.Document);
                    if (target.Item2 is { } parent && (current.FindGroup(parent) is null || SceneCanvasRoots.Resolve(session.Document, parent, current) != target.Item1)) return false;
                    retained.RemoveWhere(id => current.FindElement(id) is null && current.FindGroup(id) is null && current.FindInstance(id) is null || current.ParentId(id) != target.Item2 || SceneCanvasRoots.Resolve(session.Document, id, current) != target.Item1);
                    return retained.Count > 0;
                };
                viewMoveTarget.Visibility = Visibility.Visible;
                viewMoveTarget.Tag = (Action)(() =>
                {
                    if (moveTargetValid?.Invoke() != true) { viewMoveTarget.Visibility = Visibility.Collapsed; return; }
                    if (!canMutate() || session.IsTransactionActive) { status.Text = L.Get("Inspector.BusyLayersRetained"); return; }
                    try
                    {
                        if (target.Item1 != session.ActiveRootCanvas) { if (RootCanvasRequested is { } requested) requested(target.Item1); else session.SetRootCanvas(target.Item1); }
                        if (session.ActiveRootCanvas != target.Item1) return;
                        session.SetEditScope(target.Item2); session.SetSelection(retained); Refresh(); viewMoveTarget.Visibility = Visibility.Collapsed;
                    }
                    catch (Exception error) { status.Text = error.Message; }
                });
                dialog.Close();
            }
            catch (Exception ex) { notice.Text = ex.Message; }
        };
        Populate(); SessionWindowCoordinator.ShowModal(dialog);
    }
    private Func<bool>? moveTargetValid;
    private readonly Button viewMoveTarget = new() { Content = L.Get("Layers.ViewTarget"), Visibility = Visibility.Collapsed, MinHeight = 28 };
}
