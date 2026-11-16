using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FloatCanvas.App;

internal static class ToolbarIconCenteringTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var session = RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        var create = typeof(DesktopSession).GetMethod("ToolbarIcon", BindingFlags.Instance | BindingFlags.NonPublic)!;
        nint previous = SetThreadDpiAwarenessContext(-4);
        try
        {
            foreach (double scale in new[] { 1d, 1.25, 1.5, 1.75, 2d })
            {
                var root = new StackPanel { Orientation = Orientation.Horizontal, UseLayoutRounding = true, SnapsToDevicePixels = true };
                var samples = new List<(string Name, FrameworkElement Icon, FrameworkElement Frame)>();
                foreach (bool dynamic in new[] { false, true })
                foreach (bool hints in new[] { false, true })
                {
                    var button = (Button)create.Invoke(session, ["probe", CommandGlyphs.CanvasFlat, (Action)(() => { }), ""])!;
                    if (dynamic) button.Content = ToolParameterIcon.Create(new ToolParameters { Tool = EditorTool.Rectangle });
                    var icon = (FrameworkElement)button.Content;
                    if (hints) button.Content = ToolbarShortcutHint.CreateButtonContent(button, "R").Content;
                    root.Children.Add(button);
                    samples.Add(($"dynamic={dynamic} hints={hints}", icon, button));
                }
                var shelf = new StampShelfControl(new StampLibrary(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FloatCanvas-centering-" + Guid.NewGuid().ToString("N"))), _ => { });
                shelf.PrepareForShow(); root.Children.Add(shelf);
                var slot = (Grid)((Button)((StackPanel)shelf.Content).Children[0]).Content;
                // A thin symmetric edge at the bitmap boundary exposes Image's
                // drawing-time pixel snapping, even when its layout is centered.
                var pixels = new byte[160 * 156 * 4];
                for (int y = 0; y < 156; y++)
                for (int x = 0; x < 160; x++)
                    if (x < 4 || x >= 156 || y < 4 || y >= 152)
                    { int offset = (y * 160 + x) * 4; pixels[offset + 2] = 255; pixels[offset + 3] = 255; }
                slot.Children.OfType<Image>().Single().Source = System.Windows.Media.Imaging.BitmapSource.Create(160, 156, 96, 96, PixelFormats.Pbgra32, null, pixels, 640);
                samples.Add(("stamp preview", slot.Children.OfType<Image>().Single(), slot.Children.OfType<Border>().Single()));
                using var source = new HwndSource(new HwndSourceParameters("FloatCanvas icon centering test")
                    { Width = 1000, Height = 100, PositionX = -22000, PositionY = -22000, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000000 });
                source.RootVisual = root;
                VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
                root.Measure(new Size(1000, 100)); root.Arrange(new Rect(new Point(), root.DesiredSize)); root.UpdateLayout();
                if (scale == 1.5)
                {
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 144, 144, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    int stride = bitmap.PixelWidth * 4;
                    var rendered = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(rendered, stride, 0);
                    var image = slot.Children.OfType<Image>().Single();
                    var origin = image.TranslatePoint(new Point(), root);
                    int row = (int)((origin.Y + image.ActualHeight / 2) * scale), left = 0, right = 0;
                    for (int x = (int)Math.Floor(origin.X * scale); x < Math.Ceiling((origin.X + image.ActualWidth) * scale); x++)
                    {
                        int index = row * stride + x * 4;
                        int red = Math.Max(0, rendered[index + 2] - rendered[index + 1]);
                        if (x + .5 < (origin.X + image.ActualWidth / 2) * scale) left += red; else right += red;
                    }
                    Console.WriteLine($"STAMP EDGE red coverage: left={left}, right={right}");
                    check("150% stamp preview preserves both symmetric bitmap edges", left > 100 && right > 100 && Math.Abs(left - right) <= Math.Max(left, right) * .2);
                }
                foreach (var sample in samples)
                {
                    var center = sample.Icon.TranslatePoint(new Point(sample.Icon.ActualWidth / 2, sample.Icon.ActualHeight / 2), root);
                    var frameCenter = sample.Frame.TranslatePoint(new Point(sample.Frame.ActualWidth / 2, sample.Frame.ActualHeight / 2), root);
                    var delta = center - frameCenter;
                    Console.WriteLine($"CENTER {scale:P0} {sample.Name}: delta={delta.X:F6},{delta.Y:F6} DIP");
                    check($"{scale:P0} {sample.Name} uses requested native layout DPI", Math.Abs(VisualTreeHelper.GetDpi(sample.Icon).DpiScaleX - scale) < .001);
                    check($"{scale:P0} {sample.Name} is centered in its frame", Math.Abs(delta.X) < .001 && Math.Abs(delta.Y) < .001);
                }
            }
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
