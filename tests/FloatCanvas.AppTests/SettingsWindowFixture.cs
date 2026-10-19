using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
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
        new UserSettings { Language = Environment.GetEnvironmentVariable("FLOATCANVAS_SETTINGS_TEST_LANGUAGE") ?? "zh-CN", Theme = "light", ShowFloatingLogo=false, Toggle = new(7, 133), Hide = new(7, 132) }.Save();
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
                    if (path == "direct" && window is AppSettingsWindow layout)
                    {
                        var controls = Descendants(window).ToArray();
                        var navigation = controls.OfType<ListBox>().Single();
                        check("settings group all existing options into four categories", navigation.Items.Count == 4 && controls.OfType<ScrollViewer>().Count() == 4);
                        check("category labels fit one line without a horizontal scrollbar", ScrollViewer.GetHorizontalScrollBarVisibility(navigation) == ScrollBarVisibility.Disabled &&
                            navigation.Items.Cast<ListBoxItem>().Select(item=>(TextBlock)item.Content).All(label=>label.ActualWidth<=navigation.ActualWidth&&label.ActualHeight<=25));
                        double footer = layout.SaveButton.PointToScreen(new Point()).Y;
                        for (int i = 0; i < navigation.Items.Count; i++)
                        {
                            navigation.SelectedIndex = i; Pump();
                            var scroll = controls.OfType<ScrollViewer>().Single(view => view.IsVisible);
                            scroll.ScrollToEnd(); Pump();
                            check($"settings category {i} keeps Save visible outside its scrolling content", layout.SaveButton.IsVisible && Math.Abs(layout.SaveButton.PointToScreen(new Point()).Y - footer) < 1);
                            scroll.ScrollToTop(); Pump();
                            string renderPath = Path.Combine(directory, "settings-category-" + i + ".png");
                            Preview4SettingsRuntimeFixture.Render(window, renderPath);
                            Console.WriteLine("SETTINGS_RENDER category" + i + " " + renderPath);
                        }
                        navigation.SelectedIndex = 0; AppTheme.Apply("dark"); Pump();
                        Preview4SettingsRuntimeFixture.Render(window, Path.Combine(directory, "settings-dark.png"));
                        AppTheme.Apply("light"); Pump();
                        controls.OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == L.Get("History.Limit")).Text = "9";
                        controls.OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == L.Get("Settings.PassThroughLauncher")).IsChecked = true;
                        controls.OfType<Button>().Single(button => Equals(button.Content, L.Get("Recovery.Cancel"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                        check("Cancel discards draft settings and keeps the mascot disabled", !window.IsVisible && UserSettings.Load().HistoryVersionLimit == 60 && !UserSettings.Load().ShowPassThroughLauncher);
                    }
                    if (path == "toolbar-menu" && window is AppSettingsWindow settingsWindow)
                    {
                        var controls = Descendants(window).ToArray();
                        var limit = controls.OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == L.Get("History.Limit"));
                        check("history settings show the default sixty versions", limit.Text == "60");
                        var mascot = controls.OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == L.Get("Settings.PassThroughLauncher"));
                        check("settings retain an explicit floating logo off preference", mascot.IsChecked == false);
                        var previews = controls.OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == L.Get("Updates.IncludePreviews"));
                        check("preview updates default off in the actual settings", previews.IsChecked == false);
                        var save = settingsWindow.SaveButton;
                        limit.Text = "0"; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Pump();
                        check("invalid history capacity reveals its category and leaves settings open", window.IsVisible && limit.IsVisible && UserSettings.Load().HistoryVersionLimit == 60);
                        limit.Text = "12"; mascot.IsChecked = true; previews.IsChecked = true; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        check("saving the actual history setting persists the chosen capacity", UserSettings.Load().HistoryVersionLimit == 12);
                        check("saving the mascot option takes effect in pass-through immediately", UserSettings.Load().ShowPassThroughLauncher && app.Windows.OfType<PassThroughLauncherWindow>().Single().IsVisible && !host.IsEditing);
                        check("saving settings persists the preview update choice", UserSettings.Load().IncludePrereleaseUpdates);
                    }
                    if (window.IsVisible) window.Close();
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
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
}
