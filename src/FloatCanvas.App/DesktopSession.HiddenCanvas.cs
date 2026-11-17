using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private EditorSession? _hiddenCanvasSession;
    private long _hiddenCanvasVersion;
    private HashSet<Guid> _hiddenCanvasElements = [];
    private bool _updatingHiddenCanvas;

    private void UpdateHiddenCanvasState()
    {
        if (_updatingHiddenCanvas || _disposed) return;
        UpdatePresentationRuntime();
        UpdateWindowSourceRuntime();
        _updatingHiddenCanvas = true;
        try
        {
            if (_host.IsVisible)
            {
                _hiddenCanvasSession = null;
                _editor.CanvasContentHidden = false;
                if (_host.IsPreviewVisible) _host.SetPreviewVisible(false);
                return;
            }
            if (!ReferenceEquals(_hiddenCanvasSession, Active))
            {
                _hiddenCanvasSession = Active;
                _hiddenCanvasVersion = Active.ContentVersion;
                _hiddenCanvasElements = Active.Document.Elements.Select(element => element.Id).Concat(Active.Document.Instances.Select(instance => instance.Id)).ToHashSet();
                _editor.CanvasContentHidden = true;
                Active.SetSelection([]);
            }
            // A linked next-number update changes saved metadata, not hidden pixels.
            if (_editor.IsUpdatingNumberStart) _hiddenCanvasVersion = Active.ContentVersion;
            if (!Active.IsTransactionActive && Active.ContentVersion != _hiddenCanvasVersion)
            {
                _hiddenCanvasSession = null;
                _editor.CanvasContentHidden = false;
                _host.SetVisible(true);
                UpdateStatus(L.Get("Feedback4.Canvas.Visible"));
                return;
            }
            bool draft = Active.Document.Elements.Any(element => !_hiddenCanvasElements.Contains(element.Id)) ||
                Active.Document.Instances.Any(instance => !_hiddenCanvasElements.Contains(instance.Id));
            _host.SetPreviewVisible(_settings.PresentationGlobal.Pointer.AnyEnabled || draft || _editor.HasScreenBillboardDraft || _editor.PendingBillboardOutline is not null || _inlineText is not null || _editor.CapturePreview is not null ||
                _editor.EffectScreenPreview.Count > 0 || _host.IsEditing && _editor.Tool == EditorTool.Canvas);
        }
        finally { _updatingHiddenCanvas = false; }
    }

    private SceneDocument HiddenCanvasPreview()
    {
        var source = Active.Document;
        var canvas = source.Canvas.Clone();
        canvas.BackgroundColor = 0;
        canvas.Grid.Visible = false;
        var preview = source.CloneElements(source.Elements.Select(element => element.Id).Concat(source.Instances.Select(instance => instance.Id))
            .Where(id => !_hiddenCanvasElements.Contains(id)));
        preview.Kind = source.Kind; preview.Viewport = source.Viewport; preview.LayerOpacity = source.LayerOpacity; preview.Canvas = canvas;
        return preview;
    }
}
