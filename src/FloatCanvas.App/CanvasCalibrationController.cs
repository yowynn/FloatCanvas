using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>One calibration gesture is one document transaction, in output-screen coordinates.</summary>
internal sealed class CanvasCalibrationController
{
    private EditorSession? session;
    private SceneDocument? dragDocument;
    private PointD start;
    private PointD[] initial = [];
    private int corner = -1, edge = -1;
    private long transactionId;
    public bool IsActive => session is not null;
    public bool IsDragging => dragDocument is not null;
    public event Action? Changed;
    public event Action<string>? Error;

    public void Begin(EditorSession value)
    {
        if (ReferenceEquals(session, value)) return;
        End(); session = value; Changed?.Invoke();
    }
    public void End() { CancelDrag(); session = null; Changed?.Invoke(); }
    public bool Down(PointD point)
    {
        if (session is null || IsDragging || session.IsTransactionActive) return false;
        var quad = session.Document.Canvas.Projection.Quad;
        if (quad.Count != 4) return false;
        corner = -1; edge = -1;
        double closest = 12;
        for (int i = 0; i < 4; i++)
        {
            double distance = quad[i].DistanceTo(point);
            if (distance <= closest) { closest = distance; corner = i; }
        }
        if (corner < 0)
        {
            closest = 8;
            for (int i = 0; i < 4; i++)
            {
                double distance = SegmentDistance(point, quad[i], quad[(i + 1) % 4]);
                if (distance <= closest) { closest = distance; edge = i; }
            }
            if (edge < 0 && !Inside(point, quad)) return false;
        }
        start = point; initial = [.. quad]; dragDocument = session.Document;
        session.BeginTransaction(L.Get("Canvas.CalibrationHistory")); transactionId = session.TransactionId;
        Changed?.Invoke(); return true;
    }
    public bool Move(PointD point)
    {
        if (!ValidateDrag()) return false;
        var candidate = initial.ToArray(); var delta = point - start;
        if (corner >= 0) candidate[corner] += delta;
        else if (edge >= 0) { candidate[edge] += delta; candidate[(edge + 1) % 4] += delta; }
        else for (int i = 0; i < 4; i++) candidate[i] += delta;
        if (!CanvasCalibration.TryCalibrate(session!.Document.Canvas.Projection, candidate, session.Document.Viewport, out var settings, out var error))
        { Error?.Invoke(error!); return false; }
        session.Document.Canvas.Projection = settings; session.NotifyChanged(); Changed?.Invoke(); return true;
    }
    public void Up()
    {
        if (!ValidateDrag()) return;
        dragDocument = null; session!.Commit(); Changed?.Invoke();
    }
    public void CancelDrag()
    {
        if (dragDocument is null) return;
        bool owns = session is not null && ReferenceEquals(session.Document, dragDocument) && session.IsTransactionActive && session.TransactionId == transactionId;
        dragDocument = null;
        if (owns) session!.Cancel();
        Changed?.Invoke();
    }
    public void Escape() { if (IsDragging) CancelDrag(); else End(); }
    private bool ValidateDrag()
    {
        if (dragDocument is null) return false;
        if (session is not null && ReferenceEquals(session.Document, dragDocument) && session.IsTransactionActive && session.TransactionId == transactionId) return true;
        dragDocument = null; Changed?.Invoke(); return false;
    }
    private static double SegmentDistance(PointD p, PointD a, PointD b)
    {
        var d = b - a; double length = d.X * d.X + d.Y * d.Y;
        double t = length == 0 ? 0 : Math.Clamp(((p.X - a.X) * d.X + (p.Y - a.Y) * d.Y) / length, 0, 1);
        return p.DistanceTo(a + d * t);
    }
    private static bool Inside(PointD p, IReadOnlyList<PointD> q)
    {
        for (int i = 0; i < 4; i++)
        {
            var a = q[(i + 1) % 4] - q[i]; var b = p - q[i];
            if (a.X * b.Y - a.Y * b.X < 0) return false;
        }
        return true;
    }
}
