using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Input = System.Windows.Input;
using Threading = System.Windows.Threading;
using L = FloatCanvas.Localization.AppText;

/// <summary>Production library actions on detached WPF controls and an isolated real stamp catalog.</summary>
internal static class StampLibraryUiTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(Action<string, bool> check)
    {
        _ = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        var context = SynchronizationContext.Current; string language = L.Preference;
        SynchronizationContext.SetSynchronizationContext(new Threading.DispatcherSynchronizationContext(Threading.Dispatcher.CurrentDispatcher));
        string root = Environment.GetEnvironmentVariable("FLOATCANVAS_STAMP_LIBRARY_UI_TEST_DIR") ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-stamp-ui");
        string profile = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(profile);
        try
        {
            var saved = Task.Run(() => Prepare(profile)).GetAwaiter().GetResult();
            foreach (string locale in new[] { "zh-CN", "en" }) { L.Initialize(locale); Panel(saved.Library, saved.Editable, saved.Merged, root, locale, check); }
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); L.Initialize(language); }
    }

    private static async Task<(StampLibrary Library, Guid Editable, Guid Merged)> Prepare(string profile)
    {
        var library = new StampLibrary(profile); await library.LoadAsync();
        var source = new EditorSession(); source.Add(new() { Kind = ElementKind.Rectangle, Width = 120, Height = 75, Style = new() { StrokeWidth = 0, Fill = 0xFF2383E2 } });
        var editable = await library.SaveSelectionAsync(source, "Editable specimen");
        source.Document.Elements[0].Style.Fill = 0xFFEA6840;
        var merged = await library.SaveSelectionAsync(source, "Merged specimen", StampSaveMode.RasterMerge);
        // Copy valid self-contained revisions to exercise the existing 48-item paging boundary.
        string catalogPath = Path.Combine(library.DirectoryPath, "library.json"); var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!;
        var entries = catalog["Items"]!.AsArray(); var prototype = entries[0]!.DeepClone();
        string sourceBase = Path.Combine(library.DirectoryPath, editable.Id.ToString("N") + "-" + editable.Revision.ToString("N"));
        for (int i = 0; i < 49; i++)
        {
            Guid id = Guid.NewGuid(), revision = Guid.NewGuid(); var item = prototype.DeepClone();
            item["Id"] = id; item["Revision"] = revision; item["Name"] = "Archive " + i.ToString("00"); item["UpdatedAtUtc"] = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i);
            entries.Add(item);
            string destination = Path.Combine(library.DirectoryPath, id.ToString("N") + "-" + revision.ToString("N"));
            File.Copy(sourceBase + ".floatcanvas", destination + ".floatcanvas"); File.Copy(sourceBase + ".png", destination + ".png");
        }
        await File.WriteAllTextAsync(catalogPath, catalog.ToJsonString());
        library = new StampLibrary(profile); await library.LoadAsync();
        return (library, editable.Id, merged.Id);
    }

    private static void Panel(StampLibrary library, Guid editable, Guid merged, string output, string locale, Action<string, bool> check)
    {
        AppTheme.Apply("light"); bool allowed = true; var session = new EditorSession(); var uses = new List<Guid>(); var sources = new List<Guid>();
        var window = new StampLibraryWindow(library, () => session, uses.Add, () => allowed, sources.Add);
        var list = Field<Controls.ListBox>(window, "grid"); var search = Field<Controls.TextBox>(window, "search"); var slot = Field<Controls.ComboBox>(window, "slot");
        var buttons = Field<Dictionary<string, Controls.Button>>(window, "commandButtons");
        Controls.ListBoxItem Tile(Guid id) => list.Items.OfType<Controls.ListBoxItem>().Single(item => Equals(item.Tag, id));
        Controls.MenuItem Menu(Guid id, string key) => Tile(id).ContextMenu.Items.OfType<Controls.MenuItem>().Single(item => Equals(item.Tag, key));
        try
        {
            check(locale + " management actions share compact icon buttons and delayed accessible tooltips", buttons.Count == 8 && buttons.Values.All(button =>
                button.Content is Controls.Viewbox && button.Width == 30 && button.Height == 30 && Controls.ToolTipService.GetInitialShowDelay(button) == 650 &&
                !string.IsNullOrWhiteSpace(Wpf.Automation.AutomationProperties.GetName(button))));
            var actions = Field<Controls.WrapPanel>(window, "actions");
            check(locale + " library actions are separated into four compact task groups", actions.Children.OfType<Controls.StackPanel>().Count() == 4 && actions.Children.OfType<Controls.Border>().Count() == 3);
            check(locale + " page navigation is icon based while the page counter stays readable", Field<Controls.Button>(window, "previous").Content is Controls.Viewbox && Field<Controls.Button>(window, "next").Content is Controls.Viewbox &&
                !string.IsNullOrWhiteSpace(Field<Controls.TextBlock>(window, "pageLabel").Text));
            check(locale + " tiles show only artwork and one name rather than repeated mode text", list.Items.Count == 48 && list.Items.OfType<Controls.ListBoxItem>().All(item =>
                item.Content is Controls.StackPanel tile && tile.Children.Count == 2 && tile.Children[0] is Controls.Image && tile.Children[1] is Controls.TextBlock));
            PumpUntil(() => list.Items.OfType<Controls.ListBoxItem>().All(item => ((Controls.StackPanel)item.Content).Children.OfType<Controls.Image>().SingleOrDefault()?.Source is not null));
            check(locale + " all first-page thumbnails load from the isolated real revisions", list.Items.OfType<Controls.ListBoxItem>().All(item => ((Controls.StackPanel)item.Content).Children.OfType<Controls.Image>().Single().Source is not null));

            list.SelectedItem = Tile(editable);
            check(locale + " editable stamps cannot invoke the merged-source action", !buttons["Stamp.UseSource"].IsEnabled);
            var tile = Tile(merged); tile.RaiseEvent(new Input.MouseButtonEventArgs(Input.Mouse.PrimaryDevice, Environment.TickCount, Input.MouseButton.Right) { RoutedEvent = Input.Mouse.PreviewMouseDownEvent });
            check(locale + " right-click selects the target stamp before its context actions", Equals(((Controls.ListBoxItem)list.SelectedItem).Tag, merged) && buttons["Stamp.UseSource"].IsEnabled);
            var menu = Tile(merged).ContextMenu;
            menu.RaiseEvent(new Wpf.RoutedEventArgs(Controls.ContextMenu.OpenedEvent));
            var pins = Menu(merged, "Stamp.Pin"); var unpins = Menu(merged, "Stamp.Unpin");
            check(locale + " tile context menus provide all ten numbered pin and unpin destinations", pins.Items.Count == 10 && unpins.Items.Count == 10 &&
                pins.Items.OfType<Controls.MenuItem>().Select(item => item.Header.ToString()).SequenceEqual(new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" }));
            Click((Controls.MenuItem)pins.Items[9]); PumpUntil(() => !Field<bool>(window, "busy") && library.Pins[9] == merged);
            var reloaded = Task.Run(async () => { var copy = new StampLibrary(Path.GetDirectoryName(library.DirectoryPath)!); await copy.LoadAsync(); return copy; }).GetAwaiter().GetResult();
            check(locale + " context pin writes the exact target into slot zero and persists it", library.Pins[9] == merged && reloaded.Pins[9] == merged);
            Click((Controls.MenuItem)Menu(merged, "Stamp.Unpin").Items[9]); PumpUntil(() => !Field<bool>(window, "busy") && library.Pins[9] is null);
            check(locale + " context unpin clears only its chosen slot", library.Pins[9] is null && library.Pins[0] == editable);
            slot.SelectedIndex = 5; Click(buttons["Stamp.Pin"]); PumpUntil(() => !Field<bool>(window, "busy") && library.Pins[5] == merged);
            Click(buttons["Stamp.Unpin"]); PumpUntil(() => !Field<bool>(window, "busy") && library.Pins[5] is null);
            check(locale + " compact slot controls preserve normal pin and unpin behavior", library.Pins[5] is null && library.Pins[0] == editable);

            list.SelectedItem = Tile(merged);
            Invoke(window, "UseSelectedSource"); check(locale + " merged-source icon retains its exact stamp callback", sources.SequenceEqual([merged]));
            bool handled = (bool)Invoke(window, "HandleShortcut", Input.Key.Enter, Input.ModifierKeys.None, list)!;
            check(locale + " Enter uses the selected stamp through the normal action path", handled && uses.SequenceEqual([merged]));
            foreach (var input in new Wpf.DependencyObject[] { search, slot })
                foreach (var key in new[] { Input.Key.Enter, Input.Key.F2, Input.Key.Delete })
                    check(locale + " " + input.GetType().Name + " owns its " + key + " key without invoking stamp management", !(bool)Invoke(window, "HandleShortcut", key, Input.ModifierKeys.None, input)! && uses.Count == 1 && library.Items.Count == 51);
            search.Text = "specimen";
            check(locale + " Ctrl+F focuses and selects the existing search draft", (bool)Invoke(window, "HandleShortcut", Input.Key.F, Input.ModifierKeys.Control, search)! && search.SelectionLength == search.Text.Length);
            search.Text = "";
            check(locale + " Ctrl+N with no canvas selection explains the requirement without opening a dialog", (bool)Invoke(window, "HandleShortcut", Input.Key.N, Input.ModifierKeys.Control, list)! &&
                Field<Controls.TextBlock>(window, "status").Text == L.Get("Stamp.NoSelection"));
            allowed = false; int before = uses.Count;
            check(locale + " mutation guard blocks Enter and direct use callbacks", !(bool)Invoke(window, "HandleShortcut", Input.Key.Enter, Input.ModifierKeys.None, list)!);
            Invoke(window, "UseSelected"); check(locale + " unavailable library actions preserve the current document and selection", uses.Count == before && session.Document.Elements.Count == 0);
            allowed = true; Set(window, "busy", true); Invoke(window, "SetBusyControls"); int oldPage = Field<int>(window, "page"); Invoke(window, "ChangePage", 1);
            check(locale + " busy async work blocks paging search and repeated management", Field<int>(window, "page") == oldPage && !search.IsEnabled && !list.IsEnabled &&
                !(bool)Invoke(window, "HandleShortcut", Input.Key.Enter, Input.ModifierKeys.None, list)!);
            Set(window, "busy", false); Invoke(window, "SetBusyControls");

            var oldToken = Field<CancellationTokenSource>(window, "thumbnails").Token;
            search.Text = "Archive";
            check(locale + " changing search cancels obsolete thumbnail work and filters the gallery", oldToken.IsCancellationRequested && list.Items.Count == 48);
            Click(Field<Controls.Button>(window, "next"));
            check(locale + " filtered paging retains its 48-item boundary and last-page count", Field<int>(window, "page") == 1 && list.Items.Count == 1);
            Click(Field<Controls.Button>(window, "previous"));
            check(locale + " previous-page icon returns to the first filtered page", Field<int>(window, "page") == 0 && list.Items.Count == 48);
            search.Text = "not-present";
            check(locale + " empty search results disable both pagination actions", list.Items.Count == 0 && !Field<Controls.Button>(window, "previous").IsEnabled && !Field<Controls.Button>(window, "next").IsEnabled);
            search.Text = "specimen"; list.SelectedItem = Tile(merged);
            PumpUntil(() => list.Items.OfType<Controls.ListBoxItem>().All(item => ((Controls.StackPanel)item.Content).Children.OfType<Controls.Image>().SingleOrDefault()?.Source is not null));
            Render(window, output, locale);
            check(locale + " stamp management checks open no native library or popup window", !window.IsVisible && new Wpf.Interop.WindowInteropHelper(window).Handle == 0);
        }
        finally { window.Close(); Pump(); }
        check(locale + " closing the library cancels thumbnail work and detaches its live state", Field<bool>(window, "closed") && !window.IsVisible);
    }

    private static void Render(StampLibraryWindow window, string output, string locale)
    {
        if (Environment.GetEnvironmentVariable("FLOATCANVAS_STAMP_LIBRARY_UI_TEST_DIR") is null) return;
        // A detached visual has no presentation-source resource invalidation; build each
        // theme's production tree after changing the palette instead of caching old brushes.
        foreach (string theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme); Pump();
            var preview = new StampLibraryWindow(Field<StampLibrary>(window, "library"), Field<Func<EditorSession>>(window, "getSession"), _ => { }, useSource: _ => { });
            var previewSearch = Field<Controls.TextBox>(preview, "search"); previewSearch.Text = Field<Controls.TextBox>(window, "search").Text;
            var previewList = Field<Controls.ListBox>(preview, "grid");
            previewList.SelectedItem = previewList.Items.OfType<Controls.ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, Field<Guid?>(window, "selected")));
            Field<Controls.ComboBox>(preview, "slot").SelectedIndex = Field<Controls.ComboBox>(window, "slot").SelectedIndex;
            PumpUntil(() => previewList.Items.OfType<Controls.ListBoxItem>().All(item => ((Controls.StackPanel)item.Content).Children.OfType<Controls.Image>().SingleOrDefault()?.Source is not null));
            var content = (Wpf.FrameworkElement)preview.Content; preview.Content = null;
            var background = new Controls.Border { Child = content, Width = 840, Height = 570 };
            background.SetResourceReference(Controls.Border.BackgroundProperty, "CanvasSurface");
            background.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "CanvasText");
            try
            {
                Pump(); background.Measure(new Wpf.Size(840, 570)); background.Arrange(new Wpf.Rect(0, 0, 840, 570)); background.UpdateLayout(); Pump();
                var bitmap = new Wpf.Media.Imaging.RenderTargetBitmap(1260, 855, 144, 144, Wpf.Media.PixelFormats.Pbgra32); bitmap.Render(background);
                var encoder = new Wpf.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(Wpf.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"stamp-library-{locale}-{theme}.png")); encoder.Save(file);
            }
            finally { background.Child = null; preview.Content = content; preview.Close(); }
        }
    }
    private static void Click(Controls.Button button) => button.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
    private static void Click(Controls.MenuItem item) => item.RaiseEvent(new Wpf.RoutedEventArgs(Controls.MenuItem.ClickEvent));
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Pump()
    { var frame = new Threading.DispatcherFrame(); Threading.Dispatcher.CurrentDispatcher.BeginInvoke(Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Threading.Dispatcher.PushFrame(frame); }
    private static void PumpUntil(Func<bool> done)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(5)) { Pump(); Thread.Sleep(1); }
        if (!done()) throw new TimeoutException("The isolated stamp library operation did not finish.");
    }
}
