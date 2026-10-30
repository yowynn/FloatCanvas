using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;

/// <summary>Detached toolbar pixels plus opt-in pointer routing confined to an isolated desktop.</summary>
internal static class ToolbarOpacitySliderTests
{
    internal static void RunPointer(Action<string, bool> check)
    {
        var desktopName = new StringBuilder(256);
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, desktopName, desktopName.Capacity * 2, out _) ||
            !desktopName.ToString().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Run opacity pointer checks with --isolated-ui-desktop.");
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var slider = new ToolbarOpacitySlider { Value = .5 };
        var surface = Surface(slider); surface.Cursor = Cursors.SizeWE;
        var window = new Window { Width = 150, Height = 110, Content = surface, ShowInTaskbar = false, Left = 40, Top = 40 };
        var results = new List<(string Name, bool Passed)>();
        void Record(string name, bool passed)
        {
            Console.WriteLine($"OPACITY_POINTER {name}: {passed}; value={slider.Value:0.####}"); results.Add((name, passed));
        }
        try
        {
            window.Show(); window.Activate(); Pump();
            var track = (Track)slider.Template.FindName("PART_Track", slider);
            var slot = track.IncreaseRepeatButton;
            var press = new Point(slot.ActualWidth * .6, slot.ActualHeight / 2);
            Point atSlider = slot.TranslatePoint(press, slider);
            var thumb = track.Thumb;
            double travel = track.ActualWidth - thumb.ActualWidth;
            double start = track.TranslatePoint(new(thumb.ActualWidth / 2, 0), slider).X;
            double expected = Math.Clamp((atSlider.X - start) / travel, 0, 1);
            bool downHandled = Pointer(slot, press, Mouse.PreviewMouseDownEvent);
            Record("the actual preview press suppresses native repeat-button input", downHandled);
            Record("the actual slot press routes a direct jump instead of a repeat-button step", Math.Abs(slider.Value - expected) < .02);
            Pointer(slider, atSlider, Mouse.PreviewMouseUpEvent); Pump();

            // A non-input desktop cannot grant Win32 capture. Inject success at
            // the production capture boundary; subsequent input is routed by WPF.
            slider.Value = .5; window.UpdateLayout();
            slider.StartPointerGesture(atSlider, () => true);
            Record("slot press jumps directly to the clicked opacity", Math.Abs(slider.Value - expected) < .02);
            double held = slider.Value;
            Pump(TimeSpan.FromMilliseconds(slot.Delay + slot.Interval * 2 + 30));
            Record("holding a slot press does not repeatedly step opacity", Math.Abs(slider.Value - held) < 1e-10);
            Point moved = new(start + travel * .9, atSlider.Y);
            Pointer(slider, moved, Mouse.PreviewMouseMoveEvent);
            Record("the same held slot press can drag to another opacity", Math.Abs(slider.Value - .9) < .02);
            Pointer(slider, moved, Mouse.PreviewMouseUpEvent); Pump();

            slider.Value = .5; window.UpdateLayout();
            var edge = new Point(thumb.ActualWidth - .5, thumb.ActualHeight / 2);
            Point thumbPress = PointerPosition(slider, thumb.TranslatePoint(edge, slider));
            slider.StartPointerGesture(thumbPress, () => true);
            Record("pressing an off-center point of the thumb preserves opacity", Math.Abs(slider.Value - .5) < 1e-10);
            Pointer(slider, new(thumbPress.X + travel * .2, thumbPress.Y), Mouse.PreviewMouseMoveEvent);
            Record("the same held thumb press drags relative to its initial opacity", Math.Abs(slider.Value - .7) < .02);
            Pointer(slider, new(thumbPress.X + travel * .2, thumbPress.Y), Mouse.PreviewMouseUpEvent); Pump();
            Record("opacity and its thumb keep the arrow even under a horizontal-drag cursor parent", QueryCursor(slider) == Cursors.Arrow && QueryCursor(thumb) == Cursors.Arrow);

            slider.Value = .5; window.UpdateLayout();
            thumbPress = PointerPosition(slider, thumb.TranslatePoint(edge, slider));
            var changes = new List<double>();
            slider.ValueChanged += (_, e) => changes.Add(e.NewValue);
            slider.StartPointerGesture(thumbPress, () => { Pointer(slider, thumbPress, Mouse.PreviewMouseMoveEvent); return true; });
            Pointer(slider, thumbPress, Mouse.PreviewMouseUpEvent);
            Record("capture-time stationary movement emits no transient thumb changes", changes.Count == 0 && slider.Value == .5);

            slider.Value = .5; window.UpdateLayout(); changes.Clear();
            slider.StartPointerGesture(atSlider, () => { Pointer(slider, moved, Mouse.PreviewMouseMoveEvent); return true; });
            Record("capture-time slot movement retains its latest position", Math.Abs(slider.Value - .9) < .02 && changes.Count == 2 && Math.Abs(changes[^1] - .9) < .02);
            Pointer(slider, moved, Mouse.PreviewMouseUpEvent);

            bool ThumbInk(string resource)
            {
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                return thumb.Template.FindName("Handle", thumb) is Border { BorderBrush: SolidColorBrush actual } &&
                    Application.Current.FindResource(resource) is SolidColorBrush expectedInk && actual.Color == expectedInk.Color && actual.Opacity == expectedInk.Opacity;
            }
            slider.Value = 1; window.UpdateLayout();
            var emptySlot = PointerPosition(slider, new(start - 1, slider.ActualHeight / 2));
            Record("an empty rail position does not highlight a distant thumb", ThumbInk("CanvasText"));
            var maximumThumb = PointerPosition(slider, thumb.TranslatePoint(new(thumb.ActualWidth / 2, thumb.ActualHeight / 2), slider));
            Record("hovering the thumb itself highlights it without pressing", ThumbInk("CanvasAccent"));
            Pointer(slider, emptySlot, Mouse.PreviewMouseMoveEvent);
            slider.StartPointerGesture(emptySlot, () => true);
            Record("pressing the empty minimum slot jumps the thumb to zero and highlights it immediately", slider.Value == 0 && ThumbInk("CanvasAccent"));
            Pointer(slider, emptySlot, Mouse.PreviewMouseUpEvent);
            Record("release on the newly positioned thumb retains its hover ink", ThumbInk("CanvasAccent"));
            Pointer(slider, maximumThumb, Mouse.PreviewMouseMoveEvent);
            Record("moving onto empty rail after release removes hover despite keyboard focus", slider.IsKeyboardFocusWithin && ThumbInk("CanvasText"));
            slider.StartPointerGesture(emptySlot, () => true);
            var outside = new Point(slider.ActualWidth + 10, emptySlot.Y);
            Pointer(slider, outside, Mouse.PreviewMouseMoveEvent);
            Record("dragging outside the whole control keeps the thumb highlighted", ThumbInk("CanvasAccent"));
            Pointer(slider, outside, Mouse.PreviewMouseUpEvent);
            Record("release outside the new thumb restores neutral ink despite keyboard focus", slider.IsKeyboardFocusWithin && ThumbInk("CanvasText"));
            window.UpdateLayout();
            var onThumb = PointerPosition(slider, thumb.TranslatePoint(new(thumb.ActualWidth / 2, thumb.ActualHeight / 2), slider));
            slider.StartPointerGesture(onThumb, () => true);
            Pointer(slider, outside, Mouse.PreviewMouseMoveEvent);
            Pointer(slider, outside, Mouse.LostMouseCaptureEvent);
            Record("capture loss outside the thumb restores neutral ink despite keyboard focus", slider.IsKeyboardFocusWithin && ThumbInk("CanvasText"));
        }
        finally { Mouse.Capture(null); window.Close(); Pump(); }
        foreach (var result in results) check(result.Name, result.Passed);
    }

    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (output is not null) Directory.CreateDirectory(output);
        foreach (string theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme);
            foreach (double scale in new[] { 1d, 1.25, 1.5, 2d })
            {
                foreach (double value in new[] { 0d, .5, 1d }) VisualState(theme, scale, value, check);
                if (output is not null) Preview(theme, scale, output);
            }
        }
        AppTheme.Apply("light");
        KeyboardAndRange(check);
    }

    private static void VisualState(string theme, double scale, double value, Action<string, bool> check)
    {
        var slider = new ToolbarOpacitySlider { Value = value };
        var surface = Surface(slider);
        Layout(surface, scale);
        var track = (Track)slider.Template.FindName("PART_Track", slider);
        var thumb = track.Thumb;
        var bounds = new Rect(thumb.TranslatePoint(new(), slider), thumb.RenderSize);
        string context = $"{theme} {scale * 100:0}% opacity {value * 100:0}%";
        check(context + " keeps the compact artwork and thumb inside the toolbar hit target",
            Math.Abs(slider.ActualWidth - 56) <= .5 / scale + .01 && Math.Abs(slider.ActualHeight - 26) <= .5 / scale + .01 &&
            Math.Abs(bounds.Width - 19 * 5d / 12) <= .5 / scale + .05 && Math.Abs(bounds.Height - 19) <= .5 / scale + .05 &&
            Math.Abs(bounds.Top - 3.5) <= .5 / scale + .05 && new Rect(slider.RenderSize).Contains(bounds));
        var bitmap = Raster(surface, scale);
        var center = thumb.TranslatePoint(new(thumb.ActualWidth / 2, thumb.ActualHeight / 2), surface);
        var centerColor = Pixel(bitmap, center, scale);
        var background = ((SolidColorBrush)surface.Background).Color;
        check(context + " cuts the ribbon out of the hollow thumb center", Difference(centerColor, background) <= 4);
        var outline = thumb.TranslatePoint(new(thumb.ActualWidth / 2, thumb.ActualHeight - .8), surface);
        check(context + " retains a visible thumb outline against its toolbar surface", Difference(Pixel(bitmap, outline, scale), background) >= 24);
        var controlBounds = new Rect(slider.TranslatePoint(new(), surface), slider.RenderSize);
        int left = (int)Math.Ceiling(controlBounds.Left * scale), top = (int)Math.Ceiling(controlBounds.Top * scale);
        int right = (int)Math.Floor(controlBounds.Right * scale), bottom = (int)Math.Floor(controlBounds.Bottom * scale);
        // A ceil-sized bitmap can have a partially transparent final row outside
        // a fractional-DPI surface. Measure control artwork, not that host edge.
        Rect fullInk = InkBounds(bitmap, background);
        Rect ink = InkBounds(new CroppedBitmap(bitmap, new Int32Rect(left, top, right - left, bottom - top)), background);
        check(context + $" keeps idle artwork inside the inset toolbar area ({ink.Width}×{ink.Height}px; host {fullInk.Width}×{fullInk.Height}px)",
            ink.Width > 24 * scale && ink.Width <= 52 * scale + 1 && ink.Height <= 24 * scale);
        check(context + " remains an enabled opacity control at both ends", slider.IsEnabled && slider.Visibility == Visibility.Visible && slider.Opacity == 1 &&
            value == slider.Value && !Descendants(slider).Any(element => element is TextBox or NumericValueField));
        double position = bounds.Left + bounds.Width / 2;
        check(context + " locates its handle along the ribbon rather than a binary switch",
            Math.Abs(position - (slider.ActualWidth / 2 + (value - .5) * 42)) <= 1 / scale + .1);
        check(context + " visual checks stay detached", PresentationSource.FromVisual(slider) is null);
    }

    private static void KeyboardAndRange(Action<string, bool> check)
    {
        using var modifiers = new ThreadModifierScope(ModifierKeys.None);
        var slider = new ToolbarOpacitySlider { Value = .5 };
        Layout(Surface(slider), 1);
        var peer = UIElementAutomationPeer.CreatePeerForElement(slider);
        check("toolbar opacity retains native slider range accessibility", peer is SliderAutomationPeer &&
            peer.GetPattern(PatternInterface.RangeValue) is IRangeValueProvider provider && provider.Minimum == 0 && provider.Maximum == 1 && !provider.IsReadOnly);
        SendKey(slider, Key.Right);
        check("toolbar opacity Right key increases by one percentage point", Math.Abs(slider.Value - .51) < 1e-10);
        SendKey(slider, Key.Left);
        check("toolbar opacity Left key decreases by one percentage point", Math.Abs(slider.Value - .5) < 1e-10);
        SendKey(slider, Key.Home);
        check("zero opacity keeps the control enabled and visible", slider.Value == 0 && slider.IsEnabled && slider.Visibility == Visibility.Visible);
        SendKey(slider, Key.Left);
        check("toolbar opacity keyboard adjustment respects the zero endpoint", slider.Value == 0);
        SendKey(slider, Key.End); SendKey(slider, Key.Right);
        check("toolbar opacity keyboard adjustment respects the fully opaque endpoint", slider.Value == 1 && slider.IsEnabled && slider.Visibility == Visibility.Visible);
    }

    private static void Preview(string theme, double scale, string output)
    {
        var rows = new StackPanel();
        foreach (double value in new[] { 0d, .5, 1d })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            row.Children.Add(new TextBlock { Text = $"{value * 100:0}%", Width = 44, VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
                Foreground = (Brush)Application.Current.FindResource("CanvasText") });
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
            toolbar.Children.Add(Icon(CommandGlyphs.Undo));
            toolbar.Children.Add(new ToolbarOpacitySlider { Value = value });
            toolbar.Children.Add(Icon("M1,8 Q8,-1 15,8 Q8,17 1,8 Z M8,5 A3,3 0 1 1 7.99,5"));
            toolbar.Children.Add(Icon(CommandGlyphs.PassThrough));
            row.Children.Add(toolbar); rows.Children.Add(row);
        }
        var surface = Surface(rows); Layout(surface, scale);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Raster(surface, scale)));
        using var stream = File.Create(Path.Combine(output, $"toolbar-opacity-{theme}-{scale * 100:0}.png")); encoder.Save(stream);
    }

    private static Button Icon(string geometry) => new()
    {
        Width = 26, Height = 26, MinWidth = 26, MinHeight = 26, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(3),
        BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1), Content = CommandGlyphs.Create(geometry), Focusable = false
    };

    private static Border Surface(UIElement child) => new()
    {
        Padding = new Thickness(6), Background = (Brush)Application.Current.FindResource("CanvasSurface"), Child = child,
        UseLayoutRounding = true, SnapsToDevicePixels = true
    };

    private static void Layout(FrameworkElement root, double scale)
    {
        root.Measure(new Size(800, 400)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        foreach (var child in Descendants(root).OfType<FrameworkElement>()) { child.InvalidateMeasure(); child.InvalidateArrange(); }
        root.InvalidateMeasure(); root.InvalidateArrange();
        root.Measure(new Size(800, 400)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
    }

    private static RenderTargetBitmap Raster(FrameworkElement root, double scale)
    {
        var result = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        result.Render(root); return result;
    }

    private static Color Pixel(BitmapSource bitmap, Point point, double scale)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(Math.Clamp((int)Math.Floor(point.X * scale), 0, bitmap.PixelWidth - 1),
            Math.Clamp((int)Math.Floor(point.Y * scale), 0, bitmap.PixelHeight - 1), 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static Rect InkBounds(BitmapSource bitmap, Color background)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        int left = bitmap.PixelWidth, top = bitmap.PixelHeight, right = -1, bottom = -1;
        for (int y = 0; y < bitmap.PixelHeight; y++)
        for (int x = 0; x < bitmap.PixelWidth; x++)
        {
            int index = (y * bitmap.PixelWidth + x) * 4;
            if (Difference(Color.FromArgb(pixels[index + 3], pixels[index + 2], pixels[index + 1], pixels[index]), background) < 5) continue;
            left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        return right < left ? Rect.Empty : new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    private static int Difference(Color first, Color second) => Math.Max(Math.Abs(first.A - second.A),
        Math.Max(Math.Abs(first.R - second.R), Math.Max(Math.Abs(first.G - second.G), Math.Abs(first.B - second.B))));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void SendKey(UIElement target, Key key)
    {
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent };
        target.RaiseEvent(input);
    }

    private static bool Pointer(UIElement target, Point point, RoutedEvent routedEvent)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var source = (HwndSource)PresentationSource.FromVisual(target);
        var mouse = (MouseDevice)typeof(ToolOptionsInputFixture).GetMethod("ControlledMouse", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [target])!;
        try
        {
            typeof(MouseDevice).GetField("_inputSource", privateInstance)!.SetValue(mouse, source);
            typeof(MouseDevice).GetField("_lastPosition", privateInstance)!.SetValue(mouse,
                source.CompositionTarget.TransformToDevice.Transform(target.TranslatePoint(point, (UIElement)source.RootVisual)));
            InputEventArgs input = routedEvent == Mouse.PreviewMouseMoveEvent || routedEvent == Mouse.LostMouseCaptureEvent
                ? new MouseEventArgs(mouse, Environment.TickCount) { RoutedEvent = routedEvent }
                : new MouseButtonEventArgs(mouse, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent };
            InputManager.Current.ProcessInput(input);
            return input.Handled;
        }
        finally { ToolOptionsInputFixture.ReleaseControlledMouse(mouse); }
    }

    private static Cursor? QueryCursor(UIElement target)
    {
        var query = new QueryCursorEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.QueryCursorEvent };
        target.RaiseEvent(query); return query.Cursor;
    }

    private static Point PointerPosition(UIElement target, Point requested)
    {
        Point actual = requested;
        MouseEventHandler observe = (_, e) => actual = e.GetPosition(target);
        target.AddHandler(Mouse.PreviewMouseMoveEvent, observe, true);
        try { Pointer(target, requested, Mouse.PreviewMouseMoveEvent); }
        finally { target.RemoveHandler(Mouse.PreviewMouseMoveEvent, observe); }
        return actual;
    }

    private static void Pump(TimeSpan? duration = null)
    {
        var frame = new DispatcherFrame();
        if (duration is null) Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        else
        {
            var timer = new DispatcherTimer { Interval = duration.Value };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start();
        }
        Dispatcher.PushFrame(frame);
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int length, out int needed);

    private sealed class DetachedSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
