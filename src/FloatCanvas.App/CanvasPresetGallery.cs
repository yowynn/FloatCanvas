using System.Windows;
using System.Text.Json;
using FloatCanvas.Core;
namespace FloatCanvas.App;

internal sealed class CanvasPresetGallery : PresetGallery<CanvasPreset>
{
    private readonly CanvasPresetLibrary library;
    private readonly ToolStateStore? stateStore;
    private CanvasSettings current = new();
    private string selectedId = "";
    private CanvasPresetParameters? appliedSnapshot, appliedDefinition;
    internal event Action<CanvasPresetParameters>? CanvasApplied;
    internal CanvasPresetGallery(CanvasPresetLibrary library, ToolStateStore? stateStore = null)
    { this.library = library; this.stateStore = stateStore; selectedId = stateStore?.CanvasPresetSelection ?? ""; }
    protected override IReadOnlyList<CanvasPreset> Official => library.Official;
    protected override IReadOnlyList<CanvasPreset> Users => library.User;
    internal override string SelectedId => selectedId;
    protected override void Select(string id)
    { if (selectedId == id) return; selectedId = id; appliedSnapshot = appliedDefinition = null; if (stateStore is not null) { stateStore.CanvasPresetSelection = id; stateStore.MarkChanged(); } }
    protected override string Id(CanvasPreset p) => p.Id;
    protected override string Caption(CanvasPreset p) => p.Name;
    protected override FrameworkElement Preview(CanvasPreset p) => new CanvasParameterIcon(p.Parameters);
    protected override string PreviewIdentity(CanvasPreset p) => JsonSerializer.Serialize(p.Parameters);
    protected override bool Modified(CanvasPreset p)
    { var actual = CanvasPresetParameters.From(current); var expected = appliedSnapshot ?? p.Parameters; return !expected.Matches(actual) || !actual.Matches(expected) || appliedDefinition is { } definition && (!definition.Matches(p.Parameters) || !p.Parameters.Matches(definition)); }
    protected override void ApplyPreset(CanvasPreset p)
    { CanvasApplied?.Invoke(p.Parameters.Clone()); appliedSnapshot = CanvasPresetParameters.From(current); appliedDefinition = p.Parameters.Clone(); }
    protected override void SavePreset(string name) { var saved = library.Save(name, current); Select(saved.Id); appliedSnapshot = CanvasPresetParameters.From(current); appliedDefinition = saved.Parameters.Clone(); }
    protected override void UpdatePreset(CanvasPreset p) { library.Update(p.Id, current); Select(p.Id); appliedSnapshot = CanvasPresetParameters.From(current); appliedDefinition = CanvasPresetParameters.From(current); }
    protected override void RenamePreset(CanvasPreset p, string name) => library.Rename(p.Id, name);
    protected override void DeletePreset(CanvasPreset p) { library.Delete(p.Id); appliedSnapshot = null; }
    protected override void MovePreset(CanvasPreset p, CanvasPreset target, bool after) => library.Move(p.Id, target.Id, after);
    protected override string SuggestedName => ToolDisplayName.PresetName(EditorTool.Canvas);
    protected override bool CanEdit => library.CanEdit;
    internal void Refresh(CanvasSettings settings) { current = settings.Clone(); base.Refresh(); }
    internal void Delete(string id) => Run(() => Delete(library.All.First(p => p.Id == id)));
}
