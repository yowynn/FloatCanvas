using System.Windows.Input;

// Routed key fixtures own their device state. They do not read or change the
// user's keyboard or inherit modifiers from a preceding HWND/input fixture.
internal sealed class TestKeyboardDevice(ModifierKeys modifiers = ModifierKeys.None) : KeyboardDevice(InputManager.Current)
{
    protected override KeyStates GetKeyStatesFromSystem(Key key)
    {
        var modifier = key switch
        {
            Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
            Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
            Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
            Key.LWin or Key.RWin => ModifierKeys.Windows,
            _ => ModifierKeys.None
        };
        return modifier != ModifierKeys.None && (modifiers & modifier) != 0 ? KeyStates.Down : KeyStates.None;
    }
}
