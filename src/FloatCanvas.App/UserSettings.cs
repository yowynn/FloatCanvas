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
    public bool PrimaryScreenOnly { get; set; } = false;
    public string? MonitorDeviceName { get; set; }
    public string? CanvasReferenceDisplay { get; set; }
    public bool PreserveCanvasSettingsOnNewCanvas { get; set; }
    public bool ShowToolShortcutHints { get; set; } = true;
    public bool ShowFloatingLogo { get; set; } = true;
    // Supersede the old default-off preference once. New explicit choices persist.
    [JsonIgnore] public bool ShowPassThroughLauncher { get => ShowFloatingLogo; set => ShowFloatingLogo = value; }
    public bool IncludePrereleaseUpdates { get; set; }
    public bool PreferGpuAcceleration { get; set; } = true;
    public string OverlayMode { get; set; } = "auto";
    private int historyVersionLimit = 60;
    public int HistoryVersionLimit { get => historyVersionLimit; set => historyVersionLimit = value >= 0 ? Math.Min(value, 1000) : 60; }
    public int NumberStart { get; set; } = 1;
    public int NumberStep { get; set; } = 1;
    private static string FilePath => Path.Combine(ApplicationPaths.ProfileDirectory, "settings.json");
    private CompatibleConfiguration? compatibility;
    private static readonly JsonSerializerOptions JsonOptions = new() {WriteIndented=true};

    public static UserSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        var source=new CompatibleConfiguration(FilePath);
        var settings=source.Read<UserSettings>(JsonOptions);settings.compatibility=source;return settings;
    }

    public void Save()
    {
        (compatibility??=new CompatibleConfiguration(FilePath)).Save(this,JsonOptions);
    }
}
