using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Last working tool state, kept separately from complete presets and documents.</summary>
internal sealed class ToolStateStore
{
    private sealed class State
    {
        public string CanvasPresetSelection { get; set; } = "";
        [JsonRequired] public int Version { get; set; } = 3;
        [JsonRequired] public Dictionary<EditorTool, ToolParameters> Tools { get; set; } = [];
        [JsonRequired] public Dictionary<EditorTool, EditorTool> Buttons { get; set; } = [];
        [JsonRequired] public Dictionary<EditorTool, string> Selections { get; set; } = [];
        [JsonRequired] public Dictionary<string, ToolParameters> OfficialBaselines { get; set; } = [];
        [JsonRequired] public Dictionary<EditorTool, ToolParameters> PreviewSources { get; set; } = [];
    }

    private const string ErrorKey = "ToolState.InvalidCache";
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented=true };
    private readonly string path;
    private readonly CompatibleConfiguration compatibility;
    private string savedFingerprint;

    internal string CanvasPresetSelection { get; set; } = "";
    public Dictionary<EditorTool, ToolParameters> Tools { get; }
    public Dictionary<EditorTool, EditorTool> Buttons { get; }
    public Dictionary<EditorTool, string> Selections { get; }
    // Retain these legacy dictionaries for configuration round-tripping only.
    // Official applications and previews always use the complete library definition.
    public Dictionary<string, ToolParameters> OfficialBaselines { get; }
    public Dictionary<EditorTool, ToolParameters> PreviewSources { get; }
    internal event Action? Changed;
    internal bool LegacyKeyboardScopeMigrated { get; }

    public ToolStateStore(string? path = null, PresentationKeyboardScope legacyKeyboardScope = PresentationKeyboardScope.AllApplications)
    {
        this.path = Path.GetFullPath(path ?? Path.Combine(ApplicationPaths.ProfileDirectory, "tool-state.json"));
        compatibility = new(this.path);
        State state = new();
        if (File.Exists(this.path))
        {
            try { state = compatibility.Read<State>(JsonOptions); }
            catch (JsonException ex) { throw new InvalidDataException(L.Get(ErrorKey),ex); }
            foreach(var key in state.Tools.Where(p=>!ValidParameters(p.Key,p.Value)).Select(p=>p.Key).ToArray())state.Tools.Remove(key);
            foreach(var key in state.PreviewSources.Where(p=>!ValidParameters(p.Key,p.Value)).Select(p=>p.Key).ToArray())state.PreviewSources.Remove(key);
            foreach(var key in state.Buttons.Where(p=>!IsFamily(p.Key)||ButtonFamily(p.Value)!=p.Key).Select(p=>p.Key).ToArray())state.Buttons.Remove(key);
            foreach(var key in state.Selections.Where(p=>!IsFamily(p.Key)||!ValidPresetId(p.Value)).Select(p=>p.Key).ToArray())state.Selections.Remove(key);
            foreach(var key in state.OfficialBaselines.Where(p=>!ValidPresetId(p.Key,true)||!p.Value.IsValid()).Select(p=>p.Key).ToArray())state.OfficialBaselines.Remove(key);
            Validate(state);
            state.Version = 3;
            compatibility.Capture(state,JsonOptions);
        }
        CanvasPresetSelection = state.CanvasPresetSelection;
        Tools = state.Tools; Buttons = state.Buttons; Selections = state.Selections;
        OfficialBaselines = state.OfficialBaselines; PreviewSources = state.PreviewSources;
        savedFingerprint = JsonSerializer.Serialize(state, JsonOptions);
        foreach (var parameters in Tools.Values.Concat(PreviewSources.Values)) parameters.NormalizeForCreation();
        foreach (var key in Selections.Keys.ToArray()) Selections[key] = ToolPresetLibrary.ResolveOfficialId(Selections[key]);
        if (legacyKeyboardScope != PresentationKeyboardScope.AllApplications && Enum.IsDefined(legacyKeyboardScope) && !HasAuthoredKeyboardScope(this.path))
        {
            if (!Tools.TryGetValue(EditorTool.Presentation, out var keyboard))
                Tools[EditorTool.Presentation] = keyboard = ToolPresetLibrary.OfficialDefault(EditorTool.Presentation);
            (keyboard.Presentation ??= new()).KeyboardScope = legacyKeyboardScope;
            LegacyKeyboardScopeMigrated = true;
        }
    }

    private static bool HasAuthoredKeyboardScope(string path)
    {
        if (!File.Exists(path)) return false;
        using var source = JsonDocument.Parse(File.ReadAllText(path), new() { MaxDepth = 128 });
        return source.RootElement.TryGetProperty("Tools", out var tools) && tools.ValueKind == JsonValueKind.Object &&
            tools.TryGetProperty(nameof(EditorTool.Presentation), out var keyboard) && keyboard.ValueKind == JsonValueKind.Object &&
            keyboard.TryGetProperty(nameof(ToolParameters.Presentation), out var settings) && settings.ValueKind == JsonValueKind.Object &&
            settings.TryGetProperty(nameof(PresentationSettings.KeyboardScope), out _);
    }

    internal void MarkChanged() => Changed?.Invoke();

    public void Save()
    {
        foreach (var parameters in Tools.Values.Concat(OfficialBaselines.Values).Concat(PreviewSources.Values)) parameters.NormalizeAppearance();
        foreach (var parameters in Tools.Values.Concat(PreviewSources.Values)) parameters.NormalizeForCreation();
        var state = new State { CanvasPresetSelection = CanvasPresetSelection, Tools = Tools, Buttons = Buttons, Selections = Selections,
            OfficialBaselines = OfficialBaselines, PreviewSources = PreviewSources };
        Validate(state);
        string serialized = JsonSerializer.Serialize(state, JsonOptions);
        if (serialized == savedFingerprint && !compatibility.NeedsCleanup) return;
        if (System.Text.Encoding.UTF8.GetByteCount(serialized) > MaximumBytes) throw Invalid();
        compatibility.Save(state,JsonOptions);
        savedFingerprint = serialized;
    }

    private static void Validate(State state)
    {
        int families = Enum.GetValues<EditorTool>().Count(tool => ToolPresetLibrary.Family(tool) == tool);
        if (state.Version is < 1 or > 3 || state.Tools is null || state.Buttons is null || state.Selections is null ||
            state.OfficialBaselines is null || state.PreviewSources is null || state.Tools.Count > families ||
            state.Buttons.Count > families || state.Selections.Count > families || state.PreviewSources.Count > families ||
            state.OfficialBaselines.Count > 256 ||
            state.Tools.Any(entry => !ValidParameters(entry.Key, entry.Value)) ||
            state.PreviewSources.Any(entry => !ValidParameters(entry.Key, entry.Value)) ||
            state.Selections.Any(entry => !IsFamily(entry.Key) || !ValidPresetId(entry.Value)) ||
            state.Buttons.Any(entry => !IsFamily(entry.Key) || !Enum.IsDefined(entry.Value) || ButtonFamily(entry.Value) != entry.Key) ||
            state.OfficialBaselines.Any(entry => !ValidPresetId(entry.Key, officialOnly: true) || entry.Value is null || !entry.Value.IsValid())) throw Invalid();
    }

    private static bool ValidParameters(EditorTool key, ToolParameters? parameters) =>
        IsFamily(key) && parameters is not null && parameters.IsValid() && ToolPresetLibrary.Family(parameters.Tool) == key;
    private static bool IsFamily(EditorTool tool) => Enum.IsDefined(tool) && ToolPresetLibrary.Family(tool) == tool;
    private static EditorTool ButtonFamily(EditorTool tool) => ToolPresetLibrary.Family(tool) switch
    { EditorTool.Crop or EditorTool.Pixelate or EditorTool.Presentation or EditorTool.PointerPresentation => EditorTool.Blur, var family => family };
    private static bool ValidPresetId(string? id, bool officialOnly = false) => id is { Length: <= 80 } &&
        (id.StartsWith("official.", StringComparison.Ordinal) && id.Length > 9 || !officialOnly && id.StartsWith("user-", StringComparison.Ordinal) && id.Length > 5) &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    private static InvalidDataException Invalid() => new(L.Get(ErrorKey));

}
