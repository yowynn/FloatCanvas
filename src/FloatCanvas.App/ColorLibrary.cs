using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal enum ColorRole { Foreground, Background }
internal sealed record PaletteColor(string Key, uint Value)
{
    public string Name => L.Get("Color." + Key);
}
internal sealed record SavedColor(string Id, string Name, uint Value);

/// <summary>Role-specific built-in colors and exact ARGB history/pins shared by every color field.</summary>
internal sealed class ColorLibrary
{
    internal const uint ForegroundRed = 0xFFE03E3E;
    internal const uint BackgroundRed = 0xFFFFDCDC;
    private sealed class State
    {
        public int Version { get; set; } = 1;
        public List<uint> History { get; set; } = [];
        public List<uint> Pinned { get; set; } = [];
        public List<SavedColor> Swatches { get; set; } = [];
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly string[] Names = ["Black", "White", "Red", "Brown", "Orange", "Yellow", "Blue", "Green", "Purple", "Pink"];
    private static readonly uint[] Foreground = [0xFF252525, 0xFFFFFFFF, ForegroundRed, 0xFF9F6B53, 0xFFD9730D, 0xFFCB912F, 0xFF2383E2, 0xFF448361, 0xFF9065B0, 0xFFC14C8A];
    private static readonly uint[] Background = [0xFFE4E4E4, 0xFFFFFFFF, BackgroundRed, 0xFFEADDD4, 0xFFFFE5CC, 0xFFFFF2BB, 0xFFDCEEFF, 0xFFDCEFE5, 0xFFEBDFF5, 0xFFF6DDEB];
    private readonly string path;
    private readonly CompatibleConfiguration compatibility;
    private static ColorLibrary? shared;
    public static ColorLibrary Shared
    {
        get
        {
            string expected = Path.GetFullPath(Path.Combine(ApplicationPaths.ProfileDirectory, "colors.json"));
            if (shared is null || !StringComparer.OrdinalIgnoreCase.Equals(shared.path, expected)) shared = new(expected);
            return shared;
        }
    }
    private State state = new();
    private readonly Stack<List<SavedColor>> undo = new();
    private bool restoringResource;
    internal bool CanUndoResourceChange => undo.Count > 0;
    internal void UndoResourceChange()
    {
        if (!undo.TryPeek(out var previous)) return;
        var next = Copy(); next.Swatches = [.. previous]; restoringResource = true;
        try { Persist(next); undo.Pop(); } finally { restoringResource = false; }
    }
    public ColorLibrary(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? Path.Combine(ApplicationPaths.ProfileDirectory, "colors.json"));
        compatibility = new(this.path);
        if (!File.Exists(this.path)) return;
        try
        {
            var loaded = compatibility.Read<State>(JsonOptions);
            if (loaded is null || loaded.Version != 1 || loaded.History is null || loaded.Pinned is null || loaded.History.Count > 20 || loaded.Pinned.Count > 100 ||
                loaded.History.Distinct().Count() != loaded.History.Count || loaded.Pinned.Distinct().Count() != loaded.Pinned.Count ||
                loaded.Swatches is null || loaded.Swatches.Count > 100 || loaded.Swatches.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 80 || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80) ||
                loaded.Swatches.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != loaded.Swatches.Count)
                throw new InvalidDataException(L.Get("Color.InvalidLibrary"));
            if (loaded.Swatches.Count == 0) loaded.Swatches = loaded.Pinned.Select(color => new SavedColor("color-" + Guid.NewGuid().ToString("N"), ToHtml(color), color)).ToList();
            state = loaded;
        }
        catch (JsonException ex) { throw new InvalidDataException(L.Get("Color.InvalidLibrary"), ex); }
    }

    public IReadOnlyList<uint> History => state.History.ToArray();
    public IReadOnlyList<uint> Pinned => state.Swatches.Select(p => p.Value).ToArray();
    public IReadOnlyList<SavedColor> Swatches => state.Swatches.ToArray();
    public SavedColor Save(string name, uint color)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ArgumentException(L.Get("Preset.InvalidName"));
        if (state.Swatches.Count >= 100) throw new InvalidDataException(L.Get("Color.InvalidLibrary"));
        var saved = new SavedColor("color-" + Guid.NewGuid().ToString("N"), name.Trim(), color);
        var next = Copy(); next.Swatches.Add(saved); Persist(next); return saved;
    }
    public void Rename(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ArgumentException(L.Get("Preset.InvalidName"));
        var next = Copy(); int index = next.Swatches.FindIndex(p => p.Id == id);
        if (index < 0) throw new KeyNotFoundException(id); next.Swatches[index] = next.Swatches[index] with { Name = name.Trim() }; Persist(next);
    }
    public void Update(string id, uint color)
    {
        var next = Copy(); int index = next.Swatches.FindIndex(p => p.Id == id);
        if (index < 0) throw new KeyNotFoundException(id); next.Swatches[index] = next.Swatches[index] with { Value = color }; Persist(next);
    }
    public void Delete(string id) { var next = Copy(); next.Swatches.RemoveAll(p => p.Id == id); Persist(next); }
    public void Move(string id, string beforeId, bool after = false)
    {
        if (id == beforeId) return;
        var next = Copy(); var moving = next.Swatches.Single(p => p.Id == id); var target = next.Swatches.Single(p => p.Id == beforeId);
        next.Swatches.Remove(moving); next.Swatches.Insert(next.Swatches.IndexOf(target) + (after ? 1 : 0), moving); Persist(next);
    }
    public static IReadOnlyList<PaletteColor> Palette(ColorRole role)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        var values = role == ColorRole.Foreground ? Foreground : Background;
        return Names.Select((name, index) => new PaletteColor(name, values[index])).ToArray();
    }
    public void Remember(uint color)
    {
        var next = Copy(); next.History.Remove(color); next.History.Insert(0, color); next.History = next.History.Take(20).ToList(); Persist(next);
    }
    public void Pin(uint color)
    {
        if (Pinned.Contains(color)) return;
        Save(ToHtml(color), color);
    }
    public bool RemovePinned(uint color)
    {
        if (!Pinned.Contains(color)) return false;
        var next = Copy(); next.Swatches.RemoveAll(p => p.Value == color); Persist(next); return true;
    }
    public static string ToHtml(uint color, bool includeAlpha = true) => "#" + (includeAlpha ? color.ToString("X8", CultureInfo.InvariantCulture) : (color & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture));
    public static bool TryParseHtml(string? text, out uint color)
    {
        color = 0; string value = (text ?? "").Trim();
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length == 3) value = string.Concat(value.Select(c => new string(c, 2)));
        if (value.Length is not (6 or 8) || !uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out color)) return false;
        if (value.Length == 6) color |= 0xFF000000;
        return true;
    }
    private State Copy() => new() { History = [.. state.History], Pinned = [], Swatches = [.. state.Swatches] };
    private void Persist(State next)
    {
        var previous = state.Swatches.ToList();
        bool changed = !previous.SequenceEqual(next.Swatches);
        compatibility.Save(next,JsonOptions); state = next;
        if (changed && !restoringResource)
        {
            undo.Push(previous);
            if (undo.Count > 20) { var keep = undo.Take(20).Reverse().ToArray(); undo.Clear(); foreach (var value in keep) undo.Push(value); }
        }
    }
}
