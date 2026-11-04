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
    private CanvasSettingsTarget? target;
    private PointD? panStart;
    public bool IsDragging => document is not null;

    public bool Down(EditorSession value, PointD point, RectD referenceViewport, CanvasSettingsTarget? canvasTarget = null)
    {
        var nextTarget = canvasTarget ?? new(value);
        if (IsDragging || value.IsReadOnly || value.IsTransactionActive || !nextTarget.IsCurrent || nextTarget.Settings.Projection.Enabled ||
            !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !nextTarget.Viewport.Contains(point)) return false;
        target = nextTarget;
        session = value; document = value.Document; reference = referenceViewport;
        initialCanvas = target.Settings.Clone(); previewChanged = false; panStart = null;
        startSize = CanvasGridGeometry.ResolveCellSize(initialCanvas, reference);
        startRadius = point.DistanceTo(reference.Center);
        session.BeginTransaction(L.Get("Canvas.Title")); transaction = session.TransactionId;
        return true;
    }

    public bool Move(PointD point)
    {
        if (!Validate() || !double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;
        if (panStart is { } start)
        {
            PointD? origin = point == start ? initialCanvas!.Grid.FlatOrigin : (initialCanvas!.Grid.FlatOrigin ?? reference.Center) + point - start;
            if (target!.Settings.Grid.FlatOrigin == origin) return false;
            target.Change(canvas => canvas.Grid.FlatOrigin = origin);
            return true;
        }
        double ratio = (point.DistanceTo(reference.Center) + 32) / (startRadius + 32);
        if (ratio == 1)
        {
            if (!previewChanged) return false;
            target!.Replace(initialCanvas!); previewChanged = false; return true;
        }
        double size = Math.Clamp(startSize * ratio, CanvasGridGeometry.MinimumEditableCellSize, CanvasGridGeometry.MaximumCellSize);
        if (CanvasGridGeometry.ResolveCellSize(target!.Settings, reference) == size) return false;
        target.Change(canvas => ApplyGridSize(canvas, reference, size));
        previewChanged = true;
        return true;
    }

    internal bool DownPan(EditorSession value, PointD point, RectD referenceViewport, CanvasSettingsTarget canvasTarget)
    {
        if (!Down(value, point, referenceViewport, canvasTarget)) return false;
        panStart = point; return true;
    }

    internal static void ApplyGridSize(CanvasSettings settings, RectD reference, double size)
    {
        if (settings.Projection.PlaneBounds is not null) { settings.Grid.FlatCellSize = size; return; }
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
        session!.CommitSettings(); session = null; initialCanvas = null; target = null;
    }

    public void Cancel()
    {
        bool owns = OwnsTransaction();
        document = null;
        if (owns) session!.Cancel();
        session = null; initialCanvas = null; target = null;
    }

    private bool OwnsTransaction() => session is not null && document is not null &&
        ReferenceEquals(session.Document, document) && session.IsTransactionActive && session.TransactionId == transaction;

    private bool Validate()
    {
        if (OwnsTransaction() && target?.IsCurrent == true && !target.Settings.Projection.Enabled) return true;
        Cancel(); return false;
    }
}
