using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class PresentationEditingInputRouteTests
{
    internal static void Run(Action<string, bool> check)
    {
        using var h = new PresentationHookTestHarness(false);
        var b = h.Broker; long token = b.InputEpoch;
        PresentationObservedInput Click() => new(PresentationObservedKind.ButtonDown, h.Now, X: 70, Y: 80, HasPosition: true);
        h.Send(0x11, 29, 0); h.Send(0x43, 46, 0);
        check("pass-through keyboard uses the hook stream", b.Frame.RecentChords is [{ Keys.Count: 2, IsHeld: true }]);
        b.ObserveHookInput(new(token, [Click()], null, false));
        check("pass-through pointer hook data cannot duplicate raw mouse clicks", b.Frame.Clicks.Count == 0);
        h.Input.Queue([Click()], new(70, 80, h.Now)); h.Drain();
        check("pass-through mouse and cursor still use the passive source", b.Frame.Clicks.Count == 1 && b.Frame.CursorScreen == new PointD(70, 80));
        h.Now += 10; b.ConfigureEditingInput(true);
        h.Send(0x43, 46, 0x80, true); h.Send(0x11, 29, 0x80, true);
        check("entering editing retains the matching releases of an existing chord", b.Frame.PressedKeys.Count == 0 && b.Frame.RecentChords is [{ IsHeld: false }]);
        b.ObserveHookInput(new(token, [Click()], null, false));
        h.Input.Queue([Click()], new(71, 81, h.Now)); h.Drain();
        check("editing pointer clicks use the hook once while raw cursor updates continue", b.Frame.Clicks.Count == 2 && b.Frame.CursorScreen == new PointD(71, 81));
        b.SuppressKeyboard(true); h.Send(0x41, 30, 0); b.ObserveHookInput(new(token, [Click()], null, false));
        check("hook keyboard obeys privacy suppression without blocking pointer presentation", b.Frame.PressedKeys.Count == 0 && b.Frame.RecentChords.Count == 0 && b.Frame.Clicks.Count == 3);
        b.SuppressKeyboard(false); b.ConfigureKeyboardScope(PresentationKeyboardScope.FloatCanvasOnly);
        h.Send(0x41, 30, 0);
        check("external hook input obeys the selected keyboard scope", b.Frame.PressedKeys.Count == 0);
        h.Inside = true; h.Send(0x41, 30, 0); h.Send(0x41, 30, 0x80, true);
        check("in-scope hook input records again", b.Frame.RecentChords is [{ IsHeld: false }]);
        b.SetArmed(false); b.SetArmed(true);
        b.ObserveHookInput(new(token, [new(PresentationObservedKind.KeyDown, h.Now, VirtualKey: 0x5B, KeyLabel: "Win")], null, false));
        check("a retired subscription cannot replay delayed hook packets", b.InputEpoch != token && b.Frame.PressedKeys.Count == 0);
    }
}
