using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class ToolPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ToolParameters Parameters { get; set; } = new();
    [JsonIgnore] public bool IsOfficial { get; internal set; }
    public ToolPreset Clone() => new() { Id = Id, Name = Name, Parameters = Parameters.Clone(), IsOfficial = IsOfficial };
    public override string ToString() => Name;
}

/// <summary>Official and personal presets are complete independent tool snapshots.</summary>
internal sealed class ToolPresetLibrary
{
    private sealed class State
    {
        public int Version { get; set; } = 1;
        public List<ToolPreset> Presets { get; set; } = [];
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string path;
    private readonly CompatibleConfiguration compatibility;
    private List<ToolPreset> presets = [];
    private readonly PresetHistory<ToolPreset> history;
    internal Func<EditorSession>? HistorySession { get; set; }

    public ToolPresetLibrary(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? Path.Combine(ApplicationPaths.ProfileDirectory, "tool-presets.json"));
        compatibility = new(this.path);
        history = new(this.path, preset => preset.Id, preset => preset.Clone(), JsonOptions);
        if (!File.Exists(this.path)) return;
        try
        {
            var state = compatibility.Read<State>(JsonOptions);
            if (state is null || state.Version != 1 || state.Presets is null || state.Presets.Count > 1000 ||
                state.Presets.Any(p => p is null || p.Id is not { Length: > 5 and <= 80 } || !p.Id.StartsWith("user-", StringComparison.Ordinal) ||
                    !ValidName(p.Name) || p.Parameters is null || !p.Parameters.IsValid()) || state.Presets.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != state.Presets.Count)
                throw new InvalidDataException(L.Get("Preset.InvalidLibrary"));
            presets = state.Presets.Select(p => p.Clone()).ToList();
        }
        catch (JsonException ex) { throw new InvalidDataException(L.Get("Preset.InvalidLibrary"), ex); }
    }

    public IReadOnlyList<ToolPreset> Official => CreateOfficial();
    // Keep retired user records on disk while excluding them from executable recipes.
    public IReadOnlyList<ToolPreset> User => presets.Where(p => p.Parameters.Tool != EditorTool.Redact).Select(p => p.Clone()).ToArray();
    public IReadOnlyList<ToolPreset> All => Official.Concat(User).ToArray();
    internal static EditorTool Family(EditorTool tool) => tool switch
    {
        EditorTool.Arrow or EditorTool.DoubleArrow => EditorTool.Line,
        EditorTool.RoundedRectangle => EditorTool.Rectangle,
        EditorTool.Triangle => EditorTool.Symbol,
        _ => tool
    };
    public IReadOnlyList<ToolPreset> OfficialFor(EditorTool tool) => Official.Where(p => Family(p.Parameters.Tool) == Family(tool)).ToArray();
    public IReadOnlyList<ToolPreset> UserFor(EditorTool tool) => User.Where(p => Family(p.Parameters.Tool) == Family(tool)).ToArray();
    public void Move(string id, string beforeId, bool after = false)
    {
        var moving = FindUser(id); var target = FindUser(beforeId);
        if (id == beforeId || Family(moving.Parameters.Tool) != Family(target.Parameters.Tool)) return;
        var next = presets.ToList(); next.Remove(moving); next.Insert(next.IndexOf(target) + (after ? 1 : 0), moving);
        Persist(next, L.Format("PresetHistory.Move", moving.Name), id);
    }

    public ToolPreset Save(string name, ToolParameters parameters)
    {
        Validate(name, parameters);
        if (presets.Count >= 1000) throw new InvalidDataException(L.Get("Preset.InvalidLibrary"));
        var preset = new ToolPreset { Id = "user-" + Guid.NewGuid().ToString("N"), Name = name.Trim(), Parameters = parameters.Clone() };
        Persist(presets.Append(preset).ToList(), L.Format("PresetHistory.Create", preset.Name));
        return preset.Clone();
    }

    public ToolPreset Rename(string id, string name)
    {
        var preset = FindUser(id).Clone(); Validate(name, preset.Parameters); preset.Name = name.Trim();
        Replace(preset, L.Format("PresetHistory.Rename", preset.Name)); return preset.Clone();
    }

    public ToolPreset Update(string id, ToolParameters parameters)
    {
        var preset = FindUser(id).Clone(); Validate(preset.Name, parameters); preset.Parameters = parameters.Clone();
        Replace(preset, L.Format("PresetHistory.Update", preset.Name)); return preset.Clone();
    }

    public bool Delete(string id)
    {
        var preset = presets.FirstOrDefault(p => p.Id == id); if (preset is null) return false;
        Persist(presets.Where(p => p.Id != id).ToList(), L.Format("PresetHistory.Delete", preset.Name)); return true;
    }

    /// <summary>Apply every saved parameter; fields absent from older JSON use model defaults when loaded.</summary>
    public ToolParameters Apply(string id) =>
        (All.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException(id)).Parameters.Clone();

    private ToolPreset FindUser(string id) => presets.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException(id);
    private void Replace(ToolPreset preset, string label) => Persist(presets.Select(p => p.Id == preset.Id ? preset : p).ToList(), label);
    private void Persist(List<ToolPreset> next, string label, string? movedId = null)
    {
        history.Execute(label, presets, next, HistorySession, () => presets, values =>
        {
            if (values.Count > 1000) throw new InvalidDataException(L.Get("Preset.InvalidLibrary"));
            compatibility.Save(new State { Presets = values }, JsonOptions); presets = values;
        }, movedId);
    }
    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 80;
    private static void Validate(string name, ToolParameters parameters)
    {
        if (!ValidName(name)) throw new ArgumentException(L.Get("Preset.InvalidName"), nameof(name));
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.IsValid() || parameters.Tool == EditorTool.Redact) throw new ArgumentException(L.Get("Preset.InvalidLibrary"), nameof(parameters));
    }

    private static IReadOnlyList<ToolPreset> CreateOfficial()
    {
        var result = new List<ToolPreset>();
        void Add(string id, string key, EditorTool tool, Action<ToolParameters>? configure = null)
        {
            var parameters = new ToolParameters { Tool = tool };
            if (tool == EditorTool.Symbol) parameters.DefaultWidth = parameters.DefaultHeight = 72;
            if (tool is EditorTool.Rectangle or EditorTool.Ellipse) { parameters.DefaultWidth = 144; parameters.DefaultHeight = 108; }
            if (tool is EditorTool.Number or EditorTool.Emoji) parameters.Style.FontSize = 72;
            if (tool == EditorTool.Text) parameters.Style.FontSize = 36;
            if (tool == EditorTool.Number) parameters.Style.Fill = 0xFFFFFFFF;
            if (tool == EditorTool.Highlighter) parameters.Style.StrokeWidth = 24;
            configure?.Invoke(parameters);
            if (tool == EditorTool.CaptureRegion) parameters.ConstrainAspect = false;
            result.Add(new() { Id = "official." + id, Name = L.Get("Recipe." + key), Parameters = parameters, IsOfficial = true });
        }
        // Each family starts with its standard form, followed by ready-to-use common combinations.
        // These remain complete snapshots; personal presets and legacy missing-field defaults are untouched.
        Add("freehand", "Freehand", EditorTool.Freehand);
        Add("freehand-fine", "FreehandFine", EditorTool.Freehand, p => p.Style.StrokeWidth = 2);
        Add("freehand-bold", "FreehandBold", EditorTool.Freehand, p => p.Style.StrokeWidth = 12);
        Add("freehand-dotted", "FreehandDotted", EditorTool.Freehand, p => { p.Style.StrokeWidth = 24; p.Style.StrokePattern = StrokePattern.Dotted; });
        Add("highlighter", "Highlighter", EditorTool.Highlighter);
        Add("highlighter-yellow", "HighlighterYellow", EditorTool.Highlighter, p => { p.Style.Stroke = 0xFFCB912F; p.Style.StrokeWidth = 32; });
        Add("highlighter-green", "HighlighterGreen", EditorTool.Highlighter, p => p.Style.Stroke = 0xFF448361);
        void Line(string id, string label, ArrowHeadStyle start, ArrowHeadStyle end, StrokePattern pattern = StrokePattern.Solid) =>
            Add(id, label, EditorTool.Line,
                p => { p.Style.StartArrowHead = start; p.Style.EndArrowHead = end; p.Style.StrokePattern = pattern; });
        Line("line", "Line", ArrowHeadStyle.None, ArrowHeadStyle.None);
        Line("arrow", "Arrow", ArrowHeadStyle.None, ArrowHeadStyle.FilledTriangle);
        Line("double-arrow", "DoubleArrow", ArrowHeadStyle.FilledTriangle, ArrowHeadStyle.FilledTriangle);
        Line("dashed-line", "DashedLine", ArrowHeadStyle.None, ArrowHeadStyle.None, StrokePattern.Dashed);
        Line("dotted-line", "DottedLine", ArrowHeadStyle.None, ArrowHeadStyle.None, StrokePattern.Dotted);
        Add("rectangle", "Rectangle", EditorTool.Rectangle, p => p.CornerRadius = 0);
        Add("rounded-rectangle", "RoundedRectangle", EditorTool.Rectangle, p => p.CornerRadius = 12);
        Add("square", "Square", EditorTool.Rectangle, p => { p.CornerRadius = 0; p.ConstrainAspect = true; p.DefaultWidth = p.DefaultHeight = 108; });
        Add("rectangle-soft", "RectangleSoft", EditorTool.Rectangle, p => { p.CornerRadius = 0; p.Style.Fill = ColorLibrary.BackgroundRed; });
        Add("rectangle-filled", "RectangleFilled", EditorTool.Rectangle, p => { p.CornerRadius = 0; p.Style.Fill = ColorLibrary.BackgroundRed; p.Style.LinkColors = true; });
        Add("circle", "Circle", EditorTool.Ellipse, p => { p.ConstrainAspect = true; p.DefaultWidth = p.DefaultHeight = 108; });
        Add("ellipse", "Ellipse", EditorTool.Ellipse);
        Add("circle-soft", "CircleSoft", EditorTool.Ellipse, p => { p.ConstrainAspect = true; p.DefaultWidth = p.DefaultHeight = 108; p.Style.Fill = ColorLibrary.BackgroundRed; });
        Add("circle-filled", "CircleFilled", EditorTool.Ellipse, p => { p.ConstrainAspect = true; p.DefaultWidth = p.DefaultHeight = 108; p.Style.Fill = ColorLibrary.BackgroundRed; p.Style.LinkColors = true; });
        // The shape picker applies every style to any geometry in one click.
        // Keep one canonical shape rather than repeating a recipe for each symbol.
        Add("triangle", "SymbolOutline", EditorTool.Symbol);
        Add("symbol-soft", "SymbolSoft", EditorTool.Symbol, p => p.Style.Fill = ColorLibrary.BackgroundRed);
        Add("symbol-filled", "SymbolFilled", EditorTool.Symbol, p => { p.Style.LinkColors = true; p.Style.Fill = ColorLibrary.BackgroundRed; });
        Add("symbol-dashed", "SymbolDashed", EditorTool.Symbol, p => p.Style.StrokePattern = StrokePattern.Dashed);
        Add("symbol-green", "SymbolGreen", EditorTool.Symbol, p => { p.Style.Stroke = 0xFF448361; p.Style.StrokeWidth = 6; });
        Add("text", "Text", EditorTool.Text);
        Add("text-heading", "TextHeading", EditorTool.Text, p => p.Style.FontSize = 48);
        Add("text-body", "TextBody", EditorTool.Text, p => { p.Style.Stroke = 0xFF252525; p.Style.FontSize = 24; });
        Add("text-label", "TextLabel", EditorTool.Text, p => { p.Style.Stroke = 0xFF2383E2; p.Style.FontSize = 28; p.Style.TextAlignment = TextAlignment.Center; });
        Add("number", "Number", EditorTool.Number);
        Add("number-blue", "NumberBlue", EditorTool.Number, p => { p.Style.Stroke = 0xFF2383E2; p.Style.FontSize = 48; });
        Add("number-compact", "NumberCompact", EditorTool.Number, p => { p.Style.Stroke = 0xFF252525; p.Style.FontSize = 36; });
        Add("eraser", "Eraser", EditorTool.Eraser);
        Add("blur", "Blur", EditorTool.Blur);
        Add("blur-fine", "BlurFine", EditorTool.Blur, p => p.EffectStrength = 6);
        Add("blur-strong", "BlurStrong", EditorTool.Blur, p => p.EffectStrength = 24);
        Add("pixelate", "Pixelate", EditorTool.Pixelate);
        Add("pixelate-fine", "PixelateFine", EditorTool.Pixelate, p => p.EffectStrength = 6);
        Add("pixelate-strong", "PixelateStrong", EditorTool.Pixelate, p => p.EffectStrength = 24);
        Add("select", "Select", EditorTool.Select);
        Add("image", "Image", EditorTool.Image);
        Add("image-half", "ImageHalf", EditorTool.Image, p => p.Style.Opacity = .5);
        Add("image-faint", "ImageFaint", EditorTool.Image, p => p.Style.Opacity = .25);
        Add("crop", "Crop", EditorTool.Crop);
        Add("crop-square", "CropSquare", EditorTool.Crop, p => p.ConstrainAspect = true);
        Add("capture", "Capture", EditorTool.CaptureRegion);
        Add("capture-ellipse", "CaptureEllipse", EditorTool.CaptureRegion, p => p.CaptureShape = CaptureShape.Ellipse);
        Add("capture-annotated", "CaptureAnnotated", EditorTool.CaptureRegion, p => p.CaptureSource = CaptureSource.BackgroundAndCanvas);
        Add("capture-bordered", "CaptureBordered", EditorTool.CaptureRegion, p => p.CaptureBorderWidth = 4);
        Add("capture-lasso", "CaptureLasso", EditorTool.CaptureRegion, p => p.CaptureShape = CaptureShape.FreehandLasso);
        Add("capture-polygon", "CapturePolygon", EditorTool.CaptureRegion, p => p.CaptureShape = CaptureShape.PolygonLasso);
        Add("emoji", "Emoji", EditorTool.Emoji);
        Add("emoji-thumb", "EmojiThumb", EditorTool.Emoji, p => p.Emoji = "👍");
        Add("emoji-check", "EmojiCheck", EditorTool.Emoji, p => p.Emoji = "✅");
        Add("emoji-warning", "EmojiWarning", EditorTool.Emoji, p => p.Emoji = "⚠️");
        Add("stamp", "Stamp", EditorTool.Stamp);
        return result;
    }
}
