using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class LayerExperienceTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var a = new CanvasElement { Name = "A", Kind = ElementKind.Rectangle, Width = 20, Height = 20 };
        var b = a.Clone(true); b.Name = "B";
        var session = new EditorSession(new() { Elements = [a,b], RootOrder = [a.Id,b.Id] });
        session.SetSelection([a.Id,b.Id]); int undo = session.UndoCount;
        check("batch hide changes both", session.SetNodesFlag(session.Selection, true, false) == 2 && !a.Visible && !b.Visible);
        check("batch has one history entry", session.UndoCount == undo + 1);
        session.Undo(); check("batch undo restores all", session.Document.Elements.All(e => e.Visible));
        session.SetNodesFlag(session.Selection, false, true);
        check("unlock supports own locked nodes", session.SetNodesFlag(session.Selection, false, false) == 2);
        undo = session.UndoCount; session.SetNodesFlag(session.Selection, true, true);
        check("no-op flag has no history", session.UndoCount == undo);
        var destination = session.CreatePopulatedGroup("Destination"); session.SetSelection([a.Id,b.Id]);
        session.MoveLayerNodes(session.Selection, RootCanvasKind.Main, destination);
        check("move leaves source scope and clears moved selection", session.EditScopeId is null && session.Selection.Count == 0);
        check("move reparents both", new SceneGraph(session.Document).ParentId(a.Id) == destination && new SceneGraph(session.Document).ParentId(b.Id) == destination);
        session.Undo(); check("move undo restores ownership", new SceneGraph(session.Document).ParentId(a.Id) is null);
        var before = System.Text.Json.JsonSerializer.Serialize(session.Document);
        try { session.MoveLayerNodes([destination], RootCanvasKind.Main, destination); } catch (InvalidOperationException) { }
        check("invalid move is atomic", before == System.Text.Json.JsonSerializer.Serialize(session.Document));
        session.MoveLayerNodes([a.Id], RootCanvasKind.Foreground, null);
        check("cross-root move retains source canvas", session.ActiveRootCanvas == RootCanvasKind.Main && SceneCanvasRoots.Resolve(session.Document, a.Id) == RootCanvasKind.Foreground);

        var unsafeScene = new SceneDocument { Viewport = new(-250,-500,500,900) };
        CanvasCalibration.TryCalibrate(CanvasCalibration.CreateInitial(unsafeScene.Viewport,4,5),[new(-100,-100),new(100,-100),new(200,200),new(-200,200)],unsafeScene.Viewport,out var perspective,out _);
        unsafeScene.Canvas.Projection=perspective;
        var matrix=CanvasMapping.Create(unsafeScene).PlaneToScreen; double norm=matrix.M31*matrix.M31+matrix.M32*matrix.M32;
        var horizon = new PointD(-matrix.M31*matrix.M33/norm,-matrix.M32*matrix.M33/norm);
        var unsafeNode = new CanvasElement { Kind=ElementKind.Rectangle, Width=40, Height=40, Transform=Affine2D.Translation(horizon.X-20,horizon.Y-20) };
        unsafeScene.Elements.Add(unsafeNode); SceneCanvasRoots.Assign(unsafeScene,unsafeNode.Id,RootCanvasKind.Foreground);
        var unsafeSession = new EditorSession(unsafeScene);unsafeSession.SetRootCanvas(RootCanvasKind.Foreground);
        string unsafeBefore=System.Text.Json.JsonSerializer.Serialize(unsafeSession.Document);bool rejected=false;
        try { unsafeSession.MoveLayerNodes([unsafeNode.Id],RootCanvasKind.Main,null); } catch(InvalidOperationException) { rejected=true; }
        check("unsafe perspective transfer has no partial graph or history",rejected && unsafeSession.UndoCount==0 && unsafeBefore==System.Text.Json.JsonSerializer.Serialize(unsafeSession.Document));

        var window = new LayersWindow(() => session); session.SetEditScope(destination);
        using var host = new GroupCanvasUiTests.NativeHost(window,280,540); window.PrepareForShow(); host.Layout();
        var source = PresentationSource.FromVisual(window);
        // Exercise the production preview handler without taking desktop focus.
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, source ?? new DetachedSource(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        typeof(LayersWindow).GetMethod("OnKeyDown", hidden | BindingFlags.DeclaredOnly)!.Invoke(window, [window,key]);
        check("Escape retains ordinary scope", session.EditScopeId == destination && key.Handled);
        var toggle = (CanvasModeSwitch)typeof(LayersWindow).GetField("groupCanvasChoice", hidden)!.GetValue(window)!;
        int calls = 0;
        window.SetGroupContext(new("Destination", GroupStorageKind.Ordinary, null, GroupCanvasMode.RootProject, true, Temporary: () => calls++));
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check("switch requests once without optimistic state", calls == 1 && toggle.Mode == GroupCanvasMode.RootProject);
        window.SetGroupContext(new("Destination", GroupStorageKind.Ordinary, null, GroupCanvasMode.Temporary, true, Root: () => calls++));
        check("switch reflects accepted state", toggle.Mode == GroupCanvasMode.Temporary);
        check("switch has one control target", toggle.Content is Grid { IsHitTestVisible: false } &&
            !HasButton((DependencyObject)toggle.Content));
        host.Layout();
        foreach (double x in new[] { 1d,31d,61d })
        {
            var hit = toggle.InputHitTest(new Point(x,14)) as DependencyObject;
            while (hit is not null && hit != toggle) hit = VisualTreeHelper.GetParent(hit);
            check($"switch hit at {x} resolves to the same button", hit == toggle);
        }
        int keyboardCalls = calls;
        var toggleSource = PresentationSource.FromVisual(toggle)!;
        toggle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,toggleSource,0,Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        toggle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,toggleSource,0,Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
        check("Space requests exactly one switch including key-up", calls == keyboardCalls + 1);
        var repeated = new KeyEventArgs(Keyboard.PrimaryDevice,toggleSource,0,Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        typeof(KeyEventArgs).GetMethod("SetRepeat",hidden)!.Invoke(repeated,[true]); toggle.RaiseEvent(repeated);
        check("held keyboard activation does not repeat switch", calls == keyboardCalls + 1);
        window.SetGroupContext(null); check("missing context disables switch", !toggle.IsEnabled);
        DeepBreadcrumbs(check);
        EdgeScrolling(check);
        Statistics(check);

        if (output is not null)
        {
            System.IO.Directory.CreateDirectory(output);
            foreach (string theme in new[] { "light", "dark" })
            foreach (double scale in new[] { 1d, 1.5, 2 })
            {
                AppTheme.Apply(theme);
                window.SetGroupContext(new("Destination", GroupStorageKind.Ordinary, null, GroupCanvasMode.RootProject, true, Temporary: () => { }));
                host.Layout();
                var bitmap = new RenderTargetBitmap((int)(280*scale),(int)(540*scale),96*scale,96*scale,PixelFormats.Pbgra32); bitmap.Render(host.Root);
                using var stream = System.IO.File.Create(System.IO.Path.Combine(output,$"layers-{theme}-{scale}.png"));
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
            }
            AppTheme.Apply("light");
        }
        Console.WriteLine("Layer experience checks completed");
    }
    private static void Statistics(Action<string, bool> check)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var child = new CanvasElement { GroupId = group.Id, Kind = ElementKind.Rectangle, Width = 10, Height = 10 };
        var other = child.Clone(true); other.GroupId = null;
        group.ChildOrder.Add(child.Id);
        var session = new EditorSession(new() { Groups = [group], Elements = [child, other], RootOrder = [group.Id, other.Id] });
        SceneCanvasRoots.Assign(session.Document, other.Id, RootCanvasKind.Foreground);
        var window = new LayersWindow(() => session); window.PrepareForShow();
        var statistics = (TextBlock)typeof(LayersWindow).GetField("statistics", hidden)!.GetValue(window)!;
        var status = (TextBlock)typeof(LayersWindow).GetField("status", hidden)!.GetValue(window)!;
        string Expected(int total, int level, int selected) => FloatCanvas.Localization.AppText.Format("Layers.Statistics", total, level, selected);
        check("footer counts all roots and current direct layers", statistics.Text == Expected(3, 1, 0));
        check("empty operation feedback consumes no footer height", status.Visibility == Visibility.Collapsed);
        session.SetEditScope(group.Id); session.SetSelection([child.Id]); window.PrepareForShow();
        check("footer updates entered scope and selection", statistics.Text == Expected(3, 1, 1));
        status.Text = "Operation feedback"; session.SetSelection([]); window.PrepareForShow();
        check("selection-only refresh retains separate operation feedback", statistics.Text == Expected(3, 1, 0) && status.Text == "Operation feedback" && status.Visibility == Visibility.Visible);
        check("footer exposes accessible count description", System.Windows.Automation.AutomationProperties.GetName(statistics) == statistics.Text);
        session.ReplaceDocument(new()); window.PrepareForShow();
        check("footer resets after document replacement", statistics.Text == Expected(0, 0, 0));
    }

    private static void DeepBreadcrumbs(Action<string,bool> check)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var document = new SceneDocument(); var graph = new SceneGraph(document); Guid? parent=null; Guid first=Guid.Empty;
        for(int n=0;n<7;n++) { parent=graph.InsertGroup(new() { Name="Ancestor with a long name "+n, OrderMode=GroupOrderMode.AtomicBlock, ChildOrder=[] },parent).Id; if(n==0) first=parent.Value; }
        graph.InsertElement(new() { Kind=ElementKind.Rectangle, Width=8, Height=8 }, parent, worldCoordinates:false);
        var session = new EditorSession(document); session.SetEditScope(parent);
        var window = new LayersWindow(() => session); using var host = new GroupCanvasUiTests.NativeHost(window,280,540); window.PrepareForShow(); host.Layout();
        var panel=(StackPanel)typeof(LayersWindow).GetField("breadcrumbs",hidden)!.GetValue(window)!;
        var buttons=panel.Children.OfType<Button>().Where(b=>b.Tag is not string).ToArray();
        var overflow=panel.Children.OfType<Button>().Single(b=>Equals(b.Tag,"overflow"));
        check("deep path keeps immediate parent and current scope visible",buttons[^2].Visibility==Visibility.Visible && buttons[^2].IsEnabled && buttons[^1].Visibility==Visibility.Visible && buttons[^1].FontWeight==FontWeights.SemiBold);
        overflow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        // The overlay menu is attached to its placement target for normal ownership and Escape routing.
        var menu=(OverlayContextMenu)overflow.ContextMenu!;
        var entries=menu.Items.OfType<MenuItem>().Where(item=>!Equals(item.Tag,"scope-mode")).ToArray();
        check("overflow retains all ancestors and current with a separate mode partition",entries.Length==buttons.Length &&
            entries.Select(item=>System.Windows.Automation.AutomationProperties.GetName(item)).SequenceEqual(buttons.Select(button=>System.Windows.Automation.AutomationProperties.GetName(button))) &&
            entries[^1].InputGestureText.StartsWith("✓",StringComparison.Ordinal) && entries[^1].Icon is FrameworkElement &&
            menu.Items[1] is Separator && Equals(((MenuItem)menu.Items[0]).Tag,"scope-mode"));
        menu.IsOpen=false;
        var disposalMenu = new OverlayContextMenu { PlacementTarget = overflow }; disposalMenu.Items.Add(new MenuItem { Header = "Test" }); disposalMenu.IsOpen = true;
        var popup = typeof(OverlayContextMenu).GetField("window",hidden)!.GetValue(disposalMenu)!;
        var popupSource = (System.Windows.Interop.HwndSource)popup.GetType().GetField("source",hidden)!.GetValue(popup)!;
        popupSource.Dispose(); disposalMenu.IsOpen=false;
        check("menu cleanup tolerates an already-destroyed native source",!disposalMenu.IsOpen);
        buttons[^2].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));window.PrepareForShow();host.Layout();
        check("direct parent navigation selects the group just left",session.Selection.SetEquals([parent!.Value]));
        var returned=panel.Children.OfType<Button>().Where(button=>!Equals(button.Tag,"overflow")).ToArray();
        check("parent navigation retains the future breadcrumb",returned.Length==buttons.Length &&
            System.Windows.Automation.AutomationProperties.GetItemStatus(returned[^1])==FloatCanvas.Localization.AppText.Get("ScopeNavigation.History"));
        int navigated=0;
        var file=new BoundFileIndicator(()=>{},()=>{},()=>{},()=>true,()=>true);file.Refresh("C:/Project/long-name.floatcanvas",true,true);
        var header=new RootCanvasToolbarHeader(file,()=>{},()=>true);
        header.SetScope(new("Main › Parent › Current","Project preview","",[("Main",()=>{}),("Parent",()=>navigated++)],"Current"),true);
        header.Measure(new Size(340,32));header.Arrange(new Rect(0,0,340,32));header.UpdateLayout();
        var back=(Button)typeof(RootCanvasToolbarHeader).GetField("scopeParent",hidden)!.GetValue(header)!;back.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check("toolbar parent path is directly clickable in the existing title row",navigated==1 && back.ActualWidth>0 && header.ActualHeight==ToolbarUpperRow.ExtensionHeight);
    }
    private static void EdgeScrolling(Action<string,bool> check)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var document = new SceneDocument();
        for (int n=0;n<100;n++) document.Elements.Add(new() { Name = "Layer " + n, Kind = ElementKind.Rectangle, Width=10, Height=10 });
        var session = new EditorSession(document); var window = new LayersWindow(() => session);
        using var host = new GroupCanvasUiTests.NativeHost(window,280,540); window.PrepareForShow(); host.Layout();
        var list = (ListBox)typeof(LayersWindow).GetField("layers",hidden)!.GetValue(window)!;
        var locked = document.Elements[0].Id; var editable=document.Elements[1].Id;
        session.SetNodeLocked(locked,true); session.SetSelection([locked,editable]);window.PrepareForShow();host.Layout();
        var lockedMenu=window.CreateContextMenu(locked);var editableMenu=window.CreateContextMenu(editable);
        lockedMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));editableMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        string[] eligible(ContextMenu menu)=>menu.Items.OfType<MenuItem>().Where(i=>i.IsEnabled&&i.Tag is string key&&key is "Inspector.Delete" or "Inspector.Duplicate" or "Inspector.BringToFront" or "Layers.Group" or "Layers.HideSelected" or "Layers.UnlockSelected").Select(i=>(string)i.Tag).ToArray();
        check("mixed selection menu eligibility is independent of right-click row",eligible(lockedMenu).SequenceEqual(eligible(editableMenu))&&eligible(lockedMenu).Contains("Inspector.Delete"));
        lockedMenu.Items.OfType<MenuItem>().Single(i=>Equals(i.Tag,"Layers.HideSelected")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check("batch visibility includes self-locked nodes and retains selection",!session.Document.Elements.Single(e=>e.Id==locked).Visible&&!session.Document.Elements.Single(e=>e.Id==editable).Visible&&session.Selection.SetEquals([locked,editable]));
        session.Undo();window.PrepareForShow();host.Layout();
        var scroll = FindScroll(list)!; var now = DateTime.UtcNow;
        check("long-list drag begins",window.BeginDrag(window.VisibleNodes[0].Id));
        var point = new Point(100,list.ActualHeight-3);
        window.UpdateDragAt(point,now);
        window.UpdateDragAt(point,now.AddMilliseconds(249),true);
        check("edge drag waits for dwell",scroll.VerticalOffset==0);
        window.UpdateDragAt(point,now.AddMilliseconds(251),true);
        check("edge drag scrolls and refreshes drop target",scroll.VerticalOffset>0 && typeof(LayersWindow).GetField("dropTarget",hidden)!.GetValue(window) is Guid);
        window.UpdateDragAt(new Point(100,-5),now.AddMilliseconds(300),true);
        var timer=(System.Windows.Threading.DispatcherTimer)typeof(LayersWindow).GetField("edgeScroll",hidden)!.GetValue(window)!;
        check("breadcrumb region stops edge scrolling",!timer.IsEnabled);
        scroll.ScrollToBottom(); list.UpdateLayout();
        window.UpdateDragAt(point,now.AddMilliseconds(400)); window.UpdateDragAt(point,now.AddMilliseconds(700),true);
        check("list boundary stops edge timer",!timer.IsEnabled);
        session.SetRootCanvas(RootCanvasKind.Foreground); window.PrepareForShow();
        check("navigation cancels active drag and timer",typeof(LayersWindow).GetField("dragIds",hidden)!.GetValue(window) is null && !timer.IsEnabled);
        static ScrollViewer? FindScroll(DependencyObject node)
        {
            if (node is ScrollViewer found) return found;
            for(int n=0;n<VisualTreeHelper.GetChildrenCount(node);n++) if (FindScroll(VisualTreeHelper.GetChild(node,n)) is { } child) return child;
            return null;
        }
    }
    private sealed class DetachedSource(Visual visual) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = visual;
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
    private static bool HasButton(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is Button || HasButton(child)) return true;
        }
        return false;
    }
}
