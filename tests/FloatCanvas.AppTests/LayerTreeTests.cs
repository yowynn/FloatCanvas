using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

/// <summary>Node commands through actual recycled row controls; native hosts stay hidden.</summary>
internal static class LayerTreeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string language = L.Preference;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            {
                L.Initialize(locale); AppTheme.Apply("light");
                Commands(check, locale, output);
            }
            Virtualization(check);
            Dragging(check);
        }
        finally { L.Initialize(language); }
    }
    private static void Commands(Action<string, bool> check, string locale, string? output)
    {
        CanvasElement a = Box("A"), b = Box("B"), c = Box("C"); a.Visible = false;
        var session = new EditorSession(new() { Elements = [a, b, c] }); session.SetSelection([a.Id, b.Id]);
        var group = session.GroupSelection(locale == "zh-CN" ? "组1" : "Group 1")!.Value;
        bool allowed = true; var window = new LayersWindow(() => session, () => allowed);
        window.PrepareForShow(); int history = session.UndoCount; long revision = session.SceneRevision;
        check(locale + " root lists only direct children and groups never inline-expand", window.VisibleNodes.Count == 2 && window.FindNode(group) is { IsGroup: true, Depth: 0 } && window.FindNode(a.Id) is null);
        var groupPresenter = LayerTreeRowPresenter.Find(window.Container(group))!;
        groupPresenter.EnterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.PrepareForShow();
        check(locale + " group arrow replaces root rows with only that group's direct children", session.EditScopeId == group && window.VisibleNodes.Select(r => r.Id).ToHashSet().SetEquals([a.Id, b.Id]) &&
            window.VisibleNodes.All(r => r.Depth == 0 && r.ParentId == group) && window.FindNode(group) is null && window.FindNode(c.Id) is null);
        check(locale + " entering group changes only scope and exposes its full breadcrumb path", Crumbs().Select(button => button.Content).SequenceEqual(new object[] { L.Get("Layers.RootScope"), locale == "zh-CN" ? "组1" : "Group 1" }) &&
            session.UndoCount == history && session.SceneRevision == revision);
        Invoke(window, "CreateGroup"); var emptyGroup = session.Selection.Single();
        string defaultGroupName = session.Document.Groups.Single(g => g.Id == emptyGroup).Name;
        string chosenGroupName = locale == "zh-CN" ? "组2" : "Group 2";
        var editor = LayerTreeRowPresenter.Find(window.Container(emptyGroup))!.Editor;
        editor.Text = chosenGroupName; Invoke(window, "HandleRenameKey", System.Windows.Input.Key.Enter); window.PrepareForShow();
        check(locale + " new empty group is a real named current-level child with its own paint slot", session.Document.Groups.Single(g => g.Id == emptyGroup) is { ParentId: var parent, ChildOrder.Count: 0 } && parent == group &&
            session.Document.Groups.Single(g => g.Id == group).ChildOrder!.Contains(emptyGroup) && session.UndoCount == history + 1 + (defaultGroupName == chosenGroupName ? 0 : 1));
        window.EnterGroup(emptyGroup); window.PrepareForShow();
        check(locale + " nested group uses root to group1 to group2 breadcrumbs and an empty current list", session.EditScopeId == emptyGroup && window.VisibleNodes.Count == 0 &&
            Crumbs().Select(button => button.Content).SequenceEqual(new object[] { L.Get("Layers.RootScope"), locale == "zh-CN" ? "组1" : "Group 1", locale == "zh-CN" ? "组2" : "Group 2" }) &&
            Field<TextBlock>(window, "empty").Visibility == Visibility.Visible && !Crumbs().Last().IsEnabled);
        var beforeNavigation = JsonSerializer.Serialize(session.Document); int beforeNavigationUndo = session.UndoCount;
        Field<Button>(window, "parentScope").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.PrepareForShow();
        check(locale + " parent navigation selects the folder just left and makes no author changes", session.EditScopeId == group && session.Selection.SetEquals([emptyGroup]) &&
            JsonSerializer.Serialize(session.Document) == beforeNavigation && session.UndoCount == beforeNavigationUndo);
        window.EnterGroup(emptyGroup); window.PrepareForShow(); Crumbs().First().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.PrepareForShow();
        check(locale + " ancestor breadcrumb jumps directly to root without leaking nested rows", session.EditScopeId is null && window.VisibleNodes.Count == 2 && window.FindNode(emptyGroup) is null &&
            session.UndoCount == beforeNavigationUndo && !Field<Button>(window, "parentScope").IsEnabled);
        window.EnterGroup(group); window.PrepareForShow(); session.SetSelection([emptyGroup]); window.PrepareForShow();
        Menu(window, emptyGroup, "Inspector.Duplicate");
        check(locale + " empty groups duplicate in the current scope without inventing leaves", session.Document.Groups.Count == 3 && session.Document.Elements.Count == 3 && window.VisibleNodes.Count == 4);
        session.Undo(); window.PrepareForShow(); Root();
        var outer = LayerTreeRowPresenter.Find(window.Container(group))!;
        outer.VisibleButton.IsChecked = false; outer.VisibleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.PrepareForShow();
        window.EnterGroup(group); window.PrepareForShow();
        check(locale + " parent visibility preserves each child's own visibility at its editing level", !session.Document.Groups.Single(g => g.Id == group).Visible &&
            !session.Document.Elements.Single(e => e.Id == a.Id).Visible && session.Document.Elements.Single(e => e.Id == b.Id).Visible && !window.FindNode(b.Id)!.EffectiveVisible);
        Root(); outer = LayerTreeRowPresenter.Find(window.Container(group))!;
        outer.VisibleButton.IsChecked = true; outer.VisibleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.PrepareForShow(); window.EnterGroup(group); window.PrepareForShow();
        check(locale + " revealing parent restores inherited state without overriding hidden children", !window.FindNode(a.Id)!.EffectiveVisible && window.FindNode(b.Id)!.EffectiveVisible);
        session.SetNodeLocked(group, true); window.PrepareForShow();
        var child = LayerTreeRowPresenter.Find(window.Container(b.Id))!;
        check(locale + " a current scope locked externally disables child flags and drag", window.FindNode(b.Id)!.EffectiveLocked && !child.VisibleButton.IsEnabled && !child.LockedButton.IsEnabled && !child.Handle.IsEnabled);
        int lockedHistory = session.UndoCount; window.SetLayerFlag(b.Id, true, false);
        check(locale + " inherited lock also guards direct stale flag commands", session.UndoCount == lockedHistory && session.Document.Elements.Single(e => e.Id == b.Id).Visible);
        Root(); outer = LayerTreeRowPresenter.Find(window.Container(group))!;
        outer.LockedButton.IsChecked = false; outer.LockedButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.PrepareForShow();
        int beforeScope = session.UndoCount; window.EnterGroup(group); window.PrepareForShow(); Root();
        check(locale + " breadcrumb returns to root without history", session.EditScopeId is null && session.UndoCount == beforeScope);
        allowed = false; int blocked = session.UndoCount; Invoke(window, "CreateGroup"); window.EnterGroup(group);
        check(locale + " busy guard covers group creation and scope entry", session.UndoCount == blocked && session.EditScopeId is null);
        allowed = true; window.EnterGroup(group); window.PrepareForShow();
        check(locale + " a current child can start inline rename", window.BeginRename(b.Id)); window.FindNode(b.Id)!.DraftName = "stale scope draft";
        session.SetEditScope(emptyGroup); window.PrepareForShow();
        check(locale + " external scope changes cancel hidden rename without renaming another level", !window.IsRenaming && session.Document.Elements.Single(e => e.Id == b.Id).Name == "B" && window.VisibleNodes.Count == 0);
        var local1 = Box(locale == "zh-CN" ? "当前层级的矩形" : "Rectangle in this level");
        var local2 = Box(locale == "zh-CN" ? "较长的图层名称会在本级列表中省略显示" : "A longer layer name stays in this level");
        session.Add(local1); session.Add(local2); window.PrepareForShow();
        int visualHistory = session.UndoCount; string visualDocument = JsonSerializer.Serialize(session.Document);
        foreach (string theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme);
            check(locale + " " + theme + " level viewport follows its canvas surface", Field<ListBox>(window, "layers").Background is SolidColorBrush listSurface &&
                window.TryFindResource("CanvasSurface") is SolidColorBrush expectedSurface && listSurface.Color == expectedSurface.Color);
        }
        check(locale + " actual nested panel renders only group2 direct children with all three breadcrumbs and no author edits", window.VisibleNodes.Select(r => r.Id).ToHashSet().SetEquals([local1.Id, local2.Id]) &&
            Crumbs().Select(button => button.Content).SequenceEqual(new object[] { L.Get("Layers.RootScope"), locale == "zh-CN" ? "组1" : "Group 1", locale == "zh-CN" ? "组2" : "Group 2" }) &&
            session.UndoCount == visualHistory && JsonSerializer.Serialize(session.Document) == visualDocument);
        var visualSession = session;
        Root(); AppTheme.Apply("light");
        check(locale + " detached level navigation creates no native window", new WindowInteropHelper(window).Handle == 0 && !window.IsVisible);
        CanvasElement oldA = Box("old A"), oldB = Box("old B"), oldC = Box("old C"); var oldGroup = new CanvasGroup { Name = "Legacy" };
        oldA.GroupId = oldC.GroupId = oldGroup.Id;
        session = new(new() { Elements = [oldA, oldB, oldC], Groups = [oldGroup] });
        window.PrepareForShow(); string oldScene = JsonSerializer.Serialize(session.Document);
        check(locale + " legacy group stays marked at the current level", window.FindNode(oldGroup.Id)!.IsLegacy);
        window.EnterGroup(oldGroup.Id); window.PrepareForShow();
        check(locale + " browsing a legacy group preserves original interleaved draw order", window.VisibleNodes.Count == 2 && JsonSerializer.Serialize(session.Document) == oldScene &&
            new SceneGraph(session.Document).OrderedLeafIds.SequenceEqual([oldA.Id, oldB.Id, oldC.Id]));
        Root(); session.SetSelection([oldGroup.Id]); window.PrepareForShow(); Menu(window, oldGroup.Id, "Layers.CollectGroup");
        check(locale + " explicit consolidation changes paint order in one undo step", session.Document.Groups.Single().OrderMode == GroupOrderMode.AtomicBlock && session.UndoCount == 1 &&
            new SceneGraph(session.Document).OrderedLeafIds.SequenceEqual([oldB.Id, oldA.Id, oldC.Id]));
        check(locale + " consolidation undo restores exact legacy document", session.Undo() && JsonSerializer.Serialize(session.Document) == oldScene);
        if (output is not null)
        {
            session = visualSession; session.SetEditScope(emptyGroup); window.PrepareForShow();
            foreach (string theme in new[] { "light", "dark" })
            {
                AppTheme.Apply(theme);
                foreach (int width in new[] { 300, 340 }) Render(window, System.IO.Path.Combine(output, "layers-level-" + locale + "-" + theme + "-" + width + ".png"), width);
            }
            check(locale + " hidden native visual capture preserves exact nested content and author history", window.VisibleNodes.Select(r => r.Id).ToHashSet().SetEquals([local1.Id, local2.Id]) &&
                Crumbs().Count() == 3 && session.UndoCount == visualHistory && JsonSerializer.Serialize(session.Document) == visualDocument);
        }
        IEnumerable<Button> Crumbs() => Field<StackPanel>(window, "breadcrumbs").Children.OfType<Button>();
        void Root() { Crumbs().First().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.PrepareForShow(); }
    }
    internal static void Virtualization(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; AppTheme.Apply("light");
        const int count = 50_000;
        var group = new CanvasGroup { Name = "50,000 children", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var children = Enumerable.Range(0, count).Select(i => new CanvasElement { Name = "Leaf " + i, Kind = ElementKind.Rectangle,
            Width = 20, Height = 20, GroupId = group.Id }).ToList(); group.ChildOrder.AddRange(children.Select(e => e.Id));
        var session = new EditorSession(new() { Elements = children, Groups = [group], RootOrder = [group.Id] });
        var window = new LayersWindow(() => session); using var host = new HiddenTreeHost(window);
        var list = Field<ListBox>(window, "layers"); var timer = Stopwatch.StartNew();
        window.PrepareForShow(); host.Layout();
        check("50,000 descendants outside the current level allocate only the group's own row and container", window.VisibleNodes.Count == 1 && Realized(list).Count() == 1);
        window.EnterGroup(group.Id); host.Layout();
        var firstContainers = Realized(list).ToArray();
        check("entering a 50,000-leaf level keeps WPF containers bounded by its viewport", window.VisibleNodes.Count == count && window.VisibleNodes.All(r => r.Depth == 0) && firstContainers.Length is > 0 and < 120 &&
            VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling);
        list.ScrollIntoView(list.Items[^1]); host.Layout();
        var lastContainers = Realized(list).ToArray();
        check("scrolling recycles containers and binds their current node identity", lastContainers.Length is > 0 and < 120 && lastContainers.Any(firstContainers.Contains) &&
            lastContainers.All(c => c.DataContext is LayerTreeRow row && Equals(c.Tag, row.Id) && LayerTreeRowPresenter.Find(c)?.Label.Text == row.Name));
        session.SetSelection([children[0].Id]); window.PrepareForShow(); Invoke(window, "LeaveGroup"); host.Layout();
        check("leaving a large level selects its parent entry without changing document or history", window.VisibleNodes.Count == 1 && session.Selection.SetEquals([group.Id]) && session.UndoCount == 0 && session.Document.Elements.Count == count);
        check("virtualized tree host stays hidden and keeps the real foreground", host.NoDesktopEffect);
        Console.WriteLine($"LAYER_TREE_VIRTUALIZATION leaves={count} firstContainers={firstContainers.Length} lastContainers={lastContainers.Length} elapsedMs={timer.Elapsed.TotalMilliseconds:0.00}");
        session = new(); window.PrepareForShow();
    }
    internal static void Dragging(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; AppTheme.Apply("light");
        CanvasElement a = Box("A"), b = Box("B"), outside = Box("Outside"); outside.Transform = Affine2D.Translation(200, 100);
        var session = new EditorSession(new() { Elements = [a, b, outside] }); session.SetSelection([a.Id, b.Id]); var group = session.GroupSelection("Target")!.Value;
        session.Document.Groups.Single().Transform = Affine2D.Translation(50, 35).Compose(Affine2D.Rotation(20));
        var window = new LayersWindow(() => session); using var host = new HiddenTreeHost(window);
        window.PrepareForShow(); host.Layout();
        var worldBefore = new SceneGraph(session.Document).ToWorld(session.Document.Elements.Single(e => e.Id == outside.Id)).Transform;
        int history = session.UndoCount;
        Drag(outside.Id, group, middle: true);
        check("real Thumb center drop moves into a group and removes the row from the current level", session.Document.Elements.Single(e => e.Id == outside.Id).GroupId == group && window.FindNode(outside.Id) is null && session.EditScopeId is null && session.UndoCount == history + 1);
        var moved = new SceneGraph(session.Document).ToWorld(session.Document.Elements.Single(e => e.Id == outside.Id)).Transform;
        Console.WriteLine($"LAYER_DROP_WORLD_ERROR {MatrixError(moved, worldBefore):G17}");
        check("cross-parent UI drop preserves world geometry", MatrixError(moved, worldBefore) < 1e-8);
        window.EnterGroup(group); window.PrepareForShow(); session.SetSelection([outside.Id]); window.PrepareForShow(); Menu(window, outside.Id, "Layers.MoveOut"); host.Layout();
        check("move-to-parent command preserves world placement and adds one undo step", session.Document.Elements.Single(e => e.Id == outside.Id).GroupId is null &&
            MatrixError(new SceneGraph(session.Document).ToWorld(session.Document.Elements.Single(e => e.Id == outside.Id)).Transform, worldBefore) < 1e-8 && session.UndoCount == history + 2);
        Invoke(window, "LeaveGroup"); window.PrepareForShow(); Drag(outside.Id, group, middle: true); window.EnterGroup(group); window.PrepareForShow(); host.Layout();
        session.SetSelection([outside.Id]); window.PrepareForShow(); host.Layout();
        var rootCrumb = Field<StackPanel>(window, "breadcrumbs").Children.OfType<Button>().First();
        var list = Field<ListBox>(window, "layers"); var handle = LayerTreeRowPresenter.Find(window.Container(outside.Id))!.Handle;
        var start = handle.TranslatePoint(new Point(10, 18), list); var screenStart = list.PointToScreen(start);
        var destination = rootCrumb.TranslatePoint(new Point(rootCrumb.ActualWidth / 2, rootCrumb.ActualHeight / 2), list);
        handle.RaiseEvent(new DragStartedEventArgs(10, 18) { RoutedEvent = Thumb.DragStartedEvent });
        Invoke(window, "UpdateDropTarget", destination); var delta = list.PointToScreen(destination) - screenStart;
        check("breadcrumb drag resolves the currently mounted root button as an explicit move-out target", Field<bool>(window, "dropInto") && Field<Guid?>(window, "dropParent") is null);
        handle.RaiseEvent(new DragCompletedEventArgs(delta.X, delta.Y, false) { RoutedEvent = Thumb.DragCompletedEvent });
        window.PrepareForShow(); host.Layout();
        check("real Thumb breadcrumb drop moves out of the group with world placement intact", session.Document.Elements.Single(e => e.Id == outside.Id).GroupId is null &&
            MatrixError(new SceneGraph(session.Document).ToWorld(session.Document.Elements.Single(e => e.Id == outside.Id)).Transform, worldBefore) < 1e-8 && session.UndoCount == history + 4);
        session.EnterScope(group); session.CreateEmptyGroup("Nested"); session.LeaveScope(); window.PrepareForShow(); host.Layout();
        string beforeInvalid = JsonSerializer.Serialize(session.Document); history = session.UndoCount;
        Drag(group, group, middle: true);
        check("self-drop cannot become an implicit reparent into a hidden descendant", JsonSerializer.Serialize(session.Document) == beforeInvalid && session.UndoCount == history);
        check("current-level drag begins on its visible source", window.BeginDrag(outside.Id));
        session.SetEditScope(group); session.SetEditScope(null);
        Invoke(window, "FinishDrag", true); window.PrepareForShow();
        check("a scope change and return invalidates a waiting drag without an author edit", JsonSerializer.Serialize(session.Document) == beforeInvalid && session.UndoCount == history);
        check("node drag fixture keeps all native windows hidden and foreground unchanged", host.NoDesktopEffect);

        void Drag(Guid sourceId, Guid targetId, bool middle)
        {
            session.SetSelection([sourceId]); window.PrepareForShow(); host.Layout();
            var list = Field<ListBox>(window, "layers"); var source = LayerTreeRowPresenter.Find(window.Container(sourceId))!;
            var target = window.Container(targetId)!; var start = source.Handle.TranslatePoint(new Point(10, 18), list); var screenStart = list.PointToScreen(start);
            var point = target.TranslatePoint(new Point(110, middle ? target.ActualHeight / 2 : 1), list);
            source.Handle.RaiseEvent(new DragStartedEventArgs(10, 18) { RoutedEvent = Thumb.DragStartedEvent });
            Invoke(window, "UpdateDropTarget", point);
            var change = list.PointToScreen(point) - screenStart;
            source.Handle.RaiseEvent(new DragCompletedEventArgs(change.X, change.Y, false) { RoutedEvent = Thumb.DragCompletedEvent });
            window.PrepareForShow(); host.Layout();
        }
    }
    private static CanvasElement Box(string name) => new() { Name = name, Kind = ElementKind.Rectangle, Width = 40, Height = 30 };
    private static double MatrixError(Affine2D a, Affine2D b) => new[] { Math.Abs(a.M11 - b.M11), Math.Abs(a.M12 - b.M12), Math.Abs(a.M21 - b.M21), Math.Abs(a.M22 - b.M22), Math.Abs(a.OffsetX - b.OffsetX), Math.Abs(a.OffsetY - b.OffsetY) }.Max();
    private static void Menu(LayersWindow window, Guid id, string key)
    {
        window.Container(id)!.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Tag, key)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        window.PrepareForShow();
    }
    private static IEnumerable<ListBoxItem> Realized(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ListBoxItem item) yield return item;
            else foreach (var nested in Realized(child)) yield return nested;
        }
    }
    private static void Render(LayersWindow window, string path, int width = 340)
    {
        using var host = new HiddenTreeHost(window, width);
        var bitmap = new RenderTargetBitmap((int)(width * 1.5), 810, 144, 144, PixelFormats.Pbgra32); bitmap.Render(host.RenderRoot);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var stream = System.IO.File.Create(path); png.Save(stream);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private sealed class HiddenTreeHost : IDisposable
    {
        private readonly LayersWindow window;
        private readonly FrameworkElement content;
        private readonly Border root;
        private readonly HwndSource source;
        private readonly double width;
        internal FrameworkElement RenderRoot => root;
        private readonly nint foreground = GetForegroundWindow();
        internal HiddenTreeHost(LayersWindow window, int width = 340)
        {
            this.window = window; this.width = width; content = (FrameworkElement)window.Content; window.Content = null;
            root = new Border { Width = width, Height = 540, Child = content };
            root.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
            source = new(new HwndSourceParameters("FloatCanvas hidden layer tree") { Width = width, Height = 540,
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080 }); source.RootVisual = root;
            var dpi = VisualTreeHelper.GetDpi(root);
            if (!SetWindowPos(source.Handle, 0, 0, 0, (int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(540 * dpi.DpiScaleY), 0x16))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Layout();
        }
        internal bool NoDesktopEffect => !IsWindowVisible(source.Handle) && !window.IsVisible && GetForegroundWindow() == foreground;
        internal void Layout()
        {
            root.Measure(new Size(width, 540)); root.Arrange(new Rect(0, 0, width, 540)); root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        public void Dispose() { Invoke(window, "CancelDrag"); root.Child = null; source.RootVisual = null; source.Dispose(); window.Content = content; }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
}
