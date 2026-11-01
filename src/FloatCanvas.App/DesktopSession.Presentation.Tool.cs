using System.Text.Json;
using FloatCanvas.Core;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private bool _syncingPointerTool;

    private void ApplyPointerToolPreferences()
    {
        if (_syncingPointerTool || _editor.Tool != EditorTool.PointerPresentation || _editor.PointerPresentation is not { } pointer) return;
        if (SamePointerPreferences(pointer, _settings.PresentationGlobal.Pointer)) return;
        var value = _settings.PresentationGlobal.Clone(); value.Pointer = pointer.Clone();
        try { ApplyPresentationGlobalSettings(value); }
        catch
        {
            SynchronizePointerToolPreferences();
            throw;
        }
    }

    private void SynchronizePointerToolPreferences()
    {
        if (_syncingPointerTool || _editor.Tool != EditorTool.PointerPresentation ||
            SamePointerPreferences(_editor.PointerPresentation, _settings.PresentationGlobal.Pointer)) return;
        var parameters = _editor.Parameters; parameters.PointerPresentation = _settings.PresentationGlobal.Pointer.Clone();
        _syncingPointerTool = true;
        try { _editor.ApplyParameters(parameters); }
        finally { _syncingPointerTool = false; }
        _toolOptionsPanel?.Refresh();
    }

    private static bool SamePointerPreferences(PresentationPointerSettings? first, PresentationPointerSettings second) =>
        first is not null && JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
}
