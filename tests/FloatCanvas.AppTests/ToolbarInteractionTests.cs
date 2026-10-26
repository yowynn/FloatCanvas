using System.Reflection;
using FloatCanvas.App;
using Controls = System.Windows.Controls;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using L = FloatCanvas.Localization.AppText;

internal static class ToolbarInteractionTests
{
    public static void Run(Action<string, bool> assert)
    {
        assert("first selection of a different tool leaves its parameters closed", !DesktopSession.ShouldOpenToolOptions(sameTool: false, wasOpen: false));
        assert("reselecting the active tool opens its closed parameter popup", DesktopSession.ShouldOpenToolOptions(sameTool: true, wasOpen: false));
        assert("reselecting the active tool closes its open parameter popup", !DesktopSession.ShouldOpenToolOptions(sameTool: true, wasOpen: true));
        assert("switching tools while parameters are open keeps the same popup open", DesktopSession.ShouldOpenToolOptions(sameTool: false, wasOpen: true));
        string previousLanguage = L.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            {
                L.Initialize(language);
                var launcher = new ToolbarLauncher();
                int menus = 0, visibilityChanges = 0, drags = 0;
                launcher.MenuRequested += () => menus++;
                launcher.ToggleVisibilityRequested += () => visibilityChanges++;
                launcher.DragRequested += () => drags++;
                var gestures = (LauncherGestureState)typeof(ToolbarLauncher).GetField("gestures", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(launcher)!;
                gestures.PointerDown(10, 10, 1000); gestures.PointerUp(10, 10, 1010);
                typeof(ToolbarLauncher).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(launcher, null);
                assert(language + " keyboard/automation launcher entry opens one menu and cancels pending mouse gestures", menus == 1 && visibilityChanges == 0 && drags == 0 && !gestures.HasPendingClick && !gestures.IsPointerDown);
                gestures.PointerDown(10, 10, 2000); gestures.PointerUp(10, 10, 2010);
                gestures.PointerDown(10, 10, 2100);
                var doubleClick = gestures.PointerUp(10, 10, 2110);
                typeof(ToolbarLauncher).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(launcher, [doubleClick]);
                assert(language + " a completed launcher double-click requests visibility only and leaves no delayed menu", visibilityChanges == 1 && menus == 1 && drags == 0 &&
                    gestures.AdvanceTime(10000) == LauncherGestureAction.None && !gestures.HasPendingClick && !gestures.IsPointerDown);
                assert(language + " editing launcher displays the application resource and delayed hint without taking focus", launcher.Content is Controls.Image image &&
                    ReferenceEquals(image.Source, AppIcon.ToolbarImage) && !launcher.Focusable && Controls.ToolTipService.GetInitialShowDelay(launcher) > 0 && !string.IsNullOrWhiteSpace(launcher.ToolTip as string) && Controls.ToolTipService.GetShowsToolTipOnKeyboardFocus(launcher)==false);
                foreach (string name in new[] { "Open", "Save", "SaveAs", "Copy", "Paste", "Undo", "Redo", "Duplicate", "Delete", "PassThrough" })
                    assert(language + " " + name + " menu header keeps keyboard gestures separate", !L.Get("ToolbarV2." + name).Contains("Ctrl", StringComparison.OrdinalIgnoreCase));
                launcher.CancelPendingGestures();
            }
        }
        finally { L.Initialize(previousLanguage); }
        LauncherArtworkLayout(assert);
    }

    private static void LauncherArtworkLayout(Action<string, bool> assert)
    {
        foreach (bool dark in new[] { false, true })
        {
            var foreground = dark ? Media.Brushes.WhiteSmoke : Media.Brushes.Black;
            var firstTool = new Controls.Border { Width = 28, Height = 26, HorizontalAlignment = Wpf.HorizontalAlignment.Left };
            var rows = new Controls.StackPanel();
            var tools = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal };
            tools.Children.Add(firstTool);
            foreach (string geometry in new[] { "M3,1 L3,15 L7,11 L10,16 L12,15 L9,10 L15,10 Z", "M2,13 L3,9 L11,1 L15,5 L7,13 Z", "M2,14 L14,2 M8,2 L14,2 L14,8", "M2,2 L14,2 L14,14 L2,14 Z" })
            {
                tools.Children.Add(new Controls.Border { Width = 30, Height = 26,
                    Child = new System.Windows.Shapes.Path { Data = Media.Geometry.Parse(geometry), Width = 16, Height = 16,
                        Stretch = Media.Stretch.Uniform, Stroke = foreground, StrokeThickness = 1.5,
                        StrokeStartLineCap = Media.PenLineCap.Round, StrokeEndLineCap = Media.PenLineCap.Round } });
            }
            rows.Children.Add(tools);
            rows.Children.Add(new Controls.Border {Height=2});
            rows.Children.Add(new Controls.TextBlock { Text = "浮绘  ·  FloatCanvas", Foreground = foreground, Height = 24, FontSize = 12, Margin = new Wpf.Thickness(0, 2, 0, 0) });
            var body = new Controls.Border { Child = rows, CornerRadius = new Wpf.CornerRadius(10), BorderThickness = new Wpf.Thickness(1),
                BorderBrush = dark ? Media.Brushes.DimGray : Media.Brushes.LightGray,
                Background = (Media.SolidColorBrush)new Media.BrushConverter().ConvertFromString(dark ? "#24272C" : "#F8F9FB")! };
            var launcher = new ToolbarLauncher();
            var surface = launcher.CreateSurface(body);
            Layout(surface);
            string theme = dark ? "dark" : "light";
            var artworkBounds = Bounds(launcher, surface);
            var bodyBounds = Bounds(body, surface);
            var firstToolOrigin = firstTool.TranslatePoint(new Wpf.Point(), surface);
            assert(theme + " both modes use the full 94 DIP artwork canvas",Near(artworkBounds.Width,94)&&Near(artworkBounds.Height,94));
            assert(theme + " v8 artwork keeps the 60 DIP toolbar and first tool position",
                Near(bodyBounds.Height,60)&&Near(firstToolOrigin.X,103)&&Near(bodyBounds.Y,32));
            assert(theme + " toolbar aligns with the crown/hair junction and lowest pillow pixels",
                Math.Abs(bodyBounds.Top-359d/1024*94)<1.5&&Math.Abs(bodyBounds.Bottom-1020d/1024*94)<2);
            assert(theme + " launcher stays above the bar and its entire artwork fits inside the measured window",
                Controls.Panel.GetZIndex(launcher) > Controls.Panel.GetZIndex(body) &&
                new Wpf.Rect(surface.RenderSize).Contains(artworkBounds) && !surface.ClipToBounds && !launcher.ClipToBounds);
            assert(theme + " unused space above toolbar tools remains transparent and does not intercept clicks",
                surface.Background is null && surface.InputHitTest(new Wpf.Point(surface.ActualWidth - 5, 5)) is null);

            foreach (double scale in new[] { 1d, 1.5, 2d })
            {
                var artwork = new Controls.Image { Source = AppIcon.ToolbarImage, Width = ToolbarLauncher.ArtworkSize, Height = ToolbarLauncher.ArtworkSize };
                Layout(artwork);
                var iconPixels = Raster(artwork, scale);
                bool topCovered = CornerCovered(iconPixels, bodyBounds, 10, top: true);
                bool bottomCovered = CornerCovered(iconPixels, bodyBounds, 10, top: false);
                assert($"{theme} {scale * 100:0}% DPI artwork masks both left toolbar corners",topCovered&&bottomCovered);
                int goldAboveToolbar = 0;
                for (int y = 0; y < (int)(bodyBounds.Top * scale); y++)
                    for (int x = 0; x < iconPixels.PixelWidth; x++)
                    {
                        var color = ColorAt(iconPixels, x, y);
                        if (color.A > 240 && color.R > 175 && color.G > 95 && color.B < 140) goldAboveToolbar++;
                    }
                assert($"{theme} {scale * 100:0}% DPI crown visibly extends above the toolbar", goldAboveToolbar > 20 * scale * scale);
                var rendered = Raster(surface, scale);
                assert($"{theme} {scale * 100:0}% DPI launcher has no rectangular hover or selection background",
                    ColorAt(rendered, 0, 0).A == 0 &&
                    ColorAt(rendered, (int)((ToolbarLauncher.ArtworkSize - 1) * scale), 0).A == 0 &&
                    ColorAt(rendered, rendered.PixelWidth - 2, 2).A == 0 && launcher.FocusVisualStyle is null && launcher.Template.Triggers.Count == 0);
                if (scale == 1.5) SavePreview(rendered, $"launcher-{theme}-150.png");
            }

            body.Visibility = Wpf.Visibility.Collapsed;
            Layout(surface);
            assert(theme + " collapsed toolbar retains the same icon size and origin",
                Near(surface.ActualWidth,94)&&Near(surface.ActualHeight,94)&&Bounds(launcher,surface).TopLeft==new Wpf.Point());
            launcher.CancelPendingGestures();
        }
    }

    private static void Layout(Wpf.FrameworkElement element)
    {
        element.Measure(new Wpf.Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Wpf.Rect(element.DesiredSize)); element.UpdateLayout();
    }

    private static Wpf.Rect Bounds(Wpf.FrameworkElement element, Wpf.UIElement relativeTo) =>
        new(element.TranslatePoint(new Wpf.Point(), relativeTo), element.RenderSize);

    private static Imaging.RenderTargetBitmap Raster(Wpf.FrameworkElement element, double scale)
    {
        var bitmap = new Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, Media.PixelFormats.Pbgra32);
        bitmap.Render(element); return bitmap;
    }

    private static Media.Color ColorAt(Imaging.BitmapSource bitmap, int x, int y)
    {
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Wpf.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Media.Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static bool CornerCovered(Imaging.BitmapSource artwork, Wpf.Rect body, double radius, bool top)
    {
        double scale = artwork.DpiX / 96;
        for (int i = 0; i <= 20; i++)
        {
            double angle = (top ? Math.PI : Math.PI / 2) + i * Math.PI / 40;
            double x = body.Left + radius + Math.Cos(angle) * radius;
            double y = (top ? body.Top + radius : body.Bottom - radius) + Math.Sin(angle) * radius;
            if (ColorAt(artwork, (int)Math.Round(x * scale), (int)Math.Round(y * scale)).A < 245) return false;
        }
        return true;
    }

    private static void SavePreview(Imaging.BitmapSource bitmap, string fileName)
    {
        if (Environment.GetEnvironmentVariable("FLOATCANVAS_TOOLBAR_PREVIEW_DIR") is not { Length: > 0 } directory) return;
        System.IO.Directory.CreateDirectory(directory);
        using var stream = System.IO.File.Create(System.IO.Path.Combine(directory, fileName));
        var encoder = new Imaging.PngBitmapEncoder(); encoder.Frames.Add(Imaging.BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }

    private static bool Near(double first, double second) => Math.Abs(first - second) < .01;
}
