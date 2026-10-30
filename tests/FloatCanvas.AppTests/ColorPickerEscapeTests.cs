using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FloatCanvas.App;

internal static class ColorPickerEscapeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        output ??= Path.Combine(ApplicationPaths.ProfileDirectory, "color-picker-escape");
        Directory.CreateDirectory(output);
        foreach (string theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme);
            using var fixture = new Fixture(Path.Combine(output, theme, "colors.json"));
            foreach (string origin in new[] { "window", "caption", "caption button", "outer border", "hex", "spectrum" })
            {
                var popup = fixture.Open();
                check(theme + " " + origin + " Escape fixture uses the real regular owned color window",
                    Field<PopupHandle>(fixture.Field, "popupDisplay").Key is WindowPopup && popup.Owner == fixture.Owner && popup.IsVisible);
                UIElement target = Target(fixture, popup, origin);
                int closed = 0; void Closed() => closed++;
                fixture.Field.PickerClosed += Closed;
                bool handled;
                try { handled = Escape(target); }
                finally { fixture.Field.PickerClosed -= Closed; }
                check(theme + " Escape from " + origin + " closes only the secondary color window once",
                    handled && !fixture.Field.IsPickerOpen && closed == 1 && fixture.Owner.IsVisible);
            }
            foreach (bool numeric in new[] { false, true })
            foreach (string origin in new[] { "window", "hex" }) Gesture(fixture, numeric, origin, theme, check);
            FocusedHex(fixture, theme, check);
            FocusedHexDraft(fixture, false, theme, check);
            FocusedHexDraft(fixture, true, theme, check);
            UnchangedPointer(fixture, false, theme, check);
            UnchangedPointer(fixture, true, theme, check);
        }
    }

    private static void UnchangedPointer(Fixture fixture, bool numeric, string theme, Action<string, bool> check)
    {
        fixture.Field.Value = 0x80336699;
        var popup = fixture.Open();
        var picker = fixture.Picker;
        var alpha = Field<NumericValueField>(picker, "alphaField");
        var hue = Field<ColorHueSlider>(picker, "hue");
        if (numeric)
        {
            if (!alpha.BeginGesture()) throw new InvalidOperationException("The unchanged opacity fixture could not begin editing.");
        }
        else
        {
            var track = (System.Windows.Controls.Primitives.Track)hue.Template.FindName("PART_Track", hue)!;
            var thumb = track.Thumb;
            hue.StartPointerGesture(thumb.TranslatePoint(new(thumb.ActualWidth / 2, thumb.ActualHeight / 2), hue), () => true);
        }
        check(theme + " an unchanged " + (numeric ? "opacity" : "hue lever") + " gesture remains distinct from focus-only input",
            picker.HasCancellableGesture && fixture.Field.Value == 0x80336699);
        bool handled = Escape(popup);
        check(theme + " Escape cancels an unchanged active " + (numeric ? "opacity" : "hue lever") + " gesture without closing its window",
            handled && fixture.Field.IsPickerOpen && !picker.IsInteracting && !alpha.IsInteracting && !hue.IsPointerInteracting && fixture.Field.Value == 0x80336699);
        handled = Escape(popup);
        check(theme + " a second Escape closes the unchanged " + (numeric ? "opacity" : "hue lever") + " gesture's window",
            handled && !fixture.Field.IsPickerOpen && fixture.Owner.IsVisible);
    }

    private static void FocusedHex(Fixture fixture, string theme, Action<string, bool> check)
    {
        var popup = fixture.Open();
        var hex = Field<TextBox>(fixture.Picker, "hex");
        SetFocus(new WindowInteropHelper(popup).Handle);
        Keyboard.Focus(hex);
        check(theme + " the regular color window can focus its unchanged hex input", hex.IsKeyboardFocused);
        bool handled = Escape(hex);
        check(theme + " Escape closes an unchanged hex field without requiring a focus-only cancellation",
            handled && !fixture.Field.IsPickerOpen && fixture.Owner.IsVisible);
        check(theme + " closing the color window returns keyboard focus to its source color button",
            Field<Button>(fixture.Field, "button").IsKeyboardFocused);
    }

    private static void FocusedHexDraft(Fixture fixture, bool invalid, string theme, Action<string, bool> check)
    {
        fixture.Field.Value = 0x80336699;
        var popup = fixture.Open();
        var hex = Field<TextBox>(fixture.Picker, "hex");
        SetFocus(new WindowInteropHelper(popup).Handle); Keyboard.Focus(hex);
        hex.Text = invalid ? "invalid" : "#445566";
        bool handled = Escape(hex);
        check(theme + " first Escape restores a " + (invalid ? "malformed" : "modified") + " focused hex draft and keeps the color window",
            handled && fixture.Field.IsPickerOpen && !fixture.Picker.IsInteracting && fixture.Field.Value == 0x80336699 && hex.Text == "#336699");
        handled = Escape(hex);
        check(theme + " second Escape closes the restored " + (invalid ? "malformed" : "modified") + " hex editor",
            handled && !fixture.Field.IsPickerOpen && fixture.Owner.IsVisible);
    }

    private static void Gesture(Fixture fixture, bool numeric, string origin, string theme, Action<string, bool> check)
    {
        fixture.Field.Value = 0x80336699;
        var popup = fixture.Open();
        var picker = fixture.Picker;
        var hue = Field<ColorHueSlider>(picker, "hue");
        var alpha = Field<NumericValueField>(picker, "alphaField");
        int cancelled = 0;
        void Completed(bool commit) { if (!commit) cancelled++; }
        fixture.Field.GestureCompleted += Completed;
        try
        {
            if (numeric)
            {
                if (!alpha.BeginGesture()) throw new InvalidOperationException("The opacity escape fixture could not begin editing.");
                alpha.Slider.Value = 37;
            }
            else hue.StartPointerGesture(new(hue.ActualWidth * .75, 1), () => true);
            check(theme + " " + origin + " " + (numeric ? "opacity" : "hue") + " Escape fixture has a live changed draft",
                picker.IsInteracting && fixture.Field.Value != 0x80336699);
            bool handled = Escape(Target(fixture, popup, origin));
            check(theme + " first Escape from " + origin + " cancels the live " + (numeric ? "opacity" : "hue") + " draft and keeps its window",
                handled && fixture.Field.IsPickerOpen && !picker.IsInteracting && !hue.IsPointerInteracting && !alpha.IsInteracting &&
                fixture.Field.Value == 0x80336699 && cancelled == 1);
            handled = Escape(Target(fixture, popup, origin));
            check(theme + " second Escape from " + origin + " closes the color window after cancellation",
                handled && !fixture.Field.IsPickerOpen && cancelled == 1 && fixture.Owner.IsVisible);
        }
        finally { fixture.Field.GestureCompleted -= Completed; fixture.Field.ClosePicker(); }
    }

    private static UIElement Target(Fixture fixture, Window popup, string origin) => origin switch
    {
        "caption" => (UIElement)popup.Template.FindName("TitleBar", popup)!,
        "caption button" => (UIElement)popup.Template.FindName("Close", popup)!,
        "outer border" => (UIElement)popup.Template.FindName("WindowBorder", popup)!,
        "hex" => Field<TextBox>(fixture.Picker, "hex"),
        "spectrum" => Field<ColorSpectrum>(fixture.Picker, "spectrum"),
        _ => popup
    };

    private static bool Escape(UIElement target)
    {
        var source = (HwndSource)PresentationSource.FromVisual(target);
        var key = new KeyEventArgs(new TestKeyboardDevice(), source, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(key);
        if (!key.Handled) { key.RoutedEvent = Keyboard.KeyDownEvent; target.RaiseEvent(key); }
        return key.Handled;
    }

    private sealed class Fixture : IDisposable
    {
        internal Window Owner { get; }
        internal ColorField Field { get; }
        internal ColorPickerPanel Picker => ColorPickerEscapeTests.Field<ColorPickerPanel>(Field, "picker");
        internal Fixture(string colors)
        {
            Field = new ColorField(ColorRole.Foreground, new ColorLibrary(colors)) { Width = 360 };
            Owner = new Window { Content = new Border { Padding = new(12), Child = Field }, Width = 450, Height = 150,
                Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            Owner.Show(); Owner.UpdateLayout();
        }
        internal Window Open()
        {
            Field.OpenPicker();
            var popup = Window.GetWindow(Picker) ?? throw new InvalidOperationException("The real color window was not created.");
            popup.UpdateLayout(); return popup;
        }
        public void Dispose() { Field.ClosePicker(); Owner.Close(); }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
}
