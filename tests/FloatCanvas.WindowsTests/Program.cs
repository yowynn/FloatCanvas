using FloatCanvas.Windows;
using FloatCanvas.WindowsTests;

// These tests exercise only pure keyboard state and monitor selection. They install no hooks, create no HWNDs,
// call no keyboard APIs and generate no desktop input.
int checks = 0;
void Check(bool condition, string description)
{ checks++; if (!condition) throw new InvalidOperationException(description); }
KeyboardSuppressionState New(byte[]? initial = null)
{
    var state = new KeyboardSuppressionState(256, 2000);
    state.Begin(initial ?? new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 100);
    return state;
}
bool Key(KeyboardSuppressionState state, uint vk, bool up = false, long now = 110, uint scan = 0, uint flags = 0,
    bool own = true, bool healthy = true) => state.Process(vk, scan, flags | (up ? 0x80u : 0), (uint)now,
        up, up ? 0x101u : 0x100u, 42, own, healthy, now);
KeyboardInputPacket Take(KeyboardSuppressionState state)
{
    Check(state.TryDequeue(out var packet), "A queued packet can be claimed");
    return packet!;
}
List<KeyboardInputPacket> Drain(KeyboardSuppressionState state)
{
    var packets = new List<KeyboardInputPacket>();
    while (state.TryDequeue(out var packet)) { packets.Add(packet!); state.Complete(packet!.Sequence); }
    return packets;
}

try
{
    MonitorSelectionTests.Run(Check);
    var state = New();
    Check(Key(state, 0x58, scan: 45, flags: 0x10), "Fresh X is suppressed");
    Check(state.NeedsNotification, "First queued packet needs one UI wake");
    state.MarkNotificationPosted();
    Check(!state.NeedsNotification, "A posted wake is not duplicated");
    var down = Take(state);
    Check(down.VirtualKey == 0x58 && down.ScanCode == 45 && down.Flags == 0x10 && down.Time == 110 &&
        !down.IsKeyUp && !down.IsRepeat && down.ForegroundWindow == 42, "Packet preserves original native fields");
    Check(down.KeyboardState[0x58] == 0x80, "Down packet has its own local state");
    var copy = down.CopyKeyboardState(); copy[0x58] = 0;
    Check(down.KeyboardState[0x58] == 0x80, "Caller cannot mutate the packet state through a copied array");
    Check(Key(state, 0x58, now: 120, scan: 45), "Held-key repeat is suppressed");
    Check(!state.NeedsNotification && !state.TryDequeue(out _), "In-flight packet blocks the following wake and claim");
    Check(!state.Complete(down.Sequence + 1) && !state.NeedsNotification, "Wrong completion cannot advance the UI state");
    Check(state.Complete(down.Sequence) && state.NeedsNotification, "Completion schedules the next wake only afterwards");
    var repeat = Take(state); state.Complete(repeat.Sequence);
    Check(repeat.IsRepeat, "Repeated down is identified without consulting async state");
    Check(Key(state, 0x58, true, now: 130, scan: 45), "Matching X up is suppressed");
    var up = Take(state); state.Complete(up.Sequence);
    Check(up.IsKeyUp && up.KeyboardState[0x58] == 0 && down.KeyboardState[0x58] == 0x80,
        "Later release cannot mutate a previous packet snapshot");

    state = New();
    Key(state, 0x10, scan: 0x2A); var leftShift = Take(state); state.Complete(leftShift.Sequence);
    Check(leftShift.Modifiers == OverlayModifiers.Shift && leftShift.KeyboardState[0xA0] == 0x80 && leftShift.KeyboardState[0x10] == 0x80,
        "Generic left Shift updates both side and aggregate local states");
    Key(state, 0x10, scan: 0x36); Drain(state);
    Key(state, 0x10, true, scan: 0x2A); var oneShift = Take(state); state.Complete(oneShift.Sequence);
    Check(oneShift.Modifiers == OverlayModifiers.Shift && oneShift.KeyboardState[0xA1] == 0x80,
        "Releasing left Shift does not clear a held right Shift");
    Key(state, 0x11, scan: 0x1D, flags: 1); var rightControl = Take(state); state.Complete(rightControl.Sequence);
    Check(rightControl.KeyboardState[0xA3] == 0x80 && (rightControl.Modifiers & OverlayModifiers.Control) != 0,
        "Extended generic Ctrl maps to right Ctrl");
    state = New();
    Key(state, 0x14); var caps = Take(state); state.Complete(caps.Sequence);
    Key(state, 0x14); var capsRepeat = Take(state); state.Complete(capsRepeat.Sequence);
    Check(caps.KeyboardState[0x14] == 0x81 && capsRepeat.KeyboardState[0x14] == 0x81,
        "Caps Lock toggles only on the first down, not repeat");
    Key(state, 0x14, true); Drain(state); Key(state, 0x14); var capsAgain = Take(state); state.Complete(capsAgain.Sequence);
    Check(capsAgain.KeyboardState[0x14] == 0x80, "A fresh Caps Lock press toggles the local bit back");

    var initial = new byte[256]; initial[0xA2] = initial[0x11] = initial[0xA4] = initial[0x12] = initial[0x46] = 0x80;
    state = New(initial);
    Check(!Key(state, 0x46) && !Key(state, 0x46, true) && !Key(state, 0xA2, true) && !Key(state, 0xA4, true),
        "Entering through a globally held Ctrl+Alt+F preserves each repeat and physical up");
    Check(state.QueuedCount == 0 && state.IsActive, "Inherited releases do not create fake relays or toggle commands");
    Check(Key(state, 0x46), "A released inherited key becomes isolated on its next fresh down");

    state = new(256, 2000);
    Check(!Key(state, 0xA2, own: false), "Inactive physical down passes before lease acquisition");
    state.Begin(new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 120);
    Check(!Key(state, 0xA2, true, now: 130), "A just-passed down remains inherited even before its native async state is visible");

    foreach (bool hide in new[] { false, true })
    {
        state = New(); Key(state, 0xA2); Key(state, 0xA4);
        if (hide) Key(state, 0xA0);
        Check(Key(state, 0x46), "Configured command trigger is swallowed");
        var packets = Drain(state);
        Check(!state.IsActive && packets.Count(p => p.Kind == (hide ? KeyboardInputPacketKind.Hide : KeyboardInputPacketKind.Toggle)) == 1,
            "Configured chord emits exactly one private command and ends the lease");
        Check(packets.Last().Kind == KeyboardInputPacketKind.LeaseEnded && packets.Where(p => p.Kind == KeyboardInputPacketKind.Key).All(p => p.IsLocalBalance && p.IsKeyUp),
            "Commands discard pending keydowns and precede their end marker with local balancing ups");
        Check(Key(state, 0x46, now: 140) && Key(state, 0x46, true, now: 150), "Consumed command repeat and physical tail up cannot leak");
        Check(!Key(state, 0x46, now: 160), "A new inactive press after the tail release passes normally");
    }

    state = New(); Key(state, 0x58, scan: 45); state.End(KeyboardLeaseEndReason.Explicit, 200);
    var ended = Drain(state);
    Check(ended.Count == 2 && ended[0].IsLocalBalance && ended[0].KeyboardState[0x58] == 0,
        "EndLease clears queued down and supplies a local balancing release");
    Check(Key(state, 0x58, now: 250, scan: 45), "A bounded held repeat tail stays isolated after lease end");
    Check(!Key(state, 0x59, now: 250), "A different new key is never captured by the tail");
    Check(!Key(state, 0x58, true, now: 260, scan: 46), "A mismatching scan-code up cannot consume the original key tail");
    Check(Key(state, 0x58, true, now: 270, scan: 45), "The matching observed up clears the tail");
    Check(!Key(state, 0x58, now: 280, scan: 45), "The next fresh press is passed");
    state = New(); Key(state, 0x58, scan: 45); state.End(KeyboardLeaseEndReason.HeartbeatExpired, 200);
    Check(!Key(state, 0x58, now: 2200, scan: 45), "Tail expiry fails open instead of holding keys indefinitely");
    state = New(); Key(state, 0x58, scan: 45); state.End(KeyboardLeaseEndReason.Explicit, 200);
    for (int i = 1; i <= 8; i++) Check(Key(state, 0x58, now: 200 + i * 1500, scan: 45),
        "Continuing held-key repeats refresh the tail instead of leaking after two seconds total");
    Check(Key(state, 0x58, true, now: 13000, scan: 45) && !Key(state, 0x58, now: 13010, scan: 45),
        "A long repeating tail ends only on its matching up, then a fresh press passes");

    foreach (var loss in new[] { KeyboardLeaseEndReason.ForegroundLost, KeyboardLeaseEndReason.HeartbeatExpired })
    {
        state = New(); Key(state, 0x58);
        Check(!Key(state, 0x59, own: loss != KeyboardLeaseEndReason.ForegroundLost,
            healthy: loss != KeyboardLeaseEndReason.HeartbeatExpired), "Loss event fails open for a new key");
        Check(!state.IsActive && state.LastEndReason == loss && Drain(state).Last().EndReason == loss,
            "Foreground/heartbeat loss ends the lease with an explicit control packet");
    }
    foreach (bool upAtLoss in new[] { false, true })
    {
        state = New(); Key(state, 0x58, scan: 45);
        Check(Key(state, 0x58, upAtLoss, scan: 45, own: false) && !state.IsActive,
            "The event revealing focus loss cannot leak a previously swallowed repeat or up");
    }
    state = New(); Key(state, 0xA4);
    Check(!Key(state, 9) && !state.IsActive && state.LastEndReason == KeyboardLeaseEndReason.SystemEscape,
        "Alt+Tab ends the lease and passes Tab without pretending to restore a global Alt");
    state = New(); Check(!Key(state, 0x5B) && !state.IsActive, "Windows key immediately exits isolation and passes to the OS");
    state = New(); Key(state, 0xA2); Key(state, 0xA4);
    Check(Key(state, 0x1B) && !state.IsActive && state.LastEndReason == KeyboardLeaseEndReason.EmergencyEscape,
        "Ctrl+Alt+Esc terminates the lease directly in pure hook state, without UI cooperation");

    state = New();
    for (int i = 0; i < 256; i++) Check(Key(state, 0x58), "A packet within capacity can be suppressed");
    Check(!Key(state, 0x59) && !state.IsActive && state.LastEndReason == KeyboardLeaseEndReason.QueueOverflow,
        "Overflow immediately passes the new, undeliverable key and ends the lease");
    Check(state.QueuedCount <= 256 && Drain(state).All(p => p.IsLocalBalance || p.Kind == KeyboardInputPacketKind.LeaseEnded),
        "Overflow removes pending input and retains only bounded cleanup controls");

    state = New(); Key(state, 0x58); var inFlight = Take(state);
    state.End(KeyboardLeaseEndReason.Explicit, 200);
    long nextLease = state.Begin(new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 210);
    Key(state, 0x59, now: 220); state.End(KeyboardLeaseEndReason.Explicit, 230);
    Check(!state.TryDequeue(out _) && !state.NeedsNotification, "Quick End/Begin cannot bypass an old in-flight packet");
    Check(state.Complete(inFlight.Sequence), "Completing an old in-flight packet still releases the single-packet barrier");
    var compacted = Drain(state);
    Check(compacted.Count(p => p.IsLocalBalance && p.VirtualKey == 0x58) == 1 &&
        compacted.Count(p => p.IsLocalBalance && p.VirtualKey == 0x59) == 1 && compacted.Last().LeaseId == nextLease,
        "Quick reentry compacts but preserves both old and new pending local releases");
    Check(!state.Complete(inFlight.Sequence), "Duplicate completion cannot release another packet");
    state = New(); Key(state, 0x58); var pendingDown = Take(state);
    Key(state, 0x58, true); state.End(KeyboardLeaseEndReason.Explicit, 200);
    state.Complete(pendingDown.Sequence);
    var releasedWhileInFlight = Drain(state);
    Check(releasedWhileInFlight.Any(p => p.IsLocalBalance && p.VirtualKey == 0x58 && p.IsKeyUp),
        "EndLease balances an in-flight down even if the physical up was queued and then discarded");
    state = New(); Key(state, 0xA2); var oldControl = Take(state);
    state.End(KeyboardLeaseEndReason.Explicit, 200);
    var newInitial = new byte[256]; newInitial[1] = newInitial[5] = 0x80;
    state.Begin(newInitial, HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 210);
    var resetState = state.CopyLocalState();
    Check(resetState[0x11] == 0 && resetState[0xA2] == 0 && resetState[1] == 0x80 && resetState[5] == 0x80,
        "A new lease snapshot clears old private Ctrl without waiting for its balance, preserving current mouse bits");
    resetState[0xA2] = 0x80;
    Check(state.CopyLocalState()[0xA2] == 0, "A caller-owned initialization snapshot cannot mutate state-machine storage");
    state.Complete(oldControl.Sequence);
    foreach (var invalidBinding in new[] { new HotkeyBinding(8, 0x46), new HotkeyBinding(0, 0x5B),
        new HotkeyBinding(3, 0x1B), new HotkeyBinding(7, 0x1B), new HotkeyBinding(1, 9), new HotkeyBinding(2, 0x1B) })
    {
        state = New();
        bool rejected = false;
        try { state.Begin(new byte[256], invalidBinding, HotkeyBinding.DefaultHide, 200); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected && state.IsActive && state.LeaseId == 1,
            "Win/system/emergency hotkeys are explicitly rejected without replacing a valid lease");
    }
    state = New();
    state.UpdateHotkeys(new(6, 0x47), new(7, 0x48));
    Key(state, 0xA2); Key(state, 0xA4); Key(state, 0x46);
    Check(state.IsActive && Drain(state).All(p => p.Kind == KeyboardInputPacketKind.Key),
        "An atomic binding update stops recognizing the old chord without ending the lease");
    Key(state, 0x46, true); Key(state, 0xA4, true); Key(state, 0xA0); Key(state, 0x47);
    Check(!state.IsActive && Drain(state).Count(p => p.Kind == KeyboardInputPacketKind.Toggle) == 1,
        "The updated toggle binding works inside the same lease");
    state = New(); bool rejectedUpdate = false;
    try { state.UpdateHotkeys(new(8, 0x47), HotkeyBinding.DefaultHide); }
    catch (ArgumentException) { rejectedUpdate = true; }
    Key(state, 0xA2); Key(state, 0xA4); Key(state, 0x46);
    Check(rejectedUpdate && Drain(state).Count(p => p.Kind == KeyboardInputPacketKind.Toggle) == 1,
        "Rejected bindings leave the previous active command configuration intact");
    Check(!Key(state, 0) && !Key(state, 255), "Invalid keyboard VKs are never captured");
    Console.WriteLine($"Windows keyboard state and monitor selection checks: {checks}; passed=true; native hooks/UI/input never run.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error); Console.WriteLine($"Windows keyboard state and monitor selection checks: {checks}; passed=false"); return 1;
}
