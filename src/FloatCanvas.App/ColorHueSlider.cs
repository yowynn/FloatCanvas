using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FloatCanvas.App;

/// <summary>The hue lane and its lever share one captured pointer gesture.</summary>
internal sealed class ColorHueSlider : Slider
{
    private bool pointerActive, thumbPressed, capturing;
    private double pointerOrigin, pointerValue, railStart, railTravel;
    internal bool IsPointerInteracting => pointerActive;
    internal Func<bool>? CanBeginPointerGesture { get; set; }
    internal event Action<bool>? PointerGestureCompleted;

    internal ColorHueSlider()
    {
        Minimum = 0; Maximum = 360; SmallChange = 1; LargeChange = 10;
        SetResourceReference(StyleProperty, "CanvasHueSlider");
        IsEnabledChanged += (_, _) => { if (!IsEnabled) EndPointerGesture(false); };
        Unloaded += (_, _) => EndPointerGesture(false);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        StartPointerGesture(e.GetPosition(this), CaptureMouse);
    }

    internal void StartPointerGesture(Point point, Func<bool> capture)
    {
        if (pointerActive || !IsEnabled || !double.IsFinite(point.X)) return;
        ApplyTemplate();
        if (Template.FindName("PART_Track", this) is not Track { Thumb: { } thumb } track) return;
        Focus();
        pointerOrigin = point.X; pointerValue = Value;
        railStart = track.TranslatePoint(new(thumb.ActualWidth / 2, 0), this).X;
        railTravel = Math.Max(1, track.ActualWidth - thumb.ActualWidth);
        thumbPressed = new Rect(thumb.TranslatePoint(new(), this), thumb.RenderSize).Contains(point);
        // Both the mode and the owner's transaction must exist before capture
        // can synchronously route another move or invalidate this gesture.
        pointerActive = true;
        if (CanBeginPointerGesture?.Invoke() == false) { pointerActive = false; return; }
        if (!pointerActive) return;
        if (!thumbPressed) UpdatePointer(point.X);
        if (!pointerActive) return;
        bool captured;
        capturing = true;
        try { captured = capture(); }
        finally { capturing = false; }
        if (!captured) EndPointerGesture(false);
        else if (!pointerActive && IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (!pointerActive) { base.OnPreviewMouseMove(e); return; }
        // Capture's synchronous mouse synchronization is not a new drag sample.
        if (!capturing) UpdatePointer(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!pointerActive) { base.OnPreviewMouseLeftButtonUp(e); return; }
        UpdatePointer(e.GetPosition(this).X);
        EndPointerGesture(true); e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!IsMouseCaptured) EndPointerGesture(false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && pointerActive) { EndPointerGesture(false); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }

    private void UpdatePointer(double x)
    {
        if (!pointerActive || !double.IsFinite(x)) return;
        double fraction = (x - railStart) / railTravel;
        if (IsDirectionReversed) fraction = 1 - fraction;
        double next = thumbPressed
            ? pointerValue + (IsDirectionReversed ? -1 : 1) * (x - pointerOrigin) / railTravel * (Maximum - Minimum)
            : Minimum + fraction * (Maximum - Minimum);
        SetCurrentValue(ValueProperty, Math.Clamp(next, Minimum, Maximum));
    }

    internal void EndPointerGesture(bool commit)
    {
        if (!pointerActive) return;
        pointerActive = false;
        if (!commit) SetCurrentValue(ValueProperty, pointerValue);
        if (IsMouseCaptured) ReleaseMouseCapture();
        PointerGestureCompleted?.Invoke(commit);
    }
}
