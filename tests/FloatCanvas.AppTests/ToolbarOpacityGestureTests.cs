using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;

/// <summary>Real toolbar/session callbacks on a screen-external test window; no physical input.</summary>
internal static class ToolbarOpacityGestureTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // Call after the test runner configures its isolated temporary profile.
    internal static void Run(Action<string, bool> check)
    {
        string isolatedDesktop = DesktopName();
        if (!isolatedDesktop.StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Run toolbar opacity gesture checks with --isolated-ui-desktop.");
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var bounds = new PixelBounds(-20000, -20000, 64, 64);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("toolbar-opacity-gesture", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
            [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", Private)!;
        ((IDisposable?)isolation.GetValue(host))?.Dispose(); isolation.SetValue(host, null);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        var state = Field<EditorController>(desktop, "_editor").Session;
        var slider = Field<ToolbarOpacitySlider>(desktop, "_opacitySlider");
        var parent = (Panel)slider.Parent;
        int index = parent.Children.IndexOf(slider);
        parent.Children.Remove(slider);
        var surface = new Border { Padding = new Thickness(12), Child = slider };
        var window = new Window { Title = "FloatCanvas toolbar opacity gesture fixture", Content = surface,
            Width = 104, Height = 74, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        void Synchronize(double value)
        {
            var flag = typeof(DesktopSession).GetField("_syncingControls", Private)!;
            bool previous = (bool)flag.GetValue(desktop)!;
            flag.SetValue(desktop, true);
            try { slider.Value = value; }
            finally { flag.SetValue(desktop, previous); }
        }
        Point Position(double fraction)
        {
            window.UpdateLayout();
            var track = (Track)slider.Template.FindName("PART_Track", slider);
            return track.TranslatePoint(new(track.Thumb.ActualWidth / 2 + fraction * (track.ActualWidth - track.Thumb.ActualWidth),
                track.ActualHeight / 2), slider);
        }
        Point ThumbEdge()
        {
            window.UpdateLayout();
            var thumb = ((Track)slider.Template.FindName("PART_Track", slider)).Thumb;
            return thumb.TranslatePoint(new(thumb.ActualWidth - 1, thumb.ActualHeight / 2), slider);
        }
        double ExpectedAt(Point point)
        {
            var track = (Track)slider.Template.FindName("PART_Track", slider);
            double start = track.TranslatePoint(new(track.Thumb.ActualWidth / 2, 0), slider).X;
            return Math.Clamp((point.X - start) / Math.Max(1, track.ActualWidth - track.Thumb.ActualWidth), 0, 1);
        }
        try
        {
            window.Show(); window.UpdateLayout(); Pump();
            state.Document.LayerOpacity = .5; Synchronize(.5);
            int originalUndo = state.UndoCount;
            long revision = state.PreviewRevision;
            var requestedPress = Position(.2);
            var press = Pointer(slider, requestedPress, Mouse.PreviewMouseMoveEvent);
            slider.StartPointerGesture(press, () => true);
            check("toolbar opacity track press updates the real document inside one pending transaction",
                state.IsTransactionActive && state.UndoCount == originalUndo && Near(state.Document.LayerOpacity, ExpectedAt(press)));
            var requestedMiddle = Position(.35);
            var middle = Pointer(slider, requestedMiddle, Mouse.PreviewMouseMoveEvent);
            check("toolbar opacity routed movement previews its intermediate value before release",
                state.IsTransactionActive && state.UndoCount == originalUndo && Near(state.Document.LayerOpacity, ExpectedAt(middle)));
            var requestedEnd = Position(.8);
            var end = Pointer(slider, requestedEnd, Mouse.PreviewMouseMoveEvent);
            check("toolbar opacity live movement advances the real preview revision without committing history",
                state.IsTransactionActive && state.PreviewRevision > revision && state.UndoCount == originalUndo && Near(state.Document.LayerOpacity, ExpectedAt(end)));
            var release = Pointer(slider, requestedEnd, Mouse.PreviewMouseUpEvent);
            double finalOpacity = state.Document.LayerOpacity;
            check("toolbar opacity release commits the entire real gesture once",
                !state.IsTransactionActive && state.UndoCount == originalUndo + 1 && Near(finalOpacity, ExpectedAt(release)));
            slider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
            check("capture loss after toolbar opacity release does not commit a second time", state.UndoCount == originalUndo + 1);
            check("one undo restores opacity before the drag", state.Undo() && Near(state.Document.LayerOpacity, .5));
            check("one redo restores opacity after the drag", state.Redo() && Near(state.Document.LayerOpacity, finalOpacity));

            Synchronize(state.Document.LayerOpacity);
            var changes = new List<double>();
            slider.ValueChanged += (_, _) => changes.Add(slider.Value);
            int beforeClick = state.UndoCount;
            var requestedEdge = ThumbEdge();
            var edge = Pointer(slider, requestedEdge, Mouse.PreviewMouseMoveEvent);
            slider.StartPointerGesture(edge, () => true);
            Pointer(slider, requestedEdge, Mouse.PreviewMouseUpEvent);
            check("an off-center toolbar thumb click creates no transient document change or undo item",
                changes.Count == 0 && Near(state.Document.LayerOpacity, finalOpacity) && state.UndoCount == beforeClick && !state.IsTransactionActive);

            var requestedLossStart = Position(slider.Value);
            var lossStart = Pointer(slider, requestedLossStart, Mouse.PreviewMouseMoveEvent);
            double beforeLoss = slider.Value;
            var lossTrack = (Track)slider.Template.FindName("PART_Track", slider);
            double lossTravel = Math.Max(1, lossTrack.ActualWidth - lossTrack.Thumb.ActualWidth);
            slider.StartPointerGesture(lossStart, () => true);
            var requestedLossMove = Position(.45);
            var lossMove = Pointer(slider, requestedLossMove, Mouse.PreviewMouseMoveEvent);
            double expectedLoss = Math.Clamp(beforeLoss + (lossMove.X - lossStart.X) / lossTravel, 0, 1);
            check("toolbar opacity preview remains pending until capture actually ends",
                state.IsTransactionActive && state.UndoCount == beforeClick && Near(state.Document.LayerOpacity, expectedLoss));
            slider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
            check("toolbar opacity capture loss preserves the existing commit-current-preview policy",
                !state.IsTransactionActive && state.UndoCount == beforeClick + 1 && Near(state.Document.LayerOpacity, expectedLoss));
            slider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
            Pointer(slider, requestedLossMove, Mouse.PreviewMouseUpEvent);
            check("repeated capture loss and late release do not duplicate the toolbar opacity edit", state.UndoCount == beforeClick + 1);
            check("capture-loss opacity edits remain reversible as one action", state.Undo() && Near(state.Document.LayerOpacity, finalOpacity));

            int undo = state.UndoCount, redo = state.RedoCount;
            double modelOpacity = state.Document.LayerOpacity;
            Synchronize(.123);
            check("toolbar control synchronization does not change the real document or history",
                Near(state.Document.LayerOpacity, modelOpacity) && state.UndoCount == undo && state.RedoCount == redo && !state.IsTransactionActive);
            Synchronize(modelOpacity);

            var requestedCancelStart = Position(slider.Value);
            var cancelStart = Pointer(slider, requestedCancelStart, Mouse.PreviewMouseMoveEvent);
            slider.StartPointerGesture(cancelStart, () => true);
            var requestedBeforeCancel = Position(.2);
            Pointer(slider, requestedBeforeCancel, Mouse.PreviewMouseMoveEvent);
            check("toolbar opacity has a pending live preview before global Escape",
                state.IsTransactionActive && !Near(state.Document.LayerOpacity, modelOpacity) && state.UndoCount == undo);
            typeof(DesktopSession).GetMethod("OnKey", Private)!.Invoke(desktop, [0x1B]);
            check("global Escape restores the toolbar opacity gesture without adding undo history",
                !state.IsTransactionActive && Near(state.Document.LayerOpacity, modelOpacity) && state.UndoCount == undo && state.RedoCount == redo);
            foreach (double fraction in new[] { .65, .1 })
                Pointer(slider, Position(fraction), Mouse.PreviewMouseMoveEvent);
            Pointer(slider, Position(.1), Mouse.PreviewMouseUpEvent);
            check("late toolbar opacity moves and release after Escape cannot reapply a cancelled gesture",
                !state.IsTransactionActive && Near(state.Document.LayerOpacity, modelOpacity) && Near(slider.Value, modelOpacity) &&
                state.UndoCount == undo && state.RedoCount == redo);
            check("toolbar opacity integration remains on its isolated desktop with screen-external native surfaces",
                DesktopName() == isolatedDesktop && window.Left < -10000 && window.Top < -10000 && !window.ShowActivated &&
                host.Diagnostics.All(pair => pair.Bounds.Right < -10000 && pair.Bounds.Bottom < -10000));
        }
        finally
        {
            if (state.IsTransactionActive) state.Cancel();
            surface.Child = null;
            window.Close();
            parent.Children.Insert(index, slider);
            Pump();
        }
    }

    private static bool Near(double first, double second) => Math.Abs(first - second) < 1e-8;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;

    private static Point Pointer(UIElement target, Point point, RoutedEvent routedEvent)
    {
        var source = (HwndSource)PresentationSource.FromVisual(target);
        var root = (UIElement)source.RootVisual;
        var mouse = (MouseDevice)typeof(ToolOptionsInputFixture).GetMethod("ControlledMouse", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [target])!;
        Point? observed = null;
        MouseEventHandler observeMove = (_, args) => observed = args.GetPosition(target);
        MouseButtonEventHandler observeButton = (_, args) => observed = args.GetPosition(target);
        Delegate observer = routedEvent == Mouse.PreviewMouseMoveEvent ? observeMove : observeButton;
        target.AddHandler(routedEvent, observer, true);
        try
        {
            typeof(MouseDevice).GetField("_inputSource", Private)!.SetValue(mouse, source);
            typeof(MouseDevice).GetField("_lastPosition", Private)!.SetValue(mouse,
                source.CompositionTarget.TransformToDevice.Transform(target.TranslatePoint(point, root)));
            InputEventArgs input = routedEvent == Mouse.PreviewMouseMoveEvent
                ? new MouseEventArgs(mouse, Environment.TickCount) { RoutedEvent = routedEvent }
                : new MouseButtonEventArgs(mouse, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent };
            InputManager.Current.ProcessInput(input);
            Point actual = observed ?? ((MouseEventArgs)input).GetPosition(target);
            return actual;
        }
        finally
        {
            target.RemoveHandler(routedEvent, observer);
            ToolOptionsInputFixture.ReleaseControlledMouse(mouse);
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static string DesktopName()
    {
        var name = new StringBuilder(256);
        return GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _) ? name.ToString() : "";
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int length, out int needed);
}
