using System.Text.Json;
using System.IO;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class CanvasSafetyResolutionTests
{
    internal static void Run(Action<string, bool> check)
    {
        CornerCancelAndRelocate(check);
        KeepLayers(check);
        EdgeDelete(check);
        FourthPoint(check);
        FailedRelocation(check);
        TinyRelocation(check);
        BillboardSky(check);
    }

    private static void CornerCancelAndRelocate(Action<string, bool> check)
    {
        var (session, target, groupIds) = CornerFixture();
        var controller = new CanvasCalibrationController(); controller.Begin(session);
        string before = JsonSerializer.Serialize(session.Document);
        string elementsBefore = JsonSerializer.Serialize(session.Document.Elements);
        var selection = session.Selection.ToHashSet();
        var start = session.Document.Canvas.Projection.Quad[0];
        int prompts = 0; controller.ResolutionRequired += () => prompts++;
        check("safety corner gesture begins with safe editable content and an old hidden locked offender", controller.Down(start));
        bool continuous = true;
        for (int sample = 1; sample <= 20; sample++)
        {
            var pointer = start + (target - start) * (sample / 20.0);
            continuous &= controller.Move(pointer, OverlayModifiers.Control) && !controller.IsDragConstrained &&
                session.Document.Canvas.Projection.Quad[0].DistanceTo(pointer) < 1e-6;
        }
        check("existing and newly unsafe layers do not stop continuous corner preview", continuous && session.IsTransactionActive &&
            JsonSerializer.Serialize(session.Document.Elements) == elementsBefore);
        check("a horizon may enter the viewport during actual corner calibration", !CanvasCalibration.IsStableViewport(session.Document.Canvas.Projection.Matrix, session.Document.Viewport));
        controller.Up(); controller.Up();
        check("release prompts once and keeps calibration pending without committing history", prompts == 1 && controller.IsAwaitingResolution &&
            controller.IsDragging && session.IsTransactionActive && session.UndoCount == 0);
        check("pending calibration recovery snapshot remains the complete previous committed scene", JsonSerializer.Serialize(session.CreateCommittedSnapshot()) == before);
        check("pending count includes hidden locked offenders and safe descendants of the affected root group", controller.AffectedLayerIds.Count == 3 &&
            groupIds.All(controller.AffectedLayerIds.Contains) && session.Document.Elements.Where(e => !e.Visible && e.Locked).All(e => controller.AffectedLayerIds.Contains(e.Id)));
        check("cancel explicitly resolves the pending corner gesture", controller.Resolve(CanvasSafetyAction.Cancel));
        check("cancel restores the exact scene selection and history", JsonSerializer.Serialize(session.Document) == before &&
            session.Selection.SetEquals(selection) && session.UndoCount == 0 && !controller.IsAwaitingResolution && !session.IsTransactionActive);

        var originals = session.Document.Elements.ToDictionary(e => e.Id, e => e.Clone());
        string groupsBefore = JsonSerializer.Serialize(session.Document.Groups);
        controller.Down(start); controller.Move(target, OverlayModifiers.Control); controller.Up();
        check("relocate applies a pending calibration as one undo step", controller.Resolve(CanvasSafetyAction.Relocate) &&
            !controller.IsAwaitingResolution && !session.IsTransactionActive && session.UndoCount == 1);
        var mapping = CanvasMapping.Create(session.Document);
        check("relocation returns every affected group and hidden locked layer to safe space", session.Document.Elements.All(e => mapping.IsElementSafe(e)) &&
            JsonSerializer.Serialize(session.Document.Groups) == groupsBefore);
        bool retained = true;
        foreach (var current in session.Document.Elements)
        {
            var normalized = current.Clone(); normalized.Transform = originals[current.Id].Transform; normalized.BillboardAnchor = originals[current.Id].BillboardAnchor;
            retained &= JsonSerializer.Serialize(normalized) == JsonSerializer.Serialize(originals[current.Id]);
        }
        var grouped = groupIds.Select(id => session.Document.Elements.Single(e => e.Id == id)).ToArray();
        var firstDelta = grouped[0].Transform.OffsetX - originals[grouped[0].Id].Transform.OffsetX;
        var secondDelta = grouped[0].Transform.OffsetY - originals[grouped[0].Id].Transform.OffsetY;
        check("relocation preserves IDs geometry styles dimensions and the grouped relative offset", retained && grouped.All(e =>
            Math.Abs(e.Transform.OffsetX - originals[e.Id].Transform.OffsetX - firstDelta) < 1e-6 &&
            Math.Abs(e.Transform.OffsetY - originals[e.Id].Transform.OffsetY - secondDelta) < 1e-6));
        string applied = JsonSerializer.Serialize(session.Document);
        check("one undo restores calibration and all relocated layers exactly", session.Undo() && JsonSerializer.Serialize(session.Document) == before && session.Selection.SetEquals(selection));
        check("one redo restores the accepted calibration and relocation exactly", session.Redo() && JsonSerializer.Serialize(session.Document) == applied);
        controller.End();
    }

    private static void KeepLayers(Action<string, bool> check)
    {
        var (session, target, groupIds) = CornerFixture();
        var initial = session.Document.Canvas.Projection;
        if (!CanvasCalibration.TryCalibrate(initial, initial.Quad, session.Document.Viewport, out var normalized, out _))
            throw new InvalidOperationException("Keep fixture must begin with a production-calibrated reference.");
        session.Document.Canvas.Projection = normalized;
        session.Document.Canvas.Grid.FlatCellSize = normalized.GridCellSize;
        var originalProjection = session.Document.Canvas.Projection.Clone();
        var future = session.Document.Clone(); var quad = originalProjection.Quad.ToArray(); quad[0] = target;
        if (!CanvasCalibration.TryCalibrate(originalProjection, quad, future.Viewport, out var changed, out _))
            throw new InvalidOperationException("Keep fixture must have a valid target projection.");
        future.Canvas.Projection = changed;
        var futureMapping = CanvasMapping.Create(future); var originalMapping = CanvasMapping.Create(session.Document);
        PointD? unsafeAnchor = null;
        for (int y = -2000; y <= 2000 && unsafeAnchor is null; y += 40)
            for (int x = -2000; x <= 2000 && unsafeAnchor is null; x += 40)
            {
                var point = new PointD(x, y);
                if (originalMapping.TryGetDepthScale(point, out _) && !futureMapping.TryGetDepthScale(point, out _)) unsafeAnchor = point;
            }
        if (unsafeAnchor is null) throw new InvalidOperationException("Keep fixture needs an anchor safe before and unsafe after calibration.");
        var billboard = new CanvasElement { Kind = ElementKind.Rectangle, Width = 90, Height = 140, IsBillboard = true,
            BillboardAnchor = unsafeAnchor.Value, BillboardHeight = -20, Transform = Affine2D.Rotation(15),
            Visible = false, Locked = true, GroupId = session.Document.Elements.Single(e => e.Id == groupIds[0]).GroupId,
            Style = new() { StrokeWidth = 4, Fill = 0xFF339955 } };
        session.Document.Elements.Add(billboard); session.SetSelection([.. groupIds, billboard.Id]);
        var probeViewport = originalMapping.ProjectedBounds(billboard).Inflate(12);
        using var renderer = new SceneRenderer();
        byte[] originalRender = RenderBillboardProbe(renderer, session.Document, session.Assets, billboard.Id, probeViewport);
        string before = JsonSerializer.Serialize(session.Document);
        string elementsBefore = JsonSerializer.Serialize(session.Document.Elements), groupsBefore = JsonSerializer.Serialize(session.Document.Groups);
        var selection = session.Selection.ToHashSet();
        var controller = new CanvasCalibrationController(); controller.Begin(session);
        controller.Down(originalProjection.Quad[0]); controller.Move(target, OverlayModifiers.Control); controller.Up();
        string pending = JsonSerializer.Serialize(session.Document);
        check("keep decision remains isolated from the committed recovery snapshot", controller.IsAwaitingResolution &&
            controller.AffectedLayerIds.Contains(billboard.Id) && JsonSerializer.Serialize(session.CreateCommittedSnapshot()) == before && session.UndoCount == 0);
        check("keep commits only the new perspective and preserves every layer and group exactly", controller.Resolve(CanvasSafetyAction.Keep) &&
            !controller.IsAwaitingResolution && !session.IsTransactionActive && session.UndoCount == 1 &&
            JsonSerializer.Serialize(session.Document) == pending && JsonSerializer.Serialize(session.Document.Elements) == elementsBefore &&
            JsonSerializer.Serialize(session.Document.Groups) == groupsBefore && session.Selection.SetEquals(selection) &&
            !CanvasMapping.Create(session.Document).TryGetDepthScale(billboard.BillboardAnchor, out _));

        string root = Path.Combine(Path.GetTempPath(), "FloatCanvasSafetyKeepTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ProjectStore(); string path = Path.Combine(root, "kept-layers.floatcanvas");
        store.SaveAsync(path, session.CreateCommittedSnapshot(), session.Assets).GetAwaiter().GetResult();
        var loaded = store.LoadAsync(path).GetAwaiter().GetResult();
        check("keep project saves and reopens unsafe hidden locked billboards and all original layers", JsonSerializer.Serialize(loaded.Document) == pending);
        byte[] keptRender = RenderBillboardProbe(renderer, loaded.Document, loaded.Assets, billboard.Id, probeViewport);
        using (var beforePixels = SKBitmap.Decode(originalRender))
        using (var keptPixels = SKBitmap.Decode(keptRender))
            check("a visibility-only render probe hides the kept unsafe billboard while preserving its source geometry",
                beforePixels.Pixels.Any(pixel => pixel.Alpha > 0) && keptPixels.Pixels.All(pixel => pixel.Alpha == 0));
        var recovery = new RecoveryStore(Path.Combine(root, "recovery"));
        recovery.SaveAsync(new() { LiveDocument = session.CreateCommittedSnapshot() }, session.Assets).GetAwaiter().GetResult();
        check("keep recovery stores the accepted new perspective and original layer contents", JsonSerializer.Serialize(
            recovery.LoadAsync().GetAwaiter().GetResult()!.State.LiveDocument) == pending);
        check("keep and the perspective change undo as one complete operation", session.Undo() && JsonSerializer.Serialize(session.Document) == before &&
            session.Selection.SetEquals(selection) && session.Redo() && JsonSerializer.Serialize(session.Document) == pending);

        var reopened = new EditorSession(loaded.Document, loaded.Assets);
        var restore = new CanvasCalibrationController(); restore.Begin(reopened);
        restore.Down(reopened.Document.Canvas.Projection.Quad[0]);
        restore.Move(originalProjection.Quad[0], OverlayModifiers.Control); restore.Up();
        if (restore.IsAwaitingResolution) restore.Resolve(CanvasSafetyAction.Keep);
        check("moving the saved perspective back restores the original appearance coordinates without changing layers",
            JsonSerializer.Serialize(reopened.Document) == before && CanvasMapping.Create(reopened.Document).TryGetDepthScale(billboard.BillboardAnchor, out _) &&
            JsonSerializer.Serialize(reopened.Document.Elements) == elementsBefore && JsonSerializer.Serialize(reopened.Document.Groups) == groupsBefore);
        byte[] restoredRender = RenderBillboardProbe(renderer, reopened.Document, reopened.Assets, billboard.Id, probeViewport);
        using (var beforePixels = SKBitmap.Decode(originalRender))
        using (var restoredPixels = SKBitmap.Decode(restoredRender))
            check("restoring the saved perspective restores every pixel of the retained billboard render probe", beforePixels.Bytes.SequenceEqual(restoredPixels.Bytes));
        File.WriteAllBytes(Path.Combine(root, "billboard-before.png"), originalRender);
        File.WriteAllBytes(Path.Combine(root, "billboard-kept-unsafe.png"), keptRender);
        File.WriteAllBytes(Path.Combine(root, "billboard-restored.png"), restoredRender);
        Console.WriteLine("KEEP_RENDER_EVIDENCE " + root);
        restore.End(); controller.End();
    }

    private static byte[] RenderBillboardProbe(SceneRenderer renderer, SceneDocument source, AssetLibrary assets, Guid billboardId, RectD viewport)
    {
        var probe = source.Clone();
        var billboard = probe.Elements.Single(element => element.Id == billboardId);
        // The real hidden/locked flags remain untouched. This isolated visibility probe
        // demonstrates rendering restoration even for a layer intentionally kept hidden.
        billboard.Visible = true; billboard.GroupId = null;
        probe.Elements = [billboard]; probe.Groups = []; probe.Background = null;
        probe.Viewport = viewport; probe.LayerOpacity = 1; probe.Canvas.BackgroundColor = 0; probe.Canvas.Grid.Visible = false;
        return renderer.ExportPng(probe, assets, transparent: true);
    }

    private static void EdgeDelete(Action<string, bool> check)
    {
        var document = Scene();
        var root = new CanvasGroup { Name = "root" }; var child = new CanvasGroup { Name = "child", ParentId = root.Id };
        document.Groups.AddRange([root, child]);
        var unsafeElement = Box(-1_000_000_000, 200); unsafeElement.GroupId = child.Id; unsafeElement.Locked = true; unsafeElement.Visible = false;
        var companion = Box(450, 300); companion.GroupId = root.Id;
        var untouched = Box(700, 450);
        document.Elements.AddRange([unsafeElement, companion, untouched]);
        var session = new EditorSession(document); session.SetSelection([unsafeElement.Id, companion.Id]);
        var controller = new CanvasCalibrationController(); controller.Begin(session);
        string before = JsonSerializer.Serialize(document);
        var quad = document.Canvas.Projection.Quad.ToArray(); var edge = (quad[1] + quad[2]) * .5;
        check("safety edge gesture starts", controller.Down(edge));
        check("edge preview advances both endpoints despite old unsafe content", controller.Move(edge + new PointD(30, 10)) &&
            document.Canvas.Projection.Quad[1] == quad[1] + new PointD(30, 10) && document.Canvas.Projection.Quad[2] == quad[2] + new PointD(30, 10));
        controller.Up();
        check("edge release counts the entire nested root group", controller.IsAwaitingResolution && controller.AffectedLayerIds.ToHashSet().SetEquals([unsafeElement.Id, companion.Id]));
        check("delete applies the calibration and removes precisely the affected group", controller.Resolve(CanvasSafetyAction.Delete) &&
            session.Document.Elements.Select(e => e.Id).SequenceEqual([untouched.Id]) && session.Document.Groups.Count == 0 && session.UndoCount == 1);
        check("one undo restores deleted hidden locked layers nested group IDs selection and calibration", session.Undo() && JsonSerializer.Serialize(session.Document) == before &&
            session.Selection.SetEquals([unsafeElement.Id, companion.Id]));
        controller.End();
    }

    private static void FourthPoint(Action<string, bool> check)
    {
        var document = Scene();
        document.Elements.Add(Box(-1_000_000_000, 200));
        var session = new EditorSession(document); var controller = new CanvasCalibrationController(); controller.BeginPlacement(session);
        string before = JsonSerializer.Serialize(document);
        PointD[] quad = [new(350, 180), new(650, 180), new(850, 650), new(150, 650)];
        int prompts = 0; controller.ResolutionRequired += () => prompts++;
        bool placed = quad.All(controller.Down);
        check("fourth point placement uses the same pending safety transaction", placed && controller.IsAwaitingResolution &&
            session.IsTransactionActive && session.UndoCount == 0 && JsonSerializer.Serialize(session.CreateCommittedSnapshot()) == before);
        check("fourth point never opens its safety modal before the physical release", prompts == 0);
        controller.Up(); controller.Up();
        check("fourth point release requests exactly one safety modal", prompts == 1);
        check("cancelling fourth point restores the exact scene and keeps three placed points for retry", controller.Resolve(CanvasSafetyAction.Cancel) &&
            JsonSerializer.Serialize(session.Document) == before && controller.IsPlacing && controller.PlacementPreview.Count == 3 && session.UndoCount == 0);
        check("fourth point can be retried and resolved by deleting the offender", controller.Down(quad[3]) && controller.IsAwaitingResolution &&
            controller.Resolve(CanvasSafetyAction.Delete) && !controller.IsPlacing && session.Document.Elements.Count == 0 && session.UndoCount == 1);
        check("placement safety resolution is one reversible operation", session.Undo() && JsonSerializer.Serialize(session.Document) == before);
        controller.BeginPlacement(session);
        check("fourth point also permits explicitly keeping its unchanged unsafe layer", quad.All(controller.Down) && controller.IsAwaitingResolution &&
            controller.Resolve(CanvasSafetyAction.Keep) && !controller.IsPlacing && session.Document.Elements.Count == 1 && session.UndoCount == 1);
        check("fourth-point keep has the same complete single-step undo", session.Undo() && JsonSerializer.Serialize(session.Document) == before);
        controller.End();
    }

    private static void FailedRelocation(Action<string, bool> check)
    {
        var document = Scene();
        document.Elements.AddRange([Box(-1_000_000_000, 200), new CanvasElement { Kind = ElementKind.Rectangle, Width = 1_000_000_000, Height = 300,
            Transform = Affine2D.Translation(-500_000_000, 200), Style = new() { StrokeWidth = 4 } }]);
        var session = new EditorSession(document); var controller = new CanvasCalibrationController(); controller.Begin(session);
        string before = JsonSerializer.Serialize(document);
        var center = document.Canvas.Projection.Quad.Aggregate(new PointD(), (sum, p) => sum + p) * .25;
        controller.Down(center); controller.Move(center + new PointD(20, 20)); controller.Up();
        string pending = JsonSerializer.Serialize(session.Document);
        string? error = null; controller.Error += value => error = value;
        check("an uncontainable layer prevents relocation and retains the pending resolution", !controller.Resolve(CanvasSafetyAction.Relocate) &&
            error == "Canvas.RelocateFailed" && controller.IsAwaitingResolution && session.IsTransactionActive && session.UndoCount == 0);
        check("failed relocation is atomic even after finding a destination for an earlier layer", JsonSerializer.Serialize(session.Document) == pending &&
            JsonSerializer.Serialize(session.CreateCommittedSnapshot()) == before);
        check("failed relocation can still be cancelled exactly", controller.Resolve(CanvasSafetyAction.Cancel) && JsonSerializer.Serialize(session.Document) == before);
        controller.Down(center); controller.Move(center + new PointD(20, 20)); controller.Up();
        string originalLayers = JsonSerializer.Serialize(session.Document.Elements);
        check("keep remains available when no safe relocation can be found", !CanvasSafetyResolution.TryRelocate(session.Document.Clone(), controller.AffectedLayerIds) &&
            controller.Resolve(CanvasSafetyAction.Keep) && session.UndoCount == 1 && JsonSerializer.Serialize(session.Document.Elements) == originalLayers);
        check("keeping unrelocatable layers can still undo the complete adjustment", session.Undo() && JsonSerializer.Serialize(session.Document) == before);
        controller.End();
    }

    private static void TinyRelocation(Action<string, bool> check)
    {
        var document = Scene();
        var projection = document.Canvas.Projection;
        projection.Matrix = Projective2D.FromAffine(Affine2D.Translation(495, 396).Compose(Affine2D.Scale(.01, .01)));
        PointD[] source = [projection.Origin, projection.Origin + new PointD(projection.Columns * projection.CellSize, 0),
            projection.Origin + new PointD(projection.Columns * projection.CellSize, projection.Rows * projection.CellSize),
            projection.Origin + new PointD(0, projection.Rows * projection.CellSize)];
        projection.Quad = source.Select(projection.Matrix.TransformPoint).ToList();
        if (!CanvasCalibration.TryGridToScreen(projection.Columns, projection.Rows, projection.Quad, out _))
            throw new InvalidOperationException("Tiny relocation fixture must retain a valid reference quadrilateral.");
        var element = Box(-1_000_000_000, 200); document.Elements.Add(element);
        string before = JsonSerializer.Serialize(document);
        bool relocated = CanvasSafetyResolution.TryRelocate(document, [element.Id]);
        bool unchanged = JsonSerializer.Serialize(document) == before;
        check("relocation does not claim success for a layer that remains too small to select", !relocated && unchanged);
    }

    private static void BillboardSky(Action<string, bool> check)
    {
        var document = Scene();
        PointD[] quad = [new(350, 180), new(650, 180), new(850, 650), new(150, 650)];
        CanvasCalibration.TryCalibrate(document.Canvas.Projection, quad, document.Viewport, out var projection, out _);
        document.Canvas.Projection = projection;
        var mapping = CanvasMapping.Create(document); mapping.TryToPlane(new(500, 250), out var anchor);
        var billboard = new CanvasElement { Kind = ElementKind.Rectangle, IsBillboard = true, BillboardAnchor = anchor, Width = 100, Height = 4000,
            Style = new() { StrokeWidth = 0, Fill = 0xFF335599 } };
        document.Elements.Add(billboard);
        check("a billboard extending into the sky is excluded from unsafe layer counts", mapping.IsElementSafe(billboard) &&
            mapping.ProjectedBounds(billboard).Top < 0 && CanvasSafetyResolution.AffectedIds(document).Count == 0);
        var session = new EditorSession(document); var controller = new CanvasCalibrationController(); controller.Begin(session);
        var center = quad.Aggregate(new PointD(), (sum, p) => sum + p) * .25;
        controller.Down(center); controller.Move(center + new PointD(5, 5)); controller.Up();
        check("safe billboard sky content allows a normal calibration commit without a warning", !controller.IsAwaitingResolution && session.UndoCount == 1 &&
            session.Document.Elements.Single().Id == billboard.Id);
        controller.End();
    }

    private static (EditorSession Session, PointD Target, Guid[] GroupIds) CornerFixture()
    {
        var document = Scene(); var initial = document.Canvas.Projection;
        var target = initial.Quad[0] + new PointD(0, (initial.Quad[3].Y - initial.Quad[0].Y) * .9);
        var candidate = initial.Quad.ToArray(); candidate[0] = target;
        if (!CanvasCalibration.TryCalibrate(initial, candidate, document.Viewport, out var projection, out _)) throw new InvalidOperationException("Safety corner fixture must be convex.");
        var future = document.Clone(); future.Canvas.Projection = projection; var futureMapping = CanvasMapping.Create(future);
        CanvasElement? offender = null, companion = null;
        for (int y = 20; y <= 760 && offender is null; y += 20)
            for (int x = 20; x <= 960 && offender is null; x += 20)
            {
                var item = Box(x, y);
                if (futureMapping.IsElementSafe(item)) continue;
                foreach (var offset in new PointD[] { new(80, 0), new(-80, 0), new(0, 80), new(0, -80), new(120, 120), new(-120, -120) })
                {
                    var other = Box(x + offset.X, y + offset.Y);
                    if (!futureMapping.IsElementSafe(other)) continue;
                    offender = item; companion = other; break;
                }
            }
        if (offender is null || companion is null) throw new InvalidOperationException("Safety fixture needs nearby safe and unsafe group members.");
        var root = new CanvasGroup { Name = "Safety root" }; var child = new CanvasGroup { Name = "Safety child", ParentId = root.Id };
        document.Groups.AddRange([root, child]); offender.GroupId = child.Id; companion.GroupId = root.Id;
        var hiddenLocked = Box(-1_000_000_000, 250); hiddenLocked.Visible = false; hiddenLocked.Locked = true;
        var safe = companion.Clone(true); safe.GroupId = null;
        document.Elements.AddRange([offender, companion, hiddenLocked, safe]);
        var session = new EditorSession(document); session.SetSelection([offender.Id, companion.Id]);
        return (session, target, [offender.Id, companion.Id]);
    }

    private static SceneDocument Scene()
    {
        var document = new SceneDocument { Viewport = new(0, 0, 1000, 800) };
        document.Canvas.Projection = CanvasCalibration.CreateInitial(document.Viewport);
        document.Canvas.Projection.Enabled = true;
        return document;
    }

    private static CanvasElement Box(double x, double y) => new() { Kind = ElementKind.Rectangle, Width = 24, Height = 18,
        Transform = Affine2D.Translation(x, y), Style = new() { Fill = 0xFF6688AA, Stroke = 0xFF112233, StrokeWidth = 3 } };
}
