using System.Windows;
using System.Windows.Controls;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class ShortcutSettingsTable
{
    private static readonly double[] ColumnWidths = [1.05, 1.8, 1.65, 3.2, .85];
    internal enum Area { General, Tools, Canvas, Layers, Stamps, Capture, Presentation, Numeric }
    internal enum Source { Default, Custom, Fixed }

    internal sealed record Row(Area Area, string Name, string Binding, string Context, Source Source,
        CommandChord[] Keys, EditorCommand? Command = null)
    {
        internal bool Matches(string query, CommandChord? recordedKey)
        {
            if (recordedKey is { } key) return Keys.Contains(key);
            var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var text = string.Join(" ", AreaLabel(Area), Name, Binding, Context, SourceLabel(Source), Command?.Id);
            var compactBinding = string.Concat(Binding.Where(character => !char.IsWhiteSpace(character)));
            return terms.All(term => text.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                compactBinding.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal static string AreaLabel(Area area) => L.Get("Shortcuts.Area." + area);
    internal static string SourceLabel(Source source) => L.Get("Shortcuts.Source." + source);

    internal static IEnumerable<Row> Rows(IReadOnlyDictionary<string, CommandChord[]> draft)
    {
        foreach (var command in EditorCommandCatalog.All)
        {
            bool custom = draft.TryGetValue(command.Id, out var keys);
            keys ??= command.Defaults;
            yield return new(CommandArea(command.Id), command.Name,
                keys.Length == 0 ? L.Get("Shortcuts.Unbound") : string.Join(" / ", keys.Select(key => key.ToString())),
                L.Get("Shortcuts.When." + CommandContext(command.Id, keys)),
                !command.Configurable ? Source.Fixed : custom ? Source.Custom : Source.Default, keys, command);
        }

        yield return Fixed(Area.Layers, "Layers.EnterGroup", "LayerGroup", new(0x0D), new(0x27));
        yield return Fixed(Area.Layers, "Shortcuts.ParentLayer", "LayerList", new(0x25), new(0x08));
        yield return Fixed(Area.Stamps, "Shortcuts.StampSearch", "StampLibrary", new CommandChord(0x46, OverlayModifiers.Control));
        yield return Fixed(Area.Stamps, "Stamp.Save", "StampLibrary", new CommandChord(0x4E, OverlayModifiers.Control));
        yield return Fixed(Area.Stamps, "Stamp.Use", "StampList", new CommandChord(0x0D));
        yield return Fixed(Area.Stamps, "Stamp.Rename", "StampList", new CommandChord(0x71));
        yield return Fixed(Area.Stamps, "Stamp.Delete", "StampList", new CommandChord(0x2E));
        yield return Fixed(Area.Capture, "Shortcuts.FinishPolygon", "Polygon", new CommandChord(0x0D));
        yield return Fixed(Area.Numeric, "Shortcuts.NumericStep", "NumericSlider", Arrows(OverlayModifiers.None));
        yield return Fixed(Area.Numeric, "Shortcuts.NumericFine", "NumericSlider", Arrows(OverlayModifiers.Shift).Concat(Arrows(OverlayModifiers.Control | OverlayModifiers.Shift)).ToArray());
        yield return Fixed(Area.Numeric, "Shortcuts.NumericCoarse", "NumericSlider", Arrows(OverlayModifiers.Control).Concat([new(0x21), new(0x22)]).ToArray());
        yield return Fixed(Area.Numeric, "Shortcuts.NumericBounds", "NumericSlider", new(0x24), new(0x23));
        yield return Gesture(Area.Layers, "DeepSelect", "DeepSelectKeys", "SelectTool");
        yield return Gesture(Area.Layers, "CrossLevelSelect", "CrossLevelSelectKeys", "SelectTool");
        yield return Gesture(Area.Layers, "DuplicateDrag", "DuplicateDragKeys", "SelectTool");
        yield return Gesture(Area.Layers, "CenterResize", "CenterResizeKeys", "ResizeHandle");
        yield return Gesture(Area.Canvas, "PointerPan", "PointerPanKeys", "ViewNavigation");
        yield return Gesture(Area.Canvas, "PointerZoom", "PointerZoomKeys", "ViewNavigation");
    }

    private static CommandChord[] Arrows(OverlayModifiers modifiers) =>
        [new(0x25, modifiers), new(0x26, modifiers), new(0x27, modifiers), new(0x28, modifiers)];

    private static Row Fixed(Area area, string name, string context, params CommandChord[] keys) =>
        new(area, L.Get(name), string.Join(" / ", keys.Select(key => key.ToString())), L.Get("Shortcuts.When." + context), Source.Fixed, keys);

    private static Row Gesture(Area area, string name, string binding, string context) =>
        new(area, L.Get("Shortcuts." + name), L.Get("Shortcuts." + binding), L.Get("Shortcuts.When." + context), Source.Fixed, []);

    private static Area CommandArea(string id) => id switch
    {
        "Tool.CaptureRegion" or "Capture.Back" => Area.Capture,
        "Tool.Presentation" or "Tool.PointerPresentation" => Area.Presentation,
        "Tool.Canvas" or "Interaction.Pan" or "Edit.Clear" => Area.Canvas,
        "Panel.Tools" => Area.Tools,
        "Panel.Layers" or "Edit.SelectAll" or "Edit.Deselect" or "Edit.Group" or "Edit.Ungroup" or "Edit.Delete" or "Edit.Duplicate" => Area.Layers,
        _ => id.Split('.')[0] switch
        {
            "Tool" or "Tools" or "Image" => Area.Tools,
            "Canvas" or "View" => Area.Canvas,
            "Layer" or "Selection" or "Move" => Area.Layers,
            "Stamp" => Area.Stamps,
            _ => Area.General
        }
    };

    private static string CommandContext(string id, CommandChord[] keys) => id switch
    {
        "Interaction.Cancel" => "Cancel",
        "Interaction.Pan" => "Pan",
        "Panel.Tools" => "ToolOptions",
        "Edit.Clear" => "ClearCanvas",
        "Capture.Back" => "PolygonOrCalibration",
        "Canvas.OpacityUp" or "Canvas.OpacityDown" => "CanvasOpacity",
        "Selection.Rename" => "RenameLayer",
        "Selection.Properties" or "View.Selection" => "Selected",
        "Edit.Ungroup" => "SelectedGroup",
        "Edit.Group" or "Edit.Cut" or "Edit.Delete" or "Edit.Duplicate" or "Stamp.Create" => "EditableSelection",
        "Edit.Undo" => "Undo",
        "Edit.Redo" => "Redo",
        "Tool.Crop" => "Crop",
        "File.Open" or "File.Save" or "File.SaveAs" or "File.New" when keys.Any(EditorShortcutPolicy.IsProjectChordUsableFromText) => "File",
        _ when id.StartsWith("Move.", StringComparison.Ordinal) => "Move",
        _ when id.StartsWith("Layer.", StringComparison.Ordinal) => "EditableSelection",
        _ when id.StartsWith("Stamp.Slot", StringComparison.Ordinal) => "StampSlot",
        _ => "Editing"
    };

    internal static Grid Cells(string[] values, bool header = false)
    {
        var grid = new Grid();
        for (int index = 0; index < values.Length; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new(ColumnWidths[index], GridUnitType.Star) });
            var cell = new TextBlock
            {
                Text = values[index], Margin = new(5, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal
            };
            if (header || index is 0 or 4) cell.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
            Grid.SetColumn(cell, index);
            grid.Children.Add(cell);
        }
        return grid;
    }
}
