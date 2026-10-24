using System.IO;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;

/// <summary>Interactive Debug-only review, isolated from the user's profile and overlay.</summary>
internal static class AuditUxPreview
{
    internal static void Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        AppTheme.Apply("light");
        string root = ApplicationPaths.ProfileDirectory;
        var editor = new EditorController(new EditorSession());
        var tools = new ToolPresetLibrary(Path.Combine(root, "tools.json")); tools.Save("Personal pen", editor.Parameters);
        var colors = new ColorLibrary(Path.Combine(root, "colors.json")); colors.Save("Saved blue", 0xFF2383E2);
        var canvases = new CanvasPresetLibrary(Path.Combine(root, "canvas-presets.json")); canvases.Save("White board", new CanvasSettings { BackgroundColor = 0xFFFFFFFF });
        var body = new DockPanel { Margin = new(16) };
        var themes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 12) };
        foreach (string theme in new[] { "light", "dark", "high contrast" })
        {
            var button = new Button { Content = theme, Margin = new(0, 0, 8, 0) };
            button.Click += (_, _) => { AppTheme.Apply(theme == "dark" ? "dark" : "light"); if (theme == "high contrast") AppTheme.ApplyPalette(app.Resources, false, true); };
            themes.Children.Add(button);
        }
        DockPanel.SetDock(themes, Dock.Top); body.Children.Add(themes);
        var tabs = new TabControl(); body.Children.Add(tabs);
        var options = new ToolOptionsPanel(editor, tools, colors) { Width = 320 };
        var toolPanel = new StackPanel(); var widths = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (int width in new[] { 320, 480, 560 }) { var button = new Button { Content = width + " DIP", Margin = new(0, 0, 8, 0) }; button.Click += (_, _) => options.Width = width; widths.Children.Add(button); }
        toolPanel.Children.Add(widths); toolPanel.Children.Add(options);
        tabs.Items.Add(new TabItem { Header = "Tools / keyboard", Content = new ScrollViewer { Content = toolPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var picker = new ColorPickerPanel(ColorRole.Foreground, colors);
        tabs.Items.Add(new TabItem { Header = "Color / validation", Content = picker });
        var resources = new StackPanel(); resources.Children.Add(new ToolPresetGallery(editor, tools)); resources.Children.Add(new CanvasPresetGallery(canvases));
        tabs.Items.Add(new TabItem { Header = "Presets / selection / undo", Content = new ScrollViewer { Content = resources, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var actions = new StackPanel();
        void Open(string label, Action action) { var button = new Button { Content = label, Margin = new(0, 0, 0, 8) }; button.Click += (_, _) => action(); actions.Children.Add(button); }
        void Show(Window window, string title) { window.Owner = app.MainWindow; window.Title = "Audit UX · " + title; window.Topmost = false; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; window.ShowDialog(); }
        Open("Canvas settings · 320 DIP", () => Show(new CanvasSettingsWindow(canvases) { Width = 320, Height = 480 }, "Canvas settings 320 DIP"));
        Open("History · 384 DIP", () => Show(new WorkspaceHistoryWindow(new WorkspaceRecoveryHistory(new RecoveryStore(Path.Combine(root, "recovery"))),
            Enumerable.Range(1, 60).Select(index => new WorkspaceRecoveryEntry(Path.Combine(root, "preview-history", index.ToString()),
                DateTime.UtcNow.AddMinutes(-index), false, $"Audit history {index:00}.floatcanvas")).ToArray()) { Height = 384 }, "History 384 DIP"));
        Open("Settings", () =>
        {
            var window = new AppSettingsWindow();
            foreach (string section in new[] { "Settings.General", "Settings.CanvasHistory", "Settings.Shortcuts", "Settings.System" })
            {
                var page = window.AddSection(FloatCanvas.Localization.AppText.Get(section), "Tab suppression preview");
                AppSettingsWindow.AddSetting(page, "Example option", new CheckBox());
                AppSettingsWindow.AddSetting(page, "Example number", new TextBox { Text = "42" });
                AppSettingsWindow.AddSetting(page, "Example slider", new Slider { Minimum = 0, Maximum = 100, Value = 50 });
            }
            window.Error.Text = "⚠ Invalid example value / 输入格式错误"; Show(window, "Settings");
        });
        Open("Mixed locked selection", () =>
        {
            var state = new EditorSession(); var locked = new CanvasElement { Locked = true }; var unlocked = new CanvasElement(); state.Add(locked); state.Add(unlocked); state.SetSelection([unlocked.Id, locked.Id]);
            var window = new InspectorWindow(() => state); window.Refresh(); Show(window, "Inspector mixed locks");
        });
        Open("Empty stamp library", () => { var library = new StampLibrary(root); Task.Run(() => library.LoadAsync()).GetAwaiter().GetResult(); Show(new StampLibraryWindow(library, () => editor.Session, _ => { }), "Empty stamp library"); });
        Open("Missing font", () => { editor.ApplyParameters(new ToolParameters { Tool = EditorTool.Text, Style = new() { FontFamily = "Missing Font Example" } }); options.Refresh(); tabs.SelectedIndex = 0; });
        Open("Offline guide", () => AppHelp.ShowText("Audit UX · Offline guide", AppHelp.ReadGuide(false), owner: app.MainWindow)); Open("Feedback preview", AppHelp.ReportIssue);
        Open("Export diagnostic bundle", () => DiagnosticExportWindow.ShowFor(app.MainWindow, false));
        var message = new TextBlock { Text = "Saved C:\\Example\\a very long project filename.floatcanvas. This result remains readable in recent messages after switching tools." };
        var status = new ToolbarStatusDisplay(message) { Width = 280 }; status.Refresh(); actions.Children.Add(status);
        tabs.Items.Add(new TabItem { Header = "Dialogs / history / help", Content = actions });
        var welcomeActions = new StackPanel();
        foreach (bool hasHistory in new[] { false, true })
        {
            var button = new Button { Content = hasHistory ? "Welcome with recent history" : "Welcome without history", Margin = new(0, 0, 0, 8) };
            button.Click += (_, _) =>
            {
                var store = new RecoveryStore(Path.Combine(root, hasHistory ? "welcome-saved" : "welcome-empty"));
                var history = new WorkspaceRecoveryHistory(store);
                if (hasHistory)
                {
                    var workspace = new WorkspaceState(); workspace.LiveDocument.Elements.Add(new CanvasElement { Width = 20, Height = 20 });
                    store.SaveAsync(workspace, new AssetLibrary()).GetAwaiter().GetResult(); history.WriteCurrentInfo(workspace, null);
                }
                var window = new Window { Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize };
                window.Content = new WelcomeView(FloatCanvas.Windows.HotkeyBinding.DefaultToggle, FloatCanvas.Windows.HotkeyBinding.DefaultHide,
                    () => window.Close(), restoreHistory: () => window.Title = "Audit UX · Recovery requested explicitly", history: history);
                Show(window, hasHistory ? "Welcome with recent history" : "Welcome without history");
            };
            welcomeActions.Children.Add(button);
        }
        tabs.Items.Add(new TabItem { Header = "Welcome", Content = welcomeActions });
        var preview = new Window { Title = "FloatCanvas Audit UX Preview", Content = body, Width = 800, Height = 720, MinWidth = 400, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterScreen };
        app.Run(preview);
    }
}
