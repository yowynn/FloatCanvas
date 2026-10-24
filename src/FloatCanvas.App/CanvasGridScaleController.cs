using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Scales the flat reference grid radially as one cancellable document gesture.</summary>
internal sealed class CanvasGridScaleController
{
    private EditorSession? session;
    private SceneDocument? document;
    private long transaction;
    private RectD reference;
    private double startRadius, startSize;
    private bool sizeLocked;
    private CanvasSettings? initialCanvas;
    private bool previewChanged;
    public bool IsDragging => document is not null;

    public bool Down(EditorSession value, PointD point, RectD referenceViewport, bool hitExistingContent = true)
    {
        if (IsDragging || value.IsTransactionActive || value.Document.Canvas.Projection.Enabled ||
            !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !value.Document.Viewport.Contains(point) ||
            hitExistingContent && value.Document.LayerOpacity > 0 && value.Document.Elements.Any(element =>
                element.Style.Opacity > 0 && ElementGeometry.HitTest(element, point))) return false;
        session = value; document = value.Document; reference = referenceViewport;
        initialCanvas = document.Canvas.Clone(); previewChanged = false;
        startSize = CanvasGridGeometry.ResolveCellSize(document.Canvas, reference);
        startRadius = point.DistanceTo(reference.Center);
        sizeLocked = document.Canvas.Projection.LockedGridCellSize.HasValue;
        session.BeginTransaction(L.Get("Canvas.Title")); transaction = session.TransactionId;
        return true;
    }

    public bool Move(PointD point)
    {
        if (!Validate() || !double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;
        double ratio = (point.DistanceTo(reference.Center) + 32) / (startRadius + 32);
        if (ratio == 1)
        {
            if (!previewChanged) return false;
            document!.Canvas = initialCanvas!.Clone(); previewChanged = false;
            session!.NotifyChanged(); return true;
        }
        double size = Math.Clamp(startSize * ratio, CanvasGridGeometry.MinimumEditableCellSize, CanvasGridGeometry.MaximumCellSize);
        if (CanvasGridGeometry.ResolveCellSize(document!.Canvas, reference) == size) return false;
        ApplyGridSize(document.Canvas, reference, size, sizeLocked);
        previewChanged = true;
        session!.NotifyChanged(); return true;
    }

    internal static void ApplyGridSize(CanvasSettings settings, RectD reference, double size, bool? locked = null)
    {
        var counts = CanvasGridGeometry.CountsForCellSize(reference, size);
        var projection = CanvasGridGeometry.CreateFlatReference(reference, counts.Columns, counts.Rows, size);
        if (locked ?? settings.Projection.LockedGridCellSize.HasValue) projection.LockedGridCellSize = size;
        settings.Projection = projection;
        settings.Grid.FlatCellSize = size;
        settings.Grid.FlatOrigin = reference.Center;
    }

    public void Up()
    {
        if (!Validate()) return;
        document = null;
        session!.Commit(); session = null; initialCanvas = null;
    }

    public void Cancel()
    {
        bool owns = OwnsTransaction();
        document = null;
        if (owns) session!.Cancel();
        session = null; initialCanvas = null;
    }

    private bool OwnsTransaction() => session is not null && document is not null &&
        ReferenceEquals(session.Document, document) && session.IsTransactionActive && session.TransactionId == transaction;

    private bool Validate()
    {
        if (OwnsTransaction() && !document!.Canvas.Projection.Enabled) return true;
        Cancel(); return false;
    }
}
