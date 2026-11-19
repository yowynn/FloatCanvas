using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private const double CornerRadiusHitDistance = 9;
    private sealed record CornerRadiusDrag(CanvasElement Target, CanvasElement Displayed, PointD LocalGrab,
        double? OriginalRadius, double EffectiveRadius);
    private CornerRadiusDrag? cornerRadiusDrag;

    internal PointD? SelectionScreenCornerRadiusHandle
    {
        get
        {
            if (Session.Selection.Count != 1 || SceneGraph.Read(Session.Document).FindElement(Session.Selection.First())?.Kind
                is not (ElementKind.Rectangle or ElementKind.RoundedRectangle)) return null;
            return SelectedEditingWorldElements(editableOnly: true).ToArray() is [var element]
                ? ScreenCornerRadiusHandle(element) : null;
        }
    }

    private PointD? ScreenCornerRadiusHandle(CanvasElement element)
    {
        if (element.Kind is not (ElementKind.Rectangle or ElementKind.RoundedRectangle) ||
            !element.Visible || element.Locked || Session.IsReadOnly || gesture == "draw" ||
            Session.Selection.Count != 1 || !Session.Selection.Contains(element.Id)) return null;
        var mapping = CurrentCanvasMapping;
        var toScreen = mapping.ElementToScreen(element);
        double maximum = Math.Min(element.Width, element.Height) / 2;
        if (!mapping.IsElementSafe(element) || !toScreen.TryTransformPoint(default, out var corner) ||
            !toScreen.TryTransformPoint(new(maximum, maximum), out var inside)) return null;
        double diagonal = corner.DistanceTo(inside);
        if (diagonal < 1e-6) return null;
        // Leave room for the square resize handle even when the authored radius is zero.
        double inset = maximum * Math.Min(.4, 18 / diagonal) + ElementGeometry.CornerRadius(element) / 2;
        return toScreen.TryTransformPoint(new(inset, inset), out var handle) ? handle : null;
    }

    private bool TryStartCornerRadius(CanvasElement element)
    {
        if (ScreenCornerRadiusHandle(element) is not { } handle) return false;
        double distance = pointerScreen.DistanceTo(handle);
        if (distance >= CornerRadiusHitDistance || SelectionScreenCorners.Any(p => p.DistanceTo(pointerScreen) <= distance) ||
            BillboardSelectionGuide is { } guide && guide.Center.DistanceTo(pointerScreen) <= distance) return false;
        if (!CurrentCanvasMapping.ElementToScreen(element).TryInverse(out var inverse) ||
            !inverse.TryTransformPoint(pointerScreen, out var grab)) return false;
        var target = Session.Document.Elements.Single(e => e.Id == element.Id);
        cornerRadiusDrag = new(target, element, grab, target.CornerRadius, ElementGeometry.CornerRadius(element));
        gesture = "corner-radius";
        BeginPointerTransaction(L.Get("Editor.History.AdjustCornerRadius"));
        return true;
    }

    private void MoveCornerRadius(CornerRadiusDrag edit)
    {
        if (!CurrentCanvasMapping.ElementToScreen(edit.Displayed).TryInverse(out var inverse) ||
            !inverse.TryTransformPoint(pointerScreen, out var local)) { RejectPointer(); return; }
        var delta = local - edit.LocalGrab;
        double maximum = Math.Min(edit.Target.Width, edit.Target.Height) / 2;
        double radius = Math.Clamp(edit.EffectiveRadius + delta.X + delta.Y, 0, maximum);
        // A click or a drag back to the initial radius preserves a legacy automatic or oversized radius.
        double? authored = Math.Abs(radius - edit.EffectiveRadius) < 1e-9 ? edit.OriginalRadius : radius;
        if (edit.Target.CornerRadius == authored) return;
        edit.Target.CornerRadius = authored;
        Session.NotifyChanged();
    }
}
