using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class ImageToolTargetTests
{
    internal static void Run(Action<string, bool> assert)
    {
        foreach (var tool in new[] { EditorTool.Crop, EditorTool.Blur, EditorTool.Pixelate })
        {
            var locked = Create(tool, true);
            locked.Session.Execute("Seed redo", d => d.LayerOpacity = .8);
            locked.Session.Undo();
            string before = JsonSerializer.Serialize(locked.Session.Document);
            int undo = locked.Session.UndoCount, redo = locked.Session.RedoCount;
            var messages = new List<string>(); locked.Controller.Message += messages.Add;
            if (tool != EditorTool.Crop) Gesture(locked.Controller);
            assert(tool + " rejects a selected locked image without falling back to capture background or viewport",
                JsonSerializer.Serialize(locked.Session.Document) == before && locked.Session.UndoCount == undo && locked.Session.RedoCount == redo);
            assert(tool + " rejects locked-image activation consistently", tool == EditorTool.Crop ? locked.Controller.Tool == EditorTool.Select && messages.Count == 0 : messages.SequenceEqual([L.Get("Editor.ImageTargetLocked")]));

            locked.Session.SetLocked(false);
            locked.Controller.Tool = tool;
            before = JsonSerializer.Serialize(locked.Session.Document); undo = locked.Session.UndoCount;
            Gesture(locked.Controller);
            assert(tool + " retries the same selected image after unlocking with one operation", ChangedImageOnly(locked.Session, tool) && locked.Session.UndoCount == undo + 1);
            locked.Session.Undo();
            assert(tool + " retry undo restores the complete unlocked document", JsonSerializer.Serialize(locked.Session.Document) == before);

            var editable = Create(tool, false);
            Gesture(editable.Controller);
            assert(tool + " processes the single editable selected image", ChangedImageOnly(editable.Session, tool) && editable.Session.UndoCount == 1);

            var unselected = Create(tool, true);
            unselected.Session.SetSelection([]);
            before = JsonSerializer.Serialize(unselected.Session.Document);
            Gesture(unselected.Controller);
            if (tool == EditorTool.Crop)
                assert("crop never falls back to a frozen background", JsonSerializer.Serialize(unselected.Session.Document) == before && unselected.Session.UndoCount == 0 && unselected.Controller.Tool == EditorTool.Select);
            else
            {
                assert(tool + " uses frozen background or canvas only when no image is selected", ChangedBackgroundOnly(unselected.Session, tool) && unselected.Session.UndoCount == 1);
                unselected.Session.Undo();
                assert(tool + " background or canvas operation undoes atomically", JsonSerializer.Serialize(unselected.Session.Document) == before);
            }

            foreach (var locks in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                var multi = Create(tool, locks.Item1);
                var second = multi.Session.Document.Elements[0].Clone(true); second.Locked = locks.Item2;
                second.Transform = Affine2D.Translation(160, 50); multi.Session.Document.Elements.Add(second);
                multi.Session.SetSelection(multi.Session.Document.Elements.Select(e => e.Id));
                before = JsonSerializer.Serialize(multi.Session.Document);
                messages = []; multi.Controller.Message += messages.Add;
                if (tool != EditorTool.Crop) Gesture(multi.Controller);
                if (tool == EditorTool.Crop)
                {
                    assert($"{tool} rejects two selected images with locks {locks} without silently choosing another target",
                        JsonSerializer.Serialize(multi.Session.Document) == before && multi.Session.UndoCount == 0 && multi.Session.Selection.Count == 2);
                    assert($"{tool} returns to selection mode for multiple images with locks {locks}", multi.Controller.Tool == EditorTool.Select && !multi.Controller.CanCropSelection);
                }
                else
                {
                    assert($"{tool} processes the frozen canvas when multiple images are selected with locks {locks}",
                        multi.Session.Document.Background!.Effects.Count == 1 && multi.Session.Document.Elements.All(e => e.Effects.Count == 0) && multi.Session.UndoCount == 1);
                    assert($"{tool} does not require a single image for a general canvas effect", messages.Count == 0);
                }
            }

            var mixed = Create(tool, false);
            var shape = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 30 };
            mixed.Session.Document.Elements.Add(shape);
            mixed.Session.SetSelection(mixed.Session.Document.Elements.Select(e => e.Id));
            if (tool == EditorTool.Crop)
                assert("crop rejects mixed image and shape selection", mixed.Controller.Tool == EditorTool.Select && !mixed.Controller.CanCropSelection && mixed.Session.UndoCount == 0);
            else
            {
                Gesture(mixed.Controller);
                assert(tool + " keeps the one image target when non-image objects are also selected",
                    ChangedImageOnly(mixed.Session, tool) && mixed.Session.Document.Elements.Single(e => e.Id == shape.Id).Width == 30);
            }
        }
        foreach (string language in new[] { "en", "zh-CN" })
        {
            L.Initialize(language);
            foreach (string key in new[] { "Editor.ImageTargetLocked", "Editor.SelectSingleImage" })
                assert(language + " provides a localized image-target validation message for " + key, L.Get(key) != key && L.Get(key).Length > 12);
        }
        L.Initialize("zh-CN");
    }

    private static (EditorSession Session, EditorController Controller) Create(EditorTool tool, bool locked)
    {
        var image = new CanvasElement { Kind = ElementKind.Image, AssetId = new string('a', 64), Width = 100, Height = 80,
            Transform = Affine2D.Translation(50, 50), Locked = locked };
        var session = new EditorSession(new SceneDocument
        {
            Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, 400, 300), Elements = [image],
            Background = new() { Kind = ElementKind.Image, AssetId = new string('b', 64), Width = 400, Height = 300, Locked = true }
        });
        session.SetSelection([image.Id]);
        return (session, new EditorController(session) { Tool = tool });
    }

    private static void Gesture(EditorController controller)
    {
        foreach (var (kind, point) in new[] { (OverlayPointerKind.Down, new OverlayPoint(60, 60)),
            (OverlayPointerKind.Move, new OverlayPoint(90, 90)), (OverlayPointerKind.Up, new OverlayPoint(90, 90)) })
            controller.Pointer(new(kind, point, OverlayMouseButton.Left, OverlayModifiers.Control));
    }

    private static bool ChangedImageOnly(EditorSession session, EditorTool tool)
    {
        var image = session.Document.Elements.Single(e => e.Kind == ElementKind.Image);
        return session.Document.Viewport == new RectD(0, 0, 400, 300) && session.Document.Background!.Effects.Count == 0 &&
            (tool == EditorTool.Crop ? image.Width == 30 && image.Height == 30 && image.Transform.OffsetX == 60 && image.Transform.OffsetY == 60 :
                image.Width == 100 && image.Height == 80 && image.Effects.Count == 1 && image.Effects[0].Kind.ToString() == tool.ToString());
    }

    private static bool ChangedBackgroundOnly(EditorSession session, EditorTool tool)
    {
        var image = session.Document.Elements.Single();
        return image.Width == 100 && image.Height == 80 && image.Effects.Count == 0 && image.ImageCrop is null &&
            (tool == EditorTool.Crop ? session.Document.Viewport == new RectD(60, 60, 30, 30) :
                session.Document.Viewport == new RectD(0, 0, 400, 300) && session.Document.Background!.Effects.Count == 1 && session.Document.Background.Effects[0].Kind.ToString() == tool.ToString());
    }
}
