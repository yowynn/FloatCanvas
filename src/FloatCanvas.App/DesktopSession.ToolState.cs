using System.Windows.Threading;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private readonly ToolStateStore? _toolStateStore;
    private DispatcherTimer? _toolStateSaveTimer;

    private void ScheduleToolStateSave()
    {
        if (_disposed) return;
        if (_toolStateSaveTimer is null)
        {
            _toolStateSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _toolStateSaveTimer.Tick += (_, _) =>
            {
                try { FlushToolState(); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { UpdateStatus(ex.Message); }
            };
        }
        _toolStateSaveTimer.Stop(); _toolStateSaveTimer.Start();
    }

    private void FlushToolState(bool commitDraft = false)
    {
        _toolStateSaveTimer?.Stop();
        if (_toolStateStore is null) return;
        if (commitDraft) _toolOptionsPanel?.TryClosePicker();
        if (_editor.Tool != EditorTool.Canvas) _toolbarToolMemory[ToolFamily(_editor.Tool)] = _editor.Parameters;
        _toolStateStore.Save();
        _toolStateSaveTimer?.Stop();
    }

    private ToolParameters ApplyToolbarPreset(string id, ToolParameters current)
    {
        if (_toolStateStore is { } state)
        {
            state.Selections[ToolFamily(current.Tool)] = id;
            state.MarkChanged();
        }
        var parameters = _toolPresets.Apply(id);
        if (parameters.Tool == EditorTool.PointerPresentation && parameters.PointerPresentation is { } pointer)
            pointer.Enabled = current.PointerPresentation?.Enabled ?? _settings.PresentationGlobal.Pointer.Enabled;
        return parameters;
    }

    // Internal capture/import transitions use the same memory without opening UI
    // or treating a return to a tool as an explicit preset selection.
    private void SwitchToolState(EditorTool tool, bool applyAlias = false)
    {
        _inspector?.CompletePendingEdit();
        var family = ToolFamily(tool);
        if (ToolFamily(_editor.Tool) == family && !applyAlias) return;
        if (_editor.Tool != EditorTool.Canvas) _toolbarToolMemory[ToolFamily(_editor.Tool)] = _editor.Parameters;
        if (family == EditorTool.Canvas)
        {
            _editor.Tool = EditorTool.Canvas;
            return;
        }
        bool remembered = _toolbarToolMemory.TryGetValue(family, out var saved);
        var next = remembered ? saved!.Clone() :
            _toolPresets.Apply(_toolPresets.OfficialFor(family).First().Id);
        next.Tool = family;
        if (!remembered)
        {
            next.NumberStart = Math.Clamp(_settings.NumberStart, -999999, 999999);
            next.NumberStep = Math.Clamp(_settings.NumberStep, -999999, 999999);
            if (family == EditorTool.Highlighter) next.Style.StrokeWidth = Math.Max(24, next.Style.StrokeWidth);
            if (family == EditorTool.Number) next.Style.Fill = 0xFFFFFFFF;
        }
        if (applyAlias && tool is EditorTool.Arrow or EditorTool.DoubleArrow)
            next = ApplyToolbarPreset(tool == EditorTool.Arrow ? "official.arrow" : "official.double-arrow", next);
        if (applyAlias && tool == EditorTool.RoundedRectangle) next = ApplyToolbarPreset("official.rounded-rectangle", next);
        if (applyAlias && tool == EditorTool.Triangle) next = ApplyToolbarPreset("official.triangle", next);
        if (family == EditorTool.PointerPresentation) next.PointerPresentation = _settings.PresentationGlobal.Pointer.Clone();
        _editor.ApplyParameters(next);
        _toolStateStore?.MarkChanged();
    }
}
