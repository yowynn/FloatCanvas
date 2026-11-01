using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using PathShape = System.Windows.Shapes.Path;

internal static class IconClippingRegressionTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        output ??= System.IO.Path.Combine(AppContext.BaseDirectory, "icon-clipping");
        Directory.CreateDirectory(output);
        var findings = new List<object>();
        string language = L.Preference;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(locale); AppTheme.Apply(theme);
                var legacyButton = new Button { Content = LayersWindow.IconPath(LayersWindow.Instance), Width = 30, MinHeight = 30, Margin = new(6, 0, 0, 0) };
                Layout(legacyButton, new(40, 34));
                var legacy = Compare(legacyButton, 144, System.IO.Path.Combine(output, $"legacy-resources-{locale}-{theme}"));
                findings.Add(new { Case = "Pre-navigation-fix resources configuration from sealed 0.4 source", Locale = locale, Theme = theme, legacy.Missing, legacy.ExpectedInk, legacy.ActualInk, legacyButton.Padding, legacyButton.MinWidth });
                Console.WriteLine($"ICON_LEGACY_REPRO missing={legacy.Missing}; historical configuration, not current production assertion");
                var session = new EditorSession(new SceneDocument { Elements = [new() { Kind = ElementKind.Rectangle, Width = 50, Height = 40, Name = "Long visible layer label with repeated content to verify current level ellipsis and reserved action controls" }] });
                var window = new LayersWindow(() => session) { Width = 300 }; window.PrepareForShow();
                var content = (FrameworkElement)window.Content;
                Layout(content, new(300, 480));
                Save(content, 144, System.IO.Path.Combine(output, $"layers-{locale}-{theme}-300.png"));
                var resources = Descendants<Button>(content).First(button => AutomationProperties.GetName(button) == L.Get("Prefab.Resources"));
                foreach (double dpi in new[] { 96d, 120, 144 })
                {
                    var result = Compare(resources, dpi, System.IO.Path.Combine(output, $"resources-{locale}-{theme}-{dpi}"));
                    findings.Add(new { Case = "Actual layer resources button", Locale = locale, Theme = theme, Dpi = dpi, result.Missing, result.ExpectedInk, result.ActualInk,
                        Width = resources.ActualWidth, Height = resources.ActualHeight, resources.Padding, resources.MinWidth, resources.MinHeight });
                    check($"{locale} {theme} {dpi}dpi layer resources glyph retains all strong reference pixels", result.Missing == 0 && result.ExpectedInk > 5);
                }
                var row = Descendants<LayerTreeRowPresenter>(content).First();
                var viewport = Descendants<ScrollContentPresenter>(content).Single(site => Descendants<LayerTreeRowPresenter>(site).Any());
                foreach (bool visible in new[] { true, false })
                foreach (bool locked in new[] { true, false })
                {
                    var element = session.Document.Elements.Single(); element.Visible = visible; element.Locked = locked; window.PrepareForShow(); Layout(content, new(300, 480));
                    foreach (var button in new[] { row.VisibleButton, row.LockedButton })
                    {
                        foreach (double dpi in new[] { 96d, 120, 144 })
                        {
                            var result = Compare(button, dpi, null);
                            check($"{locale} {theme} {dpi}dpi row visible={visible} locked={locked} action glyph retains its paint", result.Missing == 0 && result.ExpectedInk > 5);
                        }
                        foreach (var path in Descendants<PathShape>(button))
                        {
                            var bounds = path.TransformToAncestor(viewport).TransformBounds(VisualTreeHelper.GetDescendantBounds(path));
                            findings.Add(new { Case = "Layer action within list viewport", Locale = locale, Theme = theme, Visible = visible, Locked = locked, Paint = bounds, Viewport = viewport.RenderSize });
                            check($"{locale} {theme} row visible={visible} locked={locked} glyph remains inside list viewport", new Rect(viewport.RenderSize).Contains(bounds));
                        }
                    }
                }
                foreach (PanelActionIcon icon in Enum.GetValues<PanelActionIcon>())
                {
                    var button = ActionIconButton.Create(icon, icon.ToString(), () => { }); Layout(button, new(34, 34));
                    var result = Compare(button, 120, null);
                    findings.Add(new { Case = "ActionIconButton", Icon = icon, Locale = locale, Theme = theme, Dpi = 120, result.Missing, result.ExpectedInk, result.ActualInk });
                    check($"{locale} {theme} compact {icon} retains its complete painted glyph", result.Missing == 0 && result.ExpectedInk > 5);
                }
                SessionWindowCoordinator.Close(window);
            }
        }
        finally
        {
            File.WriteAllText(System.IO.Path.Combine(output, "findings.json"), JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true }));
            L.Initialize(language); AppTheme.Apply("light");
        }
    }

    private static (int Missing, int ExpectedInk, int ActualInk) Compare(Control button, double dpi, string? prefix)
    {
        button.Background = Brushes.White; button.BorderBrush = Brushes.White;
        foreach (var path in Descendants<PathShape>(button)) { path.Stroke = Brushes.Black; if (path.Fill is not null) path.Fill = Brushes.Black; }
        button.UpdateLayout();
        int width = (int)Math.Ceiling(button.ActualWidth * dpi / 96), height = (int)Math.Ceiling(button.ActualHeight * dpi / 96);
        var actualVisual = new DrawingVisual();
        using (var draw = actualVisual.RenderOpen())
        {
            var bounds = new Rect(new Point(), button.RenderSize);
            draw.DrawRectangle(Brushes.White, null, bounds);
            Vector offset = VisualTreeHelper.GetOffset(button);
            draw.DrawRectangle(new VisualBrush(button) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(new Point(offset.X, offset.Y), button.RenderSize),
                Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, bounds);
        }
        var actual = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32); actual.Render(actualVisual);
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            foreach (var path in Descendants<PathShape>(button))
            {
                GeneralTransform transform = path.TransformToAncestor(button);
                Point p0 = transform.Transform(new(0, 0)), px = transform.Transform(new(1, 0)), py = transform.Transform(new(0, 1));
                draw.PushTransform(new MatrixTransform(new(px.X - p0.X, px.Y - p0.Y, py.X - p0.X, py.Y - p0.Y, p0.X, p0.Y)));
                draw.DrawDrawing(VisualTreeHelper.GetDrawing(path) ?? throw new InvalidOperationException("Glyph produced no drawing.")); draw.Pop();
            }
        }
        var expected = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32); expected.Render(visual);
        byte[] a = new byte[width * height * 4], e = new byte[a.Length]; actual.CopyPixels(a, width * 4, 0); expected.CopyPixels(e, width * 4, 0);
        int missing = 0, expectedInk = 0, actualInk = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (e[i] < 180 && e[i + 3] > 200)
            {
                expectedInk++;
                int x = i / 4 % width, y = i / 4 / width;
                bool found = false;
                // WPF device snapping can move the same vector by one physical pixel.
                // The separate viewport-bounds assertion admits no missing action.
                for (int dy = -1; dy <= 1 && !found; dy++)
                for (int dx = -1; dx <= 1 && !found; dx++)
                    if (x + dx >= 0 && x + dx < width && y + dy >= 0 && y + dy < height)
                    { int p = ((y + dy) * width + x + dx) * 4; found = a[p] < 230 && a[p + 3] >= 80; }
                if (!found) missing++;
            }
            if (a[i] < 180 && a[i + 3] > 80) actualInk++;
        }
        if (prefix is not null) { Save(actual, prefix + "-actual.png"); Save(expected, prefix + "-expected.png"); }
        Console.WriteLine($"ICON_CLIP dpi={dpi} size={button.ActualWidth}x{button.ActualHeight} padding={button.Padding} missing={missing} expected={expectedInk} actual={actualInk}");
        return (missing, expectedInk, actualInk);
    }
    private static void Layout(FrameworkElement element, Size size) { element.Measure(size); element.Arrange(new Rect(new(), size)); element.UpdateLayout(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Save(FrameworkElement element, double dpi, string path)
    {
        var visual = new DrawingVisual(); var offset = VisualTreeHelper.GetOffset(element);
        using (var draw = visual.RenderOpen())
            draw.DrawRectangle(new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(new Point(offset.X, offset.Y), element.RenderSize), Stretch = Stretch.Fill }, null, new Rect(element.RenderSize));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi / 96), (int)Math.Ceiling(element.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual); Save(bitmap, path);
    }
    private static void Save(BitmapSource bitmap, string path)
    { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); }
}
