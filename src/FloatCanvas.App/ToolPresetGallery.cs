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
    internal override string SelectedId => ToolPresetLibrary.ResolveOfficialId(selections.GetValueOrDefault(ToolPresetLibrary.Family(controller.Tool)) ?? "");
    protected override void Select(string id)
    { if (SelectedId == id) return; selections[ToolPresetLibrary.Family(controller.Tool)] = id; stateStore?.MarkChanged(); }
    protected override string Id(ToolPreset p) => p.Id;
    protected override string Caption(ToolPreset p) => p.Name;
    protected override FrameworkElement Preview(ToolPreset p) => CreatePreview(p.Parameters, p.IsOfficial);
    internal static FrameworkElement CreatePreview(ToolParameters parameters, bool isOfficial = false)
    {
        const double tileSize = 24;
        double iconSize = isOfficial && parameters.Tool == EditorTool.Number
            ? tileSize * parameters.Style.FontSize / ToolPresetLibrary.StandardMarkerSize
            : tileSize;
        var icon = (FrameworkElement)ToolParameterIcon.Create(parameters, iconSize);
        if (parameters.Tool is not (EditorTool.Freehand or EditorTool.Highlighter or EditorTool.Line or EditorTool.Arrow or EditorTool.DoubleArrow
            or EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse or EditorTool.Symbol or EditorTool.Triangle
            or EditorTool.Text or EditorTool.Number or EditorTool.Emoji or EditorTool.Presentation)) return icon;
        return new System.Windows.Controls.Border { Width = tileSize, Height = tileSize, Background = PreviewBackground, Child = icon };
    }
    private static readonly System.Windows.Media.Brush PreviewBackground = CreatePreviewBackground();
    private static System.Windows.Media.Brush CreatePreviewBackground()
    {
        var drawing = new System.Windows.Media.DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, 8, 8));
            var shade = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(205, 210, 218));
            context.DrawRectangle(shade, null, new Rect(0, 0, 4, 4));
            context.DrawRectangle(shade, null, new Rect(4, 4, 4, 4));
        }
        var brush = new System.Windows.Media.DrawingBrush(drawing) { TileMode = System.Windows.Media.TileMode.Tile,
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 8, 8) };
        brush.Freeze(); return brush;
    }
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
    { parameters.NormalizeForCreation(); if (parameters.Tool == EditorTool.PointerPresentation && parameters.PointerPresentation is { } pointer) pointer.Enabled = controller.Parameters.PointerPresentation?.Enabled ?? false; return parameters; }
}
