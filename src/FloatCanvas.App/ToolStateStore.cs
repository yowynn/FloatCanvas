using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Last working tool state, kept separately from immutable recipes and documents.</summary>
internal sealed class ToolStateStore
{
    private sealed class State
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public Dictionary<EditorTool, ToolParameters> Tools { get; set; } = [];
        [JsonRequired] public Dictionary<EditorTool, EditorTool> Buttons { get; set; } = [];
        [JsonRequired] public Dictionary<EditorTool, string> Selections { get; set; } = [];
        [JsonRequired] public Dictionary<string, ToolParameters> OfficialBaselines { get; set; } = [];
        [JsonRequired] public Dictionary<EditorTool, ToolParameters> PreviewSources { get; set; } = [];
    }

    private const string ErrorKey = "ToolState.InvalidCache";
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly string path;
    private string savedFingerprint;

    public Dictionary<EditorTool, ToolParameters> Tools { get; }
    public Dictionary<EditorTool, EditorTool> Buttons { get; }
    public Dictionary<EditorTool, string> Selections { get; }
    public Dictionary<string, ToolParameters> OfficialBaselines { get; }
    public Dictionary<EditorTool, ToolParameters> PreviewSources { get; }
    internal event Action? Changed;

    public ToolStateStore(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? Path.Combine(ApplicationPaths.ProfileDirectory, "tool-state.json"));
        State state = new();
        if (File.Exists(this.path))
        {
            state = UserLibraryJson.Read<State>(this.path, JsonOptions, ErrorKey,
                "Version", "Tools", "Buttons", "Selections", "OfficialBaselines", "PreviewSources") ?? throw Invalid();
            // Enum dictionary keys accept numeric aliases in System.Text.Json. Require
            // canonical names before accepting the deserialized dictionaries.
            using var raw = JsonDocument.Parse(File.ReadAllText(this.path));
            foreach (string key in new[] { "Tools", "Buttons", "Selections", "PreviewSources" })
            {
                var dictionary = raw.RootElement.GetProperty(key);
                if (dictionary.ValueKind != JsonValueKind.Object) throw Invalid();
                foreach (var entry in dictionary.EnumerateObject())
                    if (!Enum.TryParse<EditorTool>(entry.Name, out var tool) || !Enum.IsDefined(tool) || entry.Name != tool.ToString()) throw Invalid();
            }
            Validate(state);
        }
        Tools = state.Tools; Buttons = state.Buttons; Selections = state.Selections;
        OfficialBaselines = state.OfficialBaselines; PreviewSources = state.PreviewSources;
        savedFingerprint = JsonSerializer.Serialize(state, JsonOptions);
    }

    internal void MarkChanged() => Changed?.Invoke();

    public void Save()
    {
        var state = new State { Tools = Tools, Buttons = Buttons, Selections = Selections,
            OfficialBaselines = OfficialBaselines, PreviewSources = PreviewSources };
        Validate(state);
        string serialized = JsonSerializer.Serialize(state, JsonOptions);
        if (serialized == savedFingerprint) return;
        if (System.Text.Encoding.UTF8.GetByteCount(serialized) > MaximumBytes) throw Invalid();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, serialized);
            File.Move(temporary, path, true);
            savedFingerprint = serialized;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(State state)
    {
        int families = Enum.GetValues<EditorTool>().Count(tool => ToolPresetLibrary.Family(tool) == tool);
        if (state.Version != 1 || state.Tools is null || state.Buttons is null || state.Selections is null ||
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
    { EditorTool.Crop or EditorTool.Pixelate or EditorTool.Redact => EditorTool.Blur, var family => family };
    private static bool ValidPresetId(string? id, bool officialOnly = false) => id is { Length: <= 80 } &&
        (id.StartsWith("official.", StringComparison.Ordinal) && id.Length > 9 || !officialOnly && id.StartsWith("user-", StringComparison.Ordinal) && id.Length > 5) &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    private static InvalidDataException Invalid() => new(L.Get(ErrorKey));

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type != typeof(ToolParameters) && type.Type != typeof(ElementStyle)) return;
            foreach (var property in type.Properties)
                // Version-one caches predate capture options. Missing new fields inherit their
                // explicit defaults; all original fields remain required and unknown fields rejected.
                property.IsRequired = type.Type != typeof(ToolParameters) || property.Name is not
                    (nameof(ToolParameters.CaptureShape) or nameof(ToolParameters.CaptureSource) or
                     nameof(ToolParameters.CaptureBorderWidth) or nameof(ToolParameters.CaptureBorderColor));
        });
        return new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, TypeInfoResolver = resolver };
    }
}
