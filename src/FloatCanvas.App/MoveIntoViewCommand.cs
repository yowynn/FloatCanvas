using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Recovers wholly off-screen selection units, preserving content already visible on any active display.</summary>
internal static class MoveIntoViewCommand
{
    internal static bool Execute(EditorSession session, IEnumerable<(RectD Bounds, RectD WorkArea)> displays, PointD screenOrigin)
    {
        if (session.IsTransactionActive) return false;
        var document = session.Document;
        RectD Local(RectD area) => new RectD(area.Left - screenOrigin.X, area.Top - screenOrigin.Y, area.Width, area.Height).Intersect(document.Viewport);
        var frames = displays.Select(display => (Bounds: Local(display.Bounds), Work: Local(display.WorkArea))).Where(display => !display.Bounds.IsEmpty).ToArray();
        var targets = frames.Select(display => display.Work).Where(area => !area.IsEmpty).ToArray();
        if (targets.Length == 0) return false;
        var graph = new SceneGraph(document);
        var world = SceneEvaluation.Build(document);
        bool Visible(RectD bounds) => !bounds.IsEmpty && frames.Any(frame => !frame.Bounds.Intersect(bounds).IsEmpty);
        Guid Root(Guid id) => graph.AncestorIds(id).LastOrDefault() is var root && root != Guid.Empty ? root : id;
        // A root group is the editing unit: its visible descendants determine reachability,
        // while hidden and locked children travel with their parent's placement unchanged.
        var units = world.Leaves
            .Where(leaf => leaf.RenderElement.Visible && !graph.IsLocked(Root(leaf.HostNodeId)) && !ElementGeometry.StrokedLocalBounds(leaf.RenderElement).IsEmpty)
            .GroupBy(leaf => Root(leaf.HostNodeId));
        var moves = new List<(CanvasElement Element, CanvasElement Draft)>();
        var groupMoves = new List<(Guid NodeId, Affine2D Transform)>();
        foreach (var unit in units)
        {
            var mapping = CanvasMapping.Create(document, unit.First().RootCanvas);
            var items = unit.Select(leaf => leaf.RenderElement).ToArray();
            var bounds = items.Select(e => mapping.ProjectedBounds(e)).ToArray();
            if (bounds.Any(Visible)) continue;
            var group = graph.FindGroup(unit.Key); var instance = graph.FindInstance(unit.Key);
            if (group is not null || instance is not null)
            {
                // Groups have affine plane placement. A common plane translation keeps
                // all child local placements intact, including billboard elevation.
                var support = items.Select(e => e.IsBillboard
                    ? new RectD(e.BillboardAnchor.X, e.BillboardAnchor.Y, 0, 0) : ElementGeometry.Bounds(e)).ToArray();
                var center = new PointD((support.Min(b => b.Left) + support.Max(b => b.Right)) / 2,
                    (support.Min(b => b.Top) + support.Max(b => b.Bottom)) / 2);
                var point = mapping.PlaneToScreen.TryTransformPoint(center, out var projectedCenter) ? projectedCenter : document.Viewport.Center;
                var targetPoints = new List<PointD>();
                if (bounds.All(bound => !bound.IsEmpty))
                {
                    var combined = bounds.Aggregate(RectD.Union);
                    foreach (var target in targets)
                    {
                        var candidates = combined.Width <= target.Width && combined.Height <= target.Height ? [combined] : bounds;
                        targetPoints.AddRange(candidates.Select(candidate => point + Into(candidate, target)));
                    }
                }
                targetPoints.AddRange(targets.OrderBy(area => area.Center.DistanceTo(point)).Select(area => area.Center));
                foreach (var target in targetPoints.OrderBy(target => target.DistanceTo(point)))
                {
                    if (!mapping.TryToPlane(target, out var destination)) continue;
                    var delta = destination - center;
                    var translation = Affine2D.Translation(delta.X, delta.Y);
                    var candidates = items.Select(element => { var draft = element.Clone(); BillboardGeometry.ApplyPlaneTransform(draft, translation); return draft; }).ToArray();
                    if (!candidates.All(element => mapping.IsElementSafe(element)) || !candidates.Any(element => Visible(mapping.ProjectedBounds(element)))) continue;
                    groupMoves.Add((unit.Key, translation.Compose(group?.Transform ?? instance!.Transform))); break;
                }
                continue;
            }
            Projective2D planeMove;
            PointD? screenMove=null;
            if (bounds.Any(bound => bound.IsEmpty))
            {
                // A finite element behind the perspective horizon has no visible screen bounds.
                // Bring its plane center to the nearest usable screen before evaluating visibility.
                var planeBounds = items.Select(e => ElementGeometry.Bounds(e)).Aggregate(RectD.Union);
                if (planeBounds.IsEmpty) continue;
                var point = mapping.PlaneToScreen.TryTransformPoint(planeBounds.Center, out var projected) ? projected : document.Viewport.Center;
                var target = targets.OrderBy(area => area.Center.DistanceTo(point)).First();
                if (!mapping.TryToPlane(target.Center, out var destination)) continue;
                var delta = destination - planeBounds.Center;
                planeMove = Projective2D.FromAffine(Affine2D.Translation(delta.X, delta.Y));
            }
            else
            {
                var combined = bounds.Aggregate(RectD.Union);
                PointD best = default; double distance = double.PositiveInfinity;
                foreach (var target in targets)
                {
                    // Preserve a group as one unit. If it cannot fit, make its closest member reachable.
                    var candidates = combined.Width <= target.Width && combined.Height <= target.Height ? [combined] : bounds;
                    foreach (var candidate in candidates)
                    {
                        var delta = Into(candidate, target);
                        double squared = delta.X * delta.X + delta.Y * delta.Y;
                        if (squared < distance) { best = delta; distance = squared; }
                    }
                }
                if (!double.IsFinite(distance) || distance == 0) continue;
                screenMove=best;
                // Conjugate screen translation so perspective and placed stamps retain their appearance.
                planeMove = mapping.ScreenToPlane.Compose(Projective2D.FromAffine(Affine2D.Translation(best.X, best.Y))).Compose(mapping.PlaneToScreen);
            }
            var planned = new List<(CanvasElement Element, CanvasElement Draft)>();
            foreach (var element in items)
            {
                var draft=element.Clone();
                if(element.IsBillboard)
                {
                    var center=ElementGeometry.LocalBounds(element).Center;
                    if(screenMove is { } shift&&mapping.ElementToScreen(element).TryTransformPoint(center,out var screenCenter))
                    {
                        if(!BillboardGeometry.TryMoveAnchor(element,mapping,center,screenCenter+shift,out var anchor))break;
                        draft.BillboardAnchor=anchor;
                    }
                    else if(planeMove.TryTransformPoint(element.BillboardAnchor,out var anchor))draft.BillboardAnchor=anchor;
                    else break;
                    if(!mapping.TryGetDepthScale(draft.BillboardAnchor,out _)||mapping.ProjectedBounds(draft).IsEmpty)break;
                    planned.Add((graph.FindElement(element.Id)!,draft));continue;
                }
                var transform = element.Transform; var placement = element.ProjectivePlacement;
                if (planeMove.M31 == 0 && planeMove.M32 == 0 && planeMove.M33 != 0)
                {
                    var affine = new Affine2D(planeMove.M11 / planeMove.M33, planeMove.M12 / planeMove.M33,
                        planeMove.M21 / planeMove.M33, planeMove.M22 / planeMove.M33, planeMove.M13 / planeMove.M33, planeMove.M23 / planeMove.M33);
                    transform = affine.Compose(transform);
                }
                else
                {
                    if (!transform.TryInverse(out var inverse)) break;
                    placement = Projective2D.FromAffine(inverse).Compose(planeMove).Compose(Projective2D.FromAffine(transform)).Compose(placement ?? Projective2D.Identity);
                }
                var projected = mapping.PlaneToScreen.Compose(Projective2D.FromAffine(transform)).Compose(placement ?? Projective2D.Identity);
                if (!projected.TryTransformBounds(ElementGeometry.StrokedLocalBounds(element), out _) ||
                    projected.Denominator(ElementGeometry.LocalBounds(element).Center) <= 0) break;
                draft.Transform=transform;draft.ProjectivePlacement=placement;
                planned.Add((graph.FindElement(element.Id)!,draft));
            }
            if (planned.Count == items.Length && planned.Any(move =>
                {
                    return Visible(mapping.ProjectedBounds(move.Draft));
                })) moves.AddRange(planned);
        }
        if (moves.Count == 0 && groupMoves.Count == 0) return false;
        session.Execute(L.Get("History.MoveIntoView"), _ =>
        {
            foreach (var move in moves)
            {move.Element.Transform=move.Draft.Transform;move.Element.ProjectivePlacement=move.Draft.ProjectivePlacement;move.Element.BillboardAnchor=move.Draft.BillboardAnchor;}
            foreach (var move in groupMoves)
                if (graph.FindGroup(move.NodeId) is { } movedGroup) movedGroup.Transform = move.Transform;
                else graph.FindInstance(move.NodeId)!.Transform = move.Transform;
        });
        return true;
    }

    private static PointD Into(RectD bounds, RectD frame)
    {
        // Fit ordinary objects completely; retain an accessible 32-pixel portion of oversized ones.
        double Position(double start, double size, double minimum, double maximum) => size <= maximum - minimum
            ? Math.Clamp(start, minimum, maximum - size)
            : Math.Clamp(start, minimum + Math.Min(32, maximum - minimum) - size, maximum - Math.Min(32, maximum - minimum));
        return new(Position(bounds.Left, bounds.Width, frame.Left, frame.Right) - bounds.Left,
            Position(bounds.Top, bounds.Height, frame.Top, frame.Bottom) - bounds.Top);
    }
}
