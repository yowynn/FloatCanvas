using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Owns held-key lifetimes. All event sources use
/// the same release path. No UI, OS calls, history or persistence lives here.</summary>
internal sealed class PresentationKeyboardState
{
    internal readonly record struct Identity(long Device, int Scan, int VirtualKey, uint Flags);
    internal enum ChangeKind { Pressed, Released, Cancelled, Reset }
    internal readonly record struct Change(ChangeKind Kind, Identity Key, PresentationKeyCap Cap);
    private readonly Dictionary<Identity, PresentationKeyCap> held = [];
    internal IReadOnlyDictionary<Identity, PresentationKeyCap> Held => held;
    internal bool HasHeldKeys => held.Count != 0;

    internal void Clear()
    { held.Clear(); }

    internal void ReleaseAll(Action<Change> changed)
    {
        foreach (var key in held.Keys.ToArray()) Release(key, changed, cancelled: true);
    }
    internal void RemoveDevice(long device, PresentationDeviceKind kind, Action<Change> changed)
    {
        // Low-level hooks have no device handle (Device=0). Release those keys only
        // when Windows positively identifies a removed keyboard. Unknown can be an
        // uncached mouse removal and must not manufacture a keyboard release.
        bool anonymous = kind == PresentationDeviceKind.Keyboard;
        foreach (var key in held.Keys.Where(key => key.Device == device || anonymous && key.Device == 0).ToArray())
            Release(key, changed, cancelled: true);
    }
    internal void Apply(PresentationObservedInput input, bool allPhysicalKeys, Action<Change> changed)
    {
        int vk = input.VirtualKey;
        if (vk is <= 0 or >= 255) return;
        var key = new Identity(input.Device, input.ScanCode, input.ScanCode is 0 or 0x45 ? vk : 0, input.KeyFlags);
        bool up = input.Kind == PresentationObservedKind.KeyUp;
        if (up)
        {
            Release(key, changed);
            return;
        }
        if (held.ContainsKey(key)) return;
        bool modifier = IsModifier(vk);
        if (!allPhysicalKeys && !modifier && !HasShortcutModifier() && !IsNamedControl(vk)) return;
        string label = input.KeyLabel is { Length: > 0 and <= 64 } text && !text.Any(char.IsControl) ? text : "Key " + vk;
        var cap = new PresentationKeyCap(vk, label, modifier, input.ScanCode, (input.KeyFlags & 2) != 0);
        Add(key, cap, changed);
    }
    private void Add(Identity key, PresentationKeyCap cap, Action<Change> changed)
    {
        if (held.Count >= PresentationLimits.MaximumHeldKeys)
        { held.Clear(); changed(new(ChangeKind.Reset, default, cap)); }
        held.Add(key, cap); changed(new(ChangeKind.Pressed, key, cap));
    }
    private void Release(Identity key, Action<Change> changed, bool cancelled = false)
    { if (held.Remove(key, out var cap)) changed(new(cancelled ? ChangeKind.Cancelled : ChangeKind.Released, key, cap)); }
    internal static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;
    private bool HasShortcutModifier() => held.Values.Any(cap => cap.VirtualKey is 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA2 and <= 0xA5);
    private static bool IsNamedControl(int vk) => vk is 8 or 9 or 13 or 19 or 20 or 27 or 32 or >= 33 and <= 46 or
        >= 0x60 and <= 0x6F or >= 0x70 and <= 0x87 or
        0x90 or 0x91 or 0x5F or >= 0xA6 and <= 0xB7;
}
