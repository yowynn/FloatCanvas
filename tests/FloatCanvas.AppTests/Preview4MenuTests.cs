using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Audits production menu factories and their rendered glyphs, without showing windows or invoking commands.</summary>
internal static class Preview4MenuTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, bool render = false)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        string language = L.Preference;
        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-menu-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var library = Task.Run(async () =>
        {
            var catalog = new StampLibrary(root); await catalog.LoadAsync();
            var source = new EditorSession(); source.Add(new() { Kind = ElementKind.Rectangle, Width = 30, Height = 20 });
            await catalog.SaveSelectionAsync(source, "Menu specimen"); return catalog;
        }).GetAwaiter().GetResult();
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(locale); AppTheme.Apply(theme);
                var all = new List<(string Label, FrameworkElement Icon)>();
                var session = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
                Set(session, "_settings", new UserSettings());
                var editor = new EditorController(new EditorSession());
                Set(session, "_editor", editor); Set(session, "_cropMenuItems", new List<MenuItem>());
                var main = (ContextMenu)Invoke(session, "CreateLauncherMenu")!;
                Audit("main", main);
                var mainItems = Items(main).ToArray();
                check($"{locale} {theme} drawing commands have distinct tool symbols", mainItems.Where(item => item.Tag is EditorTool).Select(item => Data((FrameworkElement)item.Icon)).Distinct().Count() == 15);
                check($"{locale} {theme} pass-through matches the toolbar check mark", Data((FrameworkElement)mainItems.Single(item => Equals(item.Header, L.Get("ToolbarV2.PassThrough"))).Icon) == Geometry.Parse(CommandGlyphs.PassThrough).ToString());

                string profile = Path.Combine(root, locale + "-" + theme);
                var tools = new ToolPresetLibrary(Path.Combine(profile, "tools.json")); tools.Save("Personal pen", editor.Parameters);
                var toolGallery = new ToolPresetGallery(editor, tools);
                Audit("tool recipe", toolGallery.UserTiles.Children.OfType<Button>().Single().ContextMenu);
                var canvases = new CanvasPresetLibrary(Path.Combine(profile, "canvases.json")); canvases.Save("Personal canvas", new());
                var canvasGallery = new CanvasPresetGallery(canvases);
                Audit("canvas recipe", canvasGallery.UserTiles.Children.OfType<Button>().Single().ContextMenu);
                var colors = new ColorLibrary(Path.Combine(profile, "colors.json")); colors.Save("Saved blue", 0xFF2383E2);
                var picker = new ColorPickerPanel(ColorRole.Foreground, colors);
                Audit("swatch", Field<PixelTilePanel>(picker, "pinned").Children.OfType<Button>().Single().ContextMenu);
                var layer = new CanvasElement { Kind = ElementKind.Rectangle, Width = 20, Height = 20 }; editor.Session.Add(layer);
                var layers = new LayersWindow(() => editor.Session);
                var canvasWindow = new CanvasSettingsWindow(canvases);
                var stamps = new StampLibraryWindow(library, () => editor.Session, _ => { }, useSource: _ => { });
                try
                {
                    Audit("layer", (ContextMenu)Invoke(layers, "CreateContextMenu", layer.Id)!);
                    canvasWindow.SetReferenceMonitors([new("fixture primary", new(0, 0, 100, 100), new(0, 0, 100, 100), 96, 96, true),
                        new("fixture secondary", new(100, 0, 100, 100), new(100, 0, 100, 100), 96, 96, false)], null);
                    Audit("reset display", Field<ContextMenu>(canvasWindow, "resetMenu"));
                    Guid stamp = library.Items.Single().Id;
                    Task.Run(() => library.PinAsync(0, null)).GetAwaiter().GetResult();
                    var stampMenu = (ContextMenu)Invoke(stamps, "TileContextMenu", stamp)!;
                    Audit("stamp", stampMenu);
                    check($"{locale} {theme} every nested stamp slot has an icon", Items(stampMenu).Count(item => item.Tag is int) == 10 && Items(stampMenu).Where(item => item.Tag is int).All(item => item.Icon is FrameworkElement));
                    Task.Run(() => library.PinAsync(0, stamp)).GetAwaiter().GetResult();
                    Audit("pinned stamp", (ContextMenu)Invoke(stamps, "TileContextMenu", stamp)!);
                    var shelf = new StampShelfControl(library, _ => { });
                    Audit("shelf", Field<StackPanel>(shelf, "slots").Children.OfType<Button>().First().ContextMenu);
                }
                finally { stamps.Close(); layers.Close(); canvasWindow.CloseForDispose(); }
                if (render)
                {
                    string path = Path.Combine(root, "menu-icons-" + locale + "-" + theme + ".png");
                    RenderAtlas(all, path, locale + " · " + theme); Console.WriteLine("MENU_RENDER " + path);
                }
                Console.WriteLine($"MENU_AUDIT {locale} {theme} total={all.Count}");

                void Audit(string name, ContextMenu menu)
                {
                    var items = Items(menu).ToArray();
                    bool valid = items.Length > 0 && items.All(item => item.Icon is FrameworkElement icon && !string.IsNullOrWhiteSpace(Data(icon)) &&
                        Data(icon) != Geometry.Parse("M2,2 L14,2 L14,14 L2,14 Z").ToString() && HasPixels(icon));
                    check($"{locale} {theme} {name}: all {items.Length} entries have visible non-placeholder icons", valid);
                    all.AddRange(items.Select(item => (name + " · " + item.Header, (FrameworkElement)item.Icon)));
                }
            }
            using var tray = CommandGlyphs.TrayItem("Check updates", CommandGlyphs.Updates, (_, _) => { });
            var image = (System.Drawing.Bitmap)tray.Image!;
            bool hasInk = false;
            for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) hasInk |= image.GetPixel(x, y).A > 0;
            check("tray vector icons decode into independent visible bitmaps", image.Width == 36 && image.Height == 36 && hasInk);
        }
        finally { L.Initialize(language); AppTheme.Apply("light"); SynchronizationContext.SetSynchronizationContext(context); }
    }

    internal static IEnumerable<MenuItem> Items(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        { yield return item; foreach (var child in Items(item)) yield return child; }
    }
    internal static string Data(FrameworkElement icon) => icon switch
    {
        System.Windows.Shapes.Path path => path.Data.ToString(),
        Viewbox viewbox when viewbox.Child is Canvas canvas => string.Join(" ", canvas.Children.OfType<System.Windows.Shapes.Path>().Select(path => path.Data.ToString())),
        _ => ""
    };
    private static bool HasPixels(FrameworkElement icon)
    {
        icon.Measure(new Size(16, 16)); icon.Arrange(new Rect(0, 0, 16, 16)); icon.UpdateLayout();
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen()) draw.DrawRectangle(new VisualBrush(icon), null, new Rect(0, 0, 16, 16));
        var bitmap = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[16 * 16 * 4]; bitmap.CopyPixels(pixels, 16 * 4, 0);
        return Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] > 0) > 5;
    }
    private static void RenderAtlas(IReadOnlyList<(string Label, FrameworkElement Icon)> items, string path, string title)
    {
        var visual = new DrawingVisual(); int height = 60 + (int)Math.Ceiling(items.Count / 3d) * 34;
        var text = (Brush)Application.Current.FindResource("CanvasText");
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle((Brush)Application.Current.FindResource("CanvasSurface"), null, new Rect(0, 0, 1260, height));
            void Label(string label, Point point, double size, double maxWidth)
            {
                var formatted = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI, Microsoft YaHei UI"), size, text, 1)
                    { MaxTextWidth = maxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                draw.DrawText(formatted, point);
            }
            Label("Production menu icons · " + title, new Point(16, 12), 20, 1200);
            for (int i = 0; i < items.Count; i++)
            {
                double x = 16 + i % 3 * 416, y = 54 + i / 3 * 34;
                draw.DrawRectangle(new VisualBrush(items[i].Icon), null, new Rect(x, y, 24, 24));
                Label(items[i].Label, new Point(x + 34, y + 2), 13, 372);
            }
        }
        var bitmap = new RenderTargetBitmap(1260, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private)!.SetValue(target, value);
    private static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
}
