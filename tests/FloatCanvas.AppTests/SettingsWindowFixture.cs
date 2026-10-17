using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Exercises only the actual settings entry with hidden, bounded overlay HWNDs.</summary>
internal static class SettingsWindowFixture
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        string directory = Environment.GetEnvironmentVariable("FLOATCANVAS_SETTINGS_TEST_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-settings-open");
        Directory.CreateDirectory(directory);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string profile = Path.GetFullPath(Path.Combine(temporaryRoot, "FloatCanvasSettingsEntry-" + Guid.NewGuid().ToString("N")));
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        new UserSettings { Language = "zh-CN", Theme = "light" }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var errors = new List<string>();
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.ToString()); e.Handled = true; };
        OverlayHost? host = null;
        DesktopSession? session = null;
        var rows = new List<object>();
        var success = new List<bool>();
        try
        {
            var menuText = new TextBlock { Text = "Settings" };
            var menuItem = new MenuItem { Header = menuText };
            check("menu items keep WPF menu-mode focus", !DesktopSession.ShouldSynchronizePointerFocus(menuItem));
            check("a menu item's actual header text keeps WPF menu-mode focus", !DesktopSession.ShouldSynchronizePointerFocus(menuText));
            check("context menus keep WPF menu-mode focus", !DesktopSession.ShouldSynchronizePointerFocus(new ContextMenu()));
            check("ordinary menus keep WPF menu-mode focus", !DesktopSession.ShouldSynchronizePointerFocus(new Menu()));
            check("numeric and text fields retain pointer focus synchronization", DesktopSession.ShouldSynchronizePointerFocus(new TextBox()));
            check("combo-box items retain pointer focus synchronization", DesktopSession.ShouldSynchronizePointerFocus(new ComboBoxItem()));
            var ordinaryPanel = new StackPanel();
            var ordinaryText = new TextBlock { Text = "Options" }; ordinaryPanel.Children.Add(ordinaryText);
            check("ordinary panels retain pointer focus synchronization", DesktopSession.ShouldSynchronizePointerFocus(ordinaryPanel));
            check("ordinary panel content retains pointer focus synchronization", DesktopSession.ShouldSynchronizePointerFocus(ordinaryText));
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
            var monitor = primary with { Bounds = new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 30, 640, 160) };
            Func<IReadOnlyList<MonitorInfo>> provider = () => [monitor];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), PrivateInstance, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            session = new DesktopSession(app, host);
            ((System.Windows.Forms.NotifyIcon)typeof(DesktopSession).GetField("_tray", PrivateInstance)!.GetValue(session)!).Visible = false;
            var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", PrivateInstance)!;
            (isolation.GetValue(host) as IDisposable)?.Dispose(); isolation.SetValue(host, null);
            Observe("direct", () => typeof(DesktopSession).GetMethod("ShowHotkeySettings", PrivateInstance)!.Invoke(session, null));
            var menu = (ContextMenu)typeof(DesktopSession).GetField("_toolbarMenu", PrivateInstance)!.GetValue(session)!;
            var settings = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, L.Get("Menu.Settings")));
            Observe("toolbar-menu", () => settings.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, settings)));
            File.WriteAllText(Path.Combine(directory, "settings-entry-report.json"), JsonSerializer.Serialize(new { Rows = rows, Errors = errors, host.IsEditing, Scope = host.Diagnostics.Select(x => x.Bounds) }, new JsonSerializerOptions { WriteIndented = true }));
            check("actual direct settings entry shows a nonzero visible owned window", success[0]);
            check("actual toolbar settings action shows a nonzero visible owned window", success[1]);
            check("settings entry has no dispatcher exception", errors.Count == 0);
            check("settings fixture never enables canvas input", !host.IsEditing);

            void Observe(string path, Action invoke)
            {
                Exception? failure = null;
                try { invoke(); Pump(); }
                catch (Exception ex) { failure = ex.InnerException ?? ex; Console.WriteLine(path + " EXCEPTION " + failure); }
                var windows = app.Windows.Cast<Window>().Where(w => w.Title == L.Get("Settings.Title")).ToArray();
                bool shown = windows.Any(w => w.IsVisible && w.ActualWidth > 100 && w.ActualHeight > 100);
                success.Add(shown && failure is null);
                foreach (var window in windows)
                {
                    nint handle = new WindowInteropHelper(window).Handle;
                    GetWindowRect(handle, out var rect);
                    var row = new { Path = path, window.Title, window.IsVisible, window.IsLoaded, window.IsActive, window.ActualWidth, window.ActualHeight, window.Width, window.Height, window.MaxHeight, window.SizeToContent, Owner = window.Owner?.Title, Handle = handle.ToInt64(), Rect = new { rect.Left, rect.Top, rect.Right, rect.Bottom }, Exception = failure?.ToString() };
                    Console.WriteLine(JsonSerializer.Serialize(row)); rows.Add(row);
                    window.Close();
                }
                if (windows.Length == 0) rows.Add(new { Path = path, NoSettingsWindow = true, Exception = failure?.ToString() });
                Pump();
            }
        }
        finally
        {
            foreach (var window in app.Windows.Cast<Window>().ToArray())
                if (window.Title == L.Get("Settings.Title")) window.Close();
            session?.Dispose(); if (session is null) host?.Dispose();
            Pump(); app.Shutdown();
            // Delete only this invocation's generated profile, never its retained report directory.
            if (!string.Equals(Path.GetDirectoryName(profile), temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(profile).StartsWith("FloatCanvasSettingsEntry-", StringComparison.Ordinal))
                throw new InvalidOperationException("The settings fixture profile is outside its temporary scope.");
            try { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Console.WriteLine("Settings fixture profile cleanup: " + ex.Message); }
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
}
