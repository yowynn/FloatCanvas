using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

/// <summary>Creation and follow-up adjustments are exercised through the real pointer transaction boundary.</summary>
internal static class CreationSelectionTests
{
    private static readonly EditorTool[] DragTools = [EditorTool.Line, EditorTool.Arrow,
        EditorTool.DoubleArrow, EditorTool.Rectangle, EditorTool.RoundedRectangle, EditorTool.Ellipse, EditorTool.Symbol];

    public static void Run(Action<string, bool> check)
    {
        ExistingSelectionVisibility(check);
        RegionToolSelectionRouting(check);
        CaptureSelectionCoordinates(check);
        RegionToolSelectedEndpoints(check);
        foreach (var tool in DragTools) DraggedObject(tool, check);
        ClosedStroke(check);
        PointerCancellation(check);
        ExistingSelectionIsTheTarget(check);
        CtrlGestureOwnership(check);
        ClickObjectsAndImage(check);
        ContinuousClickInsertion(check);
    }

    private static void RegionToolSelectionRouting(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Blur, EditorTool.Pixelate, EditorTool.Crop })
        {
            var selected = new CanvasElement { Kind = tool == EditorTool.Crop ? ElementKind.Image : ElementKind.Rectangle,
                AssetId = tool == EditorTool.Crop ? new string('a', 64) : null,
                Width = 120, Height = 80, Transform = Affine2D.Translation(40, 50) };
            var session = new EditorSession(new SceneDocument { Elements = [selected] }); session.SetSelection([selected.Id]);
            var controller = new EditorController(session) { Tool = tool };
            int effectRequests = 0; RectD? requestedRegion = null; ImageEffectKind? requestedKind = null;
            controller.DesktopEffectRequested += (region, kind, _, _) => { effectRequests++; requestedRegion = region; requestedKind = kind; };
            string before = JsonSerializer.Serialize(session.Document);
            var center = session.SelectionBounds()!.Value.Center;
            var bounds = session.SelectionBounds()!.Value; var corner = new PointD(bounds.Right, bounds.Bottom);
            Gesture(controller, corner, corner + new PointD(30, 20));
            var resized = session.Document.Elements.Single();
            check(tool + " existing resize handle adjusts geometry without applying effects or crop", controller.Tool == tool &&
                resized.Id == selected.Id && resized.ImageCrop is null && resized.Effects.Count == 0 && effectRequests == 0 &&
                session.Selection.SetEquals([selected.Id]) && session.UndoCount == 1 && JsonSerializer.Serialize(session.Document) != before);
            check(tool + " selected-frame resize restores its complete original object with one undo", session.Undo() && JsonSerializer.Serialize(session.Document) == before);

            var frame = controller.SelectionScreenCorners;
            var nearEdge = (frame[1] + frame[2]) * .5 + new PointD(5, 0);
            Gesture(controller, nearEdge, nearEdge + new PointD(20, 10));
            var edgeMoved = session.Document.Elements.Single();
            check(tool + " a press five pixels outside the frame edge still moves its selection instead of drawing a region", edgeMoved.Transform == Affine2D.Translation(60, 60) &&
                edgeMoved.ImageCrop is null && edgeMoved.Effects.Count == 0 && effectRequests == 0 && session.Selection.SetEquals([selected.Id]) && session.UndoCount == 1);
            check(tool + " near-edge movement undoes exactly", session.Undo() && JsonSerializer.Serialize(session.Document) == before);

            var rotation = controller.SelectionScreenRotationHandle!.Value;
            Gesture(controller, rotation, new(center.X + center.Y - rotation.Y, center.Y));
            var rotated = session.Document.Elements.Single();
            check(tool + " its existing rotation handle rotates without starting effects or crop", Math.Abs(rotated.Transform.M12 + 1) < 1e-8 && Math.Abs(rotated.Transform.M21 - 1) < 1e-8 &&
                rotated.ImageCrop is null && rotated.Effects.Count == 0 && effectRequests == 0 && session.Selection.SetEquals([selected.Id]) && session.UndoCount == 1);
            check(tool + " rotation-handle adjustment undoes exactly", session.Undo() && JsonSerializer.Serialize(session.Document) == before);

            check(tool + " Ctrl hides the existing frame before starting its region", !controller.ShouldShowSelection(OverlayModifiers.Control));
            var end = center + new PointD(30, 20);
            Send(controller, OverlayPointerKind.Down, center, OverlayModifiers.Control);
            Send(controller, OverlayPointerKind.Move, end, OverlayModifiers.Control);
            check(tool + " Ctrl draws a region preview instead of moving the selected object", controller.Marquee is not null &&
                !controller.ShouldShowSelection(OverlayModifiers.Control) && JsonSerializer.Serialize(session.Document) == before);
            Send(controller, OverlayPointerKind.Up, end, OverlayModifiers.Control);
            if (tool == EditorTool.Crop)
            {
                var cropped = session.Document.Elements.Single();
                check("Ctrl Crop applies the selected image crop instead of moving its frame", cropped.Id == selected.Id && cropped.ImageCrop is not null &&
                    cropped.Width == 30 && cropped.Height == 20 && session.UndoCount == 1 && effectRequests == 0 && controller.Tool == EditorTool.Crop);
            }
            else
                check(tool + " Ctrl dispatches the requested desktop region without transforming the old selection", effectRequests == 1 &&
                    requestedRegion == RectD.FromPoints(center, end) && requestedKind?.ToString() == tool.ToString() &&
                    JsonSerializer.Serialize(session.Document) == before && session.Selection.SetEquals([selected.Id]) && session.UndoCount == 0);
        }
    }

    private static void CaptureSelectionCoordinates(Action<string, bool> check)
    {
        foreach (bool perspective in new[] { false, true })
        {
            var selected = new CanvasElement { Kind = ElementKind.Rectangle, Width = 160, Height = 100, Transform = Affine2D.Translation(120, 130) };
            var document = new SceneDocument { Viewport = new(0, 0, 800, 600), Elements = [selected] };
            document.Canvas.Projection.Enabled = perspective;
            document.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .001, 1);
            document.Canvas.Projection.Quad = [new(0, 0), new(800, 0), new(500, 375), new(0, 375)];
            var session = new EditorSession(document); session.SetSelection([selected.Id]);
            var controller = new EditorController(session) { Tool = EditorTool.CaptureRegion, ScreenOrigin = new(-400, 200) };
            CaptureSelection? captured = null; int captures = 0;
            controller.CaptureSelectionCompleted += value => { captured = value; captures++; };
            var mapping = CanvasMapping.Create(document);
            var grab = mapping.ToScreen(new(180, 180)); var end = mapping.ToScreen(new(210, 200));
            var absoluteGrab = grab + controller.ScreenOrigin; var absoluteEnd = end + controller.ScreenOrigin;
            string before = JsonSerializer.Serialize(document);
            Send(controller, OverlayPointerKind.Down, absoluteGrab); Send(controller, OverlayPointerKind.Move, absoluteEnd);
            check("CaptureRegion selected-object drag uses a plane transform instead of screenshot preview with perspective=" + perspective,
                session.IsTransactionActive && !controller.HasCaptureGesture && controller.CapturePreview is null && captures == 0);
            Send(controller, OverlayPointerKind.Up, absoluteEnd);
            var moved = session.Document.Elements.Single();
            check("CaptureRegion selection movement respects screen origin and plane coordinates with perspective=" + perspective,
                new PointD(moved.Transform.OffsetX, moved.Transform.OffsetY).DistanceTo(new(150, 150)) < 1e-7 &&
                moved.Width == 160 && moved.Height == 100 && session.Selection.SetEquals([selected.Id]) && session.UndoCount == 1 && captures == 0 && controller.Tool == EditorTool.CaptureRegion);
            check("CaptureRegion coordinate-mapped movement has one exact undo with perspective=" + perspective, session.Undo() && JsonSerializer.Serialize(session.Document) == before);
            Gesture(controller, absoluteGrab, absoluteEnd, OverlayModifiers.Control);
            var planeRectangle = new RectD(180, 180, 30, 20);
            var projected = new PointD[] { new(180, 180), new(210, 180), new(210, 200), new(180, 200) }.Select(mapping.ToScreen).ToArray();
            var expectedCapture = new RectD(projected.Min(point => point.X), projected.Min(point => point.Y),
                projected.Max(point => point.X) - projected.Min(point => point.X), projected.Max(point => point.Y) - projected.Min(point => point.Y));
            check("CaptureRegion Ctrl retains the projected plane-rectangle screenshot bounds with perspective=" + perspective, captures == 1 && captured is not null &&
                new PointD(captured.Bounds.X, captured.Bounds.Y).DistanceTo(new(expectedCapture.X, expectedCapture.Y)) < 1e-7 &&
                Math.Abs(captured.Bounds.Width - expectedCapture.Width) < 1e-7 && Math.Abs(captured.Bounds.Height - expectedCapture.Height) < 1e-7 &&
                JsonSerializer.Serialize(session.Document) == before && session.UndoCount == 0 && session.Selection.SetEquals([selected.Id]));
            check("CaptureRegion Ctrl preserves the plane rectangle and projection metadata with perspective=" + perspective,
                captured is not null && (perspective ? captured.PlaneBounds is { } plane &&
                    new PointD(plane.X, plane.Y).DistanceTo(new(planeRectangle.X, planeRectangle.Y)) < 1e-7 &&
                    Math.Abs(plane.Width - planeRectangle.Width) < 1e-7 && Math.Abs(plane.Height - planeRectangle.Height) < 1e-7 && captured.PlaneToScreen == mapping.PlaneToScreen
                    : captured.PlaneBounds is null && captured.PlaneToScreen is null));
        }
    }

    private static void RegionToolSelectedEndpoints(Action<string, bool> check)
    {
        // A line cannot be an eligible Crop target; the other region tools keep
        // their own real tool while editing this explicitly selected old line.
        foreach (var tool in new[] { EditorTool.Blur, EditorTool.Pixelate, EditorTool.CaptureRegion })
        {
            var line = new CanvasElement { Kind = ElementKind.Line, Points = [new(0, 0), new(120, 80)], Transform = Affine2D.Translation(40, 50) };
            var session = new EditorSession(new SceneDocument { Elements = [line] }); session.SetSelection([line.Id]);
            var controller = new EditorController(session) { Tool = tool };
            int regions = 0; controller.DesktopEffectRequested += (_, _, _, _) => regions++; controller.CaptureSelectionCompleted += _ => regions++;
            var endpoint = controller.SelectionScreenEndpoints[^1];
            Gesture(controller, endpoint, endpoint + new PointD(20, 10));
            var edited = session.Document.Elements.Single();
            check(tool + " a manually selected old line endpoint remains editable instead of starting a region", edited.Id == line.Id &&
                edited.Points[^1].DistanceTo(new(140, 90)) < 1e-7 && edited.Transform == Affine2D.Translation(40, 50) &&
                session.Selection.SetEquals([line.Id]) && session.UndoCount == 1 && regions == 0 && controller.Tool == tool && controller.CapturePreview is null);
        }
    }

    private static void ExistingSelectionVisibility(Action<string, bool> check)
    {
        foreach (var requested in new[] { EditorTool.Blur, EditorTool.Pixelate, EditorTool.Crop }.Concat(Enum.GetValues<EditorTool>()).Distinct())
        {
            // Crop must retain the real Crop tool instead of silently falling back
            // to Select; Blur/Pixelate exercise an ordinary already-selected shape.
            var selected = new CanvasElement { Kind = requested == EditorTool.Crop ? ElementKind.Image : ElementKind.Rectangle,
                AssetId = requested == EditorTool.Crop ? new string('a', 64) : null,
                Width = 120, Height = 80, Transform = Affine2D.Translation(40, 50) };
            var session = new EditorSession(new SceneDocument { Elements = [selected] }); session.SetSelection([selected.Id]);
            var controller = new EditorController(session) { Tool = requested };
            bool expected = requested is not (EditorTool.Eraser or EditorTool.Canvas);
            string before = JsonSerializer.Serialize(session.Document);
            check(requested + " shows an existing selection except in Eraser and Canvas", controller.ShouldShowSelection(OverlayModifiers.None) == expected &&
                (!expected || controller.SelectionScreenCorners.Count == 4));
            if (requested is EditorTool.Blur or EditorTool.Pixelate or EditorTool.Crop)
                check(requested + " retains its actual non-creation tool while showing the selected object", controller.Tool == requested &&
                    !EditorController.IsCreationTool(controller.Tool) && session.SelectedElements().Single().Id == selected.Id);

            if (requested is not (EditorTool.Select or EditorTool.Eraser or EditorTool.Canvas))
            {
                var actualTool = controller.Tool; int commands = 0;
                controller.EditText += (_, _) => commands++; controller.CaptureSelectionCompleted += _ => commands++;
                controller.DesktopEffectRequested += (_, _, _, _) => commands++;
                var center = session.SelectionBounds()!.Value.Center;
                Gesture(controller, center, center + new PointD(25, 15));
                var moved = session.Document.Elements.Single();
                check(requested + " existing selection body moves the old object before executing its current tool", moved.Id == selected.Id &&
                    moved.Transform == Affine2D.Translation(65, 65) && moved.Width == 120 && moved.Height == 80 && moved.ImageCrop is null && moved.Effects.Count == 0 &&
                    session.Selection.SetEquals([selected.Id]) && session.UndoCount == 1 && commands == 0 && controller.Tool == actualTool);
                check(requested + " selected-body routing is one exact undo", session.Undo() && JsonSerializer.Serialize(session.Document) == before);
            }

            bool controlHides = !controller.ShouldShowSelection(OverlayModifiers.Control) &&
                !controller.ShouldShowSelection(OverlayModifiers.Control | OverlayModifiers.Shift);
            controller.CanvasContentHidden = true;
            bool hiddenSuppresses = !controller.ShouldShowSelection(OverlayModifiers.None) &&
                !controller.ShouldShowSelection(OverlayModifiers.Shift) && !controller.ShouldShowSelection(OverlayModifiers.Control);
            controller.CanvasContentHidden = false;
            check(requested + " keeps Ctrl force-create and hidden-canvas frame suppression without changing selection or history",
                controlHides && hiddenSuppresses && controller.ShouldShowSelection(OverlayModifiers.None) == expected &&
                session.Selection.SetEquals([selected.Id]) && session.UndoCount == 0 && !session.IsTransactionActive && JsonSerializer.Serialize(session.Document) == before);
            session.SetSelection([]);
            check(requested + " never displays an existing-object frame without selected objects", !controller.ShouldShowSelection(OverlayModifiers.None));
        }
    }

    private static void DraggedObject(EditorTool tool, Action<string, bool> check)
    {
        var session = new EditorSession(); var controller = new EditorController(session) { Tool = tool, Symbol = SymbolKind.Heart };
        var a = new PointD(40, 60); var b = new PointD(180, 140);
        bool autoSelect = tool is not (EditorTool.Line or EditorTool.Arrow or EditorTool.DoubleArrow);
        Send(controller, OverlayPointerKind.Down, a); Send(controller, OverlayPointerKind.Move, b);
        var draft = session.Document.Elements.Single();
        check(tool + " drawing keeps the tool-specific automatic-selection policy", session.IsTransactionActive && controller.Tool == tool &&
            (autoSelect ? session.Selection.SetEquals([draft.Id]) : session.Selection.Count == 0));
        Send(controller, OverlayPointerKind.Up, b);
        var element = session.Document.Elements.Single(); var id = element.Id;
        check(tool + " creation keeps its tool-specific committed selection policy", controller.Tool == tool &&
            (autoSelect ? session.Selection.SetEquals([id]) : session.Selection.Count == 0) && session.UndoCount == 1 && !session.IsTransactionActive);
        if (!autoSelect)
        {
            check(tool + " undo removes the new stroke without introducing a selected object", session.Undo() && session.Document.Elements.Count == 0 && session.Selection.Count == 0);
            check(tool + " redo restores the new stroke without automatically selecting it", session.Redo() && session.Document.Elements.Single().Id == id && session.Selection.Count == 0);
            session.SetSelection([id]); element = session.Document.Elements.Single();
            check(tool + " a manually selected existing stroke exposes its editable controls", controller.ShouldShowSelection(OverlayModifiers.None));
        }
        string beforeMove = JsonSerializer.Serialize(session.Document); int history = session.UndoCount;
        var center = session.SelectionBounds()!.Value.Center;
        Gesture(controller, center, center + new PointD(25, 15));
        element = session.Document.Elements.Single();
        check(tool + " selected-frame drag moves the new object without creating another", session.Document.Elements.Count == 1 && controller.Tool == tool && session.Selection.SetEquals([id]) &&
            element.Transform.OffsetX == 65 && element.Transform.OffsetY == 75 && session.UndoCount == history + 1);
        check(tool + " movement is one exact undo and redo operation", session.Undo() && JsonSerializer.Serialize(session.Document) == beforeMove && session.Redo());

        history = session.UndoCount; string beforeResize = JsonSerializer.Serialize(session.Document); var bounds = session.SelectionBounds()!.Value;
        PointD handle = ElementGeometry.IsLine(element.Kind) ? element.Transform.TransformPoint(element.Points[^1]) : new(bounds.Right, bounds.Bottom);
        Gesture(controller, handle, handle + new PointD(35, 25));
        check(tool + " selected handle adjusts geometry while preserving creation mode", controller.Tool == tool && session.Document.Elements.Count == 1 && session.UndoCount == history + 1 &&
            JsonSerializer.Serialize(session.Document) != beforeResize && session.Selection.SetEquals([id]));
        check(tool + " handle adjustment undoes to the complete prior geometry", session.Undo() && JsonSerializer.Serialize(session.Document) == beforeResize);
        bounds = session.SelectionBounds()!.Value; center = bounds.Center;
        var rotationHandle = new PointD(center.X, bounds.Top - 28);
        history = session.UndoCount; string beforeRotation = JsonSerializer.Serialize(session.Document);
        Gesture(controller, rotationHandle, new(center.X + center.Y - rotationHandle.Y, center.Y));
        check(tool + " rotation handle works without switching to Select", controller.Tool == tool && session.UndoCount == history + 1 && session.Document.Elements.Count == 1 &&
            Math.Abs(session.Document.Elements.Single().Transform.M12 + 1) < 1e-8 && Math.Abs(session.Document.Elements.Single().Transform.M21 - 1) < 1e-8);
        check(tool + " rotation is exactly reversible", session.Undo() && JsonSerializer.Serialize(session.Document) == beforeRotation);

        var selected = session.Selection.Single(); history = session.UndoCount; string beforeCtrl = JsonSerializer.Serialize(session.Document);
        check(tool + " Ctrl hides the selection controls without changing selection or history", controller.ShouldShowSelection(OverlayModifiers.None) &&
            !controller.ShouldShowSelection(OverlayModifiers.Control) && !controller.ShouldShowSelection(OverlayModifiers.Control | OverlayModifiers.Shift) &&
            session.Selection.SetEquals([selected]) && session.UndoCount == history && JsonSerializer.Serialize(session.Document) == beforeCtrl);
        check(tool + " releasing Ctrl restores the existing controls", controller.ShouldShowSelection(OverlayModifiers.None));
        var previous = session.Document.Elements.Single();
        var overlapStart = previous.Transform.TransformPoint(previous.Points.Count > 0 ? previous.Points[0] : new(0, 0));
        var overlapEnd = previous.Transform.TransformPoint(previous.Points.Count > 0 ? previous.Points[^1] : new(previous.Width, previous.Height));
        Gesture(controller, overlapStart, overlapEnd, OverlayModifiers.Control);
        var duplicate = session.Document.Elements.Last();
        check(tool + " Ctrl draws a fresh overlapping object rather than transforming the selected one", session.Document.Elements.Count == 2 && duplicate.Id != id &&
            (autoSelect ? session.Selection.SetEquals([duplicate.Id]) : session.Selection.Count == 0) && controller.Tool == tool && session.UndoCount == history + 1 &&
            JsonSerializer.Serialize(session.Document.Elements[0]) == JsonSerializer.Serialize(JsonSerializer.Deserialize<SceneDocument>(beforeCtrl)!.Elements[0]));
        check(tool + " overlapping creation undoes once and restores the previous selection", session.Undo() && session.Document.Elements.Count == 1 && session.Selection.SetEquals([id]));
        history = session.UndoCount; int redo = session.RedoCount; string beforeBlank = JsonSerializer.Serialize(session.Document);
        Gesture(controller, new(500, 350), new(500, 350));
        if (EditorController.UsesDefaultSize(tool))
            check(tool + " a blank click inserts its configured default-sized object", session.Document.Elements.Count == 2 && controller.Tool == tool &&
                session.Selection.SetEquals([session.Document.Elements[^1].Id]) && session.UndoCount == history + 1 && !session.IsTransactionActive);
        else check(tool + " a blank click deselects without leaving a tiny object or history entry", session.Selection.Count == 0 && controller.Tool == tool &&
                JsonSerializer.Serialize(session.Document) == beforeBlank && session.UndoCount == history && session.RedoCount == redo && !session.IsTransactionActive);
    }

    private static void ClosedStroke(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Highlighter })
        {
            var session = new EditorSession(); var controller = new EditorController(session) { Tool = tool };
            Gesture(controller, new(30, 30), new(100, 80));
            Send(controller, OverlayPointerKind.Down, new(400, 250));
            Send(controller, OverlayPointerKind.Move, new(440, 250));
            Send(controller, OverlayPointerKind.Move, new(440, 300));
            Send(controller, OverlayPointerKind.Move, new(400, 250));
            Send(controller, OverlayPointerKind.Up, new(400, 250));
            check(tool + " a genuinely dragged closed stroke is not mistaken for a blank click", session.Document.Elements.Count == 2 && session.Document.Elements[^1].Points.Count >= 4 &&
                session.Selection.Count == 0 && session.UndoCount == 2);
        }
    }

    private static void PointerCancellation(Action<string, bool> check)
    {
        var session = new EditorSession(); var controller = new EditorController(session) { Tool = EditorTool.Rectangle };
        Gesture(controller, new(40, 50), new(160, 130));
        string before = JsonSerializer.Serialize(session.Document); var id = session.Selection.Single(); int history = session.UndoCount;
        var center = session.SelectionBounds()!.Value.Center;
        Send(controller, OverlayPointerKind.Down, center); Send(controller, OverlayPointerKind.Move, center + new PointD(50, 20));
        Send(controller, OverlayPointerKind.Cancel, center);
        check("capture cancellation rolls back follow-up adjustment and retains the previous selection", JsonSerializer.Serialize(session.Document) == before &&
            session.Selection.SetEquals([id]) && session.UndoCount == history && !session.IsTransactionActive && controller.Tool == EditorTool.Rectangle);
        Send(controller, OverlayPointerKind.Down, new(400, 300)); Send(controller, OverlayPointerKind.Move, new(480, 350));
        Send(controller, OverlayPointerKind.Cancel, new(480, 350));
        check("capture cancellation removes an unfinished new object and restores the old selection", JsonSerializer.Serialize(session.Document) == before && session.Selection.SetEquals([id]) && session.UndoCount == history);
    }

    private static void ExistingSelectionIsTheTarget(Action<string, bool> check)
    {
        var session = new EditorSession(); var controller = new EditorController(session) { Tool = EditorTool.Rectangle };
        Gesture(controller, new(40, 50), new(180, 140)); var selected = session.Document.Elements.Single();
        var overlapping = selected.Clone(true); session.Add(overlapping); session.SetSelection([selected.Id]);
        string before = JsonSerializer.Serialize(session.Document.Elements.Single(e => e.Id == overlapping.Id));
        var center = session.SelectionBounds()!.Value.Center;
        Gesture(controller, center, center + new PointD(20, 10));
        check("creation mode adjusts the selected object even when another layer covers it", session.Document.Elements.Single(e => e.Id == selected.Id).Transform.OffsetX == 60 &&
            JsonSerializer.Serialize(session.Document.Elements.Single(e => e.Id == overlapping.Id)) == before && session.Selection.SetEquals([selected.Id]));
    }

    private static void ClickObjectsAndImage(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        foreach (var tool in new[] { EditorTool.Text, EditorTool.Number, EditorTool.Emoji, EditorTool.Image })
        {
            var session = new EditorSession(); var controller = new EditorController(session) { Tool = tool };
            int textEditRequests = 0; Guid? editedText = null;
            if (tool == EditorTool.Text)
                controller.EditText += (existing, point) => { textEditRequests++; editedText = existing?.Id; CommitText(session, existing, point, controller.Style, renderer); };
            if (tool == EditorTool.Image)
                session.Add(new() { Kind = ElementKind.Image, AssetId = session.Assets.Add([1, 2, 3]), Width = 120, Height = 80, Transform = Affine2D.Translation(100, 100) });
            else Gesture(controller, new(100, 100), new(100, 100));
            var element = session.Document.Elements.Single(); var center = session.SelectionBounds()!.Value.Center; int history = session.UndoCount;
            check(tool + " successful insertion selects the object and keeps the active tool", session.Selection.SetEquals([element.Id]) && controller.Tool == tool && controller.ShouldShowSelection(OverlayModifiers.None));
            if (tool == EditorTool.Text)
            {
                string beforeClick = JsonSerializer.Serialize(session.Document);
                Gesture(controller, center, center);
                check("Text single-click inside its existing selection does not reopen inline editing", textEditRequests == 1 &&
                    JsonSerializer.Serialize(session.Document) == beforeClick && session.UndoCount == history && session.Selection.SetEquals([element.Id]));
            }
            var beforeTransform = element.Transform;
            Gesture(controller, center, center + new PointD(15, 20));
            check(tool + " the inserted object can be moved immediately in its creation mode", session.Document.Elements.Count == 1 && session.UndoCount == history + 1 && controller.Tool == tool &&
                session.Document.Elements.Single().Transform == Affine2D.Translation(15, 20).Compose(beforeTransform) && (tool != EditorTool.Text || textEditRequests == 1));
            check(tool + " Ctrl temporarily hides its adjustment controls", !controller.ShouldShowSelection(OverlayModifiers.Control) && controller.ShouldShowSelection(OverlayModifiers.None));
            if (tool == EditorTool.Text)
            {
                var target = session.Document.Elements.Single(); var bounds = ElementGeometry.Bounds(target);
                using var pixels = SkiaSharp.SKBitmap.Decode(renderer.ExportPng(session.Document, session.Assets, true))
                    ?? throw new InvalidOperationException("Could not rasterize the text double-click fixture.");
                PointD? ink = null; double nearest = double.MaxValue;
                // Target real rendered glyph ink near the center, never guessed frame
                // whitespace or a selection handle that happens to overlap the first glyph.
                for (int y = Math.Max(0, (int)Math.Floor(bounds.Top)); y < Math.Min(pixels.Height, (int)Math.Ceiling(bounds.Bottom)); y++)
                for (int x = Math.Max(0, (int)Math.Floor(bounds.Left)); x < Math.Min(pixels.Width, (int)Math.Ceiling(bounds.Right)); x++)
                {
                    if (pixels.GetPixel(x, y).Alpha == 0) continue;
                    var point = new PointD(x + .5, y + .5); double distance = point.DistanceTo(bounds.Center);
                    if (distance < nearest) { nearest = distance; ink = point; }
                }
                check("Text double-click fixture targets rendered glyph ink", ink.HasValue);
                if (ink is { } hit)
                {
                    string beforeEdit = JsonSerializer.Serialize(session.Document); int beforeHistory = session.UndoCount;
                    Send(controller, OverlayPointerKind.DoubleClick, hit); Send(controller, OverlayPointerKind.Up, hit);
                    check("Text double-click edits the existing text instead of creating or moving an object", textEditRequests == 2 && editedText == target.Id &&
                        session.Document.Elements.Count == 1 && session.Selection.SetEquals([target.Id]) && controller.Tool == EditorTool.Text &&
                        JsonSerializer.Serialize(session.Document) == beforeEdit && session.UndoCount == beforeHistory && !session.IsTransactionActive);
                }
            }
        }
        var nonCreationSession = new EditorSession();
        nonCreationSession.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60 });
        var plain = new EditorController(nonCreationSession);
        plain.Tool = EditorTool.Stamp;
        check("Stamp displays an existing selection without making new stamps selected", plain.ShouldShowSelection(OverlayModifiers.None));
        plain.Tool = EditorTool.Eraser;
        check("Eraser hides the existing selection frame", !plain.ShouldShowSelection(OverlayModifiers.None));
        plain.Tool = EditorTool.CaptureRegion;
        check("capture retains the current image and object adjustment contract", plain.ShouldShowSelection(OverlayModifiers.None));
        plain.Tool = EditorTool.Crop;
        check("crop without an editable image remains in ordinary selection mode", plain.Tool == EditorTool.Select && plain.ShouldShowSelection(OverlayModifiers.None));
    }

    private static void CtrlGestureOwnership(Action<string, bool> check)
    {
        var session = new EditorSession(); var controller = new EditorController(session) { Tool = EditorTool.Rectangle };
        Gesture(controller, new(40, 50), new(180, 140)); var id = session.Selection.Single(); var center = session.SelectionBounds()!.Value.Center;
        Send(controller, OverlayPointerKind.Down, center);
        Send(controller, OverlayPointerKind.Move, center + new PointD(20, 10), OverlayModifiers.Control);
        Send(controller, OverlayPointerKind.Up, center + new PointD(20, 10), OverlayModifiers.Control);
        check("pressing Ctrl during a selected-object drag does not change the gesture into creation", session.Document.Elements.Count == 1 && session.Selection.SetEquals([id]) &&
            session.Document.Elements.Single().Transform.OffsetX == 60 && session.UndoCount == 2);
        center = session.SelectionBounds()!.Value.Center;
        Send(controller, OverlayPointerKind.Down, center, OverlayModifiers.Control);
        Send(controller, OverlayPointerKind.Move, center + new PointD(50, 30));
        Send(controller, OverlayPointerKind.Up, center + new PointD(50, 30));
        check("Ctrl-started creation remains creation if Ctrl is released before mouse-up", session.Document.Elements.Count == 2 && session.Selection.SetEquals([session.Document.Elements[^1].Id]) &&
            session.UndoCount == 3 && controller.ShouldShowSelection(OverlayModifiers.None));
    }

    private static void ContinuousClickInsertion(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        foreach (var tool in new[] { EditorTool.Text, EditorTool.Number, EditorTool.Emoji })
        {
            var session = new EditorSession(); var controller = new EditorController(session) { Tool = tool };
            if (tool == EditorTool.Text)
                controller.EditText += (existing, point) => CommitText(session, existing, point, controller.Style, renderer);
            Gesture(controller, new(100, 100), new(100, 100));
            var first = session.Selection.Single(); string initial = JsonSerializer.Serialize(session.Document); int history = session.UndoCount;
            Gesture(controller, new(450, 300), new(450, 300));
            var second = session.Document.Elements[^1].Id; string afterSecond = JsonSerializer.Serialize(session.Document);
            check(tool + " a blank click directly inserts and selects another object while retaining the tool", session.Document.Elements.Count == 2 &&
                second != first && session.Selection.SetEquals([second]) && session.UndoCount == history + 1 && controller.Tool == tool);
            Gesture(controller, new(800, 500), new(800, 500));
            var third = session.Document.Elements[^1].Id; string afterThird = JsonSerializer.Serialize(session.Document);
            check(tool + " the next blank click continues insertion without an extra deselect click", session.Document.Elements.Count == 3 && third != second &&
                session.Selection.SetEquals([third]) && session.UndoCount == history + 2 && controller.Tool == tool &&
                (tool != EditorTool.Number || session.Document.Elements.Select(e => e.Number).SequenceEqual([1, 2, 3])));
            check(tool + " undo removes only the latest continuous insertion and restores the previous selection", session.Undo() &&
                JsonSerializer.Serialize(session.Document) == afterSecond && session.Selection.SetEquals([second]));
            check(tool + " the next undo removes the preceding insertion independently", session.Undo() &&
                JsonSerializer.Serialize(session.Document) == initial && session.Selection.SetEquals([first]));
            check(tool + " redo restores both insertions and the newest selected object", session.Redo() && session.Redo() &&
                JsonSerializer.Serialize(session.Document) == afterThird && session.Selection.SetEquals([third]) && controller.Tool == tool);
            var center = session.SelectionBounds()!.Value.Center;
            Gesture(controller, center, center + new PointD(20, 15));
            check(tool + " dragging inside the newest selection adjusts it instead of inserting", session.Document.Elements.Count == 3 &&
                session.Selection.SetEquals([third]) && session.UndoCount == history + 3 && controller.Tool == tool &&
                JsonSerializer.Serialize(session.Document.Elements[^1]) != JsonSerializer.Serialize(JsonSerializer.Deserialize<SceneDocument>(afterThird)!.Elements[^1]));
            check(tool + " moving the newest selection undoes without changing continuous insertions", session.Undo() &&
                JsonSerializer.Serialize(session.Document) == afterThird && session.Selection.SetEquals([third]));
            center = session.SelectionBounds()!.Value.Center;
            Gesture(controller, center, center, OverlayModifiers.Control);
            check(tool + " Ctrl starts an overlapping insertion rather than adjusting the selected object", session.Document.Elements.Count == 4 &&
                session.Selection.SetEquals([session.Document.Elements[^1].Id]) && session.Document.Elements[^1].Id != third && session.UndoCount == history + 3 &&
                session.Document.Elements.Take(3).Select(e => JsonSerializer.Serialize(e)).SequenceEqual(JsonSerializer.Deserialize<SceneDocument>(afterThird)!.Elements.Select(e => JsonSerializer.Serialize(e))));
        }
        var images = new EditorSession(); images.Add(new() { Kind = ElementKind.Image, AssetId = images.Assets.Add([1, 2, 3]), Width = 120, Height = 80 });
        var imageTool = new EditorController(images) { Tool = EditorTool.Image }; int hints = 0; imageTool.Message += _ => hints++;
        string beforeImageClick = JsonSerializer.Serialize(images.Document); int imageHistory = images.UndoCount;
        Gesture(imageTool, new(500, 350), new(500, 350));
        check("image guidance tool retains blank-click deselection without inserting or opening an import hint", images.Selection.Count == 0 && hints == 0 &&
            JsonSerializer.Serialize(images.Document) == beforeImageClick && images.UndoCount == imageHistory && imageTool.Tool == EditorTool.Image);
    }

    private static void Gesture(EditorController controller, PointD from, PointD to, OverlayModifiers modifiers = OverlayModifiers.None)
    { Send(controller, OverlayPointerKind.Down, from, modifiers); Send(controller, OverlayPointerKind.Move, to, modifiers); Send(controller, OverlayPointerKind.Up, to, modifiers); }
    private static void CommitText(EditorSession session,CanvasElement? existing,PointD point,ElementStyle style,SceneRenderer renderer)
    {using var edit=new InlineTextEdit(session,existing,point,style,renderer);edit.Input.Text="Editable";edit.Finish(true);}
    private static void Send(EditorController controller, OverlayPointerKind kind, PointD point, OverlayModifiers modifiers = OverlayModifiers.None) =>
        controller.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, modifiers));
}
