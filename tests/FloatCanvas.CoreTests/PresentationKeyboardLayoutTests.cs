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
            value => value.Keys[0].ScanCode = 0, value => value.Keys[0].Label = "\n", value => value.Keys[0].X = double.NaN,
            value => value.Keys[0].Y = -1, value => value.Keys[0].Width = .24, value => value.Keys[0].Height = 17,
            value => { value.Keys[0].X = 64; value.Keys[0].Width = 1; },
            value => value.Keys = Enumerable.Range(0, 129).Select(_ => value.Keys[0].Clone()).ToList() })
        {
            var invalid = layout.Clone(); edit(invalid); check(!invalid.IsValid(), "malformed or unbounded custom keyboard geometry is rejected");
        }
        var game = layout.Clone(); foreach (var key in game.Keys) key.Enabled = false;
        check(game.IsValid(), "all keys can be hidden without deleting their recoverable layout positions");
        game.Keys = []; check(game.IsValid(), "an explicitly empty custom layout is a bounded empty keyboard");
        settings.DisplayStyle = (PresentationDisplayStyle)3; check(!settings.IsValid(), "unknown presentation display style is rejected");
        bool chordRejected = false; try { _ = new PresentationChord(Enumerable.Repeat(new PresentationKeyCap(0x41, "A"), PresentationLimits.MaximumChordKeys + 1), TimeSpan.Zero); } catch (ArgumentException) { chordRejected = true; }
        check(chordRejected, "expanded live keyboard capacity does not expand semantic chord glyph capacity");
    }
}
