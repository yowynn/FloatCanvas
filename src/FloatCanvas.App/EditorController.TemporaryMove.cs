using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private bool temporaryMove;
    internal bool IsTemporaryMove => temporaryMove;
    internal EditorTool InteractionTool => IsBorrowingSelection ? EditorTool.Select : Tool;

    private bool temporarySelectionHeld;
    internal bool IsBorrowingSelection => temporarySelectionHeld || temporaryMove;
    internal void BeginTemporarySelection() { if (HasPointerGesture || Session.IsTransactionActive || HasPendingBillboardOperation) return; temporarySelectionHeld = true; Changed?.Invoke(); }
    internal void EndTemporarySelection(bool cancel = false)
    {
        temporarySelectionHeld = false;
        if (cancel && temporaryMove) Cancel();
        Changed?.Invoke();
    }

    internal bool HandleTemporaryMovePointer(OverlayPointerEvent e)
    {
        if (!IsBorrowingSelection) return e.Button == OverlayMouseButton.Middle;
        if (e.Kind == OverlayPointerKind.Cancel || e.Kind == OverlayPointerKind.Down && e.Button == OverlayMouseButton.Right)
        { temporarySelectionHeld = false; Cancel(); return true; }
        if (e.Button == OverlayMouseButton.Left && e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick)
        {
            if (HasPointerGesture || Session.IsTransactionActive) return true;
            gestureMoved = false; shiftDeselectOnClick = false; shiftDeselectTarget = null; selectionOnClick = null;
            duplicateOnDrag = duplicatedOnDrag = false;
            clickPlacementGestureThreshold = double.IsFinite(ClickPlacementDragThreshold) && ClickPlacementDragThreshold > 0 ? ClickPlacementDragThreshold : 6;
            temporaryMove = true;
            pointerScreen = new(e.Position.X - CanvasOrigin.X, e.Position.Y - CanvasOrigin.Y);
            if (e.Kind == OverlayPointerKind.DoubleClick) { OpenSelectionTarget(pointerScreen); return true; }
            if (CurrentCanvasMapping.TryToPlane(pointerScreen, out var point) || TryBillboardSelectionInput(out point))
            {
                start = point; startScreen = pointerScreen; gestureModifiers = e.Modifiers;
                lastGestureMove = (point, e.Modifiers, OverlayMouseButton.Left);
                StartSelect(point, e.Modifiers);
            }
            else RejectPointer(true);
            Changed?.Invoke(); return true;
        }
        if (!temporaryMove) return false;
        if (e.Kind == OverlayPointerKind.Move) PointerCore(e, propertiesDoubleClick: false);
        else if (e.Kind == OverlayPointerKind.Up && e.Button == OverlayMouseButton.Left)
        {
            try { PointerCore(e, propertiesDoubleClick: false); }
            finally { temporaryMove = false; Changed?.Invoke(); }
        }
        return true;
    }
}
