using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class GridCountPickerTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string language = L.Preference;
        try
        {
            L.Initialize("en"); AppTheme.Apply("light"); Stepper(check);
            foreach (string locale in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(locale); AppTheme.Apply(theme); Dialog(check, locale + "-" + theme, output);
            }
        }
        finally { L.Initialize(language); AppTheme.Apply("light"); }
    }

    private static void Stepper(Action<string, bool> check)
    {
        var plain = new NumericValueField(1, 256, integerOnly: true) { Value = 3, Unit = "cells" };
        var stepped = new NumericValueField(1, 256, integerOnly: true) { Value = 3, Unit = "cells" }; stepped.EnableStepper();
        var panel = new StackPanel(); panel.Children.Add(plain); panel.Children.Add(stepped);
        var window = new Window { Content = panel, Width = 220, SizeToContent = SizeToContent.Height, ShowInTaskbar = false };
        window.Show(); Pump();
        try
        {
            var buttons = Descendants<Button>(stepped).ToArray();
            check("optional stepper leaves ordinary numeric fields unchanged", !Descendants<Button>(plain).Any() && buttons.Length == 2);
            check("stepper keeps field height and outer width", stepped.ActualHeight == plain.ActualHeight && stepped.ActualWidth == plain.ActualWidth);
            check("stepper reserves space for the complete editor and rail", Math.Abs(plain.Slider.ActualWidth - stepped.Slider.ActualWidth - 22) < .1 &&
                Math.Abs(plain.Input.ActualWidth - stepped.Input.ActualWidth - 22) < .1 && stepped.Unit == plain.Unit);
            check("stepper arrows stack within the right of the numeric field", buttons.All(b => b.ActualHeight <= 17) &&
                buttons[0].TranslatePoint(default, stepped).Y < buttons[1].TranslatePoint(default, stepped).Y &&
                buttons[0].TranslatePoint(default, stepped).X >= stepped.Slider.TranslatePoint(default, stepped).X + stepped.Slider.ActualWidth);
            int started = 0, completed = 0, committed = 0;
            stepped.GestureStarted += () => started++; stepped.GestureCompleted += accept => { if (accept) completed++; };
            stepped.SliderValueCommitted += () => committed++;
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("each arrow is one balanced numeric gesture", stepped.Value == 3 && started == 2 && completed == 2 && committed == 2);
            stepped.Value = 1; check("lower bound disables decrement", !buttons[1].IsEnabled && buttons[0].IsEnabled);
            stepped.Value = 256; check("upper bound disables increment", !buttons[0].IsEnabled && buttons[1].IsEnabled);
            stepped.Input.Text = "invalid"; check("invalid draft disables both arrows", buttons.All(b => !b.IsEnabled));
            stepped.Input.Text = "5"; stepped.Input.IsReadOnly = true; Pump();
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("read-only fields reject stepping", buttons.All(b => !b.IsEnabled) && stepped.Value == 5);
            stepped.Input.IsReadOnly = false; stepped.CanBeginGesture = () => false;
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("stepper honors the existing gesture permission gate", stepped.Value == 5 && !stepped.IsInteracting);
        }
        finally { window.Close(); }
    }

    private static void Dialog(Action<string, bool> check, string prefix, string? output)
    {
        var owner = new Window { Width = 80, Height = 80, ShowInTaskbar = false }; owner.Show();
        var settings = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 800, 600)) };
        var draft = new CanvasGridCalibrationDraft { Columns = "4", Rows = "5" };
        CanvasSettings? preview = null; int candidates = 0;
        var dialog = new CanvasGridCalibrationWindow(owner, settings, new(.1, .1, .4, .5), draft, value => { preview = value; if (value is not null) candidates++; });
        dialog.Show(); Pump();
        try
        {
            var numbers = Descendants<NumericValueField>(dialog).ToArray();
            var picker = Descendants<GridCountPicker>(dialog).Single();
            var reset = Descendants<Button>(dialog).Single(b => AutomationProperties.GetName(b) == L.Get("Canvas.AutomaticGridCandidate"));
            check(prefix + " paired count fields put icon actions in the aspect-ratio slot", numbers.Length == 4 &&
                ReferenceEquals(numbers[0].Parent, numbers[1].Parent) && Grid.GetColumn(picker) == 1 &&
                ReferenceEquals(numbers[2].Parent, numbers[3].Parent) && Grid.GetColumn(reset) == 1 &&
                picker.ActualHeight == 34 && reset.ActualWidth == 34 &&
                numbers.All(n => n.Unit == L.Get("Numeric.Unit.Cell")));
            check(prefix + " only total fields contain steppers", numbers.Take(2).All(n => !Descendants<Button>(n).Any()) && numbers.Skip(2).All(n => Descendants<Button>(n).Count() == 2));
            picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var cells = Descendants<Button>(picker.PickerContent).ToArray();
            check(prefix + " grid picker opens 81 cells", picker.IsPickerOpen && cells.Length == 81);
            var chosen = cells.Single(b => Equals(b.Tag, (5, 3)));
            int before = candidates; var previous = preview;
            chosen.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent }); Pump();
            check(prefix + " hovering never changes counts or candidate", draft.Columns == "4" && draft.Rows == "5" && candidates == before && ReferenceEquals(previous, preview));
            chosen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check(prefix + " selection publishes both counts as one candidate and closes", draft.Columns == "5" && draft.Rows == "3" && candidates == before + 1 && !picker.IsPickerOpen);
            picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            using (var modifiers = new ThreadModifierScope(ModifierKeys.None))
            {
                var right = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(chosen)!, 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                chosen.RaiseEvent(right); Pump();
                var next = cells.Single(b => Equals(b.Tag, (6, 3)));
                check(prefix + " arrow navigation previews without editing the draft", right.Handled && next.IsKeyboardFocused && draft.Columns == "5" && draft.Rows == "3");
                next.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(next)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent }); Pump();
                check(prefix + " Enter selects the focused cell without applying calibration", !picker.IsPickerOpen && draft.Columns == "6" && draft.Rows == "3" && dialog.IsVisible && dialog.Result is null);
            }
            picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var captured = Mouse.Captured as UIElement;
            if (captured is not null)
            {
                // WPF Popup dismisses on the regular preview press routed through PopupRoot,
                // then hit-tests its position. The OutsideCapturedElement notification alone
                // is not that press, and using the real mouse makes the position machine-dependent.
                var source = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(picker.PickerContent)!;
                check(prefix + " outside press targets this picker's captured popup root", ReferenceEquals(captured, source.RootVisual));
                var mouse = ToolOptionsInputFixture.ControlledMouse(captured);
                try
                {
                    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    typeof(MouseDevice).GetField("_inputSource", flags)!.SetValue(mouse, source);
                    typeof(MouseDevice).GetField("_lastPosition", flags)!.SetValue(mouse, new Point(-20, -20));
                    var press = new MouseButtonEventArgs(mouse, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
                    check(prefix + " outside press has a controlled point outside the popup", captured.InputHitTest(press.GetPosition(captured)) is null);
                    InputManager.Current.ProcessInput(press); Pump();
                }
                finally { ToolOptionsInputFixture.ReleaseControlledMouse(mouse); }
                check(prefix + " outside press dismisses without editing or closing the dialog", !picker.IsPickerOpen && draft.Columns == "6" && dialog.IsVisible);
            }
            else
            {
                var handle = (PopupHandle)typeof(GridCountPicker).GetField("popup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(picker)!;
                check(prefix + " picker uses the shared outside-dismissal policy", !handle.StaysOpen);
                Console.WriteLine("SKIP native outside-click delivery: isolated desktop did not acquire mouse capture.");
                picker.ClosePicker();
            }
            numbers[0].Input.Text = "12"; Pump(); picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check(prefix + " typed counts above nine survive opening", draft.Columns == "12" && numbers[0].Value == 12);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            dialog.RaiseEvent(escape); Pump();
            check(prefix + " Escape closes the picker without canceling calibration", escape.Handled && !picker.IsPickerOpen && dialog.IsVisible && dialog.Result is null);
            numbers[0].Input.Text = "4"; numbers[1].Input.Text = "5"; Pump();
            Descendants<Button>(numbers[2]).First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check(prefix + " stepper edits the candidate and enables middle reset", draft.CandidateColumns == "11" && reset.IsEnabled && preview is not null);
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check(prefix + " reset restores both automatic candidates", draft.CandidateColumns is null && draft.CandidateRows is null && numbers[2].Value == 10 && !reset.IsEnabled);
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                foreach (double scale in new[] { 1d, 1.5, 2d })
                {
                    VisualTreeHelper.SetRootDpi(dialog, new(scale, scale)); dialog.UpdateLayout(); Pump();
                    check(prefix + " fields and controls remain aligned at " + scale, numbers.All(n => Math.Abs(n.ActualHeight - 34) < .1) &&
                        Math.Abs(numbers[2].TranslatePoint(default, dialog).Y - reset.TranslatePoint(default, dialog).Y) < .1);
                    Save(dialog, Path.Combine(output, $"calibration-{prefix}-{scale:0.0}.png"), scale);
                }
                picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Save((FrameworkElement)picker.PickerContent, Path.Combine(output, $"picker-{prefix}.png"), 2);
                picker.ClosePicker();
            }
            picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); dialog.Hide(); Pump();
            check(prefix + " hiding the owner closes its picker", !picker.IsPickerOpen);
        }
        finally { dialog.Close(); owner.Close(); }
    }

    private static void Save(FrameworkElement view, string path, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth * scale), (int)Math.Ceiling(view.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
}
