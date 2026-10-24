using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Canvas values, with an optional exact calibration snapshot for personal presets.</summary>
internal sealed class CanvasPresetParameters
{
    [JsonIgnore] internal bool ResetProjection { get; set; }
    public uint BackgroundColor { get; set; }
    public bool ProjectionEnabled { get; set; }
    public int Columns { get; set; } = 8;
    public int Rows { get; set; } = 6;
    public bool GridVisible { get; set; }
    public uint GridColor { get; set; } = 0x80FFFFFF;
    public double GridWidth { get; set; } = 1;
    public StrokePattern GridPattern { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FlatCellSize { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CanvasProjectionSettings? Calibration { get; set; }
    internal bool IsValid => Columns is >= 1 and <= CanvasCalibration.MaximumGridCount &&
        Rows is >= 1 and <= CanvasCalibration.MaximumGridCount && double.IsFinite(GridWidth) && GridWidth is > 0 and <= 64 && Enum.IsDefined(GridPattern) &&
        (FlatCellSize is null || double.IsFinite(FlatCellSize.Value) && FlatCellSize is >= CanvasGridGeometry.MinimumCellSize and <= CanvasGridGeometry.MaximumCellSize) && ValidCalibration();
    internal CanvasPresetParameters Clone()
    {
        var copy = (CanvasPresetParameters)MemberwiseClone(); copy.Calibration = Calibration?.Clone(); return copy;
    }
    internal bool Matches(CanvasPresetParameters other) => BackgroundColor == other.BackgroundColor && ProjectionEnabled == other.ProjectionEnabled &&
        Columns == other.Columns && Rows == other.Rows && GridVisible == other.GridVisible && GridColor == other.GridColor &&
        GridWidth == other.GridWidth && GridPattern == other.GridPattern && (FlatCellSize is null || FlatCellSize == other.FlatCellSize) && (Calibration is null || other.Calibration is { } c &&
            Calibration.Enabled == c.Enabled && Calibration.Columns == c.Columns && Calibration.Rows == c.Rows &&
            Calibration.Origin == c.Origin && Calibration.CellSize == c.CellSize && Calibration.Matrix == c.Matrix &&
            Calibration.AlgorithmVersion == c.AlgorithmVersion && Calibration.Quad.SequenceEqual(c.Quad));
    internal static CanvasPresetParameters From(CanvasSettings settings) => new()
    {
        BackgroundColor = settings.BackgroundColor, ProjectionEnabled = settings.Projection.Enabled,
        Columns = settings.Projection.Columns, Rows = settings.Projection.Rows, GridVisible = settings.Grid.Visible,
        GridColor = settings.Grid.Color, GridWidth = settings.Grid.Width, GridPattern = settings.Grid.Pattern,
        // Presets omit the adjustment lock, so an enabled preset saves its unlocked spacing.
        FlatCellSize = settings.Projection.Enabled && settings.Projection.Quad.Count == 4
            ? settings.Projection.CellSize
            : settings.Grid.FlatCellSize ?? (settings.Projection.Quad.Count == 4 ? settings.Projection.GridCellSize : null),
        Calibration = settings.Projection.Quad.Count == 0 ? null : settings.Projection.Clone()
    };
    private bool ValidCalibration()
    {
        if (Calibration is null) return true;
        if (Calibration.Quad is not { Count: 4 } || Calibration.Columns != Columns || Calibration.Rows != Rows || Calibration.Enabled != ProjectionEnabled) return false;
        // Disabled calibrations remain restorable after a desktop change. Apply checks the actual viewport before enabling.
        var projection = Calibration.Clone(); projection.Enabled = false;
        try { SceneValidation.Validate(new SceneDocument { Canvas = new() { Projection = projection } }); return true; }
        catch (InvalidDataException) { return false; }
    }
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
        public int Version { get; set; } = 3;
        public List<CanvasPreset> Presets { get; set; } = [];
    }
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string path;
    private readonly CompatibleConfiguration compatibility;
    private List<CanvasPreset> presets = [];
    private readonly Stack<List<CanvasPreset>> undo = new();
    private bool restoringResource;
    internal bool CanUndoResourceChange => undo.Count > 0;
    internal void UndoResourceChange()
    {
        if (!undo.TryPeek(out var previous)) return;
        restoringResource = true; try { Persist(previous.Select(p => p.Clone()).ToList()); undo.Pop(); } finally { restoringResource = false; }
    }
    private void RememberResourceChange(List<CanvasPreset> previous) { undo.Push(previous); if (undo.Count > 20) { var keep = undo.Take(20).Reverse().ToArray(); undo.Clear(); foreach (var value in keep) undo.Push(value); } }
    internal bool CanEdit { get; } = true;
    internal static CanvasPresetLibrary OfficialOnly() => new(readOnly: true);
    private CanvasPresetLibrary(bool readOnly) { path = ""; compatibility=new(path); CanEdit = !readOnly; }

    internal CanvasPresetLibrary(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? Path.Combine(ApplicationPaths.ProfileDirectory, "canvas-presets.json"));
        compatibility=new(this.path);
        if (!File.Exists(this.path)) return;
        try
        {
            var state = compatibility.Read<State>(JsonOptions);
            foreach(var preset in state.Presets.Where(p=>p?.Parameters is not null && !p.Parameters.IsValid && p.Parameters.Calibration is not null))
                preset.Parameters.Calibration=null;
            if (state is null || state.Version is not (1 or 2 or 3) || state.Presets is null || state.Presets.Count > 1000 ||
                state.Presets.Any(p => p is null || p.Id is not { Length: > 5 and <= 80 } || !p.Id.StartsWith("user-", StringComparison.Ordinal) ||
                    !ValidName(p.Name) || p.Parameters is null || !p.Parameters.IsValid) ||
                state.Presets.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != state.Presets.Count)
                throw new InvalidDataException(L.Get("CanvasPreset.InvalidLibrary"));
            presets = state.Presets.Select(p => p.Clone()).ToList();
            compatibility.Capture(state,JsonOptions);
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
        if (!parameters.ProjectionEnabled && parameters.Calibration is null && parameters.FlatCellSize is { } flatSize)
            projection = CanvasGridGeometry.CreateFlatReference(referenceViewport, parameters.Columns, parameters.Rows, flatSize);
        else if (parameters.ResetProjection) projection = CanvasCalibration.CreateInitial(referenceViewport, parameters.Columns, parameters.Rows);
        else if (parameters.Calibration is { } snapshot) projection = snapshot.Clone();
        else if (projection.Quad.Count == 0)
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
        if (parameters.ProjectionEnabled && !CanvasCalibration.IsValidProjection(projection.Matrix))
            throw new InvalidDataException(L.Get("Canvas.UnstableProjection"));
        // Applying a recipe establishes a fresh calibration reference, like Reset.
        projection.LockedGridCellSize = null;
        projection.Enabled = parameters.ProjectionEnabled;
        result.BackgroundColor = parameters.BackgroundColor; result.Projection = projection;
        result.Grid = new() { Visible = parameters.GridVisible, Color = parameters.GridColor, Width = parameters.GridWidth, Pattern = parameters.GridPattern,
            FlatCellSize = projection.Enabled ? projection.GridCellSize : parameters.FlatCellSize ?? projection.GridCellSize,
            FlatOrigin = current.Grid.FlatOrigin ?? referenceViewport.Center };
        SceneValidation.Validate(new SceneDocument { Canvas = result, Viewport = viewport });
        return result;
    }

    private static CanvasPreset OfficialPreset(string id, CanvasPresetParameters parameters) => new()
    {
        Id = "official.canvas-" + id, Name = L.Get("CanvasPreset." + id),
        Parameters = OfficialParameters(parameters), IsOfficial = true
    };
    private static CanvasPresetParameters OfficialParameters(CanvasPresetParameters parameters)
    { parameters.ResetProjection = true; return parameters; }
    private CanvasPreset FindUser(string id) => presets.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException(id);
    private void Replace(CanvasPreset preset) => Persist(presets.Select(p => p.Id == preset.Id ? preset : p).ToList());
    private void Persist(List<CanvasPreset> next)
    {
        if (!CanEdit) throw new InvalidOperationException(L.Get("CanvasPreset.ReadOnly"));
        var previous = presets.Select(p => p.Clone()).ToList();
        compatibility.Save(new State { Presets = next },JsonOptions); presets = next;
        if (!restoringResource) RememberResourceChange(previous);
    }
    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 80;
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type != typeof(CanvasProjectionSettings) && type.Type != typeof(Projective2D) && type.Type != typeof(PointD)) return;
            foreach (var property in type.Properties.Where(p => p.Set is not null))
            {
                if (type.Type == typeof(CanvasProjectionSettings) && property.Name == nameof(CanvasProjectionSettings.LockedGridCellSize))
                {
                    // The adjustment lock belongs to the canvas session/project, not a preset.
                    // Keep existing version-two preset files readable by older applications.
                    property.IsRequired = false; property.ShouldSerialize = (_, _) => false;
                }
                else property.IsRequired = true;
            }
        });
        return new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, TypeInfoResolver = resolver };
    }
    private static void Validate(string name, CanvasPresetParameters parameters)
    {
        if (!ValidName(name)) throw new ArgumentException(L.Get("Preset.InvalidName"), nameof(name));
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.IsValid) throw new ArgumentException(L.Get("CanvasPreset.InvalidLibrary"), nameof(parameters));
    }
}
