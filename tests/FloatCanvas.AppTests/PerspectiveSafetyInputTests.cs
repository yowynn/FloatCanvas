using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class PerspectiveSafetyInputTests
{
    internal static void Run(Action<string, bool> check)
    {
        InvalidGestureRollsBack(check);
        StrokeFragments(check);
        StrokeModifierReentry(check);
        StrokeModifierEnvelope(check);
        BillboardAboveHorizon(check);
        CreationPaths(check);
    }

    private static SceneDocument Document(double gradient = .005)
    {
        var matrix = new Projective2D(1, 0, 0, 0, 1, 0, 0, gradient, 1);
        return new() { Viewport = new(0, -400, 1000, 1000), Canvas = new() { Projection = new()
        { Enabled = true, Columns = 100, Rows = 100, CellSize = 1, Matrix = matrix,
            Quad = EditorController.Corners(new RectD(0, 0, 100, 100)).Select(matrix.TransformPoint).ToList() } } };
    }

    private static void Send(EditorController editor, OverlayPointerKind kind, PointD screen, bool create = false) =>
        editor.Pointer(new(kind, new(screen.X + editor.ScreenOrigin.X, screen.Y + editor.ScreenOrigin.Y),
            OverlayMouseButton.Left, create ? OverlayModifiers.Control : OverlayModifiers.None));

    private static void InvalidGestureRollsBack(Action<string, bool> check)
    {
        var scene = Document();
        var box = new CanvasElement { Kind = ElementKind.Rectangle, Width = 1000, Height = 1200,
            Transform = Affine2D.Translation(1000, 800), Style = new() { StrokeWidth = 0, Fill = 0xFFFF0000 } };
        scene.Elements.Add(box);
        var session = new EditorSession(scene); session.SetSelection([box.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Select, ScreenOrigin = new(-1600, 80) };
        var mapping = CanvasMapping.Create(scene);
        var down = mapping.ToScreen(new(1600, 1200));
        var safe = mapping.ToScreen(new(1600, 1800));
        var unsafeExtent = mapping.ToScreen(new(1600, 3200));
        string original = JsonSerializer.Serialize(session.Document);
        check("safety drag fixture has a safe pointer but an unsafe full rectangle candidate", mapping.IsScreenSafe(unsafeExtent) &&
            !mapping.IsElementSafe(new() { Kind = box.Kind, Width = box.Width, Height = box.Height,
                Transform = Affine2D.Translation(1000, 2800), Style = box.Style }));
        Send(editor, OverlayPointerKind.Down, down);
        Send(editor, OverlayPointerKind.Move, safe);
        check("safe part of the move remains a live preview", session.IsTransactionActive && box.Transform != Affine2D.Translation(1000, 800));
        Send(editor, OverlayPointerKind.Move, unsafeExtent);
        check("unsafe whole-object move shows blocked pointer and retains finite safe preview", editor.IsPointerBlocked &&
            CanvasMapping.Create(session.Document).IsElementSafe(session.Document.Elements.Single()));
        Send(editor, OverlayPointerKind.Move, unsafeExtent);
        check("repeated stationary move preserves the forbidden pointer", editor.IsPointerBlocked);
        Send(editor, OverlayPointerKind.Up, unsafeExtent);
        check("unsafe release restores mouse-down state including an earlier valid preview", JsonSerializer.Serialize(session.Document) == original &&
            !session.IsTransactionActive && session.UndoCount == 0);
        check("rollback clears a stale forbidden cursor when the released pointer itself is safe", !editor.IsPointerBlocked);

        Send(editor, OverlayPointerKind.Down, down);
        Send(editor, OverlayPointerKind.Move, unsafeExtent);
        Send(editor, OverlayPointerKind.Move, safe);
        check("returning to safety clears forbidden pointer during the same drag", !editor.IsPointerBlocked);
        Send(editor, OverlayPointerKind.Up, safe);
        check("return-to-safety commits one reversible transform", session.UndoCount == 1 && session.Undo() &&
            JsonSerializer.Serialize(session.Document) == original);

        editor.Tool = EditorTool.Rectangle; editor.DefaultHeight = 2000;
        int messages = 0; editor.Message += _ => messages++;
        Send(editor, OverlayPointerKind.Down, new(100, 188), create: true);
        check("click placement rejects a full default shape extending beyond safety even if its center is safe", messages > 0 &&
            editor.IsPointerBlocked && !session.IsTransactionActive && JsonSerializer.Serialize(session.Document) == original);
    }

    private static void StrokeFragments(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Highlighter })
        {
            var session = new EditorSession(Document());
            var editor = new EditorController(session) { Tool = tool };
            Send(editor, OverlayPointerKind.Down, new(100, 150), create: true);
            Send(editor, OverlayPointerKind.Move, new(110, 170), create: true);
            Send(editor, OverlayPointerKind.Move, new(120, 198), create: true);
            Send(editor, OverlayPointerKind.Move, new(130, 180), create: true);
            Send(editor, OverlayPointerKind.Up, new(140, 182), create: true);
            var mapping = CanvasMapping.Create(session.Document);
            check($"{tool} crossing and reentering safety creates disconnected safe fragments in one undo", session.Document.Elements.Count == 2 &&
                session.Document.Elements.All(e => mapping.IsElementSafe(e)) && session.UndoCount == 1 &&
                session.Document.Elements[0].Points.Count == 2 && session.Document.Elements[1].Points.Count == 2);
            SceneValidation.Validate(session.Document);
            check($"{tool} fragments undo together", session.Undo() && session.Document.Elements.Count == 0 && session.Redo() && session.Document.Elements.Count == 2);

            session = new(Document()); editor = new(session) { Tool = tool };
            Send(editor, OverlayPointerKind.Down, new(100, 150), create: true);
            Send(editor, OverlayPointerKind.Move, new(110, 170), create: true);
            Send(editor, OverlayPointerKind.Up, new(120, 198), create: true);
            check($"{tool} release in forbidden space retains its already accepted safe portion", session.Document.Elements.Count == 1 &&
                session.UndoCount == 1 && !session.IsTransactionActive && CanvasMapping.Create(session.Document).IsElementSafe(session.Document.Elements[0]));
        }
    }

    private static void StrokeModifierReentry(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Highlighter })
        {
            var session = new EditorSession(Document());
            var editor = new EditorController(session) { Tool = tool };
            Send(editor, OverlayPointerKind.Down, new(100, 150), create: true);
            Send(editor, OverlayPointerKind.Move, new(110, 170), create: true);
            var accepted = session.Document.Elements.Single().Points.ToArray();
            Send(editor, OverlayPointerKind.Move, new(120, 198), create: true);

            // The keyboard packet can carry the new safe pointer position before its mouse-move packet.
            editor.ModifiersChanged(OverlayModifiers.Shift, new(130, 180), pointerLeftDown: true);
            check($"{tool} Shift reentry starts a new fragment without extending the earlier accepted curve",
                session.Document.Elements.Count == 2 && session.Document.Elements[0].Points.SequenceEqual(accepted) &&
                session.Document.Elements[1].Points.Count == 1 && !editor.IsPointerBlocked);

            editor.Pointer(new(OverlayPointerKind.Move, new(140, 182), OverlayMouseButton.Left, OverlayModifiers.Shift));
            editor.Pointer(new(OverlayPointerKind.Up, new(145, 184), OverlayMouseButton.Left, OverlayModifiers.Shift));
            var mapping = CanvasMapping.Create(session.Document);
            var resumed = session.Document.Elements[1];
            var screen = mapping.ElementToScreen(resumed);
            check($"{tool} Shift reentry retains only its new straight endpoint and commits safe fragments together",
                session.Document.Elements[0].Points.SequenceEqual(accepted) && resumed.Points.Count == 2 &&
                screen.TransformPoint(resumed.Points[0]).DistanceTo(new(130, 180)) < 1e-6 &&
                screen.TransformPoint(resumed.Points[1]).DistanceTo(new(145, 184)) < 1e-6 &&
                session.Document.Elements.All(item => mapping.IsElementSafe(item)) && session.UndoCount == 1 && !session.IsTransactionActive);
            SceneValidation.Validate(session.Document);
            check($"{tool} Shift-reentered fragments undo and redo as one gesture", session.Undo() &&
                session.Document.Elements.Count == 0 && session.Redo() && session.Document.Elements.Count == 2 &&
                session.Document.Elements[0].Points.SequenceEqual(accepted));
        }
    }

    private static void StrokeModifierEnvelope(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Highlighter })
        {
            var document = Document();
            var matrix = Projective2D.FromAffine(Affine2D.Translation(400, 100))
                .Compose(new(1, 0, 0, 0, 1, 0, .005, .005, 1));
            document.Canvas.Projection.Matrix = matrix;
            document.Canvas.Projection.Quad = EditorController.Corners(new RectD(0, 0, 100, 100)).Select(matrix.TransformPoint).ToList();
            var session = new EditorSession(document);
            var editor = new EditorController(session) { Tool = tool };
            var mapping = CanvasMapping.Create(document);
            var first = matrix.TransformPoint(new(-1000, 2000));
            var next = matrix.TransformPoint(new(-980, 1990));
            var rejected = matrix.TransformPoint(new(2000, -1000));
            Send(editor, OverlayPointerKind.Down, first, create: true);
            Send(editor, OverlayPointerKind.Move, next, create: true);
            var accepted = document.Elements.Single().Points.ToArray();
            check($"{tool} diagonal-horizon modifier fixture has a safe new point but an unsafe complete stroke envelope",
                mapping.IsScreenSafe(rejected) && !mapping.IsElementSafe(new()
                { Kind = document.Elements[0].Kind, Transform = document.Elements[0].Transform, Style = document.Elements[0].Style,
                    Points = [.. accepted, mapping.ScreenToPlane.TransformPoint(rejected) - mapping.ScreenToPlane.TransformPoint(first)] }));

            // No earlier unsafe mouse packet has marked this curve as interrupted.
            editor.ModifiersChanged(OverlayModifiers.Shift, new(rejected.X, rejected.Y), pointerLeftDown: true);
            check($"{tool} Shift checks the entire candidate before appending to an uninterrupted curve",
                editor.IsPointerBlocked && document.Elements.Count == 1 && document.Elements[0].Points.SequenceEqual(accepted) &&
                mapping.IsElementSafe(document.Elements[0]));
            editor.Pointer(new(OverlayPointerKind.Up, new(rejected.X, rejected.Y), OverlayMouseButton.Left, OverlayModifiers.Shift));
            check($"{tool} rejected modifier envelope can finish only disconnected safe fragments", session.UndoCount == 1 &&
                !session.IsTransactionActive && document.Elements.Count == 2 && document.Elements[0].Points.SequenceEqual(accepted) &&
                document.Elements.All(e => mapping.IsElementSafe(e)));
            SceneValidation.Validate(session.Document);
            check($"{tool} diagonal modifier fragments remain one reversible operation", session.Undo() &&
                session.Document.Elements.Count == 0 && session.Redo() && session.Document.Elements.Count == 2);
        }
    }

    private static void BillboardAboveHorizon(Action<string, bool> check)
    {
        var scene = Document(-.005);
        var item = new CanvasElement { Kind = ElementKind.Rectangle, Width = 120, Height = 360,
            IsBillboard = true, BillboardAnchor = new(300, 0), BillboardHeight = 100,
            Style = new() { StrokeWidth = 0, Fill = 0xFFFF0000 } };
        scene.Elements.Add(item);
        var session = new EditorSession(scene);
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        var sky = new PointD(300, -250);
        check("billboard safety uses the ground anchor while its visible top crosses the horizon", !CanvasMapping.Create(scene).IsScreenSafe(sky) &&
            CanvasMapping.Create(scene).IsElementSafe(item));
        Send(editor, OverlayPointerKind.Down, sky);
        Send(editor, OverlayPointerKind.Up, sky);
        check("billboard sky portion remains selectable without ground inversion", session.Selection.SetEquals([item.Id]) && !session.IsTransactionActive);
        Send(editor, OverlayPointerKind.Down, sky);
        Send(editor, OverlayPointerKind.Up, sky + new PointD(30, 0));
        check("billboard sky portion can be grabbed and moved while its ground anchor remains safe", session.UndoCount == 1 &&
            Math.Abs(session.Document.Elements.Single().BillboardAnchor.X - 330) < 1e-6 && !editor.IsPointerBlocked);

        item = session.Document.Elements.Single(); item.Kind = ElementKind.Image;
        editor.Tool = EditorTool.Crop;
        var matrix = CanvasMapping.Create(session.Document).ElementToScreen(item);
        var from = matrix.TransformPoint(new(10, 20)); var to = matrix.TransformPoint(new(100, 60));
        Send(editor, OverlayPointerKind.Down, from, create: true); Send(editor, OverlayPointerKind.Up, to, create: true);
        check("billboard crop input above the ground horizon uses its own screen mapping", item.ImageCrop is { } crop &&
            Math.Abs(crop.X - 10d / 120) < 1e-6 && Math.Abs(crop.Y - 20d / 360) < 1e-6 &&
            item.Width == 90 && item.Height == 40 && !editor.IsPointerBlocked);
    }

    private static void CreationPaths(Action<string, bool> check)
    {
        using var bitmap = new SKBitmap(4, 4); bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var png = encoded.ToArray();
        var session = new EditorSession(Document());
        bool rejected = false;
        try { SnapshotImageCommand.Insert(session, new(png, new(50, 185, 60, 30), true)); }
        catch (InvalidOperationException) { rejected = true; }
        check("snapshot rejects an image spanning the finite margin before publishing a layer or history", rejected &&
            session.Document.Elements.Count == 0 && session.Assets.Count == 0 && session.UndoCount == 0 && !session.IsTransactionActive);
        rejected = false;
        try { ImageImportCommand.Insert(session, [new(png, 100, 2000)], new(0, 0, 5000, 5000), dropCenter: new(1000, 3200)); }
        catch (InvalidOperationException) { rejected = true; }
        check("image import rejects unsafe extent atomically before importing any resources", rejected && session.Document.Elements.Count == 0 &&
            session.UndoCount == 0 && session.Assets.Count == 0);

        using var renderer = new SceneRenderer();
        using var text = new InlineTextEdit(session, null, new(50, 3790), new() { FontSize = 80 }, renderer);
        bool textRejected = false; text.PlacementRejected += () => textRejected = true;
        text.Input.Text = "Horizon text\nsecond line";
        text.Finish(commit: true);
        check("inline text growth beyond safety cancels the complete text edit and reports inability to create", textRejected &&
            session.Document.Elements.Count == 0 && session.UndoCount == 0 && !session.IsTransactionActive);

        var editor = new EditorController(session) { Tool = EditorTool.CaptureRegion };
        int captures = 0, messages = 0;
        editor.CaptureSelectionCompleted += _ => captures++; editor.Message += _ => messages++;
        Send(editor, OverlayPointerKind.Down, new(100, 150), create: true);
        Send(editor, OverlayPointerKind.Up, new(180, 198), create: true);
        check("capture finishing across safety never requests an asynchronous capture and explains rejection", captures == 0 &&
            messages == 1 && !editor.HasCaptureGesture && session.UndoCount == 0);
        Send(editor, OverlayPointerKind.Down, new(100, 150), create: true);
        Send(editor, OverlayPointerKind.Up, new(150, 175), create: true);
        check("capture can be retried normally inside safety", captures == 1 && !editor.IsPointerBlocked);

        editor.Tool = EditorTool.Number;
        var numberMapping = CanvasMapping.Create(session.Document);
        var numberScreenCenter = new PointD(100, 188);
        check("number placement fixture center remains inside the finite ground domain", numberMapping.TryToPlane(numberScreenCenter, out var numberCenter));
        CanvasElement Badge(double size) => new() { Kind = ElementKind.Number, Width = size, Height = size,
            Transform = Affine2D.Translation(numberCenter.X - size / 2, numberCenter.Y - size / 2) };
        foreach (double size in new[] { 1000, 1000 / .58, 2000 })
        {
            var badge = Badge(size);
            var plane = ElementGeometry.Bounds(badge); var screen = numberMapping.ProjectedBounds(badge);
            Console.WriteLine($"NUMBER_SAFETY size={size:R} plane=[{plane.Left:R},{plane.Top:R},{plane.Right:R},{plane.Bottom:R}] screen=[{screen.Left:R},{screen.Top:R},{screen.Right:R},{screen.Bottom:R}] safe={numberMapping.IsElementSafe(badge)} safeScreenMaxY={numberMapping.SafeScreenPolygon.Max(p => p.Y):R}");
        }
        check("a 1000 pixel badge is safe while its former font-derived diameter crosses the finite margin", numberMapping.IsElementSafe(Badge(1000)) && !numberMapping.IsElementSafe(Badge(1000 / .58)));
        check("a 2000 pixel badge has an unsafe envelope despite a safe center", !numberMapping.IsElementSafe(Badge(2000)) && numberMapping.ProjectedBounds(Badge(2000)).Bottom > numberMapping.SafeScreenPolygon.Max(p => p.Y));
        // Number Size is the badge diameter now, so use an actual size that crosses the finite margin.
        var parameters = editor.Parameters; parameters.Style.FontSize = 2000; editor.ApplyParameters(parameters);
        session.Document.NextNumber = 42;
        Send(editor, OverlayPointerKind.Down, new(100, 188), create: true);
        check("unsafe number badge neither inserts a layer nor consumes the next number", editor.IsPointerBlocked &&
            session.Document.Elements.Count == 0 && session.Document.NextNumber == 42 && session.UndoCount == 0);

        var stamp = new SceneDocument { Viewport = new(0, 0, 100, 2000), Elements =
            [new() { Kind = ElementKind.Rectangle, Width = 100, Height = 2000, Style = new() { StrokeWidth = 0 } }] };
        rejected = false;
        try { StampLibrary.Place(new(stamp, new()), session, new(1000, 3200)); }
        catch (InvalidOperationException) { rejected = true; }
        check("editable stamp checks all placed members before importing or adding any history", rejected &&
            session.Document.Elements.Count == 0 && session.UndoCount == 0 && session.Assets.Count == 0);
    }
}
