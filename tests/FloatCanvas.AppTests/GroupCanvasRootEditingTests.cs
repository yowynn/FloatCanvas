using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

/// <summary>Real desktop composition, presented picking and pointer routes on an undisplayed private desktop.</summary>
internal static partial class GroupCanvasRootEditingTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null) => RunCases(check, output, false);
    internal static void RunTemporaryRefresh(Action<string, bool> check, string? output = null) => RunCases(check, output, true);
    internal static void RunPersistentRoots(Action<string, bool> check, string? output = null) => RunCases(check, output, false, true);
    internal static void RunScopeRecovery(Action<string, bool> check, string? output = null) => RunCases(check, output, false, false, true);

    private static void RunCases(Action<string, bool> check, string? output, bool temporaryRefreshOnly, bool persistentRootsOnly = false, bool scopeRecoveryOnly = false)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal) || PresentationDesktopAvailability.IsCurrentInputDesktop())
            throw new InvalidOperationException("Use --isolated-ui-desktop --group-canvas-root-editing-only.");
        if (Application.Current is not null) throw new InvalidOperationException("Use a fresh private test process.");
        string root = Path.GetFullPath(Path.Combine(output ?? Path.GetTempPath(), "group-canvas-root-edit-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
        ApplicationPaths.Configure(["--profile", Path.Combine(root, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys(); new UserSettings { Theme = "dark", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2,
            PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var failures = new List<Exception>();
        if (scopeRecoveryOnly) { InvalidScopeRecovery(check,app,root); Console.WriteLine("SCOPE_RECOVERY_EVIDENCE " + root); return; }
        if (!temporaryRefreshOnly && !persistentRootsOnly) Case("independent 4K desktop extent", () => IndependentDesktopExtent(check, app, root));
        if (!temporaryRefreshOnly && !persistentRootsOnly) Case("CU native independent pixels", () => CuNativeIndependentPixels(check, app, root));
        if (!temporaryRefreshOnly && !persistentRootsOnly) Case("CU independent pixels", () => CuIndependentPixels(check, app, root));
        if (!temporaryRefreshOnly) Case("persistent root selector and canvas targets", () => PersistentRoots(check, app, root));
        if (!persistentRootsOnly) Case("ordinary temporary canvas refresh", () => TemporaryRefresh(check, app, root));
        if (!temporaryRefreshOnly && !persistentRootsOnly)
        {
            Case("ordinary nested group", () => Ordinary(check, app, root));
            Case("shared nested group", () => Shared(check, app, root, false));
            Case("shared nested billboard", () => Shared(check, app, root, true));
            Case("invalid scope navigation recovery", () => InvalidScopeRecovery(check, app, root));
            Case("external project source", () => External(check, app, root, false));
            Case("external prefab source", () => External(check, app, root, true));
        }
        if (failures.Count > 0) throw new AggregateException("Root group canvas editing failures.", failures);
        Console.WriteLine("GROUP_CANVAS_ROOT_EDIT_EVIDENCE " + root);
        void Case(string name, Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(error); Console.WriteLine("ROOT_CANVAS_EDIT_CASE_FAILED " + name + ": " + error.Message); }
        }
    }

    private static void IndependentDesktopExtent(Action<string, bool> check, Application app, string root)
    {
        using var fixture = new Fixture(app, Path.Combine(root, "independent-4k"), new(-24000, -23000, 3840, 2160));
        var document = new SceneDocument();
        var graph = new SceneGraph(document);
        var group = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
        graph.InsertElement(Box("Bottom right", 3500, 1900, 120, 100), group.Id, worldCoordinates: false);
        fixture.Reset(document); fixture.Editor.Session.SetEditScope(group.Id);
        typeof(DesktopSession).GetField("_groupCanvasMode", Hidden)!.SetValue(fixture.Desktop, GroupCanvasMode.Temporary);
        var view = (GroupCanvasView)fixture.Call("CurrentGroupCanvasView")!;
        var target = (CanvasSettingsTarget)fixture.Call("CaptureCanvasSettingsTarget")!;
        var expected = new RectD(-24000 - fixture.Editor.ScreenOrigin.X, -23000 - fixture.Editor.ScreenOrigin.Y, 3840, 2160);
        check("production desktop routes physical monitor bounds to independent rendering and canvas gestures",
            view.Document.Viewport == expected && target.Viewport == expected && group.PreviewCanvas is null);
    }

    private static void InvalidScopeRecovery(Action<string, bool> check, Application app, string root)
    {
        using var fixture = new Fixture(app, Path.Combine(root,"invalid-scope"));
        var document = new SceneDocument { Viewport = new(0,0,800,600) }; var graph = new SceneGraph(document);
        var a = graph.InsertGroup(new() { Name="Parent A",OrderMode=GroupOrderMode.AtomicBlock,ChildOrder=[] });
        var b = graph.InsertGroup(new() { Name="Parent B",OrderMode=GroupOrderMode.AtomicBlock,ChildOrder=[] },a.Id);
        var c = graph.InsertGroup(new() { Name="Current C",OrderMode=GroupOrderMode.AtomicBlock,ChildOrder=[] },b.Id);
        var other = graph.InsertGroup(new() { Name="Other parent",OrderMode=GroupOrderMode.AtomicBlock,ChildOrder=[] });
        foreach (var ownerId in new[] { b.Id, c.Id, other.Id })
            graph.InsertElement(Box("Navigation fixture content", 10, 10, 8, 8), ownerId, worldCoordinates: false);
        fixture.Reset(document); var session=fixture.Editor.Session;
        var toolbar=Field<Window>(fixture.Desktop,"_toolbar"); toolbar.Show(); Pump();
        fixture.Call("OpenLayers"); session.SetEditScope(c.Id); fixture.Call("SetGroupCanvasMode",GroupCanvasMode.Temporary);
        fixture.Editor.Tool=EditorTool.Canvas; fixture.Call("OpenCanvasSettings"); Pump(); fixture.Call("RefreshEditingScope");
        var layers=Field<LayersWindow>(fixture.Desktop,"_layers"); var header=Field<RootCanvasToolbarHeader>(fixture.Desktop,"_rootCanvasHeader");
        var settings=Field<CanvasSettingsWindow>(fixture.Desktop,"_canvasSettingsWindow");
        var oldSettingsTarget=(CanvasSettingsTarget)fixture.Call("CaptureCanvasSettingsTarget")!;
        var currentCrumb=Field<System.Windows.Controls.StackPanel>(layers,"breadcrumbs").Children.OfType<System.Windows.Controls.Button>()
            .Single(button=>System.Windows.Automation.AutomationProperties.GetItemStatus(button)==L.Get("ScopeNavigation.Current"));
        var oldMenu=(OverlayContextMenu)currentCrumb.ContextMenu!;
        oldMenu.PlacementTarget=currentCrumb; oldMenu.IsOpen=true;
        session.SetSelection([c.Id]); session.DeleteSelection();
        var afterDelete=((EditingScopeDescription)fixture.Call("DescribeEditingScope")!).Navigation!;
        check("deleting the current scope immediately returns all path surfaces to its nearest live parent",session.EditScopeId==b.Id &&
            Field<ScopeNavigationPath>(layers,"navigationPath").CurrentIndex==2 && afterDelete.Entries.Count==3 &&
            Field<System.Windows.Controls.TextBlock>(header,"current").Text.Contains(b.Name,StringComparison.Ordinal) && settings.Title.Contains(b.Name,StringComparison.Ordinal));
        check("scope deletion closes its old menu and invalidates the old settings gesture",!oldMenu.IsOpen && !oldSettingsTarget.IsCurrent);
        long scopeVersion=session.EditScopeVersion;
        session.MoveNodes([b.Id],other.Id);
        var moved=((EditingScopeDescription)fixture.Call("DescribeEditingScope")!).Navigation!;
        check("moving the current subtree refreshes its ancestry and invalidates old navigation epochs",session.EditScopeId==b.Id && session.EditScopeVersion>scopeVersion &&
            moved.Entries.Select(e=>e.DisplayName).SequenceEqual(new[]{L.Get("RootCanvas.Main"),other.Name,b.Name}) &&
            Field<ScopeNavigationPath>(layers,"navigationPath").Entries.Select(e=>e.Key).SequenceEqual(moved.Entries.Select(e=>e.Key)) && settings.Title.Contains(other.Name,StringComparison.Ordinal));
        var busy=typeof(DesktopSession).GetProperty("_documentBusy",Hidden)!; busy.SetValue(fixture.Desktop,true);
        try
        {
            session.ClearRootCanvas("Clear while document operation is busy");
            var path=Field<ScopeNavigationPath>(layers,"navigationPath");
            check("clearing while commands are busy refreshes read-only navigation instead of leaving a removed group",session.EditScopeId is null &&
                path.CurrentIndex==0 && path.Entries.Count==1 && layers.VisibleNodes.Count==0 && header.Visibility==Visibility.Collapsed && settings.Title==L.Get("EditingScope.MainSettings"));
        }
        finally { busy.SetValue(fixture.Desktop,false); }
        session.Undo(); session.SetEditScope(b.Id); Pump();
        var stale=((EditingScopeDescription)fixture.Call("DescribeEditingScope")!).Navigation!.Entries[^1];
        var fileState=fixture.Call("FileState",session)!;
        fileState.GetType().GetField("Path")!.SetValue(fileState,Path.Combine(root,"previously-linked.floatcanvas"));
        fixture.Call("RefreshBoundFileIndicator");
        var file=Field<BoundFileIndicator>(fixture.Desktop,"_boundFileIndicator");
        check("linked file is visible before replacing its canvas",file.Visibility==Visibility.Visible);
        var fresh=new EditorSession(new SceneDocument { Viewport=new(0,0,800,600) });
        typeof(DesktopSession).GetField("_live",Hidden)!.SetValue(fixture.Desktop,fresh);
        fixture.Editor.ReplaceSession(fresh,fixture.Editor.ScreenOrigin);
        var reset=Field<ScopeNavigationPath>(layers,"navigationPath");
        check("new canvas session immediately clears every old path and historical descendant",!stale.Available && reset.CurrentIndex==0 && reset.Entries.Count==1 &&
            layers.VisibleNodes.Count==0 && header.Visibility==Visibility.Collapsed && file.Visibility==Visibility.Collapsed && settings.Title==L.Get("EditingScope.MainSettings"));
        Pump();
    }

    private static void PersistentRoots(Action<string, bool> check, Application app, string root)
    {
        using var fixture = new Fixture(app, Path.Combine(root, "persistent-roots"));
        var main = Box("Main artwork", 60, 60, 60, 40); var foreground = Box("Foreground artwork", 60, 60, 60, 40); var background = Box("Background artwork", 60, 60, 60, 40);
        var document = new SceneDocument { Viewport = new(0, 0, 800, 600), Elements = [main, foreground, background] };
        SceneCanvasRoots.Assign(document, foreground.Id, RootCanvasKind.Foreground); SceneCanvasRoots.Assign(document, background.Id, RootCanvasKind.Background);
        fixture.Reset(document); fixture.Call("OpenLayers"); Pump();
        var layers = Field<LayersWindow>(fixture.Desktop, "_layers"); var session = fixture.Editor.Session;
        var roots = Field<SegmentedChoice>(layers, "rootCanvasChoice"); var modes = Field<CanvasModeSwitch>(layers, "groupCanvasChoice");
        check("root canvas view selector is present by default with RootProject selected and no group actions", layers.CurrentGroupContext is { IsRootContext: true, Mode: GroupCanvasMode.RootProject, Export: null, Detach: null });
        int history = session.UndoCount;
        foreach (var rootCanvas in Enum.GetValues<RootCanvasKind>())
        {
            roots.SelectedValue = rootCanvas; Pump(); if (modes.Mode != GroupCanvasMode.Temporary) modes.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump();
            using (var isolated = (CanvasPreviewSnapshot)fixture.Call("CaptureCanvasPreview")!)
                check(rootCanvas + " actual root selector command enters its independent canvas", layers.CurrentGroupContext is { IsRootContext: true, Mode: GroupCanvasMode.Temporary } &&
                    session.ActiveRootCanvas == rootCanvas && SceneEvaluation.Build(isolated.Document).Leaves.Count == 1);
            if (modes.Mode != GroupCanvasMode.RootProject) modes.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump();
            using (var whole = (CanvasPreviewSnapshot)fixture.Call("CaptureCanvasPreview")!)
                check(rootCanvas + " actual root selector command restores all roots without dirtying history", layers.CurrentGroupContext?.Mode == GroupCanvasMode.RootProject &&
                    SceneEvaluation.Build(whole.Document).Leaves.Count == 3 && session.UndoCount == history);
        }
        roots.SelectedValue = RootCanvasKind.Foreground; Pump(); if (modes.Mode != GroupCanvasMode.Temporary) modes.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump();
        fixture.Call("ChangeCanvas", (Action<CanvasSettings>)(settings => settings.BackgroundColor = 0xff123456));
        fixture.Call("SetCanvasProjection", true);
        var opacity = Field<ToolbarOpacitySlider>(fixture.Desktop, "_opacitySlider"); opacity.Value = .45; Pump();
        check("root isolation routes canvas callbacks and opacity to the shared authored header", session.Document.Canvas is
            { BackgroundColor: 0xff123456, Projection.Enabled: true } && session.Document.LayerOpacity == .45 && !session.Document.RootPreviewCanvases.ContainsKey(RootCanvasKind.Foreground));
        if (modes.Mode != GroupCanvasMode.RootProject) modes.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(); roots.SelectedValue = RootCanvasKind.Main; Pump();
        fixture.Call("ChangeCanvas", (Action<CanvasSettings>)(settings => settings.BackgroundColor = 0xff345678)); opacity.Value = .8; Pump();
        check("real root-project mode uses the same authored header without a second foreground header", session.Document.Canvas.BackgroundColor == 0xff345678 && session.Document.LayerOpacity == .8 &&
            !session.Document.RootPreviewCanvases.ContainsKey(RootCanvasKind.Foreground));
        session.Add(Box("Group artwork", 40, 50, 60, 40)); var group = session.GroupSelection("Independent group")!.Value; session.SetEditScope(group); Pump();
        if (modes.Mode != GroupCanvasMode.Temporary) modes.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump();
        fixture.Call("ChangeCanvas", (Action<CanvasSettings>)(settings => settings.BackgroundColor = 0xff56789a)); fixture.Call("SetCanvasProjection", true); opacity.Value = .3; Pump();
        check("entered ordinary group routes real canvas and toolbar callbacks to its own independent header", session.Document.Groups.Single(item => item.Id == group).PreviewCanvas is
            { Canvas.BackgroundColor: 0xff56789a, Canvas.Projection.Enabled: true, LayerOpacity: .3 } && session.Document.Canvas.BackgroundColor == 0xff345678 && session.Document.LayerOpacity == .8);
        session.ChangeEditingSubprojectCanvas("mirrored independent view", header =>
        {
            var projection = header.Canvas.Projection;
            projection.Matrix = Projective2D.FromAffine(Affine2D.Translation(700, 0).Compose(Affine2D.Scale(-1, 1)));
            projection.Quad = EditorController.Corners(CanvasProjectionGeometry.ResolvePlaneBounds(projection)).Select(projection.Matrix.TransformPoint).ToList();
        });
        var camera = JsonSerializer.Serialize(session.GetEditingSubprojectCanvas()); history = session.UndoCount;
        fixture.Call("ActivateTool", EditorTool.Canvas); Pump();
        check("opening Canvas settings preserves an existing mirrored independent preview without authoring a reset", JsonSerializer.Serialize(session.GetEditingSubprojectCanvas()) == camera && session.UndoCount == history);
    }

    private static void CuIndependentPixels(Action<string, bool> check, Application app, string root)
    {
        using var fixture = new Fixture(app, Path.Combine(root, "cu-independent-pixels"));
        fixture.Reset(CuIndependentDocument()); var session = fixture.Editor.Session;
        fixture.Call("OpenLayers"); Pump(); var layers = Field<LayersWindow>(fixture.Desktop, "_layers");
        var modes = Field<CanvasModeSwitch>(layers, "groupCanvasChoice"); var roots = Field<SegmentedChoice>(layers, "rootCanvasChoice");
        using var initial = fixture.PresentRefresh("cu-root-project");
        Sample("root-project", initial, "QA red child", 0xffe53e3e);
        Sample("root-project", initial, "QA blue nested", 0xff2366dd); Sample("root-project", initial, "QA main outside", 0xff20ac65);
        if (modes.Mode != GroupCanvasMode.Temporary) modes.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(); using var main = fixture.PresentRefresh("cu-independent-main");
        check("pure Main mode switch refreshes the keyboard display plan before capturing its frame", ReferenceEquals(
            Field<SceneDocument>(fixture.Desktop, "_presentationDisplayDocument"), ((GroupCanvasView)fixture.Call("CurrentGroupCanvasView")!).Document));
        Sample("independent Main", main, "QA red child", 0xffe53e3e);
        Sample("independent Main", main, "QA blue nested", 0xff2366dd); Sample("independent Main", main, "QA main outside", 0xff20ac65);
        roots.SelectedValue = RootCanvasKind.Foreground; Pump(); using var foreground = fixture.PresentRefresh("cu-independent-foreground");
        Sample("independent Foreground", foreground, "QA foreground", 0xffffcc22);
        check("independent Foreground omits the Main keyboard guide", Field<IReadOnlyList<ResolvedLeaf>>(fixture.Desktop, "_presentationLeaves").Count == 0);

        void Sample(string name, RefreshRendered frame, string layer, uint color)
        {
            var leaf = SceneEvaluation.Build(frame.Content.Document).Leaves.Single(item => item.RenderElement.Name == layer);
            var element = leaf.RenderElement;
            var center = CanvasMapping.CreateViewing(frame.Content.Document, frame.Content.View, leaf.RootCanvas).ElementToScreen(element).TransformPoint(new(element.Width / 2, element.Height / 2)) + frame.Content.Origin;
            Console.WriteLine($"CU_QA_PIXEL {name} viewport={frame.Content.Document.Viewport} opacity={frame.Content.Document.LayerOpacity} point={center} direct={ColorAt(frame.Direct.Pixels, center)} worker={ColorAt(frame.Content.Pixels, center)} expected={new SKColor(color)}");
            check(name + " keeps direct author pixels", ColorAt(frame.Direct.Pixels, center) == new SKColor(color));
            check(name + " keeps worker author pixels", ColorAt(frame.Content.Pixels, center) == new SKColor(color));
        }
    }

    private static SceneDocument CuIndependentDocument()
    {
        CanvasSettings Perspective()
        {
            var canvas = new CanvasSettings(); var projection = canvas.Projection;
            projection.Enabled = true; projection.PlaneBounds = projection.ReferenceBounds = new RectD(0, 0, 1200, 800);
            projection.Matrix = new(1, .12, 60, .03, 1, 90, 0, .0002, 1);
            projection.Quad = EditorController.Corners(projection.PlaneBounds.Value).Select(projection.Matrix.TransformPoint).ToList(); return canvas;
        }
        CanvasElement Rectangle(string name, uint color, double x, double y, double width = 150, double height = 90) => new()
        {
            Name = name, Kind = ElementKind.Rectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y),
            Style = new() { StrokeWidth = 0, Variant = "shape.independent", Background = color, Foreground = color }
        };
        var outer = new CanvasGroup { Name = "QA Outer", Transform = Affine2D.Translation(300, 180), OrderMode = GroupOrderMode.AtomicBlock,
            ChildOrder = [], PreviewCanvas = new() { Viewport = new(0, 0, 1200, 800) } };
        var inner = new CanvasGroup { Name = "QA Inner", ParentId = outer.Id, Transform = Affine2D.Translation(240, 60), OrderMode = GroupOrderMode.AtomicBlock,
            ChildOrder = [], PreviewCanvas = new() { Viewport = new(0, 0, 1200, 800) } };
        var red = Rectangle("QA red child", 0xffe53e3e, 160, 160); red.GroupId = outer.Id;
        var blue = Rectangle("QA blue nested", 0xff2366dd, 150, 220, 120, 100); blue.GroupId = inner.Id;
        var green = Rectangle("QA main outside", 0xff20ac65, 850, 400);
        var foreground = Rectangle("QA foreground", 0xffffcc22, 800, 220, 190, 110); var background = Rectangle("QA background", 0xff7755cc, 100, 600, 220, 120);
        var keyboard = new CanvasElement { Kind = ElementKind.Presentation, Width = 700, Height = 210, Transform = Affine2D.Translation(350, 680),
            Presentation = new() { DisplayStyle = PresentationDisplayStyle.Keyboard, KeyboardScope = PresentationKeyboardScope.OutsideFloatCanvas } };
        outer.ChildOrder = [red.Id, inner.Id]; inner.ChildOrder = [blue.Id];
        var document = new SceneDocument { Viewport = new(0, 0, 1920, 1080), Canvas = Perspective(), Groups = [outer, inner],
            Elements = [red, blue, green, foreground, background, keyboard], RootOrder = [outer.Id, green.Id, foreground.Id, background.Id, keyboard.Id] };
        SceneCanvasRoots.Assign(document, foreground.Id, RootCanvasKind.Foreground); SceneCanvasRoots.Assign(document, background.Id, RootCanvasKind.Background);
        document.RootPreviewCanvases[RootCanvasKind.Foreground] = new() { Canvas = Perspective(), Viewport = document.Viewport };
        return document;
    }

    private static void CuNativeIndependentPixels(Action<string, bool> check, Application app, string root)
    {
        using var fixture = new Fixture(app, Path.Combine(root, "cu-native-independent-pixels"));
        Field<PresentationRuntimeBroker>(fixture.Desktop, "_presentationRuntime").Dispose();
        var runtime = new PresentationRuntimeBroker(app.Dispatcher, () => fixture.Call("OnPresentationRuntimeChanged"),
            (epoch, _, _, _) => Task.FromResult<IPresentationInputSource>(new CuInputSource(epoch)));
        Set(fixture.Desktop, "_presentationRuntime", runtime); Set(fixture.Desktop, "_presentationDesktopAvailable", (Func<bool>)(() => true));
        fixture.Reset(CuIndependentDocument()); fixture.Editor.Tool = EditorTool.Freehand; fixture.Editor.Session.SetSelection([]);
        fixture.Call("InitializePresentation"); Pump();
        check("native CU repro uses a running keyboard broker without observing the real input desktop", runtime.ObservingKeyboard);
        fixture.Call("OpenLayers"); Pump(); var layers = Field<LayersWindow>(fixture.Desktop, "_layers");
        var modes = Field<CanvasModeSwitch>(layers, "groupCanvasChoice");
        using var initial = fixture.PresentNative("cu-native-root-project");
        Sample("native RootProject", initial);
        using var workerStarted = new ManualResetEventSlim(); using var workerRelease = new ManualResetEventSlim();
        using var barrierRenderer = new CanvasPreviewRenderer();
        var pump = Field<CanvasPreviewPump>(fixture.Desktop, "_canvasPreview"); int block = 1;
        typeof(CanvasPreviewPump).GetField("renderOverride", Hidden)!.SetValue(pump, (Func<CanvasPreviewSnapshot, CanvasPreviewFrame>)(snapshot =>
        {
            if (Interlocked.Exchange(ref block, 0) == 1) { workerStarted.Set(); workerRelease.Wait(); }
            return barrierRenderer.Render(snapshot);
        }));
        try
        {
            if (modes.Mode != GroupCanvasMode.Temporary) modes.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!workerStarted.IsSet)
            {
                if (timeout.ElapsedMilliseconds > 10000) throw new TimeoutException("Mode transition did not reach its pending native frame.");
                Pump(); Thread.Yield();
            }
            check("pending navigation retains the previously presented pixels without a presentation failure", !Field<bool>(fixture.Desktop, "_presentationUncertain"));
            var previousLifetime = runtime.Lifetime; long requested = pump.RequestedSequence;
            runtime.ClearKeyboardForFocusChange();
            Pump();
            check("the focus privacy transition changes keyboard lifetime while the native mode frame is unavailable", !ReferenceEquals(previousLifetime, runtime.Lifetime) &&
                pump.RequestedSequence > requested);
        }
        finally { workerRelease.Set(); }
        using var isolated = fixture.PresentNative("cu-native-main-independent", requestRender: false);
        Sample("native Main Independent", isolated);
        check("native first Main Independent preserves Freehand and no selection", fixture.Editor.Tool == EditorTool.Freehand && fixture.Editor.Session.Selection.Count == 0);

        void Sample(string name, Rendered frame)
        {
            foreach (var (layer, color) in new[] { ("QA red child", 0xffe53e3eU), ("QA blue nested", 0xff2366ddU), ("QA main outside", 0xff20ac65U) })
            {
                var center = Center(frame, layer);
                Console.WriteLine($"CU_NATIVE_PIXEL {name} {layer} point={center} color={ColorAt(frame.Pixels, center)} expected={new SKColor(color)}");
                check(name + " submits " + layer + " to the actual OverlayHost DIB", ColorAt(frame.Pixels, center) == new SKColor(color));
            }
        }
    }

    private sealed class CuInputSource(long epoch) : IPresentationInputSource
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Epoch => epoch;
        public bool IsRunning { get; private set; } = true;
        public Task Completion => completion.Task;
        public event Action? Available { add { } remove { } }
        public PresentationObservedBatch Drain() => new(epoch, [], null, false);
        public bool Stop() { IsRunning = false; completion.TrySetResult(); return true; }
        public void Dispose() => Stop();
    }

    private static void Shared(Action<string, bool> check, Application app, string root, bool billboard)
    {
        string kind = billboard ? "shared-billboard" : "shared-plane";
        using var fixture = new Fixture(app, Path.Combine(root, kind));
        var innerGroup = new CanvasGroup { Name = "Inside definition", Transform = Affine2D.Translation(25, 30).Compose(Affine2D.Rotation(-8)), ElevationScale = 1.25,
            OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var leaf = Box(kind, 20, 25, 90, 60); leaf.GroupId = innerGroup.Id;
        if (billboard) { leaf.IsBillboard = true; leaf.BillboardAnchor = new(65, 145); leaf.BillboardHeight = 55; }
        innerGroup.ChildOrder = [leaf.Id];
        var childDefinition = new PrefabDefinition { Name = "Nested source", Graph = new() { Groups = [innerGroup], Elements = [leaf], RootOrder = [innerGroup.Id] } };
        var nestedInstance = new PrefabInstance { DefinitionId = childDefinition.Id, Name = "Nested instance", Transform = Affine2D.Translation(30, 15).Compose(Affine2D.Rotation(13)), ElevationScale = .9 };
        var outerDefinition = new PrefabDefinition { Name = "Outer source", Graph = new() { Instances = [nestedInstance], RootOrder = [nestedInstance.Id] } };
        var first = new PrefabInstance { DefinitionId = outerDefinition.Id, Name = "Occurrence A", Transform = Affine2D.Translation(100, 100).Compose(Affine2D.Scale(1.3, .8)), ElevationScale = 1.15 };
        var second = new PrefabInstance { DefinitionId = outerDefinition.Id, Name = "Occurrence B", Transform = Affine2D.Translation(470, 180).Compose(Affine2D.Scale(.7, .8)), ElevationScale = .85 };
        var document = new SceneDocument { Viewport = new(0, 0, 800, 600), Definitions = [outerDefinition, childDefinition], Instances = [first, second], RootOrder = [first.Id, second.Id] };
        Perspective(document); fixture.Reset(document); var owner = fixture.Editor.Session;
        string ownerBefore = JsonSerializer.Serialize(owner.Document); int ownerHistory = owner.UndoCount;
        Await((Task)fixture.Call("EditPrefabInstanceAsync", first.Id)!);
        fixture.Editor.ScreenOrigin += new PointD(41, -27);
        Await((Task)fixture.Call("EditPrefabInstanceAsync", nestedInstance.Id)!);
        fixture.Editor.ScreenOrigin += new PointD(46, 63);
        var active = fixture.Editor.Session; active.SetEditScope(innerGroup.Id); active.SetSelection([leaf.Id]);
          fixture.Call("RefreshEditingScope");
          var enteredPath = ((EditingScopeDescription)fixture.Call("DescribeEditingScope")!).Navigation!;
          enteredPath.Entries[0].Navigate(); Pump();
          var returnedPath = ((EditingScopeDescription)fixture.Call("DescribeEditingScope")!).Navigation!;
          check(kind + " navigation to the document root retains both shared boundaries and deeper scope", ReferenceEquals(fixture.Editor.Session, owner) &&
              returnedPath.CurrentIndex == 0 && returnedPath.Entries.Select(e => e.Key).SequenceEqual(enteredPath.Entries.Select(e => e.Key)) &&
              returnedPath.Entries.Skip(1).All(e => e.Available && !e.Local));
          returnedPath.Entries[^1].Navigate(); Pump();
          active = fixture.Editor.Session;
          var resumedPath = ((EditingScopeDescription)fixture.Call("DescribeEditingScope")!).Navigation!;
          check(kind + " forward navigation reopens nested shared contexts and restores the real local group", !ReferenceEquals(active, owner) && active.EditScopeId == innerGroup.Id &&
              resumedPath.CurrentIndex == enteredPath.CurrentIndex && resumedPath.Entries[^1].Local);
          check(kind + " shared branch navigation does not publish unchanged drafts or alter owner history", owner.UndoCount == ownerHistory && JsonSerializer.Serialize(owner.Document) == ownerBefore);
          active.SetSelection([leaf.Id]);
        fixture.Call("SetGroupCanvasMode", GroupCanvasMode.Temporary);
        using var temporary = fixture.Present(kind + "-temporary");
        check(kind + " temporary control displays the current nested source selection", HasBlue(temporary.Pixels, Edge(temporary, kind)));
        fixture.Call("SetGroupCanvasMode", GroupCanvasMode.RootProject);
        CanvasFeedback(check, fixture, owner, kind);
        using var before = fixture.Present(kind + "-root-selection");
        check(kind + " root selection frame uses the entered owner's nested placement and perspective", HasBlue(before.Pixels, Edge(before, kind, first.Id)));
        check(kind + " root view leaves its true nested editing session and scope active", !ReferenceEquals(active, owner) && ReferenceEquals(fixture.Editor.Session, active) && active.EditScopeId == innerGroup.Id);
        active.SetSelection([]); fixture.PresentAndDispose(kind + "-before-hit"); var center = Center(before, kind, first.Id);
        var grab = billboard ? Map(before, kind, .2, .5, first.Id) : center;
        fixture.Pointer(OverlayPointerKind.Down, grab); fixture.Pointer(OverlayPointerKind.Up, grab);
        check(kind + " owner occurrence hit resolves the real source child rather than the host instance", active.Selection.SetEquals([leaf.Id]));
        string draftBefore = JsonSerializer.Serialize(active.Document); int history = active.UndoCount; var delta = new PointD(32, 17);
        fixture.Pointer(OverlayPointerKind.Down, grab); fixture.Pointer(OverlayPointerKind.Move, grab + delta); fixture.Pointer(OverlayPointerKind.Up, grab + delta);
        using var moved = fixture.Present(kind + "-root-moved");
        check(kind + " drag follows the grabbed root screen point with parent scale and elevation exactly once", (billboard ? Map(moved, kind, .2, .5, first.Id) : Center(moved, kind, first.Id)).DistanceTo(grab + delta) < 1e-5);
        check(kind + " draft drag is one exact source undo while the publication owner stays unchanged", active.UndoCount == history + 1 && JsonSerializer.Serialize(owner.Document) == ownerBefore &&
            owner.UndoCount == ownerHistory && active.Undo() && JsonSerializer.Serialize(active.Document) == draftBefore && active.Redo());
        var movedA = Center(moved, kind, first.Id); var movedB = Center(moved, kind, second.Id);
        check(kind + " staged edit synchronizes both outer occurrences before publication", movedB.DistanceTo(Center(before, kind, second.Id)) > 1 &&
            JsonSerializer.Serialize(owner.Document) == ownerBefore);
        if (!billboard)
        {
            TransformHandles(check, fixture, kind, first.Id);
            using var beforeNudge = fixture.Present(kind + "-before-nudge");
            var corners = PlaneCorners(beforeNudge, kind, first.Id); var nudge = new PointD(3, 2);
            string beforeNudgeDocument = JsonSerializer.Serialize(active.Document); int beforeNudgeHistory = active.UndoCount;
            fixture.Editor.TransformSelectionInCanvas(Affine2D.Translation(nudge.X, nudge.Y));
            using var nudged = fixture.Present(kind + "-root-nudged");
            check(kind + " keyboard transform seam translates every root-plane corner by 3,2", corners.Select(point => point + nudge).Zip(PlaneCorners(nudged, kind, first.Id)).All(pair => pair.First.DistanceTo(pair.Second) < 1e-5));
            check(kind + " keyboard transform is one exact source undo and redo", active.UndoCount == beforeNudgeHistory + 1 && active.Undo() && JsonSerializer.Serialize(active.Document) == beforeNudgeDocument && active.Redo() && active.Undo());
            fixture.PresentAndDispose(kind + "-after-nudge-undo");
        }
        Await((Task)fixture.Call("CompleteCurrentPrefabAsync")!); Await((Task)fixture.Call("CompleteCurrentPrefabAsync")!);
        check(kind + " publishing all parent drafts restores the owner and retains both real root placements", ReferenceEquals(fixture.Editor.Session, owner) &&
            owner.Document.Instances.Single(instance => instance.Id == first.Id).Transform == first.Transform && owner.Document.Instances.Single(instance => instance.Id == second.Id).Transform == second.Transform);
        using var committed = fixture.Present(kind + "-published");
        check(kind + " publishing does not shift either synchronized occurrence", Center(committed, kind, first.Id).DistanceTo(movedA) < 1e-5 && Center(committed, kind, second.Id).DistanceTo(movedB) < 1e-5);
        string path = Path.Combine(root, kind + ".floatcanvas"); var store = new ProjectStore(); Await(store.SaveAsync(path, owner.Document, owner.Assets));
        var reopened = Await(store.LoadAsync(path));
        check(kind + " saved owner retains the shared source geometry and both nested instance paths", JsonSerializer.Serialize(reopened.Document) == JsonSerializer.Serialize(owner.Document) &&
            SceneEvaluation.Build(reopened.Document).OccurrencesOfHostNode(first.Id).Count == 1 && SceneEvaluation.Build(reopened.Document).OccurrencesOfHostNode(second.Id).Count == 1);
    }

    private static void External(Action<string, bool> check, Application app, string root, bool prefab)
    {
        string kind = prefab ? "external-prefab" : "external-project";
        using var fixture = new Fixture(app, Path.Combine(root, kind));
        var source = new SceneDocument { Viewport = new(0, 0, 800, 600) }; var sourceGraph = new SceneGraph(source);
        var group = sourceGraph.InsertGroup(new() { Name = "Source offset group", Transform = Affine2D.Translation(140, 95).Compose(Affine2D.Rotation(10)), OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
        var leaf = sourceGraph.InsertElement(Box("external-plane", 25, 40, 105, 70), group.Id, worldCoordinates: false);
        var store = new ProjectStore(); var prefabStore = new PrefabStore(); PointD pivot = new(50, 35);
        string path = Path.Combine(root, prefab ? "External offset source.floatprefab" : "External offset source.floatcanvas");
        if (prefab)
        {
            var entry = new PrefabDefinition { Name = "Original source entry", Pivot = pivot, Graph = SceneGraphClone.CloneGraph(source).Graph };
            Await(prefabStore.SaveAsync(path, new() { EntryDefinitionId = entry.Id, Definitions = [entry] }, fixture.Editor.Session.Assets));
        }
        else Await(store.SaveAsync(path, source, fixture.Editor.Session.Assets));
        var ownerDocument = new SceneDocument { Viewport = new(0, 0, 800, 600) }; Perspective(ownerDocument); fixture.Reset(ownerDocument);
        var owner = fixture.Editor.Session; var resolver = new PrefabLinkResolver();
        var candidate = Await(resolver.PrepareAsync(owner, path, null));
        var imported = owner.ImportPrefab(candidate.Package, candidate.Assets, new(Affine2D.Translation(120, 120).Compose(Affine2D.Scale(1.1, 1)), 1.1), candidate.Source);
        Guid second = owner.PlacePrefab(imported.DefinitionId, new(Affine2D.Translation(440, 240).Compose(Affine2D.Scale(.7, .8)), .8));
        string ownerBefore = JsonSerializer.Serialize(owner.Document), diskBefore = Hash(path);
        Await((Task)fixture.Call("EditPrefabInstanceAsync", imported.InstanceId)!);
        fixture.Editor.ScreenOrigin += new PointD(53, 28);
        var active = fixture.Editor.Session; active.SetEditScope(group.Id); active.SetSelection([leaf.Id]);
        fixture.Call("SetGroupCanvasMode", GroupCanvasMode.RootProject);
        var externalBranch = ((EditingScopeDescription)fixture.Call("DescribeEditingScope")!).Navigation!;
        externalBranch.Entries[0].Navigate(); Pump();
        var returnedBranch = ((EditingScopeDescription)fixture.Call("DescribeEditingScope")!).Navigation!;
        check(kind + " returning to root retains the available external group history", ReferenceEquals(fixture.Editor.Session, owner) &&
            returnedBranch.CurrentIndex == 0 && returnedBranch.Entries.Count == externalBranch.Entries.Count && returnedBranch.Entries[^1].Available);
        returnedBranch.Entries[^1].Navigate();
        var navigationDeadline = DateTime.UtcNow.AddSeconds(10);
        while ((ReferenceEquals(fixture.Editor.Session, owner) || fixture.Editor.Session.EditScopeId != group.Id) && DateTime.UtcNow < navigationDeadline)
        { Pump(); Thread.Sleep(5); }
        active = fixture.Editor.Session;
        check(kind + " history reopens the read-only external group without changing host or source", !ReferenceEquals(active, owner) &&
            active.IsReadOnly && active.EditScopeId == group.Id && JsonSerializer.Serialize(owner.Document) == ownerBefore && Hash(path) == diskBefore);
        active.SetSelection([leaf.Id]);
        CanvasFeedback(check, fixture, owner, kind);
        using var before = fixture.Present(kind + "-root-readonly");
        var center = Center(before, "external-plane", imported.InstanceId);
        string sourceBefore = JsonSerializer.Serialize(active.Document); int history = active.UndoCount;
        check(kind + " source navigation is read-only with its imported origin retained", active.IsReadOnly && candidate.Source.ImportOrigin != default);
        bool rejected = false;
        try { active.Execute("forbidden source edit", document => document.Elements.Clear()); }
        catch (InvalidOperationException) { rejected = true; }
        check(kind + " source content commands reject authoring", rejected && JsonSerializer.Serialize(active.Document) == sourceBefore);
        fixture.Pointer(OverlayPointerKind.Down, center);
        fixture.Pointer(OverlayPointerKind.Move, center + new PointD(28, 19));
        fixture.Pointer(OverlayPointerKind.Up, center + new PointD(28, 19));
        using var afterDrag = fixture.Present(kind + "-readonly-after-drag");
        check(kind + " source pointer drag preserves pixels content history host and disk", Center(afterDrag, "external-plane", imported.InstanceId).DistanceTo(center) < 1e-5 &&
            JsonSerializer.Serialize(active.Document) == sourceBefore && active.UndoCount == history && Hash(path) == diskBefore && JsonSerializer.Serialize(owner.Document) == ownerBefore);
        active.SetEditScope(null); active.SetSelection([]);
        fixture.Call("SetGroupCanvasMode", GroupCanvasMode.Temporary);
        var view = new CanvasViewTransform(.75, new(80, 60)); fixture.Call("ChangeCanvasViewing", view, false);
        fixture.Call("ApplyCanvasViewing");
        check(kind + " independent source navigation remains display-only and Apply is blocked", Field<CanvasViewTransform>(fixture.Desktop, "_canvasView") == view &&
            JsonSerializer.Serialize(active.Document) == sourceBefore && active.UndoCount == history && Hash(path) == diskBefore);
        Await((Task)fixture.Call("CompleteCurrentPrefabAsync")!);
        check(kind + " returning from source browsing writes neither host nor external file", ReferenceEquals(fixture.Editor.Session, owner) &&
            JsonSerializer.Serialize(owner.Document) == ownerBefore && Hash(path) == diskBefore);
        Await((Task)fixture.Call("EditPrefabInstanceAsync", imported.InstanceId)!);
        {
            fixture.Call("SetGroupCanvasMode", GroupCanvasMode.Temporary); fixture.PresentAndDispose(kind + "-online-view");
            var onlineView = new CanvasViewTransform(.75, new(60, 45)); fixture.Call("ChangeCanvasViewing", onlineView, false);
            string onlineKey = (string)fixture.Call("CanvasViewingKey")!;
            fixture.Call("ReturnFromPrefabContext", Field<PrefabEditContext>(fixture.Desktop, "_prefabEditContext"));
            string ownerBeforeOffline = JsonSerializer.Serialize(owner.Document);
            string hiddenPath = path + ".temporarily-offline"; File.Move(path, hiddenPath);
            try
            {
                Await((Task)fixture.Call("EditPrefabInstanceAsync", imported.InstanceId)!);
                fixture.Call("SetGroupCanvasMode", GroupCanvasMode.Temporary);
                fixture.PresentAndDispose(kind + "-offline-view");
                var offlineView = Field<CanvasViewTransform>(fixture.Desktop, "_canvasView");
                Console.WriteLine($"OFFLINE_VIEW kind={kind} readonly={fixture.Editor.Session.IsReadOnly} expectedKey={onlineKey} actualKey={fixture.Call("CanvasViewingKey")} expected={onlineView} actual={offlineView}");
                check(kind + " missing source restores the same independent observation in a read-only snapshot", fixture.Editor.Session.IsReadOnly &&
                    (string)fixture.Call("CanvasViewingKey")! == onlineKey && offlineView.Zoom == onlineView.Zoom && offlineView.Offset.DistanceTo(onlineView.Offset) < 1e-7);
                string snapshot = JsonSerializer.Serialize(fixture.Editor.Session.Document); int undo = fixture.Editor.Session.UndoCount;
                fixture.Call("ExecuteCanvasViewing", CanvasViewingCommand.CenterContent); fixture.Call("ApplyCanvasViewing");
                check(kind + " offline navigation and blocked apply preserve both snapshot and host content", snapshot == JsonSerializer.Serialize(fixture.Editor.Session.Document) &&
                    undo == fixture.Editor.Session.UndoCount && ownerBeforeOffline == JsonSerializer.Serialize(owner.Document));
                Await((Task)fixture.Call("CompleteCurrentPrefabAsync")!);
            }
            finally { File.Move(hiddenPath, path); }
            Await((Task)fixture.Call("EditPrefabInstanceAsync", imported.InstanceId)!);
            fixture.Call("SetGroupCanvasMode", GroupCanvasMode.Temporary); fixture.PresentAndDispose(kind + "-online-return");
            check(kind + " restored source keeps the observation last used in its offline snapshot", (string)fixture.Call("CanvasViewingKey")! == onlineKey &&
                Field<CanvasViewTransform>(fixture.Desktop, "_canvasView").Zoom == onlineView.Zoom);
        }
        var externalContext = Field<PrefabEditContext>(fixture.Desktop, "_prefabEditContext");
        Await((Task)fixture.Call("EmbedEditingGroupAsync", externalContext)!);
        var embedded = fixture.Editor.Session;
        check(kind + " explicit internal conversion enables editing without writing the external source", !embedded.IsReadOnly && Hash(path) == diskBefore);
        embedded.Execute("edit embedded source", document => document.Elements.Single(element => element.Name == "external-plane").Width += 13);
        Await((Task)fixture.Call("CompleteCurrentPrefabAsync")!);
        check(kind + " internal edits publish to the owner while preserving the external file", ReferenceEquals(fixture.Editor.Session, owner) && Hash(path) == diskBefore &&
            owner.Document.Definitions.Single(definition => definition.Id == imported.DefinitionId).Source?.IsLinked != true &&
            SceneEvaluation.Build(owner.Document).Leaves.Any(item => item.RenderElement.Name == "external-plane" && item.RenderElement.Width == leaf.Width + 13));
    }

    private static void Perspective(SceneDocument document)
    {
        var projection = CanvasCalibration.CreateInitial(document.Viewport); projection.Enabled = true;
        projection.Matrix = new(1, .08, 18, .025, .95, 12, .00012, .00035, 1);
        var plane = CanvasProjectionGeometry.ResolvePlaneBounds(projection);
        projection.Quad = new[] { new PointD(plane.Left, plane.Top), new PointD(plane.Right, plane.Top),
            new PointD(plane.Right, plane.Bottom), new PointD(plane.Left, plane.Bottom) }.Select(projection.Matrix.TransformPoint).ToList();
        document.Canvas.Projection = projection;
    }

    private static void Ordinary(Action<string, bool> check, Application app, string root)
    {
        using var fixture = new Fixture(app, Path.Combine(root, "ordinary"));
        var document = new SceneDocument { Viewport = new(0, 0, 800, 600) }; var graph = new SceneGraph(document);
        var outer = graph.InsertGroup(new() { Name = "Outer", Transform = Affine2D.Translation(90, 60).Compose(Affine2D.Rotation(12)), OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
        var inner = graph.InsertGroup(new() { Name = "Inner", Transform = Affine2D.Translation(35, 30).Compose(Affine2D.Scale(1.15, .9)), OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] }, outer.Id);
        var leaf = graph.InsertElement(Box("Editable violet rectangle", 25, 25, 100, 65), inner.Id, worldCoordinates: false);
        graph.InsertElement(Box("Outside group", 620, 360, 80, 50), worldCoordinates: false);
        document.Canvas.Grid.FlatTranslation = new(12, -6);
        fixture.Reset(document); var owner = fixture.Editor.Session; owner.SetEditScope(inner.Id); owner.SetSelection([leaf.Id]);
        fixture.Call("SetGroupCanvasMode", GroupCanvasMode.Temporary);
        using var temporary = fixture.Present("ordinary-temporary");
        var edge = Edge(temporary, leaf.Name!);
        check("temporary nested group control displays its selected child's real selection frame", HasBlue(temporary.Pixels, edge));
        fixture.Call("SetGroupCanvasMode", GroupCanvasMode.RootProject);
        using var rootView = fixture.Present("ordinary-root-selection");
        check("root canvas retains the current nested group and its internal selection", owner.EditScopeId == inner.Id && owner.Selection.SetEquals([leaf.Id]));
        check("root canvas renders the same selected child's visible selection frame", HasBlue(rootView.Pixels, Edge(rootView, leaf.Name!)));
        owner.SetSelection([]); fixture.PresentAndDispose("ordinary-root-before-hit");
        var center = Center(rootView, leaf.Name!); fixture.Pointer(OverlayPointerKind.Down, center); fixture.Pointer(OverlayPointerKind.Up, center);
        check("root canvas hit testing selects the current group's actual child", owner.Selection.SetEquals([leaf.Id]));
        string before = JsonSerializer.Serialize(owner.Document); int history = owner.UndoCount;
        var delta = new PointD(34, 18); fixture.Pointer(OverlayPointerKind.Down, center); fixture.Pointer(OverlayPointerKind.Move, center + delta); fixture.Pointer(OverlayPointerKind.Up, center + delta);
        using var moved = fixture.Present("ordinary-root-moved");
        check("root canvas drag follows screen movement through nested parent placements", Center(moved, leaf.Name!).DistanceTo(center + delta) < 1e-6);
        check("root group drag is one exact undo with the same edit scope", owner.UndoCount == history + 1 && owner.Undo() && JsonSerializer.Serialize(owner.Document) == before && owner.EditScopeId == inner.Id && owner.Redo());
        TransformHandles(check, fixture, leaf.Name!);
    }

    private static void TemporaryRefresh(Action<string, bool> check, Application app, string root)
    {
        using var fixture = new Fixture(app, Path.Combine(root, "temporary-refresh"));
        var document = new SceneDocument { Viewport = new(0, 0, 800, 600) };
        document.Canvas.BackgroundColor = 0; document.Canvas.Grid.Visible = false;
        var graph = new SceneGraph(document);
        var group = graph.InsertGroup(new() { Name = "Ordinary temporary group", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
        var leaf = graph.InsertElement(Box("Temporary editable rectangle", 120, 100, 80, 60), group.Id, worldCoordinates: false);
        fixture.Reset(document); var session = fixture.Editor.Session;
        session.SetEditScope(group.Id); session.SetSelection([leaf.Id]);
        fixture.Call("SetGroupCanvasMode", GroupCanvasMode.Temporary);
        fixture.PresentAndDispose("temporary-warm");
        using var before = fixture.PresentRefresh("temporary-before");
        var start = Center(before.Content, leaf.Name!); var delta = new PointD(180, 40); var destination = start + delta;
        var fill = new SKColor(0xffbd4dba);
        check("temporary refresh baseline contains the rectangle in both worker pixels and direct pixels", ColorAt(before.Content.Pixels, start) == fill && ColorAt(before.Direct.Pixels, start) == fill);
        string original = JsonSerializer.Serialize(session.Document); int history = session.UndoCount;
        fixture.Pointer(OverlayPointerKind.Down, start);
        fixture.Pointer(OverlayPointerKind.Move, destination);
        using (var preview = fixture.PresentRefresh("temporary-during-move"))
        {
            var authored = session.Document.Elements.Single(element => element.Id == leaf.Id);
            check("temporary move changes the real author while the gesture is active", session.IsTransactionActive && authored.Transform.OffsetX == 300 && authored.Transform.OffsetY == 140);
            CheckPixels("during the move", preview, destination, start);
        }
        fixture.Pointer(OverlayPointerKind.Up, destination);
        using (var committed = fixture.PresentRefresh("temporary-move-committed"))
        {
            check("temporary move commits exactly one author edit without changing scope", !session.IsTransactionActive && session.UndoCount == history + 1 && session.EditScopeId == group.Id);
            CheckPixels("after commit", committed, destination, start);
        }
        check("temporary move Undo restores the complete author and keeps its scope", session.Undo() && JsonSerializer.Serialize(session.Document) == original && session.EditScopeId == group.Id);
        using var undone = fixture.PresentRefresh("temporary-move-undone");
        CheckPixels("after Undo", undone, start, destination);

        void CheckPixels(string stage, RefreshRendered frame, PointD painted, PointD erased)
        {
            check("temporary direct oracle responds " + stage, ColorAt(frame.Direct.Pixels, painted) == fill && ColorAt(frame.Direct.Pixels, erased).Alpha == 0);
            check("temporary visible worker pixels respond " + stage, ColorAt(frame.Content.Pixels, painted) == fill && ColorAt(frame.Content.Pixels, erased).Alpha == 0);
        }
    }

    private static void CanvasFeedback(Action<string, bool> check, Fixture fixture, EditorSession owner, string name)
    {
        var active = fixture.Editor.Session; var tool = fixture.Editor.Tool;
        string language = FloatCanvas.Localization.AppText.Preference;
        string unchangedOwner = JsonSerializer.Serialize(owner.Document), unchangedDraft = JsonSerializer.Serialize(active.Document);
        int ownerHistory = owner.UndoCount, draftHistory = active.UndoCount;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            {
                FloatCanvas.Localization.AppText.Initialize(locale);
                fixture.Editor.Tool = EditorTool.Select; fixture.Call("ActivateTool", EditorTool.Canvas);
                var status = Field<System.Windows.Controls.TextBlock>(fixture.Desktop, "_status");
                var panel = typeof(DesktopSession).GetField("_canvasSettingsWindow", Hidden)!.GetValue(fixture.Desktop) as CanvasSettingsWindow;
                if (status.Text != FloatCanvas.Localization.AppText.Get("GroupCanvas.EditPreviewInIndependent") || panel?.IsVisible == true)
                    Console.WriteLine($"CANVAS_FEEDBACK {name} {locale}: actual={status.Text}; tool={fixture.Editor.Tool}; panel={panel?.IsVisible}; mode={Field<GroupCanvasMode>(fixture.Desktop, "_groupCanvasMode")}; allowed={typeof(DesktopSession).GetProperty("CanEditDisplayedCanvas", Hidden)!.GetValue(fixture.Desktop)}");
                check(name + " " + locale + " blocked Canvas command explains where to edit subproject preview without opening a panel or changing tool", status.Text == FloatCanvas.Localization.AppText.Get("GroupCanvas.EditPreviewInIndependent") && panel?.IsVisible != true && fixture.Editor.Tool == EditorTool.Select);
                check(name + " " + locale + " Canvas feedback leaves both host and draft settings and history unchanged", JsonSerializer.Serialize(owner.Document) == unchangedOwner && JsonSerializer.Serialize(active.Document) == unchangedDraft && owner.UndoCount == ownerHistory && active.UndoCount == draftHistory);
            }
        }
        finally { fixture.Editor.Tool = tool; FloatCanvas.Localization.AppText.Initialize(language); }
    }

    private static SKColor ColorAt(SKBitmap pixels, PointD point)
    {
        int x = (int)Math.Round(point.X), y = (int)Math.Round(point.Y);
        if (x < 0 || y < 0 || x >= pixels.Width || y >= pixels.Height) throw new InvalidOperationException("Temporary refresh sample is outside the frame.");
        return pixels.GetPixel(x, y);
    }

    private static void TransformHandles(Action<string, bool> check, Fixture fixture, string name, Guid? host = null)
    {
        var session = fixture.Editor.Session; string before = JsonSerializer.Serialize(session.Document); int history = session.UndoCount;
        using var initial = fixture.Present(name + "-before-handle-oracles");
        var corners = PlaneCorners(initial, name, host); var pivot = corners.Aggregate(default(PointD), (sum, point) => sum + point) * .25;
        var rotationHandle = fixture.Editor.SelectionScreenRotationHandle ?? throw new InvalidOperationException("The selected root element has no rotation handle.");
        var from = fixture.Raster(rotationHandle); var rootMapping = CanvasMapping.Create(initial.Document);
        var handleInPlane = rootMapping.ScreenToPlane.TransformPoint(from - initial.Origin);
        var rotation = Affine2D.Around(pivot, Affine2D.Rotation(27));
        var target = rootMapping.ToScreen(rotation.TransformPoint(handleInPlane)) + initial.Origin;
        fixture.Pointer(OverlayPointerKind.Down, from);
        check(name + " real root rotation handle starts its rotation gesture", Field<string?>(fixture.Editor, "gesture") == "rotate");
        fixture.Pointer(OverlayPointerKind.Move, target); fixture.Pointer(OverlayPointerKind.Up, target);
        using var rotated = fixture.Present(name + "-root-rotated");
        var rotatedCorners = PlaneCorners(rotated, name, host);
        check(name + " rotation oracle matches root-plane R27 for every corner under the outer placement", corners.Select(rotation.TransformPoint).Zip(rotatedCorners).All(pair => pair.First.DistanceTo(pair.Second) < 1e-5));
        check(name + " root rotation is one exact source undo and redo", session.UndoCount == history + 1 && session.Undo() && JsonSerializer.Serialize(session.Document) == before && session.Redo() && session.Undo());
        using var restored = fixture.Present(name + "-after-rotation-undo");
        var resizeHandle = fixture.Editor.SelectionScreenCorners[2]; from = fixture.Raster(resizeHandle);
        var element = SceneEvaluation.Build(restored.Document).Leaves.Single(item => item.RenderElement.Name == name && (host is null || item.HostNodeId == host)).RenderElement;
        var localToRoot = ElementGeometry.LocalToPlane(element); var localCenter = new PointD(element.Width / 2, element.Height / 2);
        const double sx = 1.2, sy = .85;
        PointD ResizeLocal(PointD point) => localCenter + new PointD((point.X - localCenter.X) * sx, (point.Y - localCenter.Y) * sy);
        PointD[] localCorners = [default, new(element.Width, 0), new(element.Width, element.Height), new(0, element.Height)];
        var expected = localCorners.Select(ResizeLocal).Select(localToRoot.TransformPoint).ToArray();
        target = CanvasMapping.Create(restored.Document).ToScreen(expected[2]) + restored.Origin;
        fixture.Pointer(OverlayPointerKind.Down, from);
        check(name + " real root corner handle starts its resize gesture", Field<string?>(fixture.Editor, "gesture") == "resize");
        fixture.Pointer(OverlayPointerKind.Move, target); fixture.Pointer(OverlayPointerKind.Up, target);
        using var resized = fixture.Present(name + "-root-resized");
        var resizedCorners = PlaneCorners(resized, name, host);
        check(name + " resize oracle matches centered local 1.2 by 0.85 scale for every root-plane corner", expected.Zip(resizedCorners).All(pair => pair.First.DistanceTo(pair.Second) < 1e-5));
        check(name + " root resize is one exact source undo and redo", session.UndoCount == history + 1 && session.Undo() && JsonSerializer.Serialize(session.Document) == before && session.Redo() && session.Undo());
        fixture.PresentAndDispose(name + "-after-resize-undo");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly OverlayHost Host;
        internal readonly DesktopSession Desktop;
        internal readonly EditorController Editor;
        internal string? LastMessage;
        private readonly string output;
        private readonly PixelBounds bounds;
        internal Fixture(Application app, string output, PixelBounds? outputBounds = null)
        {
            bounds = outputBounds ?? new(-24000, -23000, 800, 600);
            this.output = output; Directory.CreateDirectory(output);
            Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("root-group-edit-private", bounds, bounds, 96, 96, true)];
            Host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
            Desktop = new(app, Host); Field<System.Windows.Forms.NotifyIcon>(Desktop, "_tray").Visible = false; Field<Window>(Desktop, "_toolbar").Opacity = 0;
            ((IDisposable?)typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.GetValue(Host))?.Dispose(); typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.SetValue(Host, null);
            Editor = Field<EditorController>(Desktop, "_editor");
            Editor.Message += message => LastMessage = message;
            if (!Editor.BackgroundPickingEnabled) throw new InvalidOperationException("This fixture requires production asynchronous pointer picking.");
            Host.SetVisible(true);
            // Only native lease acquisition is outside this fixture. Every event below enters
            // the production desktop/presentation/controller path; no OS input is injected.
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(Host, true);
        }
        internal void Reset(SceneDocument document)
        {
            Editor.Session.ReplaceDocument(document); Editor.ScreenOrigin = new(bounds.Left, bounds.Top); Editor.Tool = EditorTool.Select;
            Call("SynchronizeFlatGridOrigin"); Pump();
        }
        internal object? Call(string name, params object?[] arguments) => typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(Desktop, arguments);
        internal Rendered PresentNative(string name, bool requestRender = true)
        {
            if (requestRender) Call("RequestRender"); var preview = Field<CanvasPreviewPump>(Desktop, "_canvasPreview");
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                if (timeout.ElapsedMilliseconds > 10000)
                    throw new TimeoutException($"Native presentation did not settle: busy={preview.Busy} frames={preview.CompletedFrames} submitted={Host.PresentedFrameCount} uncertain={Field<bool>(Desktop, "_presentationUncertain")} frameSequence={preview.Frame?.Sequence}.");
                Pump(); Thread.Yield();
            }
            while (preview.Busy || Pending("_renderOperation") || Pending("_presentOperation") || preview.Frame is not { } current ||
                current.Context != (CanvasPreviewContext)Call("CurrentPreviewContext")! ||
                !Equals(typeof(DesktopSession).GetField("_presentedCanvasContext", Hidden)!.GetValue(Desktop), current.Context));
            using var snapshot = (CanvasPreviewSnapshot)Call("CaptureCanvasPreview")!;
            var outputs = (System.Collections.IEnumerable)typeof(OverlayHost).GetField("_windows", Hidden)!.GetValue(Host)!;
            var output = outputs.Cast<object>().Single(); var buffer = output.GetType().GetProperty("Buffer")!.GetValue(output)!;
            var surface = (SKSurface)buffer.GetType().GetProperty("Surface", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(buffer)!;
            using var image = surface.Snapshot(); var pixels = SKBitmap.FromImage(image);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(Path.Combine(this.output, name + ".png"), encoded.ToArray());
            return new(snapshot.Document, snapshot.Context.Origin - new PointD(bounds.Left, bounds.Top), pixels) { View = snapshot.Context.View ?? CanvasViewTransform.Identity };
        }
        internal Rendered Present(string name)
        {
            // Use the same worker-owned frame as native presentation. A synthetic parallel
            // frame can be invalidated by an older queued native submission while picking awaits.
            Call("RequestRender");
            var preview = Field<CanvasPreviewPump>(Desktop, "_canvasPreview");
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                if (timeout.ElapsedMilliseconds > 10000) throw new TimeoutException("Root editing presentation did not settle.");
                Pump(); Thread.Yield();
            }
            while (preview.Busy || Pending("_renderOperation") || Pending("_presentOperation") ||
                preview.Frame is not { } current || current.Context != (CanvasPreviewContext)Call("CurrentPreviewContext")! || current.ContentRevision != Editor.Session.PreviewRevision);
            using var snapshot = (CanvasPreviewSnapshot)Call("CaptureCanvasPreview")!;
            using var frame = preview.Frame!.Retain();
            Set(Desktop, "_submittingCanvasFrame", frame.Retain()); Set(Desktop, "_submittingCanvasContext", snapshot.Context);
            using var surface = SKSurface.Create(new SKImageInfo(bounds.Width, bounds.Height)); surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Translate(-bounds.Left, -bounds.Top); Call("Draw", surface);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            Call("CompleteCanvasSubmission", new OverlayFrameTiming(now, now, true, [new("root-group-edit-private", bounds, now, now, 0, 0)]));
            using var image = surface.Snapshot(); var pixels = SKBitmap.FromImage(image);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(Path.Combine(output, name + ".png"), encoded.ToArray());
            return new(snapshot.Document, snapshot.Context.Origin - new PointD(bounds.Left, bounds.Top), pixels) { View = snapshot.Context.View ?? CanvasViewTransform.Identity };
        }
        private bool Pending(string name) => typeof(DesktopSession).GetField(name, Hidden)!.GetValue(Desktop) is DispatcherOperation { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing };
        internal void PresentAndDispose(string name) { using var frame = Present(name); }
        internal RefreshRendered PresentRefresh(string name)
        {
            using var presented = Present(name);
            using var snapshot = (CanvasPreviewSnapshot)Call("CaptureCanvasPreview")!;
            using var frame = Field<CanvasPreviewPump>(Desktop, "_canvasPreview").Frame!.Retain();
            using var contentSurface = SKSurface.Create(new SKImageInfo(bounds.Width, bounds.Height));
            contentSurface.Canvas.Clear(SKColors.Transparent); contentSurface.Canvas.Translate(-bounds.Left, -bounds.Top);
            frame.Pixels.Draw(contentSurface.Canvas);
            using var contentImage = contentSurface.Snapshot(); var content = SKBitmap.FromImage(contentImage);
            using var directSurface = SKSurface.Create(new SKImageInfo(bounds.Width, bounds.Height));
            directSurface.Canvas.Clear(SKColors.Transparent); directSurface.Canvas.Translate(-bounds.Left, -bounds.Top);
            directSurface.Canvas.Translate((float)snapshot.Context.Origin.X, (float)snapshot.Context.Origin.Y);
            var camera = snapshot.Context.View ?? CanvasViewTransform.Identity;
            directSurface.Canvas.Translate((float)camera.Offset.X, (float)camera.Offset.Y);
            directSurface.Canvas.Scale((float)camera.Zoom);
            var visible = camera.Matrix.Inverse().TransformBounds(snapshot.Document.Viewport);
            var directDocument = SceneEvaluation.WithViewport(SceneEvaluation.Build(snapshot.Document).RenderDocument, visible,
                GroupCanvasScene.ResolveEditingCanvas(snapshot.Document.Canvas, snapshot.Document.Viewport));
            using var renderer = new SceneRenderer(); renderer.Draw(directSurface.Canvas, directDocument, Editor.Session.Assets);
            using var directImage = directSurface.Snapshot(); var direct = SKBitmap.FromImage(directImage);
            Save(contentImage, name + "-worker-content.png"); Save(directImage, name + "-direct.png");
            Console.WriteLine($"TEMPORARY_REFRESH_FRAME name={name} authorRevision={Editor.Session.PreviewRevision} frameRevision={frame.ContentRevision} builds={frame.Cache?.BuildCount} hits={frame.Cache?.HitCount} view={snapshot.Context.View}");
            return new(new(snapshot.Document, presented.Origin, content) { View = camera }, new(snapshot.Document, presented.Origin, direct) { View = camera });

            void Save(SKImage image, string filename)
            {
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(output, filename), encoded.ToArray());
            }
        }
        internal void Pointer(OverlayPointerKind kind, PointD localScreen)
        {
            var physical = localScreen + new PointD(bounds.Left, bounds.Top);
            Call("HandleCanvasPointer", new OverlayPointerEvent(kind, new(physical.X, physical.Y), OverlayMouseButton.Left, OverlayModifiers.None));
            Await(Editor.PendingPicking);
            if (kind == OverlayPointerKind.Down) Console.WriteLine($"ROOT_POINTER_DOWN point={localScreen} actual={Field<PointD>(Editor, "pointerScreen")} gesture={Field<string?>(Editor, "gesture")} suspended={Field<bool>(Editor, "presentedPickingSuspended")} selection={Editor.Session.Selection.Count} message={LastMessage}");
        }
        internal PointD Raster(PointD canvasScreen) => canvasScreen + Editor.CanvasOrigin - new PointD(bounds.Left, bounds.Top);
        public void Dispose()
        {
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(Host, false);
            Desktop.Dispose(); Host.Dispose();
        }
    }
    private sealed record Rendered(SceneDocument Document, PointD Origin, SKBitmap Pixels) : IDisposable
    {
        internal CanvasViewTransform View { get; init; } = CanvasViewTransform.Identity;
        public void Dispose() => Pixels.Dispose();
    }
    private sealed record RefreshRendered(Rendered Content, Rendered Direct) : IDisposable
    {
        public void Dispose() { Content.Dispose(); Direct.Dispose(); }
    }
    private static CanvasElement Box(string name, double x, double y, double width, double height) => new()
    { Name = name, Kind = ElementKind.Rectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { Fill = 0xffbd4dba, StrokeWidth = 0 } };
    private static PointD Center(Rendered frame, string name, Guid? host = null) => Map(frame, name, .5, .5, host);
    private static PointD Edge(Rendered frame, string name, Guid? host = null) => Map(frame, name, .5, 0, host);
    private static PointD Map(Rendered frame, string name, double x, double y, Guid? host)
    {
        var leaf = SceneEvaluation.Build(frame.Document).Leaves.Single(item => item.RenderElement.Name == name && (host is null || item.HostNodeId == host)).RenderElement;
        return CanvasMapping.CreateViewing(frame.Document, frame.View).ElementToScreen(leaf).TransformPoint(new(leaf.Width * x, leaf.Height * y)) + frame.Origin;
    }
    private static PointD[] PlaneCorners(Rendered frame, string name, Guid? host)
    {
        var leaf = SceneEvaluation.Build(frame.Document).Leaves.Single(item => item.RenderElement.Name == name && (host is null || item.HostNodeId == host)).RenderElement;
        var matrix = ElementGeometry.LocalToPlane(leaf);
        return new[] { default(PointD), new PointD(leaf.Width, 0), new PointD(leaf.Width, leaf.Height), new PointD(0, leaf.Height) }.Select(matrix.TransformPoint).ToArray();
    }
    private static bool HasBlue(SKBitmap bitmap, PointD point)
    {
        for (int y = Math.Max(0, (int)point.Y - 4); y <= Math.Min(bitmap.Height - 1, (int)point.Y + 4); y++)
            for (int x = Math.Max(0, (int)point.X - 4); x <= Math.Min(bitmap.Width - 1, (int)point.X + 4); x++)
            { var color = bitmap.GetPixel(x, y); if (color.Red < 100 && color.Green is > 80 and < 180 && color.Blue > 160 && color.Alpha > 180) return true; }
        return false;
    }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        { var frame = new DispatcherFrame(); _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.FromCurrentSynchronizationContext()); Dispatcher.PushFrame(frame); }
        task.GetAwaiter().GetResult();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Hidden)!.GetValue(value)!;
    private static void Set(object value, string name, object? content) => value.GetType().GetField(name, Hidden)!.SetValue(value, content);
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x5b01, 7, key)) continue;
            try { if (!RegisterHotKey(0, 0x5b02, 7, key + 1)) continue; UnregisterHotKey(0, 0x5b02); return (new(7, key), new(7, key + 1)); }
            finally { UnregisterHotKey(0, 0x5b01); }
        }
        throw new InvalidOperationException("No test hotkeys available.");
    }
    private static string DesktopName()
    {
        var name = new StringBuilder(256); if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); return name.ToString();
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
