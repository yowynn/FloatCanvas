using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class ThemeAcceptanceTests
{
    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        bool overlay = Environment.GetEnvironmentVariable("FLOATCANVAS_THEME_BACKEND") == "overlay";
        string initial = Environment.GetEnvironmentVariable("FLOATCANVAS_THEME_INITIAL") == "dark" ? "dark" : "light";
        string output = Path.Combine(Environment.GetEnvironmentVariable("FLOATCANVAS_THEME_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-ThemeRegression"), (overlay ? "overlay-" : "normal-") + initial); Directory.CreateDirectory(output);
        var results = new List<(string Name, bool Passed)>();
        void Observe(string name, bool passed) { results.Add((name, passed)); Console.WriteLine($"THEME_OBSERVE {name}: {passed}"); }
        HwndSource? owner = null; OverlayWindowScope? scope = null;
        try
        {
            if (overlay)
            {
                owner = new HwndSource(new HwndSourceParameters("FloatCanvas.ThemeAcceptance") { Width=10, Height=10, PositionX=-10000, PositionY=-10000, WindowStyle=unchecked((int)0x80000000) });
                scope = new OverlayWindowScope(owner.Handle, () => false);
            }
            AppTheme.Apply(initial);
            var menu = new OverlayContextMenu { Width = 260, Height = 90 };
            menu.Items.Add(new MenuItem { Header = "Readable menu", Height = 40, FontSize = 18 });
            var tile = new SelectionTileButton { Width = 38, Height = 38, Content = ToolParameterIcon.Create(new() { Tool = EditorTool.Select }, 24) };
            var tools = new[] { EditorTool.Select, EditorTool.Eraser, EditorTool.Blur, EditorTool.Pixelate };
            var icons = tools.ToDictionary(tool => tool, tool => (FrameworkElement)ToolParameterIcon.Create(new() { Tool = tool, Style = new() { Stroke = 0xFFFF0000, Fill = 0xFF00FF00 } }, 24));
            var previewMethod = typeof(CanvasPresetGallery).GetMethod("Preview", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            FrameworkElement CanvasIcon(bool perspective) => (FrameworkElement)previewMethod.Invoke(null, [new CanvasPresetParameters { ProjectionEnabled = perspective, GridVisible = true, GridColor = 0xFF2383E2 }])!;
            var flatCanvas = Render(CanvasIcon(false), 24, 24, Path.Combine(output, "canvas-flat-before.png"));
            var projectedCanvas = Render(CanvasIcon(true), 24, 24, Path.Combine(output, "canvas-perspective-before.png"));
            Observe("canvas preview distinguishes a perspective frame from a flat frame", !flatCanvas.Bytes.SequenceEqual(projectedCanvas.Bytes));
            var calibrated=(FrameworkElement)previewMethod.Invoke(null,[new CanvasPresetParameters { ProjectionEnabled=true, GridVisible=true, GridColor=0xFF2383E2,
                Columns=32,Rows=15,Calibration=CanvasCalibration.CreateInitial(new(0,0,1400,600),32,15) }])!;
            Observe("canvas preview keeps four-by-four cells independent of concrete perspective calibration",projectedCanvas.Bytes.SequenceEqual(Render(calibrated,24,24,Path.Combine(output,"canvas-calibration-independent.png")).Bytes));
            var warmCanvas = CanvasIcon(true);
            foreach (string mode in new[] { initial, initial == "light" ? "dark" : "light", initial })
            {
                AppTheme.Apply(mode); Pump();
                var input = ((SolidColorBrush)app.Resources["CanvasInput"]).Color;
                var text = ((SolidColorBrush)app.Resources["CanvasText"]).Color;
                var pixels = Render(menu, 260, 90, Path.Combine(output, "menu-" + mode + ".png"));
                Console.WriteLine($"THEME_MENU {mode} expected={input} property={(menu.Background as SolidColorBrush)?.Color} actual={pixels.At(240,70)} same={ReferenceEquals(menu.Background,app.Resources["CanvasInput"])} frozen={((SolidColorBrush)app.Resources["CanvasInput"]).IsFrozen} oldFrozen={menu.Background.IsFrozen}");
                Observe(mode + " existing menu background updates", Near(pixels.At(240, 70), input));
                Observe(mode + " existing menu header stays readable", pixels.Count(text) > 20);
                Render(tile, 38, 38, Path.Combine(output, "tile-" + mode + ".png"));
                Observe(mode + " existing preset tile uses the themed input surface", tile.Background is SolidColorBrush brush && Near(brush.Color, input));
                var canvasPixels = Render(warmCanvas, 24, 24, Path.Combine(output, "canvas-perspective-" + mode + ".png"));
                Observe(mode + " existing canvas checker follows the palette", Near(canvasPixels.At(14, 14), input));
                foreach (var (tool, icon) in icons)
                {
                    var ink = Render(icon, 24, 24, Path.Combine(output, "tool-" + tool + "-" + mode + ".png"));
                    Observe(mode + " existing " + tool + " preview ignores drawing colors", ink.Count(text) > 10 && ink.RedCount == 0);
                }
            }
            foreach (double dpi in new[] { 1d, 1.25, 1.5, 2, 3 })
            foreach (Orientation orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
            foreach (double value in new[] { 0d, 50000d, 100000d })
            {
                var bar = new ScrollBar { Orientation = orientation, Minimum = 0, Maximum = 100000, ViewportSize = 100, Value = value };
                bar.SetResourceReference(FrameworkElement.StyleProperty, typeof(ScrollBar));
                double width = orientation == Orientation.Vertical ? 12 : 220, height = orientation == Orientation.Vertical ? 220 : 12;
                Render(bar, width, height, Path.Combine(output, $"scroll-{orientation}-{value}-{dpi*100}.png"), dpi);
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                var origin = track.Thumb.TranslatePoint(new Point(), track);
                double length = orientation == Orientation.Vertical ? track.Thumb.ActualHeight : track.Thumb.ActualWidth;
                double position = orientation == Orientation.Vertical ? origin.Y : origin.X;
                double rail = orientation == Orientation.Vertical ? track.ActualHeight : track.ActualWidth;
                Point center = track.Thumb.TranslatePoint(new Point(track.Thumb.ActualWidth / 2, track.Thumb.ActualHeight / 2), track);
                Observe($"{dpi*100}% {orientation} long-content thumb remains entirely inside the rail at {value}", position >= -.01 && position + length <= rail + .01 && length >= 19.9);
                Observe($"{dpi*100}% {orientation} painted thumb center matches drag calculation at {value}", Math.Abs(track.ValueFromPoint(center) - value) < .01);
                DependencyObject? hit=VisualTreeHelper.HitTest(bar, track.Thumb.TranslatePoint(new Point(track.Thumb.ActualWidth/2,track.Thumb.ActualHeight/2),bar))?.VisualHit;
                while(hit is not null && !ReferenceEquals(hit,track.Thumb))hit=VisualTreeHelper.GetParent(hit);
                Observe($"{dpi*100}% {orientation} thumb's visible center is clickable at {value}", hit is not null);
            }
            var combo = new ComboBox { Width = 240, IsEditable = true };
            combo.SetResourceReference(FrameworkElement.StyleProperty, typeof(ComboBox));
            combo.Items.Add("Sample"); combo.Text = "Typed font";
            Render(combo, 240, 36, Path.Combine(output, "editable-combo.png"));
            var editor = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
            var arrow = (ToggleButton)combo.Template.FindName("DropDownToggle", combo);
            Observe("editable combo exposes its native text editor", editor.Visibility == Visibility.Visible && editor.Text == "Typed font");
            Observe("editable combo arrow only occupies the dropdown hit target", arrow.ActualWidth <= 30 && arrow.ActualWidth > 0);
            Observe("editable combo uses the expected popup host", overlay ? combo.Template.FindName("PART_Popup",combo) is OverlayPopup : combo.Template.FindName("PART_Popup",combo) is Popup);
            var capture=(FrameworkElement)ToolParameterIcon.Create(new ToolParameters { Tool=EditorTool.CaptureRegion, CaptureBorderWidth=5, CaptureBorderColor=0x803366FF },24);
            var capturePixels=Render(capture,24,24,Path.Combine(output,"capture-colored.png"));
            var borderPixel=capturePixels.At(3,12);
            Observe("capture preview preserves border alpha independently of its readable content cue", borderPixel.A is >50 and <=128 && borderPixel.B>borderPixel.R &&
                capturePixels.Bytes.Where((_,index)=>index%4==3).Max()>220 && capturePixels.RedCount==0);
        }
        finally { scope?.Dispose(); owner?.Dispose(); AppTheme.Apply("light"); }
        foreach (var result in results) check(result.Name, result.Passed);
    }

    private static Pixels Render(FrameworkElement element, double width, double height, string path, double scale=1)
    {
        // These detached visuals use the same palette ownership as a real
        // windowless host. A production Window supplies that ownership itself.
        AppTheme.UsePopupPalette(element);
        VisualTreeHelper.SetRootDpi(element,new DpiScale(scale,scale));element.InvalidateMeasure();element.InvalidateArrange();
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width*scale), (int)Math.Ceiling(height*scale), 96*scale, 96*scale, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(path)) encoder.Save(stream);
        byte[] bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return new(bytes, bitmap.PixelWidth);
    }
    private static bool Near(Color actual, Color expected) => actual.A > 220 && Math.Abs(actual.R - expected.R) < 16 && Math.Abs(actual.G - expected.G) < 16 && Math.Abs(actual.B - expected.B) < 16;
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    private sealed record Pixels(byte[] Bytes, int Width)
    {
        internal Color At(int x, int y) { int p = (y * Width + x) * 4; return Color.FromArgb(Bytes[p + 3], Bytes[p + 2], Bytes[p + 1], Bytes[p]); }
        internal int Count(Color expected) => Enumerable.Range(0, Bytes.Length / 4).Count(index => Near(At(index % Width, index / Width), expected));
        internal int RedCount => Enumerable.Range(0, Bytes.Length / 4).Count(index => At(index % Width, index / Width) is { A: > 220, R: > 150, G: < 90, B: < 90 });
    }
}
