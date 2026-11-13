using System.Text.Json;
using System.Windows;
namespace FloatCanvas.App;

internal sealed class ToolPresetGallery : PresetGallery<ToolPreset>
{
    private readonly EditorController controller;
    private readonly ToolPresetLibrary library;
    private readonly ToolStateStore? stateStore;
    private readonly Dictionary<EditorTool, string> selections;
    private EditorTool? observedTool;
    internal ToolPresetGallery(EditorController controller, ToolPresetLibrary library, ToolStateStore? stateStore = null)
    { this.controller = controller; this.library = library; this.stateStore = stateStore; selections = stateStore?.Selections ?? []; }
    protected override IReadOnlyList<ToolPreset> Official => library.OfficialFor(controller.Tool);
    protected override IReadOnlyList<ToolPreset> Users => library.UserFor(controller.Tool);
    internal override string SelectedId => selections.GetValueOrDefault(ToolPresetLibrary.Family(controller.Tool)) ?? "";
    protected override void Select(string id)
    { if (SelectedId == id) return; selections[ToolPresetLibrary.Family(controller.Tool)] = id; stateStore?.MarkChanged(); }
    protected override string Id(ToolPreset p) => p.Id;
    protected override string Caption(ToolPreset p) => p.Name;
    protected override FrameworkElement Preview(ToolPreset p) => (FrameworkElement)ToolParameterIcon.Create(p.Parameters, 24);
    protected override string PreviewIdentity(ToolPreset p) => JsonSerializer.Serialize(p.Parameters);
    protected override bool Modified(ToolPreset p) => JsonSerializer.Serialize(PreservePointerMaster(p.Parameters.Clone())) != JsonSerializer.Serialize(controller.Parameters);
    protected override void ApplyPreset(ToolPreset p) => controller.ApplyParameters(PreservePointerMaster(library.Apply(p.Id)));
    protected override void SavePreset(string name) => Select(library.Save(name, controller.Parameters).Id);
    protected override void UpdatePreset(ToolPreset p) => library.Update(p.Id, controller.Parameters);
    protected override void RenamePreset(ToolPreset p, string name) => library.Rename(p.Id, name);
    protected override void DeletePreset(ToolPreset p) => library.Delete(p.Id);
    protected override void MovePreset(ToolPreset p, ToolPreset target, bool after) => library.Move(p.Id, target.Id, after);
    protected override string SuggestedName => ToolDisplayName.PresetName(controller.Tool);
    internal override void PrepareForShow()
    {
        var family = ToolPresetLibrary.Family(controller.Tool);
        if (observedTool != family) { observedTool = family; revealPending = true; }
        base.PrepareForShow();
    }
    private ToolParameters PreservePointerMaster(ToolParameters parameters)
    { if (parameters.Tool == EditorTool.PointerPresentation && parameters.PointerPresentation is { } pointer) pointer.Enabled = controller.Parameters.PointerPresentation?.Enabled ?? false; return parameters; }
}
