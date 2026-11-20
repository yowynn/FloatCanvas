using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private BillboardCreationWorkflow billboardWorkflow;
    private bool screenBillboardDrawing;
    private bool screenBillboardRegion;
    private bool publishingScreenBillboard;
    internal event Action? ScreenBillboardCommitted;
    internal event Action<SceneDocument, long>? ScreenBillboardCancelled;
    private Projective2D screenDraftProjection;
    private PointD screenDraftOrigin;
    private long screenDraftScopeVersion;
    private SceneDocument? screenDraftDocument;
    private long screenDraftPreviewRevision;
    internal BillboardCreationWorkflow BillboardWorkflow
    {
        get => billboardWorkflow;
        set { if (value == billboardWorkflow) return; PlacementContextVersion++; Cancel(); billboardWorkflow = value; Changed?.Invoke(); }
    }
    internal bool UsesScreenBillboardCreation => BillboardMode && BillboardWorkflow == BillboardCreationWorkflow.ScreenFirst;
    internal CanvasElement? ScreenBillboardDraft => screenBillboardDrawing && drawing is not null ? drawing.Clone() : null;
    internal bool HasScreenBillboardDraft => screenBillboardDrawing && drawing is not null;
    internal CanvasElement? ScreenBillboardCandidate => screenBillboardDrawing && TryScreenBillboardCandidate(out var candidate) ? candidate : null;
    internal bool ScreenBillboardPending => screenBillboardDrawing && !billboardDraftValid;

    private bool UsesScreenCreationInput(OverlayModifiers modifiers)
    {
        if (screenBillboardDrawing || screenBillboardRegion) return true;
        if (gesture is not null || !UsesScreenBillboardCreation || !IsBillboardCreationTool) return false;
        if (ShouldShowSelection(modifiers) && IsSelectionFrameHit(pointerScreen, includeInterior: true)) return false;
        return Tool != EditorTool.Text || (modifiers & OverlayModifiers.Control) != 0 ||
            HitTestScreen(pointerScreen, 4) is not { Kind: ElementKind.Text };
    }

    private bool IsScreenDraftContextCurrent => !(screenBillboardDrawing || screenBillboardRegion) ||
        ReferenceEquals(screenDraftDocument, Session.Document) && screenDraftPreviewRevision == Session.PreviewRevision &&
        screenDraftProjection == CurrentCanvasMapping.PlaneToScreen &&
        screenDraftOrigin == CanvasOrigin && screenDraftScopeVersion == Session.EditScopeVersion &&
        Session.CanInsertInEditScope;

    private void BeginScreenBillboardRegion()
    {
        screenBillboardRegion = UsesScreenBillboardCreation;
        if (!screenBillboardRegion) return;
        CaptureScreenDraftContext();
    }

    private void CaptureScreenDraftContext()
    {
        screenDraftProjection = CurrentCanvasMapping.PlaneToScreen;
        screenDraftOrigin = CanvasOrigin; screenDraftScopeVersion = Session.EditScopeVersion;
        screenDraftDocument = Session.Document; screenDraftPreviewRevision = Session.PreviewRevision;
    }

    private bool TryScreenBillboardCandidate(out CanvasElement candidate)
    {
        candidate = drawing?.Clone() ?? new();
        if (drawing is null || !IsScreenDraftContextCurrent) return false;
        var screen = ElementGeometry.LocalToPlane(drawing);
        if (!gestureMoved && (UsesDefaultSize(Tool) || Tool is EditorTool.Number or EditorTool.Emoji))
        {
            var reference = (drawing.Kind == ElementKind.Symbol ? ElementGeometry.StrokedLocalBounds(drawing, false) : ElementGeometry.LocalBounds(drawing)).Center;
            return BillboardCreationGeometry.TryPlaceLogical(drawing, CurrentCanvasMapping, reference,
                screen.TransformPoint(reference), out candidate, out _);
        }
        return BillboardCreationGeometry.TryFromScreenContour(drawing, CurrentCanvasMapping, screen, out candidate, out _);
    }

    private void PublishScreenBillboard()
    {
        if (!TryScreenBillboardCandidate(out var candidate)) throw new InvalidOperationException(L.Get("Billboard.InvalidLogicalPlacement"));
        new SceneGraph(Session.Document).InsertElement(candidate, Session.EditScopeId, rootCanvas: Session.ActiveRootCanvas);
        if (candidate.Kind is not (ElementKind.Freehand or ElementKind.Highlighter) &&
            (!ElementGeometry.IsLine(candidate.Kind) || ElementGeometry.LineArcHandle(candidate).HasValue))
            Session.Selection.Add(candidate.Id);
    }
}
