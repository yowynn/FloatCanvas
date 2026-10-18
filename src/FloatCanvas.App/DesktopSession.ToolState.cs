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
        if (commitDraft) _toolOptionsPanel?.CloseColorPicker();
        _toolbarToolMemory[ToolFamily(_editor.Tool)] = _editor.Parameters;
        _toolStateStore.Save();
        _toolStateSaveTimer?.Stop();
    }

    private ToolParameters ApplyToolbarPreset(string id, ToolParameters current)
    {
        if (_toolStateStore is not { } state) return _toolPresets.Apply(id, current);
        if (!state.OfficialBaselines.TryGetValue(id, out var baseline))
            state.OfficialBaselines[id] = baseline = _toolPresets.Apply(id, current);
        state.Selections[ToolFamily(current.Tool)] = id;
        state.MarkChanged();
        return baseline.Clone();
    }

    // Internal capture/import transitions use the same memory without opening UI
    // or treating a return to a tool as an explicit preset selection.
    private void SwitchToolState(EditorTool tool, bool applyAlias = false)
    {
        _inspector?.CompletePendingEdit();
        var family = ToolFamily(tool);
        if (ToolFamily(_editor.Tool) == family && !applyAlias) return;
        _toolbarToolMemory[ToolFamily(_editor.Tool)] = _editor.Parameters;
        bool remembered = _toolbarToolMemory.TryGetValue(family, out var saved);
        var next = remembered ? saved!.Clone() :
            _toolPresets.Apply(_toolPresets.OfficialFor(family).First().Id, new ToolParameters { Tool = family });
        next.Tool = family;
        if (!remembered)
        {
            next.NumberStart = Math.Clamp(_settings.NumberStart, 1, 999999);
            next.NumberStep = Math.Clamp(_settings.NumberStep, 1, 999999);
            if (family == EditorTool.Highlighter) next.Style.StrokeWidth = Math.Max(24, next.Style.StrokeWidth);
            if (family == EditorTool.Emoji) next.Style.FontSize = 48;
        }
        if (applyAlias && tool is EditorTool.Arrow or EditorTool.DoubleArrow)
            next = ApplyToolbarPreset(tool == EditorTool.Arrow ? "official.arrow" : "official.double-arrow", next);
        if (applyAlias && tool == EditorTool.RoundedRectangle) next = ApplyToolbarPreset("official.rounded-rectangle", next);
        _editor.ApplyParameters(next);
        _toolStateStore?.MarkChanged();
    }
}
