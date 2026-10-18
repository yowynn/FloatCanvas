using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Portable canvas values. Screen corners and calibration matrices stay with the document.</summary>
internal sealed class CanvasPresetParameters
{
    public uint BackgroundColor { get; set; }
    public bool ProjectionEnabled { get; set; }
    public int Columns { get; set; } = 8;
    public int Rows { get; set; } = 6;
    public bool GridVisible { get; set; }
    public uint GridColor { get; set; } = 0x80FFFFFF;
    public double GridWidth { get; set; } = 1;
    public StrokePattern GridPattern { get; set; }
    internal bool IsValid => Columns is >= 1 and <= CanvasCalibration.MaximumGridCount &&
        Rows is >= 1 and <= CanvasCalibration.MaximumGridCount && double.IsFinite(GridWidth) && GridWidth is > 0 and <= 64 && Enum.IsDefined(GridPattern);
    internal CanvasPresetParameters Clone() => (CanvasPresetParameters)MemberwiseClone();
    internal bool Matches(CanvasPresetParameters other) => BackgroundColor == other.BackgroundColor && ProjectionEnabled == other.ProjectionEnabled &&
        Columns == other.Columns && Rows == other.Rows && GridVisible == other.GridVisible && GridColor == other.GridColor &&
        GridWidth == other.GridWidth && GridPattern == other.GridPattern;
    internal static CanvasPresetParameters From(CanvasSettings settings) => new()
    {
        BackgroundColor = settings.BackgroundColor, ProjectionEnabled = settings.Projection.Enabled,
        Columns = settings.Projection.Columns, Rows = settings.Projection.Rows, GridVisible = settings.Grid.Visible,
        GridColor = settings.Grid.Color, GridWidth = settings.Grid.Width, GridPattern = settings.Grid.Pattern
    };
}

internal sealed class CanvasPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public CanvasPresetParameters Parameters { get; set; } = new();
    [JsonIgnore] public bool IsOfficial { get; internal set; }
    internal CanvasPreset Clone() => new() { Id = Id, Name = Name, Parameters = Parameters.Clone(), IsOfficial = IsOfficial };
}

internal sealed class CanvasPresetLibrary
{
    private sealed class State
    {
        public int Version { get; set; } = 1;
        public List<CanvasPreset> Presets { get; set; } = [];
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string path;
    private List<CanvasPreset> presets = [];
    internal bool CanEdit { get; } = true;
    internal static CanvasPresetLibrary OfficialOnly() => new(readOnly: true);
    private CanvasPresetLibrary(bool readOnly) { path = ""; CanEdit = !readOnly; }

    internal CanvasPresetLibrary(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? Path.Combine(ApplicationPaths.ProfileDirectory, "canvas-presets.json"));
        if (!File.Exists(this.path)) return;
        try
        {
            var state = UserLibraryJson.Read<State>(this.path, JsonOptions, "CanvasPreset.InvalidLibrary", "Version", "Presets");
            if (state is null || state.Version != 1 || state.Presets is null || state.Presets.Count > 1000 ||
                state.Presets.Any(p => p is null || p.Id is not { Length: > 5 and <= 80 } || !p.Id.StartsWith("user-", StringComparison.Ordinal) ||
                    !ValidName(p.Name) || p.Parameters is null || !p.Parameters.IsValid) ||
                state.Presets.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != state.Presets.Count)
                throw new InvalidDataException(L.Get("CanvasPreset.InvalidLibrary"));
            presets = state.Presets.Select(p => p.Clone()).ToList();
        }
        catch (JsonException ex) { throw new InvalidDataException(L.Get("CanvasPreset.InvalidLibrary"), ex); }
    }

    internal IReadOnlyList<CanvasPreset> Official =>
    [
        OfficialPreset("transparent", new()),
        OfficialPreset("whiteboard", new() { BackgroundColor = 0xFFFFFFFF, GridColor = 0x7047618A }),
        OfficialPreset("blackboard", new() { BackgroundColor = 0xFF20242C }),
        OfficialPreset("graph", new() { BackgroundColor = 0xFFF4F8FF, GridVisible = true, GridColor = 0x705A84C8 }),
        OfficialPreset("dots", new() { GridVisible = true, GridColor = 0xA0FFFFFF, GridPattern = StrokePattern.Dotted })
    ];
    internal IReadOnlyList<CanvasPreset> User => presets.Select(p => p.Clone()).ToArray();
    internal IReadOnlyList<CanvasPreset> All => Official.Concat(User).ToArray();

    internal CanvasPreset Save(string name, CanvasSettings settings)
    {
        var parameters = CanvasPresetParameters.From(settings); Validate(name, parameters);
        if (presets.Count >= 1000) throw new InvalidDataException(L.Get("CanvasPreset.InvalidLibrary"));
        var preset = new CanvasPreset { Id = "user-" + Guid.NewGuid().ToString("N"), Name = name.Trim(), Parameters = parameters };
        Persist(presets.Append(preset).ToList()); return preset.Clone();
    }
    internal void Rename(string id, string name)
    {
        var preset = FindUser(id).Clone(); Validate(name, preset.Parameters); preset.Name = name.Trim(); Replace(preset);
    }
    internal void Update(string id, CanvasSettings settings)
    {
        var preset = FindUser(id).Clone(); var parameters = CanvasPresetParameters.From(settings);
        Validate(preset.Name, parameters); preset.Parameters = parameters; Replace(preset);
    }
    internal bool Delete(string id)
    {
        if (!presets.Any(p => p.Id == id)) return false;
        Persist(presets.Where(p => p.Id != id).ToList()); return true;
    }
    internal void Move(string id, string targetId, bool after = false)
    {
        var moving = FindUser(id); var target = FindUser(targetId);
        if (moving == target) return;
        var next = presets.ToList(); next.Remove(moving); next.Insert(next.IndexOf(target) + (after ? 1 : 0), moving); Persist(next);
    }

    internal CanvasSettings Apply(string id, CanvasSettings current, RectD referenceViewport, RectD viewport)
    {
        var parameters = All.FirstOrDefault(p => p.Id == id)?.Parameters ?? throw new KeyNotFoundException(id);
        return Apply(parameters, current, referenceViewport, viewport);
    }
    internal static CanvasSettings Apply(CanvasPresetParameters parameters, CanvasSettings current, RectD referenceViewport, RectD viewport)
    {
        Validate("preset", parameters);
        var result = current.Clone(); var projection = result.Projection;
        if (projection.Quad.Count == 0)
        {
            if (parameters.ProjectionEnabled || parameters.GridVisible)
                projection = CanvasCalibration.CreateInitial(referenceViewport, parameters.Columns, parameters.Rows);
            else { projection.Columns = parameters.Columns; projection.Rows = parameters.Rows; }
        }
        else if (projection.Columns != parameters.Columns || projection.Rows != parameters.Rows)
        {
            projection.Columns = parameters.Columns; projection.Rows = parameters.Rows;
            if (!CanvasCalibration.TryCalibrate(projection, projection.Quad, viewport, out var calibrated, out var error))
                throw new InvalidDataException(L.Get(error!));
            projection = calibrated;
        }
        if (parameters.ProjectionEnabled && !CanvasCalibration.IsStableViewport(projection.Matrix, viewport))
            throw new InvalidDataException(L.Get("Canvas.UnstableProjection"));
        projection.Enabled = parameters.ProjectionEnabled;
        result.BackgroundColor = parameters.BackgroundColor; result.Projection = projection;
        result.Grid = new() { Visible = parameters.GridVisible, Color = parameters.GridColor, Width = parameters.GridWidth, Pattern = parameters.GridPattern };
        SceneValidation.Validate(new SceneDocument { Canvas = result, Viewport = viewport });
        return result;
    }

    private static CanvasPreset OfficialPreset(string id, CanvasPresetParameters parameters) => new()
    { Id = "official.canvas-" + id, Name = L.Get("CanvasPreset." + id), Parameters = parameters, IsOfficial = true };
    private CanvasPreset FindUser(string id) => presets.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException(id);
    private void Replace(CanvasPreset preset) => Persist(presets.Select(p => p.Id == preset.Id ? preset : p).ToList());
    private void Persist(List<CanvasPreset> next)
    {
        if (!CanEdit) throw new InvalidOperationException(L.Get("CanvasPreset.ReadOnly"));
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
    private static void Validate(string name, CanvasPresetParameters parameters)
    {
        if (!ValidName(name)) throw new ArgumentException(L.Get("Preset.InvalidName"), nameof(name));
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.IsValid) throw new ArgumentException(L.Get("CanvasPreset.InvalidLibrary"), nameof(parameters));
    }
}
