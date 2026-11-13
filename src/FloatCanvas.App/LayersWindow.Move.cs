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
        var selected = session.Selection.ToArray(); SceneGraph graph; LayerOperationAnalysis analysis; LayerMoveTargets model;
        using (SceneGraph.BeginReadScope())
        {
            graph = SceneGraph.Read(document);
            analysis = session.AnalyzeLayerOperation(LayerOperation.Move, selected);
            model = new LayerMoveTargets(session, analysis, editContextName is not null);
        }
        if (analysis.BlockReason is { } blocked) { status.Text = L.Get(blocked); return; }
        var moving = analysis.Eligible.ToArray();
        if (moving.Length == 0) return;
        var dialog = new Window { Title = L.Get("Layers.MoveTo"), Width = 420, Height = 480, MinWidth = 300, MinHeight = 300, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var search = new TextBox { ToolTip = L.Get("Layers.SearchTarget"), Margin = new(0, 0, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(search, L.Get("Layers.SearchTarget"));
        var targets = model.CreateList();
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
        var apply = new Button { Content = L.Get("Layers.MoveTo"), IsEnabled = false, MinHeight = 30, IsDefault = true };
        var body = new DockPanel { Margin = new(12) };
        var searchLabel = new TextBlock { Text = L.Get("Layers.SearchTarget"), Margin = new(0,0,0,4) };
        DockPanel.SetDock(searchLabel, Dock.Top); body.Children.Add(searchLabel);
        DockPanel.SetDock(search, Dock.Top); body.Children.Add(search);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var refreshTargets = new Button { Content = L.Get("LayerExperience.RefreshTargets"), MinHeight = 30, Margin = new(0,0,8,0), Visibility = Visibility.Collapsed };
        actions.Children.Add(refreshTargets);
        var cancel = new Button { Content = L.Get("Dialog.Cancel"), IsCancel = true, MinHeight = 30, Margin = new(0,0,8,0) };
        actions.Children.Add(cancel); actions.Children.Add(apply);
        DockPanel.SetDock(actions, Dock.Bottom); body.Children.Add(actions);
        DockPanel.SetDock(notice, Dock.Bottom); body.Children.Add(notice);
        var targetArea = new Grid(); targetArea.Children.Add(targets);
        var noMatches = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
        noMatches.Children.Add(new TextBlock { Text = L.Get("Layers.NoTargetMatches"), TextWrapping = TextWrapping.Wrap, Margin = new(8), MaxWidth = 250 });
        var clearSearch = new Button { Content = L.Get("Layers.ClearTargetSearch"), MinHeight = PropertyPanelLayout.ControlHeight, Margin = new(8) };
        clearSearch.Click += (_, _) => search.Clear(); noMatches.Children.Add(clearSearch); targetArea.Children.Add(noMatches); body.Children.Add(targetArea);
        dialog.Content = body; AppWindowChrome.ApplyDialog(dialog);
        bool stale = false, searching = false;
        void UpdateTarget()
        {
            apply.IsEnabled = !stale && !searching && targets.SelectedItem is LayerMoveTargets.Row { Reason: null };
            if (stale) return;
            notice.Text = targets.SelectedItem is LayerMoveTargets.Row row
                ? row.Reason ?? ((row.Root != session.ActiveRootCanvas ? L.Get("Layers.CrossCanvasNotice") : "") +
                    (moving.Any(id => graph.FindGroup(id)?.OrderMode == GroupOrderMode.LegacyPassThrough) ? "\n" + L.Get("Layers.CollectHint") : "")) : "";
        }
        void Populate()
        {
            var selectedTarget = targets.SelectedItem;
            model.Filter(search.Text);
            if (selectedTarget is LayerMoveTargets.Row row && model.Visible.Contains(row)) targets.SelectedItem = row;
            noMatches.Visibility = model.Visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateTarget();
        }
        var searchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); searching = false; Populate(); };
        search.TextChanged += (_, _) => { searching = true; apply.IsEnabled = false; searchTimer.Stop(); searchTimer.Start(); };
        dialog.Closed += (_, _) => searchTimer.Stop();
        targets.SelectionChanged += (_, _) => UpdateTarget();
        refreshTargets.Click += (_, _) =>
        {
            if (!CanWrite || !ReferenceEquals(session, getSession()) || !ReferenceEquals(document, session.Document) ||
                !session.Selection.SetEquals(selected) || session.IsTransactionActive)
            { notice.Text = L.Get("Layers.MoveSourceChanged"); return; }
            searchTimer.Stop(); searching = false;
            var previous = targets.SelectedItem as LayerMoveTargets.Row;
            ScrollViewer? Scroll(DependencyObject node)
            {
                if (node is ScrollViewer viewer) return viewer;
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                    if (Scroll(System.Windows.Media.VisualTreeHelper.GetChild(node, i)) is { } found) return found;
                return null;
            }
            var scroll = Scroll(targets); double offset = scroll?.VerticalOffset ?? 0;
            var top = model.Visible.ElementAtOrDefault((int)offset);
            using (SceneGraph.BeginReadScope())
            {
                graph = SceneGraph.Read(document); analysis = session.AnalyzeLayerOperation(LayerOperation.Move, selected);
                model.Reload(session, analysis, editContextName is not null);
            }
            moving = analysis.Eligible.ToArray(); revision = session.SceneRevision; scope = session.EditScopeVersion;
            stale = analysis.BlockReason is not null || moving.Length == 0;
            Populate();
            targets.SelectedItem = previous is null ? null : model.Visible.FirstOrDefault(r => r.Id == previous.Id && r.Root == previous.Root);
            UpdateTarget();
            if (stale) notice.Text = L.Get(analysis.BlockReason ?? "Layers.MoveNothingEligible");
            else if (previous is not null && targets.SelectedItem is null) notice.Text = L.Get("Layers.MoveTargetUnavailable");
            refreshTargets.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
            targets.UpdateLayout();
            var newTop = top is null ? null : model.Visible.FirstOrDefault(r => r.Id == top.Id && r.Root == top.Root);
            scroll?.ScrollToVerticalOffset(newTop is null ? offset : model.Visible.IndexOf(newTop));
        };
        apply.Click += (_, _) =>
        {
            if (targets.SelectedItem is not LayerMoveTargets.Row target) return;
            if (target.Reason is not null) return;
            if (!CanWrite || !ReferenceEquals(session, getSession()) || !ReferenceEquals(document, session.Document) || session.SceneRevision != revision || session.EditScopeVersion != scope || !session.Selection.SetEquals(selected) || session.IsTransactionActive)
            { stale = true; notice.Text = L.Get("Layers.StaleMove"); refreshTargets.Visibility = Visibility.Visible; apply.IsEnabled = false; return; }
            try
            {
                var result = session.CommitLayerMove(session.PrepareLayerMove(selected, target.Root, target.Id));
                Refresh(); status.Text = L.Format("Layers.Moved", result.Moved.Count, target.Path, result.Skipped.Count);
                if (result.Moved.Count == 0) { dialog.Close(); return; }
                long generation = session.DocumentGeneration, history = session.HistoryNavigationVersion;
                var retained = result.Moved.ToHashSet();
                moveTargetValid = () =>
                {
                    if (!ReferenceEquals(session, getSession()) || session.DocumentGeneration != generation || session.HistoryNavigationVersion != history) return false;
                    var current = new SceneGraph(session.Document);
                    if (target.Id is { } parent && (current.FindGroup(parent) is null || SceneCanvasRoots.Resolve(session.Document, parent, current) != target.Root)) return false;
                    retained.RemoveWhere(id => current.FindElement(id) is null && current.FindGroup(id) is null && current.FindInstance(id) is null || current.ParentId(id) != target.Id || SceneCanvasRoots.Resolve(session.Document, id, current) != target.Root);
                    return retained.Count > 0;
                };
                viewMoveTarget.Visibility = Visibility.Visible;
                viewMoveTarget.Tag = (Action)(() =>
                {
                    if (moveTargetValid?.Invoke() != true) { viewMoveTarget.Visibility = Visibility.Collapsed; return; }
                    if (!canMutate() || session.IsTransactionActive) { status.Text = L.Get("Inspector.BusyLayersRetained"); return; }
                    try
                    {
                        if (target.Root != session.ActiveRootCanvas) { if (RootCanvasRequested is { } requested) requested(target.Root); else session.SetRootCanvas(target.Root); }
                        if (session.ActiveRootCanvas != target.Root) return;
                        session.SetEditScope(target.Id); session.SetSelection(retained); Refresh(); viewMoveTarget.Visibility = Visibility.Collapsed;
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
