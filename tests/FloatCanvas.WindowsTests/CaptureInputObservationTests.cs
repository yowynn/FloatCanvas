using System.Text.Json;
using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class CaptureInputObservationTests
{
    internal static void Run(Action<bool, string> check)
    {
        var observations = new CaptureInputObservationBuffer();
        check(observations.SnapshotAfter(0).Count == 0, "Input diagnostics begin empty without probing or generating input");
        var keyboard = new CaptureInputObservation(4, CaptureInputSource.KeyboardDown, 120, 0x10, 41, 0, true, true);
        observations.Record(keyboard);
        observations.Record(new(5, CaptureInputSource.PointerWheel, 125, 1, 41, 42, false, false));
        var snapshot = observations.SnapshotAfter(3);
        check(snapshot.Count == 2 && snapshot[0] == keyboard && snapshot[1].Version == 5,
            "Input observations retain the original event order and exact counter versions");
        check(snapshot[0].Injected && snapshot[1].Injected, "Keyboard and pointer injection flags use their respective native flag bits");
        check(!new CaptureInputObservation(6, CaptureInputSource.KeyboardDown, 0, 1, 0, 0, false, false).Injected &&
            !new CaptureInputObservation(7, CaptureInputSource.PointerPress, 0, 0x10, 0, 0, false, false).Injected,
            "Unrelated native flags do not invent injected input");
        check(observations.SnapshotAfter(4).Count == 1 && observations.SnapshotAfter(5).Count == 0,
            "The capture's pre-hide version excludes earlier background input observations");
        observations.Record(new(6, CaptureInputSource.PointerPress, 126, 0, 41, 42, false, false));
        check(snapshot.Count == 2 && snapshot[^1].Version == 5, "A published metadata snapshot cannot change under later hook writes");
        bool immutable = false;
        try { ((IList<CaptureInputObservation>)snapshot)[0] = default; }
        catch (NotSupportedException) { immutable = true; }
        check(immutable, "Published observation collections are read-only");

        for (int version = 7; version <= 100; version++)
            observations.Record(new(version, CaptureInputSource.PointerWheel, (uint)version, 0, 41, 42, false, false));
        var bounded = observations.SnapshotAfter(0);
        check(bounded.Count == CaptureInputObservationBuffer.MaximumCount && bounded[0].Version == 69 && bounded[^1].Version == 100,
            "Input diagnostics retain at most 32 latest increments with an observable version gap on overflow");
        check(observations.SnapshotAfter(99).Count == 1 && observations.SnapshotAfter(100).Count == 0,
            "A later capture never receives the preceding capture's observations");

        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(keyboard)))
        {
            var properties = json.RootElement.EnumerateObject().Select(item => item.Name).ToArray();
            string[] allowed = ["Version", "Source", "EventTime", "Flags", "ForegroundWindow", "TargetWindow", "Suppressed", "TailSuppressed", "Injected", "CountsAsExternalInput"];
            check(properties.Order().SequenceEqual(allowed.Order()), "Serialized input metadata has only the reviewed privacy-safe fields");
            check(!properties.Any(name => name is "VirtualKey" or "ScanCode" or "Text" or "Key" or "Button" or "Position" or "X" or "Y"),
                "Input diagnostics contain no specific keys, characters, buttons or coordinates");
        }

        // Exercise the real suppression decision before classifying external intent.
        var state = new KeyboardSuppressionState(256, 2000);
        state.Begin(new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 100);
        check(state.Process(0x41, 0x1E, 0, 110, false, 0x100, 10, true, true, 110), "The fixture initially suppresses its own key press");
        state.End(KeyboardLeaseEndReason.Explicit, 120);
        long tailsBefore = state.TailSuppressedCount;
        bool suppressed = state.Process(0x41, 0x1E, 0, 130, false, 0x100, 20, false, true, 130);
        var tail = new CaptureInputObservation(1, CaptureInputSource.KeyboardDown, 130, 0, 20, 0,
            suppressed, state.TailSuppressedCount != tailsBefore);
        check(tail.Suppressed && tail.TailSuppressed && !state.IsActive,
            "Diagnostics distinguish the existing swallowed key-repeat tail from input passed by this hook");
        check(!CaptureInputIntent.CountsKeyboard(false, suppressed),
            "An old intercepted repeat tail is not a new external input intent");
        var ignored = new CaptureInputObservationBuffer();
        ignored.Record(tail with { Version = 5 });
        check(ignored.SnapshotAfter(5).Count == 0 && ignored.SnapshotAfter(5, 120).Single() == tail with { Version = 5 } &&
            ignored.SnapshotAfter(5, 140).Count == 0,
            "Ignored tails remain diagnosable within a capture without advancing its external input version");
        tailsBefore = state.TailSuppressedCount;
        suppressed = state.Process(0x42, 0x30, 0, 140, false, 0x100, 20, false, true, 140);
        var passed = new CaptureInputObservation(2, CaptureInputSource.KeyboardDown, 140, 0, 20, 0,
            suppressed, state.TailSuppressedCount != tailsBefore);
        check(!passed.Suppressed && !passed.TailSuppressed, "Diagnostics separately describe fresh external keydowns that this hook passed");
        check(CaptureInputIntent.CountsKeyboard(false, suppressed) && !CaptureInputIntent.CountsKeyboard(true, false),
            "Only a passed external keydown advances the external interaction boundary");
        check(!CaptureInputIntent.CountsPointer(true, true, true),
            "An own foreground mouse capture keeps the original recipient despite an external hit-test target");
        check(CaptureInputIntent.CountsPointer(true, false, true) && CaptureInputIntent.CountsPointer(true, true, false),
            "A stale capture or a real external target without capture cannot exempt external pointer input");
        foreach (nuint message in new nuint[] { 0x200, 0x202, 0x205, 0x208, 0x20C })
            check(!KeyboardInputIsolation.IsCapturePointerInteraction(message), "Move and button-up classification remains unchanged: " + message);
        foreach (nuint message in new nuint[] { 0x201, 0x204, 0x207, 0x20A, 0x20B, 0x20D, 0x20E })
            check(KeyboardInputIsolation.IsCapturePointerInteraction(message), "Existing countable pointer input classification remains unchanged: " + message);
    }
}
