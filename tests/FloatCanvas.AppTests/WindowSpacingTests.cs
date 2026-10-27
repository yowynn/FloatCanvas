using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class WindowSpacingTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string language = L.Preference;
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-spacing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(locale); AppTheme.Apply(theme);
                string prefix = locale + " " + theme;
                var session = new EditorSession(); session.Add(new() { Kind = ElementKind.Rectangle, Width = 160, Height = 100 });
                var controller = new EditorController(session) { Tool = EditorTool.Rectangle };
                var tools = new ToolOptionsPanel(controller, new ToolPresetLibrary(Path.Combine(folder, "tools.json")), new ColorLibrary(Path.Combine(folder, "colors.json")));
                tools.PrepareForShow();
                foreach (double height in new[] { 620d, 220 })
                {
                    Measure(tools, ToolOptionsPanel.PreferredWidth, height);
                    var scroll = (ScrollViewer)tools.Content;
                    var gallery = Field<ToolPresetGallery>(tools, "presets");
                    double right = gallery.TranslatePoint(new(gallery.ActualWidth, 0), tools).X;
                    double occupied = scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? WindowLayout.ScrollBarWidth : 0;
                    Edges(prefix + " tool viewport " + height, tools, scroll, check);
                    check(prefix + " tool sidebar has no phantom right gutter " + height,
                        Math.Abs(tools.ActualWidth - right - WindowLayout.ContentInset - occupied) < .1);
                    check(prefix + " tool scrollbar responds to available height " + height,
                        (scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible) == (height == 220));
                }
                tools.Width = 320; Measure(tools, 320, 500);
                Edges(prefix + " compact tool viewport", tools, (FrameworkElement)tools.Content, check);

                var inspector = new InspectorWindow(() => session);
                inspector.PrepareForShow();
                var inspectorRoot = (FrameworkElement)inspector.Content;
                Measure(inspectorRoot, inspector.Width - 2, 650);
                var inspectorScroll = Descendants<ScrollViewer>(inspectorRoot).First();
                Edges(prefix + " inspector viewport", inspectorRoot, inspectorScroll, check);
                var properties = Field<StackPanel>(inspector, "propertyPanel");
                check(prefix + " inspector content fills its inset viewport", Math.Abs(properties.ActualWidth - (inspectorScroll.ViewportWidth - 2 * PropertyChangeRegion.HighlightInset)) < .1);

                var canvas = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly()); canvas.Refresh(new(), false);
                canvas.PrepareForShow();
                var canvasRoot = (FrameworkElement)canvas.Content;
                canvas.Content = null;
                var canvasHost = new Border { Child = canvasRoot };
                Measure(canvasHost, canvas.Width - 2, 540);
                Edges(prefix + " canvas body", canvasHost, canvasRoot, check);
                var canvasGallery = Field<CanvasPresetGallery>(canvas, "presets");
                check(prefix + " canvas sidebar reaches its content edge", Math.Abs(canvasHost.ActualWidth - canvasGallery.TranslatePoint(new(canvasGallery.ActualWidth, 0), canvasHost).X - WindowLayout.ContentInset) < .1);
                canvasHost.Child = null; canvas.Content = canvasRoot; canvas.CloseForDispose();

                var settings = new AppSettingsWindow(); settings.AddSection("Settings", "Layout fixture");
                var settingsRoot = (FrameworkElement)settings.Content; Measure(settingsRoot, settings.Width - 2, 610);
                var pages = Field<Grid>(settings, "pages");
                var page = (FrameworkElement)pages.Children[0];
                Edges(prefix + " settings page viewport", pages, page, check);
                check(prefix + " settings save aligns to the right content edge", Math.Abs(settingsRoot.ActualWidth - settings.SaveButton.TranslatePoint(new(settings.SaveButton.ActualWidth, 0), settingsRoot).X - WindowLayout.ContentInset) < .1);

                var color = new ColorPickerPanel(ColorRole.Foreground, new ColorLibrary(Path.Combine(folder, "colors.json")));
                color.PrepareForShow();
                color.Measure(new(double.PositiveInfinity, 520));
                Measure(color, color.DesiredSize.Width, 500);
                Edges(prefix + " color viewport", color, (FrameworkElement)color.Content, check);
            }
        }
        finally { L.Initialize(language); Directory.Delete(folder, true); }
    }

    private static void Edges(string name, FrameworkElement host, FrameworkElement child, Action<string, bool> check)
    {
        double left = child.TranslatePoint(new(), host).X;
        double right = host.ActualWidth - child.TranslatePoint(new(child.ActualWidth, 0), host).X;
        if (child is ScrollViewer { Content: FrameworkElement content } scroll && Descendants<PropertyChangeRegion>(content).Any())
        {
            left = content.TranslatePoint(new(), host).X;
            right = host.ActualWidth - content.TranslatePoint(new(content.ActualWidth, 0), host).X
                - (scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? WindowLayout.ScrollBarWidth : 0);
        }
        check(name + $" has 12 DIP edges ({left:0.##}/{right:0.##})", Math.Abs(left - 12) < .1 && Math.Abs(right - 12) < .1);
    }
    private static void Measure(FrameworkElement element, double width, double height)
    { element.Measure(new(width, height)); element.Arrange(new(0, 0, width, height)); element.UpdateLayout(); }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
