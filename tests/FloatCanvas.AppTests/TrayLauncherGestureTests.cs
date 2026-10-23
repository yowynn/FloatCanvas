using System.Reflection;
using FloatCanvas.App;
using Forms = System.Windows.Forms;

internal static class TrayLauncherGestureTests
{
    internal static void Run(Action<string, bool> check)
    {
        long now = 1000; int entered = 0, toggled = 0, right = 0;
        using var tray = new Forms.NotifyIcon { Visible = false };
        using var menu = new Forms.ContextMenuStrip(); tray.ContextMenuStrip = menu;
        tray.MouseUp += (_, e) => { if (e.Button == Forms.MouseButtons.Right) right++; };
        using var clicks = new TrayLauncherGestures(tray, () => entered++, () => toggled++, () => now);
        void Mouse(string kind, Forms.MouseButtons button, int count = 1) => typeof(Forms.NotifyIcon)
            .GetMethod("OnMouse" + kind, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(tray, [new Forms.MouseEventArgs(button, count, 0, 0, 0)]);
        void Click() { Mouse("Down", Forms.MouseButtons.Left); now += 10; Mouse("Up", Forms.MouseButtons.Left); }
        Click();
        check("tray single click waits for the system double-click interval", entered == 0 && toggled == 0);
        now = 1000 + Forms.SystemInformation.DoubleClickTime; clicks.AdvanceTime(); clicks.AdvanceTime();
        check("tray single click requests entering editing exactly once", entered == 1 && toggled == 0);
        now += 1000; Click(); now += 50; Click();
        check("tray double click toggles visibility without entering editing", entered == 1 && toggled == 1);
        now += 1000; clicks.AdvanceTime();
        check("tray double click has no delayed single-click action", entered == 1 && toggled == 1);
        Click(); Mouse("Down", Forms.MouseButtons.Right); Mouse("Up", Forms.MouseButtons.Right);
        now += 1000; clicks.AdvanceTime();
        check("tray right click retains its native menu and cancels delayed editing", right == 1 && ReferenceEquals(tray.ContextMenuStrip, menu) && entered == 1 && toggled == 1);
        Click(); clicks.Cancel(); now += 1000; clicks.AdvanceTime();
        check("opening the tray menu cancels a pending left click", entered == 1 && toggled == 1);
        Click(); now += Forms.SystemInformation.DoubleClickTime + 50;
        Mouse("Down", Forms.MouseButtons.Left, 2); Mouse("Up", Forms.MouseButtons.Left);
        check("native double-click recognition survives delayed UI message delivery", entered == 1 && toggled == 2);
        Click(); clicks.Dispose(); now += 1000; clicks.AdvanceTime(); Click();
        now += 1000; clicks.AdvanceTime();
        check("disposing tray gestures removes handlers and pending work", entered == 1 && toggled == 2 && !tray.Visible);
    }
}
