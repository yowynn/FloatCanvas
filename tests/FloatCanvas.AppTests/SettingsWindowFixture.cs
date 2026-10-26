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
            if (Environment.GetEnvironmentVariable("FLOATCANVAS_SETTINGS_BACKEND") == "enhanced") host.EnableSystemInput();
            session = new DesktopSession(app, host);
            ((System.Windows.Forms.NotifyIcon)typeof(DesktopSession).GetField("_tray", PrivateInstance)!.GetValue(session)!).Visible = false;
            var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", PrivateInstance)!;
            (isolation.GetValue(host) as IDisposable)?.Dispose(); isolation.SetValue(host, null);
            CheckShellRestartSettings();
            Observe("direct", () => typeof(DesktopSession).GetMethod("ShowHotkeySettings", PrivateInstance)!.Invoke(session, null));
            var menu = (ContextMenu)typeof(DesktopSession).GetField("_toolbarMenu", PrivateInstance)!.GetValue(session)!;
            var settings = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, L.Get("Menu.Settings")));
            Observe("toolbar-menu", () => settings.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, settings)));
            File.WriteAllText(Path.Combine(directory, "settings-entry-report.json"), JsonSerializer.Serialize(new { Rows = rows, Errors = errors, host.IsEditing, Scope = host.Diagnostics.Select(x => x.Bounds) }, new JsonSerializerOptions { WriteIndented = true }));
            check("actual direct settings entry shows a nonzero visible owned window", success[0]);
            check("actual toolbar settings action shows a nonzero visible owned window", success[1]);
            check("settings entry has no dispatcher exception", errors.Count == 0);
            check("settings fixture never enables canvas input", !host.IsEditing);
            var prompt = AppDialog.Create((Window)typeof(DesktopSession).GetField("_toolbar", PrivateInstance)!.GetValue(session)!, "fixture", L.Get("App.Name")).Window;
            try { check("ordinary confirmation dialogs do not retain the hidden editing toolbar as owner", prompt.Owner is null && !prompt.Topmost && OverlayWindowScope.IsRegularWindow(prompt)); }
            finally { prompt.Close(); }
            CheckHistoryTitle(regular: true);

            EnterEditing();
            check("isolated modal fixture enters editing", host.IsEditing);
            bool modal = false, blockedToggle = false, closedAfterFocusLoss = false;
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = app.Windows.OfType<AppSettingsWindow>().Single();
                try
                {
                    modal = ComponentDispatcher.IsThreadModal && dialog.IsVisible && dialog.Owner is not null && !OverlayWindowScope.IsRegularWindow(dialog);
                    check("editing settings use the unbranded window title", dialog.Title == (L.CurrentLanguage == "en" ? "Settings" : "设置"));
                    Preview4SettingsRuntimeFixture.Render(dialog, Path.Combine(directory, "settings-editing-title.png"));
                    Invoke("ToggleEditing"); blockedToggle = host.IsEditing;
                }
                finally { dialog.Close(); }
            }));
            Invoke("ShowHotkeySettings"); Pump();
            check("editing settings use a real modal loop", modal);
            check("application toggle cannot exit editing while settings are modal", blockedToggle && host.IsEditing);
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                typeof(OverlayHost).GetMethod("EndEditingAfterFocusLoss", PrivateInstance)!.Invoke(host, null);
                closedAfterFocusLoss = !app.Windows.OfType<AppSettingsWindow>().Any();
            }));
            Invoke("ShowHotkeySettings"); Pump();
            check("forced editing exit closes its modal settings window", closedAfterFocusLoss && !host.IsEditing);
            EnterEditing();
            CheckHistoryTitle(regular: false);
            EnterEditing();
            Invoke("ShowUpdates"); Pump();
            var updates = app.Windows.OfType<AppUpdateWindow>().Single();
            nint updateHandle = new WindowInteropHelper(updates).Handle;
            check("opening updates exits editing and uses an ordinary titled taskbar window", !host.IsEditing && !updates.Topmost && updates.ShowInTaskbar && updates.Title == (L.CurrentLanguage == "en" ? "FloatCanvas · Check for updates" : "浮绘·检查更新"));
            check("actual update HWND has no overlay owner, topmost or no-activate policy", OverlayWindowScope.IsRegularWindow(updates) && !updates.AllowsTransparency && GetWindow(updateHandle, 4) == 0 && (GetWindowLongPtr(updateHandle, -20).ToInt64() & 0x08000008L) == 0);
            Preview4SettingsRuntimeFixture.Render(updates, Path.Combine(directory, "updates-ordinary-window.png"));
            EnterEditing();
            check("entering editing closes and releases the ordinary update window", host.IsEditing && !updates.IsVisible && !app.Windows.OfType<AppUpdateWindow>().Any() && typeof(DesktopSession).GetField("_updatesWindow", PrivateInstance)!.GetValue(session) is null);
            Invoke("OpenWelcome", true); Pump();
            check("opening welcome first exits editing and uses ordinary window layering", !host.IsEditing && app.Windows.Cast<Window>().Any(window => window.Title == WindowTitles.Format(L.Get("Welcome.Title"), true) && !window.Topmost && window.Owner is null));
            EnterEditing();
            check("entering editing closes ordinary welcome windows", host.IsEditing && !app.Windows.Cast<Window>().Any(window => window.Title == WindowTitles.Format(L.Get("Welcome.Title"), true)));
            Invoke("SetEditing", false, false);

            object? Invoke(string name, params object[] arguments) => typeof(DesktopSession).GetMethod(name, PrivateInstance)!.Invoke(session, arguments);
            void CheckHistoryTitle(bool regular)
            {
                string? title = null; bool correctMode = false, ordinaryPolicy = false;
                app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    var dialog = app.Windows.OfType<WorkspaceHistoryWindow>().Single();
                    try
                    {
                        title = dialog.Title;
                        correctMode = host.IsEditing != regular && ComponentDispatcher.IsThreadModal;
                        ordinaryPolicy = !regular || (!dialog.Topmost && !dialog.AllowsTransparency && OverlayWindowScope.IsRegularWindow(dialog));
                        Preview4SettingsRuntimeFixture.Render(dialog, Path.Combine(directory, regular ? "history-ordinary-title.png" : "history-editing-title.png"));
                    }
                    finally { SessionWindowCoordinator.Close(dialog); }
                }));
                Invoke("ChooseRecoveryWorkspace"); Pump();
                string expected = L.CurrentLanguage == "en" ? (regular ? "FloatCanvas · Workspace history" : "Workspace history") : (regular ? "浮绘·历史画布" : "历史画布");
                check((regular ? "ordinary" : "editing") + " history uses its mode-specific title and window policy", title == expected && correctMode && ordinaryPolicy);
            }
            void CheckShellRestartSettings()
            {
                string daily = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatCanvas");
                var permission = typeof(DesktopSession).GetField("_shellIntegrationAllowed", PrivateInstance)!;
                foreach (var launch in new (string Name, string[] Arguments, bool Enabled)[]
                {
                    ("ordinary launch", [], true),
                    ("settings restart", ["--profile", daily, "--recover", "--primary-screen"], true),
                    ("update restart", ["--profile", daily, "--recover", "--all-screens"], true),
                    ("isolated restart", ["--profile", profile, "--recover"], false),
                    ("smoke launch", ["--smoke", "--profile", daily], false)
                })
                {
                    permission.SetValue(session, ProjectLaunchRequest.AllowsRegistration(launch.Arguments));
                    Invoke("ShowHotkeySettings"); Pump();
                    var window = app.Windows.OfType<AppSettingsWindow>().Single();
                    try
                    {
                        var controls = Descendants(window).ToArray();
                        check("ordinary settings use the product-prefixed title without inheriting overlay input", window.Title == (L.CurrentLanguage == "en" ? "FloatCanvas · Settings" : "浮绘·设置") && !window.AllowsTransparency && new WindowInteropHelper(window).Owner == 0);
                        var startup = controls.OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == L.Get("Shell.StartWithWindows"));
                        var associate = controls.OfType<Button>().Single(button => Equals(button.Content, L.Get("Shell.AssociateProjectFiles")));
                        check(launch.Name + " shows the expected enabled startup and file association controls",
                            startup.IsEnabled == launch.Enabled && associate.IsEnabled == launch.Enabled);
                        check(launch.Name + " uses an explicit association action distinct from the group title",
                            !Equals(associate.Content, L.Get("Shell.RegisterProjectType")) && associate.Content.ToString()!.Contains(".floatcanvas"));
                        if (launch.Name == "settings restart")
                        {
                            controls.OfType<ListBox>().Single().SelectedIndex = 3; Pump();
                            associate.BringIntoView(); Pump();
                            Preview4SettingsRuntimeFixture.Render(window, Path.Combine(directory, "settings-restart-system.png"));
                        }
                    }
                    finally { window.Close(); Pump(); }
                }
                permission.SetValue(session, false);
                check("restart settings checks preserve the isolated profile", ApplicationPaths.ProfileDirectory == profile);
            }
            void EnterEditing()
            {
                var toolbar = (Window)typeof(DesktopSession).GetField("_toolbar", PrivateInstance)!.GetValue(session)!;
                toolbar.Show(); Pump();
                typeof(NativeInputRoutingFixture).GetMethod("ActivateFixtureWindow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                    [new WindowInteropHelper(toolbar).Handle, new Action(() => Invoke("SetEditing", true, false))]);
                Pump();
                if (host.UsesSystemInput && host.IsEditing && !host.HasEditingInput)
                {
                    var frame = new DispatcherFrame(); long deadline = Environment.TickCount64 + 3000;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                    timer.Tick += (_, _) => { if (host.HasEditingInput || !host.IsEditing || Environment.TickCount64 >= deadline) frame.Continue = false; };
                    timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
                    check("enhanced settings fixture acquires its input lease", host.HasEditingInput);
                }
            }

            void Observe(string path, Action invoke)
            {
                Exception? failure = null;
                try { invoke(); Pump(); }
                catch (Exception ex) { failure = ex.InnerException ?? ex; Console.WriteLine(path + " EXCEPTION " + failure); }
                var windows = app.Windows.Cast<Window>().Where(w => w is AppSettingsWindow).ToArray();
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
                        check("pass-through settings are ordinary independent windows", window.Owner is null && !window.Topmost && window.ShowInTaskbar && OverlayWindowScope.IsRegularWindow(window));
                        var recorders = controls.OfType<TextBox>().Where(box => box.IsReadOnly).ToArray();
                        var historyNumber = controls.OfType<NumericValueField>().Single();
                        check("history slider spans zero to two hundred with default sixty", historyNumber.Slider.Minimum == 0 && historyNumber.Slider.Maximum == 200 && historyNumber.Value == 60);
                        historyNumber.Input.Text = "1000";
                        check("history input accepts values above the rail up to one thousand", historyNumber.TryGetValue(out double manualLimit) && manualLimit == 1000);
                        historyNumber.Input.Text = "60";
                        check("both shortcut recorders display their current bindings", recorders.Select(box => box.Text).SequenceEqual(new[] { HotkeyDisplay.Format(UserSettings.Load().Toggle), HotkeyDisplay.Format(UserSettings.Load().Hide) }));
                        var language = controls.OfType<ComboBox>().Single(box => AutomationProperties.GetName(box) == L.Get("Settings.Language"));
                        int originalLanguage = language.SelectedIndex; language.SelectedIndex = originalLanguage == 1 ? 2 : 1;
                        check("restart-required settings change the save action", Equals(layout.SaveButton.Content, L.Get("Settings.SaveRestart")));
                        language.SelectedIndex = originalLanguage;
                        check("reverting restart-required settings restores the save action", Equals(layout.SaveButton.Content, L.Get("Settings.Save")));
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
                        check("preview update preference appears only in the update window", !controls.OfType<CheckBox>().Any(box => AutomationProperties.GetName(box) == L.Get("Updates.IncludePreviews")));
                        var save = settingsWindow.SaveButton;
                        limit.Text = "1001"; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Pump();
                        check("invalid history capacity reveals its category and leaves settings open", window.IsVisible && limit.IsVisible && UserSettings.Load().HistoryVersionLimit == 60);
                        limit.Text = "0"; mascot.IsChecked = true; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        check("saving the actual history setting persists zero without resetting to sixty", UserSettings.Load().HistoryVersionLimit == 0);
                        check("saving the mascot option takes effect in pass-through immediately", UserSettings.Load().ShowPassThroughLauncher && app.Windows.OfType<PassThroughLauncherWindow>().Single().IsVisible && !host.IsEditing);
                        check("saving settings preserves the preview update choice", !UserSettings.Load().IncludePrereleaseUpdates);
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
                if (window is AppSettingsWindow) window.Close();
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
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
}
