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
                check(locale + " four-point placement is a full-width named primary action",
                    fourPoints.Content is string name && name == L.Get("Canvas.FourPoints") &&
                    fourPoints.Parent is Grid actions && fourPoints.ActualWidth > 100 && fourPoints.ActualWidth >= actions.ActualWidth - 1);
                int placements = 0; window.FourPointsRequested += () => placements++;
                fourPoints.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(locale + " primary anchor action requests exactly one placement", placements == 1);

                var opacity = Field<NumericValueField>(window, "guideOpacity");
                var refresh = typeof(CanvasSettingsWindow).GetMethod("RefreshGuideOpacity", Private)!;
                double requested = -1; int requests = 0;
                typeof(CanvasSettingsWindow).GetEvent("GuideOpacityChanged")!.AddEventHandler(window,
                    new Action<double>(value => { requested = value; requests++; }));
                check(locale + " guide opacity starts at one hundred percent and includes zero", opacity.Value == 100 &&
                    opacity.Slider.Minimum == 0 && opacity.Slider.Maximum == 100 && opacity.Unit == "%");
                opacity.Slider.Value = 35;
                check(locale + " reference opacity slider publishes a normalized view-only value", requests == 1 && requested == .35);
                opacity.Input.Text = "101";
                opacity.Input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), 0, Key.Enter)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                check(locale + " invalid opacity restores the last accepted value without a semantic edit", opacity.Value == 35 && requests == 1);

                refresh.Invoke(window, [.6]);
                check(locale + " a hidden canvas panel defers reference opacity updates", opacity.Value == 35 && requests == 1);
                window.PrepareForShow();
                check(locale + " preparing the panel displays cached guide opacity without emitting changes", opacity.Value == 60 && requests == 1);
                refresh.Invoke(window, [0d]); window.PrepareForShow();
                check(locale + " zero opacity can hide only the reference grid", opacity.Value == 0 && requests == 1);
                opacity.Visibility = Visibility.Collapsed; refresh.Invoke(window, [.8]); window.PrepareForShow();
                check(locale + " hidden reference opacity rows remain untouched during panel preparation", opacity.Value == 0 && requests == 1);
                opacity.Visibility = Visibility.Visible; window.PrepareForShow();
                check(locale + " showing the reference row applies its deferred value once", opacity.Value == 80 && requests == 1);
                settings.Projection.Enabled = false; expectedSettings.Projection.Enabled = false;
                window.Refresh(settings); window.PrepareForShow();
                check(locale + " flat grid also exposes reference opacity and canvas values remain independent",
                    opacity.Visibility == Visibility.Visible && requested == .35 &&
                    JsonSerializer.Serialize(settings) == JsonSerializer.Serialize(expectedSettings));
                using var fresh = new Fixture(); fresh.Window.PrepareForShow();
                check(locale + " a fresh panel defaults to fully visible references", Field<NumericValueField>(fresh.Window, "guideOpacity").Value == 100);
                check(locale + " guide UI fixtures never show a native window", !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0);
            }
        }
        finally { L.Initialize(language); AppTheme.Apply("light"); }
    }

    private static T Field<T>(object source, string name) => (T)source.GetType().GetField(name, Private)!.GetValue(source)!;
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
        internal readonly CanvasSettingsWindow Window = new(CanvasPresetLibrary.OfficialOnly());
        public void Dispose() => Window.CloseForDispose();
    }
    private sealed class DetachedSource : PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }
}
