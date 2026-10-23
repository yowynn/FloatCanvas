using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class ToolStateStoreTests
{
    public static void Run(Action<string, bool> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-tool-state", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { RoundTrip(folder, check); InvalidCaches(folder, check); FailedWrite(folder, check); }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void RoundTrip(string folder, Action<string, bool> check)
    {
        string path = Path.Combine(folder, "working.json");
        var store = new ToolStateStore(path);
        store.Save();
        check("tool state without a cache does not create a default file", !File.Exists(path) && store.Tools.Count == 0);
        int notifications = 0; store.Changed += () => notifications++;
        store.MarkChanged(); store.MarkChanged();
        check("marking tool state only notifies its owner", notifications == 2 && !File.Exists(path));

        var pen = Parameters(EditorTool.Freehand);
        store.Tools[EditorTool.Freehand] = pen;
        store.Tools[EditorTool.Line] = Parameters(EditorTool.DoubleArrow);
        store.Tools[EditorTool.Rectangle] = Parameters(EditorTool.RoundedRectangle);
        store.Tools[EditorTool.Symbol] = Parameters(EditorTool.Triangle);
        store.Tools[EditorTool.Number] = Parameters(EditorTool.Number);
        store.Selections[EditorTool.Freehand] = "user-working-basis";
        store.Selections[EditorTool.Line] = "official.double-arrow";
        store.Selections[EditorTool.Number] = "official.number";
        store.Buttons[EditorTool.Line] = EditorTool.DoubleArrow;
        store.Buttons[EditorTool.Rectangle] = EditorTool.RoundedRectangle;
        store.Buttons[EditorTool.Symbol] = EditorTool.Triangle;
        store.Buttons[EditorTool.Blur] = EditorTool.Pixelate;
        store.OfficialBaselines["official.freehand"] = new ToolParameters();
        store.PreviewSources[EditorTool.Freehand] = new ToolParameters();
        store.Save();
        var restored = new ToolStateStore(path);
        check("tool state survives reconstruction with every tool parameter field", restored.Tools.Count == 5 && store.Tools.All(pair =>
            JsonSerializer.Serialize(pair.Value) == JsonSerializer.Serialize(restored.Tools[pair.Key])));
        check("tool state keeps canonical families with their legacy alias parameters", restored.Tools[EditorTool.Line].Tool == EditorTool.DoubleArrow &&
            restored.Tools[EditorTool.Rectangle].Tool == EditorTool.RoundedRectangle && restored.Tools[EditorTool.Symbol].Tool == EditorTool.Triangle);
        check("tool state retains selected user and official recipes independently", restored.Selections.OrderBy(p => p.Key).SequenceEqual(store.Selections.OrderBy(p => p.Key)));
        check("tool state keeps grouped button choices without merging working tools", restored.Buttons.OrderBy(p => p.Key).SequenceEqual(store.Buttons.OrderBy(p => p.Key)) && !restored.Tools.ContainsKey(EditorTool.Pixelate));
        check("tool state preserves exact colors transparency line and font settings", restored.Tools[EditorTool.Freehand].Style is
            { Stroke: 0x7F102030, Fill: 0, StrokePattern: StrokePattern.Dotted, FontFamily: "Arial", FontSize: 35, TextAlignment: TextAlignment.Right });
        check("tool state preserves number defaults and geometry settings", restored.Tools[EditorTool.Number] is
            { NumberStart: 103, NumberStep: 7, DefaultWidth: 231, DefaultHeight: 87, CornerRadius: 19, ConstrainAspect: true, EffectStrength: 39 });
        restored.Tools[EditorTool.Freehand].Style.Stroke = 0xFF010203;
        check("restored working values cannot rewrite official baseline or preview source", restored.OfficialBaselines["official.freehand"].Style.Stroke == ColorLibrary.ForegroundRed &&
            restored.PreviewSources[EditorTool.Freehand].Style.Stroke == ColorLibrary.ForegroundRed);
        restored.Tools[EditorTool.Freehand].Style.Stroke = pen.Style.Stroke;
        DateTime marker = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, marker); restored.Save();
        check("unchanged tool state does not rewrite an existing file", File.GetLastWriteTimeUtc(path) == marker);
        restored.Selections[EditorTool.Freehand] = "user-deleted-but-valid"; restored.Save();
        check("syntactically valid missing preset IDs remain for gallery repair", new ToolStateStore(path).Selections[EditorTool.Freehand] == "user-deleted-but-valid");
    }

    private static void InvalidCaches(string folder, Action<string, bool> check)
    {
        string validPath = Path.Combine(folder, "valid.json");
        var initial = new ToolStateStore(validPath); initial.Tools[EditorTool.Freehand] = Parameters(EditorTool.Freehand); initial.Save();
        string valid = File.ReadAllText(validPath);
        var cases = new Dictionary<string, string>
        {
            ["unknown version"] = Change(valid, n => n["Version"] = 99),
            ["missing root field"] = Change(valid, n => n.Remove("Buttons")),
            ["unknown root field"] = Change(valid, n => n["Unknown"] = true),
            ["duplicate root field"] = valid.Replace("\"Version\": 1", "\"Version\": 1, \"Version\": 1", StringComparison.Ordinal),
            ["missing parameter field"] = Change(valid, n => n["Tools"]!["Freehand"]!.AsObject().Remove("Style")),
            ["missing style field"] = Change(valid, n => n["Tools"]!["Freehand"]!["Style"]!.AsObject().Remove("FontFamily")),
            ["unknown parameter field"] = Change(valid, n => n["Tools"]!["Freehand"]!["Unknown"] = 1),
            ["unknown style field"] = Change(valid, n => n["Tools"]!["Freehand"]!["Style"]!["Unknown"] = 1),
            ["duplicate nested field"] = valid.Replace("\"StrokeWidth\": 13", "\"StrokeWidth\": 13, \"StrokeWidth\": 13", StringComparison.Ordinal),
            ["nonfamily key"] = Change(valid, n => n["Tools"]!["Arrow"] = n["Tools"]!["Freehand"]!.DeepClone()),
            ["numeric enum alias key"] = Change(valid, n => n["Tools"]!["1"] = n["Tools"]!["Freehand"]!.DeepClone()),
            ["mismatched tool family"] = Change(valid, n => n["Tools"]!["Freehand"]!["Tool"] = (int)EditorTool.Ellipse),
            ["invalid parameters"] = Change(valid, n => n["Tools"]!["Freehand"]!["Style"]!["Opacity"] = -1),
            ["null dictionary"] = Change(valid, n => n["Selections"] = null),
            ["invalid selection ID"] = Change(valid, n => n["Selections"]!["Freehand"] = "../../preset"),
            ["invalid official baseline ID"] = Change(valid, n => n["OfficialBaselines"]!["user-wrong"] = n["Tools"]!["Freehand"]!.DeepClone()),
            ["mismatched button family"] = Change(valid, n => n["Buttons"]!["Freehand"] = (int)EditorTool.Text)
        };
        foreach (var (name, json) in cases)
        {
            string path = Path.Combine(folder, "invalid.json"); File.WriteAllText(path, json);
            bool rejected = false;
            try { _ = new ToolStateStore(path); } catch (InvalidDataException) { rejected = true; }
            bool shouldReject=name is "duplicate root field" or "duplicate nested field";
            check("tool state " + (shouldReject?"rejects ambiguous ":"compatibly reads ") + name + " and preserves source bytes", rejected==shouldReject && File.ReadAllText(path) == json);
        }
    }

    private static void FailedWrite(string folder, Action<string, bool> check)
    {
        string path = Path.Combine(folder, "atomic.json");
        var store = new ToolStateStore(path); store.Tools[EditorTool.Freehand] = new(); store.Save();
        byte[] before = File.ReadAllBytes(path); store.Tools[EditorTool.Freehand].Style.Stroke = 0xFF135790;
        bool rejected = false;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { store.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
            check("failed atomic tool save preserves the previous cache bytes", rejected && before.SequenceEqual(File.ReadAllBytes(path)));
        }
        check("failed atomic tool save removes its temporary file", Directory.GetFiles(folder, "*.tmp").Length == 0);
        store.Save();
        check("failed save does not advance the fingerprint and can retry", new ToolStateStore(path).Tools[EditorTool.Freehand].Style.Stroke == 0xFF135790);
        before = File.ReadAllBytes(path); store.Tools[EditorTool.Freehand].Style.Opacity = double.NaN;
        rejected = false;
        try { store.Save(); } catch (InvalidDataException) { rejected = true; }
        check("invalid in-memory working state cannot replace the last valid cache", rejected && before.SequenceEqual(File.ReadAllBytes(path)));
    }

    private static ToolParameters Parameters(EditorTool tool) => new()
    {
        Tool = tool, CornerRadius = 19, Symbol = SymbolKind.Heart, ConstrainAspect = true, EffectStrength = 39, Emoji = "👩‍🎨",
        DefaultWidth = 231, DefaultHeight = 87, NumberStart = 103, NumberStep = 7,
        Style = new() { Stroke = 0x7F102030, Fill = 0, StrokeWidth = 13, Opacity = .42, FontSize = 35, FontFamily = "Arial", TextAlignment = TextAlignment.Right,
            ArrowHeadStyle = ArrowHeadStyle.Diamond, StartArrowHead = ArrowHeadStyle.Open, EndArrowHead = ArrowHeadStyle.None, ArrowHeadSize = 27, StrokePattern = StrokePattern.Dotted }
    };

    private static string Change(string json, Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(json)!.AsObject(); mutate(node); return node.ToJsonString();
    }
}
