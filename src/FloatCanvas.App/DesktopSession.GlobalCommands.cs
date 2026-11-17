using FloatCanvas.Windows;
using System.Windows;
using FloatCanvas.Core;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private sealed record GlobalCaptureState(EditorSession Session, PointD Origin, ToolParameters Parameters, bool Visible, bool Editing);
    private GlobalCaptureState? _globalCapture;
    private static readonly (string Id, string Name)[] OptionalGlobalCommands = GlobalShortcutCatalog.Optional;

    private static Dictionary<string, HotkeyBinding> GlobalBindings(HotkeyBinding toggle, HotkeyBinding hide,
        Dictionary<string, HotkeyBinding> optional)
    {
        var result = optional.Where(pair => GlobalShortcutCatalog.Optional.Any(command => command.Id == pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        result["Global.Toggle"] = toggle; result["Global.Hide"] = hide;
        return result;
    }
    private void ExecuteGlobalCommand(string id)
    {
        if (_disposed || SessionKeyboardRouter.IsModal || DocumentOperationBusy) return;
        switch(id)
        {
            case "Global.Pointer":
                TogglePresentationPointer(); break;
            case "Global.Capture":
                if(_globalCapture is null)_=RunCommand(CaptureGlobalRegionAsync);
                break;
        }
    }

    private void CompleteGlobalCapture(bool inserted,bool restoreMode=true)
    {
        if(_globalCapture is not {} state) return;
        _globalCapture=null;
        _globalCaptureWindow?.Close();
        if(inserted) {RefreshToolbarState();RequestRender();return;}
        _editor.Cancel();
        if(!ReferenceEquals(Active,state.Session)) _editor.ReplaceSession(state.Session,state.Origin);
        _editor.ApplyParameters(state.Parameters);
        if(restoreMode)
        {
            if(_host.IsEditing!=state.Editing) SetEditing(state.Editing);
            if(_host.IsVisible!=state.Visible) _host.SetVisible(state.Visible);
            RefreshPassThroughLauncher();
        }
        RefreshToolbarState();RequestRender();
    }

}
