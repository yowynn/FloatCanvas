using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class ImageToolEligibilityTests
{
    internal static void Run(Action<string, bool> check)
    {
        var first = Image(20); var second = Image(180);
        var shape = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 30 };
        var session = new EditorSession(new() { Elements = [first, second, shape] });
        var editor = new EditorController(session);
        foreach (var ids in new Guid[][] { [], [shape.Id], [first.Id, second.Id], [first.Id, shape.Id] })
        {
            session.SetSelection(ids); editor.Tool = EditorTool.Crop;
            check("crop rejects non-single-image selection " + ids.Length, !editor.CanCropSelection && editor.Tool == EditorTool.Select);
        }
        session.SetSelection([first.Id]); editor.Tool = EditorTool.Crop;
        check("one editable image enables crop", editor.CanCropSelection && editor.Tool == EditorTool.Crop);
        session.SetSelection([first.Id, second.Id]);
        var state = editor.Parameters; state.Tool = EditorTool.Crop; editor.ApplyParameters(state);
        check("restoring cached crop cannot bypass mixed or multiple selection", editor.Tool == EditorTool.Select && !editor.CanCropSelection);
        foreach (string reason in new[] { "locked", "hidden", "singular", "empty" })
        {
            first.Locked = reason == "locked"; first.Visible = reason != "hidden";
            first.Transform = reason == "singular" ? Affine2D.Scale(0, 1) : Affine2D.Translation(20, 20);
            first.Width = reason == "empty" ? 0 : 100;
            session.SetSelection([first.Id]); editor.Tool = EditorTool.Crop;
            check("crop rejects " + reason + " image", !editor.CanCropSelection && editor.Tool == EditorTool.Select);
        }
        first.Visible = true; first.Locked = false; first.Width = 100; first.Transform = Affine2D.Translation(20, 20);
        var group = new CanvasGroup(); session.Document.Groups.Add(group); first.GroupId = second.GroupId = group.Id;
        session.SetSelection(session.SelectionUnitIds(first.Id));
        check("a group containing two images cannot become a crop target", !editor.CanCropSelection);
        session.SetSelection([first.Id]);
        check("an individually selected editable image member remains eligible", editor.CanCropSelection);
        foreach (string change in new[] { "selection", "lock", "hide", "remove", "content" })
        {
            var target = Image(20); var other = Image(180);
            var s = new EditorSession(new() { Elements = [target, other] }); s.SetSelection([target.Id]);
            var c = new EditorController(s) { Tool = EditorTool.Crop };
            Send(c, OverlayPointerKind.Down, 30, 30); Send(c, OverlayPointerKind.Move, 75, 70);
            check("crop begins a transient preview before " + change, c.EffectPreviewPolygon is not null && s.UndoCount == 0);
            if (change == "selection") s.SetSelection([other.Id]);
            else s.Execute("external change", d =>
            {
                if (change == "lock") target.Locked = true;
                if (change == "hide") target.Visible = false;
                if (change == "remove") d.Elements.Remove(target);
                if (change == "content") target.Transform = Affine2D.Translation(25, 25);
            });
            string before = JsonSerializer.Serialize(s.Document); int undo = s.UndoCount;
            Send(c, OverlayPointerKind.Up, 90, 80);
            check("changed crop target cancels safely after " + change, c.EffectPreviewPolygon is null && c.Marquee is null &&
                JsonSerializer.Serialize(s.Document) == before && s.UndoCount == undo && !s.IsTransactionActive);
        }
        var frozen = new EditorSession(new() { Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, 300, 200),
            Background = new() { Kind = ElementKind.Image, Width = 300, Height = 200 } });
        var frozenEditor = new EditorController(frozen) { Tool = EditorTool.Crop };
        check("a legacy frozen background alone is never a selected crop target", frozenEditor.Tool == EditorTool.Select && !frozenEditor.CanCropSelection && frozen.UndoCount == 0);
        var valid = new EditorSession(new() { Elements = [Image(20)] }); valid.SetSelection([valid.Document.Elements[0].Id]);
        var crop = new EditorController(valid) { Tool = EditorTool.Crop };
        Send(crop, OverlayPointerKind.Down, 30, 30); Send(crop, OverlayPointerKind.Move, 75, 70); Send(crop, OverlayPointerKind.Up, 75, 70);
        check("eligible image crop commits once without changing the canvas viewport", valid.UndoCount == 1 && valid.Document.Elements[0] is { Width: 45, Height: 40, ImageCrop: not null } &&
            valid.Document.Viewport == new SceneDocument().Viewport && crop.Tool == EditorTool.Crop);
    }
    private static CanvasElement Image(double x) => new() { Kind = ElementKind.Image, Width = 100, Height = 80, AssetId = new string('a', 64), Transform = Affine2D.Translation(x, 20) };
    private static void Send(EditorController editor, OverlayPointerKind kind, double x, double y) =>
        editor.Pointer(new(kind, new(x, y), OverlayMouseButton.Left, OverlayModifiers.Control));
}
