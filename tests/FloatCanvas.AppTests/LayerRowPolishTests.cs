using FloatCanvas.App;
using FloatCanvas.Core;
using SkiaSharp;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using L = FloatCanvas.Localization.AppText;
using ShapePath = System.Windows.Shapes.Path;

internal static class LayerRowPolishTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string originalLanguage = L.Preference;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(locale); AppTheme.Apply(theme);
                Verify(check, locale + " " + theme, output, locale, theme);
            }
        }
        finally { L.Initialize(originalLanguage); AppTheme.Apply("light"); }
    }

    private static void Verify(Action<string, bool> check, string prefix, string? output, string locale, string theme)
    {
        var assets = new AssetLibrary();
        using var pixels = new SKBitmap(2, 2); pixels.Erase(SKColors.CadetBlue);
        using var picture = SKImage.FromBitmap(pixels); using var encoded = picture.Encode(SKEncodedImageFormat.Png, 100);
        string asset = assets.Add(encoded.ToArray(), "image/png");
        var elements = Enum.GetValues<ElementKind>().Select(kind => new CanvasElement
        {
            Kind = kind, Name = kind.ToString(), Width = 50, Height = 30,
            AssetId = kind == ElementKind.Image ? asset : null,
            Presentation = kind == ElementKind.Presentation ? new() : null,
            Points = kind is ElementKind.Freehand or ElementKind.Highlighter ? [new(0, 0), new(50, 30)] : []
        }).ToList();
        var windowImage = new CanvasElement { Kind = ElementKind.Image, Name = "Window source", Width = 50, Height = 30, AssetId = asset, WindowSource = new() { BindingIntent = Guid.NewGuid() } };
        elements.Add(windowImage);
        var group = new CanvasGroup { Name = "OOO", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var plain = new PrefabDefinition { Name = "Shared definition", Graph = new() { Elements = [Box()] } };
        var linked = new PrefabDefinition { Name = "Linked definition", Graph = new() { Elements = [Box()] },
            Source = new() { Locator = "synthetic.floatcanvas", IsRelative = true, SourceResourceId = Guid.NewGuid(), EntryKind = PrefabSourceKind.ProjectContent,
                LastAcceptedFileSha256 = new string('0', 64), LastAcceptedContentRevision = Guid.NewGuid() } };
        var first = new PrefabInstance { DefinitionId = plain.Id, Name = "Shared OOO" };
        var second = new PrefabInstance { DefinitionId = linked.Id, Name = "Linked OOO" };
        var session = new EditorSession(new() { Elements = elements, Groups = [group], Definitions = [plain, linked], Instances = [first, second],
            RootOrder = [.. elements.Select(element => element.Id), group.Id, first.Id, second.Id] }, assets);
        var window = new LayersWindow(() => session) { Height = 1000 };
        using var host = new NativeHost(window, 300, 1000);
        window.PrepareForShow(); host.Layout();
        check(prefix + " root path is named Main canvas", Crumbs(window).First().Content?.ToString() == L.Get("Layers.RootScope") &&
            L.Get("Layers.RootScope") == (locale == "zh-CN" ? "主画布" : "Main canvas"));
        check(prefix + " all element kinds have distinct static type artwork", Enum.GetValues<ElementKind>().Select(kind => LayerTypeIcon.Geometry(kind)).Distinct().Count() == Enum.GetValues<ElementKind>().Length);
        foreach (var element in elements)
        {
            var row = window.FindNode(element.Id)!;
            var presenter = Show(window, host, element.Id);
            var icon = (ContentControl)typeof(LayerTreeRowPresenter).GetField("kind", Hidden)!.GetValue(presenter)!;
            check(prefix + " " + element.Kind + (element.WindowSource is null ? "" : " window") + " row carries and paints its static type icon", row.Kind == element.Kind && icon.Content is ShapePath path &&
                path.Data.ToString() == System.Windows.Media.Geometry.Parse(LayerTypeIcon.Geometry(element.Kind, windowSource: element.WindowSource is not null)).ToString() && InkCount((FrameworkElement)icon.Content) > 5);
            var dots = Descendants<System.Windows.Shapes.Ellipse>(presenter.Handle).ToArray();
            var bounds = Rect.Empty; foreach (var dot in dots) bounds.Union(dot.TransformToAncestor(presenter).TransformBounds(new Rect(dot.RenderSize)));
            var labelBounds = presenter.Label.TransformToAncestor(presenter).TransformBounds(new Rect(presenter.Label.RenderSize));
            check(prefix + " " + element.Kind + " six-dot handle is vertically centered with its name", dots.Length == 6 && Math.Abs(bounds.Top + bounds.Height / 2 - labelBounds.Top - labelBounds.Height / 2) < .01);
        }
        check(prefix + " group and shared and linked artwork remain distinguishable", new[]
        {
            LayerTypeIcon.Geometry(null, group: true), LayerTypeIcon.Geometry(null, instance: true),
            LayerTypeIcon.Geometry(null, instance: true, linked: true), LayerTypeIcon.Geometry(ElementKind.Image, windowSource: true)
        }.Distinct().Count() == 4);

        session.SetSelection([group.Id]); window.PrepareForShow(); host.Layout();
        var groupRow = Show(window, host, group.Id);
        var ink = PaintedPoint(groupRow.Label); var hole = HolePoint(groupRow.Label);
        check(prefix + " name ink and the O glyph hole are distinguished", LayerNameInkHit.Contains(groupRow.Label, ink) && !LayerNameInkHit.Contains(groupRow.Label, hole));
        int undo = session.UndoCount;
        check(prefix + " double-click group text renames before entering", groupRow.ActivateDoubleClick(ink) && window.IsRenaming && session.EditScopeId is null);
        groupRow.Editor.Text = locale == "zh-CN" ? "可改名组合" : "Renamed group";
        Console.WriteLine($"ROW_RENAME_BEFORE text={groupRow.Editor.Text} draft={window.FindNode(group.Id)!.DraftName} original={typeof(LayersWindow).GetField("renameOriginal", Hidden)!.GetValue(window)} sameDoc={ReferenceEquals(session.Document, typeof(LayersWindow).GetField("renameDocument", Hidden)!.GetValue(window))} renaming={window.IsRenaming} undo={session.UndoCount}");
        window.HandleRenameKey(System.Windows.Input.Key.Enter); window.PrepareForShow(); host.Layout();
        Console.WriteLine($"ROW_RENAME_AFTER name={session.Document.Groups.Single().Name} renaming={window.IsRenaming} undo={session.UndoCount} expectedUndo={undo + 1} status={((TextBlock)typeof(LayersWindow).GetField("status", Hidden)!.GetValue(window)!).Text}");
        check(prefix + " group name commits with one Undo and can Undo", session.Document.Groups.Single().Name == (locale == "zh-CN" ? "可改名组合" : "Renamed group") && session.UndoCount == undo + 1 && session.Undo());
        window.PrepareForShow(); host.Layout(); groupRow = Show(window, host, group.Id); hole = HolePoint(groupRow.Label);
        check(prefix + " double-click a glyph hole performs ordinary group entry", groupRow.ActivateDoubleClick(hole) && session.EditScopeId == group.Id && !window.IsRenaming);
        ReturnRoot(window); host.Layout(); groupRow = Show(window, host, group.Id);
        var blank = new Point(groupRow.Label.ActualWidth - 2, groupRow.Label.ActualHeight / 2);
        check(prefix + " trailing label space is not a name pixel", !LayerNameInkHit.Contains(groupRow.Label, blank));
        check(prefix + " double-click blank space performs ordinary group entry", groupRow.ActivateDoubleClick(blank) && session.EditScopeId == group.Id);
        ReturnRoot(window); host.Layout(); groupRow = Show(window, host, group.Id);
        check(prefix + " double-click the icon-side area performs group entry", groupRow.ActivateDoubleClick(new(-12, groupRow.Label.ActualHeight / 2)) && session.EditScopeId == group.Id);
        ReturnRoot(window); host.Layout();

        int editRequests = 0; window.EditPrefabRequested += _ => editRequests++;
        session.SetSelection([first.Id]); window.PrepareForShow(); host.Layout();
        var instanceRow = Show(window, host, first.Id);
        check(prefix + " shared-instance text double-click starts rename without opening definition", instanceRow.ActivateDoubleClick(PaintedPoint(instanceRow.Label)) && window.IsRenaming && editRequests == 0);
        window.HandleRenameKey(System.Windows.Input.Key.Escape); window.PrepareForShow(); host.Layout(); instanceRow = Show(window, host, first.Id);
        check(prefix + " shared-instance icon double-click preserves definition editing", instanceRow.ActivateDoubleClick(new(-10, 8)) && editRequests == 1 && !window.IsRenaming);

        var sample = Show(window, host, elements[0].Id).Label;
        foreach (string text in new[] { "中文名字", "e\u0301cole", "مرحبا", "😀", "A very long name with a trimmed ending and more letters" })
        {
            sample.Text = text; host.Layout(); Render(sample);
            var point = PaintedPoint(sample);
            check(prefix + " rendered Unicode or ellipsis ink can be renamed: " + text, LayerNameInkHit.Contains(sample, point));
            if (text.StartsWith("A very", StringComparison.Ordinal))
                check(prefix + " the displayed trimmed ending is also name ink", LayerNameInkHit.Contains(sample, PaintedPoint(sample, fromRight: true)));
        }
        if (output is not null)
        {
            Directory.CreateDirectory(output); window.PrepareForShow(); host.Layout();
            Save(host.Root, Path.Combine(output, "layer-type-icons-" + locale + "-" + theme + "-300.png"));
        }
        check(prefix + " static labels and icons never start native capture or show their host", !window.IsVisible && !IsWindowVisible(host.Handle));
        SessionWindowCoordinator.Close(window);
    }

    internal static Point PaintedPoint(TextBlock label, bool fromRight = false)
    {
        var bitmap = Render(label); int stride = bitmap.PixelWidth * 4; var bytes = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(bytes, stride, 0);
        var dpi = VisualTreeHelper.GetDpi(label);
        for (int column = 0; column < bitmap.PixelWidth; column++) for (int y = 0; y < bitmap.PixelHeight; y++)
        {
            int x = fromRight ? bitmap.PixelWidth - column - 1 : column;
            if (bytes[y * stride + x * 4 + 3] > 220) return new((x + .5) / dpi.DpiScaleX, (y + .5) / dpi.DpiScaleY);
        }
        throw new InvalidOperationException("Expected actual name ink.");
    }
    private static Point HolePoint(TextBlock label)
    {
        var bitmap = Render(label); int width = bitmap.PixelWidth, height = bitmap.PixelHeight, stride = width * 4; var bytes = new byte[stride * height]; bitmap.CopyPixels(bytes, stride, 0);
        bool Ink(int x, int y) => bytes[y * stride + x * 4 + 3] > 100;
        bool Ray(int x, int y, int dx, int dy) { for (int n = 1; n < 16; n++) { int xx = x + dx * n, yy = y + dy * n; if (xx < 0 || yy < 0 || xx >= width || yy >= height) return false; if (Ink(xx, yy)) return true; } return false; }
        var dpi = VisualTreeHelper.GetDpi(label);
        for (int y = 1; y < height - 1; y++) for (int x = 1; x < width - 1; x++)
            if (!Ink(x, y) && bytes[y * stride + x * 4 + 3] == 0 && Ray(x, y, -1, 0) && Ray(x, y, 1, 0) && Ray(x, y, 0, -1) && Ray(x, y, 0, 1))
                return new((x + .5) / dpi.DpiScaleX, (y + .5) / dpi.DpiScaleY);
        throw new InvalidOperationException("Expected a real O glyph hole.");
    }
    private static RenderTargetBitmap Render(FrameworkElement element)
    {
        element.Measure(element.RenderSize); element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element); var offset = VisualTreeHelper.GetOffset(element); var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen()) draw.DrawRectangle(new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(offset.X, offset.Y, element.ActualWidth, element.ActualHeight), Stretch = Stretch.Fill }, null, new Rect(element.RenderSize));
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); image.Render(visual); return image;
    }
    private static int InkCount(FrameworkElement element) { element.Measure(new Size(16, 16)); element.Arrange(new Rect(0, 0, 16, 16)); element.UpdateLayout(); var image = Render(element); var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return Enumerable.Range(0, image.PixelWidth * image.PixelHeight).Count(i => bytes[i * 4 + 3] > 80); }
    private static void Save(FrameworkElement root, string path) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Render(root))); using var stream = File.Create(path); encoder.Save(stream); }
    private static CanvasElement Box() => new() { Kind = ElementKind.Rectangle, Width = 20, Height = 20 };
    private static IEnumerable<Button> Crumbs(LayersWindow window) => ((StackPanel)typeof(LayersWindow).GetField("breadcrumbs", Hidden)!.GetValue(window)!).Children.OfType<Button>();
    private static void ReturnRoot(LayersWindow window) { Crumbs(window).First().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.PrepareForShow(); }
    private static LayerTreeRowPresenter Show(LayersWindow window, NativeHost host, Guid id) { var list = (ListBox)typeof(LayersWindow).GetField("layers", Hidden)!.GetValue(window)!; list.ScrollIntoView(window.FindNode(id)); host.Layout(); return LayerTreeRowPresenter.Find(window.Container(id)) ?? throw new InvalidOperationException("Expected real current row."); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var item in Descendants<T>(child)) yield return item; } }
    private sealed class NativeHost : IDisposable
    {
        private readonly LayersWindow window; private readonly FrameworkElement content; private readonly HwndSource source; private readonly int width, height;
        internal Border Root { get; } internal nint Handle => source.Handle;
        internal NativeHost(LayersWindow window, int width, int height)
        {
            this.window = window; this.width = width; this.height = height; content = (FrameworkElement)window.Content; window.Content = null;
            Root = new Border { Width = width, Height = height, Child = content }; Root.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
            source = new(new HwndSourceParameters("FloatCanvas layer row polish") { Width = width, Height = height, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080 }); source.RootVisual = Root;
            var dpi = VisualTreeHelper.GetDpi(Root); SetWindowPos(source.Handle, 0, 0, 0, (int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY), 0x16); Layout();
        }
        internal void Layout() { Root.Measure(new Size(width, height)); Root.Arrange(new Rect(0, 0, width, height)); Root.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(Root); }
        public void Dispose() { Root.Child = null; source.RootVisual = null; source.Dispose(); window.Content = content; }
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
