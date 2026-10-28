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
    private CanvasSettings? initialCanvas;
    private bool previewChanged;
    public bool IsDragging => document is not null;

    public bool Down(EditorSession value, PointD point, RectD referenceViewport)
    {
        if (IsDragging || value.IsTransactionActive || value.Document.Canvas.Projection.Enabled ||
            !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !value.Document.Viewport.Contains(point)) return false;
        session = value; document = value.Document; reference = referenceViewport;
        initialCanvas = document.Canvas.Clone(); previewChanged = false;
        startSize = CanvasGridGeometry.ResolveCellSize(document.Canvas, reference);
        startRadius = point.DistanceTo(reference.Center);
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
            session!.NotifySettingsChanged(); return true;
        }
        double size = Math.Clamp(startSize * ratio, CanvasGridGeometry.MinimumEditableCellSize, CanvasGridGeometry.MaximumCellSize);
        if (CanvasGridGeometry.ResolveCellSize(document!.Canvas, reference) == size) return false;
        ApplyGridSize(document.Canvas, reference, size);
        previewChanged = true;
        session!.NotifySettingsChanged(); return true;
    }

    internal static void ApplyGridSize(CanvasSettings settings, RectD reference, double size)
    {
        var counts = CanvasGridGeometry.CountsForCellSize(reference, size);
        var offset = settings.Grid.FlatTranslation;
        var planeReference = new RectD(reference.X - offset.X, reference.Y - offset.Y, reference.Width, reference.Height);
        var projection = CanvasGridGeometry.CreateFlatReference(planeReference, counts.Columns, counts.Rows, size);
        projection.PinLocation = settings.Projection.PinLocation;
        if (settings.Projection.LockedGridCellSize.HasValue) projection.LockedGridCellSize = size;
        projection.PinPlanePoint = projection.PinPoint;
        projection.Matrix = Projective2D.FromAffine(Affine2D.Translation(offset.X, offset.Y));
        projection.Quad = projection.Quad.Select(p => p + offset).ToList();
        settings.Projection = projection;
        settings.Grid.FlatCellSize = size;
        settings.Grid.FlatOrigin = reference.Center;
    }

    public void Up()
    {
        if (!Validate()) return;
        document = null;
        session!.CommitSettings(); session = null; initialCanvas = null;
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
