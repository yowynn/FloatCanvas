using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;
using FloatCanvas.Core;
using System.IO;

internal static class Phase2ColorWindowTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        CheckSessionAnchors(check);
        CheckContentSizedPlacement(check);
        foreach (bool enhanced in new[] { false, true })
        {
            var owner = new Window { Title = "Phase2 color owner", Width = 500, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = SystemParameters.PrimaryScreenWidth / 2 - 250, Top = 80 };
            using var scope = enhanced ? new OverlayWindowScope(new WindowInteropHelper(owner).EnsureHandle(), () => true) : null;
            var ownerPin = new Button { Content = "Owner pin" };
            AppWindowChrome.SetHeaderActions(owner, ownerPin);
            AppWindowChrome.SetHelp(owner, "Owner help", "Help belongs to the source window.");
            AppWindowChrome.Apply(owner);
            var field = new ColorField(ColorRole.Foreground);
            var outside = new Button { Content = "Source action" };
            var body = new StackPanel(); body.Children.Add(field); body.Children.Add(outside);
            owner.Content = body; owner.Show(); Pump(); field.OpenPicker(); Pump();
            try
            {
                var color = Window.GetWindow(field.PickerRoot)!;
                check("color picker uses a real owned WPF window for enhanced=" + enhanced, color is not null && color != owner && color.Owner == owner);
                var button = (Button)typeof(ColorField).GetField("button", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(field)!;
                var anchor = button.PointToScreen(new Point(0, button.ActualHeight));
                double scale = System.Windows.Media.VisualTreeHelper.GetDpi(button).DpiScaleY;
                GetWindowRect(new WindowInteropHelper(color!).Handle, out var bounds);
                var work = System.Windows.Forms.Screen.FromPoint(new((int)anchor.X, (int)anchor.Y)).WorkingArea;
                check($"color window opens below its actual source button for enhanced={enhanced}: anchor={anchor}, window={bounds.Left},{bounds.Top}",
                    Math.Abs(bounds.Left - Math.Clamp(anchor.X, work.Left, work.Right - (bounds.Right - bounds.Left))) <= 2 &&
                    Math.Abs(bounds.Top - Math.Clamp(anchor.Y + 4 * scale, work.Top, work.Bottom - (bounds.Bottom - bounds.Top))) <= 2);
                check("color window provides ordinary caption controls", color!.Template.FindName("TitleBar", color) is FrameworkElement &&
                    color.Template.FindName("Close", color) is Button && !string.IsNullOrEmpty(color.Title));
                check("source caption actions remain in the source window", Window.GetWindow(ownerPin) == owner);
                check("a color window does not inherit source help or header actions", !color.Resources.Contains("CanvasWindowActions") &&
                    Equals(color.TryFindResource("CanvasWindowHelpVisibility"), Visibility.Collapsed));
                using (var retention = PopupChildDialog.RetainFor(field.PickerRoot))
                    check("child-dialog retention keeps the regular color window open", retention is not null && field.IsPickerOpen);
                var firstChild = PopupChildDialog.RetainFor(field.PickerRoot)!;
                var secondChild = PopupChildDialog.RetainFor(field.PickerRoot)!;
                firstChild.Dispose();
                check("closing a menu before its naming child keeps the shared parent retained", PopupChildDialog.HasRetainedDialog(field.PickerRoot));
                secondChild.Dispose();
                check("closing the last child releases parent retention", !PopupChildDialog.HasRetainedDialog(field.PickerRoot));
                var picker = (ColorPickerPanel)typeof(ColorField).GetField("picker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(field)!;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                bool sawModal = false;
                timer.Tick += (_, _) =>
                {
                    var dialog = Application.Current!.Windows.Cast<Window>().FirstOrDefault(value => value.IsVisible && value.Owner == color);
                    if (dialog is null) return;
                    sawModal = !IsWindowEnabled(new WindowInteropHelper(color).Handle) && field.IsPickerOpen;
                    timer.Stop(); dialog.DialogResult = false;
                };
                timer.Start();
                try { typeof(ColorPickerPanel).GetMethod("SaveSwatch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, null); }
                finally { timer.Stop(); }
                check("the actual naming modal belongs to and retains the color window", sawModal && field.IsPickerOpen && color.IsEnabled);
                bool closedBeforePress = false;
                outside.PreviewMouseDown += (_, _) => closedBeforePress = !field.IsPickerOpen;
                typeof(ColorPopupOwnershipTests).GetMethod("Press", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [outside]);
                check("a source-window press closes the secondary window before the source handles it", closedBeforePress && !field.IsPickerOpen);
                field.OpenPicker(); Pump(); color = Window.GetWindow(field.PickerRoot)!;
                color.Close(); Pump();
                check("closing the caption closes the color-picker session", !field.IsPickerOpen);
                field.OpenPicker(); Pump(); owner.Hide(); Pump();
                check("hiding a source window closes its color child", !field.IsPickerOpen);
            }
            finally { field.ClosePicker(); owner.Close(); }
        }
    }
    private static void CheckSessionAnchors(Action<string, bool> check)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!;
        var monitor = new MonitorInfo(screen.DeviceName, new(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height),
            new(screen.WorkingArea.Left, screen.WorkingArea.Top, screen.WorkingArea.Width, screen.WorkingArea.Height),
            (uint)Math.Round(screen.Bounds.Width * 96d / SystemParameters.PrimaryScreenWidth), (uint)Math.Round(screen.Bounds.Height * 96d / SystemParameters.PrimaryScreenHeight), true);
        string directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-ColorAnchor-" + Guid.NewGuid().ToString("N"));
        foreach (bool enhanced in new[] { false, true })
        foreach (string source in new[] { "Canvas", "Inspector", "Tool options" })
        {
            Window owner; KeyValuePair<string, ColorField>[] colors;
            var library = new ColorLibrary(Path.Combine(directory, source + ".json"));
            if (source == "Canvas")
            {
                owner = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
                colors = new[] { "background", "gridColor" }.Select(name => KeyValuePair.Create(name, Field<ColorField>(owner, name))).ToArray();
            }
            else if (source == "Inspector")
            {
                var session = new EditorSession(); session.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60,
                    Style = new() { LinkColors = false, Stroke = 0xFF123456, Fill = 0x408090A0 } });
                var inspector = new InspectorWindow(() => session, colorLibrary: library); inspector.PrepareForShow(); owner = inspector;
                Field<Button>(inspector, "pin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                colors = Field<Dictionary<string, ColorField>>(owner, "colorFields").Where(pair => pair.Key == "Stroke").ToArray();
            }
            else
            {
                var controller = new EditorController(new EditorSession()) { Tool = EditorTool.Rectangle };
                controller.Style.LinkColors = false;
                var panel = new ToolOptionsPanel(controller, new ToolPresetLibrary(Path.Combine(directory, "presets.json")), library); panel.PrepareForShow();
                owner = new Window { Title = "Anchored Tool options", Content = panel, Width = 700, Height = 590, ShowInTaskbar = false };
                AppWindowChrome.Apply(owner);
                colors = [KeyValuePair.Create("foreground", Field<ColorField>(panel, "foreground"))];
            }
            owner.ShowActivated = false; owner.WindowStartupLocation = WindowStartupLocation.Manual;
            owner.Left = SystemParameters.PrimaryScreenWidth / 2 - 250; owner.Top = 80;
            var layout = new SessionWindowLayout(); layout.TrackEditing(owner, () => [monitor]);
            using var scope = enhanced ? new OverlayWindowScope(new WindowInteropHelper(owner).EnsureHandle(), () => true) : null;
            try
            {
                SessionWindowCoordinator.Show(owner, activate: false); Pump();
                foreach (string position in new[] { "initial", "moved", "right edge", "bottom edge" })
                {
                    GetWindowRect(new WindowInteropHelper(owner).Handle, out var ownerBounds);
                    if (position != "initial") OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(owner).Handle,
                        new(position == "moved" ? ownerBounds.Left - 240 : monitor.WorkArea.Right - (ownerBounds.Right - ownerBounds.Left),
                            position == "bottom edge" ? monitor.WorkArea.Bottom - (ownerBounds.Bottom - ownerBounds.Top) : ownerBounds.Top + 20));
                    Pump();
                    foreach (var pair in colors)
                    {
                        var field = pair.Value; var button = Field<Button>(field, "button");
                        button.BringIntoView(); Pump();
                        var anchor = button.PointToScreen(new Point(0, button.ActualHeight));
                        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(button).DpiScaleY;
                        NativeRect? first = null;
                        void SourceChanged(object sender, SourceChangedEventArgs args)
                        {
                            if (args.NewSource is HwndSource native) native.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
                            {
                                if (first is null && message is 0x18 or 0x47 && IsWindowVisible(hwnd) && GetWindowRect(hwnd, out var shown)) first = shown;
                                return 0;
                            });
                        }
                        PresentationSource.AddSourceChangedHandler((UIElement)field.PickerRoot, SourceChanged);
                        field.OpenPicker(); Pump();
                        PresentationSource.RemoveSourceChangedHandler((UIElement)field.PickerRoot, SourceChanged);
                        check($"{source} {pair.Key} {position}, enhanced={enhanced}: visible source opens a picker (visible={field.IsVisible}, open={field.IsPickerOpen})", field.IsVisible && field.IsPickerOpen);
                        var color = Window.GetWindow(field.PickerRoot)!;
                        GetWindowRect(new WindowInteropHelper(color).Handle, out var bounds);
                        double expectedX = Math.Clamp(anchor.X, monitor.WorkArea.Left, monitor.WorkArea.Right - (bounds.Right - bounds.Left));
                        double expectedY = Math.Clamp(anchor.Y + 4 * scale, monitor.WorkArea.Top, monitor.WorkArea.Bottom - (bounds.Bottom - bounds.Top));
                        string scenario = $"{source} {pair.Key} {position}, enhanced={enhanced}";
                        check($"{scenario}: color window follows its button, expected={expectedX},{expectedY}, actual={bounds.Left},{bounds.Top}",
                            Math.Abs(bounds.Left - expectedX) <= 2 && Math.Abs(bounds.Top - expectedY) <= 2);
                        check(scenario + ": first visible frame already uses the final anchor", first is { } initial &&
                            Math.Abs(initial.Left - bounds.Left) <= 2 && Math.Abs(initial.Top - bounds.Top) <= 2);
                        check(scenario + ": picker retains source ownership and content sizing", color.Owner == owner && color.SizeToContent == SizeToContent.WidthAndHeight &&
                            bounds.Right <= monitor.WorkArea.Right && bounds.Bottom <= monitor.WorkArea.Bottom);
                        field.ClosePicker(); Pump();
                    }
                }
            }
            finally { foreach (var pair in colors) pair.Value.ClosePicker(); owner.Close(); }
        }
    }
    private static void CheckContentSizedPlacement(Action<string, bool> check)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!;
        var monitor = new MonitorInfo(screen.DeviceName, new(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height),
            new(screen.WorkingArea.Left, screen.WorkingArea.Top, screen.WorkingArea.Width, screen.WorkingArea.Height),
            (uint)Math.Round(screen.Bounds.Width * 96d / SystemParameters.PrimaryScreenWidth), (uint)Math.Round(screen.Bounds.Height * 96d / SystemParameters.PrimaryScreenHeight), true);
        var layout = new SessionWindowLayout(); NativeRect? previous = null;
        foreach (double contentWidth in new[] { 220d, 440d })
        {
            var content = new TextBlock { Width = contentWidth, Text = new string('W', 150), TextWrapping = TextWrapping.Wrap };
            var window = new Window { Title = "Content-sized placement", Content = content, SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false };
            AppWindowChrome.Apply(window); layout.TrackEditing(window, () => [monitor]);
            try
            {
                SessionWindowCoordinator.Show(window, activate: false); Pump();
                GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds);
                if (previous is not { } saved)
                    check("a content-sized window centers using its actual template size before first display",
                        Math.Abs((bounds.Left + bounds.Right) / 2d - (monitor.WorkArea.Left + monitor.WorkArea.Right) / 2d) <= 2 &&
                        Math.Abs((bounds.Top + bounds.Bottom) / 2d - (monitor.WorkArea.Top + monitor.WorkArea.Bottom) / 2d) <= 2);
                else
                    check("recreated content-sized windows grow for new content without restoring an obsolete explicit width or origin",
                        bounds.Right - bounds.Left > saved.Right - saved.Left + 100 && Math.Abs(bounds.Left - saved.Left) <= 2 && Math.Abs(bounds.Top - saved.Top) <= 2);
                previous = bounds;
            }
            finally { window.Close(); Pump(); }
        }
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint handle);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out NativeRect bounds);
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
