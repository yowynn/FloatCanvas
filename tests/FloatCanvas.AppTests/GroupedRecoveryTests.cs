using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class GroupedRecoveryTests
{
    internal static void Run(Action<string, bool> check)
    {
        foreach (bool perspective in new[] { false, true })
        {
            var (document, root, nested, surface, board, hidden) = Fixture(2400, perspective);
            var session = new EditorSession(document); session.SetSelection([root.Id]);
            string before = JsonSerializer.Serialize(document);
            string children = JsonSerializer.Serialize(document.Elements);
            var nestedPlacement = nested.Transform; var rootLinear = root.Transform;
            var oldWorld = SceneEvaluation.Build(document);
            var oldHidden = oldWorld.FindElement(hidden.Id)!;
            check($"group recovery uses world bounds and moves nested parent placement perspective={perspective}",
                Move(session) && root.Transform != rootLinear && nested.Transform == nestedPlacement &&
                JsonSerializer.Serialize(document.Elements) == children && session.Selection.SetEquals([root.Id]) && session.UndoCount == 1);
            var world = SceneEvaluation.Build(document); var mapping = CanvasMapping.Create(document);
            check($"group recovery makes world content reachable and retains billboard local elevation perspective={perspective}",
                new[] { surface.Id, board.Id }.Any(id => !mapping.ProjectedBounds(world.FindElement(id)!).Intersect(document.Viewport).IsEmpty) &&
                board.BillboardHeight == 30 && Math.Abs(world.FindElement(board.Id)!.BillboardHeight - 90) < 1e-8 &&
                !world.FindElement(hidden.Id)!.Visible && world.FindElement(hidden.Id)!.Locked &&
                world.FindElement(hidden.Id)!.Transform != oldHidden.Transform);
            var worldSurfaceDelta = world.FindElement(surface.Id)!.Transform.OffsetX - oldWorld.FindElement(surface.Id)!.Transform.OffsetX;
            var hiddenDelta = world.FindElement(hidden.Id)!.Transform.OffsetX - oldHidden.Transform.OffsetX;
            check($"hidden and locked group descendants follow a single parent translation perspective={perspective}",
                Math.Abs(worldSurfaceDelta - hiddenDelta) < 1e-7 && Math.Abs(worldSurfaceDelta) > 1 && !Move(session));
            string applied = JsonSerializer.Serialize(document);
            check($"group recovery undo redo retains exact hierarchy and local values perspective={perspective}",
                session.Undo() && JsonSerializer.Serialize(session.Document) == before && session.Redo() && JsonSerializer.Serialize(session.Document) == applied);
        }

        foreach (bool locked in new[] { false, true })
        {
            var (document, root, _, _, _, _) = Fixture(2400, false);
            if (locked) root.Locked = true; else root.Visible = false;
            string before = JsonSerializer.Serialize(document); var session = new EditorSession(document);
            check($"hidden or locked root groups remain untouched locked={locked}", !Move(session) && session.UndoCount == 0 && JsonSerializer.Serialize(document) == before);
        }
        var (visibleDoc, _, _, _, _, _) = Fixture(200, false);
        check("world-visible nested groups are not moved for offscreen local coordinates", !Move(new(visibleDoc)));

        var (unsafeDoc, unsafeRoot, _, _, _, _) = Fixture(-1_000_000_000, false);
        unsafeDoc.Canvas.Projection = CanvasCalibration.CreateInitial(unsafeDoc.Viewport); unsafeDoc.Canvas.Projection.Enabled = true;
        string unsafeChildren = JsonSerializer.Serialize(unsafeDoc.Elements);
        var affected = CanvasSafetyResolution.AffectedIds(unsafeDoc);
        check("safety counts world-unsafe nested group including retained hidden locked descendants", affected.ToHashSet().SetEquals(unsafeDoc.Elements.Select(e => e.Id)));
        var safetySession = new EditorSession(unsafeDoc); safetySession.SetSelection([unsafeRoot.Id]);
        string safetyBefore = JsonSerializer.Serialize(unsafeDoc);
        bool relocated = false; safetySession.Execute("group safety fixture", _ => relocated = CanvasSafetyResolution.TryRelocate(unsafeDoc, affected));
        check("safety relocation changes only root placement and keeps all descendant local geometry", relocated &&
            JsonSerializer.Serialize(unsafeDoc.Elements) == unsafeChildren && safetySession.Selection.SetEquals([unsafeRoot.Id]) &&
            SceneEvaluation.Build(unsafeDoc).Leaves.All(leaf => CanvasMapping.Create(unsafeDoc).IsElementSafe(leaf.RenderElement)));
        check("world-based safety relocation is one exact undo", safetySession.UndoCount == 1 && safetySession.Undo() && JsonSerializer.Serialize(safetySession.Document) == safetyBefore);

        var safeLocal = new SceneDocument { Viewport = new(0, 0, 1920, 1080) };
        var cancelRoot = new CanvasGroup { Transform = Affine2D.Translation(1_000_000_000, 0) };
        safeLocal.Groups.Add(cancelRoot);
        safeLocal.Elements.Add(new() { GroupId = cancelRoot.Id, Kind = ElementKind.Rectangle, Width = 40, Height = 30,
            Transform = Affine2D.Translation(-999_999_600, 200), Style = new() { StrokeWidth = 0 } });
        safeLocal.Canvas.Projection = CanvasCalibration.CreateInitial(safeLocal.Viewport); safeLocal.Canvas.Projection.Enabled = true;
        check("local coordinates beyond safety range do not warn when parent maps them to safe world space", CanvasSafetyResolution.AffectedIds(safeLocal).Count == 0);
    }

    private static bool Move(EditorSession session) => MoveIntoViewCommand.Execute(session,
        [(new(0, 0, 1920, 1080), new(0, 40, 1920, 1040))], default);

    private static (SceneDocument, CanvasGroup, CanvasGroup, CanvasElement, CanvasElement, CanvasElement) Fixture(double x, bool perspective)
    {
        var document = new SceneDocument { Viewport = new(0, 0, 1920, 1080) };
        var root = new CanvasGroup { Transform = Affine2D.Translation(x, 180).Compose(Affine2D.Rotation(8)), ElevationScale = 2,
            OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var child = new CanvasGroup { ParentId = root.Id, Transform = Affine2D.Translation(30, 20).Compose(Affine2D.Scale(1.1, .8)),
            ElevationScale = 1.5, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var surface = new CanvasElement { Kind = ElementKind.Rectangle, GroupId = child.Id, Width = 80, Height = 60,
            Transform = Affine2D.Translation(10, 10), Style = new() { StrokeWidth = 0 } };
        var board = new CanvasElement { Kind = ElementKind.Ellipse, GroupId = child.Id, Width = 50, Height = 60,
            IsBillboard = true, BillboardAnchor = new(130, 90), BillboardHeight = 30, Locked = true,
            Transform = Affine2D.Rotation(12), Style = new() { StrokeWidth = 0 } };
        var hidden = surface.Clone(true); hidden.GroupId = root.Id; hidden.Visible = false; hidden.Locked = true; hidden.Transform = Affine2D.Translation(400, 20);
        document.Groups = [root, child]; document.Elements = [surface, board, hidden]; document.RootOrder = [root.Id];
        root.ChildOrder = [child.Id, hidden.Id]; child.ChildOrder = [surface.Id, board.Id];
        if (perspective)
        {
            document.Canvas.Projection = CanvasCalibration.CreateInitial(document.Viewport);
            var projection = document.Canvas.Projection; projection.Enabled = true;
            projection.Matrix = new(1, 0, 0, 0, 1, 0, .00003, 0, 1);
            projection.Quad = projection.Quad.Select(projection.Matrix.TransformPoint).ToList();
        }
        return (document, root, child, surface, board, hidden);
    }
}
