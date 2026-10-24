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
    private readonly Stack<List<ToolPreset>> undo = new();
    private bool restoringResource;
    internal bool CanUndoResourceChange => undo.Count > 0;
    internal void UndoResourceChange()
    {
        if (!undo.TryPeek(out var previous)) return;
        restoringResource = true; try { Persist(previous.Select(p => p.Clone()).ToList()); undo.Pop(); } finally { restoringResource = false; }
    }
    private void RememberResourceChange(List<ToolPreset> previous) { undo.Push(previous); if (undo.Count > 20) { var keep = undo.Take(20).Reverse().ToArray(); undo.Clear(); foreach (var value in keep) undo.Push(value); } }

    public ToolPresetLibrary(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? Path.Combine(ApplicationPaths.ProfileDirectory, "tool-presets.json"));
        compatibility = new(this.path);
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

    /// <summary>Apply every saved parameter; fields absent from older JSON use model defaults when loaded.</summary>
    public ToolParameters Apply(string id) =>
        (All.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException(id)).Parameters.Clone();

    private ToolPreset FindUser(string id) => presets.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException(id);
    private void Replace(ToolPreset preset) => Persist(presets.Select(p => p.Id == preset.Id ? preset : p).ToList());
    private void Persist(List<ToolPreset> next)
    {
        var previous = presets.Select(p => p.Clone()).ToList();
        compatibility.Save(new State { Presets = next },JsonOptions); presets = next;
        if (!restoringResource) RememberResourceChange(previous);
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
            result.Add(new() { Id = "official." + id, Name = L.Get("Preset." + key), Parameters = parameters, IsOfficial = true });
        }
        Add("freehand", "Freehand", EditorTool.Freehand); Add("highlighter", "Highlighter", EditorTool.Highlighter);
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
        Add("ellipse", "Ellipse", EditorTool.Ellipse);
        Add("circle", "Circle", EditorTool.Ellipse, p => { p.ConstrainAspect = true; p.DefaultWidth = p.DefaultHeight = 108; });
        Add("triangle", "Triangle", EditorTool.Symbol, p => p.Symbol = SymbolKind.Triangle);
        Add("text", "Text", EditorTool.Text); Add("number", "Number", EditorTool.Number); Add("eraser", "Eraser", EditorTool.Eraser);
        Add("blur", "Blur", EditorTool.Blur); Add("pixelate", "Pixelate", EditorTool.Pixelate);
        Add("select", "Standard", EditorTool.Select); Add("image", "Standard", EditorTool.Image);
        Add("crop", "Standard", EditorTool.Crop); Add("capture", "Standard", EditorTool.CaptureRegion);
        Add("emoji", "Standard", EditorTool.Emoji); Add("stamp", "Standard", EditorTool.Stamp);
        // Symbols share one tool; the gallery provides its concrete geometry.
        foreach (var symbol in Enum.GetValues<SymbolKind>().Where(value => value != SymbolKind.Triangle))
        {
            Add("symbol-" + symbol.ToString().ToLowerInvariant(), "Standard", EditorTool.Symbol, p => p.Symbol = symbol);
            result[^1].Name = L.Get("Symbol." + symbol);
        }
        return result;
    }
}
