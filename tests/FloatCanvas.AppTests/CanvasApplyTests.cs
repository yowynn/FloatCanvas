using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class CanvasApplyTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (bool perspective in new[] { false, true }) Root(check, perspective);
        Group(check);
        Failure(check);
        ViewIdentity(check);
        PersistedWorkspaceView(check);
        ColdRecoveryView(check);
        LegacyFileView(check);
        FramingAndReadonly(check);
    }
    private static void Root(Action<string, bool> check, bool perspective)
    {
        using var fixture = new Fixture();
        var session = fixture.Editor.Session;
        var document = new SceneDocument { Viewport = new(0, 0, 640, 360) };
        document.Canvas.Projection = CanvasCalibration.CreateInitial(document.Viewport);
        document.Canvas.Projection.Enabled = perspective;
        if (perspective)
        {
            var projection = document.Canvas.Projection; var bounds = CanvasProjectionGeometry.ResolvePlaneBounds(projection);
            projection.Matrix = new(1, .08, 20, .04, .95, 10, .0001, .0003, 1);
            projection.Quad = EditorController.Corners(bounds).Select(projection.Matrix.TransformPoint).ToList();
        }
        var main = Box(80, 80); var foreground = Box(300, 110); var background = Box(150, 230);
        document.Elements = [main, foreground, background]; SceneCanvasRoots.Assign(document, foreground.Id, RootCanvasKind.Foreground);
        SceneCanvasRoots.Assign(document, background.Id, RootCanvasKind.Background);
        document.Canvas.Grid = new() { Visible = true, FlatCellSize = 30, PlaneCellSize = 30, FlatOrigin = new(50, 70), PlaneOrigin = new(50, 70), Width = 2 };
        using (var surface = SKSurface.Create(new SKImageInfo(8, 8)))
        {
            surface.Canvas.Clear(new SKColor(40, 70, 90)); using var image = surface.Snapshot(); using var data = image.Encode();
            string asset = session.Assets.Add(data.ToArray());
            document.Background = new() { Kind = ElementKind.Image, AssetId = asset, Width = 200, Height = 160, Transform = Affine2D.Translation(50, 40) };
        }
        session.ReplaceDocument(document); fixture.Call("CurrentGroupCanvasView");
        var view = new CanvasViewTransform(.8, new(110, 50)); fixture.Call("ChangeCanvasViewing", view, false);
        string before = JsonSerializer.Serialize(session.Document); int history = session.UndoCount;
        var expected = Enum.GetValues<RootCanvasKind>().ToDictionary(root => root, root => view.ToView(CanvasMapping.Create(session.Document, root).ToScreen(new(100, 100))));
        fixture.Call("ApplyCanvasViewing");
        check("production apply commits root composite data and resets its complete camera " + perspective,
            session.UndoCount == history + 1 && fixture.View == CanvasViewTransform.Identity && JsonSerializer.Serialize(session.Document) != before);
        foreach (var leaf in document.Elements)
        {
            var root = SceneCanvasRoots.Resolve(document, leaf.Id);
            var actual = CanvasMapping.Create(session.Document, root).ElementToScreen(session.Document.Elements.Single(item => item.Id == leaf.Id)).TransformPoint(new(10, 10));
            var wanted = view.ToView(CanvasMapping.Create(document, root).ElementToScreen(leaf).TransformPoint(new(10, 10)));
            check("root apply changes formal output geometry in every real canvas " + root, actual.DistanceTo(wanted) < 1e-7);
        }
        var backgroundPoint = new PointD(13, 19);
        check("root background remains continuous across true apply", CanvasMapping.Create(session.Document).ElementToScreen(session.Document.Background!).TransformPoint(backgroundPoint)
            .DistanceTo(view.ToView(CanvasMapping.Create(document).ElementToScreen(document.Background!).TransformPoint(backgroundPoint))) < 1e-7);
        check("apply preserves the artboard size rather than converting resolution", session.Document.Viewport == document.Viewport);
        string applied = JsonSerializer.Serialize(session.Document);
        fixture.Call("ApplyCanvasViewing");
        check("a second apply is inert after the camera reset", history + 1 == session.UndoCount && applied == JsonSerializer.Serialize(session.Document));
        check("undo restores data and the exact prior root view", session.Undo() && JsonSerializer.Serialize(session.Document) == before && fixture.View == view);
        check("redo restores data and identity viewing without reapplying a cached framing", session.Redo() && JsonSerializer.Serialize(session.Document) == applied && fixture.View == CanvasViewTransform.Identity);
        using var snapshot = (CanvasPreviewSnapshot)fixture.Call("CaptureCanvasPreview")!;
        using var worker = new CanvasPreviewRenderer(); using var frame = worker.Render(snapshot);
        using var renderer = new SceneRenderer(); using var exported = SKBitmap.Decode(renderer.ExportPng(session.Document, session.Assets));
        using var target = SKSurface.Create(new SKImageInfo(640, 360)); frame.Pixels.Draw(target.Canvas);
        using var targetImage = target.Snapshot(); using var pixels = SKBitmap.FromImage(targetImage);
        check("formal export uses the applied artwork rather than a private view override", snapshot.Context.View is null &&
            pixels.GetPixel(190, 130) == exported.GetPixel(190, 130) && pixels.GetPixel(370, 150) == exported.GetPixel(370, 150));
    }
    private static void Group(Action<string, bool> check)
    {
        using var fixture = new Fixture(); var session = fixture.Editor.Session;
        var document = new SceneDocument { Viewport = new(0, 0, 640, 360) }; var graph = new SceneGraph(document);
        var group = graph.InsertGroup(new() { Name = "local", Transform = Affine2D.Translation(70, 80), OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
        var leaf = graph.InsertElement(Box(10, 20), group.Id, worldCoordinates: false);
        session.ReplaceDocument(document); session.SetEditScope(group.Id);
        fixture.Call("SetGroupCanvasMode", GroupCanvasMode.Temporary); fixture.Call("CurrentGroupCanvasView");
        check("independent first opening keeps the authored coordinates", fixture.View == CanvasViewTransform.Identity);
        fixture.Call("ExecuteCanvasViewing", CanvasViewingCommand.CenterContent);
        var automatic = fixture.View; string before = JsonSerializer.Serialize(session.Document);
        check("explicit content centering changes only the independent camera", automatic != CanvasViewTransform.Identity);
        fixture.Call("ApplyCanvasViewing");
        check("explicit framing can be truly applied to group content", fixture.View == CanvasViewTransform.Identity && session.UndoCount == 1 &&
            session.Document.Elements.Single(item => item.Id == leaf.Id).Transform != leaf.Transform && session.Document.Groups.Single().Transform == group.Transform);
        session.SetEditScope(null); fixture.Call("CurrentGroupCanvasView"); fixture.Call("ChangeCanvasViewing", new CanvasViewTransform(1.2, new(40, 20)), false);
        var other = fixture.View;
        check("undo from another canvas restores content without replacing that canvas's camera", session.Undo() && before == JsonSerializer.Serialize(session.Document) && fixture.View == other);
        session.SetEditScope(group.Id); fixture.Call("CurrentGroupCanvasView");
        check("the original group's view receipt is restored locally when reentered", fixture.View == automatic);
        check("group redo restores the corresponding identity camera", session.Redo() && fixture.View == CanvasViewTransform.Identity);
        session.SetEditScope(null); fixture.Call("CurrentGroupCanvasView"); session.SetEditScope(group.Id); fixture.Call("CurrentGroupCanvasView");
        check("reentering after apply retains explicit identity and does not auto-frame again", fixture.View == CanvasViewTransform.Identity);
        fixture.Call("SetGroupCanvasMode", GroupCanvasMode.RootProject); fixture.Call("ChangeCanvasViewing", automatic, false);
        int history = session.UndoCount; string current = JsonSerializer.Serialize(session.Document);
        var placedBefore = session.Document.Groups.Single().Transform;
        var localBefore = session.Document.Elements.Single(item => item.Id == leaf.Id).Transform;
        fixture.Call("ApplyCanvasViewing");
        check("group project preview applies the root view without changing edit scope", session.UndoCount == history + 1 &&
            fixture.View == CanvasViewTransform.Identity && session.EditScopeId == group.Id &&
            session.Document.Groups.Single().Transform != placedBefore && session.Document.Elements.Single(item => item.Id == leaf.Id).Transform == localBefore);
        check("root-view apply from a group undoes content and camera together", session.Undo() && current == JsonSerializer.Serialize(session.Document) && fixture.View == automatic);
    }
    private static void Failure(Action<string, bool> check)
    {
        using var fixture = new Fixture(); var session = fixture.Editor.Session;
        session.Document.Elements.Add(Box(100, 100)); fixture.Call("CurrentGroupCanvasView");
        var camera = new CanvasViewTransform(64, new(30, 20)); fixture.Call("ChangeCanvasViewing", camera, false);
        session.Document.Canvas.Grid.Width = 4;
        string before = JsonSerializer.Serialize(session.Document); int history = session.UndoCount;
        fixture.Call("ApplyCanvasViewing");
        check("invalid candidate fails atomically without clamping, editing data or clearing the camera", session.UndoCount == history && before == JsonSerializer.Serialize(session.Document) && fixture.View == camera);
    }
    private static CanvasElement Box(double x, double y) => new() { Kind = ElementKind.Rectangle, Width = 60, Height = 40,
        Transform = Affine2D.Translation(x, y), Style = new() { Foreground = 0, Background = 0xff20cc50, StrokeWidth = 0 } };

    private static void ViewIdentity(Action<string, bool> check)
    {
        using var fixture = new Fixture(); var session = fixture.Editor.Session;
        session.Add(Box(100, 100)); fixture.Call("CurrentGroupCanvasView");
        var camera = new CanvasViewTransform(.8, new(30, 20)); fixture.Call("ChangeCanvasViewing", camera, false);
        string oldKey = (string)fixture.Call("CanvasViewingKey")!;
        fixture.Call("ApplyCanvasViewing");
        fixture.Call("BindCanvasIdentity", session, "test-save-as-copy", true, true);
        string copyKey = (string)fixture.Call("CanvasViewingKey")!;
        check("save-as gives a document a separate local view namespace", copyKey != oldKey && fixture.View == CanvasViewTransform.Identity);
        check("apply undo after save-as restores the copied document's previous view", session.Undo() && fixture.View == camera);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var store = (CanvasViewStateStore)typeof(DesktopSession).GetField("_canvasViews", hidden)!.GetValue(fixture.Desktop)!;
        check("copy undo leaves the original file's identity-view record intact", store.TryRestore(oldKey, new(0, 0, 640, 360), out var sourceView) && sourceView == CanvasViewTransform.Identity);
        var snapshot = session.Document.Clone();
        var history = new EditorSession(snapshot.Clone());
        fixture.Call("BindCanvasIdentity", history, "test-history-version", true, false);
        fixture.Editor.ReplaceSession(history, default); fixture.Call("CurrentGroupCanvasView");
        var historicalCamera = new CanvasViewTransform(1.3, new(-50, -30)); fixture.Call("ChangeCanvasViewing", historicalCamera, false);
        var reopened = new EditorSession(snapshot.Clone()); fixture.Call("BindCanvasIdentity", reopened, "test-save-as-copy", false, true);
        fixture.Editor.ReplaceSession(reopened, default); fixture.Call("CurrentGroupCanvasView");
        check("opening the saved copy restores its own view instead of the edited history view", fixture.View == camera);
        var historyAgain = new EditorSession(snapshot.Clone()); fixture.Call("BindCanvasIdentity", historyAgain, "test-history-version", true, false);
        fixture.Editor.ReplaceSession(historyAgain, default); fixture.Call("CurrentGroupCanvasView");
        Console.WriteLine($"HISTORY_VIEW actual={fixture.View} expected={historicalCamera}");
        check("returning to a history version restores that version's separate observation", fixture.View.Zoom == historicalCamera.Zoom && fixture.View.Offset.DistanceTo(historicalCamera.Offset) < 1e-7);
    }

    private static void PersistedWorkspaceView(Action<string, bool> check)
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "canvas-view-reopen-" + Guid.NewGuid().ToString("N") + ".floatcanvas");
        var camera = new CanvasViewTransform(1.7, new(55, 37)); string key; byte[] savedBytes;
        using (var first = new Fixture())
        {
            var session = first.Editor.Session; session.Add(Box(100, 100)); first.Call("CurrentGroupCanvasView");
            Task.Run(() => new ProjectStore().SaveAsync(path, session.Document.Clone(), session.Assets)).GetAwaiter().GetResult();
            savedBytes = System.IO.File.ReadAllBytes(path);
            string fileNamespace = (string)typeof(DesktopSession).GetMethod("CanvasFileNamespace", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [session, path])!;
            first.Call("BindCanvasIdentity", session, fileNamespace, true, true);
            first.Call("ChangeCanvasViewing", camera, false); key = (string)first.Call("CanvasViewingKey")!;
        }
        var loaded = Task.Run(() => new ProjectStore().LoadAsync(path)).GetAwaiter().GetResult();
        using var reopened = new Fixture();
        reopened.Editor.ReplaceSession(new(loaded.Document, loaded.Assets), default); reopened.Call("CurrentGroupCanvasView");
        check("new desktop restores saved observation and zoom from local disk after the previous desktop was disposed", reopened.View.Zoom == camera.Zoom && reopened.View.Offset.DistanceTo(camera.Offset) < 1e-7);
        check("workspace recovery can resolve the last file view identity without persisting a camera or path inside the project", key == (string)reopened.Call("CanvasViewingKey")! &&
            System.IO.File.ReadAllBytes(path).SequenceEqual(savedBytes));
        var state = reopened.Call("FileState", reopened.Editor.Session)!;
        state.GetType().GetField("Path")!.SetValue(state, path);
        reopened.Call("DetachActiveFile"); string detachedKey = (string)reopened.Call("CanvasViewingKey")!;
        reopened.Call("ChangeCanvasViewing", new CanvasViewTransform(.9, new(15, 20)), false);
        var views = (CanvasViewStateStore)typeof(DesktopSession).GetField("_canvasViews", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reopened.Desktop)!;
        check("detaching a saved file forks the workspace observation without overwriting the original file view", detachedKey != key &&
            views.TryRestore(key, new(0, 0, 640, 360), out var originalView) && originalView.Zoom == camera.Zoom && originalView.Offset.DistanceTo(camera.Offset) < 1e-7 &&
            System.IO.File.ReadAllBytes(path).SequenceEqual(savedBytes));
    }

    private static void ColdRecoveryView(Action<string, bool> check)
    {
        var source = new SceneDocument { Viewport = new(0, 0, 3840, 2160),
            AuthoringReference = new(new(0, 0, 3840, 2160), new(0, 0, 3840, 2160)), Elements = [Box(400, 200)] };
        var converted = CanvasResolutionConversion.Prepare(source, new(0, 0, 640, 360)).Document;
        string originalProfile = ApplicationPaths.ProfileDirectory;
        string profile = System.IO.Path.Combine(AppContext.BaseDirectory, "canvas-cold-recovery-" + Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", profile]);
        var camera = new CanvasViewTransform(1.8, new(42, 26)); string key; string content;
        try
        {
            using (var first = new Fixture())
            {
                first.Editor.Session.ReplaceDocument(converted); first.Call("CurrentGroupCanvasView");
                first.Call("ChangeCanvasViewing", camera, false); key = (string)first.Call("CanvasViewingKey")!;
                var workspace = (WorkspaceState)first.Call("SnapshotWorkspace")!; content = JsonSerializer.Serialize(workspace.LiveDocument);
                Await((Task)first.Call("SaveRecoverySnapshotAsync", workspace, null!, null!)!);
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(profile, "cold-view-expectation.json"), JsonSerializer.Serialize(new ColdRecoveryExpectation(content, key, camera)));
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--canvas-cold-recovery-child"); start.ArgumentList.Add(profile);
            using (var child = System.Diagnostics.Process.Start(start)!)
            {
                var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(25000)) { child.Kill(); throw new TimeoutException("Owned cold recovery child did not complete."); }
                Console.Write(output.GetAwaiter().GetResult()); Console.Write(error.GetAwaiter().GetResult());
                check("a fresh process restores the converted recovery workspace and local observation from disk", child.ExitCode == 0);
            }
            using var restored = new Fixture(); Await((Task)restored.Call("InitializeDocumentsAsync", true)!);
            typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(typeof(DesktopSession).GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(restored.Desktop), true);
            restored.Call("CurrentGroupCanvasView");
            check("cold recovery opens the actual unsaved converted workspace without converting its authored frame again", content == JsonSerializer.Serialize(restored.Editor.Session.Document) &&
                restored.Editor.Session.Document.ResourceId != source.ResourceId && restored.Editor.Session.Document.AuthoringReference!.Frame == converted.AuthoringReference!.Frame);
            Console.WriteLine($"COLD_RECOVERY_VIEW expectedKey={key} actualKey={restored.Call("CanvasViewingKey")} expected={camera} actual={restored.View}");
            check("cold recovery restores local camera identity and leaves the converted copy unbound", (string)restored.Call("CanvasViewingKey")! == key && restored.View.Zoom == camera.Zoom && restored.View.Offset.DistanceTo(camera.Offset) < 1e-7 &&
                restored.Call("FileState", restored.Editor.Session)!.GetType().GetField("Path")!.GetValue(restored.Call("FileState", restored.Editor.Session)) is null);
        }
        finally { ApplicationPaths.Configure(["--profile", originalProfile]); }
    }

    private sealed record ColdRecoveryExpectation(string Document, string Key, CanvasViewTransform View);
    internal static void RunColdRecoveryChild(Action<string, bool> check, string profile)
    {
        ApplicationPaths.Configure(["--profile", profile]);
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var expected = JsonSerializer.Deserialize<ColdRecoveryExpectation>(System.IO.File.ReadAllText(System.IO.Path.Combine(profile, "cold-view-expectation.json")))!;
        using var fixture = new Fixture(); Await((Task)fixture.Call("InitializeDocumentsAsync", true)!);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(typeof(DesktopSession).GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Desktop), true);
        fixture.Call("CurrentGroupCanvasView");
        check("fresh process loads the exact converted recovery content and authoring reference", JsonSerializer.Serialize(fixture.Editor.Session.Document) == expected.Document);
        check("fresh process restores the remembered camera without in-memory identity state", (string)fixture.Call("CanvasViewingKey")! == expected.Key &&
            fixture.View.Zoom == expected.View.Zoom && fixture.View.Offset.DistanceTo(expected.View.Offset) < 1e-7);
    }
    private static void Await(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Cold canvas recovery did not complete.");
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame); Thread.Yield();
        }
        task.GetAwaiter().GetResult();
    }

    private static void LegacyFileView(Action<string, bool> check)
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "canvas-view-legacy-" + Guid.NewGuid().ToString("N") + ".floatcanvas");
        Task.Run(() => CanvasContentImportTests.Legacy(path)).GetAwaiter().GetResult();
        byte[] bytes = System.IO.File.ReadAllBytes(path); Guid firstResource; string firstNamespace;
        var camera = new CanvasViewTransform(1.4, new(80, 30));
        using (var first = new Fixture())
        {
            var loaded = Task.Run(() => new ProjectStore().LoadAsync(path)).GetAwaiter().GetResult(); firstResource = loaded.Document.ResourceId;
            first.Editor.ReplaceSession(new(loaded.Document, loaded.Assets), default);
            firstNamespace = FileNamespace(first.Editor.Session);
            first.Call("BindCanvasIdentity", first.Editor.Session, firstNamespace, false, true); first.Call("CurrentGroupCanvasView");
            first.Call("ChangeCanvasViewing", camera, false);
        }
        using var second = new Fixture(); var again = Task.Run(() => new ProjectStore().LoadAsync(path)).GetAwaiter().GetResult();
        var session = new EditorSession(again.Document, again.Assets); second.Editor.ReplaceSession(session, default);
        string space = FileNamespace(session); second.Call("BindCanvasIdentity", session, space, false, true); second.Call("CurrentGroupCanvasView");
        check("legacy file views survive freshly generated nonpersistent resource IDs on each read", !again.Document.ResourceIdentityPersistent && firstResource != again.Document.ResourceId && firstNamespace == space &&
            second.View.Zoom == camera.Zoom && second.View.Offset.DistanceTo(camera.Offset) < 1e-7 && System.IO.File.ReadAllBytes(path).SequenceEqual(bytes));
        Task.Run(() => new ProjectStore().SaveAsync(path, again.Document.Clone(), again.Assets)).GetAwaiter().GetResult();
        string upgraded = FileNamespace(session); second.Call("BindCanvasIdentity", session, upgraded, true, true);
        check("explicitly saving a legacy file promotes its local views to the persisted resource identity", upgraded != space && again.Document.ResourceIdentityPersistent && second.View.Zoom == camera.Zoom && second.View.Offset.DistanceTo(camera.Offset) < 1e-7);
        string FileNamespace(EditorSession owner) => (string)typeof(DesktopSession).GetMethod("CanvasFileNamespace", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [owner, path])!;
    }

    private static void FramingAndReadonly(Action<string, bool> check)
    {
        using var fixture = new Fixture(); var session = fixture.Editor.Session;
        var document = new SceneDocument { Viewport = new(0, 0, 640, 360) }; var graph = new SceneGraph(document);
        var group = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [], PreviewCanvas = new() { Viewport = new(0, 0, 2000, 1000) } });
        graph.InsertElement(new() { Kind = ElementKind.Rectangle, Width = 8, Height = 8, Visible = false }, group.Id);
        session.ReplaceDocument(document); session.SetEditScope(group.Id); fixture.Call("SetGroupCanvasMode", GroupCanvasMode.Temporary); fixture.Call("CurrentGroupCanvasView");
        check("hidden-content independent opening preserves the authored view", fixture.View == CanvasViewTransform.Identity);
        fixture.Call("ExecuteCanvasViewing", CanvasViewingCommand.FitContent);
        var expected = CanvasViewTransform.Fit(group.PreviewCanvas!.Viewport, new(0, 0, 640, 360), allowEnlarge: true);
        check("hidden-content independent framing uses its own saved canvas reference rather than the adjusted viewport", fixture.View == expected);
        using var imageSurface = SKSurface.Create(new SKImageInfo(2, 2)); imageSurface.Canvas.Clear(SKColors.Blue);
        using var image = imageSurface.Snapshot(); using var data = image.Encode(); string asset = session.Assets.Add(data.ToArray());
        group.PreviewCanvas.Background = new() { Kind = ElementKind.Image, AssetId = asset, Width = 800, Height = 600, Transform = Affine2D.Translation(200, 100) };
        session.NotifyChanged(); fixture.Call("ExecuteCanvasViewing", CanvasViewingCommand.FitContent);
        expected = CanvasViewTransform.Fit(new(200, 100, 800, 600), new(0, 0, 640, 360), allowEnlarge: true);
        check("a visible owned background participates in explicit content framing", fixture.View.Zoom == expected.Zoom && fixture.View.Offset.DistanceTo(expected.Offset) < 1e-7);
        var readOnly = new EditorSession(document.Clone(), session.Assets, readOnly: true); readOnly.SetEditScope(group.Id);
        fixture.Editor.ReplaceSession(readOnly, default); fixture.Editor.Tool = EditorTool.Canvas; fixture.Call("CurrentGroupCanvasView");
        string before = JsonSerializer.Serialize(readOnly.Document);
        var toolbar = (Window)typeof(DesktopSession).GetField("_toolbar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Desktop)!;
        toolbar.ShowActivated = false; toolbar.ShowInTaskbar = false; toolbar.Left = -24000; toolbar.Top = -22000; toolbar.Show(); toolbar.UpdateLayout();
        fixture.Call("OpenCanvasSettings");
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var window = (CanvasToolOptions)typeof(DesktopSession).GetField("_canvasToolOptions", hidden)!.GetValue(fixture.Desktop)!;
        var controls = (System.Windows.Controls.StackPanel)typeof(CanvasToolOptions).GetField("authoringControls", hidden)!.GetValue(window)!;
        check("read-only independent view exposes navigation while disabling all authored controls", window.IsVisible && !controls.IsEnabled);
        CanvasResolutionWorkflowTests.SaveWindow((Window)typeof(DesktopSession).GetField("_toolOptionsWindow", hidden)!.GetValue(fixture.Desktop)!, System.IO.Path.Combine(AppContext.BaseDirectory, "canvas-ui-review", "readonly-navigation.png"));
        fixture.Call("ExecuteCanvasViewing", CanvasViewingCommand.OriginalSize); fixture.Call("ExecuteCanvasViewing", CanvasViewingCommand.CenterContent);
        fixture.Call("ApplyCanvasViewing");
        check("read-only view commands do not mutate its project or create content history", before == JsonSerializer.Serialize(readOnly.Document) && readOnly.UndoCount == 0 && fixture.View.Zoom == 1);
        check("non-default scale badge stays visible even with no editable selection", BadgeHasPixels());
        fixture.Call("ExecuteCanvasViewing", CanvasViewingCommand.Reset);
        check("default view hides the scale badge", !BadgeHasPixels());
        fixture.Call("ChangeCanvasViewing", new CanvasViewTransform(1, new(20, 10)), false);
        check("panning at 100 percent still shows the non-default view badge", BadgeHasPixels());
        fixture.Call("ChangeCanvasViewing", new CanvasViewTransform(2, default), false);
        check("zooming shows the scale badge", BadgeHasPixels());
        fixture.Call("ExecuteCanvasViewing", CanvasViewingCommand.Reset);
        check("resetting a changed view hides the scale badge again", !BadgeHasPixels());

        bool BadgeHasPixels()
        {
            using var surface = SKSurface.Create(new SKImageInfo(640, 360)); surface.Canvas.Clear(SKColors.Transparent);
            fixture.Call("DrawCanvasViewingBadge", surface.Canvas);
            using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
            return pixels.Pixels.Any(pixel => pixel.Alpha > 0);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly OverlayHost host;
        internal DesktopSession Desktop { get; }
        internal EditorController Editor { get; }
        internal CanvasViewTransform View => (CanvasViewTransform)typeof(DesktopSession).GetField("_canvasView", Hidden)!.GetValue(Desktop)!;
        internal Fixture()
        {
            var monitor = new MonitorInfo("apply-hidden", new(0, 0, 640, 360), new(0, 0, 640, 360), 96, 96, true);
            Func<IReadOnlyList<MonitorInfo>> monitors = () => [monitor];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
            Desktop = new(Application.Current, host);
            ((System.Windows.Forms.NotifyIcon)typeof(DesktopSession).GetField("_tray", Hidden)!.GetValue(Desktop)!).Visible = false;
            typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, true); typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
            Editor = (EditorController)typeof(DesktopSession).GetField("_editor", Hidden)!.GetValue(Desktop)!;
            Editor.Tool = EditorTool.Select; Editor.BackgroundPickingEnabled = false;
        }
        internal object? Call(string name, params object[] arguments) => typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(Desktop, arguments);
        public void Dispose() { Desktop.Dispose(); host.Dispose(); }
    }
}
