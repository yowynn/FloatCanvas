using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using FloatCanvas.Storage;
using SkiaSharp;

internal static class BillboardInteractionAuditTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Send(EditorController editor, OverlayPointerKind kind, PointD point) => editor.Pointer(new(kind,
        new(point.X + editor.ScreenOrigin.X, point.Y + editor.ScreenOrigin.Y), OverlayMouseButton.Left, OverlayModifiers.None));

    internal static void Run(Action<string, bool> check)
    {
        Endpoint(check);
        CenteredEditing(check);
        GroupDepthGuards(check);
        DesktopEffectGesture(check);
        DesktopEffects(check);
    }

    private static void GroupDepthGuards(Action<string, bool> check)
    {
        string output = Path.Combine(Path.GetTempPath(), "FloatCanvas-billboard-group-depth", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        EditorSession NewGroup(double firstY, double firstHeight, double secondY, double secondHeight, double size)
        {
            var h = new Projective2D(1, 0, 0, 0, 1, 0, 0, .005, 1);
            var group = new CanvasGroup();
            var scene = new SceneDocument { Viewport = new(0, 0, 320, 165), Groups = [group], Canvas = new() { Projection = new()
                { Enabled = true, Columns = 100, Rows = 100, CellSize = 1, Matrix = h,
                    Quad = [new(0, 0), new(100, 0), new(100 / 1.5, 100 / 1.5), new(0, 100 / 1.5)] } }, Elements =
            [
                new() { Kind = ElementKind.Rectangle, Width = size, Height = 50, IsBillboard = true, GroupId = group.Id,
                    BillboardAnchor = new(100, firstY), BillboardHeight = firstHeight },
                new() { Kind = ElementKind.Rectangle, Width = size, Height = 50, IsBillboard = true, GroupId = group.Id,
                    BillboardAnchor = new(1100, secondY), BillboardHeight = secondHeight }
            ] };
            SceneValidation.Validate(scene);
            var session = new EditorSession(scene); session.SetSelection(scene.Elements.Select(e => e.Id)); return session;
        }
        void Save(EditorSession session, string name)
        {
            SceneValidation.Validate(session.Document);
            string path = Path.Combine(output, name + ".floatcanvas"); var store = new ProjectStore();
            store.SaveAsync(path, session.Document, session.Assets).GetAwaiter().GetResult();
            var reloaded = store.LoadAsync(path).GetAwaiter().GetResult();
            check(name + " remains valid and saves with all billboard metadata", JsonSerializer.Serialize(reloaded.Document) == JsonSerializer.Serialize(session.Document));
        }
        var rotationSession = NewGroup(600, 400, 600, 400, 100);
        var rotationEditor = new EditorController(rotationSession) { Tool = EditorTool.Select };
        var mapping = CanvasMapping.Create(rotationSession.Document); var center = rotationSession.SelectionBounds()!.Value.Center;
        var from = rotationEditor.SelectionScreenRotationHandle!.Value;
        var target = mapping.ToScreen(Affine2D.Around(center, Affine2D.Rotation(-90)).TransformPoint(mapping.ScreenToPlane.TransformPoint(from)));
        check("group horizon rotation regression uses reachable viewport input", rotationSession.Document.Viewport.Contains(from) && rotationSession.Document.Viewport.Contains(target));
        check("reachable group rotation would move an anchor behind the horizon without preflight", rotationSession.Document.Elements.Any(e =>
            !mapping.TryGetDepthScale(Affine2D.Around(center, Affine2D.Rotation(-90)).TransformPoint(e.BillboardAnchor), out _)));
        string before = JsonSerializer.Serialize(rotationSession.Document);
        Send(rotationEditor, OverlayPointerKind.Down, from); Send(rotationEditor, OverlayPointerKind.Move, target); Send(rotationEditor, OverlayPointerKind.Up, target);
        check("group rotation rejects an invalid anchor atomically without hiding another member or creating undo", JsonSerializer.Serialize(rotationSession.Document) == before && rotationSession.UndoCount == 0);
        Save(rotationSession, "rejected-group-rotation");

        var resizeSession = NewGroup(-100, -125, 600, 575, 50);
        var resizeEditor = new EditorController(resizeSession) { Tool = EditorTool.Select };
        mapping = CanvasMapping.Create(resizeSession.Document); center = resizeSession.SelectionBounds()!.Value.Center;
        from = resizeEditor.SelectionScreenCorners[2];
        target = mapping.ToScreen(Affine2D.Around(center, Affine2D.Scale(1, 2)).TransformPoint(mapping.ScreenToPlane.TransformPoint(from)));
        check("group horizon resize regression uses reachable viewport input", resizeSession.Document.Viewport.Contains(from) && resizeSession.Document.Viewport.Contains(target));
        before = JsonSerializer.Serialize(resizeSession.Document);
        Send(resizeEditor, OverlayPointerKind.Down, from); Send(resizeEditor, OverlayPointerKind.Move, target); Send(resizeEditor, OverlayPointerKind.Up, target);
        check("group resize rejects invalid anchors before mutating any member or height", JsonSerializer.Serialize(resizeSession.Document) == before && resizeSession.UndoCount == 0);
        Save(resizeSession, "rejected-group-resize");

        var mixedSession = NewGroup(-100, -125, 600, 575, 50);
        var surface = new CanvasElement { Kind = ElementKind.Rectangle, Width = 20, Height = 20,
            Transform = Affine2D.Translation(290, 15), GroupId = mixedSession.Document.Groups.Single().Id };
        mixedSession.Document.Elements.Add(surface); mixedSession.SetSelection(mixedSession.Document.Elements.Select(e => e.Id));
        var mixedEditor = new EditorController(mixedSession) { Tool = EditorTool.Select };
        mapping = CanvasMapping.Create(mixedSession.Document); center = mixedSession.SelectionBounds()!.Value.Center;
        from = mixedEditor.SelectionScreenCorners[2];
        target = mapping.ToScreen(Affine2D.Around(center, Affine2D.Scale(1, 2)).TransformPoint(mapping.ScreenToPlane.TransformPoint(from)));
        before = JsonSerializer.Serialize(mixedSession.Document);
        Send(mixedEditor, OverlayPointerKind.Down, from); Send(mixedEditor, OverlayPointerKind.Up, target);
        check("invalid mixed-group resize preserves surface members as well as every billboard atomically", mixedSession.Document.Viewport.Contains(from) &&
            mixedSession.Document.Viewport.Contains(target) && JsonSerializer.Serialize(mixedSession.Document) == before && mixedSession.UndoCount == 0);
        Save(mixedSession, "rejected-mixed-group-resize");

        var validSession = NewGroup(600, 50, 600, 50, 100);
        var validEditor = new EditorController(validSession) { Tool = EditorTool.Select };
        mapping = CanvasMapping.Create(validSession.Document); center = validSession.SelectionBounds()!.Value.Center;
        from = validEditor.SelectionScreenRotationHandle!.Value; var rotation = Affine2D.Around(center, Affine2D.Rotation(10));
        target = mapping.ToScreen(rotation.TransformPoint(mapping.ScreenToPlane.TransformPoint(from)));
        var expected = validSession.Document.Elements.Select(e => rotation.TransformPoint(e.BillboardAnchor)).ToArray();
        Send(validEditor, OverlayPointerKind.Down, from); Send(validEditor, OverlayPointerKind.Up, target);
        check("valid all-billboard group rotation keeps its existing layout and single undo behavior", validSession.UndoCount == 1 &&
            validSession.Document.Elements.Select((e, i) => e.BillboardAnchor.DistanceTo(expected[i]) < 1e-6 && e.BillboardHeight == 50).All(v => v));
        Save(validSession, "valid-group-rotation");
    }

    private static void DesktopEffectGesture(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Blur, EditorTool.Pixelate })
        {
            var scene = new SceneDocument { Viewport = new(0, 0, 600, 450) };
            var projection = CanvasCalibration.CreateInitial(scene.Viewport); projection.Enabled = true;
            projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .001, 1); scene.Canvas.Projection = projection;
            var session = new EditorSession(scene); var editor = new EditorController(session) { Tool = tool, BillboardMode = true };
            RectD? requested = null; bool retainedAtEvent = false;
            editor.DesktopEffectRequested += (region, _, _, _) => { requested = region; retainedAtEvent = editor.ActiveBillboardDepthLine is not null; };
            Send(editor, OverlayPointerKind.Down, new(300, 300)); Send(editor, OverlayPointerKind.Up, new(300, 300));
            var depth = editor.ActiveBillboardDepthLine!.Value;
            Send(editor, OverlayPointerKind.Down, new(100, 100)); Send(editor, OverlayPointerKind.Move, new(180, 150));
            check($"{tool} new desktop mask previews selected depth and screen-vertical center guide", editor.BillboardDrawingGuide is { } guide &&
                Math.Abs(guide.Center.X - guide.Anchor.X) < 1e-6 && guide.Center.DistanceTo(new(140, 125)) < 1e-6 &&
                editor.MarqueeScreenPolygon[2].DistanceTo(new(180, 150)) < 1e-6);
            Send(editor, OverlayPointerKind.Up, new(180, 150));
            check($"{tool} desktop mask completion forwards logical region and frozen depth without empty history", requested is { } logical &&
                Math.Abs(logical.Width - 80 / depth.Scale) < 1e-6 && Math.Abs(logical.Height - 50 / depth.Scale) < 1e-6 && retainedAtEvent &&
                editor.ActiveBillboardDepthLine is null && session.UndoCount == 0);
        }
    }

    private static void CenteredEditing(Action<string, bool> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 600, 450) };
        var projection = CanvasCalibration.CreateInitial(scene.Viewport); projection.Enabled = true;
        projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .0005, 1); scene.Canvas.Projection = projection;
        var box = new CanvasElement { Kind = ElementKind.Rectangle, Width = 200, Height = 100, IsBillboard = true,
            BillboardAnchor = new(300, 300), BillboardHeight = 50, Transform = Affine2D.Rotation(25), Style = new() { StrokeWidth = 5 } };
        var session = new EditorSession(scene); scene.Elements.Add(box); session.SetSelection([box.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        var center = editor.BillboardSelectionGuide!.Value.Center; var anchor = box.BillboardAnchor;
        var corner = editor.SelectionScreenCorners[2]; var target = center + (corner - center) * 1.5;
        Send(editor, OverlayPointerKind.Down, corner); Send(editor, OverlayPointerKind.Move, target); Send(editor, OverlayPointerKind.Up, target);
        check("billboard corner resize keeps approved center and ground anchor", box.BillboardAnchor == anchor && box.BillboardHeight == 50 &&
            editor.BillboardSelectionGuide!.Value.Center.DistanceTo(center) < 1e-6);
        check("billboard rectangle resize edits logical dimensions while retaining uniform logical stroke", Math.Abs(box.Width - 300) < 1e-6 &&
            Math.Abs(box.Height - 150) < 1e-6 && box.Style.StrokeWidth == 5 && Math.Abs(box.Transform.MaximumScale - 1) < 1e-6);
        var image = new CanvasElement { Kind = ElementKind.Image, Width = 200, Height = 100, IsBillboard = true,
            BillboardAnchor = anchor, BillboardHeight = 50 };
        session = new EditorSession(scene.Clone()); session.Document.Elements = [image]; session.SetSelection([image.Id]);
        editor = new EditorController(session) { Tool = EditorTool.Crop };
        var mapping = CanvasMapping.Create(session.Document); center = mapping.ElementToScreen(image).TransformPoint(new(100, 50));
        var cropStart = mapping.ElementToScreen(image).TransformPoint(new(20, 10)); var cropEnd = mapping.ElementToScreen(image).TransformPoint(new(100, 60));
        Send(editor, OverlayPointerKind.Down, cropStart); Send(editor, OverlayPointerKind.Move, cropEnd); Send(editor, OverlayPointerKind.Up, cropEnd);
        check("billboard crop keeps center and ground anchor while retaining normalized source coordinates", image.BillboardAnchor == anchor &&
            mapping.ElementToScreen(image).TransformPoint(ElementGeometry.LocalBounds(image).Center).DistanceTo(center) < 1e-6 &&
            image.ImageCrop is { } crop && Math.Abs(crop.X - .1) < 1e-6 && Math.Abs(crop.Y - .1) < 1e-6 && Math.Abs(crop.Width - .4) < 1e-6 && Math.Abs(crop.Height - .5) < 1e-6);
        var plain = new EditorSession(); editor.BillboardMode = true; editor.ReplaceSession(plain, default);
        plain.Document.Canvas.Projection.Enabled = true; plain.NotifyChanged();
        check("switching through a nonperspective canvas resets global billboard mode", !editor.BillboardMode);
    }

    private static void Endpoint(Action<string, bool> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 600, 450) };
        var projection = CanvasCalibration.CreateInitial(scene.Viewport); projection.Enabled = true;
        scene.Canvas.Projection = projection;
        var line = new CanvasElement { Kind = ElementKind.Line, Points = [new(0, 0), new(200, 0)],
            IsBillboard = true, BillboardAnchor = new(250, 250), BillboardHeight = 50,
            Style = new() { StrokeWidth = 5 } };
        var session = new EditorSession(scene); session.Document.Elements.Add(line); session.SetSelection([line.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        var mapping = CanvasMapping.Create(scene); var from = mapping.ElementToScreen(line).TransformPoint(line.Points[^1]);
        var target = from + new PointD(40, 0);
        var opposite = mapping.ElementToScreen(line).TransformPoint(line.Points[0]);
        mapping.TryGetDepthScale(line.BillboardAnchor, out double originalScale);
        Send(editor, OverlayPointerKind.Down, from); Send(editor, OverlayPointerKind.Move, target); Send(editor, OverlayPointerKind.Up, target);
        var renderedEnd = mapping.ElementToScreen(line).TransformPoint(line.Points[^1]);
        mapping.TryGetDepthScale(line.BillboardAnchor, out double finalScale);
        check("billboard endpoint edit reaches pointer while retaining opposite endpoint and depth", renderedEnd.DistanceTo(target) < 1e-6 &&
            mapping.ElementToScreen(line).TransformPoint(line.Points[0]).DistanceTo(opposite) < 1e-6 && Math.Abs(finalScale - originalScale) < 1e-6);
        check("billboard endpoint edit is one undo operation", session.UndoCount == 1 && session.Undo() && session.Document.Elements.Single().Points[^1] == new PointD(200, 0));
    }

    private static void DesktopEffects(Action<string, bool> check)
    {
        string originalProfile = ApplicationPaths.ProfileDirectory; var originalScope = ApplicationPaths.RequestedMonitorScope;
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-billboard-effect-audit", Guid.NewGuid().ToString("N"));
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var context = SynchronizationContext.Current; var originalWindows = app.Windows.Cast<Window>().ToArray();
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]); new UserSettings { Theme = "light" }.Save();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        DesktopSession? desktop = null; OverlayHost? host = null;
        try
        {
            foreach (int build in new[] { 14393, 19041 })
            foreach (var kind in new[] { ImageEffectKind.Blur, ImageEffectKind.Pixelate })
            {
                var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
                var bounds = new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 30, 128, 96);
                var bounded = primary with { Bounds = bounds, WorkArea = bounds };
                Func<IReadOnlyList<MonitorInfo>> provider = () => [bounded];
                host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
                    [OverlayMonitorScope.PrimaryScreen, provider, false, WindowsCapabilities.FromVersion(new Version(10, 0, build))], null)!;
                desktop = new DesktopSession(app, host); Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
                var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", Private)!;
                (isolation.GetValue(host) as IDisposable)?.Dispose(); isolation.SetValue(host, null);
                typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
                var editor = Field<EditorController>(desktop, "_editor"); var document = editor.Session.Document;
                var projection = CanvasCalibration.CreateInitial(document.Viewport);
                projection.Enabled = true; projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .0025, 1);
                projection.Quad = projection.Quad.Select(projection.Matrix.TransformPoint).ToList(); document.Canvas.Projection = projection;
                editor.Tool = EditorTool.Rectangle; editor.BillboardMode = true;
                Send(editor, OverlayPointerKind.Down, new(64, 70)); Send(editor, OverlayPointerKind.Up, new(64, 70));
                var depth = editor.ActiveBillboardDepthLine!.Value; var screenRegion = new RectD(20, 20, 50, 25);
                var logicalRegion = new RectD(screenRegion.X / depth.Scale, screenRegion.Y / depth.Scale, screenRegion.Width / depth.Scale, screenRegion.Height / depth.Scale);
                int captures = 0;
                Func<Task<CapturedDesktop>> capture = () =>
                {
                    captures++; editor.ClearBillboardDepth();
                    using var surface = SKSurface.Create(new SKImageInfo(bounds.Width, bounds.Height)); surface.Canvas.Clear(SKColors.Blue);
                    using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    return Task.FromResult(new CapturedDesktop(bounds, data.ToArray()));
                };
                var operation = (Task)typeof(DesktopSession).GetMethod("AddDesktopEffectCoreAsync", Private)!.Invoke(desktop,
                    [logicalRegion, kind, 7d, 0xFF112233u, capture])!;
                if (build >= 19041)
                {
                    // This hidden route test supplies the accepted readiness notification;
                    // actual WGC readiness and screen pixels belong to the existing native fixture.
                    var pending = Field<object>(desktop, "_liveEffectPreparation");
                    var ready = (TaskCompletionSource<bool>)pending.GetType().GetProperty("Ready", Private)!.GetValue(pending)!;
                    ready.TrySetResult(true);
                }
                Await(operation);
                var element = editor.Session.Document.Elements.Single();
                var projected = CanvasMapping.Create(document).ProjectedBounds(element, false);
                check($"{kind} Windows {build} new desktop filter retains billboard depth and screen bounds", element.IsBillboard &&
                    projected.Center.DistanceTo(screenRegion.Center) < 1e-6 && Math.Abs(projected.Width - 50) < 1e-6 && Math.Abs(projected.Height - 25) < 1e-6);
                check($"{kind} Windows {build} routes live mask or frozen bitmap without extra capture", captures == (build < 19041 ? 1 : 0) &&
                    element.Kind == (build < 19041 ? ElementKind.Image : kind == ImageEffectKind.Blur ? ElementKind.LiveBlur : ElementKind.LivePixelate));
                check($"{kind} Windows {build} creates one undo and keeps native surfaces hidden", editor.Session.UndoCount == 1 && !host.IsEditing &&
                    host.Diagnostics.All(pair => !pair.DisplayVisible && !pair.InputVisible));
                if (element.AssetId is { } asset)
                {
                    using var bitmap = SKBitmap.Decode(editor.Session.Assets.Get(asset).Data.ToArray());
                    check($"{kind} legacy fallback stores original screen-sized pixels separately from logical dimensions", bitmap.Width == 50 && bitmap.Height == 25 && Math.Abs(element.Width - 50 / depth.Scale) < 1e-6);
                }
                desktop.Dispose(); desktop = null; host = null;
            }
        }
        finally
        {
            if (desktop is not null) desktop.Dispose(); else host?.Dispose();
            foreach (var window in app.Windows.Cast<Window>().Except(originalWindows).ToArray()) window.Close();
            var restore = new List<string> { "--profile", originalProfile };
            if (originalScope is { } scope) restore.Add(scope == OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens");
            ApplicationPaths.Configure(restore.ToArray()); SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static void Await(Task task)
    {
        if (task.IsCompleted) { task.GetAwaiter().GetResult(); return; }
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(15);
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!task.IsCompleted) throw new TimeoutException("Billboard desktop effect command did not complete.");
        task.GetAwaiter().GetResult();
    }
}
