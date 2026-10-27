using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>
/// Actual reusable property windows and coordinator hide/show paths. Overlay coverage uses
/// the production popup/window adapter without an editing input lease. Normal native
/// activation runs exclusively on the separate --isolated-ui-desktop test desktop.
/// </summary>
internal static class WindowFocusLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        string desktop = DesktopName();
        if (!desktop.StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Run --isolated-ui-desktop --window-focus-only; this regression normally activates its own windows.");
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_WINDOW_FOCUS_OUTPUT") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "artifacts",
            "FloatCanvas-global-hidden-refresh", "verification", "window-focus");
        Directory.CreateDirectory(output);
        var findings = new List<(string Name, bool Passed)>();
        var observations = new List<object>();
        try
        {
            foreach (bool overlay in new[] { false, true })
            {
                string backend = overlay ? "overlay" : "ordinary";
                AppTheme.Apply("light");
                var bounds = new PixelBounds(-20000, -20000, 900, 900);
                Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("focus-lifecycle", bounds, bounds, 96, 96, true)];
                using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
                    [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
                var toolbarFocus = new TextBox { Text = "Isolated toolbar focus destination", Height = 28 };
                var tool = new Button { Content = "Text", Height = 28 };
                var launcher = new ToolbarLauncher();
                var toolbarBody = new StackPanel(); toolbarBody.Children.Add(toolbarFocus); toolbarBody.Children.Add(tool);
                var toolbar = new Window { Content = toolbarBody, Width = 600, Height = 90, Left = -20000, Top = -20000,
                    ShowActivated = true, ShowInTaskbar = false, Opacity = 0, WindowStyle = WindowStyle.None };
                var alternate = new Window { Content = new TextBox { Text = "Other isolated native focus owner" }, Width = 320, Height = 100,
                    Left = -18000, Top = -20000, ShowActivated = true, ShowInTaskbar = false, Opacity = 0 };
                nint toolbarHandle = new WindowInteropHelper(toolbar).EnsureHandle();
                using var scope = overlay ? new OverlayWindowScope(toolbarHandle, () => true) : null;
                AppTheme.Apply("light");
                using var coordinator = new SessionWindowCoordinator(host, toolbar);
                var first = new CanvasElement { Kind = ElementKind.Text, Text = "Focus lifecycle", Width = 160, Height = 60,
                    Style = new() { FontFamily = "Arial" } };
                var document = new EditorSession(new SceneDocument { Elements = [first] }); document.SelectAll();
                var editor = new EditorController(document) { Tool = EditorTool.Text }; editor.Style.FontFamily = "Arial";

                // The real production factory and open/close methods need these collaborators;
                // avoiding the full desktop constructor also avoids hotkeys, tray and input hooks.
                var session = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
                Set("_app", app); Set("_host", host); Set("_toolbar", toolbar); Set("_toolbarLauncher", launcher);
                Set("_settings", new UserSettings { ToolbarExpanded = true }); Set("_windowLayout", new SessionWindowLayout());
                Set("_editor", editor); Set("_windowCoordinator", coordinator);
                Set("_toolbarToolButtons", new Dictionary<EditorTool, Button> { [EditorTool.Text] = tool });
                Set("_toolPresets", new ToolPresetLibrary(Path.Combine(output, backend + "-presets.json")));
                Set("_colorLibrary", new ColorLibrary(Path.Combine(output, backend + "-colors.json")));
                InspectorWindow? inspector = null;
                Window? options = null;
                try
                {
                    SessionWindowCoordinator.Show(toolbar, activate: true); Pump();
                    SessionWindowCoordinator.Show(alternate, activate: true); Pump();
                    Invoke("CreateToolOptionsPopup", toolbar);
                    options = Field<Window>(session, "_toolOptionsWindow");
                    options.ShowActivated = true; options.ShowInTaskbar = false; options.Opacity = 0;
                    coordinator.Register(options, () => Field<bool>(session, "_toolOptionsPinned"), reason => Invoke("DismissToolOptions", reason));
                    var panel = Field<ToolOptionsPanel>(session, "_toolOptionsPanel");
                    var optionsFont = Field<FontPickerControl>(panel, "fontFamily");
                    Exercise("tool options", options, optionsFont,
                        () => Invoke("OpenToolOptions"), () => Invoke("CloseToolOptions"),
                        () => Field<Button>(session, "_toolOptionsPin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                        () => editor.Style.FontFamily == "Arial", Field<Dictionary<string, TextBox>>(panel, "numbers")["FontSize"]);

                    inspector = new InspectorWindow(() => document, colorLibrary: new ColorLibrary(Path.Combine(output, backend + "-inspector-colors.json")))
                    {
                        Owner = toolbar, Left = -20000, Top = -20000, Width = 460, Height = 720,
                        Opacity = 0, ShowActivated = true, ShowInTaskbar = false, Topmost = false
                    };
                    coordinator.Register(inspector, () => inspector.IsPinned, reason => inspector.Dismiss(reason));
                    var inspectorFont = Field<FontPickerControl>(inspector, "fontPicker");
                    Exercise("inspector", inspector, inspectorFont,
                        () => { inspector.PrepareForShow(); SessionWindowCoordinator.Show(inspector, activate: true); }, () => inspector.Dismiss(),
                        () => Field<Button>(inspector, "pin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                        () => document.Document.Elements.Single().Style.FontFamily == "Arial" && document.UndoCount == 0 && !document.IsTransactionActive,
                        Field<Dictionary<string, TextBox>>(inspector, "fields")["Text"]);
                }
                finally
                {
                    // Cleanup is after every assertion; it is never used to clear the focus
                    // being tested or manufacture the subsequent reopen state.
                    if (inspector is not null)
                    {
                        inspector.Hide(); nint inspectorHandle = new WindowInteropHelper(inspector).Handle;
                        if (inspectorHandle != 0) HwndSource.FromHwnd(inspectorHandle)?.Dispose();
                    }
                    alternate.Close(); Set("_disposed", true); toolbar.Close();
                    if (options is not null && new WindowInteropHelper(options).Handle is var optionsHandle && optionsHandle != 0)
                        HwndSource.FromHwnd(optionsHandle)?.Dispose();
                    Pump();
                }

                void Exercise(string surface, Window window, FontPickerControl font, Action open, Action close, Action pin, Func<bool> modelUnchanged, TextBox regularEditor)
                {
                    string label = backend + " " + surface;
                    ActivateOwn(toolbar); Keyboard.Focus(toolbarFocus); Pump();
                    open(); Pump(); ActivateOwn(window);
                    font.ApplyTemplate(); window.UpdateLayout(); Pump();
                    var input = (TextBox)font.Template.FindName("PART_EditableTextBox", font);
                    switch (font.Template.FindName("PART_Popup", font))
                    {
                        case Popup { Child: { } child }: child.Opacity = 0; break;
                        case OverlayPopup { Child: { } child }: child.Opacity = 0; break;
                        default: throw new InvalidOperationException(label + " is missing its actual font popup.");
                    }
                    pin();
                    ActivateOwn(toolbar); Keyboard.Focus(toolbarFocus); Pump();
                    ActivateOwn(window); Keyboard.Focus(input); Pump();
                    Observe(label + " begins with its real focused font editor and logical focus memory",
                        window.IsVisible && input.IsLoaded && ReferenceEquals(Keyboard.FocusedElement, input) &&
                        ReferenceEquals(FocusManager.GetFocusedElement(window), input) && font.IsDropDownOpen && !font.IsReadOnly);
                    input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, HwndSource.FromHwnd(new WindowInteropHelper(window).Handle), Environment.TickCount, Key.Enter)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Pump();
                    Observe(label + " retains component focus after confirming its collapsed read-only font",
                        ReferenceEquals(Keyboard.FocusedElement, input) && font.IsReadOnly && !font.IsDropDownOpen && input.SelectionLength == 0);
                    Snapshot("before-close");

                    // Real closing hands input to the toolbar before Hide. Losing native
                    // focus while the editor is still alive populates WPF's source-level
                    // restore cache, separately from FocusManager's logical focus memory.
                    // Do not choose any WPF child or clear the old window's logical scope.
                    ActivateOwn(toolbar);
                    Observe(label + " native pre-dismiss handoff transfers input away from the still-visible property window",
                        window.IsVisible && !window.IsActive && GetActiveWindow() == toolbarHandle && GetFocus() == toolbarHandle);
                    Snapshot("native-handoff-before-close");
                    close(); Pump();
                    Observe(label + " production close hides the window", !window.IsVisible);
                    Observe(label + " closing removes keyboard focus from property components", !IsPropertyFocus(window, Keyboard.FocusedElement));
                    Observe(label + " closing removes remembered property-component logical focus", !IsPropertyFocus(window, FocusManager.GetFocusedElement(window)));
                    Observe(label + " closing cancels the font dropdown and returns to read-only display", !font.IsDropDownOpen && font.IsReadOnly);
                    Snapshot("closed");

                    // Normal production Show/Activate is intentionally allowed. Only the
                    // native window receives focus; the fixture never focuses a neutral
                    // WPF child to overwrite the cached editor and manufacture a pass.
                    int reopenedDropdowns = 0;
                    EventHandler reopened = (_, _) => reopenedDropdowns++;
                    font.DropDownOpened += reopened;
                    try { open(); Pump(); ActivateOwn(window); Wait(350); }
                    finally { font.DropDownOpened -= reopened; }
                    input = (TextBox)font.Template.FindName("PART_EditableTextBox", font);
                    Observe(label + " reopen actually activates its native window", window.IsActive && GetActiveWindow() == new WindowInteropHelper(window).Handle && GetFocus() == new WindowInteropHelper(window).Handle);
                    Observe(label + " reopens visibly without focusing a property component", window.IsVisible && !IsPropertyFocus(window, Keyboard.FocusedElement));
                    Observe(label + " reopening does not restore a remembered property editor", !IsPropertyFocus(window, FocusManager.GetFocusedElement(window)));
                    Observe(label + " reopening keeps the font read-only with no dropdown", font.IsReadOnly && input.IsReadOnly && !font.IsDropDownOpen);
                    Observe(label + " reopening never briefly opens the font dropdown", reopenedDropdowns == 0);
                    Observe(label + " reopening does not automatically select the displayed font text", input.SelectionLength == 0);
                    Observe(label + " reopening keeps the same canonical font and model", ToolOptionPreviews.SelectedFontName(font) == "Arial" && modelUnchanged());
                    Snapshot("reopened");

                    // Retain the visible surface while another real HWND gains and loses
                    // activation. This exercises WM_KILLFOCUS/WM_SETFOCUS again without
                    // Hide, so a stale native restore cache cannot hide behind that reset.
                    pin(); ActivateOwn(alternate);
                    Observe(label + " real alternate window receives activation and native focus", alternate.IsActive && GetActiveWindow() == new WindowInteropHelper(alternate).Handle && GetFocus() == new WindowInteropHelper(alternate).Handle);
                    Snapshot("alternate-window-active");
                    ActivateOwn(window); Wait(350);
                    Observe(label + " native reactivation does not restore any property component", window.IsVisible && window.IsActive && !IsPropertyFocus(window, Keyboard.FocusedElement) && !IsPropertyFocus(window, FocusManager.GetFocusedElement(window)));
                    Observe(label + " native reactivation leaves the font read-only unselected and closed", font.IsReadOnly && input.IsReadOnly && !font.IsDropDownOpen && input.SelectionLength == 0 && modelUnchanged());
                    Snapshot("reactivated-after-alternate");
                    Keyboard.Focus(regularEditor); Pump();
                    Observe(label + " explicit editor focus after reopening still works", ReferenceEquals(Keyboard.FocusedElement, regularEditor));
                    ActivateOwn(alternate); ActivateOwn(window); Wait(350);
                    Observe(label + " a still-visible window retains normal editor focus restoration", ReferenceEquals(Keyboard.FocusedElement, regularEditor));
                    close(); Pump();

                    void Snapshot(string phase)
                    {
                        var observed = new { Desktop = desktop, Backend = backend, Surface = surface, Phase = phase, window.IsVisible, window.IsActive,
                            Keyboard = Describe(Keyboard.FocusedElement), Logical = Describe(FocusManager.GetFocusedElement(window)),
                            KeyboardInProperty = IsPropertyFocus(window, Keyboard.FocusedElement), LogicalInProperty = IsPropertyFocus(window, FocusManager.GetFocusedElement(window)),
                            NativeFocus = GetFocus().ToInt64(), NativeActive = GetActiveWindow().ToInt64(), WindowHandle = new WindowInteropHelper(window).Handle.ToInt64(), ForegroundIsFixture = GetForegroundWindow() == new WindowInteropHelper(window).Handle,
                            Text = input.Text, input.SelectionStart, input.SelectionLength, font.IsReadOnly, font.IsDropDownOpen,
                            Font = ToolOptionPreviews.SelectedFontName(font), ModelUnchanged = modelUnchanged() };
                        observations.Add(observed); Console.WriteLine("WINDOW_FOCUS " + JsonSerializer.Serialize(observed));
                    }
                }
                void Set(string name, object value) => typeof(DesktopSession).GetField(name, Private)!.SetValue(session, value);
                object? Invoke(string name, params object[] arguments) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, arguments);
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "window-focus-state.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var result in findings) check(result.Name, result.Passed);
        void Observe(string name, bool passed) { findings.Add((name, passed)); Console.WriteLine($"WINDOW_FOCUS_FINDING {passed}: {name}"); }
    }

    private static bool IsPropertyFocus(Window window, IInputElement? focus) => focus is Control and not Window and not ScrollViewer &&
        focus is DependencyObject element && (ReferenceEquals(Window.GetWindow(element), window) || OverlayPopupWindow.BelongsTo(element, window));
    private static string? Describe(IInputElement? focus) => focus is FrameworkElement element ? element.GetType().Name + "#" + element.Name : focus?.GetType().Name;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void ActivateOwn(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        GetWindowThreadProcessId(handle, out uint process);
        if (handle == 0 || process != Environment.ProcessId) throw new InvalidOperationException("Native activation must target an isolated fixture HWND.");
        SetForegroundWindow(handle); SetActiveWindow(handle); SetFocus(handle); Pump();
    }
    private static string DesktopName()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetActiveWindow();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
