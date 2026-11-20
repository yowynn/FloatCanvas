using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class SingleLayerResizeTests
{
    internal static void Run(Action<string, bool> check)
    {
        foreach (var kind in new[] { ElementKind.Number, ElementKind.Image, ElementKind.Freehand, ElementKind.Highlighter,
            ElementKind.Line, ElementKind.Arrow, ElementKind.DoubleArrow, ElementKind.Rectangle, ElementKind.RoundedRectangle,
            ElementKind.Ellipse, ElementKind.Triangle, ElementKind.Symbol, ElementKind.Text, ElementKind.Presentation,
            ElementKind.LiveBlur, ElementKind.LivePixelate })
            Drag(kind.ToString(), kind, check);

        Drag("free resize without Shift", ElementKind.Number, check, locked: false, shift: false);
        Drag("locked ratio", ElementKind.Number, check, shift: false);
        Drag("Shift locks a free layer", ElementKind.Number, check, locked: false);
        Drag("Alt center", ElementKind.Freehand, check, centered: true);
        Drag("top-left handle", ElementKind.Number, check, handle: 0);
        Drag("bottom-left handle", ElementKind.Freehand, check, handle: 3);
        Drag("existing skew and mirrored axes", ElementKind.Image, check, skew: true);
        Drag("child of a transformed group", ElementKind.Number, check, grouped: true);
        Drag("one-leaf group selection", ElementKind.Image, check, grouped: true, selectGroup: true);
        Drag("layer on a parent card", ElementKind.Number, check, grouped: true, card: true, projected: true);
        Drag("billboard on a parent card", ElementKind.Image, check, grouped: true, card: true, billboard: true, projected: true);
        Drag("plane-framed capture", ElementKind.Image, check, capture: true);
        Drag("projected canvas", ElementKind.Number, check, projected: true);
        Drag("projective placement", ElementKind.Number, check, warped: true);
        Drag("projective rectangle", ElementKind.Rectangle, check, warped: true);
        Drag("projective text", ElementKind.Text, check, warped: true);
        Drag("billboard with an image border", ElementKind.Image, check, billboard: true);
        Drag("billboard in perspective", ElementKind.Number, check, billboard: true, projected: true);
        Drag("centered billboard in perspective", ElementKind.Image, check, centered: true, billboard: true, projected: true);
        ModifierAndCancellation(check);
    }

    private static void ModifierAndCancellation(Action<string, bool> check)
    {
        var badge = new CanvasElement { Kind = ElementKind.Number, Width = 100, Height = 70, ConstrainAspect = true,
            Transform = Affine2D.Translation(300, 190).Compose(Affine2D.Rotation(-39)) };
        var session = new EditorSession(new() { Elements = [badge] });
        session.SetSelection([badge.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        var initial = editor.SelectionScreenCorners.ToArray();
        var start = initial[2]; var end = start + new PointD(55, 42);
        var original = badge.Transform;
        void Send(OverlayPointerKind action, PointD point) => editor.Pointer(new(action, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
        Send(OverlayPointerKind.Down, start); Send(OverlayPointerKind.Move, end);
        var locked = editor.SelectionScreenCorners.ToArray();
        editor.ModifiersChanged(OverlayModifiers.Shift, new(end.X, end.Y));
        check("Shift during corner drag unlocks along the original axes and keeps the opposite corner",
            editor.SelectionScreenCorners[2].DistanceTo(end) < 1e-7 && editor.SelectionScreenCorners[0].DistanceTo(initial[0]) < 1e-7);
        editor.ModifiersChanged(OverlayModifiers.None, new(end.X, end.Y));
        check("releasing Shift returns to the same proportional preview", editor.SelectionScreenCorners.Zip(locked)
            .All(pair => pair.First.DistanceTo(pair.Second) < 1e-7));
        Send(OverlayPointerKind.Move, start); Send(OverlayPointerKind.Up, start);
        check("returning the corner to its start is a no-op", session.Document.Elements.Single().Transform == original && session.UndoCount == 0);
        Send(OverlayPointerKind.Down, start); Send(OverlayPointerKind.Move, end); editor.Cancel();
        check("cancel restores the original transform and lock with no undo entry", session.Document.Elements.Single().Transform == original &&
            session.Document.Elements.Single().ConstrainAspect == true && session.UndoCount == 0);
    }

    private static void Drag(string name, ElementKind kind, Action<string, bool> check, bool locked = true, bool shift = true,
        bool centered = false, bool grouped = false, bool selectGroup = false, bool skew = false, bool capture = false,
        bool projected = false, bool warped = false, bool billboard = false, bool card = false, int handle = 1)
    {
        var element = new CanvasElement { Kind = kind, Width = 120, Height = 80, Number = 100, Text = "Label", ConstrainAspect = locked,
            Transform = Affine2D.Translation(340, 240).Compose(Affine2D.Rotation(33)), Style = new() { StrokeWidth = 12, Opacity = .81 } };
        if (ElementGeometry.UsesPoints(kind)) element.Points = [new(-35, 10), new(25, -20), new(115, 60), new(80, 93)];
        if (kind == ElementKind.Image) element.ImageBorderWidth = 12;
        if (kind == ElementKind.Presentation) element.Presentation = new();
        if (skew) element.Transform = element.Transform.Compose(new(-1.2, .26, 0, .8, 0, 0));
        if (capture) element.ImageFrame = [new(.1, .2), new(.8, .12), new(.9, .75), new(.3, .88)];
        if (warped || capture) element.ProjectivePlacement = new(1, .07, 0, -.04, 1, 0, .0005, .0003, 1);
        if (billboard)
        {
            element.IsBillboard = true; element.BillboardAnchor = new(390, 330); element.BillboardHeight = 40;
            element.Transform = Affine2D.Rotation(33);
        }
        var document = new SceneDocument { Viewport = new(0, 0, 1200, 900), Elements = [element] };
        if (projected)
        {
            document.Canvas.Projection.Enabled = true;
            var projection = new Projective2D(1, .08, 8, .12, 1, 12, .0002, .0001, 1);
            document.Canvas.Projection.Matrix = projection;
            document.Canvas.Projection.Quad = EditorController.Corners(document.Viewport).Select(projection.TransformPoint).ToList();
        }
        if (grouped)
        {
            var group = new CanvasGroup { ChildOrder = [element.Id], OrderMode = GroupOrderMode.AtomicBlock, Opacity = .55,
                Transform = Affine2D.Translation(70, 30).Compose(Affine2D.Rotation(-18)).Compose(Affine2D.Scale(1.12, .83)) };
            if (card) group.Billboard = new ContainerBillboard(new(220, 190), 20);
            element.GroupId = group.Id; document.Groups = [group]; document.RootOrder = [group.Id];
        }
        var session = new EditorSession(document);
        session.SetSelection([selectGroup ? element.GroupId!.Value : element.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        var initial = editor.SelectionScreenCorners.ToArray();
        var world = session.SelectedWorldElements().Single();
        var mapping = editor.CurrentCanvasMapping;
        var frameToScreen = capture ? mapping.PlaneToScreen.Compose(Projective2D.FromAffine(world.Transform)) : mapping.ElementToScreen(world);
        if (capture)
        {
            var unplaced = world.Clone(); unplaced.Transform = Affine2D.Identity;
            var expectedFrame = EditorController.Corners(ElementGeometry.Bounds(unplaced)).Select(frameToScreen.TransformPoint);
            check("captured image retains its tight contour in the rotated selection axes",
                expectedFrame.Zip(initial).All(pair => pair.First.DistanceTo(pair.Second) < 1e-5));
        }
        var frame = initial.Select(frameToScreen.Inverse().TransformPoint).ToArray();
        int opposite = (handle + 2) % 4;
        var pivot = centered ? (frame[0] + frame[2]) * .5 : frame[opposite];
        var vector = frame[handle] - pivot;
        var free = new PointD(vector.X * 1.53, vector.Y * .72);
        double uniform = (free.X * vector.X + free.Y * vector.Y) / (vector.X * vector.X + vector.Y * vector.Y);
        var scale = locked ^ shift ? Affine2D.Scale(uniform, uniform) : Affine2D.Scale(1.53, .72);
        var expected = frame.Select(Affine2D.Around(pivot, scale).TransformPoint).Select(frameToScreen.TransformPoint).ToArray();
        var end = frameToScreen.TransformPoint(pivot + free);
        var mods = (shift ? OverlayModifiers.Shift : OverlayModifiers.None) | (centered ? OverlayModifiers.Alt : OverlayModifiers.None);
        void Send(OverlayPointerKind action, PointD at) =>
            editor.Pointer(new(action, new(at.X, at.Y), OverlayMouseButton.Left, mods));
        bool Matches(IReadOnlyList<PointD> corners) => corners.Count == 4 &&
            corners.Zip(expected).All(pair => pair.First.DistanceTo(pair.Second) < 1e-5);
        var begin = initial[handle];
        int history = session.UndoCount;
        Send(OverlayPointerKind.Down, begin);
        Send(OverlayPointerKind.Move, begin + (end - begin) * .4);
        Send(OverlayPointerKind.Move, end);
        check(name + " previews both dimensions on the displayed frame axes", Matches(editor.SelectionScreenCorners));
        Send(OverlayPointerKind.Up, end);
        check(name + " commits the preview once and retains the lock preference", Matches(editor.SelectionScreenCorners) &&
            session.UndoCount == history + 1 && session.Document.Elements.Single().ConstrainAspect == locked && session.Document.Elements.Single().Style.Opacity == .81);
        check(name + " undo restores the original frame", session.Undo() &&
            editor.SelectionScreenCorners.Zip(initial).All(pair => pair.First.DistanceTo(pair.Second) < 1e-5));
    }
}
