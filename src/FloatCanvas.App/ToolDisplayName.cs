using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Localized tool names without shortcut legends, shared by commands and preset prompts.</summary>
internal static class ToolDisplayName
{
    internal static string Get(EditorTool tool) => L.Get(ToolPresetLibrary.Family(tool) switch
    {
        EditorTool.Select => "ToolbarV2.Select",
        EditorTool.Line => "ToolbarV2.Lines",
        EditorTool.Rectangle => "ToolbarV2.Rectangles",
        EditorTool.Ellipse => "ToolbarV2.Ellipses",
        EditorTool.Symbol => "Tool.Symbol",
        EditorTool.Emoji => "Tool.Emoji",
        EditorTool.Stamp => "Tool.Stamp",
        EditorTool.Image => "Toolbar.Image",
        EditorTool.Crop => "Tool.Crop",
        EditorTool.CaptureRegion => "Tool.CaptureRegion",
        EditorTool.Canvas => "Canvas.Title",
        var family => "Preset." + family
    });

    internal static string PresetName(EditorTool tool) => L.Format("ToolOptions.DefaultPresetName", Get(tool));
}
