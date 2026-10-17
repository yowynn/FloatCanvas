using System.Windows;
using System.Windows.Interop;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private static void ShowAppWindow(Window window, bool activate = false)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        if (activate) window.Activate();
    }

    private void ConfigureAppWindow(Window window, bool dialog = false)
    {
        window.Icon = AppIcon.WindowImage;
        window.Topmost = true;
        window.ShowInTaskbar = false;
        if (!ReferenceEquals(window, _toolbar))
        {
            // Welcome and tray-opened dialogs may precede the first editing session.
            // WPF requires the owner to have a native source even while it remains hidden.
            new WindowInteropHelper(_toolbar).EnsureHandle();
            window.Owner ??= _toolbar;
        }
        if (ReferenceEquals(window, _welcomeWindow)) _windowLayout.BindRoot(window);
        else _windowLayout.Track(window);
        if (dialog) AppWindowChrome.ApplyDialog(window); else AppWindowChrome.Apply(window);
        void ScheduleRaise()
        {
            // Re-entering SetWindowPos during the first IsVisibleChanged notification
            // can replace WPF's initial width with its provisional one-pixel HWND size.
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (window.IsVisible && !_disposed) RaiseVisibleToolWindows();
            });
        }
        window.SourceInitialized += (_, _) => ScheduleRaise();
        window.IsVisibleChanged += (_, _) => ScheduleRaise();
    }
}
