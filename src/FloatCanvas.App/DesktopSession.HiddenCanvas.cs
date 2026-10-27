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
                _hiddenCanvasElements = Active.Document.Elements.Select(element => element.Id).ToHashSet();
                _editor.CanvasContentHidden = true;
                Active.SetSelection([]);
            }
            if (!Active.IsTransactionActive && Active.ContentVersion != _hiddenCanvasVersion)
            {
                _hiddenCanvasSession = null;
                _editor.CanvasContentHidden = false;
                _host.SetVisible(true);
                UpdateStatus(L.Get("Feedback4.Canvas.Visible"));
                return;
            }
            bool draft = Active.Document.Elements.Any(element => !_hiddenCanvasElements.Contains(element.Id));
            _host.SetPreviewVisible(draft || _inlineText is not null || _editor.CapturePreview is not null ||
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
        return new() { Kind = source.Kind, Viewport = source.Viewport, LayerOpacity = source.LayerOpacity, Canvas = canvas,
            Elements = source.Elements.Where(element => !_hiddenCanvasElements.Contains(element.Id)).ToList(), Groups = source.Groups };
    }
}
