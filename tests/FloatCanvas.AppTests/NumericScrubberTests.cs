using System.Reflection;
using FloatCanvas.App;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Input = System.Windows.Input;

/// <summary>Detached gesture checks; never captures the mouse or creates an HWND.</summary>
internal static class NumericScrubberTests
{
    public static void Run(Action<string, bool> assert)
    {
        var label = new Controls.TextBlock { Text = "X" };
        bool allowed = false;
        int begins = 0;
        var updates = new List<double>();
        var finishes = new List<bool>();
        var scrubber = new NumericScrubber(label, () => { begins++; return allowed; }, updates.Add, finishes.Add);
        assert("numeric scrub label advertises horizontal dragging and can receive Escape", label.Cursor == Input.Cursors.SizeWE && label.Focusable);
        assert("rejected numeric scrub does not start a gesture", !scrubber.TryBegin(100) && !scrubber.IsActive && begins == 1);
        scrubber.Update(120); scrubber.End(true);
        assert("idle numeric scrub does not update or finish", updates.Count == 0 && finishes.Count == 0);

        allowed = true;
        assert("numeric scrub begins exactly once", scrubber.TryBegin(100) && !scrubber.TryBegin(120) && begins == 2);
        scrubber.Update(115); scrubber.Update(105);
        assert("numeric scrub reports displacement from start rather than individual movement", updates.SequenceEqual([15d, 5d]));
        scrubber.Update(115, Input.ModifierKeys.Shift);
        scrubber.Update(116, Input.ModifierKeys.Control);
        scrubber.Update(126, Input.ModifierKeys.Shift | Input.ModifierKeys.Control);
        assert("numeric scrub changes sensitivity continuously with Shift taking precedence", updates.TakeLast(3).SequenceEqual([6d, 16d, 17d]));
        int count = updates.Count;
        scrubber.Update(126); scrubber.Update(double.NaN); scrubber.Update(double.PositiveInfinity);
        assert("stationary and non-finite scrub positions do not preview a change", updates.Count == count);
        scrubber.End(true); scrubber.End(false);
        assert("numeric scrub commits once despite repeated completion", !scrubber.IsActive && finishes.SequenceEqual([true]));

        scrubber.TryBegin(0); scrubber.Update(-8);
        assert("numeric scrub resets origin and accumulated displacement for each gesture", updates[^1] == -8);
        assert("unrelated key leaves scrub active", !scrubber.HandleKey(Input.Key.Enter) && scrubber.IsActive);
        assert("Escape cancels numeric scrub once", scrubber.HandleKey(Input.Key.Escape) && !scrubber.HandleKey(Input.Key.Escape) && finishes.SequenceEqual([true, false]));

        scrubber.TryBegin(0);
        label.RaiseEvent(new Input.MouseEventArgs(Input.Mouse.PrimaryDevice, 0) { RoutedEvent = Wpf.UIElement.LostMouseCaptureEvent });
        assert("capture loss cancels numeric scrub", !scrubber.IsActive && finishes.Count == 3 && !finishes[^1]);
        scrubber.TryBegin(0);
        label.RaiseEvent(new Wpf.RoutedEventArgs(Wpf.FrameworkElement.UnloadedEvent));
        assert("unloading a numeric label cancels its gesture", !scrubber.IsActive && finishes.Count == 4 && !finishes[^1]);

        var window = new Wpf.Window { Content = label };
        scrubber.TryBegin(0);
        typeof(Wpf.Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [EventArgs.Empty]);
        assert("window deactivation cancels numeric scrub", !scrubber.IsActive && finishes.Count == 5 && !finishes[^1]);

        int failedFinishes = 0;
        var failed = new NumericScrubber(new Controls.TextBlock(), () => true, _ => throw new InvalidOperationException("preview failed"), commit => { if (!commit) failedFinishes++; });
        failed.TryBegin(0); failed.Update(5); failed.End(true);
        assert("failed numeric preview cancels once and leaves the gesture idle", !failed.IsActive && failedFinishes == 1);
        assert("numeric scrub checks leave no visible or native window", !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0);
        window.Close();
    }
}
