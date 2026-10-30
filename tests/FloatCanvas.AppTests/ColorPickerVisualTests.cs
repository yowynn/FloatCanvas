using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using L = FloatCanvas.Localization.AppText;

internal static class ColorPickerVisualTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        output ??= Path.Combine(ApplicationPaths.ProfileDirectory, "color-picker-visuals");
        Directory.CreateDirectory(output);
        string language = L.Preference;
        try
        {
            L.Initialize("zh-CN");
            foreach (string theme in new[] { "light", "dark" })
            {
                AppTheme.Apply(theme);
                var library = new ColorLibrary(Path.Combine(output, "profile-" + theme, "colors.json"));
                foreach (uint color in new uint[] { 0xFFE03E3E, 0xFF2383E2, 0xFF448361, 0x80FFFFFF }) library.Remember(color);
                library.Pin(0xFF9065B0); library.Pin(0x80336699);
                var picker = new ColorPickerPanel(ColorRole.Foreground, library)
                {
                    Value = 0x80336699, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
                };
                picker.DragHandle.Visibility = Visibility.Collapsed;
                using var surface = new ColorPickerTestSurface(picker);
                picker.UpdateLayout();
                var spectrum = Field<ColorSpectrum>(picker, "spectrum");
                var hue = Field<Slider>(picker, "hue");
                var hex = Field<TextBox>(picker, "hex");
                var scroll = (ScrollViewer)picker.Content;
                check(theme + " taller color spectrum fits the two-column picker without vertical scrolling",
                    spectrum.ActualHeight >= 150 && scroll.ScrollableHeight < .1 && scroll.ActualHeight <= 520.1);
                var spectrumBounds = Bounds(spectrum, picker);
                var hueBounds = Bounds(hue, picker);
                var hexBounds = Bounds(hex, picker);
                check(theme + " spectrum, hue lane and color input have separate usable rows",
                    spectrumBounds.Bottom <= hueBounds.Top && hueBounds.Bottom <= hexBounds.Top);

                hue.ApplyTemplate();
                var track = (Track)hue.Template.FindName("PART_Track", hue)!;
                var lane = (Border)hue.Template.FindName("HueLane", hue)!;
                check(theme + " hue uses one rainbow lane and a taller narrow lever",
                    lane.Background is LinearGradientBrush { GradientStops.Count: 7 } && lane.ActualHeight >= 12 &&
                    track.Thumb is { } thumb && thumb.ActualHeight >= 20 && thumb.ActualWidth <= 12 && thumb.ActualHeight > thumb.ActualWidth);
                foreach (double value in new[] { 0d, 180, 360 })
                {
                    hue.Value = value; picker.UpdateLayout();
                    var laneBounds = Bounds(lane, hue);
                    var thumbBounds = Bounds(track.Thumb, hue);
                    double expected = laneBounds.Left + laneBounds.Width * value / 360;
                    check(theme + " hue " + value + " aligns the native thumb center with the rainbow lane",
                        Math.Abs(thumbBounds.Left + thumbBounds.Width / 2 - expected) < 1 &&
                        Math.Abs(thumbBounds.Top + thumbBounds.Height / 2 - (laneBounds.Top + laneBounds.Height / 2)) < 1);
                }
                check(theme + " hue retains the right red endpoint without wrapping its lever",
                    hue.Value == 360 && spectrum.Hue == 360 && picker.TryGetDraft(out uint red) && (red >> 24) == 128);
                picker.Value = 0x80336699;
                hue.Value = 120; picker.UpdateLayout();
                check(theme + " custom hue track still updates spectrum and hex while preserving alpha",
                    spectrum.Hue == 120 && picker.TryGetDraft(out uint green) && (green >> 24) == 128 &&
                    ((green >> 8) & 255) > ((green >> 16) & 255) &&
                    ColorLibrary.TryParseHtml(hex.Text, out uint textColor) && (textColor & 0xFFFFFF) == (green & 0xFFFFFF));
                HuePointer(picker, hue, lane, theme, check);
                HuePointerLifecycle(picker, (ColorHueSlider)hue, track, theme, check);
                picker.Value = 0xFFE03E3E; picker.UpdateLayout();
                foreach (int dpi in new[] { 96, 144 }) Save(picker, Path.Combine(output, "picker-" + theme + "-" + dpi + ".png"), dpi);
            }
        }
        finally { L.Initialize(language); }
    }

    private static void HuePointer(ColorPickerPanel picker, Slider hue, Border lane, string theme, Action<string, bool> check)
    {
        picker.Value = 0x80FF0000; picker.UpdateLayout();
        int started = 0;
        bool changedBeforeGesture = false;
        void Started() => started++;
        void Changed(object sender, RoutedPropertyChangedEventArgs<double> args)
        { if (!picker.IsInteracting) changedBeforeGesture = true; }
        picker.GestureStarted += Started; hue.ValueChanged += Changed;
        try { PointerPress(lane, new(lane.ActualWidth * .75, lane.ActualHeight / 2)); }
        finally { picker.GestureStarted -= Started; hue.ValueChanged -= Changed; }
        check(theme + " clicking the rainbow lane starts its gesture before the native slider edits color",
            started == 1 && !changedBeforeGesture && picker.IsInteracting && hue.Value > 250 && hue.Value < 290 &&
            picker.TryGetDraft(out uint changed) && changed != 0x80FF0000 && (changed >> 24) == 128);
        check(theme + " a non-thumb hue press captures the same continuous drag", hue.IsMouseCaptured);
        Pointer(hue, new(hue.ActualWidth * .25, -30), Mouse.PreviewMouseMoveEvent);
        check(theme + " a hue lane press continues updating while dragged outside the control",
            picker.IsInteracting && hue.Value > 80 && hue.Value < 100);
        int completed = 0; bool committed = false;
        void Completed(bool commit) { completed++; committed = commit; }
        picker.GestureCompleted += Completed;
        try { Pointer(hue, new(hue.ActualWidth + 30, -30), Mouse.PreviewMouseUpEvent); }
        finally { picker.GestureCompleted -= Completed; }
        check(theme + " releasing a hue lane drag outside commits exactly once and releases capture",
            !picker.IsInteracting && !hue.IsMouseCaptured && hue.Value == 360 && completed == 1 && committed);
        picker.Value = 0x80FF0000; picker.UpdateLayout();
        PointerPress(lane, new(lane.ActualWidth * .75, lane.ActualHeight / 2));
        picker.EndGesture(false); picker.UpdateLayout();
        check(theme + " cancelling a rainbow-lane click restores the pre-click color",
            !picker.IsInteracting && picker.TryGetDraft(out uint restored) && restored == 0x80FF0000 && Math.Abs(hue.Value) < .01);
        picker.CanBeginGesture = () => false;
        try { PointerPress(lane, new(lane.ActualWidth * .5, lane.ActualHeight / 2)); }
        finally { picker.CanBeginGesture = null; }
        check(theme + " a denied hue gesture cannot change color through move-to-point",
            !picker.IsInteracting && Math.Abs(hue.Value) < .01 && picker.TryGetDraft(out uint denied) && denied == 0x80FF0000);
    }

    private static void HuePointerLifecycle(ColorPickerPanel picker, ColorHueSlider hue, Track track, string theme, Action<string, bool> check)
    {
        picker.Value = 0x80336699; picker.UpdateLayout();
        double original = hue.Value;
        var thumb = track.Thumb;
        var origin = thumb.TranslatePoint(new(thumb.ActualWidth / 4, thumb.ActualHeight / 2), hue);
        PointerPress(thumb, new(thumb.ActualWidth / 4, thumb.ActualHeight / 2));
        check(theme + " a lever press preserves its off-center grab point without jumping",
            hue.IsPointerInteracting && hue.IsMouseCaptured && hue.Value == original);
        Pointer(hue, new(origin.X + 20, origin.Y), Mouse.PreviewMouseMoveEvent);
        double expected = original + 20 / (track.ActualWidth - thumb.ActualWidth) * 360;
        check(theme + " lever dragging preserves the initial pointer offset", Math.Abs(hue.Value - expected) < .01);
        var source = (HwndSource)PresentationSource.FromVisual(hue);
        hue.RaiseEvent(new KeyEventArgs(new TestKeyboardDevice(), source, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        check(theme + " Escape cancels the captured hue drag and restores its starting color",
            !hue.IsPointerInteracting && !hue.IsMouseCaptured && !picker.IsInteracting &&
            picker.TryGetDraft(out uint escaped) && escaped == 0x80336699);
        Pointer(hue, new(hue.ActualWidth, 0), Mouse.PreviewMouseMoveEvent);
        Pointer(hue, new(hue.ActualWidth, 0), Mouse.PreviewMouseUpEvent);
        check(theme + " late hue movement and release cannot reclaim an escaped drag",
            picker.TryGetDraft(out uint late) && late == 0x80336699);

        picker.Value = 0x80FF0000; picker.UpdateLayout();
        PointerPress(hue, new(hue.ActualWidth * .5, 1));
        check(theme + " hue dragging can start in the control's vertical padding", hue.IsPointerInteracting && hue.IsMouseCaptured);
        int cancelled = 0;
        void Completed(bool commit) { if (!commit) cancelled++; }
        picker.GestureCompleted += Completed;
        try { hue.ReleaseMouseCapture(); }
        finally { picker.GestureCompleted -= Completed; }
        check(theme + " losing hue capture cancels once and restores the starting color",
            cancelled == 1 && !hue.IsPointerInteracting && !picker.IsInteracting &&
            picker.TryGetDraft(out uint lost) && lost == 0x80FF0000);

        PointerPress(hue, new(hue.ActualWidth * .75, 1));
        picker.SynchronizeExternalValue(0xAA987654); picker.UpdateLayout();
        Pointer(hue, new(hue.ActualWidth, 0), Mouse.PreviewMouseMoveEvent);
        Pointer(hue, new(hue.ActualWidth, 0), Mouse.PreviewMouseUpEvent);
        check(theme + " external color replacement cancels hue capture and rejects late input",
            !hue.IsPointerInteracting && !hue.IsMouseCaptured && !picker.IsInteracting &&
            picker.TryGetDraft(out uint external) && external == 0xAA987654);
        hue.StartPointerGesture(new(hue.ActualWidth / 2, 1), () => false);
        check(theme + " failed hue capture cancels its initial jump",
            !hue.IsPointerInteracting && !picker.IsInteracting &&
            picker.TryGetDraft(out uint failed) && failed == 0xAA987654);
        hue.StartPointerGesture(new(hue.ActualWidth / 2, 1), () =>
        {
            picker.SynchronizeExternalValue(0x55010203); return true;
        });
        Pointer(hue, new(hue.ActualWidth, 0), Mouse.PreviewMouseMoveEvent);
        Pointer(hue, new(hue.ActualWidth, 0), Mouse.PreviewMouseUpEvent);
        check(theme + " external replacement during capture cannot reactivate an obsolete hue drag",
            !hue.IsPointerInteracting && !picker.IsInteracting && picker.TryGetDraft(out uint replaced) && replaced == 0x55010203);
    }

    private static void PointerPress(UIElement target, Point point) => Pointer(target, point, Mouse.PreviewMouseDownEvent);

    private static void Pointer(UIElement target, Point point, RoutedEvent routedEvent)
    {
        var source = (HwndSource)PresentationSource.FromVisual(target);
        var devicePoint = source.CompositionTarget.TransformToDevice.Transform(target.TranslatePoint(point, (UIElement)source.RootVisual));
        if (routedEvent == Mouse.PreviewMouseDownEvent)
        {
            // A synthetic routed press has no preceding native input stream.
            // Activate this owned HWND's provider without reading or moving the
            // physical cursor, including when running on an isolated desktop.
            SetCapture(source.Handle);
            if (GetCapture() != source.Handle) throw new InvalidOperationException("The hue fixture could not initialize its native mouse stream.");
            nint coordinates = (nint)(((int)Math.Round(devicePoint.X) & 0xFFFF) | (((int)Math.Round(devicePoint.Y) & 0xFFFF) << 16));
            SendMessage(source.Handle, 0x0200, 0, coordinates);
        }
        var mouse = (MouseDevice)typeof(ToolOptionsInputFixture).GetMethod("ControlledMouse", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [target])!;
        try
        {
            typeof(MouseDevice).GetField("_inputSource", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mouse, source);
            typeof(MouseDevice).GetField("_lastPosition", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mouse,
                devicePoint);
            InputEventArgs input = routedEvent == Mouse.PreviewMouseMoveEvent
                ? new MouseEventArgs(mouse, Environment.TickCount) { RoutedEvent = routedEvent }
                : new MouseButtonEventArgs(mouse, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent };
            InputManager.Current.ProcessInput(input);
        }
        finally
        {
            ToolOptionsInputFixture.ReleaseControlledMouse(mouse);
            if (routedEvent == Mouse.PreviewMouseDownEvent && Mouse.Captured is null && GetCapture() == source.Handle) ReleaseCapture();
        }
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static void Save(FrameworkElement element, string path, int dpi)
    {
        double scale = dpi / 96d;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var pixels = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        pixels.Render(drawing);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(pixels));
        using var file = File.Create(path); png.Save(file);
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
