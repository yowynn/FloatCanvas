using System.IO;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class StampPlacementRequestTests
{
    public static async Task RunAsync(Action<string, bool> assert)
    {
        var session = new EditorSession();
        var editor = new EditorController(session) { Tool = EditorTool.Stamp };
        using var guard = new StampPlacementRequestGuard(editor);
        Guid id = Guid.NewGuid(), revision = Guid.NewGuid();
        var request = guard.Capture(10, id, revision, source: false, new(320, 240));
        assert("async stamp request accepts only its original live context", guard.CanPlace(request, 10, id, revision, editing: true, available: true));
        assert("async stamp request retains the original click coordinate and target identity", request.Position == new PointD(320, 240) && ReferenceEquals(request.Target, session));
        assert("a newer stamp request invalidates a late read", !guard.CanPlace(request, 11, id, revision, true, true));
        assert("changing the selected stamp invalidates a late read", !guard.CanPlace(request, 10, Guid.NewGuid(), revision, true, true));
        assert("overwriting the catalog again invalidates a late read", !guard.CanPlace(request, 10, id, Guid.NewGuid(), true, true));
        assert("pass-through and document-busy states cannot receive late stamp placement", !guard.CanPlace(request, 10, id, revision, false, true) && !guard.CanPlace(request, 10, id, revision, true, false));
        editor.Tool = EditorTool.Freehand;
        assert("changing tools invalidates pending placement", !guard.CanPlace(request, 10, id, revision, true, true));
        editor.Tool = EditorTool.Stamp;
        assert("switching away and back cannot revive an obsolete placement", !guard.CanPlace(request, 10, id, revision, true, true));
        request = guard.Capture(10, id, revision, false, new(320, 240));
        guard.Invalidate();
        assert("leaving editing and later returning cannot revive a hidden-toolbar request", !guard.CanPlace(request, 10, id, revision, true, true));
        request = guard.Capture(10, id, revision, false, new(320, 240));
        session.Execute("edit while reading", document => document.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 20, Height = 20 }));
        assert("committed target changes block late placement", !guard.CanPlace(request, 10, id, revision, true, true));
        request = guard.Capture(10, id, revision, false, new(320, 240));
        session.BeginTransaction("new gesture");
        assert("an in-progress target gesture blocks late placement", !guard.CanPlace(request, 10, id, revision, true, true));
        session.Cancel();
        assert("a cancelled intervening gesture still invalidates the earlier request", !guard.CanPlace(request, 10, id, revision, true, true));
        request = guard.Capture(10, id, revision, false, new(320, 240));
        editor.ReplaceSession(new(), default);
        assert("replacing the active document blocks late placement into either document", !guard.CanPlace(request, 10, id, revision, true, true));
        editor.ReplaceSession(session, default);
        assert("returning to the old document cannot revive an obsolete placement", !guard.CanPlace(request, 10, id, revision, true, true));

        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-StampPlacementTests", Guid.NewGuid().ToString("N"));
        var library = new StampLibrary(profile); await library.LoadAsync();
        var source = new EditorSession(new() { Elements = [new() { Kind = ElementKind.Rectangle, Width = 75, Height = 35,
            Transform = Affine2D.Translation(30, 50).Compose(Affine2D.Rotation(27)) }, new() { Kind = ElementKind.Symbol, Symbol = SymbolKind.Heart, Width = 30, Height = 24, Transform = Affine2D.Translation(120, 60) }] });
        source.SetSelection(source.Document.Elements.Select(element => element.Id));
        var stamp = await library.SaveSelectionAsync(source, "Selected stamp");
        var previouslyCached = await library.LoadStampAsync(stamp.Id);
        source.Document.Elements[0].Style.Fill = 0xFF29AC83;
        var updated = await library.SaveSelectionAsync(source, "Selected stamp updated", replaceId: stamp.Id);
        var target = new EditorSession();
        var controller = new EditorController(target) { Tool = EditorTool.Stamp };
        using var placement = new StampPlacementRequestGuard(controller);
        var reload = placement.Capture(20, updated.Id, updated.Revision, false, new(580, 410));
        var current = await library.LoadStampAsync(updated.Id);
        if (placement.CanPlace(reload, 20, updated.Id, updated.Revision, true, true)) StampLibrary.Place(current, reload.Target, reload.Position);
        assert("one click after overwrite places the reloaded revision in one undo step", target.UndoCount == 1 && target.Document.Elements[0].Style.Fill == 0xFF29AC83 && previouslyCached.Document.Elements[0].Style.Fill != 0xFF29AC83);
        var bounds = target.Document.Elements.Select(element => ElementGeometry.Bounds(element)).Aggregate(RectD.Union);
        assert("reloaded placement uses the captured click and preserves original rotation and dimensions", bounds.Center.DistanceTo(reload.Position) < 1e-8 && target.Document.Elements[0].Width == 75 &&
            target.Document.Elements[0].Transform.M11 == source.Document.Elements[0].Transform.M11);

        var flattened = await library.SaveSelectionAsync(source, "Flattened preset", StampSaveMode.RasterMerge);
        var raster = await library.LoadStampAsync(flattened.Id);
        var editableSource = await library.LoadSourceAsync(flattened.Id);
        var sourceTarget = new EditorSession();
        StampLibrary.Place(editableSource, sourceTarget, new(500, 300));
        Guid? group = sourceTarget.Document.RootGroupId(sourceTarget.Document.Elements[0]);
        assert("default merged use remains one image while source use restores editable vector children", raster.Document.Elements is [{ Kind: ElementKind.Image }] &&
            sourceTarget.Document.Elements.Count == 2 && sourceTarget.Document.Elements[0].Kind == ElementKind.Rectangle && group is not null);
        assert("source-object use keeps creation size and groups the children in one undo step", sourceTarget.UndoCount == 1 && sourceTarget.Document.Elements[0].Width == 75 &&
            sourceTarget.Document.Elements[0].Height == 35 && sourceTarget.Document.Elements.All(element => sourceTarget.Document.RootGroupId(element) == group));
        sourceTarget.SetSelection([sourceTarget.Document.Elements[0].Id]);
        sourceTarget.UpdateSelection(element => element.Style.Fill = 0xFFCC7733, "edit restored source");
        sourceTarget.SetSelection(sourceTarget.Document.GroupElements(group!.Value).Select(element => element.Id));
        var edited = await library.SaveSelectionAsync(sourceTarget, flattened.Name, StampSaveMode.RasterMerge, flattened.Id);
        var editedSource = await library.LoadSourceAsync(edited.Id);
        assert("edited source objects can overwrite the same merged preset without losing the other child", edited.Id == flattened.Id && editedSource.Document.Elements.Count == 2 && editedSource.Document.Elements[0].Style.Fill == 0xFFCC7733);
        var pending = placement.Capture(21, updated.Id, updated.Revision, false, new(700, 400));
        controller.ReplaceSession(new(), default);
        var late = await library.LoadStampAsync(updated.Id);
        if (placement.CanPlace(pending, 21, updated.Id, updated.Revision, true, true)) StampLibrary.Place(late, pending.Target, pending.Position);
        assert("a completed reload never inserts into a replaced document or its abandoned predecessor", controller.Session.Document.Elements.Count == 0 && target.Document.Elements.Count == 2 && target.UndoCount == 1);
    }
}
