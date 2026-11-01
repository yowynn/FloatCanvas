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
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Real desktop composition, presented picking and pointer routes on an undisplayed private desktop.</summary>
internal static class GroupCanvasRootEditingTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal) || PresentationDesktopAvailability.IsCurrentInputDesktop())
            throw new InvalidOperationException("Use --isolated-ui-desktop --group-canvas-root-editing-only.");
        if (Application.Current is not null) throw new InvalidOperationException("Use a fresh private test process.");
        string root = Path.Combine(output ?? Path.GetTempPath(), "group-canvas-root-edit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        ApplicationPaths.Configure(["--profile", Path.Combine(root, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys(); new UserSettings { Theme = "dark", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2,
            PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var failures = new List<Exception>();
        Case("ordinary nested group", () => Ordinary(check, app, root));
        Case("shared nested group", () => Shared(check, app, root, false));
        Case("shared nested billboard", () => Shared(check, app, root, true));
        Case("external project source", () => External(check, app, root, false));
        Case("external prefab source", () => External(check, app, root, true));
        if (failures.Count > 0) throw new AggregateException("Root group canvas editing failures.", failures);
        Console.WriteLine("GROUP_CANVAS_ROOT_EDIT_EVIDENCE " + root);
        void Case(string name, Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(error); Console.WriteLine("ROOT_CANVAS_EDIT_CASE_FAILED " + name + ": " + error.Message); }
        }
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
        using var temporary = fixture.Present(kind + "-temporary");
        check(kind + " temporary control displays the current nested source selection", HasBlue(temporary.Pixels, Edge(temporary, kind)));
        fixture.Call("SetGroupCanvasMode", GroupCanvasMode.RootProject);
        using var before = fixture.Present(kind + "-root-selection");
        check(kind + " root selection frame uses the entered owner's nested placement and perspective", HasBlue(before.Pixels, Edge(before, kind, first.Id)));
        check(kind + " root view leaves its true nested editing session and scope active", !ReferenceEquals(active, owner) && ReferenceEquals(fixture.Editor.Session, active) && active.EditScopeId == innerGroup.Id);
        active.SetSelection([]); fixture.PresentAndDispose(kind + "-before-hit"); var center = Center(before, kind, first.Id);
        fixture.Pointer(OverlayPointerKind.Down, center); fixture.Pointer(OverlayPointerKind.Up, center);
        check(kind + " owner occurrence hit resolves the real source child rather than the host instance", active.Selection.SetEquals([leaf.Id]));
        string draftBefore = JsonSerializer.Serialize(active.Document); int history = active.UndoCount; var delta = new PointD(32, 17);
        fixture.Pointer(OverlayPointerKind.Down, center); fixture.Pointer(OverlayPointerKind.Move, center + delta); fixture.Pointer(OverlayPointerKind.Up, center + delta);
        using var moved = fixture.Present(kind + "-root-moved");
        check(kind + " drag follows the actual root screen point with parent scale and elevation exactly once", Center(moved, kind, first.Id).DistanceTo(center + delta) < 1e-5);
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
        using var before = fixture.Present(kind + "-root-selection");
        check(kind + " root frame compensates source origin or pivot and its active source group", candidate.Source.ImportOrigin != default && HasBlue(before.Pixels, Edge(before, "external-plane", imported.InstanceId)));
        active.SetSelection([]); fixture.PresentAndDispose(kind + "-before-hit"); var center = Center(before, "external-plane", imported.InstanceId);
        fixture.Pointer(OverlayPointerKind.Down, center); fixture.Pointer(OverlayPointerKind.Up, center);
        check(kind + " root hit selects the real original source element", active.Selection.SetEquals([leaf.Id]));
        string sourceBefore = JsonSerializer.Serialize(active.Document); int history = active.UndoCount; var delta = new PointD(28, 19);
        fixture.Pointer(OverlayPointerKind.Down, center); fixture.Pointer(OverlayPointerKind.Move, center + delta); fixture.Pointer(OverlayPointerKind.Up, center + delta);
        using var moved = fixture.Present(kind + "-root-moved");
        check(kind + " root drag tracks the pointer through source coordinates and owner perspective", Center(moved, "external-plane", imported.InstanceId).DistanceTo(center + delta) < 1e-5 &&
            Hash(path) == diskBefore && JsonSerializer.Serialize(owner.Document) == ownerBefore);
        check(kind + " root drag is one exact source undo and redo", active.UndoCount == history + 1 && active.Undo() && JsonSerializer.Serialize(active.Document) == sourceBefore && active.Redo());
        var movedA = Center(moved, "external-plane", imported.InstanceId); var movedB = Center(moved, "external-plane", second);
        string savedSource = JsonSerializer.Serialize(active.Document);
        string savedGraph = JsonSerializer.Serialize(SceneGraphClone.CloneGraph(active.Document).Graph);
        Await((Task)fixture.Call("TryHandlePrefabSaveAsync", false)!);
        if (prefab)
        {
            var written = Await(prefabStore.LoadAsync(path)); var entry = written.Package.Definitions.Single(definition => definition.Id == written.Package.EntryDefinitionId);
            check(kind + " Ctrl S preserves the original source entry pivot and unprojected graph", entry.Pivot == pivot && JsonSerializer.Serialize(entry.Graph) == savedGraph && entry.Graph.Elements.Single().Id == leaf.Id);
        }
        else
        {
            var written = Await(store.LoadAsync(path));
            check(kind + " Ctrl S saves the true source graph rather than a projected owner copy", JsonSerializer.Serialize(written.Document) == savedSource && written.Document.Elements.Single().Id == leaf.Id);
        }
        Await((Task)fixture.Call("CompleteCurrentPrefabAsync")!);
        using var committed = fixture.Present(kind + "-root-published");
        check(kind + " completion synchronizes both links without rebasing their visible positions again", ReferenceEquals(fixture.Editor.Session, owner) &&
            Center(committed, "external-plane", imported.InstanceId).DistanceTo(movedA) < 1e-5 && Center(committed, "external-plane", second).DistanceTo(movedB) < 1e-5 &&
            owner.Document.Definitions.Single(definition => definition.Id == imported.DefinitionId).Source!.LastAcceptedFileSha256 == Hash(path));
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
        private readonly PixelBounds bounds = new(-24000, -23000, 800, 600);
        internal Fixture(Application app, string output)
        {
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
            return new(snapshot.Document, snapshot.Context.Origin - new PointD(bounds.Left, bounds.Top), pixels);
        }
        private bool Pending(string name) => typeof(DesktopSession).GetField(name, Hidden)!.GetValue(Desktop) is DispatcherOperation { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing };
        internal void PresentAndDispose(string name) { using var frame = Present(name); }
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
    private sealed record Rendered(SceneDocument Document, PointD Origin, SKBitmap Pixels) : IDisposable { public void Dispose() => Pixels.Dispose(); }
    private static CanvasElement Box(string name, double x, double y, double width, double height) => new()
    { Name = name, Kind = ElementKind.Rectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { Fill = 0xffbd4dba, StrokeWidth = 0 } };
    private static PointD Center(Rendered frame, string name, Guid? host = null) => Map(frame, name, .5, .5, host);
    private static PointD Edge(Rendered frame, string name, Guid? host = null) => Map(frame, name, .5, 0, host);
    private static PointD Map(Rendered frame, string name, double x, double y, Guid? host)
    {
        var leaf = SceneEvaluation.Build(frame.Document).Leaves.Single(item => item.RenderElement.Name == name && (host is null || item.HostNodeId == host)).RenderElement;
        return CanvasMapping.Create(frame.Document).ElementToScreen(leaf).TransformPoint(new(leaf.Width * x, leaf.Height * y)) + frame.Origin;
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
