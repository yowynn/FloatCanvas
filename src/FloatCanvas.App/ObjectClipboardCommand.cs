using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Editable clipboard snapshots and one-step insertion without OS clipboard access.</summary>
internal static class ObjectClipboardCommand
{
    internal static SceneDocument CreateRasterSnapshot(EditorSession session, SceneDocument objects)
    {
        var snapshot = objects.Clone();
        snapshot.Canvas = session.Document.Canvas.Clone();
        if (snapshot.Elements.Count > 0)
            snapshot.Viewport = snapshot.Elements.Select(element => SceneRenderer.ProjectedBounds(session.Document, element, true))
                .Aggregate(RectD.Union).Inflate(1).Intersect(session.Document.Viewport);
        return snapshot;
    }

    internal static SceneDocument CreateSnapshot(EditorSession session)
    {
        var snapshot = session.CreateSelectionSnapshot(normalizeOrigin: false);
        snapshot.Elements.RemoveAll(element => !element.Visible);
        snapshot.PruneGroups();
        if (snapshot.Elements.Count > 0)
        {
            // Preserve desktop positions and the visible clipboard appearance, including thin lines.
            snapshot.Viewport = snapshot.Elements.Select(element => ElementGeometry.Bounds(element)).Aggregate(RectD.Union).Inflate(1);
            snapshot.LayerOpacity = session.Document.LayerOpacity;
        }
        return snapshot;
    }

    internal static void Insert(EditorSession target, SceneDocument source, AssetLibrary assets, RectD visibleArea)
    {
        if (source.Elements.Count == 0) return;
        var snapshot = source.Clone();
        foreach (var element in snapshot.Elements) element.Locked = false;
        var offset = new PointD(16, 16);
        var bounds = snapshot.Elements.Select(element => ElementGeometry.Bounds(element)).Aggregate(RectD.Union);
        var shiftedBounds = new RectD(bounds.X + offset.X, bounds.Y + offset.Y, bounds.Width, bounds.Height);
        // Keep the normal paste offset unless the entire selection would be off the current screen.
        if (!shiftedBounds.Intersects(visibleArea)) offset += visibleArea.Center - shiftedBounds.Center;
        var mapping = CanvasMapping.Create(target.Document);
        foreach (var element in snapshot.Elements)
        {
            var candidate = element.Clone();
            BillboardGeometry.ApplyPlaneTransform(candidate, Affine2D.Translation(offset.X, offset.Y));
            if (!mapping.IsElementSafe(candidate)) throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
        }
        target.InsertDocument(snapshot, assets, Affine2D.Translation(offset.X, offset.Y), label: L.Get("History.Paste"));
    }
}
