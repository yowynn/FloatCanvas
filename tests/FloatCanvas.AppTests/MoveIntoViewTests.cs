using System.Text.Json;
using System.IO;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class MoveIntoViewTests
{
    private static readonly (RectD Bounds, RectD WorkArea)[] Single = [(new(0, 0, 1920, 1080), new(0, 40, 1920, 1040))];
    internal static void Run(Action<string, bool> check)
    {
        var visible = Session(Box(1100, 600));
        string before = JsonSerializer.Serialize(visible.Document);
        check("already visible content must not move or create undo history", !Execute(visible) && visible.UndoCount == 0 && JsonSerializer.Serialize(visible.Document) == before);
        foreach (var point in new[] { new PointD(-40, 100), new(1900, 100), new(100, -30), new(100, 1060), new(100, 5) })
        {
            var partial = Session(Box(point.X, point.Y)); string original = JsonSerializer.Serialize(partial.Document);
            check($"partially visible or taskbar-area content stays unchanged at {point}", !Execute(partial) && partial.UndoCount == 0 && JsonSerializer.Serialize(partial.Document) == original);
        }
        foreach (var sample in new[] { (new PointD(-100, 100), new PointD(0, 100)), (new PointD(2000, 100), new PointD(1840, 100)),
            (new PointD(100, -100), new PointD(100, 40)), (new PointD(100, 1200), new PointD(100, 1020)), (new PointD(-80, 100), new PointD(0, 100)) })
        {
            var session = Session(Box(sample.Item1.X, sample.Item1.Y));
            check($"fully offscreen object moves by the nearest displacement from {sample.Item1}", Execute(session) && ElementGeometry.Bounds(session.Document.Elements[0]).Center == sample.Item2 + new PointD(40, 30));
        }
        var onScreen = Box(400, 100); var left = Box(-100, 200); var right = Box(2100, 400);
        var hidden = Box(3000, 100); hidden.Visible = false; var locked = Box(-2000, 200); locked.Locked = true;
        var mixed = Session(onScreen, left, right, hidden, locked);
        mixed.SetSelection([onScreen.Id, left.Id]);
        string mixedBefore = JsonSerializer.Serialize(mixed.Document);
        var selection = mixed.Selection.ToArray();
        check("independent offscreen objects recover to their own nearest edges in one operation", Execute(mixed) && left.Transform.OffsetX == 0 && right.Transform.OffsetX == 1840 && mixed.UndoCount == 1);
        check("recovery preserves visible, hidden, locked layers and selection", onScreen.Transform.OffsetX == 400 && hidden.Transform.OffsetX == 3000 && locked.Transform.OffsetX == -2000 && mixed.Selection.SetEquals(selection));
        check("a repeated recovery is a no-op", !Execute(mixed) && mixed.UndoCount == 1);
        check("one undo restores the complete prior document", mixed.Undo() && JsonSerializer.Serialize(mixed.Document) == mixedBefore);
        var lockedOnly = Session(locked.Clone(), hidden.Clone());
        check("only hidden or locked layers create no operation", !Execute(lockedOnly) && lockedOnly.UndoCount == 0);

        (RectD Bounds, RectD WorkArea)[] pair = [(new(-1000, 0, 800, 600), new(-1000, 0, 800, 560)), (new(0, 0, 1000, 800), new(0, 40, 1000, 760))];
        var gapLeft = Session(Box(-180, 200)); gapLeft.Document.Viewport = new(-1000, 0, 2000, 800);
        check("a screen-gap object uses the nearer negative-coordinate display", Execute(gapLeft, pair) && ElementGeometry.Bounds(gapLeft.Document.Elements[0]).Right == -200);
        var gapRight = Session(Box(-80, 200)); gapRight.Document.Viewport = new(-1000, 0, 2000, 800);
        check("a screen-gap object may instead use the nearer right display", Execute(gapRight, pair) && ElementGeometry.Bounds(gapRight.Document.Elements[0]).Left == 0);
        var secondary = Session(Box(-900, 200)); secondary.Document.Viewport = new(-1000, 0, 2000, 800);
        check("content visible on a secondary screen is never pulled to primary", !Execute(secondary, pair) && secondary.UndoCount == 0);
        var primaryScope = Session(Box(-900, 200));
        check("a display outside the active scope is not treated as available", Execute(primaryScope) && ElementGeometry.Bounds(primaryScope.Document.Elements[0]).Left == 0);
        (RectD Bounds, RectD WorkArea)[] stacked = [(new(-200, -900, 1200, 800), new(-200, -900, 1200, 800)), (new(0, 0, 1000, 800), new(0, 40, 1000, 760))];
        var gapAbove = Session(Box(200, -80, 80, 20)); gapAbove.Document.Viewport = new(-200, -900, 1200, 1700);
        check("negative vertical coordinates choose the nearer upper display", Execute(gapAbove, stacked) && ElementGeometry.Bounds(gapAbove.Document.Elements[0]).Bottom == -100);

        var groupA = Box(2100, 100); var groupB = Box(2250, 150); var group = Session(groupA, groupB); Group(group, groupA, groupB);
        var groupBefore = JsonSerializer.Serialize(group.Document);
        check("a wholly offscreen group keeps its spacing and group identity", Execute(group) && groupB.Transform.OffsetX - groupA.Transform.OffsetX == 150 && groupB.Transform.OffsetY - groupA.Transform.OffsetY == 50 && group.Document.Groups.Count == 1);
        check("offscreen group recovery is one undoable step", group.UndoCount == 1 && group.Undo() && JsonSerializer.Serialize(group.Document) == groupBefore);
        var reachableA = Box(1900, 100); var distantB = Box(2200, 100); var reachable = Session(reachableA, distantB); Group(reachable, reachableA, distantB);
        check("a group with any visible editable member remains untouched", !Execute(reachable) && distantB.Transform.OffsetX == 2200 && reachable.UndoCount == 0);
        var spanA = Box(-200, 100); var spanB = Box(2200, 100); var spanning = Session(spanA, spanB); Group(spanning, spanA, spanB);
        check("an oversized group with no visible member becomes reachable without collapsing spacing", Execute(spanning) && ElementGeometry.Bounds(spanA).Left == 0 && spanB.Transform.OffsetX - spanA.Transform.OffsetX == 2400);

        var oversized = Session(Box(3000, 100, 3000, 200));
        check("oversized objects retain size with an accessible visible portion", Execute(oversized) && ElementGeometry.Bounds(oversized.Document.Elements[0]).Intersect(Single[0].WorkArea).Width >= 32 && oversized.Document.Elements[0].Width == 3000 && oversized.Document.Elements[0].Transform.M11 == 1);
        var empty = Session(Box(3000, 100, 0, 0));
        check("empty geometry does not create useless recovery history", !Execute(empty) && empty.UndoCount == 0);
        var noScreens = Session(Box(3000, 100));
        check("no available display is a no-op", !Execute(noScreens, []) && noScreens.UndoCount == 0);
        var busy = Session(Box(3000, 100)); busy.BeginTransaction("fixture pending");
        check("an active editing transaction is never replaced by recovery", !Execute(busy) && busy.IsTransactionActive && busy.UndoCount == 0); busy.Cancel();

        foreach (var origin in new[] { new PointD(0, 0), new(-800, -300) })
        {
            var frozen = Session(Box(1800, 600)); frozen.Document.Kind = DocumentKind.FrozenCapture; frozen.Document.Viewport = new(1000, 500, 600, 400);
            frozen.Document.Background = new() { Kind = ElementKind.Image, Width = 1920, Height = 1080, Locked = true };
            string original = JsonSerializer.Serialize(frozen.Document);
            string background = JsonSerializer.Serialize(frozen.Document.Background);
            check($"frozen crop and origin {origin} constrain recovered annotations", Execute(frozen, Single, origin) && ElementGeometry.Bounds(frozen.Document.Elements[0]).Right == 1600 && ElementGeometry.Bounds(frozen.Document.Elements[0]).Top == 600);
            check($"frozen recovery leaves crop and background untouched at {origin}", frozen.Document.Viewport == new RectD(1000, 500, 600, 400) && JsonSerializer.Serialize(frozen.Document.Background) == background);
            check($"frozen recovery undoes completely at {origin}", frozen.UndoCount == 1 && frozen.Undo() && JsonSerializer.Serialize(frozen.Document) == original);
        }
        var outsideCrop = Session(Box(3100, 2100)); outsideCrop.Document.Kind = DocumentKind.FrozenCapture; outsideCrop.Document.Viewport = new(3000, 2000, 600, 400);
        check("a fully invisible frozen crop is not silently moved or expanded", !Execute(outsideCrop) && outsideCrop.UndoCount == 0);

        var perspective = Session(Box(20000, 1000)); SetProjection(perspective, new(1, 0, 0, 0, 1, 0, .0003, 0, 1));
        var element = perspective.Document.Elements[0]; var mapping = CanvasMapping.Create(perspective.Document);
        PointD[] corners = [new(0, 0), new(80, 0), new(80, 60), new(0, 60)];
        var projectedBefore = corners.Select(mapping.ElementToScreen(element).TransformPoint).ToArray();
        string perspectiveBefore = JsonSerializer.Serialize(perspective.Document);
        check("perspective recovery brings a projected offscreen object into view", Execute(perspective) && mapping.ProjectedBounds(element).Right <= 1920 + .00001 && mapping.ProjectedBounds(element).Intersect(Single[0].WorkArea).Width > 0);
        var projectedAfter = corners.Select(mapping.ElementToScreen(element).TransformPoint).ToArray(); var shift = projectedAfter[0] - projectedBefore[0];
        check("perspective recovery is a uniform screen translation without visual distortion", projectedAfter.Select((point, i) => (point - projectedBefore[i]).DistanceTo(shift)).All(error => error < .00001) && element.Width == 80 && element.Height == 60);
        string projectPath = Path.Combine(Path.GetTempPath(), "FloatCanvas-recovered-perspective-" + Guid.NewGuid().ToString("N") + ".floatcanvas");
        var project = new ProjectStore();
        project.SaveAsync(projectPath, perspective.Document, perspective.Assets).GetAwaiter().GetResult();
        var loaded = project.LoadAsync(projectPath).GetAwaiter().GetResult();
        check("recovered perspective remains valid in a real saved project", JsonSerializer.Serialize(loaded.Document) == JsonSerializer.Serialize(perspective.Document));
        File.Delete(projectPath);
        check("perspective placement and undo remain exact", perspective.Undo() && JsonSerializer.Serialize(perspective.Document) == perspectiveBefore);
        var placed = Box(2300, 100); placed.ProjectivePlacement = new(1, 0, 0, 0, 1, 0, .001, 0, 1);
        var oldPlacement = placed.ProjectivePlacement;
        check("existing projective placement is retained during ordinary recovery", Execute(Session(placed)) && placed.ProjectivePlacement == oldPlacement);
        var projectedVisible = Session(Box(2500, 200)); SetProjection(projectedVisible, Projective2D.FromAffine(Affine2D.Scale(.5, .5)));
        check("visibility uses projected coordinates rather than offscreen plane coordinates", !Execute(projectedVisible) && projectedVisible.UndoCount == 0);
        var projectedOutside = Session(Box(1100, 100)); SetProjection(projectedOutside, Projective2D.FromAffine(Affine2D.Scale(2, 2)));
        check("plane-visible content projected outside the display is recovered", Execute(projectedOutside) && Math.Abs(CanvasMapping.Create(projectedOutside.Document).ProjectedBounds(projectedOutside.Document.Elements[0]).Right - 1920) < .00001);
        var stroke = Session(new CanvasElement { Kind = ElementKind.Line, Points = [new(-1, 100), new(-1, 200)], Style = new() { StrokeWidth = 4 } });
        check("a visible stroke edge prevents moving an otherwise offscreen path", !Execute(stroke) && stroke.UndoCount == 0);
        var behind = Session(Box(-4000, 1000)); SetProjection(behind, new(1, 0, 0, 0, 1, 0, .0003, 0, 1));
        check("finite content behind the projection horizon can be recovered", Execute(behind) && !CanvasMapping.Create(behind.Document).ProjectedBounds(behind.Document.Elements[0]).Intersect(Single[0].WorkArea).IsEmpty);
        BillboardRecovery(check);
    }

    private static void BillboardRecovery(Action<string,bool> check)
    {
        foreach(bool perspective in new[]{false,true})
        {
            var billboard=Box(0,0);billboard.IsBillboard=true;billboard.BillboardAnchor=new(20000,1000);billboard.BillboardHeight=80;
            billboard.Transform=Affine2D.Rotation(30);billboard.Style.StrokeWidth=5;
            var session=Session(billboard);
            if(perspective)SetProjection(session,new(1,0,0,0,1,0,.0003,0,1));
            string before=JsonSerializer.Serialize(session.Document);var transform=billboard.Transform;
            check($"billboard offscreen recovery moves the ground anchor with perspective={perspective}",Execute(session)&&!CanvasMapping.Create(session.Document).ProjectedBounds(billboard).Intersect(Single[0].WorkArea).IsEmpty);
            check($"billboard recovery preserves logical geometry elevation angle and stroke with perspective={perspective}",billboard.Width==80&&billboard.Height==60&&billboard.BillboardHeight==80&&billboard.Style.StrokeWidth==5&&billboard.Transform==transform&&billboard.ProjectivePlacement is null&&billboard.BillboardAnchor!=new PointD(20000,1000));
            check($"billboard recovery repeats without drift with perspective={perspective}",!Execute(session)&&session.UndoCount==1);
            check($"billboard recovery undoes all placement metadata with perspective={perspective}",session.Undo()&&JsonSerializer.Serialize(session.Document)==before);
        }
        var hidden=Box(0,0);hidden.IsBillboard=true;hidden.BillboardAnchor=new(-4000,1000);hidden.BillboardHeight=30;
        var behind=Session(hidden);SetProjection(behind,new(1,0,0,0,1,0,.0003,0,1));
        check("billboard behind the perspective horizon recovers through its ground anchor",Execute(behind)&&!CanvasMapping.Create(behind.Document).ProjectedBounds(hidden).Intersect(Single[0].WorkArea).IsEmpty&&hidden.BillboardHeight==30);
        var surface=Box(2100,100);var board=Box(0,0);board.IsBillboard=true;board.BillboardAnchor=new(2300,300);board.BillboardHeight=50;
        var mixed=Session(surface,board);Group(mixed,surface,board);string mixedBefore=JsonSerializer.Serialize(mixed.Document);
        check("mixed recovery preserves both placement kinds and their logical metadata",Execute(mixed)&&!surface.IsBillboard&&board.IsBillboard&&board.BillboardHeight==50&&board.Width==80&&board.Height==60&&mixed.Document.Groups.Count==1);
        check("mixed billboard recovery is one undoable operation",mixed.UndoCount==1&&mixed.Undo()&&JsonSerializer.Serialize(mixed.Document)==mixedBefore);
    }

    private static void SetProjection(EditorSession session, Projective2D matrix)
    {
        var projection = CanvasCalibration.CreateInitial(session.Document.Viewport);
        projection.Enabled = true; projection.Quad = projection.Quad.Select(matrix.TransformPoint).ToList(); projection.Matrix = matrix;
        session.Document.Canvas.Projection = projection;
    }
    private static void Group(EditorSession session, params CanvasElement[] elements)
    {
        Guid id = Guid.NewGuid(); session.Document.Groups.Add(new() { Id = id });
        foreach (var element in elements) element.GroupId = id;
    }
    private static EditorSession Session(params CanvasElement[] elements) => new(new SceneDocument { Viewport = new(0, 0, 1920, 1080), Elements = elements.ToList() });
    private static CanvasElement Box(double x, double y, double width = 80, double height = 60) => new() { Kind = ElementKind.Rectangle, Width = width, Height = height,
        Transform = Affine2D.Translation(x, y), Style = new() { Fill = 0xFF2383E2, StrokeWidth = 0 } };
    private static bool Execute(EditorSession session, (RectD Bounds, RectD WorkArea)[]? screens = null, PointD origin = default) =>
        MoveIntoViewCommand.Execute(session, screens ?? Single, origin);
}
