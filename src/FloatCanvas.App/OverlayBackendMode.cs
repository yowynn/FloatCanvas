namespace FloatCanvas.App;

internal enum OverlayBackendMode { Auto, Enhanced, Desktop }

internal static class OverlayBackendModes
{
    internal static OverlayBackendMode Parse(string? value) => value switch
    { "enhanced" => OverlayBackendMode.Enhanced, "desktop" => OverlayBackendMode.Desktop, _ => OverlayBackendMode.Auto };
    internal static string Serialize(OverlayBackendMode mode) => mode switch
    { OverlayBackendMode.Enhanced => "enhanced", OverlayBackendMode.Desktop => "desktop", _ => "auto" };
    internal static OverlayBackendMode Resolve(string? preference, string[] args) => args.Contains("--desktop-mode")
        ? OverlayBackendMode.Desktop : args.Contains("--retry-fullscreen") ? OverlayBackendMode.Auto : Parse(preference);
}
