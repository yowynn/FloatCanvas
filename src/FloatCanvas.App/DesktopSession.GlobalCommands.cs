using FloatCanvas.Windows;
using System.Windows;
using FloatCanvas.Core;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private bool _keyboardPresentationPaused, _pointerPresentationPaused;
    private (bool Visible, bool Editing)? _allOverlaysHidden;
    private sealed record GlobalCaptureState(EditorSession Session,PointD Origin,ToolParameters Parameters,bool Visible,bool Editing,
        (bool Visible,bool Editing)? HiddenOverlays);
    private GlobalCaptureState? _globalCapture;
    private static readonly (string Id, string Name)[] OptionalGlobalCommands =
    [
        ("Global.Enter", "Shortcuts.Global.Enter"), ("Global.Exit", "Shortcuts.Global.Exit"),
        ("Global.Keyboard", "Shortcuts.Global.Keyboard"), ("Global.Pointer", "Shortcuts.Global.Pointer"),
        ("Global.HideAll", "Shortcuts.Global.HideAll"), ("Global.Capture", "Shortcuts.Global.Capture")
    ];

    private static Dictionary<string, HotkeyBinding> GlobalBindings(HotkeyBinding toggle, HotkeyBinding hide,
        Dictionary<string, HotkeyBinding> optional) => new(optional)
        { ["Global.Toggle"] = toggle, ["Global.Hide"] = hide };

    private void ExecuteGlobalCommand(string id)
    {
        if (_disposed || SessionKeyboardRouter.IsModal || DocumentOperationBusy) return;
        switch(id)
        {
            case "Global.Enter":
                RestoreAllOverlays(); if (!_host.IsVisible) _host.SetVisible(true); if (!_host.IsEditing) SetEditing(true); break;
            case "Global.Exit":
                if (_host.IsEditing) SetEditing(false); break;
            case "Global.Keyboard":
                _keyboardPresentationPaused=!_keyboardPresentationPaused; ResetPresentationCommandContext(); break;
            case "Global.Pointer":
                _pointerPresentationPaused=!_pointerPresentationPaused; ResetPresentationCommandContext(); break;
            case "Global.HideAll":
                if(_allOverlaysHidden is not null) RestoreAllOverlays();
                else
                {
                    _allOverlaysHidden=(_host.IsVisible,_host.IsEditing);
                    _editor.Cancel(); SetEditing(false); _host.SetVisible(false);
                    _toolbar.Hide(); _passThroughLauncher?.Hide(); ResetPresentationCommandContext();
                }
                break;
            case "Global.Capture":
                if(_globalCapture is not null) break;
                var previous=new GlobalCaptureState(Active,_editor.CanvasOrigin,_editor.Parameters,_host.IsVisible,_host.IsEditing,_allOverlaysHidden);
                RestoreAllOverlays();
                _globalCapture=previous;
                if(!_host.IsEditing && !SetEditing(true)) {CompleteGlobalCapture(false);break;}
                if(!_host.IsVisible) _host.SetVisible(true);
                SelectTool(EditorTool.CaptureRegion); break;
        }
    }

    private void CompleteGlobalCapture(bool inserted,bool restoreMode=true)
    {
        if(_globalCapture is not {} state) return;
        _globalCapture=null;
        if(inserted) {SwitchToolState(EditorTool.Select);RefreshToolbarState();RequestRender();return;}
        _editor.Cancel();
        if(!ReferenceEquals(Active,state.Session)) _editor.ReplaceSession(state.Session,state.Origin);
        _editor.ApplyParameters(state.Parameters);
        if(restoreMode)
        {
            _allOverlaysHidden=state.HiddenOverlays;
            if(_host.IsEditing!=state.Editing) SetEditing(state.Editing);
            if(_host.IsVisible!=state.Visible) _host.SetVisible(state.Visible);
            RefreshPassThroughLauncher();
        }
        RefreshToolbarState();RequestRender();
    }

    private void RestoreAllOverlays()
    {
        if(_allOverlaysHidden is not {} state) return;
        _allOverlaysHidden=null; _host.SetVisible(state.Visible); SetEditing(state.Editing);
        RefreshPassThroughLauncher(); ResetPresentationCommandContext();
    }

    private void ResetPresentationCommandContext()
    {
        _presentationInputContext=new(); UpdatePresentationRuntime(); RequestRender();
    }
}
