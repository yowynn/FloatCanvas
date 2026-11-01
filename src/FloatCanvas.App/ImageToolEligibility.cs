using FloatCanvas.Core;

namespace FloatCanvas.App;

/// <summary>Image-only tools never infer a target from mixed selection or a canvas background.</summary>
internal static class ImageToolEligibility
{
    internal static bool TryGetReplaceTarget(EditorSession session, out CanvasElement? image)
    {
        image = session.Selection.Count == 1 ? session.Document.Elements.FirstOrDefault(element => session.Selection.Contains(element.Id)) : null;
        if (image is not { Kind: ElementKind.Image } || session.IsNodeLocked(image.Id)) { image = null; return false; }
        return true;
    }

    internal static bool TryGetCropTarget(EditorSession session, out CanvasElement? image)
    {
        image = null;
        if (session.Selection.Count != 1) return false;
        var selected = session.Document.Elements.FirstOrDefault(element => session.Selection.Contains(element.Id));
        if (selected is not { Kind: ElementKind.Image, Visible: true, Locked: false, Width: > 0, Height: > 0 } ||
            !session.IsNodeVisible(selected.Id) || session.IsNodeLocked(selected.Id) ||
            !ElementGeometry.LocalToPlane(new SceneGraph(session.Document).ToWorld(selected)).TryInverse(out _)) return false;
        image = selected;
        return true;
    }
}
