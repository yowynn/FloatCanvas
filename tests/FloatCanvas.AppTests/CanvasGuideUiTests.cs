using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class CanvasGuideUiTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string language = L.Preference;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            {
                L.Initialize(locale); AppTheme.Apply("light");
                using var fixture = new Fixture();
                var window = fixture.Window;
                var settings = new CanvasSettings { Projection = new() { Enabled = true } };
                var expectedSettings = settings.Clone();
                window.Refresh(settings); window.PrepareForShow();
                Layout(window);
                check(locale + " canvas settings no longer exposes a finish-adjustment action",
                    !Descendants(window).OfType<Button>().Any(button => button.Content is string text &&
                        text is "完成画布调整" or "Finish adjustment" or "调整透视画布" or "Adjust perspective canvas"));
                var fourPoints = Field<Button>(window, "fourPoints");
                check(locale + " four-point placement is a named action beside four-line placement",
                    System.Windows.Automation.AutomationProperties.GetName(fourPoints) == L.Get("Canvas.FourPoints") && fourPoints.Content is StackPanel &&
                    fourPoints.Parent is Grid actions && fourPoints.ActualWidth > 100 && actions.ColumnDefinitions.Count == 2 && fourPoints.ActualWidth <= actions.ActualWidth / 2 + 1);
                int placements = 0; window.FourPointsRequested += () => placements++;
                fourPoints.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(locale + " primary anchor action requests exactly one placement", placements == 1);

                var opacity = Field<ToolbarOpacitySlider>(window, "guideOpacity");
                double requested = -1; int requests = 0;
                window.GuideOpacityChanged += value => { requested = value; requests++; };
                check(locale + " guide opacity shares the toolbar ribbon with a two-slot caption width", opacity.Value == 1 && opacity.Minimum == 0 && opacity.Maximum == 1 && opacity.Width == 62 && opacity.Height == 28);
                opacity.Value = .35;
                check(locale + " reference opacity publishes a normalized preview-only value", requests == 1 && requested == .35);
                window.RefreshGuideOpacity(.6);
                check(locale + " owner refresh updates caption opacity without emitting edits", opacity.Value == .6 && requests == 1);
                window.RefreshGuideOpacity(0);
                check(locale + " zero hides only reference guides", opacity.Value == 0 && requests == 1);
                settings.Projection.Enabled = false; expectedSettings.Projection.Enabled = false;
                window.Refresh(settings); window.PrepareForShow();
                check(locale + " flat grid keeps the caption opacity and leaves canvas values unchanged", opacity.Visibility == Visibility.Visible &&
                    JsonSerializer.Serialize(settings) == JsonSerializer.Serialize(expectedSettings));
                using var fresh = new Fixture(); fresh.Window.PrepareForShow();
                check(locale + " a fresh panel defaults to full guide opacity", Field<ToolbarOpacitySlider>(fresh.Window, "guideOpacity").Value == 1);
                check(locale + " guide UI fixtures never show a native window", !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0);
            }
        }
        finally { L.Initialize(language); AppTheme.Apply("light"); }
    }

    private static T Field<T>(object source, string name) => (T)(CanvasOptionsTestWindow.Unwrap(source)).GetType().GetField(name, Private)!.GetValue(CanvasOptionsTestWindow.Unwrap(source))!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Layout(Window window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new(680, 600)); content.Arrange(new(0, 0, 680, 600)); content.UpdateLayout();
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly CanvasOptionsTestWindow Window = new(CanvasPresetLibrary.OfficialOnly());
        public void Dispose() => Window.CloseForDispose();
    }
    private sealed class DetachedSource : PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }
}
