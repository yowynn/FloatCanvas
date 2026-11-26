using System.Text.Json;
using FloatCanvas.Core;

internal static class PresentationKeyboardLayoutTests
{
    internal static void Run(Action<bool, string> check)
    {
        var settings = new PresentationSettings(); var layout = PresentationKeyboardLayout.CreateDefault();
        check(settings.DisplayStyle == PresentationDisplayStyle.Log && settings.KeyboardLayout is null && layout.IsValid() && layout.Keys.Count == 104,
            "legacy log defaults stay compact while the effective keyboard is a complete 104-key layout");
        check(layout.Keys.Count(key => key.VirtualKey == 0x0D) == 2 && layout.Keys.Where(key => key.VirtualKey == 0x0D).Select(key => key.IsExtended).Distinct().Count() == 2 &&
            new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }.All(vk => layout.Keys.Any(key => key.VirtualKey == vk)),
            "physical layout distinguishes both Enter keys and both sides of Shift Control and Alt");
        check(!JsonSerializer.Serialize(settings).Contains("KeyboardLayout", StringComparison.Ordinal), "default log settings do not serialize 104 unused key descriptions");
        settings.KeyboardLayout = layout; settings.DisplayStyle = PresentationDisplayStyle.Keyboard;
        var clone = settings.Clone(); clone.KeyboardLayout!.Keys[0].X = 40; clone.KeyboardLayout.Keys[1].Enabled = false;
        clone.KeyboardLayout.Keys.RemoveAt(2);
        check(layout.Keys[0].X == 0 && layout.Keys[1].Enabled && layout.Keys.Count == 104, "settings snapshots deeply own keyboard keys and their layout collection");
        string json = JsonSerializer.Serialize(settings); var restored = JsonSerializer.Deserialize<PresentationSettings>(json)!;
        check(restored.IsValid() && JsonSerializer.Serialize(restored) == json && !json.Contains("\"Id\"", StringComparison.Ordinal),
            "keyboard identity and geometry roundtrip without derived IDs or transient held state");
        foreach (var edit in new Action<PresentationKeyboardLayout>[] {
            value => value.Keys = null!, value => value.Keys.Add(value.Keys[0].Clone()), value => value.Keys[0].VirtualKey = 255,
            value => value.Keys[0].ScanCode = -1, value => value.Keys[0].Label = "\n", value => value.Keys[0].SecondaryLabel = "\n",
            value => value.Keys[0].X = double.NaN,
            value => value.Keys[0].Y = PresentationKeyboardKey.MinimumPosition - .25,
            value => value.Keys[0].Width = .24, value => value.Keys[0].Height = 17,
            value => { value.Keys[0].X = 64; value.Keys[0].Width = 1; },
            value => value.Keys = Enumerable.Range(0, 129).Select(_ => value.Keys[0].Clone()).ToList() })
        {
            var invalid = layout.Clone(); edit(invalid); check(!invalid.IsValid(), "malformed or unbounded custom keyboard geometry is rejected");
        }
        var game = layout.Clone(); foreach (var key in game.Keys) key.Enabled = false;
        check(!game.IsValid(), "a layout with no enabled keys is rejected");
        game.Keys = []; check(!game.IsValid(), "an explicitly empty custom layout is rejected");
        var media = layout.Clone(); media.Keys[0].ScanCode = 0;
        check(media.IsValid(), "physical media and virtual keys may have no reported scan code");
        var mediaKey = new PresentationKeyboardKey { VirtualKey = 0xB0, ScanCode = 0x19, IsExtended = true, Label = "Next track" };
        var mediaCap = new PresentationKeyCap(0xB0, "Next track", ScanCode: 0x19, IsExtended: true);
        check(mediaKey.Matches(mediaCap) && !mediaKey.Matches(mediaCap with { IsExtended = false }),
            "extended media keys require matching extended state during live input");
        var leftShift = layout.Keys.Single(key => key.VirtualKey == 0xA0);
        var rightShift = new PresentationKeyCap(0xA1, "Shift", true, 0x36, false);
        var relatedShift = leftShift.Clone(); relatedShift.MatchRelatedKeys = true;
        check(leftShift.SupportsRelatedMatch && !leftShift.Matches(rightShift) &&
            relatedShift.Matches(new PresentationKeyCap(0xA0, "Shift", true, 0x2A, false)) &&
            relatedShift.Matches(rightShift) &&
            !relatedShift.Matches(new PresentationKeyCap(0xA3, "Ctrl", true, 0x1D, true)),
            "left and right modifiers remain independent unless related-key matching is enabled");
        var rightShiftKey = layout.Keys.Single(key => key.VirtualKey == 0xA1);
        check(rightShiftKey.Matches(new PresentationKeyCap(0xA1, "Shift", true, 0x36, true)),
            "right Shift matching tolerates an injected extended-flag difference");
        var mainOne = layout.Keys.Single(key => key.VirtualKey == 0x31);
        var numpadOne = layout.Keys.Single(key => key.VirtualKey == 0x61);
        var relatedOne = mainOne.Clone(); relatedOne.MatchRelatedKeys = true;
        check(!mainOne.Matches(new PresentationKeyCap(0x61, "1", false, 0x4F, false)) &&
            relatedOne.Matches(new PresentationKeyCap(0x61, "1", false, 0x4F, false)),
            "top-row and numpad numbers match only when related-key matching is enabled");
        var keypadEnd = new PresentationKeyCap(0x23, "End", ScanCode: 0x4F);
        var relatedPeriod = layout.Keys.Single(key => key.VirtualKey == 0xBE).Clone(); relatedPeriod.MatchRelatedKeys = true;
        var keypadDelete = new PresentationKeyCap(0x2E, "Delete", ScanCode: 0x53);
        check(relatedOne.Matches(keypadEnd) && relatedPeriod.Matches(keypadDelete) &&
            !relatedOne.Matches(keypadEnd with { IsExtended = true }) && !relatedPeriod.Matches(keypadDelete with { IsExtended = true }),
            "related-key matching uses the physical keypad even with Num Lock off and excludes the separate navigation cluster");
        var mainEnter = layout.Keys.Single(key => key.VirtualKey == 0x0D && !key.IsExtended);
        var numEnter = layout.Keys.Single(key => key.VirtualKey == 0x0D && key.IsExtended);
        var relatedEnter = mainEnter.Clone(); relatedEnter.MatchRelatedKeys = true;
        var numEnterCap = new PresentationKeyCap(0x0D, "Enter", false, 0x1C, true);
        check(!mainEnter.Matches(numEnterCap) && relatedEnter.Matches(numEnterCap),
            "main and numpad Enter are independent by default and related when enabled");
        foreach (var pair in new[]
        {
            (Main: 0xBD, Numpad: 0x6D, MainScan: 0x0C, NumpadScan: 0x4A),
            (Main: 0xBB, Numpad: 0x6B, MainScan: 0x0D, NumpadScan: 0x4E),
            (Main: 0xBE, Numpad: 0x6E, MainScan: 0x34, NumpadScan: 0x53),
            (Main: 0xBF, Numpad: 0x6F, MainScan: 0x35, NumpadScan: 0x35)
        })
        {
            var main = layout.Keys.Single(key => key.VirtualKey == pair.Main && key.ScanCode == pair.MainScan);
            var related = main.Clone(); related.MatchRelatedKeys = true;
            var numpadCap = new PresentationKeyCap(pair.Numpad, "Numpad", false, pair.NumpadScan, pair.Numpad == 0x6F);
            check(!main.Matches(numpadCap) && related.Matches(numpadCap),
                "main and numpad operators match only in their intended related-key family");
        }
        check(layout.Keys.Single(key => key.VirtualKey == 0x14).SupportsLockIndicator &&
            layout.Keys.Single(key => key.VirtualKey == 0x90).SupportsLockIndicator &&
            layout.Keys.Single(key => key.VirtualKey == 0x91).SupportsLockIndicator,
            "Caps Lock, Num Lock and Scroll Lock all support configurable lock indicators");
        check(layout.Keys.Where(key => key.VirtualKey is 0x14 or 0x90 or 0x91)
            .All(key => !key.ShowLockIndicator), "lock indicators are disabled by default");
        var shownLock = layout.Keys.Single(key => key.VirtualKey == 0x90).Clone();
        shownLock.ShowLockIndicator = true;
        check(shownLock.IsValid() && JsonSerializer.Deserialize<PresentationKeyboardKey>(
            JsonSerializer.Serialize(shownLock))!.ShowLockIndicator,
            "enabling a lock indicator persists on that lock key");
        var broadA = layout.Keys.Single(key => key.VirtualKey == 0x41).Clone(); broadA.MatchRelatedKeys = true;
        var broadPlaceholder = PresentationKeyboardLayout.CreatePlaceholder("Macro"); broadPlaceholder.MatchRelatedKeys = true;
        var broadLock = layout.Keys.Single(key => key.VirtualKey == 0xA0).Clone(); broadLock.ShowLockIndicator = true;
        check(!broadA.IsValid() && !broadPlaceholder.IsValid(),
            "related-key matching is limited to supported cross-region key families");
        check(!broadLock.IsValid(), "lock-indicator visibility is limited to lock keys");
        string relatedJson = JsonSerializer.Serialize(relatedOne);
        check(JsonSerializer.Deserialize<PresentationKeyboardKey>(relatedJson)!.MatchRelatedKeys,
            "related-key matching survives authored JSON");
        var placeholder = PresentationKeyboardLayout.CreatePlaceholder("Macro"); placeholder.X = 60; placeholder.Y = 60;
        placeholder.SecondaryLabel = "!";
        check(!JsonSerializer.Serialize(layout.Keys[0]).Contains("IsPlaceholder", StringComparison.Ordinal) &&
            JsonSerializer.Serialize(placeholder).Contains("\"IsPlaceholder\":true", StringComparison.Ordinal),
            "physical keys omit the placeholder marker while neutral cells persist it");
        var custom = layout.Clone(); custom.Keys.Add(placeholder);
        check(custom.IsValid() && placeholder.Id.StartsWith("custom:", StringComparison.Ordinal), "a neutral placeholder has a bounded unique custom identity");
        string customJson = JsonSerializer.Serialize(custom);
        var customRestored = JsonSerializer.Deserialize<PresentationKeyboardLayout>(customJson)!;
        var restoredPlaceholder = customRestored.Keys.Single(key => key.IsPlaceholder);
        check(customRestored.IsValid() && restoredPlaceholder is { SecondaryLabel: "!" } &&
            restoredPlaceholder.Id == placeholder.Id, "custom placeholders and secondary labels persist through JSON");
        check(!placeholder.Matches(new PresentationKeyCap(0x41, "A")), "neutral placeholders never match physical input");
        settings.DisplayStyle = (PresentationDisplayStyle)99; check(!settings.IsValid(), "unknown presentation display style is rejected");
        bool chordRejected = false; try { _ = new PresentationChord(Enumerable.Repeat(new PresentationKeyCap(0x41, "A"), PresentationLimits.MaximumChordKeys + 1), TimeSpan.Zero); } catch (ArgumentException) { chordRejected = true; }
        check(chordRejected, "expanded live keyboard capacity does not expand semantic chord glyph capacity");
    }
}
