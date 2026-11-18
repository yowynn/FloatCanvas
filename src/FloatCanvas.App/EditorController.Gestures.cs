using FloatCanvas.Core;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private bool OpenSelectionTarget(PointD point)
    {
        Cancel();
        var hit=HitTestScreen(point,4);
        if(hit is null) return false;
        var node=ResolveHitSelectableNode(hit.Id);
        if(node is {} group && Session.Document.Groups.Any(item=>item.Id==group)) Session.EnterScope(group);
        else if(node is {} instance && Session.Document.Instances.Any(item=>item.Id==instance)) PrefabEditRequested?.Invoke(instance);
        else { Session.SetSelection([hit.Id]); PropertiesRequested?.Invoke(point); }
        return true;
    }
    internal void SelectContextTarget(PointD screen)
    {
        if(HitTestScreen(screen,7) is {} hit && ResolveHitSelectableNode(hit.Id) is {} node)
        {
            if (Session.Selection.Contains(node) ||
                ResolveHitSelectableNode(hit.Id, individual: true) is { } leaf && Session.Selection.Contains(leaf)) return;
            Session.SetSelection([node]);
        }
        else Session.SetSelection([]);
    }
    private PointD ResizePivot(RectD bounds, PointD grab) => centeredGesture ? bounds.Center :
        new(grab.X >= bounds.Center.X ? bounds.X : bounds.Right, grab.Y >= bounds.Center.Y ? bounds.Y : bounds.Bottom);

    // Geometry resize helpers preserve the center. Translate the resized geometry
    // back to the chosen fixed corner without baking the viewport into the document.
    private void PreserveResizePivot(CanvasElement source, CanvasElement target, PointD pivot)
    {
        if (centeredGesture) return;
        var bounds = ElementGeometry.LocalBounds(source);
        var resized = ElementGeometry.LocalBounds(target);
        var local = new PointD(resized.X + (pivot.X-bounds.X) / Math.Max(.0001,bounds.Width)*resized.Width,
            resized.Y + (pivot.Y-bounds.Y) / Math.Max(.0001,bounds.Height)*resized.Height);
        if(source.IsBillboard)
        {
            var mapping=CurrentCanvasMapping;
            if(BillboardGeometry.TryMoveAnchor(target,mapping,local,mapping.ElementToScreen(source).TransformPoint(pivot),out var anchor))
                target.BillboardAnchor=anchor;
        }
        else
        {
            var delta=ElementGeometry.LocalToPlane(source).TransformPoint(pivot)-ElementGeometry.LocalToPlane(target).TransformPoint(local);
            BillboardGeometry.ApplyPlaneTransform(target,Affine2D.Translation(delta.X,delta.Y));
        }
    }
}
