using System.Text.Json;
using FloatCanvas.Core;

internal static class ArrowHeadTests
{
    public static void Run(Action<bool, string> check)
    {
        var legacy = JsonSerializer.Deserialize<ElementStyle>("{\"StrokeWidth\":4}")!;
        check(legacy.ArrowHeadStyle == ArrowHeadStyle.FilledTriangle && legacy.ArrowHeadSize == 0, "old style JSON retains filled triangle and automatic head size");
        var arrow = new CanvasElement { Kind = ElementKind.Arrow, Points = [new(0, 0), new(100, 0)], Style = legacy };
        check(ElementGeometry.ArrowHead(arrow).SequenceEqual(new PointD[] { new(100, 0), new(84, 7.2), new(84, -7.2) }), "legacy automatic head geometry is unchanged");
        arrow.Style.StrokeWidth = 1;
        check(ElementGeometry.ArrowHead(arrow)[1].X == 90, "automatic arrow head keeps minimum size ten");
        arrow.Style.StrokeWidth = 6;
        check(ElementGeometry.ArrowHead(arrow)[1].X == 76, "automatic arrow head follows stroke width times four");
        arrow.Style.StrokeWidth = 2; arrow.Style.ArrowHeadSize = 40;
        var transform = Affine2D.Translation(200, -50).Compose(Affine2D.Rotation(33)).Compose(Affine2D.Scale(2, .5));
        foreach (var style in Enum.GetValues<ArrowHeadStyle>().Where(s => s != ArrowHeadStyle.None))
        {
            arrow.Style.ArrowHeadStyle = style;
            var copy = arrow.Clone();
            check(copy.Style.ArrowHeadStyle == style && copy.Style.ArrowHeadSize == 40 && !ReferenceEquals(copy.Style, arrow.Style), $"{style}: clone preserves independent style and size");
            var head = ElementGeometry.GetArrowHeads(arrow).Single();
            check(head.Count == (style == ArrowHeadStyle.Diamond ? 4 : 3) && head.Min(p => p.X) == 60 && head.Max(p => p.X) == 100,
                $"{style}: explicit head length and vertex count");
            arrow.Transform = transform;
            check(head.All(p => ElementGeometry.Bounds(arrow).Contains(transform.TransformPoint(p))) &&
                head.All(p => ElementGeometry.Bounds(arrow, false).Contains(transform.TransformPoint(p))), $"{style}: transformed bounds include real head vertices");
            check(ElementGeometry.HitTest(arrow, transform.TransformPoint(new(70, 8)), 0) == (style != ArrowHeadStyle.Open), $"{style}: transformed interior respects fill or open V");
            arrow.Kind = ElementKind.DoubleArrow;
            var heads = ElementGeometry.GetArrowHeads(arrow);
            check(heads.Count == 2 && heads[0].Max(p => p.X) - heads[0].Min(p => p.X) == 40 &&
                heads[1].Max(p => p.X) - heads[1].Min(p => p.X) == 40 && heads.All(h => h.Count == head.Count), $"{style}: both arrow ends share style and size");
            check(heads.SelectMany(h => h).All(p => ElementGeometry.Bounds(arrow).Contains(transform.TransformPoint(p))), $"{style}: double arrow bounds include both transformed heads");
            arrow.Kind = ElementKind.Arrow; arrow.Transform = Affine2D.Identity;
        }
        arrow.Style.ArrowHeadStyle = ArrowHeadStyle.Open;
        check(ElementGeometry.HitTest(arrow, new(80, 9), 0), "open V wing has an exact stroke hit");
        check(!ElementGeometry.HitTest(arrow, new(60, 8), 0), "open V does not invent a closing base edge or filled interior");
        arrow.Transform = transform;
        check(ElementGeometry.HitTest(arrow, transform.TransformPoint(new(80, 9)), 0) &&
            !ElementGeometry.HitTest(arrow, transform.TransformPoint(new(60, 6)), 0), "open arrow stroke respects nonuniform matrix rather than maximum-scale inflation");
        arrow.Transform = Affine2D.Identity; arrow.Style.ArrowHeadStyle = ArrowHeadStyle.FilledTriangle;
        check(!ElementGeometry.HitTest(arrow, new(70, 14.5), 0) && ElementGeometry.HitTest(arrow, new(70, 14.5), 2), "filled head honors requested hit tolerance instead of a fixed halo");
        arrow.Style.ArrowHeadStyle = ArrowHeadStyle.Diamond;
        check(ElementGeometry.HitTest(arrow, new(80, 15), 0), "diamond side interior is filled");
        arrow.Style.ArrowHeadStyle = ArrowHeadStyle.FilledTriangle;
        check(!ElementGeometry.HitTest(arrow, new(80, 15), 0), "triangle does not use diamond silhouette");
        arrow.Style.StrokeWidth = 0;
        check(!ElementGeometry.HitTest(arrow, new(70, 8), 0), "zero-width invisible arrow does not retain a filled-head hit");
        arrow.Style.StrokeWidth = 2;
        arrow.Style.ArrowHeadSize = 512;
        check(ElementGeometry.Bounds(arrow).Left < -400, "large explicit heads remain part of object bounds");
        arrow.Points = [new(0, 0), new(0, 0)];
        foreach (var style in Enum.GetValues<ArrowHeadStyle>().Where(s => s != ArrowHeadStyle.None))
        {
            arrow.Style.ArrowHeadStyle = style;
            check(ElementGeometry.ArrowHead(arrow).All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)), $"{style}: zero-length segment has finite head geometry");
        }
    }

    public static void CommittedSnapshots(Action<bool, string> check)
    {
        var a = new CanvasElement { Kind = ElementKind.Text, Text = "committed A", Width = 80, Height = 24 };
        var b = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 20 };
        var session = new EditorSession(); session.Add(a);
        session.BeginTransaction("pending B"); session.Document.Elements.Add(b);
        var snapshot = session.CreateCommittedSnapshot();
        check(snapshot.Elements.Count == 1 && snapshot.Elements[0].Id == a.Id, "active gesture committed snapshot includes A and excludes unfinished B");
        snapshot.Elements[0].Text = "independent";
        check(session.Document.Elements[0].Text == "committed A" && session.CreateCommittedSnapshot().Elements[0].Text == "committed A", "committed snapshot is independent of live session and transaction backup");
        session.Commit();
        check(session.CreateCommittedSnapshot().Elements.Select(e => e.Id).SequenceEqual(new[] { a.Id, b.Id }), "snapshot includes B after commit");
        session.BeginTransaction("cancel mutation"); session.Document.Elements[0].Text = "pending"; session.Document.Elements.RemoveAt(1); session.Cancel();
        check(session.CreateCommittedSnapshot().Elements[0].Text == "committed A" && session.CreateCommittedSnapshot().Elements.Count == 2, "cancel restores committed A and B for recovery snapshots");
        var canceled = new EditorSession(); canceled.Add(a);
        canceled.BeginTransaction("discard B"); canceled.Document.Elements.Add(b); canceled.Cancel();
        var canceledSnapshot = canceled.CreateCommittedSnapshot();
        check(canceledSnapshot.Elements.Count == 1 && canceledSnapshot.Elements[0].Id == a.Id && canceledSnapshot.Elements[0].Text == "committed A",
            "canceling unfinished B leaves a committed snapshot containing only unchanged A");
    }
}
