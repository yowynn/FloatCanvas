using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private BillboardCreationWorkflow billboardWorkflow;
    private bool screenBillboardDrawing;
    private bool screenBillboardRegion;
    private Projective2D screenDraftProjection;
    private PointD screenDraftOrigin;
    private long screenDraftScopeVersion;
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
        screenDraftProjection == CurrentCanvasMapping.PlaneToScreen &&
        screenDraftOrigin == CanvasOrigin && screenDraftScopeVersion == Session.EditScopeVersion &&
        Session.CanInsertInEditScope;

    private void BeginScreenBillboardRegion()
    {
        screenBillboardRegion = UsesScreenBillboardCreation;
        if (!screenBillboardRegion) return;
        screenDraftProjection = CurrentCanvasMapping.PlaneToScreen;
        screenDraftOrigin = CanvasOrigin; screenDraftScopeVersion = Session.EditScopeVersion;
    }

    private bool TryScreenBillboardCandidate(out CanvasElement candidate)
    {
        candidate = drawing?.Clone() ?? new();
        if (drawing is null || !IsScreenDraftContextCurrent ||
            !BillboardGeometry.TryResolveScreenBottom(candidate, CurrentCanvasMapping,
                ElementGeometry.LocalToPlane(candidate), out var placement, out _)) return false;
        candidate.IsBillboard = true; candidate.ProjectivePlacement = null;
        candidate.BillboardAnchor = placement.Anchor; candidate.BillboardHeight = placement.Height;
        candidate.Transform = placement.Linear;
        return true;
    }

    private void PublishScreenBillboard()
    {
        if (!TryScreenBillboardCandidate(out var candidate)) throw new InvalidOperationException(L.Get("Billboard.InvalidDepth"));
        new SceneGraph(Session.Document).InsertElement(candidate, Session.EditScopeId, rootCanvas: Session.ActiveRootCanvas);
        if (candidate.Kind is not (ElementKind.Freehand or ElementKind.Highlighter) && !ElementGeometry.IsLine(candidate.Kind))
            Session.Selection.Add(candidate.Id);
    }
}
