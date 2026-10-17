using System.Windows;
using System.Windows.Input;

namespace FloatCanvas.App;

/// <summary>Turns a numeric field label into one cancellable horizontal drag.</summary>
internal sealed class NumericScrubber
{
    private readonly FrameworkElement label;
    private readonly Func<bool> begin;
    private readonly Action<double> update;
    private readonly Action<bool> finish;
    private Window? owner;
    private double previousX;
    private double totalDelta;

    internal bool IsActive { get; private set; }

    internal NumericScrubber(FrameworkElement label, Func<bool> begin, Action<double> update, Action<bool> finish)
    {
        this.label = label;
        this.begin = begin;
        this.update = update;
        this.finish = finish;
        label.Cursor = Cursors.SizeWE;
        label.Focusable = true;
        label.PreviewMouseLeftButtonDown += OnMouseDown;
        label.PreviewMouseMove += OnMouseMove;
        label.PreviewMouseLeftButtonUp += OnMouseUp;
        label.LostMouseCapture += OnLostMouseCapture;
        label.PreviewKeyDown += OnKeyDown;
        label.Unloaded += OnUnloaded;
    }

    // These methods also exercise the gesture without mouse capture or an HWND.
    internal bool TryBegin(double pointerX)
    {
        if (IsActive || !double.IsFinite(pointerX)) return false;
        if (!begin()) return false;
        previousX = pointerX;
        totalDelta = 0;
        IsActive = true;
        owner = Window.GetWindow(label);
        if (owner is not null) owner.Deactivated += OnWindowDeactivated;
        return true;
    }

    internal void Update(double pointerX, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (!IsActive || !double.IsFinite(pointerX)) return;
        double scale = (modifiers & ModifierKeys.Shift) != 0 ? 0.1 : (modifiers & ModifierKeys.Control) != 0 ? 10 : 1;
        double delta = (pointerX - previousX) * scale;
        previousX = pointerX;
        if (delta == 0 || !double.IsFinite(totalDelta + delta)) return;
        totalDelta += delta;
        try { update(totalDelta); }
        catch { End(false); }
    }

    internal void End(bool commit)
    {
        if (!IsActive) return;
        IsActive = false;
        if (owner is not null) owner.Deactivated -= OnWindowDeactivated;
        owner = null;
        try
        {
            if (label.IsMouseCaptured) label.ReleaseMouseCapture();
        }
        finally
        {
            // The state is cleared before callbacks: a capture-loss notification
            // or a refresh removing the label cannot finish the same drag twice.
            try { finish(commit); }
            catch { }
        }
    }

    internal bool HandleKey(Key key)
    {
        if (!IsActive || key != Key.Escape) return false;
        End(false);
        return true;
    }

    private double Position(MouseEventArgs e) => e.GetPosition(owner ?? (IInputElement)label).X;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !TryBegin(e.GetPosition(Window.GetWindow(label) ?? (IInputElement)label).X)) return;
        label.Focus();
        if (IsActive && !label.CaptureMouse()) End(false);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsActive) return;
        if (e.LeftButton != MouseButtonState.Pressed) { End(false); return; }
        Update(Position(e), Keyboard.Modifiers);
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsActive || e.ChangedButton != MouseButton.Left) return;
        Update(Position(e), Keyboard.Modifiers);
        End(true);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleKey(e.Key)) e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e) => End(false);
    private void OnWindowDeactivated(object? sender, EventArgs e) => End(false);
    private void OnUnloaded(object sender, RoutedEventArgs e) => End(false);
}
