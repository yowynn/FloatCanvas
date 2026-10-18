using System.Text.Json;
using FloatCanvas.App;

internal static class GpuSettingsTests
{
    internal static void Run(Action<string,bool> check)
    {
        check("new settings prefer GPU acceleration",new UserSettings().PreferGpuAcceleration);
        var legacy=JsonSerializer.Deserialize<UserSettings>("""{"Theme":"dark","Language":"en","ShowToolShortcutHints":false}""")!;
        check("existing settings without GPU preference enable it and retain saved choices",legacy.PreferGpuAcceleration && legacy.Theme=="dark" && legacy.Language=="en" && !legacy.ShowToolShortcutHints);
        bool roundtrip=true;
        foreach(bool enabled in new[]{false,true})
        {
            legacy.PreferGpuAcceleration=enabled;
            var saved=JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(legacy))!;
            roundtrip&=saved.PreferGpuAcceleration==enabled && saved.Theme=="dark" && saved.Language=="en" && !saved.ShowToolShortcutHints;
        }
        check("explicit GPU and CPU preferences both survive JSON persistence",roundtrip);
    }
}
