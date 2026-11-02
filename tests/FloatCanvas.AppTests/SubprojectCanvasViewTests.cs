using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class SubprojectCanvasViewTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LocalGroup(check); AuxiliaryRoot(check); HiddenHost(check);
    }

    private static void LocalGroup(Action<string, bool> check)
    {
        var parent = new CanvasGroup { Transform = Affine2D.Translation(300, 180).Compose(Affine2D.Rotation(25)), ElevationScale = 1.5 };
        var group = new CanvasGroup { ParentId = parent.Id, Transform = Affine2D.Translation(40, 30).Compose(Affine2D.Scale(1.4, .7)),
            PreviewCanvas = new() { Origin = new(10, 20), Viewport = new(0, 0, 640, 480) } };
        var leaf = Box("local", 50, 60); leaf.GroupId = group.Id;
        var outside = Box("outside", 40, 40);
        var session = new EditorSession(new() { Groups = [parent, group], Elements = [leaf, outside] }); session.SetEditScope(group.Id);
        var editor = new EditorController(session) { Tool = EditorTool.Select }; var scene = new GroupCanvasScene();
        var view = scene.Capture(session, null, GroupCanvasMode.Temporary, default, session.Assets);
        Configure(editor, view);
        var shown = SceneEvaluation.Build(view.Document).RenderDocument.Elements.Single();
        check("independent nested group retains its own origin and local graph across the editing viewport", shown.Transform.TransformPoint(default).DistanceTo(new(40, 40)) < 1e-7 && view.Document.Groups.Count == 0 && view.Document.Viewport == session.Document.Viewport && group.PreviewCanvas.Viewport == new RectD(0, 0, 640, 480));
        var start = new PointD(70, 60); Pointer(editor, OverlayPointerKind.Down, start); Pointer(editor, OverlayPointerKind.Up, start);
        check("independent group fallback picking resolves the real source child before a frame is presented", session.Selection.SetEquals([leaf.Id]));
        var delta = new PointD(17, 13); int history = session.UndoCount; var original = leaf.Transform; var hostPlacement = new SceneGraph(session.Document).NodeWorldPlacement(group.Id);
        Pointer(editor, OverlayPointerKind.Down, start); Pointer(editor, OverlayPointerKind.Move, start + delta); Pointer(editor, OverlayPointerKind.Up, start + delta);
        var world = new SceneGraph(session.Document).ToWorld(session.Document.Elements.Single(element => element.Id == leaf.Id));
        check("independent drag follows display coordinates and never changes group/ancestor placement", editor.CurrentCanvasMapping.ElementToScreen(world).TransformPoint(new(30, 20)).DistanceTo(start + delta) < 1e-6 &&
            new SceneGraph(session.Document).NodeWorldPlacement(group.Id) == hostPlacement && session.UndoCount == history + 1);
        check("independent drag undo restores local author coordinates", session.Undo() && session.Document.Elements.Single(element => element.Id == leaf.Id).Transform == original);
        editor.DetachSession();
    }

    private static void AuxiliaryRoot(Action<string, bool> check)
    {
        var foreground = Box("foreground", 60, 60); var main = Box("main", 60, 60); var background = Box("background", 60, 60);
        var preview = new SubprojectCanvasSettings { Viewport = new(0, 0, 640, 480), Origin = new(10, 20) };
        preview.Canvas.Projection = CanvasCalibration.CreateInitial(preview.Viewport); preview.Canvas.Projection.Enabled = true;
        preview.Canvas.Projection.Matrix = Projective2D.FromAffine(Affine2D.Translation(80, 50).Compose(Affine2D.Scale(1.2, .8)));
        preview.Canvas.Projection.Quad = EditorController.Corners(CanvasProjectionGeometry.ResolvePlaneBounds(preview.Canvas.Projection)).Select(preview.Canvas.Projection.Matrix.TransformPoint).ToList();
        var document = new SceneDocument { Elements = [background, main, foreground], RootPreviewCanvases = new() { [RootCanvasKind.Foreground] = preview } };
        SceneCanvasRoots.Assign(document, foreground.Id, RootCanvasKind.Foreground); SceneCanvasRoots.Assign(document, background.Id, RootCanvasKind.Background);
        var session = new EditorSession(document); session.SetRootCanvas(RootCanvasKind.Foreground);
        var editor = new EditorController(session) { Tool = EditorTool.Select }; var scene = new GroupCanvasScene();
        var isolated = scene.Capture(session, null, GroupCanvasMode.Temporary, default, session.Assets); Configure(editor, isolated);
        check("independent foreground displays only its author root through its own perspective", isolated.Document.Elements.Single().Id == foreground.Id && isolated.Document.RootCanvasAssignments.Count == 0 && editor.CurrentCanvasMapping.IsPerspective);
        var center = editor.CurrentCanvasMapping.ElementToScreen(foreground).TransformPoint(new(30, 20));
        Pointer(editor, OverlayPointerKind.Down, center); Pointer(editor, OverlayPointerKind.Up, center);
        check("display Main picking preserves foreground author authorization and ignores overlapping Main/Background", session.Selection.SetEquals([foreground.Id]));
        var root = scene.Capture(session, null, GroupCanvasMode.RootProject, default, session.Assets); Configure(editor, root);
        check("root-project foreground display restores all roots and screen-plane mapping", root.Document.Elements.Count == 3 && !editor.CurrentCanvasMapping.IsPerspective && editor.CurrentCanvasMapping.ToScreen(new(60, 60)) == new PointD(60, 60));
        session.SetSelection([]); Pointer(editor, OverlayPointerKind.Down, new(90, 80)); Pointer(editor, OverlayPointerKind.Up, new(90, 80));
        check("root-project picking edits only the selected foreground", session.Selection.SetEquals([foreground.Id]));
        editor.DetachSession();
    }

    private static void Configure(EditorController editor, GroupCanvasView view)
    {
        editor.SetCanvasView(view.EditingPlacement is { } placement ? CanvasMapping.CreateView(view.Document, placement, view.EditingRootCanvas) : null,
            view.EditingPlacement is not null ? view.Origin : null, view.Epoch, view.EditingPlacement, view.Document, view.EditingRootCanvas,
            !view.IsRootPreview && view.EditingPlacement is not null);
        editor.ResumePresentedPicking();
    }

    private static void HiddenHost(Action<string, bool> check)
    {
        var parent = new CanvasGroup { Visible = false }; var group = new CanvasGroup { ParentId = parent.Id, Visible = false };
        var child = Box("visible local child", 40, 40); child.GroupId = group.Id;
        var hidden = Box("hidden local child", 160, 40); hidden.GroupId = group.Id; hidden.Visible = false;
        var session = new EditorSession(new() { Groups = [parent, group], Elements = [child, hidden] }); session.SetEditScope(group.Id);
        var editor = new EditorController(session) { Tool = EditorTool.Select }; var scene = new GroupCanvasScene();
        var view = scene.Capture(session, null, GroupCanvasMode.Temporary, default, session.Assets); Configure(editor, view);
        var visible = SceneEvaluation.Build(view.Document).RenderDocument.Elements;
        check("independent preview ignores hidden host ancestors and entry while preserving a hidden child's own flag", visible.Single(element => element.Id == child.Id).Visible && !visible.Single(element => element.Id == hidden.Id).Visible);
        Pointer(editor, OverlayPointerKind.Down, new(70, 60)); Pointer(editor, OverlayPointerKind.Up, new(70, 60));
        check("visible local child remains pickable with selection controls under hidden host ancestors", session.Selection.SetEquals([child.Id]) && editor.SelectionScreenCorners.Count == 4);
        session.SetSelection([]); Pointer(editor, OverlayPointerKind.Down, new(190, 60)); Pointer(editor, OverlayPointerKind.Up, new(190, 60));
        check("independent preview cannot pick a child whose own visibility is off", session.Selection.Count == 0);
        session.Document.Groups.Single(item => item.Id == group.Id).Locked = true; session.NotifyChanged();
        Configure(editor, scene.Capture(session, null, GroupCanvasMode.Temporary, default, session.Assets));
        Pointer(editor, OverlayPointerKind.Down, new(70, 60)); Pointer(editor, OverlayPointerKind.Up, new(70, 60));
        check("independent preview preserves source ancestor locks rather than implicitly unlocking shown content", session.Selection.Count == 0 && !session.CanInsertInEditScope);
        editor.DetachSession();
    }
    private static void Pointer(EditorController editor, OverlayPointerKind kind, PointD point) => editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
    private static CanvasElement Box(string name, double x, double y) => new() { Name = name, Kind = ElementKind.Rectangle, Width = 60, Height = 40,
        Transform = Affine2D.Translation(x, y), Style = new() { Fill = 0xff3366cc, StrokeWidth = 0 } };
}
