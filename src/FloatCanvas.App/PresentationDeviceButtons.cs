using System.Runtime.InteropServices;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class PresentationDeviceButtons
{
    private static readonly string[] mouseNames = ["MouseLeft", "MouseRight", "MouseMiddle", "MouseX1", "MouseX2"];
    private static readonly string[] mouseLabels = ["LMB", "RMB", "MMB", "MB4", "MB5", "Wheel ↑", "Wheel ↓"];
    private static readonly string[] gamepadLabels = ["↑", "↓", "←", "→", "Start", "Back", "LS", "RS", "LB", "RB", "A", "B", "X", "Y"];
    internal const int GamepadSlots = 4;
    internal static string Label(PresentationKeyDevice type, int code) => type switch
    {
        PresentationKeyDevice.Mouse => mouseLabels[code - 1],
        PresentationKeyDevice.Gamepad => L.Format("Presentation.Layout.GamepadButton", gamepadLabels[code - 1]),
        _ => HotkeyDisplay.KeyName((uint)code)
    };
    internal static string Name(PresentationKeyDevice type, int code) => type == PresentationKeyDevice.Mouse
        ? code switch
        {
            6 => L.Get("Presentation.Layout.WheelUp"),
            7 => L.Get("Presentation.Layout.WheelDown"),
            _ => L.Get("Input.EntryRecovery." + mouseNames[code - 1])
        }
        : Label(type, code);

    // XInput places the face buttons above two reserved bits. Normalize the
    // layout so stored button identities do not depend on a driver bit mask.
    internal static ushort ReadGamepad(int slot) => OperatingSystem.IsWindows() && XInputGetState((uint)slot, out var state) == 0
        ? (ushort)((state.Buttons & 0x03FF) | ((state.Buttons & 0xF000) >> 2))
        : (ushort)0;

    [StructLayout(LayoutKind.Sequential)]
    private struct GamepadState
    {
        internal uint Packet;
        internal ushort Buttons;
        internal byte LeftTrigger, RightTrigger;
        internal short LeftX, LeftY, RightX, RightY;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetState(uint slot, out GamepadState state);
}
