using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Windows;

/// <summary>Hidden DesktopSession fixtures inject keys without owning real keyboard input.</summary>
internal static class DesktopSyntheticKeyboard
{
    internal static void Allow(DesktopSession desktop, OverlayHost host)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var router = (SessionKeyboardRouter)typeof(DesktopSession).GetField("_keyboardRouter", fields)!.GetValue(desktop)!;
        // Replace only the fixture's input-lease predicate. Keep production routing,
        // repeat ownership and command dispatch; never acquire an actual input lease.
        typeof(SessionKeyboardRouter).GetField("active", fields)!.SetValue(router, (Func<bool>)(() => host.IsEditing));
    }
}
