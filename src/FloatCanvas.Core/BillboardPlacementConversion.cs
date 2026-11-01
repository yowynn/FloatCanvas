namespace FloatCanvas.Core;

/// <summary>Converts placement only when the current screen appearance has an exact, safe representation.</summary>
public static class BillboardPlacementConversion
{
    public static bool TryPreserveScreen(CanvasElement element, bool billboard, CanvasMapping mapping)
    {
        if (element.IsBillboard == billboard) return true;
        var candidate = element.Clone();
        var screen = mapping.ElementToScreen(element);
        if (billboard)
        {
            if (!BillboardGeometry.TryResolveScreenBottom(candidate, mapping, screen, out var placement, out _)) return false;
            candidate.IsBillboard = true; candidate.ProjectivePlacement = null;
            candidate.BillboardAnchor = placement.Anchor; candidate.BillboardHeight = placement.Height; candidate.Transform = placement.Linear;
        }
        else
        {
            var plane = mapping.ScreenToPlane.Compose(screen);
            if (!plane.IsFinite || !plane.TryInverse(out _)) return false;
            candidate.IsBillboard = false; candidate.Transform = Affine2D.Identity; candidate.ProjectivePlacement = plane;
        }
        if (!mapping.IsElementSafe(candidate)) return false;
        EditorSession.CopyPlacement(element, candidate);
        return true;
    }
}
