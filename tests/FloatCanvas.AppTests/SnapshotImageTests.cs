using System.IO;
using System.IO.Compression;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Exercises the production snapshot source, insertion and persistence seams without capturing the user's desktop.</summary>
internal static class SnapshotImageTests
{
    internal static void Run(Action<string, bool> check) => Task.Run(() => RunAsync(check)).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<string, bool> check)
    {
        SourcePixels(check);
        PrefabBackgroundOnly(check);
        LayerOpacityAndLiveEffects(check);
        RequestGuards(check);
        ProjectedCaptureShapes(check);
        BillboardSnapshots(check);
        ViewedPixelsAndInsertion(check);
        await InsertAndPersistenceAsync(check);
    }

    private static void ViewedPixelsAndInsertion(Action<string, bool> check)
    {
        var camera = new CanvasViewTransform(.5, new(-100, 20));
        var source = new SceneDocument { Viewport = new(0, 0, 320, 180), Elements = [Box(400, 40, 40, 30, 0xff00ff00)] };
        source.Canvas.Grid.Visible = false;
        source.Elements.Add(new() { Kind = ElementKind.LivePixelate, Width = 60, Height = 40, Transform = Affine2D.Translation(430, 100),
            Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 4 }], Style = new() { StrokeWidth = 0 } });
        var desktop = Desktop(new(0, 0, 640, 360), (x, _) => x < 200 ? SKColors.Blue : SKColors.Red);
        string before = JsonSerializer.Serialize(source);
        foreach (var root in Enum.GetValues<RootCanvasKind>())
        {
            SceneCanvasRoots.Assign(source, source.Elements[0].Id, root);
            foreach (var kind in Enum.GetValues<CaptureSource>())
            {
                var selection = Selection(kind, new(90, 30, 80, 80));
                var raster = SnapshotImageCommand.Render(source, [], selection, desktop, [new(0, 0, 640, 360)], viewing: camera);
                using var pixels = Decode(raster);
                check($"viewed {kind}/{root} capture retains the physical selection allocation", raster.Bounds == selection.Bounds && pixels.Width == 80 && pixels.Height == 80);
                check($"viewed {kind}/{root} draws moved canvas ink only when canvas is requested", pixels.GetPixel(20, 15) == (kind == CaptureSource.Background ? SKColors.Blue : SKColors.Lime));
                check($"viewed {kind}/{root} keeps the actual desktop unscaled and CanvasOnly transparent", kind == CaptureSource.CanvasOnly ? pixels.GetPixel(65, 15).Alpha == 0 : pixels.GetPixel(65, 15) == SKColors.Blue);
                check($"viewed {kind}/{root} filter samples its authored desktop position", pixels.GetPixel(40, 50) == (kind == CaptureSource.Background ? SKColors.Blue : SKColors.Red));
            }
        }
        source.RootCanvasAssignments.Clear();
        check("view capture preserves original scene and live filter parameters", before == JsonSerializer.Serialize(source));
        var plain = new SceneDocument { Viewport = source.Viewport, Elements = [source.Elements[0].Clone()] }; plain.Canvas.Grid.Visible = false;
        var snapshot = SnapshotImageCommand.Render(plain, [], Selection(CaptureSource.CanvasOnly, new(90, 30, 80, 80)), null, [new(0, 0, 640, 360)], viewing: camera);
        var session = new EditorSession(plain); int history = session.UndoCount; var mapping = CanvasMapping.CreateViewing(plain, camera);
        var inserted = SnapshotImageCommand.Insert(session, snapshot, mapping.PlaneToScreen, capturedMapping: mapping);
        var world = new SceneGraph(session.Document).ToWorld(inserted);
        var frame = CanvasMapping.CreateViewing(session.Document, camera).ElementToScreen(world);
        check("viewed snapshot insertion preserves on-screen position and size without applying the camera twice", frame.TransformPoint(default).DistanceTo(new(90, 30)) < 1e-7 &&
            frame.TransformPoint(new(inserted.Width, inserted.Height)).DistanceTo(new(170, 110)) < 1e-7 && session.UndoCount == history + 1);
        check("viewed snapshot remains an ordinary image removable in one undo", inserted.Kind == ElementKind.Image && session.Undo() && session.Document.Elements.Count == 1);
        ViewedGroupSnapshot(check);
        foreach (bool perspective in new[] { false, true })
        {
            var billboardDocument = perspective ? ProjectedDocument() : new SceneDocument { Viewport = new(0, 0, 800, 600) };
            var billboardSession = new EditorSession(billboardDocument); var billboardView = new CanvasViewTransform(.75, new(50, 20));
            var billboardMapping = CanvasMapping.CreateViewing(billboardDocument, billboardView);
            var image = SnapshotImageCommand.Insert(billboardSession, new(Png(80, 40, (_, _) => SKColors.Lime), new(150, 100, 80, 40), true),
                billboardMapping.PlaneToScreen, billboardScreenFirst: true, capturedMapping: billboardMapping);
            var transform = billboardMapping.ElementToScreen(image);
            check("viewed screen-first billboard snapshot inserts at the captured pixel rectangle " + perspective, image.IsBillboard &&
                transform.TransformPoint(default).DistanceTo(new(150, 100)) < 1e-7 && transform.TransformPoint(new(image.Width, image.Height)).DistanceTo(new(230, 140)) < 1e-7);
        }
    }

    private static void ViewedGroupSnapshot(Action<string, bool> check)
    {
        var document = new SceneDocument { Viewport = new(0, 0, 640, 360) }; var graph = new SceneGraph(document);
        var group = graph.InsertGroup(new() { Transform = Affine2D.Translation(100, 80).Compose(Affine2D.Scale(2, 2)), OrderMode = GroupOrderMode.AtomicBlock,
            ChildOrder = [], PreviewCanvas = new() { Origin = new(30, 40), Viewport = document.Viewport } });
        var leaf = graph.InsertElement(Box(100, 50, 40, 30, 0xff00ff00), group.Id, worldCoordinates: false);
        var session = new EditorSession(document); session.SetEditScope(group.Id);
        var display = new GroupCanvasScene().Capture(session, null, GroupCanvasMode.Temporary, default, session.Assets, document.Viewport);
        var camera = new CanvasViewTransform(2, new(20, 10));
        var mapping = CanvasMapping.CreateView(display.Document, display.EditingPlacement!.Value, display.EditingRootCanvas, camera);
        var corner = mapping.ElementToScreen(graph.ToWorld(leaf)).TransformPoint(default);
        var selection = Selection(CaptureSource.CanvasOnly, new(corner.X - 10, corner.Y - 10, 100, 100));
        var raster = SnapshotImageCommand.Render(display.Document, [], selection, null, [document.Viewport], viewing: camera);
        using var pixels = Decode(raster);
        check("independent group snapshot samples its displayed contents despite host placement and nonzero origin", pixels.GetPixel(20, 20) == SKColors.Lime && pixels.GetPixel(5, 5).Alpha == 0);
        int history = session.UndoCount; var added = SnapshotImageCommand.Insert(session, raster, mapping.PlaneToScreen, capturedMapping: mapping);
        var stored = session.Document.Elements.Single(element => element.Id == added.Id);
        var actual = mapping.ElementToScreen(new SceneGraph(session.Document).ToWorld(stored));
        check("independent snapshot insertion converts displayed pixels back through the host into local group coordinates", stored.GroupId == group.Id &&
            actual.TransformPoint(default).DistanceTo(new(selection.Bounds.X, selection.Bounds.Y)) < 1e-7 &&
            actual.TransformPoint(new(stored.Width, stored.Height)).DistanceTo(new(selection.Bounds.Right, selection.Bounds.Bottom)) < 1e-7 && session.UndoCount == history + 1);
        check("independent snapshot undo preserves the group placement and original child", session.Undo() && session.Document.Elements.Count == 1 && session.Document.Groups.Single().Transform == group.Transform);
    }

    private static SceneDocument ProjectedDocument() => new()
    {
        Viewport = new(0, 0, 800, 600),
        Canvas = new() { Projection = new() { Enabled = true,
            Matrix = new(1, .08, 0, 0, 1, 0, 0, .001, 1),
            Quad = [new(0,0),new(800,0),new(600,400),new(0,400)] } }
    };

    private static void PrefabBackgroundOnly(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); string stored = assets.Add(Png(120, 80, (_, _) => SKColors.Lime));
        var nested = new PrefabDefinition { Name = "nested red", Graph = new() { Elements = [Box(0, 0, 120, 80, 0xFFFF0000)] } };
        var child = new PrefabInstance { DefinitionId = nested.Id };
        var definition = new PrefabDefinition { Name = "outer", Graph = new() { Instances = [child], RootOrder = [child.Id] } };
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [], Transform = Affine2D.Translation(-80, -40) };
        var instance = new PrefabInstance { DefinitionId = definition.Id, ParentId = group.Id }; group.ChildOrder = [instance.Id];
        var annotation = Box(-75, -35, 100, 70, 0xFFFFFF00);
        var source = new SceneDocument { Viewport = new(-80, -40, 120, 80), LayerOpacity = .3, Elements = [annotation], Groups = [group],
            Instances = [instance], Definitions = [definition, nested], RootOrder = [annotation.Id, group.Id],
            Background = new() { Kind = ElementKind.Image, AssetId = stored, Width = 120, Height = 80, Transform = Affine2D.Translation(-80, -40) } };
        source.Canvas.BackgroundColor = 0xFF992266; source.Canvas.Grid.Visible = true; source.Canvas.Grid.FlatCellSize = 4;
        var selection = Selection(CaptureSource.Background, new(-70, -30, 70, 50));
        var desktop = Desktop(new(-80, -40, 120, 80), (_, _) => SKColors.Blue);
        string original = JsonSerializer.Serialize(source); var originalBytes = assets.Get(stored).Data.ToArray(); int resourceCount = assets.Count;
        var raster = SnapshotImageCommand.Render(source, assets.Resources, selection, desktop, [source.Viewport]);
        using var background = Decode(raster);
        check("prefab background-only capture excludes authored layers groups instances definitions grid and stored background", background.Pixels.All(pixel => pixel == SKColors.Blue) &&
            raster.Bounds == selection.Bounds && background.Width == 70 && background.Height == 50);
        using var combined = Decode(SnapshotImageCommand.Render(source, assets.Resources, selection with { Source = CaptureSource.BackgroundAndCanvas }, desktop, [source.Viewport]));
        check("the same nested prefab remains visible when the capture source includes canvas content", combined.GetPixel(35, 25) == SKColors.Red);
        check("prefab background-only preparation preserves the exact source graph definitions opacity and PNG assets", JsonSerializer.Serialize(source) == original &&
            assets.Count == resourceCount && assets.Get(stored).Data.Span.SequenceEqual(originalBytes));
    }

    private static void ProjectedCaptureShapes(Action<string, bool> check)
    {
        foreach (var shape in new[] { CaptureShape.Rectangle, CaptureShape.Ellipse })
        {
            var source = ProjectedDocument();
            source.Elements.Add(Box(0, 0, 1400, 1400, 0xFF0088FF));
            var controller = new EditorController(new(source)) { Tool = EditorTool.CaptureRegion, CaptureShape = shape,
                CaptureSource = CaptureSource.CanvasOnly, CaptureBorderWidth = 0 };
            var mapping = CanvasMapping.Create(source);
            CaptureSelection? selected = null;
            controller.CaptureSelectionCompleted += value => selected = value;
            void Point(OverlayPointerKind kind, PointD plane) => controller.Pointer(new(kind,
                new(mapping.ToScreen(plane).X, mapping.ToScreen(plane).Y), OverlayMouseButton.Left, OverlayModifiers.None, 0));
            Point(OverlayPointerKind.Down, new(100, 100)); Point(OverlayPointerKind.Up, new(240, 190));
            check(shape + " capture uses a projected plane outline", selected is { PlaneBounds: { } bounds } &&
                Math.Abs(bounds.Width - 140) < 1e-7 && Math.Abs(bounds.Height - 90) < 1e-7 &&
                selected.Points.Count == (shape == CaptureShape.Rectangle ? 4 : 128));
            var raster = SnapshotImageCommand.Render(source, [], selected!, null, [source.Viewport]);
            var target = new EditorSession(ProjectedDocument());
            var image = SnapshotImageCommand.Insert(target, raster);
            check(shape + " capture preserves its encoded pixels during insertion", target.Assets.Get(image.AssetId!).Data.Span.SequenceEqual(raster.PngBytes));
            var selectedImage = new EditorController(target) { Tool = EditorTool.Select };
            var frame = selectedImage.SelectionScreenCorners.Select(mapping.ScreenToPlane.TransformPoint).ToArray();
            check(shape + " screenshot selection frame is rectangular on the canvas plane", frame.Length == 4 &&
                Math.Abs(frame[0].Y - frame[1].Y) < 1e-7 && Math.Abs(frame[1].X - frame[2].X) < 1e-7 &&
                Math.Abs(frame[2].Y - frame[3].Y) < 1e-7 && Math.Abs(frame[3].X - frame[0].X) < 1e-7);
            var planeBounds = RectD.FromPoints(frame[0], frame[2]);
            check(shape + " screenshot frame exactly matches the captured plane region", selected!.PlaneBounds is { } capturedPlane &&
                Math.Abs(planeBounds.Left - capturedPlane.Left) < 1e-7 && Math.Abs(planeBounds.Top - capturedPlane.Top) < 1e-7 &&
                Math.Abs(planeBounds.Right - capturedPlane.Right) < 1e-7 && Math.Abs(planeBounds.Bottom - capturedPlane.Bottom) < 1e-7);
            void Drag(PointD from, PointD to)
            {
                foreach (var (kind, point) in new[] { (OverlayPointerKind.Down, from), (OverlayPointerKind.Move, to), (OverlayPointerKind.Up, to) })
                    selectedImage.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None, 0));
            }
            var center = planeBounds.Center;
            Drag(mapping.ToScreen(frame[2]), mapping.ToScreen(center + (frame[2] - center) * 1.25));
            var expectedScale = Affine2D.Around(center, Affine2D.Scale(1.25, 1.25));
            check(shape + " screenshot resizes from its projected plane-frame corner", target.UndoCount == 2 &&
                target.Document.Elements.Single().Transform.TransformPoint(center).DistanceTo(center) < 1e-7 &&
                target.Document.Elements.Single().Transform.TransformPoint(frame[2]).DistanceTo(expectedScale.TransformPoint(frame[2])) < 1e-7 &&
                target.Assets.Get(image.AssetId!).Data.Span.SequenceEqual(raster.PngBytes));
            check(shape + " screenshot plane-frame resize undoes in one step", target.Undo() && target.Document.Elements.Single().Transform == image.Transform);
            var rotationStart = selectedImage.SelectionScreenRotationHandle!.Value;
            var rotationPoint = mapping.ScreenToPlane.TransformPoint(rotationStart);
            Drag(rotationStart, mapping.ToScreen(center + Affine2D.Rotation(20).TransformPoint(rotationPoint - center)));
            var rotated = target.Document.Elements.Single().Transform;
            var expectedRotation = Affine2D.Around(center, Affine2D.Rotation(20));
            check(shape + " screenshot rotates from the new frame handle without altering its pixels", target.UndoCount == 2 &&
                rotated.TransformPoint(center).DistanceTo(center) < 1e-7 &&
                rotated.TransformPoint(center + new PointD(1, 0)).DistanceTo(expectedRotation.TransformPoint(center + new PointD(1, 0))) < 1e-7 &&
                target.Assets.Get(image.AssetId!).Data.Span.SequenceEqual(raster.PngBytes));
            check(shape + " screenshot plane-frame rotation undoes in one step", target.Undo() && target.Document.Elements.Single().Transform == image.Transform);
            target.Document.Canvas.Projection.Enabled = false;
            using var renderer = new SceneRenderer();
            using var pixels = SKBitmap.Decode(renderer.ExportPng(target.Document, target.Assets, transparent: true));
            check(shape + " capture becomes the intended plane shape when perspective is disabled",
                pixels.GetPixel(170, 145).Alpha > 250 && pixels.GetPixel(95, 145).Alpha == 0 && pixels.GetPixel(245, 145).Alpha == 0 &&
                (shape == CaptureShape.Rectangle ? pixels.GetPixel(106, 106).Alpha > 250 : pixels.GetPixel(106, 106).Alpha == 0));
        }
        var lassoScene = ProjectedDocument();
        var lasso = new EditorController(new(lassoScene)) { Tool = EditorTool.CaptureRegion, CaptureShape = CaptureShape.FreehandLasso };
        CaptureSelection? contour = null; lasso.CaptureSelectionCompleted += value => contour = value;
        foreach (var (kind, point) in new[] { (OverlayPointerKind.Down,new OverlayPoint(100,100)),
            (OverlayPointerKind.Move,new OverlayPoint(240,100)), (OverlayPointerKind.Move,new OverlayPoint(240,190)),
            (OverlayPointerKind.Up,new OverlayPoint(100,190)) }) lasso.Pointer(new(kind,point,OverlayMouseButton.Left,OverlayModifiers.None,0));
        check("perspective lasso retains its screen-drawn contour", contour?.PlaneBounds is null && contour?.Points.SequenceEqual(
            new[] {new PointD(100,100),new PointD(240,100),new PointD(240,190),new PointD(100,190)}) == true);
    }

    private static void BillboardSnapshots(Action<string, bool> check)
    {
        var session = new EditorSession(ProjectedDocument());
        var mapping = CanvasMapping.Create(session.Document);
        check("snapshot depth can be fixed independently of the captured screen region", mapping.TryGetEqualDepthLine(new(150,250), out var depth));
        var bounds = new RectD(90, 60, 100, 80);
        var png = Png(100,80,(_,_) => SKColors.Blue);
        var element = SnapshotImageCommand.Insert(session,new(png,bounds,true),mapping.PlaneToScreen,depth);
        var screen = mapping.ElementToScreen(element);
        check("billboard screenshot stores shared logical dimensions while retaining exact screen placement",
            element.IsBillboard && Math.Abs(element.Width - bounds.Width / depth.Scale) < 1e-7 &&
            Math.Abs(element.Height - bounds.Height / depth.Scale) < 1e-7 &&
            screen.TransformPoint(default).DistanceTo(new(bounds.X,bounds.Y)) < 1e-7 &&
            screen.TransformPoint(new(element.Width,element.Height)).DistanceTo(new(bounds.Right,bounds.Bottom)) < 1e-7);
        check("billboard screenshot preserves source pixels, selects one image and uses one undo entry",
            session.Assets.Get(element.AssetId!).Data.Span.SequenceEqual(png) && session.UndoCount == 1 && session.Selection.SetEquals([element.Id]));
        var imports = new EditorSession(ProjectedDocument());
        ImageImportCommand.InsertBillboard(imports,[new(png,100,80)],imports.Document.Viewport,depth,new(300,200));
        var imported = imports.Document.Elements.Single();
        check("fresh image insertion uses the chosen depth and projected screen center", imported.IsBillboard &&
            CanvasMapping.Create(imports.Document).ElementToScreen(imported).TransformPoint(ElementGeometry.LocalBounds(imported).Center).DistanceTo(new(300,200)) < 1e-7 &&
            imports.Assets.Get(imported.AssetId!).Data.Span.SequenceEqual(png) && imports.UndoCount == 1);
    }

    private static void SourcePixels(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        string frozen = assets.Add(Png(120, 80, (_, _) => SKColors.Lime));
        var scene = new SceneDocument
        {
            Viewport = new(-80, -40, 120, 80), LayerOpacity = .25,
            Background = new() { Kind = ElementKind.Image, AssetId = frozen, Width = 120, Height = 80, Transform = Affine2D.Translation(-80, -40) },
            Elements = [Box(-60, -25, 20, 20, 0x80FF0000)]
        };
        var selection = Selection(CaptureSource.Background, new(-70, -30, 70, 50));
        var desktop = Desktop(new(-80, -40, 120, 80), (_, _) => SKColors.Blue);
        RectD[] displays = [scene.Viewport];
        string original = JsonSerializer.Serialize(scene);
        var resourceBytes = assets.Resources.ToDictionary(a => a.Id, a => a.Data.ToArray());
        var background = SnapshotImageCommand.Render(scene, assets.Resources, selection, desktop, displays);
        var combined = SnapshotImageCommand.Render(scene, assets.Resources, selection with { Source = CaptureSource.BackgroundAndCanvas }, desktop, displays);
        var canvas = SnapshotImageCommand.Render(scene, assets.Resources, selection with { Source = CaptureSource.CanvasOnly }, null, displays);
        using var bg = Decode(background); using var all = Decode(combined); using var overlay = Decode(canvas);
        check("snapshot background source uses the fresh desktop and excludes original annotations", bg.GetPixel(20, 15) == SKColors.Blue && bg.GetPixel(60, 40) == SKColors.Blue);
        check("snapshot combined source composites the fresh desktop and annotation pixels", all.GetPixel(20, 15).Alpha == 255 && all.GetPixel(20, 15).Red is >= 127 and <= 128 && all.GetPixel(20, 15).Blue is >= 127 and <= 128);
        check("snapshot canvas-only source preserves element alpha and omits both desktop and stored background", overlay.GetPixel(20, 15).Alpha is >= 127 and <= 128 && overlay.GetPixel(20, 15).Red == 255 && overlay.GetPixel(60, 40).Alpha == 0);
        check("all capture sources retain exact original physical coordinates and dimensions", background.Bounds == selection.Bounds && combined.Bounds == selection.Bounds && canvas.Bounds == selection.Bounds && bg.Width == 70 && bg.Height == 50);
        check("source preparation leaves the live document, original background and opacity unchanged", JsonSerializer.Serialize(scene) == original);
        check("source preparation does not append resources or alter original PNG bytes", assets.Count == resourceBytes.Count && assets.Resources.All(r => r.Data.Span.SequenceEqual(resourceBytes[r.Id])));
        foreach (var source in new[] { CaptureSource.Background, CaptureSource.BackgroundAndCanvas })
        {
            bool missing = false;
            try { SnapshotImageCommand.Render(scene, assets.Resources, selection with { Source = source }, null, displays); }
            catch (InvalidOperationException) { missing = true; }
            check($"{source} rejects missing desktop instead of reusing an old frozen background", missing);
        }
        var empty = new SceneDocument { Viewport = scene.Viewport };
        var invisible = SnapshotImageCommand.Render(empty, [], selection with { Source = CaptureSource.CanvasOnly }, null, displays);
        var emptySession = new EditorSession(empty);
        bool refused = false;
        try { SnapshotImageCommand.Insert(emptySession, invisible); }
        catch (InvalidOperationException) { refused = true; }
        check("an empty canvas-only result does not create an invisible image, asset or undo entry", !invisible.HasVisiblePixels && refused && emptySession.Document.Elements.Count == 0 && emptySession.Assets.Count == 0 && emptySession.UndoCount == 0);
        var gap = SnapshotImageCommand.Render(scene, assets.Resources, selection, desktop,
            [new(-80, -40, 45, 80), new(-25, -40, 65, 80)]);
        using var gapPixels = Decode(gap);
        check("production source rendering preserves physical monitor gaps", gapPixels.GetPixel(38, 20).Alpha == 0 && gapPixels.GetPixel(20, 20) == SKColors.Blue && gapPixels.GetPixel(55, 20) == SKColors.Blue);
    }

    private static void LayerOpacityAndLiveEffects(Action<string, bool> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 100, 80), LayerOpacity = .25, Elements = [Box(10, 10, 30, 30, 0xFFFF0000)] };
        var capture = SnapshotImageCommand.Render(scene, [], Selection(CaptureSource.CanvasOnly, new(5, 5, 50, 50)), null, [scene.Viewport]);
        using var raster = Decode(capture);
        check("snapshot PNG does not bake the live canvas's global layer opacity", raster.GetPixel(15, 15) == SKColors.Red);
        var resultSession = new EditorSession(new() { Viewport = scene.Viewport, LayerOpacity = .25 });
        var element = SnapshotImageCommand.Insert(resultSession, capture);
        using var renderer = new SceneRenderer();
        using var displayed = SKBitmap.Decode(renderer.ExportPng(resultSession.Document, resultSession.Assets, true));
        check("inserted snapshot receives global layer opacity only in the normal image render pass", displayed.GetPixel(20, 20).Alpha is >= 63 and <= 64 && element.Style.Opacity == 1);
        check("source preparation preserves the original canvas opacity", scene.LayerOpacity == .25);

        var viewport = new RectD(-80, -40, 160, 120);
        var live = new SceneDocument { Viewport = viewport, LayerOpacity = .4,
            Elements = [new() { Kind = ElementKind.LivePixelate, Width = 90, Height = 60,
                Transform = Affine2D.Translation(-60, -20), Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 17 }], Style = new() { StrokeWidth = 0 } }] };
        var desktop = Desktop(new(-80, -40, 160, 120), (x, y) => x % 13 < 5 ? SKColors.White : y % 9 < 4 ? SKColors.Blue : SKColors.Red);
        var entire = SnapshotImageCommand.Render(live, [], Selection(CaptureSource.CanvasOnly, viewport), desktop, [viewport]);
        var region = new RectD(-47, -11, 51, 40);
        var partial = SnapshotImageCommand.Render(live, [], Selection(CaptureSource.CanvasOnly, region), desktop, [viewport]);
        using var fullPixels = Decode(entire); using var partialPixels = Decode(partial);
        check("canvas-only dynamic mosaic freezes sampled desktop only inside the effect", fullPixels.GetPixel(40, 40).Alpha == 255 && fullPixels.GetPixel(150, 110).Alpha == 0 && entire.HasVisiblePixels);
        bool same = true;
        for (int y = 0; y < partialPixels.Height; y++) for (int x = 0; x < partialPixels.Width; x++)
            same &= partialPixels.GetPixel(x, y) == fullPixels.GetPixel(x + 33, y + 29);
        check("production source preparation preserves the full-scene mosaic sampling context for cropped selections", same);
        check("freezing dynamic pixels does not attach the sampled desktop to the original document", live.Background is null && live.Elements.Single().AssetId is null && live.LayerOpacity == .4);
        var zeroOpacity = live.Clone(); zeroOpacity.LayerOpacity = 0;
        var zeroCapture = SnapshotImageCommand.Render(zeroOpacity, [], Selection(CaptureSource.CanvasOnly, viewport), desktop, [viewport]);
        using var zeroPixels = Decode(zeroCapture);
        check("zero global opacity does not bake fallback pixels into a captured dynamic mosaic", zeroPixels.GetPixel(40, 40) == fullPixels.GetPixel(40, 40) && zeroPixels.GetPixel(150, 110).Alpha == 0 && zeroOpacity.LayerOpacity == 0);
    }

    private static void RequestGuards(Action<string, bool> check)
    {
        var target = new EditorSession(new() { Elements = [Box(10, 10, 20, 20, 0xFFFF0000)] });
        var request = new SnapshotCaptureRequest(target, 7);
        check("snapshot request initially accepts only its exact target and committed document", request.MatchesContent(target) && !request.MatchesContent(new(target.Document)) && request.EditingVersion == 7);
        target.SetSelection([target.Document.Elements.Single().Id]);
        check("selection-only changes do not invalidate a committed snapshot source", request.MatchesContent(target));
        target.BeginTransaction("intervening gesture");
        check("an unfinished transaction blocks snapshot insertion", !request.MatchesContent(target));
        target.Cancel();
        check("a cancelled intervening transaction cannot revive the previous snapshot request", !request.MatchesContent(target));
        request = new(target, 9);
        target.Execute("change", document => document.NextNumber++);
        check("committed changes invalidate snapshot insertion", !request.MatchesContent(target));
        target.Undo();
        check("undoing the intervening change does not revive a stale snapshot request", !request.MatchesContent(target));
        request = new(target, 11);
        target.ReplaceDocument(target.Document.Clone());
        check("a replacement document cannot receive a previous document's snapshot", !request.MatchesContent(target));
        request = new(target, 13);
        request.AcceptRestoredVersion(15);
        check("a validated capture continuation advances only the request's recorded editing version", request.EditingVersion == 15 && request.MatchesContent(target));
        request.Cancel();
        request.AcceptRestoredVersion(17);
        check("cancelled capture permission remains revoked after an obsolete continuation arrives", request.Cancelled && !request.MatchesContent(target));
        object view = new(); var origin = new PointD(-120, 50);
        request = new(target, 19, view, origin);
        check("viewed capture permission accepts only its frozen viewing epoch and canvas origin", request.MatchesContent(target, view, origin) &&
            request.ContentRejection(target, new object(), origin) == "ViewChanged" && request.ContentRejection(target, view, origin + new PointD(1, 0)) == "ScreenOriginChanged");
        check("returning to the same numeric view under a new epoch cannot revive a delayed capture", !request.MatchesContent(target, new object(), origin));
        var parent = new EditorSession(); long parentRevision = parent.PreviewRevision;
        request = new(target, 21, view, origin, () => parent.PreviewRevision == parentRevision);
        parent.Execute("parent placement", document => document.NextNumber++);
        check("a parent draft change rejects capture even when the active child's document and camera remain unchanged", request.ContentRejection(target, view, origin) == "ContextChanged");

        var scopeDocument = new SceneDocument(); var graph = new SceneGraph(scopeDocument);
        var scope = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock });
        var scoped = new EditorSession(scopeDocument); long revision = scoped.SceneRevision;
        var rootRequest = new SnapshotCaptureRequest(scoped, 19);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> PendingResult()
        {
            await completion.Task.ConfigureAwait(false);
            return rootRequest.ContentRejection(scoped);
        }
        var pending = PendingResult(); scoped.EnterScope(scope.Id);
        check("entering a different edit scope invalidates the pending snapshot without a content revision", scoped.SceneRevision == revision &&
            rootRequest.ContentRejection(scoped) == "EditScopeChanged" && !rootRequest.MatchesDocument(scoped));
        scoped.LeaveScope(); completion.SetResult(true);
        check("a delayed capture cannot revive after the original scope is restored", pending.GetAwaiter().GetResult() == "EditScopeChanged" &&
            scoped.EditScopeId is null && scoped.SceneRevision == revision && !rootRequest.MatchesContent(scoped));
        scoped.EnterScope(scope.Id); var innerRequest = new SnapshotCaptureRequest(scoped, 21);
        scoped.SetEditScope(scope.Id);
        check("reaffirming the same active scope keeps its snapshot permission valid", innerRequest.MatchesContent(scoped) && innerRequest.MatchesDocument(scoped));
        scoped.LeaveScope(); scoped.EnterScope(scope.Id); innerRequest.AcceptRestoredVersion(23);
        check("editing-session restoration cannot revive a snapshot after leaving and reentering its group", !innerRequest.MatchesContent(scoped) &&
            !innerRequest.MatchesDocument(scoped) && innerRequest.ContentRejection(scoped) == "EditScopeChanged" && innerRequest.EditingVersion == 23);
    }

    private static async Task InsertAndPersistenceAsync(Action<string, bool> check)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "snapshot-image-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var assets = new AssetLibrary();
        var original = Box(-60, -30, 20, 25, 0xFFFF0000);
        var live = new SceneDocument { Viewport = new(-100, -60, 180, 120), Elements = [original], LayerOpacity = .65 };
        var session = new EditorSession(live, assets); session.SetSelection([original.Id]);
        string originalElement = JsonSerializer.Serialize(original);
        var defaults = new ToolParameters { Tool = EditorTool.CaptureRegion, CaptureShape = CaptureShape.Ellipse, CaptureSource = CaptureSource.CanvasOnly, CaptureBorderWidth = 4 };
        string defaultState = JsonSerializer.Serialize(defaults);
        var selection = new CaptureSelection(defaults.CaptureShape, defaults.CaptureSource, new(-70, -40, 80, 60), [], defaults.CaptureBorderWidth, defaults.CaptureBorderColor);
        var raster = SnapshotImageCommand.Render(live.Clone(), assets.Resources, selection, null, [live.Viewport]);
        var image = SnapshotImageCommand.Insert(session, raster);
        check("snapshot inserts one ordinary editable PNG image without removing original annotations", session.Document.Kind == DocumentKind.LiveOverlay && session.Document.Elements.Count == 2 && session.Document.Elements.Last().Kind == ElementKind.Image && session.Document.Background is null);
        check("snapshot insertion uses the output's original position and size at 1:1", image.Transform == Affine2D.Translation(raster.Bounds.X, raster.Bounds.Y) && image.Width == raster.Bounds.Width && image.Height == raster.Bounds.Height);
        check("new snapshot is selected and insertion adds exactly one undo entry", session.Selection.SetEquals([image.Id]) && session.UndoCount == 1 && session.RedoCount == 0);
        check("snapshot does not alter original marks or current screenshot parameters", JsonSerializer.Serialize(session.Document.Elements[0]) == originalElement && JsonSerializer.Serialize(defaults) == defaultState);
        check("snapshot asset stores the exact generated PNG bytes", assets.Count == 1 && assets.Get(image.AssetId!).Data.Span.SequenceEqual(raster.PngBytes));
        check("one undo removes only the snapshot and restores the previous selection", session.Undo() && session.Document.Elements.Count == 1 && session.Document.Elements[0].Id == original.Id && session.Selection.SetEquals([original.Id]));
        assets.Prune(session.ReferencedAssetsIncludingHistory());
        check("undo retains PNG resources needed by redo", assets.TryGet(image.AssetId!, out var retained) && retained.Data.Span.SequenceEqual(raster.PngBytes));
        check("one redo restores the same image, resource and selection", session.Redo() && session.Document.Elements.Last().Id == image.Id && session.Document.Elements.Last().AssetId == image.AssetId && session.Selection.SetEquals([image.Id]));

        var project = new ProjectStore(); string projectPath = Path.Combine(directory, "snapshot.floatcanvas");
        await project.SaveAsync(projectPath, session.Document.Clone(), assets);
        var loaded = await project.LoadAsync(projectPath);
        var loadedImage = loaded.Document.Elements.Last();
        check("project roundtrip keeps a normal PNG image with exact alpha pixels, placement and edit frame", loadedImage.Kind == ElementKind.Image && loadedImage.Transform == image.Transform && loadedImage.Width == image.Width && loadedImage.Height == image.Height &&
            (image.ImageFrame is null ? loadedImage.ImageFrame is null : loadedImage.ImageFrame?.SequenceEqual(image.ImageFrame) == true) && loaded.Assets.Get(loadedImage.AssetId!).Data.Span.SequenceEqual(raster.PngBytes));
        using (var archive = ZipFile.OpenRead(projectPath))
        using (var reader = new StreamReader(archive.GetEntry("scene.json")!.Open()))
        {
            string sceneJson = reader.ReadToEnd();
            check("project snapshot stores its image frame without persisting capture-tool or source choices",
                sceneJson.Contains("ImageFrame", StringComparison.Ordinal) == (image.ImageFrame is not null) && !sceneJson.Contains("CaptureShape", StringComparison.Ordinal) &&
                !sceneJson.Contains("CaptureSource", StringComparison.Ordinal));
        }
        var reopened = new EditorSession(loaded.Document, loaded.Assets);
        reopened.SetSelection([loadedImage.Id]);
        var before = loadedImage.Transform;
        reopened.UpdateSelection(element => { element.Transform = Affine2D.Translation(20, -10).Compose(element.Transform).Compose(Affine2D.Rotation(13)); element.ImageCrop = new(.1, .1, .8, .8); });
        check("reopened snapshot supports normal image transform and crop editing", reopened.Document.Elements.Last().Transform != before && reopened.Document.Elements.Last().ImageCrop is { Width: .8, Height: .8 });
        check("ordinary image edits remain reversible after project loading", reopened.Undo() && reopened.Document.Elements.Last().Transform == before && reopened.Document.Elements.Last().ImageCrop is null);
        using (var bitmap = Decode(raster))
            check("saved snapshot alpha remains transparent outside its elliptical selection", bitmap.GetPixel(0, 0).Alpha == 0);

        var oldDraft = live.CreateCapture(new(-90, -50, 120, 80), assets.Add(Png(120, 80, (_, _) => SKColors.Blue)));
        string oldState = JsonSerializer.Serialize(oldDraft);
        var workspace = new WorkspaceState { LiveDocument = session.Document.Clone(), CaptureDocument = oldDraft, CaptureOrigin = new(-90, -50) };
        var store = new RecoveryStore(Path.Combine(directory, "recovery"));
        await store.SaveAsync(workspace, assets);
        var restored = await store.LoadAsync();
        check("recovery roundtrip preserves the new live snapshot as a normal image with its edit frame", restored is not null && restored.State.LiveDocument.Elements.Last().Kind == ElementKind.Image &&
            (image.ImageFrame is null ? restored.State.LiveDocument.Elements.Last().ImageFrame is null : restored.State.LiveDocument.Elements.Last().ImageFrame?.SequenceEqual(image.ImageFrame) == true) && restored.Assets.Get(image.AssetId!).Data.Span.SequenceEqual(raster.PngBytes));
        check("recovery retains the old FrozenCapture draft and original screen origin unchanged", restored?.State.CaptureDocument?.Kind == DocumentKind.FrozenCapture && JsonSerializer.Serialize(restored.State.CaptureDocument) == oldState && restored.State.CaptureOrigin == new PointD(-90, -50));
        using var legacyRenderer = new SceneRenderer();
        using var legacyPixels = SKBitmap.Decode(legacyRenderer.ExportPng(restored!.State.CaptureDocument!, restored.Assets));
        check("retained legacy draft remains renderable and exportable", legacyPixels.Width == 120 && legacyPixels.Height == 80 && legacyPixels.GetPixel(115, 75) == SKColors.Blue);
        Console.WriteLine($"Snapshot image artifacts: {directory}");
    }

    private static CaptureSelection Selection(CaptureSource source, RectD bounds) => new(CaptureShape.Rectangle, source, bounds, [], 0, ColorLibrary.ForegroundRed);
    private static CanvasElement Box(double x, double y, double width, double height, uint fill) => new()
    { Kind = ElementKind.Rectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { Fill = fill, StrokeWidth = 0 } };
    private static SKBitmap Decode(CaptureRasterResult result) => SKBitmap.Decode(result.PngBytes) ?? throw new InvalidDataException("Invalid snapshot PNG.");
    private static CapturedDesktop Desktop(PixelBounds bounds, Func<int, int, SKColor> color) => new(bounds, Png(bounds.Width, bounds.Height, color));
    private static byte[] Png(int width, int height, Func<int, int, SKColor> color)
    {
        using var bitmap = new SKBitmap(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bitmap.SetPixel(x, y, color(x, y));
        using var image = SKImage.FromBitmap(bitmap); using var bytes = image.Encode(SKEncodedImageFormat.Png, 100); return bytes.ToArray();
    }
}
