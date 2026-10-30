using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

/// <summary>Production pointer and keyboard routes on an isolated desktop; no physical input injection.</summary>
internal static class NumericPointerRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        CaptureReentrancy(check);
        var field = new NumericValueField(0, 100) { Value = 50, Width = 240, Unit = "px" };
        var label = new TextBlock { Text = "Width" }; field.SetLabel(label);
        var window = new Window { Content = new StackPanel { Margin = new Thickness(20), Children = { label, field } },
            Width = 300, Height = 130, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 40, Top = 40 };
        var results = new List<(string Name, bool Passed)>();
        void Record(string name, bool passed)
        {
            Console.WriteLine($"POINTER {name}: {passed}; value={field.Value}; focus={Keyboard.FocusedElement?.GetType().Name}");
            results.Add((name, passed));
        }
        try
        {
            window.Show(); window.Activate(); Pump();
            field.Value = 50; field.Input.Focus(); Pump();
            Click(field.DragAnchor, new(12, 8));
            Record("clicking the drag icon retains slider keyboard focus", field.Slider.IsKeyboardFocused && !field.Input.IsKeyboardFocused);
            SendKey(Key.Right); Record("Right after an icon click increases by one", field.Value == 51);
            SendKey(Key.Left); Record("Left after an icon click decreases by one", field.Value == 50);

            field.Input.Focus(); Pump();
            var frame = (Border)field.Content;
            Click(frame, new(frame.ActualWidth - 1, 1));
            Record("clicking the frame outside text retains slider focus", field.Slider.IsKeyboardFocused && !field.Input.IsKeyboardFocused);
            field.Input.Focus(); Pump();
            var panel = (Grid)frame.Child;
            var editor = (Grid)panel.Children[0];
            Click(editor, new(editor.ActualWidth - 4, editor.ActualHeight / 2));
            Record("clicking the unit region retains slider focus", field.Slider.IsKeyboardFocused && !field.Input.IsKeyboardFocused);
            field.Input.Focus(); Pump(); Click(label, new(12, 6));
            Record("clicking the associated caption retains slider focus", field.Slider.IsKeyboardFocused && !field.Input.IsKeyboardFocused);

            Click(field.Input, new(field.Input.ActualWidth / 2, field.Input.ActualHeight / 2));
            field.Input.CaretIndex = 1; double inputValue = field.Value;
            SendKey(Key.Left);
            Record("direct text input keeps caret navigation and its numeric value", field.Input.IsKeyboardFocused && field.Input.CaretIndex == 0 && field.Value == inputValue);

            var session = new EditorSession();
            session.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 60 });
            var inspector = new InspectorWindow(() => session);
            try
            {
                inspector.Show(); inspector.Activate(); Pump();
                var numbers = (Dictionary<string, NumericValueField>)typeof(InspectorWindow).GetField("numericFields", PrivateInstance)!.GetValue(inspector)!;
                var width = numbers["W"];
                width.Input.Focus(); Pump(); Click(width.DragAnchor, new(12, 8));
                Record("inspector geometry icon retains its associated slider focus", width.Slider.IsKeyboardFocused);
                double previousWidth = width.Value;
                SendKey(Key.Right);
                Record("Right after inspector geometry icon changes the layer width", width.Value == previousWidth + 1 && session.Document.Elements[0].Width == previousWidth + 1);
            }
            finally
            {
                inspector.CompletePendingEdit();
                inspector.Closing += (_, e) => e.Cancel = false;
                inspector.Close(); Pump();
            }
        }
        finally { field.CancelGesture(); window.Close(); Pump(); }
        foreach (var result in results) check(result.Name, result.Passed);
    }

    private static void CaptureReentrancy(Action<string, bool> check)
    {
        var field = new NumericValueField(1, 64, integerOnly: true) { Value = 6, Width = 120, VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Content = new StackPanel { Margin = new Thickness(20), Children = { field } },
            Width = 180, Height = 120, ShowInTaskbar = false, Left = 40, Top = 40 };
        var changes = new List<double>(); field.ValueChanged += changes.Add;
        var results = new List<(string Name, bool Passed)>();
        try
        {
            window.Show(); window.Activate(); Pump();
            Point Edge()
            {
                window.UpdateLayout();
                var thumb = ((Track)field.Slider.Template.FindName("PART_Track", field.Slider)).Thumb;
                return thumb.TranslatePoint(new(thumb.ActualWidth - 1, thumb.ActualHeight / 2), field.Slider);
            }
            var point = Edge();
            field.StartRailGesture(point, () =>
            {
                // WPF synchronizes the pointer during mouse capture and can route a
                // MouseMove before CaptureMouse returns to the button-down handler.
                Pointer(field.Slider, point, Mouse.PreviewMouseMoveEvent);
                return true;
            });
            bool unchangedAtPress = field.IsInteracting && field.Value == 6 && changes.Count == 0;
            Pointer(field.Slider, point, Mouse.PreviewMouseUpEvent);
            Console.WriteLine("CAPTURE thumb click values=" + string.Join(",", changes));
            results.Add(("capture-time stationary MouseMove never previews a thumb click offset", unchangedAtPress));
            results.Add(("thumb click and release emits no transient value changes", field.Value == 6 && changes.Count == 0 && !field.IsInteracting));

            field.Value = 6; changes.Clear(); point = Edge();
            field.StartRailGesture(point, () =>
            {
                Pointer(field.Slider, point, Mouse.PreviewMouseMoveEvent);
                return false;
            });
            Console.WriteLine("CAPTURE rejected thumb values=" + string.Join(",", changes));
            results.Add(("rejected capture emits no transient thumb value before cancellation", field.Value == 6 && changes.Count == 0 && !field.IsInteracting));

            field.Value = 6; changes.Clear(); point = Edge();
            var outside = new Point(field.Slider.ActualWidth - 4, point.Y);
            field.StartRailGesture(outside, () =>
            {
                Pointer(field.Slider, outside, Mouse.PreviewMouseMoveEvent);
                return true;
            });
            results.Add(("capture-time MouseMove still previews an empty-track press", field.Value == 64 && changes.SequenceEqual([64d])));
            Pointer(field.Slider, outside, Mouse.PreviewMouseUpEvent);
            results.Add(("empty-track release retains its clicked value", field.Value == 64 && !field.IsInteracting));

            field.Value = 6; changes.Clear(); point = Edge();
            outside = new Point(field.Slider.ActualWidth - 4, point.Y);
            var latest = new Point(field.Slider.ActualWidth * .55, point.Y);
            double latestValue = Math.Round(1 + 63 * (latest.X - 4) / (field.Slider.ActualWidth - 8));
            field.StartRailGesture(outside, () =>
            {
                Pointer(field.Slider, latest, Mouse.PreviewMouseMoveEvent);
                return true;
            });
            results.Add(("capture-time track movement retains its latest position after capture returns", field.Value == latestValue));
            Pointer(field.Slider, latest, Mouse.PreviewMouseUpEvent);
            results.Add(("track release preserves the latest capture-time movement", field.Value == latestValue && !field.IsInteracting));

            field.Value = 6; changes.Clear(); point = Edge();
            field.StartRailGesture(point, () => { field.CancelGesture(); return true; });
            bool moveHandled = Pointer(field.Slider, point, Mouse.PreviewMouseMoveEvent);
            Pointer(field.Slider, point, Mouse.PreviewMouseUpEvent);
            results.Add(("capture-time cancellation does not revive rail routing after capture returns", !field.IsInteracting && changes.Count == 0 && !moveHandled));
        }
        finally { field.CancelGesture(); window.Close(); Pump(); }
        foreach (var result in results)
        {
            Console.WriteLine($"CAPTURE {result.Name}: {result.Passed}");
            check(result.Name, result.Passed);
        }
    }

    private static void Click(UIElement target, Point point)
    {
        Pointer(target, point, Mouse.PreviewMouseDownEvent); Pump();
        Pointer(target, point, Mouse.PreviewMouseUpEvent); Pump();
    }

    private static bool Pointer(UIElement target, Point point, RoutedEvent routedEvent)
    {
        var source = (HwndSource)PresentationSource.FromVisual(target);
        var root = (UIElement)source.RootVisual;
        var mouse = (MouseDevice)typeof(ToolOptionsInputFixture).GetMethod("ControlledMouse", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [target])!;
        try
        {
            typeof(MouseDevice).GetField("_inputSource", PrivateInstance)!.SetValue(mouse, source);
            typeof(MouseDevice).GetField("_lastPosition", PrivateInstance)!.SetValue(mouse,
                source.CompositionTarget.TransformToDevice.Transform(target.TranslatePoint(point, root)));
            InputEventArgs input = routedEvent == Mouse.PreviewMouseMoveEvent
                ? new MouseEventArgs(mouse, Environment.TickCount) { RoutedEvent = routedEvent }
                : new MouseButtonEventArgs(mouse, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent };
            InputManager.Current.ProcessInput(input);
            return input.Handled;
        }
        finally { ToolOptionsInputFixture.ReleaseControlledMouse(mouse); }
    }

    private static void SendKey(Key key)
    {
        var target = (UIElement)Keyboard.FocusedElement;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key)
            { RoutedEvent = Keyboard.KeyDownEvent });
        Pump();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
