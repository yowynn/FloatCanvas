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
        check(ElementGeometry.HitTest(line, new(4, 0), 0) && !ElementGeometry.HitTest(line, new(11, 0), 0), "dashed exact hit respects ink and gaps");
        line.Style.StrokePattern = StrokePattern.Dotted;
        check(ElementGeometry.HitTest(line, new(5, 0), 0) && !ElementGeometry.HitTest(line, new(2.5, 0), 0), "dotted exact hit respects round dots and gaps");

        var rectangle = new CanvasElement { Kind = ElementKind.RoundedRectangle, Width = 100, Height = 60 };
        check(ElementGeometry.CornerRadius(rectangle) == 12 && ElementGeometry.CornerRadius(rectangle.Clone()) == 12, "legacy rounded rectangle keeps twenty percent radius");
        rectangle.Kind = ElementKind.Rectangle; rectangle.CornerRadius = 30; rectangle.Style.Fill = 0xFF2244FF;
        check(!ElementGeometry.HitTest(rectangle, new(1, 1), 0) && ElementGeometry.HitTest(rectangle, new(50, 30), 0), "custom radius shares real rounded hit contour");
        rectangle.CornerRadius = 1000;
        check(ElementGeometry.CornerRadius(rectangle) == 30, "corner radius clamps geometrically without mutating requested radius");
        foreach (var symbol in Enum.GetValues<SymbolKind>())
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
        check(session.Selection.SetEquals([box.Id, text.Id]), "normal hit selects complete group");
        session.SelectAt(new(20, 40), individual: true);
        check(session.Selection.SetEquals([text.Id]), "individual hit selects editable group member");
        session.UpdateSelection(e => e.Text = "单独编辑");
        check(session.Document.Elements.Single(e => e.Id == text.Id).Text == "单独编辑" && session.Document.Groups.Count == 1, "member edit preserves surrounding group");
        session.Add(outside); session.SetSelection([box.Id, text.Id, outside.Id]);
        Guid outer = session.GroupSelection("outer")!.Value;
        check(session.Document.Groups.Single(g => g.Id == group).ParentId == outer && session.SelectionUnitIds(text.Id).Count() == 3, "nested group retains inner identity and outer hit unit");
        var originalTransforms = session.Document.Elements.ToDictionary(e => e.Id, e => e.Transform);
        var transform = Affine2D.Translation(25, 10).Compose(Affine2D.Rotation(20)).Compose(Affine2D.Scale(2, 2));
        session.TransformSelection(transform);
        check(session.Document.Elements.All(e => e.Transform == transform.Compose(originalTransforms[e.Id])) && session.Document.Elements.Single(e => e.Id == text.Id).Kind == ElementKind.Text,
            "whole nested group scales and rotates vector text without rasterization");
        session.Undo();
        check(session.Document.Elements.All(e => e.Transform == originalTransforms[e.Id]), "whole group transform undo restores every matrix");
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
        session.SetSelection([box.Id, text.Id, outside.Id]);
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
        session.Add(picture); session.SetSelection([box.Id, text.Id, picture.Id]);
        var imageSnapshot = session.CreateSelectionSnapshot();
        var destination = new EditorSession();
        destination.InsertDocument(imageSnapshot, session.Assets, Affine2D.Scale(3, 3), group: true);
        var imported = destination.Document.Elements.Single(e => e.Kind == ElementKind.Image);
        check(imported.Id != picture.Id && imported.Effects[0].Id != picture.Effects[0].Id && imported.ImageCrop == picture.ImageCrop && destination.Assets.Get(imported.AssetId!).Data.Span.SequenceEqual(new byte[] { 1, 2, 3 }), "stamp insertion remaps effect/entity IDs and keeps original resource bytes and crop");
        check(destination.Document.Elements.Single(e => e.Kind == ElementKind.Text).Text == "单独编辑" && imported.Transform == Affine2D.Scale(3, 3).Compose(imageSnapshot.Elements.Single(e => e.Id == picture.Id).Transform), "stamp insertion transforms vectors without resampling media");
        check(destination.SelectionUnitIds(imported.Id).Count() == 3, "ungrouped selection inserts as a single editable stamp unit when requested");
        check(destination.UndoCount == 1 && destination.Undo() && destination.Document.Elements.Count == 0 && destination.Document.Groups.Count == 0 && destination.Redo(), "stamp insertion is one undoable transaction including groups");
        int beforeFailedInsert = destination.Document.Elements.Count;
        bool rejected = false;
        try { destination.InsertDocument(imageSnapshot, new(), Affine2D.Identity); } catch (KeyNotFoundException) { rejected = true; }
        check(rejected && destination.Document.Elements.Count == beforeFailedInsert && !destination.IsTransactionActive, "missing stamp assets cannot partially overwrite a receiving document");
    }
}
