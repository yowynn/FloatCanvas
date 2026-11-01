using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private bool temporaryMove;
    internal bool IsTemporaryMove => temporaryMove;
    internal EditorTool InteractionTool => temporaryMove ? EditorTool.Select : Tool;

    /// <summary>Uses the normal Move selection gesture while retaining the actual tool and its working parameters.</summary>
    internal bool HandleTemporaryMovePointer(OverlayPointerEvent e)
    {
        if (Tool == EditorTool.PointerPresentation)
        {
            if (temporaryMove || e.Kind == OverlayPointerKind.Cancel) Cancel();
            return true;
        }
        if (e.Kind == OverlayPointerKind.Cancel)
        {
            if (!temporaryMove) return false;
            Cancel(); return true;
        }
        if (e.Button == OverlayMouseButton.Middle && e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick)
        {
            Cancel();
            if (Session.IsTransactionActive) return true;
            temporaryMove = true;
            pointerScreen = new(e.Position.X - CanvasOrigin.X, e.Position.Y - CanvasOrigin.Y);
            if (CurrentCanvasMapping.TryToPlane(pointerScreen, out var point) || TryBillboardSelectionInput(out point))
            {
                start = point; startScreen = pointerScreen;
                gestureModifiers = e.Modifiers;
                lastGestureMove = (point, e.Modifiers, OverlayMouseButton.Left);
                StartSelect(point, e.Modifiers);
            }
            else RejectPointer(true);
            Changed?.Invoke();
            return true;
        }
        if (!temporaryMove) return e.Button == OverlayMouseButton.Middle;
        if (e.Kind == OverlayPointerKind.Down && e.Button == OverlayMouseButton.Right)
        {
            Cancel();
            return true;
        }
        if (e.Kind == OverlayPointerKind.Move)
            PointerCore(e with { Button = OverlayMouseButton.Left }, propertiesDoubleClick: false);
        else if (e.Kind == OverlayPointerKind.Up && e.Button == OverlayMouseButton.Middle)
        {
            try { PointerCore(e with { Button = OverlayMouseButton.Left }, propertiesDoubleClick: false); }
            finally { temporaryMove = false; Changed?.Invoke(); }
        }
        return true;
    }
}
