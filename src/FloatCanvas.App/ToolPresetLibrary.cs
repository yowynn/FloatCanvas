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
    [JsonIgnore] internal ToolPresetFields Fields { get; set; } = ToolPresetFields.All;
    public ToolPreset Clone() => new() { Id = Id, Name = Name, Parameters = Parameters.Clone(), IsOfficial = IsOfficial, Fields = Fields };
    public override string ToString() => Name;
}

[Flags]
internal enum ToolPresetFields { None = 0, Tool = 1, Line = 2, Corner = 4, Aspect = 8, Symbol = 16, Capture = 32, All = 63 }

/// <summary>Official recipes merge relevant parameters; user presets retain complete independent tool snapshots.</summary>
internal sealed class ToolPresetLibrary
{
    private sealed class State
    {
        public int Version { get; set; } = 1;
        public List<ToolPreset> Presets { get; set; } = [];
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string path;
    private List<ToolPreset> presets = [];

    public ToolPresetLibrary(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? Path.Combine(ApplicationPaths.ProfileDirectory, "tool-presets.json"));
        if (!File.Exists(this.path)) return;
        try
        {
            var state = UserLibraryJson.Read<State>(this.path, JsonOptions, "Preset.InvalidLibrary", "Version", "Presets");
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
        var next = presets.ToList(); next.Remove(moving); next.Insert(next.IndexOf(target) + (after ? 1 : 0), moving); Persist(next);
    }

    public ToolPreset Save(string name, ToolParameters parameters)
    {
        Validate(name, parameters);
        if (presets.Count >= 1000) throw new InvalidDataException(L.Get("Preset.InvalidLibrary"));
        var preset = new ToolPreset { Id = "user-" + Guid.NewGuid().ToString("N"), Name = name.Trim(), Parameters = parameters.Clone() };
        Persist(presets.Append(preset).ToList());
        return preset.Clone();
    }

    public ToolPreset Rename(string id, string name)
    {
        var preset = FindUser(id).Clone(); Validate(name, preset.Parameters); preset.Name = name.Trim();
        Replace(preset); return preset.Clone();
    }

    public ToolPreset Update(string id, ToolParameters parameters)
    {
        var preset = FindUser(id).Clone(); Validate(preset.Name, parameters); preset.Parameters = parameters.Clone();
        Replace(preset); return preset.Clone();
    }

    public bool Delete(string id)
    {
        if (!presets.Any(p => p.Id == id)) return false;
        Persist(presets.Where(p => p.Id != id).ToList()); return true;
    }

    /// <summary>Null applies saved colors for complete user presets and retains current colors for official recipes.</summary>
    public ToolParameters Apply(string id, ToolParameters current, bool? includeColors = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        var preset = All.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException(id);
        ToolParameters result;
        if (!preset.IsOfficial) result = preset.Parameters.Clone();
        else
        {
            result = current.Clone(); var saved = preset.Parameters;
            if (preset.Fields.HasFlag(ToolPresetFields.Tool)) result.Tool = saved.Tool;
            if (preset.Fields.HasFlag(ToolPresetFields.Line))
            {
                result.Style.StartArrowHead = saved.Style.StartArrowHead;
                result.Style.EndArrowHead = saved.Style.EndArrowHead;
                result.Style.ArrowHeadStyle = saved.Style.ArrowHeadStyle;
                result.Style.ArrowHeadSize = saved.Style.ArrowHeadSize;
                result.Style.StrokePattern = saved.Style.StrokePattern;
            }
            if (preset.Fields.HasFlag(ToolPresetFields.Corner)) result.CornerRadius = saved.CornerRadius;
            if (preset.Fields.HasFlag(ToolPresetFields.Aspect)) result.ConstrainAspect = saved.ConstrainAspect;
            if (preset.Fields.HasFlag(ToolPresetFields.Symbol))
            {
                result.Symbol = saved.Symbol;
                result.DefaultWidth = saved.DefaultWidth; result.DefaultHeight = saved.DefaultHeight;
            }
            if (preset.Fields.HasFlag(ToolPresetFields.Capture))
            {
                result.CaptureShape = saved.CaptureShape; result.CaptureSource = saved.CaptureSource;
                result.CaptureBorderWidth = saved.CaptureBorderWidth; result.CaptureBorderColor = saved.CaptureBorderColor;
            }
        }
        if (!(includeColors ?? !preset.IsOfficial)) { result.Style.Stroke = current.Style.Stroke; result.Style.Fill = current.Style.Fill; }
        else if (preset.IsOfficial) { result.Style.Stroke = preset.Parameters.Style.Stroke; result.Style.Fill = preset.Parameters.Style.Fill; }
        return result;
    }

    private ToolPreset FindUser(string id) => presets.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException(id);
    private void Replace(ToolPreset preset) => Persist(presets.Select(p => p.Id == preset.Id ? preset : p).ToList());
    private void Persist(List<ToolPreset> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new State { Presets = next }, JsonOptions));
            File.Move(temporary, path, true); presets = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
        void Add(string id, string key, EditorTool tool, ToolPresetFields fields = ToolPresetFields.Tool, Action<ToolParameters>? configure = null)
        {
            var parameters = new ToolParameters { Tool = tool };
            if (tool == EditorTool.Symbol) parameters.DefaultWidth = parameters.DefaultHeight = 100;
            configure?.Invoke(parameters);
            result.Add(new() { Id = "official." + id, Name = L.Get("Preset." + key), Parameters = parameters, IsOfficial = true, Fields = fields });
        }
        Add("freehand", "Freehand", EditorTool.Freehand); Add("highlighter", "Highlighter", EditorTool.Highlighter);
        void Line(string id, string label, ArrowHeadStyle start, ArrowHeadStyle end, StrokePattern pattern = StrokePattern.Solid) =>
            Add(id, label, EditorTool.Line, ToolPresetFields.Tool | ToolPresetFields.Line,
                p => { p.Style.StartArrowHead = start; p.Style.EndArrowHead = end; p.Style.StrokePattern = pattern; });
        Line("line", "Line", ArrowHeadStyle.None, ArrowHeadStyle.None);
        Line("arrow", "Arrow", ArrowHeadStyle.None, ArrowHeadStyle.FilledTriangle);
        Line("double-arrow", "DoubleArrow", ArrowHeadStyle.FilledTriangle, ArrowHeadStyle.FilledTriangle);
        Line("dashed-line", "DashedLine", ArrowHeadStyle.None, ArrowHeadStyle.None, StrokePattern.Dashed);
        Line("dotted-line", "DottedLine", ArrowHeadStyle.None, ArrowHeadStyle.None, StrokePattern.Dotted);
        var shape = ToolPresetFields.Tool | ToolPresetFields.Aspect;
        Add("rectangle", "Rectangle", EditorTool.Rectangle, shape | ToolPresetFields.Corner, p => p.CornerRadius = 0);
        Add("rounded-rectangle", "RoundedRectangle", EditorTool.Rectangle, shape | ToolPresetFields.Corner, p => p.CornerRadius = 12);
        Add("square", "Square", EditorTool.Rectangle, shape | ToolPresetFields.Corner, p => { p.CornerRadius = 0; p.ConstrainAspect = true; });
        Add("ellipse", "Ellipse", EditorTool.Ellipse, shape);
        Add("circle", "Circle", EditorTool.Ellipse, shape, p => p.ConstrainAspect = true);
        Add("triangle", "Triangle", EditorTool.Symbol, shape | ToolPresetFields.Symbol, p => p.Symbol = SymbolKind.Triangle);
        Add("text", "Text", EditorTool.Text); Add("number", "Number", EditorTool.Number); Add("eraser", "Eraser", EditorTool.Eraser);
        Add("blur", "Blur", EditorTool.Blur); Add("pixelate", "Pixelate", EditorTool.Pixelate);
        Add("select", "Standard", EditorTool.Select); Add("image", "Standard", EditorTool.Image);
        Add("crop", "Standard", EditorTool.Crop); Add("capture", "Standard", EditorTool.CaptureRegion, ToolPresetFields.Tool | ToolPresetFields.Capture);
        Add("emoji", "Standard", EditorTool.Emoji); Add("stamp", "Standard", EditorTool.Stamp);
        // Symbols share one tool; the gallery provides its concrete geometry.
        foreach (var symbol in Enum.GetValues<SymbolKind>().Where(value => value != SymbolKind.Triangle))
        {
            Add("symbol-" + symbol.ToString().ToLowerInvariant(), "Standard", EditorTool.Symbol, shape | ToolPresetFields.Symbol, p => p.Symbol = symbol);
            result[^1].Name = L.Get("Symbol." + symbol);
        }
        return result;
    }
}
