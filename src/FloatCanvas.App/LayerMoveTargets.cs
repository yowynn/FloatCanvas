using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Stable destination data. Filtering never creates WPF containers.</summary>
internal sealed class LayerMoveTargets
{
    internal sealed class Row : INotifyPropertyChanged
    {
        public required RootCanvasKind Root { get; init; }
        public Guid? Id { get; init; }
        public Row? Parent { get; init; }
        public required string Name { get; init; }
        public required string Path { get; init; }
        public string? Reason { get; init; }
        public List<Row> Children { get; } = [];
        public int Depth { get; init; }
        public bool Expanded { get; set; }
        public bool Filtering { get; set; }
        public bool Matches { get; set; }
        public bool DirectMatch { get; set; }
        public string Caption => Filtering && !DirectMatch ? Name + " · " + L.Get("Layers.TargetAncestor") : Name;
        public string ParentPath => Parent?.Path ?? "";
        public Visibility PathVisibility => Filtering && Parent is not null ? Visibility.Visible : Visibility.Collapsed;
        public Thickness Indent => new(Filtering ? Math.Min(Depth, 2) * 8 : Depth * 14, 0, 0, 0);
        public double RowHeight => PathVisibility == Visibility.Visible ? 46 : 30;
        public double TextOpacity => Reason is not null ? .55 : Filtering && !DirectMatch ? .7 : 1;
        public FontWeight Weight => Filtering && DirectMatch ? FontWeights.SemiBold : FontWeights.Normal;
        public Visibility ExpanderVisibility => Children.Count == 0 || Filtering ? Visibility.Hidden : Visibility.Visible;
        public string Expansion => Children.Count == 0 ? "" : Expanded || Filtering ? "⌄" : "›";
        public string Hint => Path + (Reason is null ? "" : "\n" + Reason);
        public string DepthHint => L.Format("Layers.TargetDepth", Depth);
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Changed() => PropertyChanged?.Invoke(this, new(null));
    }
    private readonly List<Row> ordered = [];
    public ObservableCollection<Row> Visible { get; } = [];
    public string Search { get; private set; } = "";
    public LayerMoveTargets(EditorSession session, LayerOperationAnalysis analysis, bool sharedContext)
        => Reload(session, analysis, sharedContext);
    public void Reload(EditorSession session, LayerOperationAnalysis analysis, bool sharedContext)
    {
        var expansion = ordered.ToDictionary(r => (r.Root, r.Id), r => r.Expanded);
        ordered.Clear();
        var graph = SceneGraph.Read(session.Document);
        var expanded = session.EditScopePath.Select(g => g.Id).ToHashSet();
        foreach (var root in new[] { RootCanvasKind.Foreground, RootCanvasKind.Main, RootCanvasKind.Background })
        {
            var name = L.Get("RootCanvas." + root);
            var row = Add(root, null, null, name);
            if (root == session.ActiveRootCanvas)
                foreach (var id in graph.EnumerateChildren(null))
                    if (SceneCanvasRoots.Resolve(session.Document, id, graph) == root) Visit(id, row);
        }
        Row Add(RootCanvasKind root, Guid? id, Row? parent, string name)
        {
            var key = root != session.ActiveRootCanvas && sharedContext ? "RootCanvas.ReturnUnavailable" : analysis.MoveTargetReason(root, id);
            var row = new Row { Root = root, Id = id, Parent = parent, Name = name,
                Path = parent is null ? name : parent.Path + " › " + name, Depth = parent is null ? 0 : parent.Depth + 1,
                Reason = key is null ? null : L.Get(key), Expanded = expansion.TryGetValue((root, id), out bool open) ? open :
                    id is null || expanded.Contains(id.Value) };
            ordered.Add(row); parent?.Children.Add(row); return row;
        }
        void Visit(Guid id, Row parent)
        {
            if (graph.FindGroup(id) is not { } group) return;
            var name = string.IsNullOrWhiteSpace(group.Name) ? L.Get("Layers.Group") : group.Name;
            var row = Add(parent.Root, id, parent, name);
            foreach (var child in graph.EnumerateChildren(id)) Visit(child, row);
        }
        Filter(Search);
    }
    public void Toggle(Row row) { if (Search.Length != 0 || row.Children.Count == 0) return; row.Expanded = !row.Expanded; Filter(Search); }
    public void Filter(string text)
    {
        Search = text.Trim(); bool filter = Search.Length > 0;
        foreach (var row in ordered)
        { row.DirectMatch = !filter || row.Path.Contains(Search, StringComparison.CurrentCultureIgnoreCase); row.Matches = row.DirectMatch; row.Filtering = filter; }
        // A single reverse traversal propagates matches to required ancestors.
        for (int i = ordered.Count - 1; i >= 0; i--)
            if (ordered[i].Matches && ordered[i].Parent is { } parent) parent.Matches = true;
        var next = new List<Row>();
        void Visit(Row row)
        {
            if (!row.Matches) return;
            next.Add(row); row.Changed();
            if (filter || row.Expanded) foreach (var child in row.Children) Visit(child);
        }
        foreach (var row in ordered) if (row.Parent is null) Visit(row);
        var retained = next.ToHashSet();
        for (int i = Visible.Count - 1; i >= 0; i--) if (!retained.Contains(Visible[i])) Visible.RemoveAt(i);
        for (int i = 0; i < next.Count; i++) if (i == Visible.Count || !ReferenceEquals(Visible[i], next[i])) Visible.Insert(i, next[i]);
    }
    public ListBox CreateList()
    {
        var list = new ListBox { DataContext = this, ItemsSource = Visible, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SetResourceReference(Control.BackgroundProperty, "CanvasSurface");
        list.SetResourceReference(Control.ForegroundProperty, "CanvasText");
        list.SetResourceReference(Control.BorderBrushProperty, "CanvasBorder");
        // Put metadata on the actual item peers, not only on a visual child panel.
        var itemStyle = new Style(typeof(ListBoxItem), list.TryFindResource(typeof(ListBoxItem)) as Style);
        itemStyle.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(nameof(Row.Path))));
        itemStyle.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.HelpTextProperty, new Binding(nameof(Row.DepthHint))));
        itemStyle.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.ItemStatusProperty,
            new Binding(nameof(Row.Reason)) { TargetNullValue = "" }));
        list.ItemContainerStyle = itemStyle;
        var factory = new FrameworkElementFactory(typeof(LayerMoveTargetPresenter));
        list.ItemTemplate = new DataTemplate { VisualTree = factory };
        list.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is Button { DataContext: Row row }) { Toggle(row); e.Handled = true; }
        }));
        list.PreviewKeyDown += (_, e) =>
        {
            if (list.SelectedItem is not Row row) return;
            if (e.Key == Key.Right) { if (Search.Length == 0 && !row.Expanded) Toggle(row); else if (row.Children.FirstOrDefault(Visible.Contains) is { } child) list.SelectedItem = child; e.Handled = true; }
            else if (e.Key == Key.Left) { if (Search.Length == 0 && row.Expanded && row.Children.Count > 0) Toggle(row); else if (row.Parent is { } parent) list.SelectedItem = parent; e.Handled = true; }
            if (e.Handled && list.SelectedItem is { } selected) list.ScrollIntoView(selected);
        };
        return list;
    }
}

internal sealed class LayerMoveTargetPresenter : DockPanel
{
    public LayerMoveTargetPresenter()
    {
        LastChildFill = true;
        SetBinding(HeightProperty, new Binding(nameof(LayerMoveTargets.Row.RowHeight)));
        SetBinding(MarginProperty, new Binding(nameof(LayerMoveTargets.Row.Indent)));
        var expand = new Button { Width = 22, Height = 22, MinWidth = 0, MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center, Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new(0), Focusable = false, Padding = new(0) };
        expand.SetBinding(ContentControl.ContentProperty, new Binding(nameof(LayerMoveTargets.Row.Expansion)));
        expand.SetBinding(VisibilityProperty, new Binding(nameof(LayerMoveTargets.Row.ExpanderVisibility)));
        SetDock(expand, Dock.Left); Children.Add(expand);
        var icon = new ContentControl { Width = 18, Margin = new(2, 0, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        SetDock(icon, Dock.Left); Children.Add(icon);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var caption = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        caption.SetBinding(TextBlock.TextProperty, new Binding(nameof(LayerMoveTargets.Row.Caption)));
        caption.SetBinding(OpacityProperty, new Binding(nameof(LayerMoveTargets.Row.TextOpacity)));
        caption.SetBinding(TextBlock.FontWeightProperty, new Binding(nameof(LayerMoveTargets.Row.Weight)));
        var path = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        path.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        path.SetBinding(TextBlock.TextProperty, new Binding(nameof(LayerMoveTargets.Row.ParentPath)));
        path.SetBinding(VisibilityProperty, new Binding(nameof(LayerMoveTargets.Row.PathVisibility)));
        labels.Children.Add(caption); labels.Children.Add(path); Children.Add(labels);
        SetBinding(ToolTipProperty, new Binding(nameof(LayerMoveTargets.Row.Hint)));
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not LayerMoveTargets.Row row) return;
            icon.Content = ScopeNavigation.Icon(row.Id is null ? ScopeNavigation.RootGlyphFor(row.Root) : "M1,3 H6 L8,5 H15 V13 H1 Z", 0);
            System.Windows.Automation.AutomationProperties.SetName(this, row.Path);
            System.Windows.Automation.AutomationProperties.SetItemStatus(this, row.Reason ?? "");
            System.Windows.Automation.AutomationProperties.SetPositionInSet(this, row.Parent?.Children.IndexOf(row) + 1 ?? 1);
            System.Windows.Automation.AutomationProperties.SetHelpText(this, L.Format("Layers.TargetDepth", row.Depth));
        };
    }
}
