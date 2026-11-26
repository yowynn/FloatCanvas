using FloatCanvas.Core;
using FloatCanvas.Windows;
using PhysicalKey = FloatCanvas.App.PresentationKeyboardState.Identity;

namespace FloatCanvas.App;

internal sealed record PresentationRuntimeDemand(bool Keyboard, bool Pointer, bool Clicks, bool Trail,
    bool AllPhysicalKeys, double KeyHoldMilliseconds, double ClickMilliseconds, double TrailMilliseconds)
{
    internal static PresentationRuntimeDemand Empty { get; } = new(false, false, false, false, false, 900, 450, 800);
    internal bool ScopedKeyboard { get; init; }
    internal bool MouseButtons { get; init; }
    internal bool GamepadButtons { get; init; }
    internal bool Any => Keyboard || Pointer;
}

/// <summary>Bounded, transient input state. It has no document, asset, history or file dependency.</summary>
internal sealed class PresentationTimeline
{
    private sealed record ChordState(PhysicalKey Main, PresentationKeyCap[] Keys, TimeSpan Time, bool Held);
    private readonly PresentationKeyboardState keyboard = new();
    private readonly HashSet<(PresentationDeviceKind Kind, long Device, int Code)> heldButtons = [];
    private readonly Dictionary<(long Device, int Code), TimeSpan> wheelPulses = [];
    private static readonly TimeSpan WheelHighlight = TimeSpan.FromMilliseconds(150);
    private IReadOnlyDictionary<PhysicalKey, PresentationKeyCap> held => keyboard.Held;
    private readonly Dictionary<PhysicalKey, PresentationKeyCap> pendingModifiers = [];
    private TimeSpan pendingModifierTime;
    private readonly List<ChordState> chords = [];
    private readonly List<PresentationClick> clicks = [];
    private readonly List<PresentationTrailSample> trail = [];
    private PointD? cursor;
    private PresentationLockState lockState;
    private TimeSpan lastTime;
    internal long Revision { get; private set; }
    internal bool KeyboardSuppressed { get; private set; }
    internal bool HasTransients => wheelPulses.Count > 0 || clicks.Count > 0 || trail.Count > 0 || chords.Any(chord => !chord.Held);
    internal bool HasHeldKeys => keyboard.HasHeldKeys;

    internal void Clear()
    {
        keyboard.Clear(); heldButtons.Clear(); wheelPulses.Clear(); pendingModifiers.Clear(); chords.Clear(); clicks.Clear(); trail.Clear(); cursor = null;
        lockState = PresentationLockState.None; lastTime = TimeSpan.Zero;
        KeyboardSuppressed = false; Revision++;
    }
    internal void SetKeyboardSuppressed(bool value)
    {
        if (KeyboardSuppressed == value) return;
        KeyboardSuppressed = value; keyboard.Clear(); heldButtons.Clear(); wheelPulses.Clear(); pendingModifiers.Clear(); chords.Clear(); Revision++;
    }
    internal void ClearKeyboard()
    { keyboard.Clear(); heldButtons.Clear(); wheelPulses.Clear(); pendingModifiers.Clear(); chords.Clear(); KeyboardSuppressed = false; Revision++; }
    internal void ReleaseHeldKeys(TimeSpan timestamp)
    {
        if (heldButtons.Count > 0) { heldButtons.Clear(); Revision++; }
        if (wheelPulses.Count > 0) { wheelPulses.Clear(); Revision++; }
        if (!keyboard.HasHeldKeys) return;
        timestamp = timestamp < lastTime ? lastTime : timestamp;
        keyboard.ReleaseAll(change => OnKeyChanged(change, timestamp)); lastTime = timestamp;
    }
    internal void Apply(PresentationObservedBatch batch, PresentationRuntimeDemand demand, TimeSpan now)
    {
        if (batch.Overflowed)
        {
            bool suppressed = KeyboardSuppressed; var previousLockState = lockState;
            var previousTime = lastTime;
            Clear();
            KeyboardSuppressed = suppressed; lockState = previousLockState; lastTime = previousTime;
        }
        now = now < lastTime ? lastTime : now;
        Trim(now, demand);
        foreach (var input in batch.Changes)
        {
            if (input.Milliseconds < 0) continue;
            var timestamp = FromMilliseconds(input.Milliseconds); if (timestamp > now) timestamp = now;
            if (timestamp < lastTime) timestamp = lastTime; lastTime = timestamp;
            if (input.Kind == PresentationObservedKind.DeviceRemoved)
            {
                if (heldButtons.RemoveWhere(key => key.Kind == input.DeviceKind && key.Device == input.Device) > 0) Revision++;
                if (input.DeviceKind == PresentationDeviceKind.Mouse &&
                    (wheelPulses.Remove((input.Device, 6)) | wheelPulses.Remove((input.Device, 7)))) Revision++;
                keyboard.RemoveDevice(input.Device, input.DeviceKind, change => OnKeyChanged(change, timestamp)); continue;
            }
            if (input.Kind == PresentationObservedKind.LockStateChanged)
            {
                var flag = input.VirtualKey switch
                {
                    0x14 => PresentationLockState.CapsLock,
                    0x90 => PresentationLockState.NumLock,
                    0x91 => PresentationLockState.ScrollLock,
                    _ => PresentationLockState.None
                };
                if (flag == PresentationLockState.None) continue;
                var next = input.IsLocked ? lockState | flag : lockState & ~flag;
                if (next != lockState) { lockState = next; Revision++; }
                continue;
            }
            if (input.Kind is PresentationObservedKind.KeyDown or PresentationObservedKind.KeyUp)
            {
                if (!demand.Keyboard || KeyboardSuppressed || input.VirtualKey is <= 0 or > 255) continue;
                keyboard.Apply(input, demand.AllPhysicalKeys, change => OnKeyChanged(change, timestamp));
            }
            else if (input.Kind == PresentationObservedKind.Wheel && demand.Keyboard && demand.MouseButtons &&
                     !KeyboardSuppressed && input.DeviceKind == PresentationDeviceKind.Mouse && input.Button is 5 or 6)
            {
                var pulse = (input.Device, input.Button + 1);
                if (wheelPulses.Count < PresentationLimits.MaximumHeldKeys || wheelPulses.ContainsKey(pulse))
                {
                    wheelPulses[pulse] = timestamp + WheelHighlight;
                    Revision++;
                }
            }
            else if (input.Kind is PresentationObservedKind.ButtonDown or PresentationObservedKind.ButtonUp)
            {
                bool down = input.Kind == PresentationObservedKind.ButtonDown;
                bool observeButton = demand.Keyboard && !KeyboardSuppressed && (input.DeviceKind switch
                {
                    PresentationDeviceKind.Mouse => demand.MouseButtons && input.Button is >= 0 and < 5,
                    PresentationDeviceKind.Gamepad => demand.GamepadButtons && input.Button is >= 0 and < (int)PresentationGamepadButton.Y,
                    _ => false
                });
                var button = (input.DeviceKind, input.Device, input.Button + 1);
                if (observeButton && (down ? heldButtons.Count < PresentationLimits.MaximumHeldKeys && heldButtons.Add(button) : heldButtons.Remove(button)))
                    Revision++;
                if (down && input.DeviceKind != PresentationDeviceKind.Gamepad && demand.Clicks && input.HasPosition && input.Button is >= 0 and < 5)
                {
                    if (clicks.Count == PresentationLimits.MaximumClicks) clicks.RemoveAt(0);
                    clicks.Add(new(new(input.X, input.Y), (PresentationMouseButton)input.Button, timestamp)); Revision++;
                }
            }
        }
        if (demand.Pointer && batch.Cursor is { } movement)
        {
            var point = new PointD(movement.X, movement.Y); var timestamp = FromMilliseconds(Math.Max(0, movement.Milliseconds));
            if (timestamp > now) timestamp = now; if (timestamp < lastTime) timestamp = lastTime; lastTime = timestamp;
            if (cursor != point)
            {
                cursor = point; Revision++;
                if (demand.Trail && (trail.Count == 0 || trail[^1].ScreenPoint.DistanceTo(point) >= .75))
                {
                    bool start = trail.Count == 0 || timestamp - trail[^1].Timestamp > TimeSpan.FromMilliseconds(200);
                    if (trail.Count == PresentationLimits.MaximumTrailPoints) { trail.RemoveAt(0); if (trail.Count > 0) trail[0] = trail[0] with { StartsSegment = true }; }
                    trail.Add(new(point, timestamp, start));
                }
            }
        }
    }
    internal void Trim(TimeSpan time, PresentationRuntimeDemand demand)
    {
        int removed = clicks.RemoveAll(click => time - click.Timestamp >= TimeSpan.FromMilliseconds(demand.ClickMilliseconds));
        removed += trail.RemoveAll(point => time - point.Timestamp >= TimeSpan.FromMilliseconds(demand.TrailMilliseconds));
        removed += chords.RemoveAll(chord => !chord.Held && time - chord.Time >= TimeSpan.FromMilliseconds(demand.KeyHoldMilliseconds));
        foreach (var pulse in wheelPulses.Where(pulse => pulse.Value <= time).Select(pulse => pulse.Key).ToArray())
            if (wheelPulses.Remove(pulse)) removed++;
        if (removed > 0) { if (trail.Count > 0) trail[0] = trail[0] with { StartsSegment = true }; Revision++; }
    }
    internal PresentationFrame Capture(long epoch, long sequence, TimeSpan now)
    {
        if (now < lastTime) now = lastTime;
        var recent = chords.Select(chord => new PresentationChord(chord.Keys, chord.Time, chord.Held)).ToList();
        var pending = PendingCaps();
        if (pending.Length > 0)
        {
            if (recent.Count == PresentationLimits.MaximumChords) recent.RemoveAt(0);
            recent.Add(new PresentationChord(pending, pendingModifierTime, true));
        }
        var buttons = heldButtons.Select(button => (Device: button.Kind == PresentationDeviceKind.Mouse
            ? PresentationKeyDevice.Mouse : PresentationKeyDevice.Gamepad, button.Code))
            .Concat(wheelPulses.Where(pulse => pulse.Value > now).Select(pulse => (Device: PresentationKeyDevice.Mouse, pulse.Key.Code))).Distinct()
            .OrderBy(button => button.Device).ThenBy(button => button.Code)
            .Select(button => new PresentationKeyCap(button.Code, PresentationDeviceButtons.Label(button.Device, button.Code), DeviceType: button.Device));
        return new(epoch, sequence, now, KeyboardSuppressed ? [] : buttons.Concat(Caps()).Take(PresentationLimits.MaximumHeldKeys),
            KeyboardSuppressed ? [] : recent, cursor, clicks, trail, KeyboardSuppressed,
            KeyboardSuppressed ? PresentationLockState.None : lockState);
    }
    private void OnKeyChanged(PresentationKeyboardState.Change change, TimeSpan timestamp)
    {
        Revision++;
        var key = change.Key;
        switch (change.Kind)
        {
            case PresentationKeyboardState.ChangeKind.Reset:
                pendingModifiers.Clear(); chords.Clear(); break;
            case PresentationKeyboardState.ChangeKind.Cancelled:
                pendingModifiers.Remove(key); ReleaseChords(timestamp); break;
            case PresentationKeyboardState.ChangeKind.Released:
                if (pendingModifiers.ContainsKey(key))
                {
                    bool sameFamilyRemains = pendingModifiers.Any(other => other.Key != key &&
                        held.ContainsKey(other.Key) && ModifierFamily(other.Value.VirtualKey) == ModifierFamily(change.Cap.VirtualKey));
                    if (!sameFamilyRemains) CommitPending(timestamp, key);
                    else pendingModifiers.Remove(key);
                }
                ReleaseChords(timestamp); break;
            case PresentationKeyboardState.ChangeKind.Pressed:
                if (change.Cap.IsModifier)
                {
                    if (pendingModifiers.Count == 0) pendingModifierTime = timestamp;
                    pendingModifiers[key] = change.Cap;
                }
                else
                {
                    pendingModifiers.Clear();
                    if (chords.Count == PresentationLimits.MaximumChords) chords.RemoveAt(0);
                    chords.Add(new(key, ChordCaps(change.Cap), timestamp, true));
                }
                break;
        }
    }
    private void CommitPending(TimeSpan timestamp, PhysicalKey key)
    {
        var preview = PendingCaps();
        if (preview.Length > 0)
        {
            if (chords.Count == PresentationLimits.MaximumChords) chords.RemoveAt(0);
            chords.Add(new(key, preview, timestamp, false));
        }
        pendingModifiers.Clear();
    }
    private void ReleaseChords(TimeSpan timestamp)
    {
        for (int index = 0; index < chords.Count; index++)
        {
            var chord = chords[index];
            if (chord.Held && (!held.ContainsKey(chord.Main) || chord.Keys.Any(key => key.IsModifier &&
                !held.Values.Any(current => current.IsModifier && ModifierFamily(current.VirtualKey) == key.VirtualKey))))
                chords[index] = chord with { Held = false, Time = timestamp };
        }
    }
    private PresentationKeyCap[] PendingCaps() => pendingModifiers.Values
        .GroupBy(cap => ModifierFamily(cap.VirtualKey))
        .Select(group => group.OrderBy(cap => cap.VirtualKey).First() with { VirtualKey = group.Key })
        .OrderBy(cap => ModifierOrder(cap.VirtualKey)).ToArray();
    private PresentationKeyCap[] ChordCaps(PresentationKeyCap main) => held.Values.Where(cap => cap.IsModifier)
        .GroupBy(cap => ModifierFamily(cap.VirtualKey))
        .Select(group => group.OrderBy(cap => cap.VirtualKey).First() with { VirtualKey = group.Key })
        .OrderBy(cap => ModifierOrder(cap.VirtualKey)).Append(main).ToArray();
    private PresentationKeyCap[] Caps() => held.Values.DistinctBy(cap => (cap.VirtualKey, cap.ScanCode, cap.IsExtended))
        .OrderBy(cap => ModifierOrder(cap.VirtualKey)).ThenBy(cap => cap.VirtualKey).ThenBy(cap => cap.ScanCode).ThenBy(cap => cap.IsExtended)
        .Take(PresentationLimits.MaximumHeldKeys).ToArray();
    private static int ModifierFamily(int vk) => vk switch
    { 0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, 0x5C => 0x5B, _ => vk };
    internal static bool IsModifier(int vk) => PresentationKeyboardState.IsModifier(vk);
    private static int ModifierOrder(int vk) => vk is 0x11 or 0xA2 or 0xA3 ? 0 : vk is 0x12 or 0xA4 or 0xA5 ? 1 : vk is 0x10 or 0xA0 or 0xA1 ? 2 : vk is 0x5B or 0x5C ? 3 : 4;
    internal static TimeSpan FromMilliseconds(long value) => TimeSpan.FromTicks(Math.Clamp(value, 0, TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond) * TimeSpan.TicksPerMillisecond);
}
