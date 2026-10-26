using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>One calibration gesture is one document transaction, in output-screen coordinates.</summary>
internal sealed class CanvasCalibrationController
{
    public const double CenterHandleHalfSize = 17;
    private EditorSession? session;
    private SceneDocument? dragDocument;
    private PointD start;
    private PointD[] initial = [];
    private CanvasProjectionSettings? initialProjection;
    private int corner = -1, edge = -1;
    private long transactionId;
    private readonly List<PointD> placed=[];
    private PointD? hover;
    private bool placementGesture;
    private bool resolutionRequested;
    public bool IsPlacing { get; private set; }
    public IReadOnlyList<PointD> PlacementPreview => hover is { } point ? [..placed,point] : placed.ToArray();
    public bool IsActive => session is not null;
    public bool IsDragging => dragDocument is not null;
    public bool IsDragConstrained { get; private set; }
    public bool IsAwaitingResolution { get; private set; }
    public IReadOnlyList<Guid> AffectedLayerIds { get; private set; } = [];
    public event Action? Changed;
    public event Action<string>? Error;
    public event Action? ResolutionRequired;

    public void Begin(EditorSession value)
    {
        if (ReferenceEquals(session, value)) return;
        End(); session = value; Changed?.Invoke();
    }
    public void BeginPlacement(EditorSession value)
    {
        End();session=value;IsPlacing=true;Changed?.Invoke();
    }
    public void End() { CancelDrag(); IsPlacing=false;placed.Clear();hover=null;session = null; Changed?.Invoke(); }
    public bool Down(PointD point)
    {
        if(IsPlacing)return Place(point);
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
        var center = CanvasGridGeometry.ReferenceCenter(session.Document.Canvas.Projection);
        bool centerHandle = Math.Abs(point.X - center.X) <= CenterHandleHalfSize && Math.Abs(point.Y - center.Y) <= CenterHandleHalfSize;
        if (corner < 0 && !centerHandle)
        {
            closest = 8;
            for (int i = 0; i < 4; i++)
            {
                double distance = SegmentDistance(point, quad[i], quad[(i + 1) % 4]);
                if (distance <= closest) { closest = distance; edge = i; }
            }
            if (edge < 0 && !Inside(point, quad)) return false;
        }
        start = point; initial = [.. quad]; initialProjection = session.Document.Canvas.Projection.Clone();
        dragDocument = session.Document; IsDragConstrained = false;
        session.BeginTransaction(L.Get("Canvas.CalibrationHistory")); transactionId = session.TransactionId;
        Changed?.Invoke(); return true;
    }
    public bool Move(PointD point, OverlayModifiers modifiers = OverlayModifiers.None)
    {
        if (IsAwaitingResolution) return false;
        if(IsPlacing)
        {if(hover!=point){hover=point;Changed?.Invoke();}return true;}
        if (!ValidateDrag()) return false;
        var candidate = initial.ToArray(); var delta = point - start;
        if (corner >= 0)
        {
            bool shift = (modifiers & OverlayModifiers.Shift) != 0;
            bool horizontal = Math.Abs(delta.X) >= Math.Abs(delta.Y);
            if (shift) delta = horizontal ? new(delta.X, 0) : new(0, delta.Y);
            var target = initial[corner] + delta;
            if ((modifiers & OverlayModifiers.Control) == 0)
            {
                var adjacent = new[] { initial[(corner + 3) % 4], initial[(corner + 1) % 4] };
                const double threshold = 8;
                if (!shift || horizontal)
                {
                    var x = adjacent.OrderBy(p => Math.Abs(p.X - target.X)).First().X;
                    if (Math.Abs(x - target.X) <= threshold) target = new(x, target.Y);
                }
                if (!shift || !horizontal)
                {
                    var y = adjacent.OrderBy(p => Math.Abs(p.Y - target.Y)).First().Y;
                    if (Math.Abs(y - target.Y) <= threshold) target = new(target.X, y);
                }
            }
            candidate[corner] = target;
        }
        else if (edge >= 0) { candidate[edge] += delta; candidate[(edge + 1) % 4] += delta; }
        else for (int i = 0; i < 4; i++) candidate[i] += delta;
        var current = session!.Document.Canvas.Projection;
        bool wasConstrained = IsDragConstrained;
        bool translating = corner < 0 && edge < 0;
        CanvasProjectionSettings settings;
        string? error;
        if (translating)
        {
            settings = initialProjection!.Clone(); settings.Quad = [.. candidate];
            settings.Matrix = Projective2D.FromAffine(Affine2D.Translation(delta.X, delta.Y)).Compose(initialProjection.Matrix);
            bool valid = candidate.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)) && CanvasCalibration.IsValidProjection(settings.Matrix);
            for (int i = 0; valid && i < 4; i++)
            {
                var a = candidate[(i + 1) % 4] - candidate[i]; var b = candidate[(i + 2) % 4] - candidate[(i + 1) % 4];
                double cross = a.X * b.Y - a.Y * b.X;
                valid = double.IsFinite(cross) && cross >= 16;
            }
            IsDragConstrained = !valid; error = valid ? null : "Canvas.UnstableProjection";
        }
        else IsDragConstrained = !CanvasCalibration.TryCalibrate(current, candidate, session.Document.Viewport, out settings, out error);
        if (IsDragConstrained)
        {
            if (!wasConstrained) Error?.Invoke(error!);
            if (translating) { if (!wasConstrained) Changed?.Invoke(); return false; }
            // Keep following coarse pointer samples up to the safe boundary instead of dropping the entire move.
            // Keep the four-point geometry valid; the viewport may contain the horizon.
            var previous = current.Quad;
            double lower = 0, upper = 1;
            bool advanced = false;
            for (int iteration = 0; iteration < 24; iteration++)
            {
                double amount = (lower + upper) / 2;
                var clipped = candidate.Select((p, i) => previous[i] + (p - previous[i]) * amount).ToArray();
                if (CanvasCalibration.TryCalibrate(current, clipped, session.Document.Viewport, out var valid, out _))
                { lower = amount; settings = valid; advanced = true; }
                else upper = amount;
            }
            if (!advanced || settings.Quad.Select((p, i) => p.DistanceTo(previous[i])).Max() < .01)
            {
                if (!wasConstrained) Changed?.Invoke();
                return false;
            }
        }
        session.Document.Canvas.Projection = settings;
        session.Document.Canvas.Grid.FlatCellSize = settings.GridCellSize;
        session.NotifySettingsChanged(); Changed?.Invoke(); return true;
    }
    public void Up(bool requestResolution = true)
    {
        if (!ValidateDrag()) return;
        if (IsAwaitingResolution)
        {
            if (requestResolution && !resolutionRequested) { resolutionRequested = true; ResolutionRequired?.Invoke(); }
            return;
        }
        AffectedLayerIds = CanvasSafetyResolution.AffectedIds(session!.Document);
        if (AffectedLayerIds.Count > 0)
        {
            IsAwaitingResolution = true; IsDragConstrained = false;
            Changed?.Invoke();
            if (requestResolution) { resolutionRequested = true; ResolutionRequired?.Invoke(); }
            return;
        }
        CommitDrag();
    }
    public bool Resolve(CanvasSafetyAction action)
    {
        if (!IsAwaitingResolution || !ValidateDrag()) return false;
        if (action == CanvasSafetyAction.Cancel) { CancelDrag(); return true; }
        if (action == CanvasSafetyAction.Keep) { CommitDrag(); return true; }
        if (action == CanvasSafetyAction.Relocate && !CanvasSafetyResolution.TryRelocate(session!.Document, AffectedLayerIds))
        { Error?.Invoke("Canvas.RelocateFailed"); return false; }
        if (action == CanvasSafetyAction.Delete)
        {
            var ids = AffectedLayerIds.ToHashSet();
            session!.Document.Elements.RemoveAll(e => ids.Contains(e.Id));
        }
        CommitDrag(); return true;
    }
    private void CommitDrag()
    {
        dragDocument = null; initialProjection = null; IsDragConstrained = false; IsAwaitingResolution = false; resolutionRequested = false; AffectedLayerIds = [];
        if (placementGesture) { IsPlacing = false; placed.Clear(); hover = null; placementGesture = false; }
        session!.Commit(); Changed?.Invoke();
    }
    public void CancelDrag()
    {
        if (dragDocument is null) return;
        bool owns = session is not null && ReferenceEquals(session.Document, dragDocument) && session.IsTransactionActive && session.TransactionId == transactionId;
        dragDocument = null; initialProjection = null; IsDragConstrained = false; IsAwaitingResolution = false; resolutionRequested = false; AffectedLayerIds = [];
        if (placementGesture) { IsPlacing = true; placementGesture = false; hover = null; }
        if (owns) session!.Cancel();
        Changed?.Invoke();
    }
    public void Escape() { if (IsDragging) CancelDrag(); else End(); }
    private bool Place(PointD point)
    {
        if(session is null||session.IsTransactionActive||!double.IsFinite(point.X)||!double.IsFinite(point.Y))return false;
        if(placed.Any(p=>p.DistanceTo(point)<2))return false;
        if(placed.Count<3){placed.Add(point);hover=null;Changed?.Invoke();return true;}
        PointD[] candidate=[..placed,point];
        string? error="Canvas.InvalidQuad";
        if(!CanvasCalibration.TryOrderCorners(candidate,out var quad)||
            !CanvasCalibration.TryCalibrate(session.Document.Canvas.Projection,quad,session.Document.Viewport,out var projection,out error))
        {Error?.Invoke(error??"Canvas.InvalidQuad");return false;}
        session.BeginTransaction(L.Get("Canvas.CalibrationHistory")); transactionId = session.TransactionId;
        dragDocument = session.Document; placementGesture = true; IsPlacing = false;
        session.Document.Canvas.Projection = projection;
        session.Document.Canvas.Grid.FlatCellSize = projection.GridCellSize;
        session.NotifySettingsChanged();
        // The fourth point is selected on Down. Wait for the physical Up before opening a modal.
        Up(requestResolution: false); Changed?.Invoke(); return true;
    }
    private bool ValidateDrag()
    {
        if (dragDocument is null) return false;
        if (session is not null && ReferenceEquals(session.Document, dragDocument) && session.IsTransactionActive && session.TransactionId == transactionId) return true;
        dragDocument = null; initialProjection = null; IsDragConstrained = false; IsAwaitingResolution = false; resolutionRequested = false; AffectedLayerIds = []; placementGesture = false;
        Changed?.Invoke(); return false;
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
