using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Inline saturation/value plane; the adjacent hue slider and hex field share this draft.</summary>
internal sealed class ColorSpectrum : FrameworkElement
{
    private double hue, saturation = 1, brightness = 1;
    internal event Action<uint>? ColorChanged;
    internal Func<bool>? CanBeginGesture { get; set; }
    internal event Action<bool>? GestureCompleted;
    private bool dragging;
    internal double Hue => hue;
    internal uint SelectedColor
    {
        get => ToColor(hue, saturation, brightness);
        set
        {
            double r = ((value >> 16) & 255) / 255d, g = ((value >> 8) & 255) / 255d, b = (value & 255) / 255d;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
            if (delta > 0) hue = ((max == r ? (g - b) / delta : max == g ? 2 + (b - r) / delta : 4 + (r - g) / delta) * 60 + 360) % 360;
            saturation = max == 0 ? 0 : delta / max; brightness = max;
            InvalidateVisual();
        }
    }

    internal ColorSpectrum()
    {
        Height = 160; MinWidth = 180; Focusable = true; Cursor = Cursors.Cross;
        AutomationProperties.SetName(this, L.Get("Color.Spectrum"));
        ToolTip = L.Get("Color.SpectrumHint");
    }

    internal void ChangeHue(double next)
    {
        hue = Math.Clamp(next, 0, 360); Notify();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var color = ToMedia(ToColor(hue, 1, 1));
        dc.PushClip(new RectangleGeometry(bounds, 5, 5));
        dc.DrawRectangle(new SolidColorBrush(color), null, bounds);
        dc.DrawRectangle(new LinearGradientBrush(Colors.White, Colors.Transparent, new Point(0, .5), new Point(1, .5)), null, bounds);
        dc.DrawRectangle(new LinearGradientBrush(Colors.Transparent, Colors.Black, new Point(.5, 0), new Point(.5, 1)), null, bounds);
        dc.Pop();
        var center = new Point(Math.Clamp(saturation * ActualWidth, 3, Math.Max(3, ActualWidth - 3)), Math.Clamp((1 - brightness) * ActualHeight, 3, Math.Max(3, ActualHeight - 3)));
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 3), center, 4, 4);
        dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), center, 4, 4);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (CanBeginGesture?.Invoke() == false) { e.Handled = true; return; }
        Focus(); dragging = CaptureMouse();
        if (dragging) Pick(e.GetPosition(this)); else GestureCompleted?.Invoke(false);
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging) { Pick(e.GetPosition(this)); e.Handled = true; }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!dragging) return;
        Pick(e.GetPosition(this)); dragging = false; ReleaseMouseCapture(); GestureCompleted?.Invoke(true); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!dragging) return;
        dragging = false; GestureCompleted?.Invoke(false);
    }
    internal void CancelGesture()
    {
        dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? .1 : .01;
        if (e.Key == Key.Left) saturation -= step;
        else if (e.Key == Key.Right) saturation += step;
        else if (e.Key == Key.Up) brightness += step;
        else if (e.Key == Key.Down) brightness -= step;
        else { base.OnKeyDown(e); return; }
        saturation = Math.Clamp(saturation, 0, 1); brightness = Math.Clamp(brightness, 0, 1);
        Notify(); e.Handled = true;
    }
    private void Pick(Point position)
    {
        saturation = Math.Clamp(position.X / Math.Max(1, ActualWidth), 0, 1);
        brightness = 1 - Math.Clamp(position.Y / Math.Max(1, ActualHeight), 0, 1);
        Notify();
    }
    private void Notify() { InvalidateVisual(); ColorChanged?.Invoke(SelectedColor); }
    internal static uint ToColor(double hue, double saturation, double brightness)
    {
        double c = brightness * saturation, h = (hue % 360 + 360) % 360 / 60, x = c * (1 - Math.Abs(h % 2 - 1)), m = brightness - c;
        (double r, double g, double b) = h switch { < 1 => (c, x, 0d), < 2 => (x, c, 0d), < 3 => (0d, c, x), < 4 => (0d, x, c), < 5 => (x, 0d, c), _ => (c, 0d, x) };
        return 0xFF000000 | ((uint)Math.Round((r + m) * 255) << 16) | ((uint)Math.Round((g + m) * 255) << 8) | (uint)Math.Round((b + m) * 255);
    }
    private static Color ToMedia(uint color) => Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);
}
