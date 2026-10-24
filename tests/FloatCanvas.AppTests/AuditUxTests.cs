using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Reflection;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class AuditUxTests
{
    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var button = new Button { Style = (Style)app.FindResource(typeof(Button)) };
        var window = new Window { Content = button };
        int toggles = 0;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, Key.Tab)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        button.RaiseEvent(args);
        check("audit Tab does not traverse controls without an editor command route", args.Handled && toggles == 0);
        check("audit controls retain the application's disabled Tab traversal", KeyboardNavigation.GetTabNavigation(button) == KeyboardNavigationMode.None);
        var slider = new Slider { Style = (Style)app.FindResource(typeof(Slider)) };
        check("audit buttons and sliders add no keyboard focus frames", button.FocusVisualStyle is null && slider.FocusVisualStyle is null);
        AppInputPolicy.AttachToolbarToggle(window, () => toggles++);
        var toolbarTab = new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, Key.Tab) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        button.RaiseEvent(toolbarTab);
        check("audit ordinary editor controls retain the Tab collapse command", toolbarTab.Handled && toggles == 1);
        window.Close();
        foreach (bool dark in new[] { false, true })
        {
            var palette = new ResourceDictionary(); AppTheme.ApplyPalette(palette, dark, false);
            check("audit error text contrast " + dark, Contrast(((SolidColorBrush)palette["CanvasSurface"]).Color, ((SolidColorBrush)palette["CanvasErrorText"]).Color) >= 4.5);
        }
        var contrast = new ResourceDictionary(); AppTheme.ApplyPalette(contrast, false, true);
        check("audit high contrast uses Windows surfaces and text", Equals(contrast["CanvasSurface"], SystemColors.WindowBrush) && Equals(contrast["CanvasText"], SystemColors.WindowTextBrush));
        var tile = new SelectionTileButton { Content = new TextBlock { Text = "recipe" } };
        tile.SetSelected(true);
        var peer = UIElementAutomationPeer.CreatePeerForElement(tile)!;
        check("audit preset exposes selected state to automation", peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider { IsSelected: true });
        check("audit selected preset has a non-color checkmark", Visuals(tile.Content as DependencyObject).OfType<TextBlock>().Any(value => value.Text == "✓"));
        tile.SetSelected(false);
        check("audit selection can be removed without losing tile artwork", tile.Content is TextBlock { Text: "recipe" });
        var name = new TextBox { Text = "   " }; var save = new Button(); var form = new StackPanel();
        NamePrompt.AttachValidation(name, save, form);
        check("audit blank names have visible validation", !save.IsEnabled && form.Children.OfType<TextBlock>().Single().Visibility == Visibility.Visible);
        name.Text = "valid";
        check("audit valid names can be saved", save.IsEnabled);
        var font = ToolOptionPreviews.FontPicker(); ToolOptionPreviews.SelectFont(font, "FloatCanvas Missing Font 12345");
        check("audit missing font retains original source name", ((ComboBoxItem)font.SelectedItem).Tag is "FloatCanvas Missing Font 12345");
        check("audit missing font announces replacement", System.Windows.Automation.AutomationProperties.GetHelpText(font).Contains("FloatCanvas Missing Font 12345"));
        var text = new TextBlock(); var display = new ToolbarStatusDisplay(text);
        for (int i = 0; i < 25; i++) { text.Text = "message " + i; display.Refresh(); }
        check("audit status retains twenty complete copyable messages", display.RecentMessages.Count == 20 && display.RecentMessages[0] == "message 24" && display.RecentMessages[^1] == "message 5");
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-audit-ux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var colorPicker = new ColorPickerPanel(ColorRole.Foreground, new ColorLibrary(Path.Combine(folder, "validation-colors.json")));
        var hex = Field<TextBox>(colorPicker, "hex"); uint originalColor = colorPicker.Value; int commits = 0;
        colorPicker.ColorChosen += _ => commits++;
        hex.Text = "invalid"; hex.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, hex, save) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        check("audit invalid hex loses focus with visible explanation and preserves color", Field<TextBlock>(colorPicker, "error").Text.StartsWith("⚠") && !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetHelpText(hex)) && colorPicker.Value == originalColor && commits == 0);
        hex.Text = "#112233";
        check("audit corrected hex clears validation without forced commit", Field<TextBlock>(colorPicker, "error").Text.Length == 0 && commits == 0);
        foreach (string language in new[] { "zh-CN", "en" })
        {
            FloatCanvas.Localization.AppText.Initialize(language);
            string guide = AppHelp.ReadGuide(false), shortcuts = AppHelp.ReadGuide(true);
            check("audit offline guide and shortcuts use bundled " + language + " content", guide.Contains("--unregister-integration") && shortcuts.Contains("Ctrl+Z") && !shortcuts.Contains("--unregister-integration"));
        }
        FloatCanvas.Localization.AppText.Initialize("zh-CN");
        ResourceUndo(folder, check);
        CommandState(check);
        Layout(folder, check);
        StampBudget(folder, check);
    }
    private static void CommandState(Action<string, bool> check)
    {
        var active = new EditorSession(); var editor = new EditorController(active);
        var desktop = (DesktopSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        typeof(DesktopSession).GetField("_editor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(desktop, editor);
        bool Can(string key) => (bool)typeof(DesktopSession).GetMethod("CanExecuteUiCommand", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(desktop, [key])!;
        string Hint(string key) => (string)typeof(DesktopSession).GetMethod("UiCommandDisabledHint", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(desktop, [key])!;
        check("audit empty command state disables undo redo object actions with reasons", !Can("undo") && !Can("redo") && !Can("delete") && !Can("group") && Hint("undo").Length > 0 && Hint("group").Length > 0 && Can("copy"));
        var one = new CanvasElement(); var two = new CanvasElement(); active.Add(one); active.Add(two); active.SetSelection([one.Id, two.Id]);
        check("audit editable selection enables grouping and undo", Can("group") && Can("delete") && Can("undo") && !Can("redo"));
        active.GroupSelection(); check("audit complete selected group enables ungroup", Can("ungroup"));
        active.Undo(); check("audit redo state follows editor history", Can("redo"));
        active.Document.Elements.ForEach(element => element.Locked = true); active.SetSelection(active.Document.Elements.Select(element => element.Id));
        check("audit locked selection disables destructive object actions", !Can("delete") && !Can("duplicate") && !Can("group"));
        var inspector = new InspectorWindow(() => active); inspector.Refresh();
        check("audit locked inspector explains its unavailable fields", Field<TextBlock>(inspector, "status").Text.Contains("2") && Field<StackPanel>(inspector, "propertyPanel").Visibility == Visibility.Collapsed);
        active.Document.Elements[0].Locked = false; inspector.Refresh();
        check("audit mixed lock inspector explains the locked member", Field<TextBlock>(inspector, "status").Text == FloatCanvas.Localization.AppText.Format("Audit.LockedSelection", 1));
        inspector.Close();
        editor.ReplaceSession(new EditorSession(), new PointD()); check("audit switching to a blank document clears command eligibility", !Can("undo") && !Can("redo") && !Can("group"));
    }
    private static void ResourceUndo(string folder, Action<string, bool> check)
    {
        var tools = new ToolPresetLibrary(Path.Combine(folder, "tools.json"));
        var first = tools.Save("first", new ToolParameters()); var second = tools.Save("second", new ToolParameters());
        tools.Delete(first.Id); tools.UndoResourceChange();
        check("audit tool deletion restores ID name order and parameters", tools.User[0].Id == first.Id && tools.User[0].Name == first.Name && tools.User[1].Id == second.Id);
        var changed = new ToolParameters { DefaultWidth = 123 }; tools.Update(first.Id, changed); tools.UndoResourceChange();
        check("audit tool overwrite restores original parameters", tools.User[0].Parameters.DefaultWidth == first.Parameters.DefaultWidth);
        var canvas = new CanvasPresetLibrary(Path.Combine(folder, "canvas.json"));
        var preset = canvas.Save("canvas", new CanvasSettings { BackgroundColor = 0xFF112233 });
        canvas.Update(preset.Id, new CanvasSettings()); canvas.UndoResourceChange();
        check("audit canvas overwrite is recoverable", canvas.User[0].Parameters.BackgroundColor == 0xFF112233);
        canvas.Delete(preset.Id); canvas.UndoResourceChange();
        check("audit canvas deletion restores identity", canvas.User[0].Id == preset.Id);
        var colors = new ColorLibrary(Path.Combine(folder, "colors.json"));
        var color = colors.Save("color", 0xFF123456); colors.Update(color.Id, 0); colors.UndoResourceChange();
        check("audit color overwrite restores exact ARGB", colors.Swatches[0] == color);
        colors.Delete(color.Id); colors.Save("new", 42); colors.UndoResourceChange(); colors.UndoResourceChange();
        check("audit intervening color edits preserve undo order", colors.Swatches.Single() == color);
        colors.Delete(color.Id); colors.Remember(0xFFEEDDCC); colors.UndoResourceChange();
        check("audit resource undo retains unrelated recent-color history", colors.Swatches.Single() == color && colors.History[0] == 0xFFEEDDCC);
    }
    private static void Layout(string folder, Action<string, bool> check)
    {
        var editor = new EditorController(new EditorSession());
        var options = new ToolOptionsPanel(editor, new ToolPresetLibrary(Path.Combine(folder, "layout-tools.json")), new ColorLibrary(Path.Combine(folder, "layout-colors.json")));
        foreach (double width in new[] { 320d, 480, 560 })
        {
            options.Width = width; options.Measure(new Size(width, 600)); options.Arrange(new Rect(0, 0, width, 600)); options.UpdateLayout();
            var fields = Field<Dictionary<string, TextBox>>(options, "numbers").Values.Where(value => value.ActualWidth > 0).ToArray();
            check("audit tool fields fit width " + width, fields.Length > 0 && fields.All(value => value.ActualWidth >= 80));
        }
        foreach (string language in new[] { "zh-CN", "en" })
        foreach (int count in new[] { 0, 1, 60 })
        {
            FloatCanvas.Localization.AppText.Initialize(language);
            var entries = Enumerable.Range(0, count).Select(index => new WorkspaceRecoveryEntry(Path.Combine(folder, "history", index.ToString()), DateTime.UtcNow.AddDays(-index), false)).ToArray();
            var history = new WorkspaceHistoryWindow(new WorkspaceRecoveryHistory(new RecoveryStore(Path.Combine(folder, "history"))), entries);
            var content = (FrameworkElement)history.Content; history.Content = null;
            var root = new Border { Child = content, Width = 620 };
            foreach (double height in new[] { 384d, 480 })
            {
                root.Height = height - 40; root.Measure(new Size(620, root.Height)); root.Arrange(new Rect(0, 0, 620, root.Height)); root.UpdateLayout();
                var restore = Visuals(root).OfType<Button>().First(value => Equals(value.Content, FloatCanvas.Localization.AppText.Get("Recovery.Restore")));
                check($"audit history {language} {count} footer fits height {height}", restore.TranslatePoint(new Point(), root).Y + restore.ActualHeight <= height - 40 && restore.IsEnabled == (count > 0));
                int realized = Visuals(Field<ListBox>(history, "list")).OfType<ListBoxItem>().Count();
                check($"audit history {language} {count} bounds its preview rows at {height}", count == 0 ? realized == 0 : realized is > 0 and <= 10);
            }
            root.Child = null; history.Content = content; history.Close();
        }
        FloatCanvas.Localization.AppText.Initialize("zh-CN");
    }
    private static void StampBudget(string folder, Action<string, bool> check)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try
        {
            var results = new List<object>();
            foreach (int count in new[] { 100, 1000 })
            {
                var library = new StampLibrary(Path.Combine(folder, "stamps-" + count)); Directory.CreateDirectory(library.DirectoryPath);
                var items = Enumerable.Range(0, count).Select(i => new StampEntry(Guid.NewGuid(), "stamp " + i, StampSaveMode.EditableGroup, 30, 30, DateTime.UtcNow, Guid.NewGuid())).ToArray();
                File.WriteAllText(Path.Combine(library.DirectoryPath, "library.json"), JsonSerializer.Serialize(new { Version = 1, Items = items, Pins = new Guid?[10], Order = items.Select(value => value.Id).ToArray() }));
                Task.Run(() => library.LoadAsync()).GetAwaiter().GetResult();
                long memory = GC.GetTotalMemory(true); var timer = Stopwatch.StartNew();
                var window = new StampLibraryWindow(library, () => new EditorSession(), _ => { });
                var content = (FrameworkElement)window.Content; window.Content = null;
                var root = new Border { Child = content, Width = window.Width - 2, Height = 580, UseLayoutRounding = true };
                root.Measure(new Size(root.Width, root.Height)); root.Arrange(new Rect(0, 0, root.Width, root.Height)); root.UpdateLayout(); double first = timer.Elapsed.TotalMilliseconds;
                var list = Field<ListBox>(window, "grid");
                check("audit stamp viewport is bounded for " + count, list.Items.Count == count && Visuals(list).OfType<ListBoxItem>().Count() is > 0 and <= 40);
                var scroll = Visuals(list).OfType<ScrollViewer>().First();
                scroll.ScrollToEnd(); root.UpdateLayout();
                Console.WriteLine($"AUDIT UX SCROLL count={count} offset={scroll.VerticalOffset} extent={scroll.ExtentHeight} viewport={scroll.ViewportHeight} tags={string.Join(',', Visuals(list).OfType<ListBoxItem>().Select(tile => Array.FindIndex(items, entry => Equals(tile.Tag, entry.Id))))}");
                check("audit continuous scrolling reaches the final stamp " + count,
                    Visuals(list).OfType<ListBoxItem>().Any(tile => Equals(tile.Tag, items[^1].Id)) && Visuals(list).OfType<ListBoxItem>().Count() <= 40);
                list.SelectedItem = items[^1];
                check("audit recycled stamp selection retains catalog identity " + count, Field<Guid?>(window, "selected") == items[^1].Id);
                scroll.ScrollToHome(); root.UpdateLayout();
                check("audit scrolling home reuses a bounded viewport " + count,
                    Visuals(list).OfType<ListBoxItem>().Any(tile => Equals(tile.Tag, items[0].Id)) && Visuals(list).OfType<ListBoxItem>().Count() <= 40);
                var samples = new List<double>(); var searchSamples = new List<double>(); long peakManaged = GC.GetTotalMemory(false);
                for (int i = 0; i < 12; i++) { timer.Restart(); window.Refresh(); root.UpdateLayout(); samples.Add(timer.Elapsed.TotalMilliseconds); peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false)); }
                var search = Field<TextBox>(window, "search");
                var initialGeneration = Field<CancellationTokenSource>(window, "thumbnails");
                for (int i = 0; i < 20; i++) search.Text = "quick draft " + i;
                check("audit rapid typing coalesces thumbnail work " + count, ReferenceEquals(initialGeneration, Field<CancellationTokenSource>(window, "thumbnails")));
                for (int i = 0; i < 10; i++)
                {
                    var generation = Field<CancellationTokenSource>(window, "thumbnails"); timer.Restart(); search.Text = i % 2 == 0 ? "stamp 1" : "stamp";
                    while (ReferenceEquals(generation, Field<CancellationTokenSource>(window, "thumbnails")) && timer.ElapsedMilliseconds < 3000)
                    { System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { })); Thread.Sleep(2); }
                    root.UpdateLayout(); searchSamples.Add(timer.Elapsed.TotalMilliseconds); peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                }
                check("audit debounced search finishes with only visible containers " + count, list.Items.Count == count && Visuals(list).OfType<ListBoxItem>().Count() is > 0 and <= 40 && searchSamples.All(value => value < 3000));
                samples.Sort(); searchSamples.Sort(); results.Add(new { Count = count, FirstViewportMs = first, RefreshP95Ms = samples[(int)Math.Ceiling(samples.Count * .95) - 1], SearchP95IncludingDebounceMs = searchSamples[(int)Math.Ceiling(searchSamples.Count * .95) - 1], SampledManagedPeakDelta = peakManaged - memory, ManagedBytesDelta = GC.GetTotalMemory(false) - memory, CatalogCount = list.Items.Count, RealizedCount = Visuals(list).OfType<ListBoxItem>().Count() });
                root.Child = null; window.Content = content;
                window.Close();
            }
            Console.WriteLine("AUDIT UX STAMP BUDGET " + JsonSerializer.Serialize(results));
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static IEnumerable<DependencyObject> Visuals(DependencyObject? value)
    {
        if (value is null) yield break; yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) foreach (var child in Visuals(VisualTreeHelper.GetChild(value, i))) yield return child;
    }
    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte x) { double c = x / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Lum(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        double a = Lum(first), b = Lum(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private sealed class DetachedSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
