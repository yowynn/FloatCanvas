using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace FloatCanvas.App;

/// <summary>Uses the launcher's click timing while retaining the tray's native right-click menu.</summary>
internal sealed class TrayLauncherGestures : IDisposable
{
    private readonly Forms.NotifyIcon tray;
    private readonly Action enterEditing, toggleVisibility;
    private readonly Func<long> now;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Input);
    private readonly LauncherGestureState gestures;
    private bool nativeDoubleClick;

    internal TrayLauncherGestures(Forms.NotifyIcon tray, Action enterEditing, Action toggleVisibility, Func<long>? now = null)
    {
        this.tray = tray; this.enterEditing = enterEditing; this.toggleVisibility = toggleVisibility;
        this.now = now ?? (() => Environment.TickCount64);
        var size = Forms.SystemInformation.DoubleClickSize;
        gestures = new(Forms.SystemInformation.DoubleClickTime, size.Width, size.Height, size.Width, size.Height);
        timer.Tick += (_, _) => AdvanceTime();
        tray.MouseDown += Down; tray.MouseUp += Up;
    }

    private void Down(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left) { Cancel(); return; }
        if (e.Clicks == 2) { Cancel(); nativeDoubleClick = true; return; }
        Execute(gestures.PointerDown(e.X, e.Y, now())); Arm();
    }
    private void Up(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left) { Cancel(); return; }
        if (nativeDoubleClick) { Cancel(); toggleVisibility(); return; }
        Execute(gestures.PointerUp(e.X, e.Y, now())); Arm();
    }
    internal void AdvanceTime() { timer.Stop(); Execute(gestures.AdvanceTime(now())); Arm(); }
    internal void Cancel() { timer.Stop(); gestures.Cancel(); nativeDoubleClick = false; }
    private void Execute(LauncherGestureAction action)
    {
        if (action == LauncherGestureAction.OpenMenu) enterEditing();
        else if (action == LauncherGestureAction.ToggleVisibility) toggleVisibility();
    }
    private void Arm()
    {
        timer.Stop();
        if (gestures.PendingClickDeadlineMs is not { } deadline) return;
        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, deadline - now())); timer.Start();
    }
    public void Dispose() { Cancel(); tray.MouseDown -= Down; tray.MouseUp -= Up; }
}
