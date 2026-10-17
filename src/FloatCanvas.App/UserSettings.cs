using L = FloatCanvas.Localization.AppText;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed class UserSettings
{
    public HotkeyBinding Toggle { get; set; } = HotkeyBinding.DefaultToggle;
    public HotkeyBinding Hide { get; set; } = HotkeyBinding.DefaultHide;
    // UI geometry belongs to this process only. Older settings may still contain these
    // fields; ignoring them restores the startup layout without changing user preferences.
    [JsonIgnore] public double? ToolbarX { get; set; }
    [JsonIgnore] public double? ToolbarY { get; set; }
    [JsonIgnore] public bool ToolbarExpanded { get; set; } = true;
    public string Theme { get; set; } = "system";
    public string Language { get; set; } = "auto";
    public bool PrimaryScreenOnly { get; set; } = true;
    public bool ShowToolShortcutHints { get; set; } = true;
    public int NumberStart { get; set; } = 1;
    public int NumberStep { get; set; } = 1;
    private static string FilePath => Path.Combine(ApplicationPaths.ProfileDirectory, "settings.json");

    public static UserSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        // Do not silently overwrite malformed settings. The application reports the failure.
        return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException(L.Get("App.SettingsEmpty") + FilePath);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}
