using FloatCanvas.Core;
using System.Text.Json;

internal static class PresentationGlobalSettingsTests
{
    internal static void Run(Action<bool, string> check)
    {
        var global = new PresentationGlobalSettings();
        check(global.IsValid() && global.KeyboardScope == PresentationKeyboardScope.AllApplications && !global.Pointer.AnyEnabled,
            "new global demonstration preferences default to all applications with the mouse master switch off");
        check(new PresentationSettings().KeyDisplay == PresentationKeyDisplayMode.AllPhysicalKeys &&
            JsonSerializer.Deserialize<PresentationSettings>("{\"KeyDisplay\":0}")!.KeyDisplay == PresentationKeyDisplayMode.ShortcutChords,
            "new keyboard layers show physical keys by default while explicit legacy shortcut filtering remains intact");
        global.Pointer.Enabled = true;
        check(global.Pointer.AnyEnabled && global.Pointer.CursorEnabled && global.Pointer.ClicksEnabled && !global.Pointer.TrailEnabled,
            "enabling the mouse master switch activates the default highlight and independent click styles");
        check(global.Pointer.LeftClick.Color != global.Pointer.RightClick.Color && global.Pointer.RightClick.Color != global.Pointer.MiddleClick.Color,
            "left right and middle buttons have distinct independently editable default colors");
        global.Pointer.LeftClick.Shape = PresentationClickShape.Disc;
        global.Pointer.RightClick.DurationMilliseconds = 1200;
        global.Pointer.TrailColor = 0x80765432;
        global.KeyboardScope = PresentationKeyboardScope.OutsideFloatCanvas;
        var copy = global.Clone();
        copy.Pointer.LeftClick.Color = 0; copy.Pointer.RightClick.DurationMilliseconds = 333;
        copy.Pointer.MiddleClick.Enabled = false; copy.Pointer.CursorColor = 0; copy.KeyboardScope = PresentationKeyboardScope.FloatCanvasOnly;
        check(global.Pointer.LeftClick.Color != 0 && global.Pointer.RightClick.DurationMilliseconds == 1200 && global.Pointer.MiddleClick.Enabled &&
            global.Pointer.CursorColor != 0 && global.KeyboardScope == PresentationKeyboardScope.OutsideFloatCanvas,
            "global preference snapshots deeply own all three button styles and keyboard scope");
        global.Pointer.Enabled = false;
        check(!global.Pointer.AnyEnabled && global.Pointer.LeftClick.Shape == PresentationClickShape.Disc && global.Pointer.RightClick.DurationMilliseconds == 1200,
            "turning the mouse master switch off retains each configured effect");
        global.Pointer.Enabled = true; global.Pointer.CursorEnabled = global.Pointer.TrailEnabled = false;
        global.Pointer.LeftClick.Enabled = global.Pointer.RightClick.Enabled = global.Pointer.MiddleClick.Enabled = false;
        check(!global.Pointer.AnyEnabled, "a click-only tool with all buttons disabled requests no pointer observation");
        global.Pointer.MiddleClick.Enabled = true;
        check(global.Pointer.AnyEnabled && global.Pointer.GetClick(PresentationMouseButton.Middle) == global.Pointer.MiddleClick &&
            global.Pointer.GetClick(PresentationMouseButton.X1) is null && global.Pointer.GetClick(PresentationMouseButton.X2) is null,
            "button lookup does not misclassify side buttons as left right or middle clicks");
        string json = JsonSerializer.Serialize(global);
        var roundtrip = JsonSerializer.Deserialize<PresentationGlobalSettings>(json)!;
        check(roundtrip.IsValid() && roundtrip.Pointer.TrailColor == global.Pointer.TrailColor &&
            roundtrip.Pointer.LeftClick.Shape == PresentationClickShape.Disc && roundtrip.KeyboardScope == global.KeyboardScope && !json.Contains("AnyEnabled", StringComparison.Ordinal),
            "personal preference JSON retains authored configuration without computed runtime demand");
        foreach (var invalidate in new Action<PresentationGlobalSettings>[]
        {
            s => s.KeyboardScope = (PresentationKeyboardScope)3, s => s.Pointer = null!,
            s => s.Pointer.LeftClick = null!, s => s.Pointer.RightClick = null!, s => s.Pointer.MiddleClick = null!,
            s => s.Pointer.CursorRadiusPixels = double.NaN, s => s.Pointer.CursorRadiusPixels = 1,
            s => s.Pointer.CursorRadiusPixels = 129, s => s.Pointer.CursorOpacity = double.PositiveInfinity,
            s => s.Pointer.CursorOpacity = -.01, s => s.Pointer.CursorOpacity = 1.01,
            s => s.Pointer.LeftClick.RadiusPixels = 1, s => s.Pointer.RightClick.RadiusPixels = 129,
            s => s.Pointer.MiddleClick.StrokeWidthPixels = .49, s => s.Pointer.LeftClick.StrokeWidthPixels = 33,
            s => s.Pointer.RightClick.DurationMilliseconds = 99, s => s.Pointer.MiddleClick.DurationMilliseconds = 2001,
            s => s.Pointer.LeftClick.Shape = (PresentationClickShape)2, s => s.Pointer.RightClick.Opacity = -.01,
            s => s.Pointer.MiddleClick.Opacity = double.NaN, s => s.Pointer.LeftClick.Opacity = 1.01,
            s => s.Pointer.TrailMilliseconds = 99, s => s.Pointer.TrailMilliseconds = 5001,
            s => s.Pointer.TrailWidthPixels = 0, s => s.Pointer.TrailWidthPixels = 65,
            s => s.Pointer.TrailOpacity = -.01, s => s.Pointer.TrailOpacity = 1.01
        })
        {
            var invalid = new PresentationGlobalSettings(); invalidate(invalid);
            check(!invalid.IsValid(), "disabled mouse preferences reject malformed hidden values before allocation or drawing");
        }
        var boundaries = new PresentationGlobalSettings();
        boundaries.Pointer.CursorRadiusPixels = 2; boundaries.Pointer.CursorOpacity = 0;
        boundaries.Pointer.LeftClick.RadiusPixels = 128; boundaries.Pointer.LeftClick.StrokeWidthPixels = .5;
        boundaries.Pointer.LeftClick.DurationMilliseconds = 100; boundaries.Pointer.LeftClick.Opacity = 0;
        boundaries.Pointer.RightClick.RadiusPixels = 2; boundaries.Pointer.RightClick.StrokeWidthPixels = 32;
        boundaries.Pointer.RightClick.DurationMilliseconds = 2000; boundaries.Pointer.RightClick.Opacity = 1;
        boundaries.Pointer.TrailWidthPixels = 64; boundaries.Pointer.TrailMilliseconds = 5000; boundaries.Pointer.TrailOpacity = 1;
        check(boundaries.IsValid(), "finite inclusive preference boundaries are accepted");
        var legacy = new PresentationSettings { Modules = PresentationModules.Keys | PresentationModules.Cursor | PresentationModules.Clicks | PresentationModules.Trail,
            CursorRadiusPixels = 27, RippleMilliseconds = 700, TrailWidthPixels = 5 };
        string legacyJson = JsonSerializer.Serialize(legacy);
        var legacyRoundtrip = JsonSerializer.Deserialize<PresentationSettings>(legacyJson)!;
        check(legacyRoundtrip.IsValid() && legacyRoundtrip.Modules == legacy.Modules && legacyRoundtrip.CursorRadiusPixels == 27 &&
            legacyRoundtrip.RippleMilliseconds == 700 && legacyRoundtrip.TrailWidthPixels == 5,
            "legacy region configuration remains readable without moving mouse preferences into a project");
        var defaults = new PresentationPointerSettings();
        check(defaults.ColorAlphaVersion == 1 && defaults.CursorOpacity == 1 && defaults.TrailOpacity == 1 &&
            defaults.CursorColor >> 24 == 61 && defaults.TrailColor >> 24 == 230,
            "new pointer preferences express cursor and trail transparency in their color alpha alone");
        var oldAlpha = new PresentationPointerSettings { Enabled = true, ColorAlphaVersion = 0,
            CursorColor = 0x80445566, CursorOpacity = .5, TrailColor = 0x81667788, TrailOpacity = .25,
            LeftClick = new() { Color = 0x7F112233, Opacity = .5 } };
        oldAlpha.NormalizeColorAlpha();
        check(oldAlpha.CursorColor == 0x40445566 && oldAlpha.TrailColor == 0x20667788 && oldAlpha.LeftClick.Color == 0x40112233 &&
            oldAlpha.CursorOpacity == 1 && oldAlpha.TrailOpacity == 1 && oldAlpha.LeftClick.Opacity == 1 && oldAlpha.ColorAlphaVersion == 1 && oldAlpha.Enabled,
            "legacy effect opacity folds once into color alpha while retaining RGB and the global master");
        string normalizedAlpha = JsonSerializer.Serialize(oldAlpha); oldAlpha.NormalizeColorAlpha();
        check(JsonSerializer.Serialize(oldAlpha) == normalizedAlpha,
            "repeated pointer normalization never multiplies already migrated color alpha again");
        oldAlpha.CursorColor = 0x00445566; oldAlpha.CursorOpacity = .5; oldAlpha.NormalizeColorAlpha();
        check(oldAlpha.CursorColor == 0x00445566 && oldAlpha.CursorOpacity == 1,
            "migration preserves fully transparent colors without inventing visible ink");
        var invalidAlpha = new PresentationPointerSettings { CursorOpacity = 2 };
        invalidAlpha.NormalizeColorAlpha();
        check(!invalidAlpha.IsValid() && invalidAlpha.CursorOpacity == 2,
            "normalization does not hide malformed pointer settings from validation");
    }
}
