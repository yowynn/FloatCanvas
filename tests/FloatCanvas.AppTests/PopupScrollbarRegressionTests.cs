using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;

internal static class PopupScrollbarRegressionTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var activationGuard = new ActivationGuard();
        var findings = new List<(string Name, bool Passed)>();
        foreach (bool enhanced in new[] { false, true })
        {
            using var source = new HwndSource(new HwndSourceParameters("FloatCanvas.ScrollbarRegression")
            { Width = 440, Height = 100, PositionX = -10000, PositionY = -10000, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x080000A0 });
            using var scope = enhanced ? new OverlayWindowScope(source.Handle, () => true) : null;
            AppTheme.Apply("dark");
            foreach (bool fonts in new[] { false, true })
            {
                string label = $"{(enhanced ? "enhanced" : "ordinary")} {(fonts ? "font" : "generic")} dropdown";
                ComboBox combo = fonts ? (ComboBox)ToolOptionPreviews.FontPicker() : new OverlayComboBox { MaxDropDownHeight = 200 };
                if (!fonts) foreach (int number in Enumerable.Range(0, 100)) combo.Items.Add("Choice " + number);
                var root = new StackPanel(); root.Children.Add(combo);
                var outside = new Button { Content = "Outside capture target", Height = 24 }; root.Children.Add(outside);
                var window = new Window { Content=root,Width=440,Height=100,Left=-10000,Top=-10000,
                    Opacity=0,ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None };
                OverlayWindowScope.Prepare(window);window.Show();Pump();combo.ApplyTemplate();
                var popup = combo.Template.FindName("PART_Popup", combo);
                var child = popup is Popup ordinary ? ordinary.Child : ((OverlayPopup)popup).Child!;
                child.Opacity = 0;
                try
                {
                    combo.IsDropDownOpen = true; Pump();
                    var scroll = Descendants(child).OfType<ScrollViewer>().First();
                    var bar = Descendants(scroll).OfType<ScrollBar>().Single(x => x.Orientation == Orientation.Vertical);
                    var track = (Track)bar.Template.FindName("PART_Track", bar);
                    findings.Add((label + " exposes a visible scroll thumb", bar.IsVisible && track.Thumb.IsVisible));
                    // Scroll-thumb capture does not require keyboard focus. Keep this
                    // fixture nonactivating; native focus routing has separate coverage.
                    window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                        {RoutedEvent=Mouse.PreviewMouseDownEvent,Source=track.Thumb});Pump();
                    findings.Add((label+" scroll-thumb preview does not dismiss the dropdown",combo.IsDropDownOpen));
                    track.Thumb.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                        {RoutedEvent=UIElement.MouseLeftButtonDownEvent});Pump();
                    findings.Add((label + " remains open when its scroll thumb is pressed", combo.IsDropDownOpen));
                    double before = scroll.VerticalOffset;
                    if (combo.IsDropDownOpen) { track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 35)); Pump(); }
                    findings.Add((label + " scrolls while its thumb drag remains inside the popup", combo.IsDropDownOpen && scroll.VerticalOffset > before));
                    track.Thumb.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                        {RoutedEvent=UIElement.MouseLeftButtonUpEvent});Pump();
                    findings.Add((label + " remains open after its thumb releases capture", combo.IsDropDownOpen));
                    combo.IsDropDownOpen = false; Pump(); combo.IsDropDownOpen = true; Pump();
                    scroll = Descendants(child).OfType<ScrollViewer>().First();
                    bar = Descendants(scroll).OfType<ScrollBar>().Single(x => x.Orientation == Orientation.Vertical);
                    track = (Track)bar.Template.FindName("PART_Track", bar);
                    before = scroll.VerticalOffset;
                    typeof(ButtonBase).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(track.IncreaseRepeatButton,null);Pump();
                    findings.Add((label + " remains open when its page-down track is pressed", combo.IsDropDownOpen));
                    findings.Add((label + " page-down track advances the scroll position", combo.IsDropDownOpen && scroll.VerticalOffset > before));
                    Reopen();
                    var escape = new KeyEventArgs(Keyboard.PrimaryDevice, HwndSource.FromHwnd(new WindowInteropHelper(window).Handle), Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.KeyDownEvent };
                    combo.RaiseEvent(escape); Pump();
                    Console.WriteLine($"SCROLLBAR_ESCAPE {label}: handled={escape.Handled} comboOpen={combo.IsDropDownOpen} popupOpen={PopupOpen()} captured={Mouse.Captured?.GetType().Name}");
                    findings.Add((label + " Escape closes both dropdown state and popup and releases capture", !combo.IsDropDownOpen && !PopupOpen() && Mouse.Captured is null));

                    Reopen(); Mouse.Capture(combo, CaptureMode.SubTree);
                    bool outsideCaptured = Mouse.Capture(outside); Pump();
                    findings.Add((label + " outside capture transfer closes without taking capture back", outsideCaptured && ReferenceEquals(Mouse.Captured, outside) && !combo.IsDropDownOpen && !PopupOpen()));
                    Mouse.Capture(null); Pump();

                    Reopen(); Mouse.Capture(combo, CaptureMode.SubTree); Mouse.Capture(null); Pump();
                    findings.Add((label + " ordinary loss of the combo's own capture is not swallowed", Mouse.Captured is null && !combo.IsDropDownOpen && !PopupOpen()));

                    Reopen();
                    track = (Track)Descendants(child).OfType<ScrollBar>().Single(x => x.Orientation == Orientation.Vertical).Template.FindName("PART_Track", Descendants(child).OfType<ScrollBar>().Single(x => x.Orientation == Orientation.Vertical));
                    bool thumbCaptured = Mouse.Capture(track.Thumb);
                    findings.Add((label + " native capture probe starts from a captured hosted child", thumbCaptured && ReferenceEquals(Mouse.Captured, track.Thumb) && combo.IsDropDownOpen));
                    var adapterArgs = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent, Source = combo };
                    typeof(OverlayComboBox).GetMethod("OnHostedCaptureChanged", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [combo, adapterArgs]);
                    findings.Add((label + " hosted-capture adapter intercepts only the hosted popup", adapterArgs.Handled == enhanced));
                    bool nativeTransferObserved = false;
                    // Establish the real Win32-only capture state before the loss event
                    // bubbles to ComboBox; no external HWND or physical input is used.
                    MouseEventHandler transferNativeCapture = (_, args) =>
                    {
                        if (!ReferenceEquals(args.OriginalSource, track.Thumb) || Mouse.Captured is not null) return;
                        SetCapture(source.Handle);
                        nativeTransferObserved = GetCapture() == source.Handle;
                    };
                    track.Thumb.AddHandler(Mouse.LostMouseCaptureEvent, transferNativeCapture, true);
                    try { Mouse.Capture(null); Pump(); }
                    finally { track.Thumb.RemoveHandler(Mouse.LostMouseCaptureEvent, transferNativeCapture); }
                    Console.WriteLine($"SCROLLBAR_NATIVE_CAPTURE {label}: observed={nativeTransferObserved} expected={source.Handle} actual={GetCapture()} wpf={Mouse.Captured?.GetType().Name} comboOpen={combo.IsDropDownOpen}");
                    findings.Add((label + " native HWND capture is retained when WPF has no captured element", nativeTransferObserved && GetCapture() == source.Handle && Mouse.Captured is null));
                    ReleaseCapture(); combo.IsDropDownOpen = false; Pump();
                    Mouse.Capture(null); Pump();
                    GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
                    findings.Add((label + " never activates the test process", foreground != Environment.ProcessId));

                    bool PopupOpen() => popup is Popup ordinaryPopup ? ordinaryPopup.IsOpen : ((OverlayPopup)popup).IsOpen;
                    void Reopen()
                    {
                        combo.IsDropDownOpen = false; Mouse.Capture(null); ReleaseCapture(); Pump();
                        combo.IsDropDownOpen = true; Pump();
                    }
                }
                finally { ReleaseCapture(); Mouse.Capture(null); combo.IsDropDownOpen = false; window.Close(); Pump(); }
            }
        }
        foreach (var result in findings) Console.WriteLine($"SCROLLBAR_FINDING {result.Passed}: {result.Name}");
        foreach (var result in findings) check(result.Name, result.Passed);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    // The test exercises real local focus/capture without activating its offscreen windows.
    private sealed class ActivationGuard : IDisposable
    {
        private readonly HookProcedure procedure;
        private nint hook;
        internal ActivationGuard()
        {
            procedure = (code, wparam, lparam) => code == 5 ? 1 : CallNextHookEx(hook, code, wparam, lparam);
            hook = SetWindowsHookExW(5, procedure, 0, GetCurrentThreadId());
            if (hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() { if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; } }
    }
    private delegate nint HookProcedure(int code, nint wparam, nint lparam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int kind, HookProcedure procedure, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
}
