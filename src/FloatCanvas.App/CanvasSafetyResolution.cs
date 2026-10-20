using FloatCanvas.Core;

namespace FloatCanvas.App;

internal enum CanvasSafetyAction { Cancel, Relocate, Delete }

/// <summary>Resolves whole editing units without changing geometry, styles or group membership.</summary>
internal static class CanvasSafetyResolution
{
    internal static IReadOnlyList<Guid> AffectedIds(SceneDocument document)
    {
        var mapping = CanvasMapping.Create(document);
        var affected = document.Elements.Where(e => !mapping.IsElementSafe(e)).Select(e => e.Id).ToHashSet();
        var groups = document.Elements.Where(e => affected.Contains(e.Id)).Select(document.RootGroupId).OfType<Guid>().ToHashSet();
        return document.Elements.Where(e => affected.Contains(e.Id) || document.RootGroupId(e) is { } group && groups.Contains(group)).Select(e => e.Id).ToArray();
    }

    internal static bool TryRelocate(SceneDocument document, IReadOnlyList<Guid> affectedIds)
    {
        var mapping = CanvasMapping.Create(document);
        if (mapping.SafeScreenPolygon.Count < 3) return false;
        var affected = affectedIds.ToHashSet();
        var units = document.Elements.Where(e => affected.Contains(e.Id)).GroupBy(e => document.RootGroupId(e) ?? e.Id).ToArray();
        var replacements = new Dictionary<Guid, CanvasElement>();
        var reference = document.Canvas.Projection;
        var referenceCenter = mapping.ToScreen(reference.Origin + new PointD(reference.Columns * reference.CellSize / 2, reference.Rows * reference.CellSize / 2));
        var safeCenter = mapping.SafeScreenPolygon.Aggregate(new PointD(), (sum, p) => sum + p) * (1.0 / mapping.SafeScreenPolygon.Count);
        int index = 0;
        foreach (var unit in units)
        {
            var originals = unit.ToArray();
            var support = originals.Select(e => e.IsBillboard
                ? new RectD(e.BillboardAnchor.X, e.BillboardAnchor.Y, 0, 0)
                : ElementGeometry.Bounds(e)).ToArray();
            var center = new PointD((support.Min(b => b.Left) + support.Max(b => b.Right)) / 2,
                (support.Min(b => b.Top) + support.Max(b => b.Bottom)) / 2);
            CanvasElement[]? relocated = null;
            double minimumVisibleSize = Math.Min(12, originals.Max(e =>
            { var bounds = ElementGeometry.StrokedLocalBounds(e); return Math.Max(bounds.Width, bounds.Height); }));
            var stagger = new PointD((index % 5) * 28, (index / 5 % 5) * 28);
            foreach (var target in Targets(referenceCenter + stagger, safeCenter + stagger, document.Viewport))
            {
                if (!mapping.TryToPlane(target, out var targetPlane)) continue;
                var delta = targetPlane - center;
                var candidates = originals.Select(e => e.Clone()).ToArray();
                foreach (var candidate in candidates) BillboardGeometry.ApplyPlaneTransform(candidate, Affine2D.Translation(delta.X, delta.Y));
                if (!candidates.All(e => mapping.IsElementSafe(e) && HasInteriorMargin(mapping, e))) continue;
                var bounds = candidates.Select(e => mapping.ProjectedBounds(e)).Aggregate(RectD.Union);
                var visible = bounds.Intersect(document.Viewport);
                // Choose a useful interior position, rather than stranding content on the horizon.
                if (visible.IsEmpty || Math.Max(visible.Width, visible.Height) < minimumVisibleSize) continue;
                relocated = candidates; break;
            }
            if (relocated is null) return false;
            foreach (var element in relocated) replacements.Add(element.Id, element);
            index++;
        }
        // Nothing is changed until every unit has a valid destination.
        foreach (var element in document.Elements)
            if (replacements.TryGetValue(element.Id, out var replacement))
            { element.Transform = replacement.Transform; element.BillboardAnchor = replacement.BillboardAnchor; }
        return true;
    }

    private static bool HasInteriorMargin(CanvasMapping mapping, CanvasElement element)
    {
        var inverse = mapping.ScreenToPlane;
        double normal = Math.Sqrt(inverse.M31 * inverse.M31 + inverse.M32 * inverse.M32);
        if (normal < 1e-15) return true;
        if (element.IsBillboard) return inverse.Denominator(mapping.ToScreen(element.BillboardAnchor)) / normal >= 32;
        var bounds = mapping.ProjectedBounds(element);
        PointD[] corners = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];
        return corners.All(p => inverse.Denominator(p) / normal >= 32);
    }

    private static IEnumerable<PointD> Targets(PointD referenceCenter, PointD safeCenter, RectD viewport)
    {
        yield return referenceCenter; yield return safeCenter;
        foreach (double fraction in new[] { .5, .35, .65, .2, .8 })
            foreach (double other in new[] { .5, .35, .65, .2, .8 })
                yield return new(viewport.Left + viewport.Width * fraction, viewport.Top + viewport.Height * other);
    }
}
