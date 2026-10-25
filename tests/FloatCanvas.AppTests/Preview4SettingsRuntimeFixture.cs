using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;

/// <summary>Production mascot and mode transitions; controlled gesture state, no physical input injection.</summary>
internal static class Preview4SettingsRuntimeFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host, Window toolbar, Action<Action> activate)
    {
        var settings = Field<UserSettings>(session, "_settings");
        check("disabled mascot creates no pass-through window", Field<PassThroughLauncherWindow?>(session, "_passThroughLauncher") is null);
        var toolbarLauncher = Field<ToolbarLauncher>(session, "_toolbarLauncher");
        var toolbarAnchor = toolbarLauncher.PointToScreen(new Point(ToolbarLauncher.ArtworkSize, ToolbarLauncher.ArtworkSize));
        settings.ShowPassThroughLauncher = true;
        Invoke(session, "SetEditing", false, false); Pump();
        var mascot = Field<PassThroughLauncherWindow>(session, "_passThroughLauncher");
        check("pass-through keeps the mascot eye art and toolbar bottom-right anchor without canvas input", mascot.IsVisible && !toolbar.IsVisible && !host.IsEditing &&
            Near(mascot.PointToScreen(new Point(mascot.ActualWidth, mascot.ActualHeight)), toolbarAnchor) &&
            ReferenceEquals(((Image)mascot.Launcher.Content).Source, AppIcon.LauncherImage(host.IsVisible,false)));
        check("pass-through keeps the editing window's artwork and hit size", Math.Abs(mascot.ActualWidth - ToolbarLauncher.ArtworkSize) < 1 && Math.Abs(mascot.ActualHeight - ToolbarLauncher.ArtworkSize) < 1 &&
            mascot.Launcher.TransformToAncestor((Visual)mascot.Content).TransformBounds(new Rect(mascot.Launcher.RenderSize)).Size == new Size(94,94));
        nint handle = new WindowInteropHelper(mascot).Handle;
        check("mascot stays non-activating while accepting mouse clicks", !mascot.ShowActivated && !mascot.Focusable && !mascot.Launcher.Focusable &&
            ((long)GetWindowLongPtr(handle, -20) & 0x08000080L) == 0x08000080L && SendMessage(handle, 0x21, 0, 0) == 3);
        nint foreground = GetForegroundWindow();
        Invoke(session, "RefreshPassThroughLauncher"); Pump();
        check("refreshing a visible mascot never takes foreground", GetForegroundWindow() == foreground);
        var expected = mascot.PointToScreen(new Point());
        toolbar.Left += 20; toolbar.Top += 14; Invoke(session, "SaveToolbarPosition"); Invoke(session, "RefreshPassThroughLauncher"); Pump();
        check("the shared mascot follows later toolbar movement", !Near(mascot.PointToScreen(new Point()), expected) &&
            Near(mascot.PointToScreen(new Point(mascot.ActualWidth, mascot.ActualHeight)),
                toolbarLauncher.PointToScreen(new Point(ToolbarLauncher.ArtworkSize, ToolbarLauncher.ArtworkSize))));
        var gestures = Field<LauncherGestureState>(mascot.Launcher, "gestures");
        long now = Environment.TickCount64;
        gestures.PointerDown(10, 10, now);
        check("small pointer jitter retains click behavior", gestures.PointerMove(11, 11, now + 1) == LauncherGestureAction.None && !gestures.IsDragging);
        gestures.Cancel();
        bool visible = host.IsVisible;
        Click(now); gestures.PointerDown(10, 10, now + 30); Execute(gestures.PointerUp(10, 10, now + 40));
        check("double-click toggles the real canvas and eye state without entering drawing", host.IsVisible != visible && !host.IsEditing && mascot.IsVisible &&
            ReferenceEquals(((Image)mascot.Launcher.Content).Source, AppIcon.LauncherImage(host.IsVisible,false)) && !gestures.HasPendingClick);
        Execute(gestures.AdvanceTime(now + 10000));
        check("a completed double-click leaves no delayed entry", !host.IsEditing && !toolbar.IsVisible);
        now = Environment.TickCount64; Click(now); Invoke(mascot.Launcher, "ArmTimer");
        check("single-click waits for the system double-click interval", !host.IsEditing && gestures.HasPendingClick);
        activate(() => WaitFor(() => host.IsEditing, System.Windows.Forms.SystemInformation.DoubleClickTime + 1000));
        check("the real delayed timer enters drawing and hides the passive mascot", host.IsEditing && toolbar.IsVisible && !mascot.IsVisible && !gestures.HasPendingClick);
        Invoke(session, "SetEditing", false, false); Pump();
        activate(() => Invoke(mascot.Launcher, "OnPreviewMouseRightButtonUp", new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent }));
        check("right-click enters drawing immediately", host.IsEditing && toolbar.IsVisible && !mascot.IsVisible);
        Invoke(session, "SetEditing", false, false); Pump();
        var before = new Point(mascot.Left, mascot.Top);
        mascot.Left += 80; mascot.Top += 60; Invoke(mascot, "CompleteDrag", before); Pump();
        var shared = mascot.PointToScreen(new Point());
        check("a passive drag updates the shared session position",Math.Abs(settings.ToolbarX!.Value-shared.X)<2&&Math.Abs(settings.ToolbarY!.Value-shared.Y)<2);
        activate(() => Invoke(session,"SetEditing",true,false));Pump();
        var fitted=toolbarLauncher.PointToScreen(new Point());
        Invoke(session,"SetEditing",false,false);Pump();
        check("both modes retain the shared fitted position after a passive drag",mascot.IsVisible&&Near(fitted,mascot.PointToScreen(new Point())));
        var monitor=host.PrimaryMonitor;before=new Point(mascot.Left,mascot.Top);
        OverlayCoordinates.PositionToolWindow(handle,new(monitor.Bounds.Right-94*monitor.DpiX/96d-2,monitor.Bounds.Top+120));
        Invoke(mascot,"CompleteDrag",before);Pump();
        activate(()=>Invoke(session,"SetEditing",true,false));Pump();
        fitted=toolbarLauncher.PointToScreen(new Point());
        check("entering editing from the right edge brings the complete toolbar into view",fitted.X+toolbar.ActualWidth*VisualTreeHelper.GetDpi(toolbar).DpiScaleX<=monitor.Bounds.Right+2);
        Invoke(session,"SetEditing",false,false);Pump();
        check("returning to passive mode keeps the adapted shared position",Near(fitted,mascot.PointToScreen(new Point())));
        Click(Environment.TickCount64); Invoke(mascot.Launcher, "ArmTimer");
        settings.ShowPassThroughLauncher = false; Invoke(session, "RefreshPassThroughLauncher"); Pump();
        check("disabling mascot hides it and cancels pending entry", !mascot.IsVisible && !gestures.HasPendingClick && !host.IsEditing);
        using (var fresh = new FreshMascot()) check("a fresh mascot uses the shared artwork dimensions",fresh.Window.Width==ToolbarLauncher.ArtworkSize);
        settings.ShowPassThroughLauncher = true; Invoke(session, "RefreshPassThroughLauncher"); Pump();
        string path = Path.Combine(Path.GetTempPath(), "FloatCanvas-pass-through-mascot-" + Guid.NewGuid().ToString("N") + ".png");
        Render(mascot, path); Console.WriteLine("SETTINGS_RENDER mascot " + path);
        settings.ShowPassThroughLauncher = false; Invoke(session, "RefreshPassThroughLauncher");

        void Click(long time) { Execute(gestures.PointerDown(10, 10, time)); Execute(gestures.PointerUp(10, 10, time + 5)); }
        void Execute(LauncherGestureAction action) => Invoke(mascot.Launcher, "Execute", action);
    }
    private sealed class FreshMascot : IDisposable
    {
        internal PassThroughLauncherWindow Window { get; } = new();
        public void Dispose() => Window.Close();
    }
    internal static void Render(Window window, string path)
    {
        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static bool Near(Point a, Point b) => (a - b).Length < 2;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    private static void WaitFor(Func<bool> condition, int milliseconds)
    {
        var frame = new DispatcherFrame(); var end = Environment.TickCount64 + milliseconds;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || Environment.TickCount64 > end) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
