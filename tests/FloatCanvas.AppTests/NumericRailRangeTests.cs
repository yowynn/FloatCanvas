using System.Windows;
using System.Windows.Input;
using FloatCanvas.App;

internal static class NumericRailRangeTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var field = new NumericValueField(-1_000_000, 1_000_000, 0, 64) { Value = 500.123456789 };
        int changes = 0, commits = 0;
        field.ValueChanged += _ => changes++;
        field.SliderValueCommitted += () => commits++;
        string text = field.Input.Text;
        field.SetSliderRange(16, 32);
        check("changing a rail silently preserves an exact model value beyond its common range", field.Value == 500.123456789 &&
            field.TryGetValue(out double exact) && exact == field.Value && field.Slider.Value == 32 && field.Input.Text == text && changes == 0 && commits == 0);
        field.Input.Text = "1234.56789"; changes = 0;
        field.SetSliderRange(-10, 10);
        check("changing a rail preserves a precise typed draft and its larger legal value", field.Input.Text == "1234.56789" &&
            field.TryGetValue(out exact) && exact == 1234.56789 && field.Value == exact && changes == 0 && commits == 0);
        field.Input.Text = "12e";
        field.SetSliderRange(100, 200);
        check("disjoint rail changes retain invalid drafts without publishing model edits", field.Input.Text == "12e" &&
            !field.TryGetValue(out _) && field.Value == 1234.56789 && field.Slider.Minimum == 100 && field.Slider.Maximum == 200 && changes == 0 && commits == 0);
        field.SetModelValue(1234.56789);
        field.ShowPlaceholder("—"); field.SetSliderRange(0, 16);
        check("rail context changes retain mixed-value placeholders", field.Input.Text == "—" && field.Value == 1234.56789 && changes == 0 && commits == 0);
        field.SetModelValue(1234.56789);
        field.AnchorScrubber!.TryBegin(0); field.AnchorScrubber.Update(10); field.AnchorScrubber.End(true);
        check("icon dragging remains free to edit beyond the narrowed slider range", field.Value > 1234.56789 && field.Slider.Value == 16 && changes == 1 && commits == 1);
        var keyboard = new NumericValueField(1, 256, sliderMaximum: 32, integerOnly: true) { Value = 64 };
        SendKey(keyboard.Slider, Key.Left);
        check("keyboard fine adjustment uses the actual value beyond the common rail", keyboard.Value == 63 && keyboard.Slider.Value == 32);
        keyboard.Value = 32; SendKey(keyboard.Slider, Key.Right);
        check("keyboard fine adjustment can cross the common rail boundary", keyboard.Value == 33 && keyboard.Slider.Value == 32);
        int keyboardCommits = 0; keyboard.SliderValueCommitted += () => keyboardCommits++;
        keyboard.Value = 256; SendKey(keyboard.Slider, Key.Right);
        check("keyboard adjustment respects the original hard limit without a redundant commit", keyboard.Value == 256 && keyboardCommits == 0);
        keyboard.Value = 64; keyboard.BeginGesture(); SendKey(keyboard.Slider, Key.Right);
        check("keyboard input during a pending drag remains part of the same gesture", keyboard.Value == 65 && keyboard.IsInteracting && keyboardCommits == 0);
        keyboard.EndGesture(true);
        check("combined drag and keyboard input commits only once", keyboardCommits == 1);
        ToolNumericRailTests.Run(check);
        InspectorNumericRailTests.Run(check);
    }

    private static void SendKey(UIElement target, Key key)
    {
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(input);
        if (!input.Handled) { input.RoutedEvent = Keyboard.KeyDownEvent; target.RaiseEvent(input); }
    }

    private sealed class DetachedSource : PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }
}
