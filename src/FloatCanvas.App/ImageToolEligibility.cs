using FloatCanvas.Core;

namespace FloatCanvas.App;

/// <summary>Image-only tools never infer a target from mixed selection or a canvas background.</summary>
internal static class ImageToolEligibility
{
    internal static bool TryGetCropTarget(EditorSession session, out CanvasElement? image)
    {
        image = null;
        if (session.Selection.Count != 1) return false;
        var selected = session.SelectedElements().SingleOrDefault();
        if (selected is not { Kind: ElementKind.Image, Visible: true, Locked: false, Width: > 0, Height: > 0 } ||
            !ElementGeometry.LocalToPlane(selected).TryInverse(out _)) return false;
        image = selected;
        return true;
    }
}
