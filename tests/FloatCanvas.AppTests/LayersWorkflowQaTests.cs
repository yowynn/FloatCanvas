using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Exercises the actual layers event routes against a real owner on a private desktop.</summary>
internal static class LayersWorkflowQaTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal)) throw new InvalidOperationException("Use --isolated-ui-desktop --layers-workflow-qa-only.");
        if (Application.Current is not null) throw new InvalidOperationException("Run in a fresh child process.");
        string root = Path.Combine(output ?? Path.GetTempPath(), "layers-workflow-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        ApplicationPaths.Configure(["--profile", Path.Combine(root, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys();
        new UserSettings { Theme = "light", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2, PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var bounds = new PixelBounds(-22000, -22000, 1280, 1200);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("layers-workflow", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false; Field<Window>(desktop, "_toolbar").Opacity = 0;
        ((IDisposable?)typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.GetValue(host))?.Dispose(); typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.SetValue(host, null);
        host.SetVisible(true);
        var editor = Field<EditorController>(desktop, "_editor"); var owner = editor.Session;
        var observations = new List<(string, bool)>();
        void Observe(string text, bool result) { Console.WriteLine($"LAYERS_WORKFLOW {(result ? "PASS" : "FAIL")} {text}"); observations.Add((text, result)); }
        var leaf = new CanvasElement { Kind = ElementKind.Rectangle, Name = "Artwork", Width = 30, Height = 20, Transform = Affine2D.Translation(owner.Document.Viewport.Left + 30, owner.Document.Viewport.Top + 40) };
        var secondLeaf = leaf.Clone(true); secondLeaf.Name = "Second artwork"; secondLeaf.Transform = Affine2D.Translation(owner.Document.Viewport.Left + 80, owner.Document.Viewport.Top + 40);
        owner.Add(leaf); owner.Add(secondLeaf); owner.SetSelection([leaf.Id, secondLeaf.Id]); Guid inner = owner.GroupSelection("Inner")!.Value;
        owner.SetSelection([inner]); Guid outer = owner.GroupSelection("Outer")!.Value;
        Invoke("OpenLayers"); var layers = Field<LayersWindow>(desktop, "_layers"); layers.Width = 280; layers.Height = 600; layers.UpdateLayout();
        Select(outer);
        SaveCardState("selected-at-root");
        Observe("selecting a root group does not expose a group configuration card", layers.CurrentGroupContext is null && Field<Border>(layers, "groupContextSite").Visibility == Visibility.Collapsed);
        Row(outer).EnterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout();
        Observe("row entry reaches ordinary group's actual child list", owner.EditScopeId == outer && layers.VisibleNodes.Select(row => row.Id).SequenceEqual([inner]) && layers.CurrentGroupContext is { Storage: GroupStorageKind.Ordinary, IsEntered: true });
        Select(inner);
        Observe("selecting a child group keeps the card bound to the entered parent", layers.CurrentGroupContext?.Name == "Outer" && owner.EditScopeId == outer && Field<TextBlock>(layers, "groupName").Text == "Outer");
        Row(inner).EnterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout();
        Observe("entering the child replaces the card with the entered child", owner.EditScopeId == inner && layers.CurrentGroupContext?.Name == "Inner" && Field<TextBlock>(layers, "groupName").Text == "Inner");
        Field<Button>(layers, "parentScope").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout();
        Observe("returning to the parent restores its card even with the child selected", owner.EditScopeId == outer && owner.Selection.SetEquals([inner]) && layers.CurrentGroupContext?.Name == "Outer");
        Field<Button>(layers, "parentScope").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout();
        SaveCardState("returned-to-root");
        Observe("returning to root removes the card although the group left remains selected", owner.EditScopeId is null && owner.Selection.SetEquals([outer]) && layers.CurrentGroupContext is null && Field<Border>(layers, "groupContextSite").Visibility == Visibility.Collapsed);
        Row(outer).EnterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(); Select(inner);
        var choice = Field<SegmentedChoice>(layers, "groupCanvasChoice");
        SendKey(choice.Items[0], Key.Right); Pump();
        Observe("Right on canvas selector changes view without entering selected child", owner.EditScopeId == outer && layers.CurrentGroupContext?.Mode == GroupCanvasMode.RootProject);
        Snapshot("ordinary");
        owner.SetEditScope(outer); Invoke("SetGroupCanvasMode", GroupCanvasMode.RootProject); Layout(); Select(inner);
        SendKey(choice.Items[1], Key.Left); Pump();
        Observe("Left on canvas selector changes view without leaving current group", owner.EditScopeId == outer && layers.CurrentGroupContext?.Mode == GroupCanvasMode.Temporary);
        owner.SetEditScope(outer); Invoke("SetGroupCanvasMode", GroupCanvasMode.Temporary); Layout(); Select(inner);
        SendKey(Field<ListBox>(layers, "layers"), Key.Right); Pump();
        Observe("Right on layer list still enters selected group", owner.EditScopeId == inner);
        SendKey(Field<ListBox>(layers, "layers"), Key.Left); Pump();
        Observe("Left on layer list returns one scope and selects the group left", owner.EditScopeId == outer && owner.Selection.SetEquals([inner]));
        Select(inner); SendKey(Field<ListBox>(layers, "layers"), Key.F2); Layout();
        Row(inner).Editor.Text = "Inner renamed"; SendKey(Row(inner).Editor, Key.Enter); Pump();
        Observe("F2 and Enter rename an ordinary selected group without entering it", owner.Document.Groups.Single(group => group.Id == inner).Name == "Inner renamed" && owner.EditScopeId == outer && !layers.IsRenaming);

        owner.SetEditScope(null); owner.SetSelection([outer]); var shared = owner.CreatePrefabFromSelection("Shared badge"); Layout(); Select(shared.InstanceId);
        SendKey(Field<ListBox>(layers, "layers"), Key.Enter); Pump();
        var draft = editor.Session; Guid draftGroup = draft.Document.Groups.Single(group => group.ParentId is null).Id;
        Observe("list Enter opens shared instance's real detached child list", !ReferenceEquals(draft, owner) && layers.VisibleNodes.Any(row => row.Id == draftGroup) && layers.CurrentGroupContext?.Storage == GroupStorageKind.Embedded);
        Select(draftGroup); SendKey(Field<ListBox>(layers, "layers"), Key.F2); Layout();
        Row(draftGroup).Editor.Text = "Edited shared group"; SendKey(Row(draftGroup).Editor, Key.Enter); Pump();
        Observe("shared draft child rename stays detached from the owner", draft.Document.Groups.Single(group => group.Id == draftGroup).Name == "Edited shared group" && owner.Document.Definitions.Single(definition => definition.Id == shared.DefinitionId).Graph.Groups.All(group => group.Name != "Edited shared group"));
        Snapshot("embedded");
        layers.Width = 280; layers.Height = 280; Layout();
        var done = ContextButton("Prefab.Complete"); SendKey(done, Key.Enter); Pump();
        Observe("Enter on Done publishes rather than entering the selected child group", ReferenceEquals(editor.Session, owner) && owner.Document.Definitions.Single(definition => definition.Id == shared.DefinitionId).Graph.Groups.Any(group => group.Name == "Edited shared group"));
        if (!ReferenceEquals(editor.Session, owner)) Await((Task)Invoke("CompleteCurrentPrefabAsync")!);
        Layout(); Select(shared.InstanceId); Row(shared.InstanceId).EnterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
        draft = editor.Session; draftGroup = draft.Document.Groups.Single(group => group.ParentId is null).Id; Select(draftGroup);
        SendKey(ContextButton("Prefab.Cancel"), Key.Enter); Pump();
        Observe("Enter on Cancel returns an unchanged shared draft instead of entering a child", ReferenceEquals(editor.Session, owner));
        if (!ReferenceEquals(editor.Session, owner)) Await((Task)Invoke("LeaveCurrentPrefabAsync")!);

        Layout(); Select(shared.InstanceId); Row(shared.InstanceId).EnterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
        draft = editor.Session; draftGroup = draft.Document.Groups.Single(group => group.ParentId is null).Id; Select(draftGroup);
        SendKey(Field<ListBox>(layers, "layers"), Key.F2); Layout(); Row(draftGroup).Editor.Text = "Discard this draft"; SendKey(Row(draftGroup).Editor, Key.Enter); Pump();
        using (var answer = new DialogAnswer(app, "Dialog.Cancel"))
        {
            SendKey(ContextButton("Prefab.Cancel"), Key.Enter); Pump();
            Observe("cancelling the shared leave question retains the detached edit and child list", answer.Count == 1 && ReferenceEquals(editor.Session, draft) && layers.VisibleNodes.Any(row => row.Id == draftGroup) && draft.Document.Groups.Single(group => group.Id == draftGroup).Name == "Discard this draft");
        }
        using (var answer = new DialogAnswer(app, "Prefab.Discard"))
        {
            SendKey(ContextButton("Prefab.Cancel"), Key.Enter); Pump();
            Observe("discard from actual Cancel button returns without publishing shared draft changes", answer.Count == 1 && ReferenceEquals(editor.Session, owner) && owner.Document.Definitions.Single(definition => definition.Id == shared.DefinitionId).Graph.Groups.All(group => group.Name != "Discard this draft"));
        }

        string sourcePath = Path.Combine(root, "external.floatcanvas"); var source = new SceneDocument { Viewport = new(0, 0, 320, 240), Elements = [new() { Kind = ElementKind.Rectangle, Name = "External content", Width = 20, Height = 25 }] };
        var projects = new ProjectStore(); Await(projects.SaveAsync(sourcePath, source, owner.Assets)); string sourceHash = Hash(sourcePath); DateTime sourceWriteTime = File.GetLastWriteTimeUtc(sourcePath);
        var prepared = Await(new PrefabLinkResolver().PrepareAsync(owner, sourcePath, null)); var external = owner.ImportPrefab(prepared.Package, prepared.Assets, LayerPlacement.Identity, prepared.Source);
        Layout(); Select(external.InstanceId); SendKey(Field<ListBox>(layers, "layers"), Key.F2); Layout();
        Row(external.InstanceId).Editor.Text = "Renamed external instance"; SendKey(Row(external.InstanceId).Editor, Key.Enter); Pump();
        Observe("F2 and Enter rename external instance before any source navigation", ReferenceEquals(editor.Session, owner) && owner.Document.Instances.Single(instance => instance.Id == external.InstanceId).Name == "Renamed external instance");
        Select(external.InstanceId); SendKey(Field<ListBox>(layers, "layers"), Key.Enter); Wait(() => !ReferenceEquals(editor.Session, owner)); Layout();
        Observe("list Enter opens external file's actual contents and source path", !ReferenceEquals(editor.Session, owner) && layers.VisibleNodes.Single().Name == "External content" && layers.CurrentGroupContext is { Storage: GroupStorageKind.ExternalFile, IsEntered: true } context && Path.GetFullPath(context.SourcePath!) == Path.GetFullPath(sourcePath));
        Snapshot("external");
        SendKey(ContextButton("Prefab.Cancel"), Key.Enter); Wait(() => ReferenceEquals(editor.Session, owner));
        Observe("Enter on Cancel returns an unchanged external source without rewriting it", ReferenceEquals(editor.Session, owner) && Await(projects.LoadAsync(sourcePath)).Document.Elements.Single().Name == "External content");
        Observe("Cancel on an untouched external source preserves the exact source file bytes", Hash(sourcePath) == sourceHash);
        Observe("Cancel on an untouched external source does not write the file", File.GetLastWriteTimeUtc(sourcePath) == sourceWriteTime);
        string offlineSource = sourcePath + ".unavailable"; File.Move(sourcePath, offlineSource);
        int beforeOffline = owner.UndoCount;
        Layout(); Select(external.InstanceId); Row(external.InstanceId).EnterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Wait(() => editor.Session.IsReadOnly); Layout();
        var offline = editor.Session;
        Observe("missing source enters the real cached layer list with an explicit read-only notice", offline.IsReadOnly && layers.VisibleNodes.Single().IsGroup && layers.CurrentGroupContext is { IsReadOnly: true, Storage: GroupStorageKind.ExternalFile } && Field<TextBlock>(layers, "groupCanvasNotice").Text == L.Get("GroupCanvas.ReadOnlyNotice"));
        Guid cachedWrapper = layers.VisibleNodes.Single().Id; Row(cachedWrapper).EnterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout();
        Guid cachedChild = offline.Document.Elements.Single().Id; Select(cachedChild); SendKey(Field<ListBox>(layers, "layers"), Key.F2); SendKey(Field<ListBox>(layers, "layers"), Key.Delete);
        Observe("offline UI disables rename visibility lock drag and delete without changing content history", !layers.IsRenaming && !Row(cachedChild).VisibleButton.IsEnabled && !Row(cachedChild).LockedButton.IsEnabled && !layers.BeginDrag(cachedChild) && offline.Document.Elements.Single().Name == "External content" && offline.UndoCount == 0 && owner.UndoCount == beforeOffline);
        SendKey(choice.Items[0], Key.Right); Pump(); bool rootMode = layers.CurrentGroupContext?.Mode == GroupCanvasMode.RootProject;
        SendKey(choice.Items[1], Key.Left); Pump();
        Observe("offline canvas selector still changes both views without replacing its cached child list", rootMode && layers.CurrentGroupContext?.Mode == GroupCanvasMode.Temporary && ReferenceEquals(editor.Session, offline) && layers.VisibleNodes.Single().Id == cachedChild && offline.UndoCount == 0);
        SendKey(Field<ListBox>(layers, "layers"), Key.Left); Layout();
        Field<Button>(layers, "groupDetach").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Wait(() => ReferenceEquals(editor.Session, owner)); Layout();
        Observe("actual offline Detach action returns an editable ordinary group in one owner history entry", ReferenceEquals(editor.Session, owner) && !owner.IsReadOnly && owner.EditScopeId == external.InstanceId && new SceneGraph(owner.Document).FindGroup(external.InstanceId) is not null && layers.CurrentGroupContext is { Storage: GroupStorageKind.Ordinary, IsReadOnly: false } && owner.UndoCount == beforeOffline + 1);
        Observe("offline browsing and detaching preserve the unavailable source bytes", !File.Exists(sourcePath) && Hash(offlineSource) == sourceHash);
        Field<Button>(layers, "parentScope").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout();
        var missing = owner.ImportPrefab(prepared.Package, prepared.Assets, LayerPlacement.Identity, prepared.Source); Layout(); Select(missing.InstanceId);
        var beforeMissingRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMissingRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var productionResolver = Field<PrefabLinkResolver>(desktop, "_prefabLinkResolver");
        long beforeAbaVersion = owner.EditScopeVersion; int beforeAbaHistory = owner.UndoCount;
        productionResolver.BeforeReadAsync = ct => { beforeMissingRead.TrySetResult(); return releaseMissingRead.Task.WaitAsync(ct); };
        try
        {
            var pendingMissingEdit = (Task)Invoke("EditPrefabInstanceAsync", missing.InstanceId)!; Await(beforeMissingRead.Task);
            Select(external.InstanceId); Row(external.InstanceId).EnterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout();
            bool enteredB = owner.EditScopeId == external.InstanceId;
            Field<Button>(layers, "parentScope").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout();
            Observe("pending missing-source read allows actual layer scope A to B to A navigation without history", enteredB && owner.EditScopeId is null && owner.EditScopeVersion == beforeAbaVersion + 2 && owner.UndoCount == beforeAbaHistory);
            releaseMissingRead.TrySetResult(); Await(pendingMissingEdit); Layout();
            Console.WriteLine($"LAYERS_ABA_RESULT sameOwner={ReferenceEquals(editor.Session, owner)} activeReadOnly={editor.Session.IsReadOnly} scopeBefore={beforeAbaVersion} scopeAfter={owner.EditScopeVersion} currentScope={owner.EditScopeId} currentContext={layers.CurrentGroupContext?.Name}");
            Observe("a stale missing-source request cannot replace the editor after scope A to B to A", ReferenceEquals(editor.Session, owner) && !editor.Session.IsReadOnly && owner.EditScopeId is null && owner.UndoCount == beforeAbaHistory && layers.CurrentGroupContext?.IsEntered != true);
        }
        finally { productionResolver.BeforeReadAsync = null; releaseMissingRead.TrySetResult(); }
        var beforeSharedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSharedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long beforeSharedScope = owner.EditScopeVersion, beforeSharedRevision = owner.SceneRevision; int beforeSharedHistory = owner.UndoCount;
        productionResolver.BeforeReadAsync = ct => { beforeSharedRead.TrySetResult(); return releaseSharedRead.Task.WaitAsync(ct); };
        try
        {
            var pendingSharedBoundary = (Task)Invoke("EditPrefabInstanceAsync", missing.InstanceId)!; Await(beforeSharedRead.Task);
            Select(shared.InstanceId); Row(shared.InstanceId).EnterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            bool enteredShared = !ReferenceEquals(editor.Session, owner) && !editor.Session.IsReadOnly;
            SendKey(ContextButton("Prefab.Cancel"), Key.Enter); Wait(() => ReferenceEquals(editor.Session, owner)); Layout();
            Observe("pending missing-source read permits a real shared entry and unchanged Cancel without changing owner scope or history", enteredShared && ReferenceEquals(editor.Session, owner) && owner.EditScopeId is null && owner.EditScopeVersion == beforeSharedScope && owner.SceneRevision == beforeSharedRevision && owner.UndoCount == beforeSharedHistory);
            releaseSharedRead.TrySetResult(); Await(pendingSharedBoundary); Layout();
            Console.WriteLine($"LAYERS_SHARED_ABA_RESULT sameOwner={ReferenceEquals(editor.Session, owner)} activeReadOnly={editor.Session.IsReadOnly} scopeBefore={beforeSharedScope} scopeAfter={owner.EditScopeVersion} revisionBefore={beforeSharedRevision} revisionAfter={owner.SceneRevision} currentContext={layers.CurrentGroupContext?.Name}");
            Observe("a stale missing-source request cannot replace the editor after a shared context round trip", ReferenceEquals(editor.Session, owner) && !editor.Session.IsReadOnly && owner.EditScopeId is null && owner.UndoCount == beforeSharedHistory && layers.CurrentGroupContext?.IsEntered != true);
        }
        finally { productionResolver.BeforeReadAsync = null; releaseSharedRead.TrySetResult(); }
        if (!ReferenceEquals(editor.Session, owner)) { SendKey(ContextButton("Prefab.Cancel"), Key.Enter); Wait(() => ReferenceEquals(editor.Session, owner)); Layout(); }
        // This private desktop has no acquired input lease. Stage the established
        // logical host state, then exercise the real production exit/epoch boundary.
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, true);
        long beforeExitEpoch = host.EditingSessionVersion; int beforeExitHistory = owner.UndoCount;
        var beforeExitRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExitRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        productionResolver.BeforeReadAsync = ct => { beforeExitRead.TrySetResult(); return releaseExitRead.Task.WaitAsync(ct); };
        try
        {
            var pendingExitBoundary = (Task)Invoke("EditPrefabInstanceAsync", missing.InstanceId)!; Await(beforeExitRead.Task);
            bool exited = (bool)Invoke("SetEditing", false, false)!; Pump();
            Observe("production editing exit advances its host epoch and hides Layers while a missing-source read is pending", exited && !host.IsEditing && host.EditingSessionVersion > beforeExitEpoch && !layers.IsVisible);
            releaseExitRead.TrySetResult(); Await(pendingExitBoundary); Pump();
            Console.WriteLine($"LAYERS_MODE_EPOCH_RESULT sameOwner={ReferenceEquals(editor.Session, owner)} activeReadOnly={editor.Session.IsReadOnly} epochBefore={beforeExitEpoch} epochAfter={host.EditingSessionVersion} editing={host.IsEditing} layersVisible={layers.IsVisible} currentContext={layers.CurrentGroupContext?.Name}");
            Observe("a stale missing-source request cannot revive its edit context or Layers after mode exit", ReferenceEquals(editor.Session, owner) && !editor.Session.IsReadOnly && !host.IsEditing && !layers.IsVisible && owner.UndoCount == beforeExitHistory);
        }
        finally { productionResolver.BeforeReadAsync = null; releaseExitRead.TrySetResult(); }
        Observe("all workflow input stayed on the private Windows desktop", DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal));
        foreach (var (text, result) in observations) check(text, result);

        object? Invoke(string method, params object?[] arguments) => typeof(DesktopSession).GetMethod(method, Hidden)!.Invoke(desktop, arguments);
        void Layout() { layers.PrepareForShow(); layers.UpdateLayout(); Pump(); }
        void Select(Guid id) { Layout(); var list = Field<ListBox>(layers, "layers"); list.SelectedItem = layers.FindNode(id); list.ScrollIntoView(list.SelectedItem); layers.UpdateLayout(); Pump(); }
        LayerTreeRowPresenter Row(Guid id) => LayerTreeRowPresenter.Find(layers.Container(id)) ?? throw new InvalidOperationException("Current row missing: " + id);
        Button ContextButton(string key) => Field<WrapPanel>(layers, "editContext").Children.OfType<Button>().Single(button => Equals(button.Content, L.Get(key)));
        void SaveCardState(string name)
        {
            if (output is null) return;
            layers.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(layers);
            var image = new RenderTargetBitmap((int)Math.Ceiling(layers.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(layers.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); image.Render(layers);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(root, name + ".png")); encoder.Save(stream);
        }
        void Snapshot(string kind)
        {
            double previousWidth = layers.Width, previousHeight = layers.Height;
            foreach (var size in new[] { new Size(280, 280), new Size(340, 540) })
            {
            layers.Width = size.Width; layers.Height = size.Height;
            Layout();
            var viewport = Field<ScrollViewer>(layers, "headerViewport");
            bool Within(FrameworkElement control) { var rect = control.TransformToAncestor(layers).TransformBounds(new Rect(control.RenderSize)); return rect.Left >= -.5 && rect.Top >= -.5 && rect.Right <= layers.ActualWidth + .5 && rect.Bottom <= layers.ActualHeight + .5; }
            var controls = Descendants(viewport).OfType<ButtonBase>().Where(button => button.IsVisible && button.ActualWidth > 0).ToArray();
            bool reachable = true;
            foreach (var control in controls) { control.BringIntoView(); layers.UpdateLayout(); Pump(); reachable &= Within(control); }
            Observe(kind + $" actual {size.Width}x{size.Height} window keeps a usable list and scrolls every operation into view", Within(viewport) && Field<ListBox>(layers, "layers").ActualHeight >= 96 && reachable);
            if (output is null) continue;
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) }; timer.Tick += (_, _) => frame.Continue = false; timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            var dpi = VisualTreeHelper.GetDpi(layers); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(layers.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(layers.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); bitmap.Render(layers);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(root, kind + $"-window-{size.Width}x{size.Height}.png")); encoder.Save(stream);
            }
            layers.Width = previousWidth; layers.Height = previousHeight; Layout();
        }
    }
    private static void SendKey(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("Expected a real displayed WPF input route.");
        target.Focus();
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyUpEvent });
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Await(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Wait(Func<bool> complete)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) }; var frame = new DispatcherFrame(); var elapsed = System.Diagnostics.Stopwatch.StartNew();
        timer.Tick += (_, _) => { if (complete() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); try { if (!complete()) Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
    private static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) { var child = VisualTreeHelper.GetChild(root, index); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private sealed class DialogAnswer : IDisposable
    {
        private readonly DispatcherTimer timer;
        internal int Count { get; private set; }
        internal DialogAnswer(Application app, string key)
        {
            timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                var button = app.Windows.OfType<Window>().Where(window => window.IsVisible && window.Title.Contains(L.Get("Prefab.Resources"), StringComparison.Ordinal)).SelectMany(Descendants).OfType<Button>().FirstOrDefault(button => Equals(button.Content, L.Get(key)));
                if (button is null) return; timer.Stop(); Count++; button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            };
            timer.Start();
        }
        public void Dispose() => timer.Stop();
    }
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x5d11, 7, key)) continue;
            try { if (!RegisterHotKey(0, 0x5d12, 7, key + 1)) continue; UnregisterHotKey(0, 0x5d12); return (new(7, key), new(7, key + 1)); }
            finally { UnregisterHotKey(0, 0x5d11); }
        }
        throw new InvalidOperationException("No test hotkeys available.");
    }
    private static string DesktopName() { var name = new StringBuilder(256); if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); return name.ToString(); }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
