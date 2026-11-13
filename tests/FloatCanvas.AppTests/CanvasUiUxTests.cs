using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class CanvasUiUxTests
{
    internal static void Run(Action<string, bool> check)
    {
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string before = L.Preference;
        var existingWindows = application.Windows.OfType<Window>().ToHashSet();
        try
        {
            L.Initialize("en"); AppTheme.Apply("light");
            SelectionScope(check);
            MoveTargets(check);
            CalibrationRoles(check);
            CalibrationIssues(check);
        }
        finally
        {
            // Production Layers.Close intentionally hides its reusable panel.
            // Tests must release only their own HWNDs before the next fixture.
            foreach (var window in application.Windows.OfType<Window>().Where(w => !existingWindows.Contains(w)).ToArray())
            { window.Closing += (_, e) => e.Cancel = false; window.Close(); }
            L.Initialize(before);
        }
    }
    private static void SelectionScope(Action<string, bool> check)
    {
        var group = new CanvasGroup { Name = "Inside", OrderMode = GroupOrderMode.AtomicBlock };
        var nested = new CanvasElement { GroupId = group.Id, Width = 20, Height = 20 };
        var root = new CanvasElement { Width = 20, Height = 20 }; group.ChildOrder = [nested.Id];
        var session = new EditorSession(new() { Groups = [group], Elements = [nested, root], RootOrder = [group.Id, root.Id] });
        session.SetSelection([nested.Id, root.Id]);
        var window = new LayersWindow(() => session); window.PrepareForShow(); window.Show(); Pump();
        try
        {
            check("cross-level selection footer distinguishes visible and other-level selections",
                Descendants<TextBlock>(window).Any(t => t.Text.Contains("Selected 2") && t.Text.Contains("this level 1") && t.Text.Contains("other levels 1")));
            var menu = window.CreateContextMenu(root.Id); menu.PlacementTarget = window; menu.IsOpen = true; Pump();
            check("batch menu explains selection beyond the displayed level",
                Descendants<TextBlock>(menu).Any(t => t.Text.Contains("other levels 1")));
            menu.IsOpen = false;
        }
        finally { window.Close(); }
    }
    internal static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void CalibrationRoles(Action<string, bool> check)
    {
        var owner = new Window { Width = 100, Height = 100 }; owner.Show();
        var settings = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 800, 600)) };
        var draft = new CanvasGridCalibrationDraft { Columns = "4", Rows = "5" };
        var window = new CanvasGridCalibrationWindow(owner, settings, new(.1, .1, .4, .5), draft, _ => { }); window.Show(); Pump();
        try
        {
            var automatic = Descendants<Button>(window).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == L.Get("Canvas.AutomaticGridCandidate"));
            check("automatic grid estimate does not offer a redundant reset", !automatic.IsEnabled);
            var total = Descendants<TextBox>(window).Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == L.Get("Canvas.TotalColumns"));
            total.Text = "11"; Pump();
            check("manual grid override is explicitly labeled", automatic.IsEnabled && Descendants<TextBlock>(window).Any(t => t.Text == L.Get("Canvas.GridCandidateManual")));
            automatic.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check("restore automatic clears the override and restores computed counts", total.Text == "10" && draft.CandidateColumns is null && !automatic.IsEnabled);
        }
        finally { window.Close(); owner.Close(); }
    }
    private static void CalibrationIssues(Action<string, bool> check)
    {
        var owner = new Window { Width = 100, Height = 100 }; owner.Show();
        var issue = new CanvasCalibrationIssue(Guid.NewGuid(), "Tower", "Skyline › Tower", new(30, 50, 80, 120));
        var settings = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 800, 600)) };
        CanvasCalibrationIssue? marked = null;
        var window = new CanvasGridCalibrationWindow(owner, settings, new(.1, .1, .4, .5), new() { Columns = "4", Rows = "5" }, _ => { },
            (_, _) => Task.FromResult(new CanvasCalibrationCheck(1) { Issues = [issue] }), highlight: value => marked = value);
        window.Show(); Pump();
        try
        {
            var details = Descendants<Expander>(window).SingleOrDefault();
            check("unsafe calibration exposes inspectable affected objects", details?.IsVisible == true);
            details!.IsExpanded = true; Pump();
            var list = Descendants<ListBox>(details).Single(); list.SelectedIndex = 0;
            Descendants<Button>(details).Single(b => Equals(b.Content, L.Get("Canvas.MarkGridIssue"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("issue review marks original geometry without accepting the candidate", marked?.Id == issue.Id && window.Result is null && window.State == CanvasGridCalibrationWindow.CandidateState.Unsafe);
            details.IsExpanded = false;
            check("closing issue details clears the temporary highlight", marked is null);
        }
        finally { window.Close(); owner.Close(); }
    }
    private static void Until(Func<bool> ready)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && timer.Elapsed.TotalSeconds < 5) { Pump(); Thread.Sleep(5); }
        if (!ready()) throw new InvalidOperationException("Timed out waiting for target UI");
    }
    private static void MoveTargets(Action<string, bool> check)
    {
        var group = new CanvasGroup { Name = "Destination", OrderMode = GroupOrderMode.AtomicBlock };
        var child = new CanvasElement { GroupId = group.Id, Width = 20, Height = 20 };
        var moving = new CanvasElement { Width = 20, Height = 20 }; group.ChildOrder = [child.Id];
        var session = new EditorSession(new() { Groups = [group], Elements = [child, moving], RootOrder = [group.Id, moving.Id] });
        session.SetSelection([moving.Id]);
        var owner = new LayersWindow(() => session); owner.PrepareForShow(); owner.Show(); Pump();
        Exception? failure = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().Single(w => ReferenceEquals(w.Owner, owner));
            try
            {
                var search = Descendants<TextBox>(dialog).Single(); search.Text = "Destination";
                var list = Descendants<ListBox>(dialog).Single(); Until(() => list.Items.Count == 2);
                list.SelectedItem = list.Items.OfType<LayerMoveTargets.Row>().Single(r => r.Id == group.Id);
                session.RenameLayer(group.Id, "Destination renamed");
                Descendants<Button>(dialog).Single(b => Equals(b.Content, L.Get("Layers.MoveTo"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var refresh = Descendants<Button>(dialog).Single(b => Equals(b.Content, L.Get("LayerExperience.RefreshTargets")));
                check("stale destination asks for refresh", refresh.IsVisible);
                refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check("refresh targets preserves the original dialog and query", dialog.IsVisible && search.Text == "Destination");
                check("refresh retains a valid selected destination by identity", list.SelectedItem is LayerMoveTargets.Row row && row.Id == group.Id && row.Name == "Destination renamed");
                search.Text = "no matching destination"; Until(() => list.Items.Count == 0);
                check("empty target search provides a clear explanation", Descendants<TextBlock>(dialog).Any(t => t.IsVisible && t.Text == L.Get("Layers.NoTargetMatches")));
                Descendants<Button>(dialog).Single(b => Equals(b.Content, L.Get("Layers.ClearTargetSearch"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(() => list.Items.Count > 0); check("clear target search restores browsing", search.Text.Length == 0);
                list.SelectedItem = list.Items.OfType<LayerMoveTargets.Row>().Single(r => r.Id == group.Id);
                session.Execute("Remove destination", document => new SceneGraph(document).RemoveNodes([group.Id]));
                Descendants<Button>(dialog).Single(b => Equals(b.Content, L.Get("Layers.MoveTo"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check("refresh explains a deleted destination without moving anything", dialog.IsVisible && list.SelectedItem is null &&
                    session.Document.Elements.Any(e => e.Id == moving.Id && e.GroupId is null) &&
                    Descendants<TextBlock>(dialog).Any(t => t.Text == L.Get("Layers.MoveTargetUnavailable")));
            }
            catch (Exception error) { failure = error; }
            finally { dialog.Close(); }
        }));
        owner.CreateContextMenu(moving.Id).Items.OfType<MenuItem>().Single(m => Equals(m.Tag, "Layers.MoveTo")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        owner.Close(); if (failure is not null) throw failure;
    }
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
