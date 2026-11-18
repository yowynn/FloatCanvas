using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class KeyboardFocusAppearanceTests
{
    internal static void Run(Action<string, bool> check)
    {
        ApplicationPaths.Configure(["--profile", Path.Combine(Path.GetTempPath(), "FloatCanvas-keyboard-appearance", Guid.NewGuid().ToString("N"))]);
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_KEYBOARD_APPEARANCE_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "keyboard-appearance");
        Directory.CreateDirectory(output);
        foreach (string language in new[] { "zh-CN", "en" })
        foreach (string theme in new[] { "light", "dark" })
        {
            L.Initialize(language); AppTheme.Apply(theme);
            var button = new Button { Content = L.Get("Canvas.CalibrationHistory") };
            var toggle = new CheckBox { Content = "Grid", IsChecked = true };
            var mode = new CanvasModeSwitch(); mode.Synchronize(GroupCanvasMode.Temporary, true);
            var segmented = new SegmentedChoice(); segmented.Add("A", 0, new Border()); segmented.Add("B", 1, new Border()); segmented.SelectedIndex = 0;
            var pin = new CanvasPinSelector();
            var number = new NumericValueField(0, 100) { Value = 50, Unit = "%" };
            var combo = new ComboBox { ItemsSource = new[] { "A", "B" }, SelectedIndex = 0 };
            var hotkey = new TextBox { Text = "Ctrl+~", IsReadOnly = true };
            var slider = new Slider { Value = 50, Maximum = 100 };
            var hue = new ColorHueSlider();
            var opacity = new ToolbarOpacitySlider();
            var tile = new SelectionTileButton { Content = "Preset", Height = 34 }; tile.SetSelected(true);
            var body = new StackPanel { Margin = new(12), Children = { button, toggle, mode, segmented, pin, number, combo, hotkey, slider, hue, opacity, tile } };
            var root = new Border { Child = new AdornerDecorator { Child = body } };
            root.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
            var window = new Window { Content = root, Width = 420, Height = 640, ShowInTaskbar = false };
            var controls = new Control[] { button, toggle, mode, segmented.Items[0],
                ((Grid)pin.Children[0]).Children.OfType<ToggleButton>().First(), number.Slider, combo, hotkey, slider, hue, opacity, tile };
            try
            {
                window.Show(); window.Activate(); Pump();
                foreach (double scale in new[] { 1d, 1.5, 2d })
                foreach (var control in controls)
                {
                    Keyboard.Focus(button); Pump();
                    var before = Capture(root, scale);
                    control.Focus(); Pump();
                    var after = Capture(root, scale);
                    string name = $"{language}/{theme}/{scale * 100:0}% {control.GetType().Name}";
                    check(name + " retains internal focus without a focus adorner", control.IsKeyboardFocused && control.FocusVisualStyle is null);
                    bool acceptsKeyboardInput = control is Slider or ComboBox or TextBox;
                    check(name + " shows focus only for keyboard input controls", Pixels(before).SequenceEqual(Pixels(after)) != acceptsKeyboardInput);
                    Keyboard.Focus(button); Pump();
                    check(name + " removes input focus styling on blur", Pixels(before).SequenceEqual(Pixels(Capture(root, scale))));
                    if (ReferenceEquals(control, combo))
                    {
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(after));
                        using var stream = File.Create(Path.Combine(output, $"{language}-{theme}-{scale * 100:0}.png")); encoder.Save(stream);
                    }
                }
            }
            finally { window.Close(); Pump(); }
        }
        L.Initialize("zh-CN"); AppTheme.Apply("light");
    }
    private static RenderTargetBitmap Capture(FrameworkElement root, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root); return bitmap;
    }
    private static byte[] Pixels(BitmapSource image)
    { var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels; }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
