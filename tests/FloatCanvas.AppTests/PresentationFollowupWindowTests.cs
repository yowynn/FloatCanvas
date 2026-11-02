using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Entire production tool window, shown only on a never-selected private desktop.</summary>
internal static class PresentationFollowupWindowTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        RequirePrivateDesktop();
        if (Application.Current is not null) throw new InvalidOperationException("Run this fixture in a fresh private-desktop process.");
        string folder = Path.Combine(output ?? AppContext.BaseDirectory, "presentation-followup-window"); Directory.CreateDirectory(folder);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var receipts = new List<object>();
        foreach (string locale in new[] { "zh-CN", "en" }) foreach (string theme in new[] { "light", "dark" })
        {
            ApplicationPaths.Configure(["--profile", Path.Combine(folder, "profile-" + locale + "-" + theme), "--primary-screen"]);
            var hotkeys = FreeHotkeys();
            new UserSettings { Theme = theme, Language = locale, Toggle = hotkeys.Item1, Hide = hotkeys.Item2, PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
            var monitor = new PixelBounds(-24000, -23000, 1920, 1080);
            Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("presentation-followup", monitor, monitor, 96, 96, true)];
            using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
            using var desktop = new DesktopSession(app, host);
            // Production toolbar refresh dismisses editing panels outside editing mode.
            // Model editing and pinning here without entering a native input lease.
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, true);
            typeof(DesktopSession).GetField("_toolOptionsPinned", Hidden)!.SetValue(desktop, true);
            Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
            var toolbar = Field<Window>(desktop, "_toolbar"); toolbar.ShowActivated = false; toolbar.Opacity = 0;
            toolbar.Show(); Pump();
            var actualDpi = VisualTreeHelper.GetDpi(toolbar);
            typeof(OverlayHost).GetField("_monitors", Hidden)!.SetValue(host, (IReadOnlyList<MonitorInfo>)[new("presentation-followup", monitor, monitor, (uint)Math.Round(actualDpi.PixelsPerInchX), (uint)Math.Round(actualDpi.PixelsPerInchY), true)]);
            var editor = Field<EditorController>(desktop, "_editor"); editor.Tool = EditorTool.Presentation;
            var panel = Field<ToolOptionsPanel>(desktop, "_toolOptionsPanel");
            var window = Field<Window>(desktop, "_toolOptionsWindow"); window.ShowActivated = false;
            void Call(string name) => typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(desktop, null);
            Call("UpdateToolOptionsPin");
            void Snapshot(string name, Window? target = null)
            {
                var displayed = target ?? window;
                Settle(); displayed.UpdateLayout();
                var root = (FrameworkElement)VisualTreeHelper.GetChild(displayed, 0);
                root.InvalidateArrange(); root.UpdateLayout();
                var hwnd = new WindowInteropHelper(displayed).Handle; GetWindowRect(hwnd, out var native);
                receipts.Add(new { locale, theme, name, WindowWidth = displayed.ActualWidth, WindowHeight = displayed.ActualHeight,
                    RootWidth = root.ActualWidth, RootHeight = root.ActualHeight, Native = new { native.Left, native.Top, native.Right, native.Bottom },
                    ActualDpi = VisualTreeHelper.GetDpi(root).PixelsPerInchX, displayed.MinWidth, displayed.MinHeight, displayed.Title });
                foreach (int dpi in new[] { 96, 144 })
                {
                    var pixels = RenderWindow(displayed, dpi); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(pixels));
                    using var file = File.Create(Path.Combine(folder, $"{name}-{locale}-{theme}-{dpi}dpi.png")); png.Save(file);
                }
            }
            Call("OpenToolOptions"); Pump();
            double keyboardWidth = window.Width;
            Console.WriteLine($"NATIVE_KEYBOARD locale={locale} theme={theme} visible={window.IsVisible} hwnd={new WindowInteropHelper(window).Handle} width={window.Width} actual={window.ActualWidth}x{window.ActualHeight} minimum={window.MinWidth}x{window.MinHeight} tool={editor.Tool} state={window.SizeToContent}");
            Snapshot("keyboard-initial-state");
            check(locale + " " + theme + " native keyboard window keeps standard columns within the DPI-adjusted work area", window.IsVisible && new WindowInteropHelper(window).Handle != 0 && window.MinWidth == PropertyPanelLayout.TwoColumnMinimumWidth && window.ActualWidth >= 640 && window.ActualHeight * actualDpi.DpiScaleY <= monitor.Height && editor.Tool != EditorTool.PointerPresentation);
            var columns = Field<Grid>(panel, "parameterColumns"); var parameters = Field<Grid>(panel, "parameterGrid");
            var keyboard = Field<PresentationSettingsPanel>(panel, "presentationSettings");
            var scope = Field<Dictionary<string, FrameworkElement>>(keyboard, "rows")["KeyboardScope"];
            var pointer = Field<PresentationGlobalSettingsPanel>(panel, "presentationGlobalSettings");
            void CheckSharedOrder(string state)
            {
                var toolRows = Field<Dictionary<string, FrameworkElement>>(panel, "rows");
                string[] shared = ["Colors", "Opacity", "FontFamily", "FontSize", "Size"];
                check(locale + " " + theme + " " + state + " tool mode and exclusive fields precede every shared property", shared.All(key => toolRows[key].IsVisible && Grid.GetRow(toolRows[key]) > Grid.GetRow(toolRows["PresentationSettings"])) && Field<Dictionary<string, FrameworkElement>>(keyboard, "rows")["KeyPanelAlignment"].IsVisible);
                var element = new CanvasElement { Kind = ElementKind.Presentation, Width = 760, Height = 260, Presentation = editor.Parameters.Presentation!.Clone(), Style = editor.Parameters.Style.Clone() };
                var inspected = new EditorSession(new() { Elements = [element] }); inspected.SetSelection([element.Id]);
                var inspector = new InspectorWindow(() => inspected) { Owner = window, ShowActivated = false };
                Field<Button>(inspector, "pin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); inspector.PrepareForShow(); inspector.Show(); Pump();
                try
                {
                    var fields = Field<Dictionary<string, FrameworkElement>>(inspector, "fieldContainers"); var properties = Field<StackPanel>(inspector, "propertyPanel");
                    double boundary = fields["PresentationSettings"].TransformToAncestor(properties).TransformBounds(new Rect(fields["PresentationSettings"].RenderSize)).Bottom;
                    string[] names = ["X", "W", "Angle", "Stroke", "Fill", "Opacity", "FontFamily", "FontSize"];
                    check(locale + " " + theme + " " + state + " Inspector mode and exclusive fields precede all visible shared fields", names.All(key => fields[key].IsVisible && fields[key].TransformToAncestor(properties).TransformBounds(new Rect(fields[key].RenderSize)).Top >= boundary - 1) && Field<Dictionary<string, FrameworkElement>>(Field<PresentationSettingsPanel>(inspector, "presentationSettings"), "rows")["KeyPanelAlignment"].IsVisible);
                    Snapshot("inspector-" + state, inspector);
                }
                finally { SessionWindowCoordinator.Close(inspector); }
            }
            check(locale + " " + theme + " keyboard window has scope and presets without mouse controls or a recording switch", columns.IsVisible && Field<ToolPresetGallery>(panel, "presets").IsVisible && scope.IsVisible && !pointer.IsVisible && !Descendants(keyboard).OfType<CheckBox>().Any());
            check(locale + " " + theme + " keyboard parameter rows have distinct non-overlapping grid positions", parameters.Children.OfType<FrameworkElement>().Where(row => row.Visibility == Visibility.Visible).GroupBy(Grid.GetRow).All(group => group.Count() == 1));
            Snapshot("keyboard-default");
            CheckSharedOrder("simple");
            window.SizeToContent = SizeToContent.Manual; window.Width = 640; window.Height = 420; Pump();
            check(locale + " " + theme + " minimum native keyboard window preserves both usable columns", parameters.ActualWidth >= 340 && Field<ToolPresetGallery>(panel, "presets").ActualWidth >= 196 && window.ActualWidth >= 640 && window.ActualHeight >= 420);
            Snapshot("keyboard-minimum");
            window.SizeToContent = SizeToContent.Height; window.Width = keyboardWidth; window.Height = double.NaN;
            var keyboardEntries = Field<IDictionary>(keyboard, "entries");
            T KeyControl<T>(string key) where T : FrameworkElement { var entry = keyboardEntries[key]!; return (T)entry.GetType().GetProperty("Control")!.GetValue(entry)!; }
            KeyControl<SegmentedChoice>("LogStyle").SelectedValue = PresentationDisplayStyle.Keycaps; Pump(); Snapshot("keyboard-keycaps"); CheckSharedOrder("keycaps");
            KeyControl<SegmentedChoice>("DisplayStyle").SelectedValue = PresentationDisplayStyle.Keyboard; Pump(); Snapshot("keyboard-live"); CheckSharedOrder("live");
            Exception? editorFailure = null;
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var dialog = app.Windows.OfType<KeyboardLayoutEditorWindow>().Single(candidate => candidate.IsVisible);
                try
                {
                    check(locale + " " + theme + " secondary native keyboard editor exposes all default keys", Field<Dictionary<string, Thumb>>(dialog, "keyViews").Count == 104 && dialog.Draft.IsValid());
                    Snapshot("keyboard-layout-editor", dialog); dialog.Accept();
                }
                catch (Exception error) { editorFailure = error; if (dialog.IsVisible) SessionWindowCoordinator.SetModalResult(dialog, false); }
            });
            KeyControl<Button>("KeyboardLayout").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (editorFailure is not null) throw new InvalidOperationException("Native layout editor fixture failed.", editorFailure);
            string document = JsonSerializer.Serialize(editor.Session.Document); int undo = editor.Session.UndoCount;
            Call("ShowPresentationPointerSettings"); Pump();
            check(locale + " " + theme + " formal mouse tool uses the same properties and preset window bounds", editor.Tool == EditorTool.PointerPresentation && window.Title == L.Get("Tool.PointerPresentation") && Math.Abs(window.Width - keyboardWidth) < 1 && window.MinWidth == 640 && window.ActualHeight >= 420 && pointer.IsVisible && columns.IsVisible);
            check(locale + " " + theme + " mouse master off exposes no dependent fields or keyboard scope while keeping mouse presets", Descendants(pointer).OfType<CheckBox>().Count(box => box.IsVisible) == 1 && !scope.IsVisible && Field<ToolPresetGallery>(panel, "presets").IsVisible);
            Snapshot("mouse-off");
            window.SizeToContent = SizeToContent.Manual; window.Width = 640; window.Height = 420; Pump();
            check(locale + " " + theme + " minimum mouse window retains usable properties and its preset column", parameters.ActualWidth >= 340 && Field<ToolPresetGallery>(panel, "presets").ActualWidth >= 196);
            Snapshot("mouse-minimum"); window.SizeToContent = SizeToContent.Height; window.Width = keyboardWidth; window.Height = double.NaN;
            Toggle(pointer, "PointerEnabled", true); Pump();
            var conditions = Field<Dictionary<string, PropertyChangeRegion>>(pointer, "conditions");
            check(locale + " " + theme + " mouse master immediately reveals its existing conditional option sections", conditions["PointerEnabled"].IsVisible && Control<CheckBox>(pointer, "CursorEnabled").IsVisible && Control<ColorField>(pointer, "CursorColor").IsVisible && Control<CheckBox>(pointer, "ClicksEnabled").IsVisible && !Control<ColorField>(pointer, "TrailColor").IsVisible);
            Snapshot("mouse-enabled");
            Control<SegmentedChoice>(pointer, "LeftClick.Shape").SelectedValue = PresentationClickShape.Disc;
            Settle();
            check(locale + " " + theme + " mouse shape conditional cue survives synchronous preference refresh", !Field<Dictionary<string, FrameworkElement>>(pointer, "rows")["LeftClick.StrokeWidthPixels"].IsVisible && conditions["LeftClick.Enabled"].CueStrength > 0);
            Control<SegmentedChoice>(pointer, "LeftClick.Shape").SelectedValue = PresentationClickShape.Ring;
            Toggle(pointer, "CursorEnabled", false); Toggle(pointer, "ClicksEnabled", false); Settle();
            check(locale + " " + theme + " disabled mouse subsections settle to a compact actual window", !Control<ColorField>(pointer, "CursorColor").IsVisible && !Control<CheckBox>(pointer, "LeftClick.Enabled").IsVisible && window.ActualHeight >= 420 && window.ActualHeight < 500);
            Snapshot("mouse-sections-off");
            Toggle(pointer, "TrailEnabled", true); Pump();
            check(locale + " " + theme + " trail options appear directly after their switch", conditions["TrailEnabled"].IsVisible && Control<ColorField>(pointer, "TrailColor").IsVisible && FollowsToggle(pointer, "TrailEnabled"));
            Snapshot("mouse-trail");
            var trailColor = Control<ColorField>(pointer, "TrailColor");
            Field<Button>(trailColor, "button").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check(locale + " " + theme + " independent mouse color popup participates in existing tool input ownership", panel.IsColorPickerOpen && panel.OwnsColorPickerInput(Field<object>(trailColor, "picker") as DependencyObject) && panel.CloseColorPicker() && !panel.IsColorPickerOpen);
            string choices = JsonSerializer.Serialize(Field<UserSettings>(desktop, "_settings").PresentationGlobal.Pointer);
            Toggle(pointer, "PointerEnabled", false); Pump();
            var retained = Field<UserSettings>(desktop, "_settings").PresentationGlobal.Pointer.Clone(); retained.Enabled = true;
            check(locale + " " + theme + " mouse master collapse retains styles and changes no authored document or history", JsonSerializer.Serialize(retained) == choices && !Control<CheckBox>(pointer, "CursorEnabled").IsVisible && JsonSerializer.Serialize(editor.Session.Document) == document && editor.Session.UndoCount == undo);
            editor.Tool = EditorTool.Presentation; Call("OpenToolOptions"); Pump();
            check(locale + " " + theme + " returning to keyboard restores its original host width columns and title", editor.Tool != EditorTool.PointerPresentation && Math.Abs(window.Width - keyboardWidth) < 1 && columns.IsVisible && !pointer.IsVisible && window.MinWidth == 640 && window.Title == L.Get("Tool.Presentation"));
            editor.Tool = EditorTool.Rectangle; panel.PrepareForShow(); Call("AnchorToolOptions"); Pump();
            check(locale + " " + theme + " ordinary tool parameters keep their existing host layout", !scope.IsVisible && !pointer.IsVisible && columns.IsVisible && parameters.ActualWidth >= 340 && Field<ToolPresetGallery>(panel, "presets").IsVisible && window.MinWidth == 640);
            Snapshot("rectangle-regression");
            check(locale + " " + theme + " private native UI exercise never observes user input", !PresentationDesktopAvailability.IsCurrentInputDesktop() && !Field<PresentationRuntimeBroker>(desktop, "_presentationRuntime").Observing && !host.IsKeyboardIsolationActive);
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, false);
        }
        File.WriteAllText(Path.Combine(folder, "native-window-bounds.json"), JsonSerializer.Serialize(receipts, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PRESENTATION_FOLLOWUP_WINDOWS " + folder);
    }
    private static bool FollowsToggle(PresentationGlobalSettingsPanel panel, string key)
    {
        var row = Field<Dictionary<string, FrameworkElement>>(panel, "rows")[key]; var region = Field<Dictionary<string, PropertyChangeRegion>>(panel, "conditions")[key];
        return LogicalTreeHelper.GetParent(row) is Panel parent && parent.Children.IndexOf(region) == parent.Children.IndexOf(row) + 1;
    }
    private static void Toggle(PresentationGlobalSettingsPanel panel, string key, bool value)
    { var control = Control<CheckBox>(panel, key); control.IsChecked = value; control.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); }
    private static T Control<T>(PresentationGlobalSettingsPanel panel, string key) where T : FrameworkElement
    { var entries = Field<IDictionary>(panel, "entries"); var entry = entries[key]!; return (T)entry.GetType().GetProperty("Control")!.GetValue(entry)!; }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var value in Descendants(VisualTreeHelper.GetChild(root, index))) yield return value; }
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    private static void Settle()
    {
        var elapsed = Stopwatch.StartNew(); double[] previous = [];
        while (elapsed.ElapsedMilliseconds < 1800)
        {
            RenderTick();
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
            RenderTick(); Pump();
            var windows = Application.Current.Windows.OfType<Window>().Where(candidate => candidate.IsVisible).ToArray();
            var heights = windows.Select(window => window.ActualHeight).ToArray();
            bool stable = previous.Length == heights.Length && previous.Zip(heights).All(pair => Math.Abs(pair.First - pair.Second) < .5);
            bool transitioning = windows.SelectMany(Descendants).OfType<PropertyChangeRegion>().Any(region => !double.IsNaN(region.Height));
            if (elapsed.ElapsedMilliseconds >= 220 && stable && !transitioning) return;
            previous = heights;
        }
        throw new TimeoutException("Private WPF window height did not settle after complete render passes.");
    }
    private static void RenderTick()
    {
        // A non-input desktop can defer clocks. Render the same full surfaces
        // used by screenshots; a 1px clipped target does not cover their regions.
        foreach (var window in Application.Current.Windows.OfType<Window>().Where(candidate => candidate.IsVisible).ToArray())
            if (VisualTreeHelper.GetChildrenCount(window) > 0) _ = RenderWindow(window, 96);
    }
    private static RenderTargetBitmap RenderWindow(Window window, int dpi)
    {
        window.UpdateLayout(); var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        root.InvalidateArrange(); root.UpdateLayout(); double scale = dpi / 96d;
        var pixels = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        pixels.Render(root); root.UpdateLayout(); return pixels;
    }
    private static void RequirePrivateDesktop()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _) || !name.ToString().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Use --isolated-ui-desktop --presentation-followup-ui-only.");
    }
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x5c31, 7, key)) continue;
            try { if (!RegisterHotKey(0, 0x5c32, 7, key + 1)) continue; UnregisterHotKey(0, 0x5c32); return (new(7, key), new(7, key + 1)); }
            finally { UnregisterHotKey(0, 0x5c31); }
        }
        throw new InvalidOperationException("No free fixture hotkeys.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder name, uint length, out uint needed);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint handle, int id);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect bounds);
}
