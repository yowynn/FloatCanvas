using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

/// <summary>Render the production Layers content in hidden native presentation sources; no user desktop input.</summary>
internal static class DepthOcclusionUiRuntimeTests
{
    public static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run --depth-occlusion-ui-only in a fresh process.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_DEPTH_UI_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-depth-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            foreach (var locale in new[] { "zh-CN", "en" })
            foreach (var theme in new[] { "light", "dark" })
            {
                L.Initialize(locale); AppTheme.Apply(theme);
                var session = new EditorSession(new() { Canvas = new() { Projection = new() { Enabled = true } }, Elements =
                    [new() { Kind = ElementKind.Rectangle, Width = 100, Height = 80, IsBillboard = true, Name = locale == "zh-CN" ? "立牌矩形" : "Billboard rectangle" },
                     new() { Kind = ElementKind.Ellipse, Width = 100, Height = 80, Name = locale == "zh-CN" ? "贴面椭圆" : "Surface ellipse" }] });
                var window = new LayersWindow(() => session);
                var option = (CheckBox)typeof(LayersWindow).GetField("depthPriorityOcclusion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var content = (FrameworkElement)window.Content; window.Content = null;
                var root = new Border { Width = 320, Height = 520, Child = content, Background = (Brush)app.Resources["CanvasSurface"] };
                root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, app.Resources["CanvasText"]);
                root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
                root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
                using (var source = new HwndSource(new HwndSourceParameters("FloatCanvas depth Layers UI")
                { Width = 320, Height = 520, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080 }))
                {
                    source.RootVisual = root; Layout(root);
                    check(locale + " " + theme + " production depth option participates in native Layers layout", PresentationSource.FromVisual(option) is HwndSource &&
                        option.Visibility == Visibility.Visible && option.ActualHeight > 10 && option.ActualWidth > 150 && option.TranslatePoint(new Point(), root).Y < 100);
                    option.IsChecked = true; option.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(root);
                    check(locale + " " + theme + " routed native depth option commits one undo step", session.Document.Canvas.DepthPriorityOcclusion && session.UndoCount == 1);
                    Render(root, Path.Combine(output, "layers-depth-" + locale + "-" + theme + ".png"));
                    session.Undo(); window.Refresh(); Layout(root);
                    check(locale + " " + theme + " native checkbox tracks undo", option.IsChecked == false);
                    session.Document.Canvas.Projection.Enabled = false; window.Refresh(); Layout(root);
                    check(locale + " " + theme + " flat canvas removes the depth row from layout", option.Visibility == Visibility.Collapsed && option.DesiredSize.Height == 0);
                    root.Child = null; source.RootVisual = null;
                }
                window.Content = content; window.Close();
            }
        }
        finally { L.Initialize("zh-CN"); app.Shutdown(); }
    }

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(320, 520)); root.Arrange(new Rect(0, 0, 320, 520)); root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void Render(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap(320, 520, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
