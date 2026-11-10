using FloatCanvas.Core;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class LayersWindow
{
    private readonly ScopeNavigationHistory navigationHistory = new();
    private EditingScopeDescription? navigationDescription;
    private EditorSession? navigationSession;
    private long navigationVersion, navigationGeneration;
    private ScopeNavigationPath? navigationPath;
    private string? navigationViewKey;
    private object? navigationContextKey;
    internal void CompletePendingNavigation() { CommitRename(); CancelDrag(); }
    internal void SetNavigation(EditingScopeDescription description)
    {
        navigationDescription = description; navigationSession = getSession();
        navigationVersion = navigationSession.EditScopeVersion; navigationGeneration = navigationSession.DocumentGeneration;
        RefreshBreadcrumbs(navigationSession);
    }
    private void RefreshBreadcrumbs(EditorSession session)
    {
        bool external = ReferenceEquals(session, navigationSession) && session.EditScopeVersion == navigationVersion && session.DocumentGeneration == navigationGeneration;
        var description = external ? navigationDescription : null;
        if (description?.Navigation is { } provided) navigationPath = provided;
        else
        {
            var entries = new List<ScopeNavigationEntry>();
            var generation = session.DocumentGeneration; var root = session.ActiveRootCanvas;
            bool Valid() => ReferenceEquals(session, getSession()) && session.DocumentGeneration == generation && session.ActiveRootCanvas == root;
            void Add(string name, Guid? id, Guid? expectedParent)
            {
                bool Exists() => Valid() && (id is null || SceneGraph.Read(session.Document).FindGroup(id.Value) is { } group && group.ParentId == expectedParent && SceneCanvasRoots.Resolve(session.Document,id.Value, SceneGraph.Read(session.Document)) == root);
                entries.Add(new(id?.ToString() ?? "root", name, id is null ? editContextName is null ? ScopeNavigation.RootGlyphFor(root) : Instance : Folder,
                    () => { if (Exists()) ChangeScope(id); }, id, true, Exists,
                    () => canMutate() && !session.IsTransactionActive,
                    () => id is { } node && SceneGraph.Read(session.Document).FindGroup(node) is { } group ? GroupName(group) : name));
            }
            Add(editContextName ?? L.Get("RootCanvas." + root), null, null);
            foreach (var group in session.EditScopePath) Add(GroupName(group), group.Id, group.ParentId);
            navigationPath = navigationHistory.Update((session, generation, root, editContextName ?? ""), entries);
        }
        var path = navigationPath;
        string mode = NavigationMode(description);
        bool canToggle = description?.CanToggleMode?.Invoke() ?? groupCanvasChoice.IsEnabled;
        string key = ScopeNavigation.ViewKey(path,mode,description?.State ?? "",canToggle);
        object contextKey = description?.ContextKey ?? (session, session.DocumentGeneration, session.EditScopeVersion);
        if (navigationViewKey == key && Equals(navigationContextKey,contextKey)) return;
        foreach(var button in breadcrumbs.Children.OfType<Button>()) if(button.ContextMenu is { } old) OverlayContextMenu.SetOpen(old,false);
        navigationViewKey = key; navigationContextKey = contextKey;
        breadcrumbs.Children.Clear();
        var overflow = NavigationButton(); overflow.Content = new TextBlock { Text = "…", FontSize = 14 }; overflow.Tag = "overflow"; overflow.Width = 27;
        overflow.ContextMenu = CreateNavigationMenu(path, description);
        overflow.Click += (_, _) => OpenNavigationMenu(overflow); breadcrumbs.Children.Add(overflow);
        for (int i = 0; i < path.Entries.Count; i++)
        {
            var entry = path.Entries[i]; int index = i;
            bool current = i == path.CurrentIndex, history = i > path.CurrentIndex;
            if (i > 0) breadcrumbs.Children.Add(new TextBlock { Text = "›", Margin = new(1,0,1,0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
            var label = new TextBlock { Text = entry.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                FontStyle = history ? FontStyles.Italic : FontStyles.Normal };
            var button = NavigationButton();
            if (!current) button.Content = label;
            button.Tag = entry.Local ? new ScopeTarget(entry.LocalScope) : null;
            button.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
            button.SetResourceReference(Control.ForegroundProperty, current ? "CanvasAccent" : history ? "CanvasMuted" : "CanvasText");
            button.ToolTip = entry.DisplayName + "\n" + L.Get(current ? "ScopeNavigation.Current" : history ? "ScopeNavigation.History" : "Layers.ScopeHint");
            button.IsEnabled = current || entry.Available;
            AutomationProperties.SetName(button, entry.DisplayName);
            AutomationProperties.SetItemStatus(button,L.Get(current ? "ScopeNavigation.Current" : history ? "ScopeNavigation.History" : "ScopeNavigation.Ancestor"));
            if (current)
            {
                var content = new DockPanel { LastChildFill = true };
                var arrow = CommandGlyphs.Create("M4,6 L8,10 L12,6"); arrow.Width = arrow.Height = 10; arrow.Margin = new(4,0,0,0);
                DockPanel.SetDock(arrow,Dock.Right); content.Children.Add(arrow); content.Children.Add(label); button.Content = content;
                button.ContextMenu = CreateNavigationMenu(path,description); button.Click += (_, _) => OpenNavigationMenu(button);
            }
            else button.Click += (_, _) => { if (index != path.CurrentIndex && entry.Available) { CompletePendingNavigation(); entry.Navigate(); } };
            breadcrumbs.Children.Add(button);
        }
        breadcrumbViewport.ToolTip = string.Join(" › ",path.Entries.Take(path.CurrentIndex+1).Select(e=>e.DisplayName));
        SizeBreadcrumbs();
    }
    private static Button NavigationButton()
    {
        var button = new Button { Height = 32, MinHeight = 0, MinWidth = 0, Padding = new(5,0,5,0), VerticalContentAlignment = VerticalAlignment.Center };
        button.SetResourceReference(Control.StyleProperty,"CanvasToolbarInlineButton"); return button;
    }
    private OverlayContextMenu CreateNavigationMenu(ScopeNavigationPath path, EditingScopeDescription? description) => ScopeNavigation.Menu(path,
        NavigationMode(description),
        description?.ToggleMode ?? (() => InvokeGroupAction(context => context.Mode == GroupCanvasMode.Temporary ? context.Root : context.Temporary)),
        description?.CanToggleMode ?? (() => groupCanvasChoice.IsEnabled));
    private string NavigationMode(EditingScopeDescription? description) => description?.Mode ??
        L.Get(groupContext?.Mode is GroupCanvasMode.Temporary ? "GroupCanvas.Temporary" : "GroupCanvas.RootProject");
    private static void OpenNavigationMenu(Button button)
    { if (button.ContextMenu is { } menu) { menu.PlacementTarget = button; OverlayContextMenu.SetOpen(menu,true); } }
    private void SizeBreadcrumbs()
    {
        if (navigationPath is not { } path) return;
        var buttons = breadcrumbs.Children.OfType<Button>().Where(b=>b.Tag is not string).ToArray();
        if (buttons.Length == 0) return;
        var overflow = breadcrumbs.Children.OfType<Button>().First(b=>Equals(b.Tag,"overflow"));
        double available = breadcrumbViewport.ActualWidth > 0 ? breadcrumbViewport.ActualWidth : Math.Max(100,Width-75);
        bool fold = available < buttons.Length*80;
        foreach (FrameworkElement element in breadcrumbs.Children) element.Visibility = Visibility.Collapsed;
        int first = fold ? Math.Max(0,path.CurrentIndex-1) : 0;
        int last = fold ? Math.Min(buttons.Length-1,path.CurrentIndex+1) : buttons.Length-1;
        overflow.Visibility = first > 0 || last < buttons.Length-1 ? Visibility.Visible : Visibility.Collapsed;
        double budget = Math.Max(0,available-(overflow.Visibility==Visibility.Visible?27:0)-(last-first)*10);
        double currentWidth = budget * (first == last ? 1 : last-first == 1 ? .62 : .5);
        double otherWidth = first == last ? 0 : (budget-currentWidth)/(last-first);
        for(int i=first;i<=last;i++)
        {
            var button=buttons[i]; button.MaxWidth=Math.Max(1,i==path.CurrentIndex?currentWidth:otherWidth); button.Visibility=Visibility.Visible;
            int index=breadcrumbs.Children.IndexOf(button);
            if(i>first && index>0) ((FrameworkElement)breadcrumbs.Children[index-1]).Visibility=Visibility.Visible;
        }
    }
}
