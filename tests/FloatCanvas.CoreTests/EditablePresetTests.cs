using System.Text.Json;
using FloatCanvas.Core;

internal static class EditablePresetTests
{
    public static void Run(Action<bool, string> check)
    {
        check(new SceneDocument().Version == SceneDocument.CurrentVersion && new SceneDocument().Clone().Version == SceneDocument.CurrentVersion,
            "new editable documents explicitly require the current reader contract");
        var line = new CanvasElement { Kind = ElementKind.Line, Points = [new(0, 0), new(100, 0)],
            Style = new() { StrokeWidth = 2, ArrowHeadSize = 30, StartArrowHead = ArrowHeadStyle.Open, EndArrowHead = ArrowHeadStyle.Diamond } };
        var heads = ElementGeometry.GetStyledArrowHeads(line);
        check(heads.Count == 2 && heads[0].Style == ArrowHeadStyle.Diamond && heads[1].Style == ArrowHeadStyle.Open,
            "unified line independently resolves both endpoint styles");
        check(ElementGeometry.HitTest(line, new(85, 10), 0) && !ElementGeometry.HitTest(line, new(25, 8), 0),
            "mixed heads retain filled diamond and hollow open geometry");
        line.Style.EndArrowHead = ArrowHeadStyle.None;
        check(ElementGeometry.GetStyledArrowHeads(line).Count == 1 && !ElementGeometry.HitTest(line, new(85, 10), 0), "explicit none suppresses only its endpoint");
        line.Style.StartArrowHead = ArrowHeadStyle.None; line.Style.StrokePattern = StrokePattern.Dashed;
        check(ElementGeometry.HitTest(line, new(4.75, 0), 0) && !ElementGeometry.HitTest(line, new(6, 0), 0) &&
            !ElementGeometry.HitTest(line, new(4.85, .85), 0), "dashed exact hit respects round caps and one-width gaps");
        line.Style.StrokePattern = StrokePattern.Dotted;
        check(ElementGeometry.HitTest(line, new(4, 0), 0) && !ElementGeometry.HitTest(line, new(2, 0), 0), "dotted exact hit respects round dots and one-width gaps");

        line.Style.StrokePattern = StrokePattern.Solid;
        line.Style.LineArcBulge = .5;
        var arc = ElementGeometry.LineShaftPoints(line);
        check(arc.Count > 2 && arc[0] == new PointD(0, 0) && arc[^1] == new PointD(100, 0) &&
            arc.All(point => Math.Abs(point.DistanceTo(new(50, 0)) - 50) < 1e-8), "semicircle preserves both endpoints and its radius");
        check(ElementGeometry.LineArcHandle(line) == new PointD(50, 50) &&
            ElementGeometry.LineArcCoordinatesAt(line, new(85, -30), centered: true) == (0, -.3) &&
            ElementGeometry.HitTest(line, new(50, 50), 0) && !ElementGeometry.HitTest(line, new(50, 0), 0),
            "arc handle edits the perpendicular midpoint and hit-testing follows its visible curve");
        line.Style.EndArrowHead = ArrowHeadStyle.Open;
        var arcHead = ElementGeometry.ArrowHead(line);
        check(((arcHead[0] + arcHead[2]) * .5).DistanceTo(new(100, 30)) < 1e-8,
            "an arc arrowhead points along the end tangent");
        line.Style.EndArrowHead = ArrowHeadStyle.Diamond;
        line.Style.StrokeWidth = 8;
        line.Style.ArrowHeadSize = 0;
        check(ElementGeometry.PointInPolygon(ElementGeometry.LineShaftPoints(line)[^1], ElementGeometry.ArrowHead(line)),
            "a tight arc reaches inside its diamond head instead of separating at the tangent");

        line.Style.EndArrowHead = ArrowHeadStyle.None;
        HalfEllipse(check);
        line.Style.LineArcBulge = 0;
        line.Style.LineArcOffset = 1;
        check(ElementGeometry.LineShaftPoints(line).SequenceEqual(new[] { new PointD(0, 0), new PointD(150, 0), new PointD(100, 0) }),
            "a collinear handle past the endpoints remains reachable with a single reversal");

        var rectangle = new CanvasElement { Kind = ElementKind.RoundedRectangle, Width = 100, Height = 60 };
        check(ElementGeometry.CornerRadius(rectangle) == 12 && ElementGeometry.CornerRadius(rectangle.Clone()) == 12, "legacy rounded rectangle keeps twenty percent radius");
        rectangle.Kind = ElementKind.Rectangle; rectangle.CornerRadius = 30; rectangle.Style.Fill = 0xFF2244FF;
        check(!ElementGeometry.HitTest(rectangle, new(1, 1), 0) && ElementGeometry.HitTest(rectangle, new(50, 30), 0), "custom radius shares real rounded hit contour");
        rectangle.CornerRadius = 1000;
        check(ElementGeometry.CornerRadius(rectangle) == 30, "corner radius clamps geometrically without mutating requested radius");
        foreach (var symbol in SymbolGeometry.BuiltIns)
        {
            var element = new CanvasElement { Kind = ElementKind.Symbol, Symbol = symbol, Width = 120, Height = 100, Style = new() { Fill = 0xFF44AA88 } };
            var outline = ElementGeometry.Outline(element);
            check(outline.Count >= 3 && outline.All(p => p.X >= 0 && p.X <= 120 && p.Y >= 0 && p.Y <= 100), symbol + " has finite bounded vector outline");
            var copy = element.Clone();
            check(copy.Symbol == symbol && !ReferenceEquals(copy.Style, element.Style), symbol + " remains an independently editable symbol");
        }
        var triangle = SymbolGeometry.Outline(SymbolKind.Triangle, 120, 100);
        check(Math.Abs(triangle[1].X - triangle[2].X - 120) < 1e-8 && Math.Abs(triangle[1].Y - triangle[0].Y - 100 * Math.Sqrt(3) / 2) < 1e-8,
            "symbol triangle follows both requested dimensions inside non-square bounds");

        var session = new EditorSession();
        var text = new CanvasElement { Kind = ElementKind.Text, Text = "可编辑矢量", Width = 90, Height = 30, Transform = Affine2D.Translation(20, 30) };
        var box = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 70, Transform = Affine2D.Translation(10, 20) };
        var outside = new CanvasElement { Kind = ElementKind.Symbol, Symbol = SymbolKind.Star, Width = 50, Height = 50, Transform = Affine2D.Translation(200, 30) };
        session.Add(box); session.Add(text); session.SetSelection([box.Id, text.Id]);
        int beforeGroup = session.UndoCount; Guid group = session.GroupSelection("label")!.Value;
        check(session.Document.Groups.Count == 1 && session.Document.Elements.All(e => e.GroupId == group) && session.UndoCount == beforeGroup + 1, "group creates one logical vector transaction");
        session.SelectAt(new(20, 40));
        check(session.Selection.SetEquals([group]) && session.SelectedElements().Count() == 2, "normal hit selects the group node and expands members explicitly");
        session.SelectAt(new(20, 40), individual: true);
        check(session.Selection.SetEquals([text.Id]), "individual hit selects editable group member");
        session.UpdateSelection(e => e.Text = "单独编辑");
        check(session.Document.Elements.Single(e => e.Id == text.Id).Text == "单独编辑" && session.Document.Groups.Count == 1, "member edit preserves surrounding group");
        session.Add(outside); session.SetSelection([group, outside.Id]);
        Guid outer = session.GroupSelection("outer")!.Value;
        check(session.Document.Groups.Single(g => g.Id == group).ParentId == outer && session.SelectionUnitIds(text.Id).SequenceEqual([outer]), "nested group retains inner identity and resolves the outer node as its hit unit");
        var originalTransforms = SceneEvaluation.Build(session.Document).RenderDocument.Elements.ToDictionary(e => e.Id, e => e.Transform);
        var transform = Affine2D.Translation(25, 10).Compose(Affine2D.Rotation(20)).Compose(Affine2D.Scale(2, 2));
        session.TransformSelection(transform);
        check(SceneEvaluation.Build(session.Document).RenderDocument.Elements.All(e => e.Transform == transform.Compose(originalTransforms[e.Id])) &&
            session.Document.Elements.All(e => e.Transform == originalTransforms[e.Id]) && session.Document.Groups.Single(g => g.Id == outer).Transform == transform &&
            session.Document.Elements.Single(e => e.Id == text.Id).Kind == ElementKind.Text,
            "whole nested group changes its local parent placement while vector child geometry stays editable and unbaked");
        session.Undo();
        check(SceneEvaluation.Build(session.Document).RenderDocument.Elements.All(e => e.Transform == originalTransforms[e.Id]), "whole group transform undo restores every world matrix");
        session.UngroupSelection();
        check(session.Document.Groups.Count == 1 && session.Document.Groups[0].ParentId is null && session.Document.Elements.Single(e => e.Id == outside.Id).GroupId is null, "ungroup removes one outer level and preserves child grouping");
        session.Undo();
        check(session.Document.Groups.Count == 2, "ungroup is undoable");
        session.DuplicateSelection();
        var duplicate = session.SelectedElements().ToArray();
        check(duplicate.Length == 3 && session.Document.Groups.Count == 4 && duplicate.All(e => !originalTransforms.ContainsKey(e.Id)) && !duplicate.Select(e => e.GroupId).Intersect(new Guid?[] { group, outer }).Any(), "duplicate remaps all group and entity identities");
        session.DeleteSelection();
        check(session.Document.Groups.Count == 2 && session.Document.Elements.Count == 3, "deleting a duplicated group prunes only now-empty metadata");
        session.Undo(); session.Redo();
        check(session.Document.Groups.Count == 2, "delete undo redo maintains valid grouping");
        session.SetSelection([outer]);
        var snapshot = session.CreateSelectionSnapshot();
        check(snapshot.Viewport.X == 0 && snapshot.Viewport.Y == 0 && snapshot.Groups.Count == 2 && snapshot.Elements.Single(e => e.Id == text.Id).Text == "单独编辑", "normalized stamp snapshot preserves nested editable text");
        var independent = snapshot.Clone(true);
        check(!independent.Groups.Select(g => g.Id).Intersect(snapshot.Groups.Select(g => g.Id)).Any() && independent.Elements.All(e => e.GroupId is null || independent.Groups.Any(g => g.Id == e.GroupId)), "generic clone remaps complete hierarchy coherently");
        var beforeCancel = JsonSerializer.Serialize(session.Document);
        session.BeginTransaction("cancel grouping mutation"); session.Document.Groups.Clear(); session.Document.Elements[0].CornerRadius = 18;
        session.Document.Elements[0].Style.StrokePattern = StrokePattern.Dotted; session.Document.Elements[0].Style.StartArrowHead = ArrowHeadStyle.Diamond; session.Cancel();
        check(JsonSerializer.Serialize(session.Document) == beforeCancel, "transaction rollback restores group records and new geometry properties");

        var assetId = session.Assets.Add([1, 2, 3]);
        var picture = new CanvasElement { Kind = ElementKind.Image, AssetId = assetId, Width = 50, Height = 40, ImageCrop = new(.1, .2, .7, .6),
            Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(.2, .2, .3, .3) }], Transform = Affine2D.Rotation(35) };
        session.Add(picture); session.SetSelection([group, picture.Id]);
        var imageSnapshot = session.CreateSelectionSnapshot();
        var destination = new EditorSession();
        destination.InsertDocument(imageSnapshot, session.Assets, Affine2D.Scale(3, 3), group: true);
        var imported = destination.Document.Elements.Single(e => e.Kind == ElementKind.Image);
        check(imported.Id != picture.Id && imported.Effects[0].Id != picture.Effects[0].Id && imported.ImageCrop == picture.ImageCrop && destination.Assets.Get(imported.AssetId!).Data.Span.SequenceEqual(new byte[] { 1, 2, 3 }), "stamp insertion remaps effect/entity IDs and keeps original resource bytes and crop");
        check(destination.Document.Elements.Single(e => e.Kind == ElementKind.Text).Text == "单独编辑" && imported.Transform == Affine2D.Scale(3, 3).Compose(imageSnapshot.Elements.Single(e => e.Id == picture.Id).Transform), "stamp insertion transforms vectors without resampling media");
        check(destination.SelectionUnitIds(imported.Id).Count() == 1 && destination.SelectedElements().Count() == 3, "ungrouped selection inserts as one editable stamp node with three explicit member leaves");
        check(destination.UndoCount == 1 && destination.Undo() && destination.Document.Elements.Count == 0 && destination.Document.Groups.Count == 0 && destination.Redo(), "stamp insertion is one undoable transaction including groups");
        int beforeFailedInsert = destination.Document.Elements.Count;
        bool rejected = false;
        try { destination.InsertDocument(imageSnapshot, new(), Affine2D.Identity); } catch (KeyNotFoundException) { rejected = true; }
        check(rejected && destination.Document.Elements.Count == beforeFailedInsert && !destination.IsTransactionActive, "missing stamp assets cannot partially overwrite a receiving document");
    }

    private static void HalfEllipse(Action<bool, string> check)
    {
        var line = new CanvasElement { Kind = ElementKind.Line, Points = [new(43, 68), new(316, 68)] };
        void MoveHandle(PointD p) =>
            (line.Style.LineArcOffset, line.Style.LineArcBulge) = ElementGeometry.LineArcCoordinatesAt(line, p, centered: false);

        // Reference positions from the draggable half-ellipse preview.
        MoveHandle(new(386, 123));
        var points = ElementGeometry.LineShaftPoints(line).ToList();
        int handleIndex = points.FindIndex(p => p.DistanceTo(new(386, 123)) < 1e-9);
        check(handleIndex > 0 && points.Skip(handleIndex).Max(p => p.X) is > 397.20 and < 397.26 &&
            points.All(p => p.Y >= 68), "beyond B, the return passes the handle on its right and stays on its side of AB");
        check(points.All(p =>
        {
            double v = (p.Y - 68) / 58.64930569026503;
            double u = (p.X - 179.5) / 136.5 - 1.2429214617781612 * v;
            return Math.Abs(u * u + v * v - 1) < 1e-9;
        }), "both sides of the handle lie on the same preview ellipse centered at the chord midpoint");
        var bounds = ElementGeometry.LocalBounds(line);
        check(Math.Abs(bounds.Right - 397.25295973311534) < 1e-8 && Math.Abs(bounds.Bottom - 126.64930569026503) < 1e-8 &&
            ElementGeometry.HitTest(line, new(397, 114), 0), "the returning tip is included in exact bounds and hit testing");

        var rotated = line.Clone();
        var placement = Affine2D.Translation(100, -40).Compose(Affine2D.Rotation(37));
        rotated.Points = new(line.Points.Select(placement.TransformPoint));
        var rotatedPath = ElementGeometry.LineShaftPoints(rotated);
        check(rotatedPath.Count == points.Count && points.Select(placement.TransformPoint)
            .Zip(rotatedPath).All(pair => pair.First.DistanceTo(pair.Second) < 1e-8),
            "rotating the chord rotates the entire half ellipse, including the returning tip");

        foreach (int side in new[] { -1, 1 })
        {
            double previous = double.PositiveInfinity;
            foreach (double offset in new[] { 136.5, 206.5, 256.5, 406.5 })
            {
                double x = 179.5 + side * offset;
                MoveHandle(new(x, 123));
                bounds = ElementGeometry.LocalBounds(line);
                double over = side < 0 ? x - bounds.Left : bounds.Right - x;
                check(over > 0 && over < previous, "the lateral return narrows continuously beyond either endpoint");
                previous = over;
            }
            foreach (double dy in new[] { -.001, 0, .001 })
            {
                double x = 179.5 + side * 206.5;
                MoveHandle(new(x, 68 + dy));
                var nearAxis = ElementGeometry.LineShaftPoints(line);
                check(nearAxis.All(p => Math.Abs(p.Y - 68) < .002) &&
                    nearAxis.Any(p => p.DistanceTo(new(x, 68 + dy)) < 1e-9) &&
                    nearAxis.All(p => side * (p.X - x) < .001),
                    "crossing either extension collapses onto the collinear return at the handle");
            }
        }
    }
}
