using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private void SwitchRootCanvas(RootCanvasKind root)
    {
        if (DocumentOperationBusy || _prefabEditContext is not null || Active.ActiveRootCanvas == root) return;
        _inspector?.CompletePendingEdit(); FinishInlineText(); _editor.Cancel();
        EndCanvasCalibration(hidePanel: true); CancelLivePreparation(); ClearProjectionHover();
        _editor.ClearPresentedScene();
        _groupCanvasScene = null;
        Active.SetRootCanvas(root);
        UpdateStatus(L.Get("RootCanvas." + root));
        RequestRender();
    }
}
