using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using FloatCanvas.App;

internal static class OverlayModeTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("new settings select automatic overlay mode", new UserSettings().OverlayMode == "auto");
        var legacy = JsonSerializer.Deserialize<UserSettings>("""{"Theme":"dark"}""")!;
        check("legacy settings retain their preferences and default to automatic", legacy.OverlayMode == "auto" && legacy.Theme == "dark");
        check("unknown overlay preference resolves to automatic", OverlayBackendModes.Parse("future") == OverlayBackendMode.Auto);
        check("desktop recovery argument overrides forced enhancement", OverlayBackendModes.Resolve("enhanced", ["--desktop-mode"]) == OverlayBackendMode.Desktop);
        check("legacy retry argument explicitly requests automatic detection", OverlayBackendModes.Resolve("desktop", ["--retry-fullscreen"]) == OverlayBackendMode.Auto);

        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-OverlayModes", Guid.NewGuid().ToString("N"));
        string previous = ApplicationPaths.ProfileDirectory;
        try
        {
            ApplicationPaths.Configure(["--profile", root]);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), """{"Theme":"dark","FuturePreference":{"Keep":true}}""");
            foreach (var mode in Enum.GetValues<OverlayBackendMode>())
            {
                var settings = UserSettings.Load();
                settings.OverlayMode = OverlayBackendModes.Serialize(mode);
                settings.Save();
                check("overlay choice survives profile save/load: " + mode,
                    OverlayBackendModes.Resolve(UserSettings.Load().OverlayMode, []) == mode);
            }
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "settings.json")));
            check("saving overlay choice preserves unrelated and future preferences", document.RootElement.GetProperty("Theme").GetString() == "dark" && document.RootElement.GetProperty("FuturePreference").GetProperty("Keep").GetBoolean());
        }
        finally { ApplicationPaths.Configure(["--profile", previous]); }

        int probes = 0, loads = 0;
        var enhanced = new OverlayBackendProtection(Path.Combine(root, "enhanced"), OverlayBackendMode.Enhanced,
            () => { probes++; return new(false, "would reject"); });
        check("forced enhancement initializes directly without probing", enhanced.TryEnable(() => loads++) && probes == 0 && loads == 1);
        check("forced enhancement disables automatic fallback", !enhanced.AllowsAutomaticFallback);
        enhanced.Disable("runtime error");
        check("forced enhancement does not record a disabled backend", enhanced.Failure is null && !enhanced.IsDisabled && !File.Exists(Path.Combine(root, "enhanced", "overlay-backend-state.json")));
        bool reported = false;
        var failing = new OverlayBackendProtection(root, OverlayBackendMode.Enhanced);
        try { failing.TryEnable(() => throw new COMException("forced failure")); }
        catch (COMException) { reported = true; }
        check("forced initialization failure is reported rather than silently falling back", reported && failing.Failure is null);
        var desktop = new OverlayBackendProtection(root, OverlayBackendMode.Desktop,
            () => { probes++; return new(true, "available"); });
        check("forced ordinary mode never probes or loads enhancement", !desktop.TryEnable(() => loads++) && probes == 0 && loads == 1 && !desktop.AllowsAutomaticFallback);
    }
}
