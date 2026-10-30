using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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

/// <summary>Production library actions in an invisible nonactivating host and an isolated real catalog.</summary>
internal static class StampLibraryUiTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void RunNativeGeometry(Action<string, bool> check)
    {
        _ = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new Threading.DispatcherSynchronizationContext(Threading.Dispatcher.CurrentDispatcher));
        try { RunNativeGeometryCore(check); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }
    private static void RunNativeGeometryCore(Action<string, bool> check)
    {
        AppTheme.Apply("light");
        string root = Environment.GetEnvironmentVariable("FLOATCANVAS_STAMP_LIBRARY_UI_TEST_DIR") ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-stamp-native-geometry");
        Directory.CreateDirectory(root);
        var saved = Task.Run(() => Prepare(Path.Combine(root, Guid.NewGuid().ToString("N")))).GetAwaiter().GetResult();
        var reports = new List<object>(); var rows = new List<(string Name, int Actual, int Expected)>();
        foreach (var (name, columns) in new[] { ("default", StampLibraryWindow.DefaultTileColumns), ("minimum", StampLibraryWindow.DefaultTileColumns - 1) })
        {
            var window = new StampLibraryWindow(saved.Library, () => new EditorSession(), _ => { })
            { ShowActivated = false, ShowInTaskbar = false, Topmost = false,
                WindowStartupLocation = Wpf.WindowStartupLocation.Manual, Left = Wpf.SystemParameters.WorkArea.Left + 24, Top = Wpf.SystemParameters.WorkArea.Top + 24 };
            if (name == "minimum") window.Width = window.MinWidth;
            double width = window.Width;
            try
            {
                window.Show(); Pump(); window.UpdateLayout(); Pump();
                var list = Field<Controls.ListBox>(window, "grid");
                var scroll = Visuals(list).OfType<Controls.ScrollViewer>().First();
                var viewport = Visuals(scroll).OfType<Controls.ScrollContentPresenter>().First();
                var presenter = Visuals(list).OfType<Controls.ItemsPresenter>().First();
                var wrap = Visuals(list).OfType<VirtualizingTilePanel>().First();
                var tiles = Realized(list).ToArray();
                var first = tiles[0].TranslatePoint(new Wpf.Point(), wrap);
                var row = tiles.Where(tile => Math.Abs(tile.TranslatePoint(new Wpf.Point(), wrap).Y - first.Y) < 1).ToArray();
                nint handle = new Wpf.Interop.WindowInteropHelper(window).Handle;
                GetWindowRect(handle, out var native); GetClientRect(handle, out var client);
                var dpi = Wpf.Media.VisualTreeHelper.GetDpi(window);
                object Size(Wpf.FrameworkElement element) => new { element.ActualWidth, element.ActualHeight, element.DesiredSize.Width, element.DesiredSize.Height };
                reports.Add(new { Name = name, RequestedWidth = width, window.ActualWidth, window.ActualHeight, DpiX = dpi.PixelsPerInchX, DpiY = dpi.PixelsPerInchY,
                    NativeWidth = native.Right - native.Left, ClientWidth = client.Right - client.Left, NativeHeight = native.Bottom - native.Top,
                    WindowBorder = Size((Wpf.FrameworkElement)window.Template.FindName("WindowBorder", window)),
                    Body = Size((Wpf.FrameworkElement)window.Content), Grid = Size(list), ScrollViewer = Size(scroll),
                    ScrollContentPresenter = Size(viewport), ItemsPresenter = Size(presenter), WrapPanel = Size(wrap),
                    FirstRowColumns = row.Length, ExpectedColumns = columns, FirstTile = Size(tiles[0]),
                    FirstTileMargin = tiles[0].Margin.ToString(), ListPadding = list.Padding.ToString(), ListBorder = list.BorderThickness.ToString() });
                var bitmap = new Wpf.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, Wpf.Media.PixelFormats.Pbgra32);
                bitmap.Render(window); var encoder = new Wpf.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(Wpf.Media.Imaging.BitmapFrame.Create(bitmap));
                using var image = File.Create(Path.Combine(root, "stamp-native-first-show-" + name + ".png")); encoder.Save(image);
                rows.Add((name, row.Length, columns));
            }
            finally { window.Close(); Pump(); }
        }
        string reportPath = Path.Combine(root, "stamp-native-first-show-geometry.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(reportPath);
        foreach (var row in rows) check("native first Show " + row.Name + " width yields the intended complete tile columns", row.Actual == row.Expected);
    }
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
        // A continuous gallery must expose all 51 real revisions across several rows.
        string catalogPath = Path.Combine(library.DirectoryPath, "library.json"); var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!;
        var entries = catalog["Items"]!.AsArray(); var prototype = entries.Single(item => item!["Id"]!.GetValue<Guid>() == editable.Id)!.DeepClone();
        string sourceBase = Path.Combine(library.DirectoryPath, editable.Id.ToString("N") + "-" + editable.Revision.ToString("N"));
        for (int i = 0; i < 49; i++)
        {
            Guid id = Guid.NewGuid(), revision = Guid.NewGuid(); var item = prototype.DeepClone();
            item["Id"] = id; item["Revision"] = revision; item["Name"] = "Archive " + i.ToString("00"); item["UpdatedAtUtc"] = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i);
            entries.Add(item);
            string destination = Path.Combine(library.DirectoryPath, id.ToString("N") + "-" + revision.ToString("N"));
            File.Copy(sourceBase + ".floatcanvas", destination + ".floatcanvas"); File.Copy(sourceBase + ".png", destination + ".png");
        }
        catalog.AsObject().Remove("Order"); // Existing catalogs predate explicit drag ordering.
        await File.WriteAllTextAsync(catalogPath, catalog.ToJsonString());
        library = new StampLibrary(profile); await library.LoadAsync();
        return (library, editable.Id, merged.Id);
    }

    private static IEnumerable<Controls.ListBoxItem> Realized(Controls.ListBox list) => list.Items.Cast<object>().Select(item => list.ItemContainerGenerator.ContainerFromItem(item)).OfType<Controls.ListBoxItem>();

    private static void Panel(StampLibrary library, Guid editable, Guid merged, string output, string locale, Action<string, bool> check)
    {
        AppTheme.Apply("light"); bool allowed = true; var session = new EditorSession(); var uses = new List<Guid>(); var sources = new List<Guid>();
        Task.Run(async () => { for (int i = 0; i < 10; i++) await library.PinAsync(i, null); await library.PinAsync(0, editable); await library.PinAsync(1, merged); }).GetAwaiter().GetResult();
        var window = new StampLibraryWindow(library, () => session, uses.Add, () => allowed, sources.Add);
        var list = Field<Controls.ListBox>(window, "grid"); var search = Field<Controls.TextBox>(window, "search");
        var buttons = Field<Dictionary<string, Controls.Button>>(window, "commandButtons");
        Controls.ListBoxItem Tile(Guid id) => Realized(list).Single(item => Equals(item.Tag, id));
        StampEntry Entry(Guid id) => list.Items.Cast<StampEntry>().Single(item => item.Id == id);
        Controls.MenuItem Menu(Guid id, string key) => Tile(id).ContextMenu.Items.OfType<Controls.MenuItem>().Single(item => Equals(item.Tag, key));
        try
        {
            ShowFixture(window);
            var root = (Wpf.FrameworkElement)window.Content; root.Measure(new Wpf.Size(window.Width - 2, 580)); root.Arrange(new Wpf.Rect(0, 0, window.Width - 2, 580)); root.UpdateLayout();
            check(locale + " management actions share compact icon buttons and delayed accessible tooltips", buttons.Count == 6 && buttons.Values.All(button =>
                button.Content is Controls.Viewbox && button.Width == 30 && button.Height == 30 && Controls.ToolTipService.GetInitialShowDelay(button) == 650 &&
                !string.IsNullOrWhiteSpace(Wpf.Automation.AutomationProperties.GetName(button))));
            var actions = Field<Controls.WrapPanel>(window, "actions");
            check(locale + " library actions are separated into three compact task groups beside the new shelf", actions.Children.OfType<Controls.StackPanel>().Count() == 3 && actions.Children.OfType<Controls.Border>().Count() == 2);
            window.ApplyTemplate();
            check(locale + " instructions live in the shared question-mark help beside Close", window.Template.FindName("Help", window) is Controls.Button { Visibility: Wpf.Visibility.Visible } help &&
                Wpf.Automation.AutomationProperties.GetName(help) == L.Get("StampShelf.Help") && (string)window.Resources["CanvasWindowHelpText"] == L.Get("StampShelf.HelpText"));
            check(locale + " all entries retain their names with compact group or raster labels and overlaid pin badges", list.Items.Count == 51 && Realized(list).All(item =>
                item.Content is Controls.StackPanel tile && tile.Children.Count == 3 && tile.Children[0] is Controls.Grid && tile.Children[1] is Controls.TextBlock && tile.Children[2] is Controls.TextBlock { Tag: "Stamp.Mode" }));
            PumpUntil(() => Realized(list).All(item => Thumbnail(item).Source is not null));
            check(locale + " all realized continuous-list thumbnails load from the isolated real revisions", Realized(list).All(item => Thumbnail(item).Source is not null));
            check(locale + " shelf matches toolbar digit order one through nine then zero", Field<Controls.WrapPanel>(window, "slotShelf").Children.OfType<Controls.Border>()
                .Select(frame => ((int)frame.Tag + 1) % 10).SequenceEqual(Enumerable.Range(0, 10).Select(index => (index + 1) % 10)) && Field<int?>(window, "selectedSlot") is null);
            check(locale + " fixed compact slots stay left aligned with small independent clear buttons", Field<Controls.WrapPanel>(window, "slotShelf").HorizontalAlignment == Wpf.HorizontalAlignment.Left &&
                Field<Controls.WrapPanel>(window, "slotShelf").Children.OfType<Controls.Border>().All(frame => frame.Width == 46 && frame.Height == 46) &&
                Field<Dictionary<int, Controls.Button>>(window, "slotClearButtons").Values.All(button => button.Width == 14 && button.Height == 14));
            check(locale + " shelf slot number badges remain plain digits without duplicated pin glyphs", Field<Dictionary<int, Controls.Button>>(window, "slotButtons").Values.All(button =>
                ((Controls.Grid)button.Content).Children.OfType<Controls.Border>().Single().Child is Controls.TextBlock));

            list.SelectedItem = Entry(editable);
            check(locale + " editable stamps cannot invoke the merged-source action", !buttons["Stamp.UseSource"].IsEnabled);
            var tile = Tile(merged); tile.RaiseEvent(new Input.MouseButtonEventArgs(Input.Mouse.PrimaryDevice, Environment.TickCount, Input.MouseButton.Right) { RoutedEvent = Input.Mouse.PreviewMouseDownEvent });
            check(locale + " right-click selects the target stamp before its context actions", ((StampEntry)list.SelectedItem).Id == merged && buttons["Stamp.UseSource"].IsEnabled);
            var menu = Tile(merged).ContextMenu;
            menu.RaiseEvent(new Wpf.RoutedEventArgs(Controls.ContextMenu.OpenedEvent));
            check(locale + " a pinned stamp exposes only one unpin action alongside its normal management commands", menu.Items.OfType<Controls.MenuItem>().Any(item => Equals(item.Tag, "Stamp.Unpin")) &&
                !menu.Items.OfType<Controls.MenuItem>().Any(item => Equals(item.Tag, "Stamp.Pin")) && Menu(merged, "Stamp.Unpin").Items.Count == 0);
            Click(Menu(merged, "Stamp.Unpin")); PumpUntil(() => !Field<bool>(window, "busy") && library.Pins[1] is null);
            var pins = Menu(merged, "Stamp.Pin");
            check(locale + " an unpinned stamp exposes only the ten-slot pin submenu in visual digit order", pins.Items.Count == 10 && !Tile(merged).ContextMenu.Items.OfType<Controls.MenuItem>().Any(item => Equals(item.Tag, "Stamp.Unpin")) &&
                pins.Items.OfType<Controls.MenuItem>().Select(item => item.Header.ToString()).SequenceEqual(Enumerable.Range(0, 10).Select(i => ((i + 1) % 10).ToString())));
            Click((Controls.MenuItem)pins.Items[9]); PumpUntil(() => !Field<bool>(window, "busy") && library.Pins[9] == merged);
            var reloaded = Task.Run(async () => { var copy = new StampLibrary(Path.GetDirectoryName(library.DirectoryPath)!); await copy.LoadAsync(); return copy; }).GetAwaiter().GetResult();
            check(locale + " context pin writes the exact target into slot zero and persists it", library.Pins[9] == merged && reloaded.Pins[9] == merged);
            Click(Field<Dictionary<int, Controls.Button>>(window, "slotButtons")[5]);
            Tile(merged).RaiseEvent(new Input.MouseButtonEventArgs(Input.Mouse.PrimaryDevice, Environment.TickCount, Input.MouseButton.Left) { RoutedEvent = Input.Mouse.PreviewMouseUpEvent });
            check(locale + " a tile release without its own matching press cannot pin or use a stamp", library.Pins[5] is null && !Field<bool>(window, "busy") && uses.Count == 0);
            var pinsBeforeFailedAssign = library.Pins.ToArray();
            Invoke(window, "ActivateTile", Guid.NewGuid());
            PumpUntil(() => !Field<bool>(window, "busy"));
            check(locale + " a failed stamp assignment preserves the selected destination for retry",
                Field<int?>(window, "selectedSlot") == 5 && library.Pins.SequenceEqual(pinsBeforeFailedAssign));
            string pinCatalog = Path.Combine(library.DirectoryPath, "library.json");
            byte[] pinCatalogBeforeFailure = File.ReadAllBytes(pinCatalog);
            var pinCatalogAttributes = File.GetAttributes(pinCatalog);
            try
            {
                File.SetAttributes(pinCatalog, pinCatalogAttributes | FileAttributes.ReadOnly);
                Invoke(window, "ActivateTile", merged);
                PumpUntil(() => !Field<bool>(window, "busy"));
                check(locale + " a catalog write failure keeps the destination selected and preserves pins for retry",
                    Field<int?>(window, "selectedSlot") == 5 && library.Pins.SequenceEqual(pinsBeforeFailedAssign) &&
                    File.ReadAllBytes(pinCatalog).SequenceEqual(pinCatalogBeforeFailure));
            }
            finally { File.SetAttributes(pinCatalog, pinCatalogAttributes); }
            Invoke(window, "ActivateTile", merged);
            PumpUntil(() => !Field<bool>(window, "busy") && library.Pins[5] == merged);
            check(locale + " choosing a shelf slot then a stamp pins it once and clears the active destination",
                Field<int?>(window, "selectedSlot") is null && uses.Count == 0 && library.Pins[9] == merged &&
                Field<Dictionary<int, Controls.Button>>(window, "slotButtons").Values.Cast<SelectionTileButton>().All(button => !button.Selected));
            Invoke(window, "ActivateTile", editable);
            check(locale + " the next ordinary stamp click cannot replace a slot without choosing its destination again",
                !Field<bool>(window, "busy") && library.Pins[5] == merged && uses.Count == 0);
            Click(Field<Dictionary<int, Controls.Button>>(window, "slotButtons")[5]);
            Invoke(window, "ActivateTile", merged);
            PumpUntil(() => !Field<bool>(window, "busy"));
            check(locale + " assigning the same stamp again also completes the one-shot destination",
                Field<int?>(window, "selectedSlot") is null && library.Pins[5] == merged);
            var badge = ((Controls.Grid)((Controls.StackPanel)Tile(merged).Content).Children[0]).Children.OfType<Controls.Border>().Single(item => Equals(item.Tag, "Stamp.PinBadge"));
            check(locale + " duplicate pins show a compact vector pin with the first toolbar-order slot number", badge.Child is Controls.StackPanel pinBadge &&
                pinBadge.Children.Count == 2 && pinBadge.Children[0] is Controls.Viewbox { Width: 12, Height: 12 } && pinBadge.Children[1] is Controls.TextBlock { Text: "6" });
            Click(Menu(merged, "Stamp.Unpin")); PumpUntil(() => !Field<bool>(window, "busy") && library.Pins[5] is null);
            check(locale + " repeated unpin follows toolbar order one through nine then zero and retains other stamps", library.Pins[9] == merged && library.Pins[0] == editable);
            Click(Field<Dictionary<int, Controls.Button>>(window, "slotButtons")[5]);
            Invoke(window, "ActivateTile", merged); PumpUntil(() => !Field<bool>(window, "busy") && library.Pins[5] == merged);
            Click(Field<Dictionary<int, Controls.Button>>(window, "slotButtons")[5]);
            Click(Field<Dictionary<int, Controls.Button>>(window, "slotClearButtons")[5]); PumpUntil(() => !Field<bool>(window, "busy") && library.Pins[5] is null);
            check(locale + " the slot corner X clears only its pin without changing slot selection or using the stamp", library.Items.Any(item => item.Id == merged) && library.Pins[0] == editable &&
                library.Pins[9] == merged && Field<int?>(window, "selectedSlot") == 5 && uses.Count == 0);
            Click(Field<Dictionary<int, Controls.Button>>(window, "slotButtons")[5]);
            check(locale + " clicking the selected slot again deselects the single active destination", Field<int?>(window, "selectedSlot") is null);
            Click(Field<Dictionary<int, Controls.Button>>(window, "slotButtons")[5]); Click(Field<Dictionary<int, Controls.Button>>(window, "slotButtons")[9]);
            check(locale + " selecting another slot replaces the previous active destination", Field<int?>(window, "selectedSlot") == 9);
            check(locale + " Escape cancels slot selection before ordinary stamp shortcuts", (bool)Invoke(window, "HandleShortcut", Input.Key.Escape, Input.ModifierKeys.None, list)! && Field<int?>(window, "selectedSlot") is null);
            Invoke(window, "ActivateTile", merged);
            check(locale + " an ordinary tile click without a selected slot retains normal selection-only behavior", uses.Count == 0 && library.Pins[5] is null);

            list.SelectedItem = Entry(merged);
            Invoke(window, "UseSelectedSource"); check(locale + " merged-source icon retains its exact stamp callback", sources.SequenceEqual([merged]));
            ShowFixture(window);
            bool handled = (bool)Invoke(window, "HandleShortcut", Input.Key.Enter, Input.ModifierKeys.None, list)!;
            check(locale + " Enter uses the selected stamp through the normal action path", handled && uses.SequenceEqual([merged]));
            ShowFixture(window);
            foreach (var input in new Wpf.DependencyObject[] { search })
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
            allowed = true; Set(window, "busy", true); Invoke(window, "SetBusyControls");
            check(locale + " busy async work blocks search and repeated management", !search.IsEnabled && !list.IsEnabled &&
                !(bool)Invoke(window, "HandleShortcut", Input.Key.Enter, Input.ModifierKeys.None, list)!);
            Set(window, "busy", false); Invoke(window, "SetBusyControls");

            var oldToken = Field<CancellationTokenSource>(window, "thumbnails").Token;
            search.Text = "Archive";
            PumpUntil(() => oldToken.IsCancellationRequested && list.Items.Count == 49);
            check(locale + " changing search cancels obsolete thumbnail work and keeps every matching entry", oldToken.IsCancellationRequested && list.Items.Count == 49);
            search.Text = "not-present";
            PumpUntil(() => list.Items.Count == 0);
            check(locale + " empty search results contain no stale entries", list.Items.Count == 0);
            search.Text = ""; PumpUntil(() => list.Items.Count == 51); list.SelectedItem = Entry(merged); Invoke(window, "ShowStatus", "");
            PumpUntil(() => Realized(list).All(item => Thumbnail(item).Source is not null));
            CompactLayout(window, locale, check);
            Render(window, output, locale);
            GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
            check(locale + " stamp management fixture remains invisible and nonactivating", window.Opacity == 0 && !window.ShowActivated && foreground != Environment.ProcessId);
        }
        finally { window.Close(); Pump(); }
        check(locale + " closing the library cancels thumbnail work and detaches its live state", Field<bool>(window, "closed") && !window.IsVisible);
    }

    private static void CompactLayout(StampLibraryWindow window, string locale, Action<string, bool> check)
    {
        var content = (Wpf.FrameworkElement)window.Content; window.Content = null;
        double scale = Wpf.Media.VisualTreeHelper.GetDpi(window).DpiScaleX, chromeEdges = 2 * Math.Round(scale) / scale;
        double defaultContentWidth = window.Width - chromeEdges, contentHeight = window.Height - AppWindowChrome.CaptionHeight - chromeEdges;
        var background = new Controls.Border { Child = content, Width = defaultContentWidth, Height = contentHeight, UseLayoutRounding = true };
        try
        {
            void Layout(double width)
            { background.Width = width; background.Measure(new Wpf.Size(width, contentHeight)); background.Arrange(new Wpf.Rect(0, 0, width, contentHeight)); background.UpdateLayout(); }
            Layout(defaultContentWidth);
            var list = Field<Controls.ListBox>(window, "grid"); var scroll = Visuals(list).OfType<Controls.ScrollViewer>().First();
            var viewport = Visuals(scroll).OfType<Controls.ScrollContentPresenter>().First();
            var tiles = Realized(list).ToArray();
            var first = tiles[0].TranslatePoint(new Wpf.Point(), viewport);
            var row = tiles.Where(tile => Math.Abs(tile.TranslatePoint(new Wpf.Point(), viewport).Y - first.Y) < 1).ToArray();
            double rightEdge = row[^1].TranslatePoint(new Wpf.Point(row[^1].ActualWidth + row[^1].Margin.Right, 0), viewport).X;
            check(locale + " default width derives from five complete tile footprints plus DPI-rounded list and window edges", Math.Abs(window.Width - StampLibraryWindow.WidthForColumns(StampLibraryWindow.DefaultTileColumns, scale)) < 1e-7 &&
                row.Length == StampLibraryWindow.DefaultTileColumns && rightEdge <= viewport.ActualWidth + .1 && viewport.ActualWidth - rightEdge < 2 / scale);
            scroll.ScrollToEnd(); background.UpdateLayout();
            tiles = Realized(list).ToArray();
            var lastBounds = new Wpf.Rect(tiles[^1].TranslatePoint(new Wpf.Point(), viewport), tiles[^1].RenderSize);
            check(locale + " one continuous scroll reaches the final entry without a page command", list.Items.Count == 51 && scroll.ScrollableHeight > 0 &&
                lastBounds.Bottom <= viewport.ActualHeight + 1 && lastBounds.Bottom > 0);
            scroll.ScrollToHome(); background.UpdateLayout();
            double listTop = list.TranslatePoint(new Wpf.Point(), background).Y;
            Invoke(window, "SelectSlot", 5); background.UpdateLayout();
            check(locale + " slot selection changes only its border and reserves no instruction or footer row", Math.Abs(list.TranslatePoint(new Wpf.Point(), background).Y - listTop) < .1 &&
                listTop < 120 && Field<Controls.TextBlock>(window, "status").Visibility == Wpf.Visibility.Collapsed &&
                Math.Abs(list.TranslatePoint(new Wpf.Point(0, list.ActualHeight), background).Y - (contentHeight - 16)) < 1);
            Invoke(window, "SelectSlot", 5);

            Layout(window.MinWidth - chromeEdges);
            var actions = Field<Controls.WrapPanel>(window, "actions"); var search = Field<Controls.TextBox>(window, "search");
            var commandRow = Field<Controls.DockPanel>(window, "commandRow");
            var searchBox = (Controls.Grid)Wpf.LogicalTreeHelper.GetParent(search);
            Wpf.Rect Bounds(Wpf.FrameworkElement element) => new(element.TranslatePoint(new Wpf.Point(), background), element.RenderSize);
            var actionBounds = Bounds(actions); var searchBounds = Bounds(search); var rowBounds = Bounds(commandRow);
            check(locale + " actions and search share one compact row without clipping at minimum window width", actions.Parent == commandRow && searchBox.Parent == commandRow &&
                Math.Abs(actionBounds.Top + actionBounds.Height / 2 - searchBounds.Top - searchBounds.Height / 2) < 2 &&
                actionBounds.Right <= searchBounds.Left && searchBounds.Right <= background.Width && searchBounds.Width >= 200 && rowBounds.Height <= 36);
            check(locale + " search embeds a noninteractive magnifier and preserves its accessible text-box name", search.Padding.Left >= 24 &&
                searchBox.Children.OfType<System.Windows.Shapes.Path>().SingleOrDefault() is { IsHitTestVisible: false } && Wpf.Automation.AutomationProperties.GetName(search) == L.Get("Stamp.Search"));
            var firstCharacter = search.GetRectFromCharacterIndex(0);
            check(locale + " compact search text fits fully inside its content box", !firstCharacter.IsEmpty && firstCharacter.Left >= 24 &&
                firstCharacter.Top >= 0 && firstCharacter.Bottom <= search.ActualHeight && firstCharacter.Height >= 14);
            check(locale + " all ten compact slots fit on one row at the minimum supported width", Field<Controls.WrapPanel>(window, "slotShelf").Children.OfType<Controls.Border>()
                .All(frame => frame.ActualWidth == 46 && frame.ActualHeight == 46 && Math.Abs(Bounds(frame).Top - Bounds(Field<Controls.WrapPanel>(window, "slotShelf")).Top - 2) < 1 && Bounds(frame).Right <= background.Width));
        }
        finally { background.Child = null; window.Content = content; }
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
            ShowFixture(preview);
            var previewSearch = Field<Controls.TextBox>(preview, "search"); previewSearch.Text = Field<Controls.TextBox>(window, "search").Text;
            preview.Refresh(); Pump();
            var previewList = Field<Controls.ListBox>(preview, "grid");
            previewList.SelectedItem = previewList.Items.OfType<StampEntry>().FirstOrDefault(item => item.Id == Field<Guid?>(window, "selected"));
            Invoke(preview, "SelectSlot", 5);
            PumpUntil(() => Realized(previewList).Any() && Realized(previewList).All(item => Thumbnail(item).Source is not null));
            var content = (Wpf.FrameworkElement)preview.Content; preview.Content = null;
            double scale = Wpf.Media.VisualTreeHelper.GetDpi(preview).DpiScaleX, chromeEdges = 2 * Math.Round(scale) / scale;
            double width = preview.Width - chromeEdges, height = preview.Height - AppWindowChrome.CaptionHeight - chromeEdges;
            var background = new Controls.Border { Child = content, Width = width, Height = height, UseLayoutRounding = true };
            background.SetResourceReference(Controls.Border.BackgroundProperty, "CanvasSurface");
            background.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "CanvasText");
            try
            {
                Pump(); background.Measure(new Wpf.Size(width, height)); background.Arrange(new Wpf.Rect(0, 0, width, height)); background.UpdateLayout(); Pump();
                var bitmap = new Wpf.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(width * 1.5), (int)Math.Ceiling(height * 1.5), 144, 144, Wpf.Media.PixelFormats.Pbgra32); bitmap.Render(background);
                var encoder = new Wpf.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(Wpf.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"stamp-library-{locale}-{theme}.png")); encoder.Save(file);
                object Bounds(Wpf.FrameworkElement element)
                {
                    var origin = element.TranslatePoint(new Wpf.Point(), background);
                    return new { X = origin.X, Y = origin.Y, Width = element.ActualWidth, Height = element.ActualHeight,
                        CenterX = origin.X + element.ActualWidth / 2, CenterY = origin.Y + element.ActualHeight / 2 };
                }
                File.WriteAllText(Path.Combine(output, $"stamp-library-bounds-{locale}-{theme}.json"), JsonSerializer.Serialize(new
                {
                    CoordinateSpace = "Detached production content in DIPs, excluding the window's 1-DIP border and 36-DIP caption.",
                    ContentWidth = width, ContentHeight = height, WindowWidth = preview.Width, TileColumns = StampLibraryWindow.DefaultTileColumns, TileFootprint = StampLibraryWindow.TileFootprint,
                    Search = Bounds(previewSearch), List = Bounds(previewList),
                    Slots = Field<Dictionary<int, Controls.Button>>(preview, "slotButtons").Select(pair => new { Digit = (pair.Key + 1) % 10, Bounds = Bounds(pair.Value) }),
                    SlotClearButtons = Field<Dictionary<int, Controls.Button>>(preview, "slotClearButtons").Where(pair => pair.Value.Visibility == Wpf.Visibility.Visible).Select(pair => new { Digit = (pair.Key + 1) % 10, Bounds = Bounds(pair.Value) }),
                    Tiles = Realized(previewList).Take(4).Select(item => new { Id = (Guid)item.Tag, Name = Wpf.Automation.AutomationProperties.GetName(item), Bounds = Bounds(item) })
                }, new JsonSerializerOptions { WriteIndented = true }));
                double narrowWidth = preview.MinWidth - chromeEdges;
                background.Width = narrowWidth; background.Measure(new Wpf.Size(narrowWidth, height)); background.Arrange(new Wpf.Rect(0, 0, narrowWidth, height)); background.UpdateLayout(); Pump();
                var narrow = new Wpf.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(narrowWidth * 1.5), (int)Math.Ceiling(height * 1.5), 144, 144, Wpf.Media.PixelFormats.Pbgra32); narrow.Render(background);
                var narrowEncoder = new Wpf.Media.Imaging.PngBitmapEncoder(); narrowEncoder.Frames.Add(Wpf.Media.Imaging.BitmapFrame.Create(narrow));
                using var narrowFile = File.Create(Path.Combine(output, $"stamp-library-{locale}-{theme}-narrow.png")); narrowEncoder.Save(narrowFile);
            }
            finally { background.Child = null; preview.Content = content; preview.Close(); }
        }
    }
    private static IEnumerable<Wpf.DependencyObject> Visuals(Wpf.DependencyObject root)
    {
        for (int i = 0; i < Wpf.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Wpf.Media.VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Visuals(child)) yield return descendant;
        }
    }
    private static void Click(Controls.Button button) => button.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
    private static Controls.Image Thumbnail(Controls.ListBoxItem item) => ((Controls.Grid)((Controls.StackPanel)item.Content).Children[0]).Children.OfType<Controls.Image>().Single();
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
    private static void ShowFixture(Wpf.Window window)
    {
        window.ShowActivated = false; window.ShowInTaskbar = false; window.Topmost = false; window.Opacity = 0;
        window.WindowStartupLocation = Wpf.WindowStartupLocation.Manual; window.Left = window.Top = -10000;
        if (new Wpf.Interop.WindowInteropHelper(window).Handle == 0)
            window.SourceInitialized += (_, _) =>
            {
                nint handle = new Wpf.Interop.WindowInteropHelper(window).Handle;
                SetWindowLongPtrW(handle, -20, GetWindowLongPtrW(handle, -20) | 0x08000000);
            };
        window.Show(); Pump();
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
        if (foreground == Environment.ProcessId) throw new InvalidOperationException("Stamp fixture unexpectedly owns foreground.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint handle, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint handle, int index, nint value);
}
