using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed record PresentationRuntimeDemand(bool Keyboard, bool Pointer, bool Clicks, bool Trail,
    bool AllPhysicalKeys, double KeyHoldMilliseconds, double ClickMilliseconds, double TrailMilliseconds)
{
    internal static PresentationRuntimeDemand Empty { get; } = new(false, false, false, false, false, 900, 450, 800);
    internal bool Any => Keyboard || Pointer;
}

/// <summary>Bounded, transient input state. It has no document, asset, history or file dependency.</summary>
internal sealed class PresentationTimeline
{
    private readonly record struct PhysicalKey(long Device, int Scan, int VirtualKey, uint Flags);
    private sealed record ChordState(PhysicalKey Main, PresentationKeyCap[] Keys, TimeSpan Time, bool Held);
    private readonly Dictionary<PhysicalKey, PresentationKeyCap> held = [];
    private readonly List<ChordState> chords = [];
    private readonly List<PresentationClick> clicks = [];
    private readonly List<PresentationTrailSample> trail = [];
    private PointD? cursor;
    private TimeSpan lastTime;
    internal long Revision { get; private set; }
    internal bool KeyboardSuppressed { get; private set; }
    internal bool HasTransients => clicks.Count > 0 || trail.Count > 0 || chords.Any(chord => !chord.Held);

    internal void Clear()
    {
        held.Clear(); chords.Clear(); clicks.Clear(); trail.Clear(); cursor = null; lastTime = TimeSpan.Zero;
        KeyboardSuppressed = false; Revision++;
    }
    internal void SetKeyboardSuppressed(bool value)
    {
        if (KeyboardSuppressed == value) return;
        KeyboardSuppressed = value; held.Clear(); chords.Clear(); Revision++;
    }
    internal void ClearKeyboard()
    { held.Clear(); chords.Clear(); KeyboardSuppressed = false; Revision++; }
    internal void ReleaseHeldKeys(TimeSpan timestamp)
    {
        if (held.Count == 0) return;
        timestamp = timestamp < lastTime ? lastTime : timestamp;
        held.Clear(); ReleaseChords(timestamp); lastTime = timestamp; Revision++;
    }
    internal void Apply(PresentationObservedBatch batch, PresentationRuntimeDemand demand, TimeSpan now)
    {
        if (batch.Overflowed)
        {
            bool suppressed = KeyboardSuppressed;
            var previousTime = lastTime;
            Clear();
            KeyboardSuppressed = suppressed; lastTime = previousTime;
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
                foreach (var key in held.Keys.Where(key => key.Device == input.Device).ToArray()) held.Remove(key);
                ReleaseChords(timestamp); Revision++; continue;
            }
            if (input.Kind is PresentationObservedKind.KeyDown or PresentationObservedKind.KeyUp)
            {
                if (!demand.Keyboard || KeyboardSuppressed || input.VirtualKey is <= 0 or > 255) continue;
                // VK can change for the same physical key when NumLock or the
                // keyboard layout changes between make and break.
                // Pause and NumLock share scan 0x45 and still need VK disambiguation.
                var key = new PhysicalKey(input.Device, input.ScanCode, input.ScanCode is 0 or 0x45 ? input.VirtualKey : 0, input.KeyFlags);
                if (input.Kind == PresentationObservedKind.KeyUp)
                { if (held.Remove(key)) { ReleaseChords(timestamp); Revision++; } continue; }
                if (held.ContainsKey(key)) continue; // Repeats never grow the timeline.
                bool modifier = IsModifier(input.VirtualKey);
                if (!demand.AllPhysicalKeys && !modifier && !HasShortcutModifier() && !IsNamedControl(input.VirtualKey)) continue;
                string label = input.KeyLabel is { Length: > 0 and <= 64 } text && !text.Any(char.IsControl) ? text : "Key " + input.VirtualKey;
                if (held.Count == PresentationLimits.MaximumHeldKeys) { held.Clear(); chords.Clear(); }
                var cap = new PresentationKeyCap(input.VirtualKey, label, modifier, input.ScanCode, (input.KeyFlags & 2) != 0); held.Add(key, cap); Revision++;
                if (!modifier)
                {
                    var keys = ChordCaps(cap);
                    if (chords.Count == PresentationLimits.MaximumChords) chords.RemoveAt(0);
                    chords.Add(new(key, keys, timestamp, true));
                }
            }
            else if (input.Kind == PresentationObservedKind.ButtonDown && demand.Clicks && input.HasPosition && input.Button is >= 0 and <= 4)
            {
                if (clicks.Count == PresentationLimits.MaximumClicks) clicks.RemoveAt(0);
                clicks.Add(new(new(input.X, input.Y), (PresentationMouseButton)input.Button, timestamp)); Revision++;
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
        if (removed > 0) { if (trail.Count > 0) trail[0] = trail[0] with { StartsSegment = true }; Revision++; }
    }
    internal PresentationFrame Capture(long epoch, long sequence, TimeSpan now)
    {
        if (now < lastTime) now = lastTime;
        return new(epoch, sequence, now, KeyboardSuppressed ? [] : Caps(),
            KeyboardSuppressed ? [] : chords.Select(chord => new PresentationChord(chord.Keys, chord.Time, chord.Held)), cursor, clicks, trail, KeyboardSuppressed);
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
    private PresentationKeyCap[] ChordCaps(PresentationKeyCap main) => held.Values.Where(cap => cap.IsModifier)
        .GroupBy(cap => ModifierFamily(cap.VirtualKey))
        .Select(group => group.OrderBy(cap => cap.VirtualKey).First() with { VirtualKey = group.Key })
        .OrderBy(cap => ModifierOrder(cap.VirtualKey)).Append(main).ToArray();
    private PresentationKeyCap[] Caps() => held.Values.DistinctBy(cap => (cap.VirtualKey, cap.ScanCode, cap.IsExtended))
        .OrderBy(cap => ModifierOrder(cap.VirtualKey)).ThenBy(cap => cap.VirtualKey).ThenBy(cap => cap.ScanCode).ThenBy(cap => cap.IsExtended)
        .Take(PresentationLimits.MaximumHeldKeys).ToArray();
    private static int ModifierFamily(int vk) => vk switch
    { 0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, 0x5C => 0x5B, _ => vk };
    private bool HasShortcutModifier() => held.Values.Any(cap => cap.VirtualKey is 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA2 and <= 0xA5);
    internal static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;
    private static bool IsNamedControl(int vk) => vk is 8 or 9 or 13 or 19 or 20 or 27 or 32 or >= 33 and <= 46 or >= 0x70 and <= 0x87 or 0x90 or 0x91;
    private static int ModifierOrder(int vk) => vk is 0x11 or 0xA2 or 0xA3 ? 0 : vk is 0x12 or 0xA4 or 0xA5 ? 1 : vk is 0x10 or 0xA0 or 0xA1 ? 2 : vk is 0x5B or 0x5C ? 3 : 4;
    internal static TimeSpan FromMilliseconds(long value) => TimeSpan.FromTicks(Math.Clamp(value, 0, TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond) * TimeSpan.TicksPerMillisecond);
}
