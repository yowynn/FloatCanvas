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

/// <summary>Render production Canvas settings in hidden native presentation sources; no desktop input.</summary>
internal static class DepthOcclusionUiRuntimeTests
{
    private const int Width = 600, Height = 590;
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
                var session = new EditorSession(new() { Canvas = new() { Projection = new() { Enabled = true } } });
                var window = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
                window.Refresh(session.Document.Canvas);
                window.PrepareForShow();
                window.DepthPriorityOcclusionChanged += value =>
                {
                    DesktopSession.ApplyCanvasDepthPriorityOcclusion(session, window.DisplayedSettings, value, true,
                        change => session.Execute(L.Get("Canvas.Title"), document => change(document.Canvas)));
                    window.Refresh(session.Document.Canvas);
                    window.PrepareForShow();
                };
                var option = Field<CheckBox>(window, "depthPriorityOcclusion");
                var projection = Field<SegmentedChoice>(window, "projection");
                var gridLock = Field<CheckBox>(window, "gridSizeLock");
                var content = (FrameworkElement)window.Content; window.Content = null;
                var root = new Border { Width = Width, Height = Height, Child = content, Background = (Brush)app.Resources["CanvasSurface"] };
                root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, app.Resources["CanvasText"]);
                root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
                root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
                using (var source = new HwndSource(new HwndSourceParameters("FloatCanvas depth Canvas settings UI")
                { Width = Width, Height = Height, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080 }))
                {
                    source.RootVisual = root; Layout(root);
                    double optionY = option.TranslatePoint(new Point(), root).Y;
                    Console.WriteLine($"CANVAS_DEPTH_LAYOUT locale={locale} theme={theme} visible={option.Visibility} size={option.ActualWidth}x{option.ActualHeight} optionY={optionY} projectionY={projection.TranslatePoint(new Point(), root).Y} lockY={gridLock.TranslatePoint(new Point(), root).Y} row={((FrameworkElement)option.Parent).ActualWidth}");
                    check(locale + " " + theme + " production depth option is below perspective and above grid lock in native Canvas layout",
                        PresentationSource.FromVisual(option) is HwndSource && option.Visibility == Visibility.Visible &&
                        option.ActualHeight > 10 && option.ActualWidth > 20 && ((FrameworkElement)option.Parent).ActualWidth > 150 && optionY > projection.TranslatePoint(new Point(), root).Y &&
                        optionY < gridLock.TranslatePoint(new Point(), root).Y && optionY + option.ActualHeight < Height);
                    check(locale + " " + theme + " Canvas depth option has its localized accessible name",
                        System.Windows.Automation.AutomationProperties.GetName(option) == L.Get("Canvas.DepthPriorityOcclusion") &&
                        System.Windows.Automation.AutomationProperties.GetName(option) == L.Get("Canvas.DepthPriorityOcclusion"));
                    option.IsChecked = true; option.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(root);
                    check(locale + " " + theme + " routed native Canvas depth option commits one undo step", session.Document.Canvas.DepthPriorityOcclusion && session.UndoCount == 1);
                    Render(root, Path.Combine(output, "canvas-depth-" + locale + "-" + theme + ".png"));
                    session.Undo(); window.Refresh(session.Document.Canvas); window.PrepareForShow(); Layout(root);
                    check(locale + " " + theme + " native Canvas depth checkbox tracks undo", option.IsChecked == false);
                    session.Document.Canvas.Projection.Enabled = false; window.Refresh(session.Document.Canvas); window.PrepareForShow(); Layout(root);
                    check(locale + " " + theme + " flat Canvas settings remove the depth row from layout", option.Visibility == Visibility.Collapsed && option.DesiredSize.Height == 0);
                    Render(root, Path.Combine(output, "canvas-flat-" + locale + "-" + theme + ".png"));
                    root.Child = null; source.RootVisual = null;
                }
                window.Content = content; window.CloseForDispose();
            }
        }
        finally { L.Initialize("zh-CN"); app.Shutdown(); }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(Width, Height)); root.Arrange(new Rect(0, 0, Width, Height)); root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void Render(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
