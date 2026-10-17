using System.IO;
using System.Reflection;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;

internal static class ToolPresetAndColorTests
{
    public static void Run(Action<string, bool> check)
    {
        check("tool parameters reject multiple emoji graphemes before preview or insertion", !new ToolParameters { Emoji = "😀😀" }.IsValid());
        check("tool parameters retain whole joined emoji sequences", new ToolParameters { Emoji = "👩‍🎨" }.IsValid());
        check("tool parameters reject blank emoji glyphs", !new ToolParameters { Emoji = " " }.IsValid());
        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-tool-presets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Presets(root, check); Colors(root, check); Fields(root, check);
    }

    private static void Presets(string root, Action<string, bool> check)
    {
        string path = Path.Combine(root, "presets.json");
        var library = new ToolPresetLibrary(path);
        var current = new ToolParameters
        {
            Tool = EditorTool.Rectangle, CornerRadius = 27, Symbol = SymbolKind.Heart, ConstrainAspect = true, EffectStrength = 42,
            Style = new() { Stroke = 0x7F112233, Fill = 0x33445566, StrokeWidth = 13, Opacity = .42, FontSize = 35, FontFamily = "Arial",
                StartArrowHead = ArrowHeadStyle.Open, EndArrowHead = ArrowHeadStyle.None, ArrowHeadSize = 45, StrokePattern = StrokePattern.Dotted }
        };
        var applied = library.Apply("official.arrow", current);
        check("official unified arrow changes relevant line parameters and preserves colors and unrelated values", applied.Tool == EditorTool.Line &&
            applied.Style.StartArrowHead == ArrowHeadStyle.None && applied.Style.EndArrowHead == ArrowHeadStyle.FilledTriangle && applied.Style.StrokePattern == StrokePattern.Solid &&
            applied.Style.Stroke == current.Style.Stroke && applied.Style.Fill == current.Style.Fill && applied.Style.StrokeWidth == 13 && applied.Style.Opacity == .42 && applied.CornerRadius == 27 && applied.EffectStrength == 42);
        var circle = library.Apply("official.circle", current);
        check("circle recipe constrains ellipse without replacing font or effect state", circle.Tool == EditorTool.Ellipse && circle.ConstrainAspect && circle.Style.FontFamily == "Arial" && circle.EffectStrength == 42);
        var rounded = library.Apply("official.rounded-rectangle", current);
        check("rounded recipe uses unified rectangle radius and releases square constraint", rounded.Tool == EditorTool.Rectangle && rounded.CornerRadius == 12 && !rounded.ConstrainAspect);
        check("plain and double arrow recipes remain independent", library.Apply("official.line", current).Style.EndArrowHead == ArrowHeadStyle.None && library.Apply("official.double-arrow", current).Style.StartArrowHead == ArrowHeadStyle.FilledTriangle);
        check("official dashed and dotted recipes preserve custom colors", library.Apply("official.dashed-line", current).Style.StrokePattern == StrokePattern.Dashed && library.Apply("official.dotted-line", current).Style.Stroke == current.Style.Stroke);
        string savedState = JsonSerializer.Serialize(current);
        var saved = library.Save(" My preset ", current); current.Style.Stroke = 0xFFFFFFFF;
        check("user save keeps complete independent parameter snapshot", saved.Name == "My preset" && JsonSerializer.Serialize(saved.Parameters) == savedState && library.User[0].Parameters.Style.Stroke == 0x7F112233);
        var userApplied = library.Apply(saved.Id, current);
        check("user preset defaults to complete saved colors and parameters", JsonSerializer.Serialize(userApplied) == savedState);
        check("explicit user color preservation merges only active colors", library.Apply(saved.Id, current, includeColors: false).Style.Stroke == 0xFFFFFFFF && library.Apply(saved.Id, current, includeColors: false).Style.Fill == current.Style.Fill);
        userApplied.Style.FontSize = 99;
        check("applying a user preset does not mutate library snapshot", library.User[0].Parameters.Style.FontSize == 35);
        saved.Parameters.Style.FontSize = 111;
        check("returned preset objects do not alias library state", library.User[0].Parameters.Style.FontSize == 35);
        library.Rename(saved.Id, "Renamed"); current.EffectStrength = 55; library.Update(saved.Id, current);
        var reloaded = new ToolPresetLibrary(path);
        check("rename and overwrite keep stable preset ID and persist full new parameters", reloaded.User.Single().Id == saved.Id && reloaded.User[0].Name == "Renamed" && reloaded.User[0].Parameters.EffectStrength == 55);
        check("official entries cannot be deleted", !library.Delete("official.line"));
        check("user delete persists and leaves official recipes", reloaded.Delete(saved.Id) && new ToolPresetLibrary(path).User.Count == 0 && reloaded.Official.Count >= 19);
        string invalid = Path.Combine(root, "invalid-presets.json");
        foreach (var json in new[] { "{}", "{\"Version\":99,\"Presets\":[]}", "{\"Version\":1,\"Version\":1,\"Presets\":[]}", "{\"Version\":1,\"Presets\":[],\"Unknown\":true}" })
        {
            File.WriteAllText(invalid, json); bool rejected = false;
            try { _ = new ToolPresetLibrary(invalid); } catch (InvalidDataException) { rejected = true; }
            check("invalid preset library is rejected and preserved: " + json, rejected && File.ReadAllText(invalid) == json);
        }
        var brokenParameters = current.Clone(); brokenParameters.Style.Opacity = double.NaN;
        bool invalidSave = false;
        try { library.Save("bad", brokenParameters); } catch (ArgumentException) { invalidSave = true; }
        check("invalid user state cannot persist a partial preset", invalidSave && library.User.Count == 1);
        string blockedPath = Path.Combine(root, "blocked-presets.json"); Directory.CreateDirectory(blockedPath);
        var blocked = new ToolPresetLibrary(blockedPath); bool ioRejected = false;
        try { blocked.Save("blocked", current); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ioRejected = true; }
        check("failed preset persistence leaves in-memory library unchanged", ioRejected && blocked.User.Count == 0);
    }

    private static void Colors(string root, Action<string, bool> check)
    {
        string path = Path.Combine(root, "colors.json"); var library = new ColorLibrary(path);
        var foreground = ColorLibrary.Palette(ColorRole.Foreground); var background = ColorLibrary.Palette(ColorRole.Background);
        check("both role palettes have ten ordered named colors", foreground.Count == 10 && background.Count == 10 && foreground.Select(c => c.Key).SequenceEqual(new[] { "Black", "White", "Red", "Brown", "Orange", "Yellow", "Blue", "Green", "Purple", "Pink" }));
        check("role palettes supply different matching semantic values", foreground.Single(c => c.Key == "Blue").Value == 0xFF2383E2 && background.Single(c => c.Key == "Blue").Value == 0xFFDCEEFF);
        check("HTML color parser supports three six and eight digit exact ARGB", ColorLibrary.TryParseHtml("#F08", out uint shortHex) && shortHex == 0xFFFF0088 && ColorLibrary.TryParseHtml("336699", out uint rgb) && rgb == 0xFF336699 && ColorLibrary.TryParseHtml("#7F112233", out uint argb) && argb == 0x7F112233);
        check("HTML color parser rejects malformed values", !ColorLibrary.TryParseHtml("#12345", out _) && !ColorLibrary.TryParseHtml("#GG2233", out _) && !ColorLibrary.TryParseHtml("red", out _));
        check("HTML formatting retains exact alpha on request", ColorLibrary.ToHtml(0x7F112233) == "#7F112233" && ColorLibrary.ToHtml(0x7F112233, false) == "#112233");
        for (uint i = 0; i < 25; i++) library.Remember(0x80000000 | i);
        library.Remember(0x8000000A);
        check("history preserves alpha deduplicates recency and bounds its length", library.History.Count == 20 && library.History[0] == 0x8000000A && library.History.Count(c => c == 0x8000000A) == 1);
        library.Pin(0x12345678); library.Pin(0x12345678); library.Pin(0);
        var restored = new ColorLibrary(path);
        check("exact ARGB pinned and history colors persist independently", restored.Pinned.SequenceEqual(new uint[] { 0x12345678, 0 }) && restored.History.SequenceEqual(library.History));
        restored.RemovePinned(0x12345678);
        check("pinned color removal persists", new ColorLibrary(path).Pinned.SequenceEqual(new uint[] { 0 }));
        string invalid = Path.Combine(root, "invalid-colors.json");
        foreach (var json in new[] { "{}", "{\"Version\":1,\"History\":[1,1],\"Pinned\":[]}", "{\"Version\":1,\"History\":[],\"Pinned\":[],\"Unknown\":true}" })
        {
            File.WriteAllText(invalid, json); bool rejected = false;
            try { _ = new ColorLibrary(invalid); } catch (InvalidDataException) { rejected = true; }
            check("invalid color library is rejected without overwrite: " + json, rejected && File.ReadAllText(invalid) == json);
        }
        string blockedPath = Path.Combine(root, "blocked-colors.json"); Directory.CreateDirectory(blockedPath);
        var blocked = new ColorLibrary(blockedPath); bool ioRejected = false;
        try { blocked.Pin(0x12345678); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ioRejected = true; }
        check("failed color persistence leaves in-memory library unchanged", ioRejected && blocked.Pinned.Count == 0);
    }

    private static void Fields(string root, Action<string, bool> check) => Feedback4OptionsTests.Colors(root, "library-fields", check);

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Click(Controls.Button button) => button.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject root)
    {
        yield return root;
        foreach (var child in Wpf.LogicalTreeHelper.GetChildren(root).OfType<Wpf.DependencyObject>()) foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
