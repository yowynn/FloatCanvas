using L = FloatCanvas.Localization.AppText;
using System.IO;
using System.Text.Json;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed class UserSettings
{
    public HotkeyBinding Toggle { get; set; } = HotkeyBinding.DefaultToggle;
    public HotkeyBinding Hide { get; set; } = HotkeyBinding.DefaultHide;
    public double? ToolbarX { get; set; }
    public double? ToolbarY { get; set; }
    public string Theme { get; set; } = "system";
    public string Language { get; set; } = "auto";
    public bool PrimaryScreenOnly { get; set; } = true;
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
