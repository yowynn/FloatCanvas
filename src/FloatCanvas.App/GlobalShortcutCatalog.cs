namespace FloatCanvas.App;

internal static class GlobalShortcutCatalog
{
    internal static readonly (string Id, string Name)[] Optional =
    [
        ("Global.Pointer", "Shortcuts.Global.Pointer"),
        ("Global.Capture", "Shortcuts.Global.Capture")
    ];
    internal static bool IsRetired(string id) => id is "Global.Enter" or "Global.Exit" or "Global.Keyboard" or "Global.HideAll";
}
