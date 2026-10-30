using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class InspectorColorMenuLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Run --isolated-ui-desktop --inspector-color-menu-only.");
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("dark");
        output ??= Path.Combine(ApplicationPaths.ProfileDirectory, "inspector-color-menu");
        Directory.CreateDirectory(output);
        var results = new List<(string Name, bool Passed)>();
        foreach (bool managed in new[] { false, true })
        {
            string name = managed ? "managed-overlay" : "ordinary";
            using var source = !managed ? null : new HwndSource(new HwndSourceParameters("FloatCanvas Inspector menu scope")
            {
                Width = 1200, Height = 900, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080
            }) { RootVisual = new Border() };
            if (source is not null) ShowWindow(source.Handle, 4);
            using var scope = source is null ? null : new OverlayWindowScope(source.Handle, () => true);
            Scenario(managed, Path.Combine(output, name), (label, passed) => results.Add((name + " " + label, passed)));
        }
        File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(results.Select(result => new { name = result.Name, passed = result.Passed })));
        foreach (var (name, passed) in results) check(name, passed);
    }

    private static void Scenario(bool managed, string output, Action<string, bool> check)
    {
        Directory.CreateDirectory(output);
        var session = new EditorSession();
        session.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60,
            Style = new() { Stroke = 0x80112233, Fill = 0x40667788 } });
        var inspector = new InspectorWindow(() => session, colorLibrary: new ColorLibrary(Path.Combine(output, "profile", "colors.json")))
        { Left = 80, Top = 80, ShowActivated = false };
        Window? outside = null;
        try
        {
            if (managed) OverlayWindowScope.Prepare(inspector);
            inspector.Show(); inspector.PrepareForShow(); inspector.UpdateLayout();
            Activate(inspector);
            var colors = Field<Dictionary<string, ColorField>>(inspector, "colorFields");
            var field = colors["Stroke"];
            var trigger = Field<Button>(field, "button");
            var clipboard = Field<ColorClipboardMenu>(field, "clipboard");
            typeof(ColorClipboardMenu).GetField("readText", Private)!.SetValue(clipboard, (Func<string?>)(() => "#FF445566"));
            trigger.BringIntoView(); trigger.Focus(); Pump();
            check("the real unpinned inspector is visible and active before opening its main color menu",
                inspector.IsVisible && inspector.IsActive && !inspector.IsPinned && !field.IsPickerOpen);
            OpenMenu(trigger);
            Pump();
            var menu = clipboard.Menu;
            Trace("opened");
            check("opening the inspector main color menu retains its unpinned source after all deferred focus checks",
                inspector.IsVisible && !inspector.IsPinned && menu.IsOpen && !field.IsPickerOpen && menu.IsKeyboardFocusWithin);

            var copy = (MenuItem)menu.Items[1];
            copy.Focus(); copy.IsSubmenuOpen = true; Pump();
            Trace("copy-submenu");
            check("the inspector remains visible while its own color copy submenu is open",
                inspector.IsVisible && menu.IsOpen && copy.IsSubmenuOpen && copy.Items.Count == 6);
            CopyAndObserve(clipboard, trigger, "primary");
            ShowInspector(); trigger.Focus(); Pump(); OpenMenu(trigger); Pump();
            int undo = session.UndoCount;
            var transparent = (MenuItem)menu.Items[3];
            transparent.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, transparent));
            inspector.CompletePendingEdit(); Pump();
            Trace("transparent");
            check("a main color menu command returns to the inspector and makes one coherent edit",
                inspector.IsVisible && !menu.IsOpen && session.Document.Elements.Single().Style.Stroke == 0 && session.UndoCount == undo + 1);
            check("the inspector color menu edit retains the prior exact alpha in undo",
                session.Undo() && session.Document.Elements.Single().Style.Stroke == 0x80112233);

            ShowInspector(); trigger.Focus(); Pump();
            field.OpenPicker(); Pump();
            var picker = Field<ColorPickerPanel>(field, "picker");
            var preview = Field<Border>(picker, "preview");
            var pickerCommands = Field<ColorClipboardMenu>(picker, "clipboard");
            typeof(ColorClipboardMenu).GetField("readText", Private)!.SetValue(pickerCommands, (Func<string?>)(() => "#FF445566"));
            var pickerWindow = Window.GetWindow(picker)!;
            Activate(pickerWindow); preview.Focus(); Pump(); OpenMenu(preview); Pump();
            check("the inspector and its secondary color window retain the secondary copy menu",
                inspector.IsVisible && field.IsPickerOpen && pickerCommands.Menu.IsOpen);
            CopyAndObserve(pickerCommands, preview, "secondary");
            field.ClosePicker(); Pump();

            ShowInspector(); trigger.Focus(); Pump(); OpenMenu(trigger); Pump();
            check("the inspector main color menu can be reopened after a command", inspector.IsVisible && menu.IsOpen);
            menu.IsOpen = false; Pump();
            outside = new Window { Content = new TextBox { Text = "outside" }, Width = 220, Height = 120, Left = 700, Top = 80,
                ShowInTaskbar = false, ShowActivated = false };
            outside.Show(); Activate(outside); Pump();
            Trace("outside");
            check("a true outside activation still dismisses the unpinned inspector", !inspector.IsVisible && outside.IsActive);

            void CopyAndObserve(ColorClipboardMenu commands, FrameworkElement target, string origin)
            {
                string? copied = null;
                typeof(ColorClipboardMenu).GetField("writeText", Private)!.SetValue(commands, (Action<string>)(text => copied = text));
                var sourceMenu = commands.Menu;
                var formats = (MenuItem)sourceMenu.Items[1]; formats.IsSubmenuOpen = true; Pump();
                var leaf = (MenuItem)formats.Items[1];
                typeof(MenuItem).GetMethod("OnClick", Private)!.Invoke(leaf, null);
                var feedback = Field<ColorCopyFeedback>(commands, "feedback");
                Pump(250);
                File.AppendAllText(Path.Combine(output, "copy.jsonl"), JsonSerializer.Serialize(new
                {
                    origin, inspector = inspector.IsVisible, picker = field.IsPickerOpen, menu = sourceMenu.IsOpen,
                    submenu = formats.IsSubmenuOpen, copied, receipt = feedback.IsOpen, receiptNative = FeedbackVisible(feedback),
                    target = target.IsVisible, focused = Keyboard.FocusedElement?.GetType().Name,
                    receiptOwner = FeedbackOwner(feedback).ToInt64(), sourceHandle = (PresentationSource.FromVisual(target) as HwndSource)?.Handle.ToInt64()
                }) + Environment.NewLine);
                check(origin + " copy closes its actual context menu and preserves the exact ARGB text",
                    !sourceMenu.IsOpen && !formats.IsSubmenuOpen && copied == ColorLibrary.ToHtml(field.Value));
                check(origin + " copy receipt remains natively visible 250 ms after context-menu closure",
                    inspector.IsVisible && target.IsVisible && FeedbackVisible(feedback));
                check(origin + " copy receipt is natively owned by its actual source window",
                    FeedbackOwnedBy(feedback, target));
                feedback.Close();
            }

            void ShowInspector()
            {
                if (!inspector.IsVisible) { inspector.Show(); inspector.PrepareForShow(); inspector.UpdateLayout(); }
                Activate(inspector);
            }

            void Trace(string stage) => File.AppendAllText(Path.Combine(output, "lifecycle.jsonl"), JsonSerializer.Serialize(new
            {
                stage, inspector = inspector.IsVisible, active = inspector.IsActive, pinned = inspector.IsPinned,
                picker = field.IsPickerOpen, menu = menu.IsOpen, menuKeyboard = menu.IsKeyboardFocusWithin,
                focused = Keyboard.FocusedElement?.GetType().Name, undo = session.UndoCount
            }) + Environment.NewLine);
        }
        finally { outside?.Close(); inspector.Close(); Pump(); }
    }

    private static void OpenMenu(FrameworkElement target)
    {
        var opening = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), Private, null, [target, true], null)!;
        opening.RoutedEvent = ContextMenuService.ContextMenuOpeningEvent; target.RaiseEvent(opening);
    }

    private static void Activate(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        SetActiveWindow(handle); SetFocus(handle); Pump();
    }

    private static void Pump()
    {
        for (int pass = 0; pass < 3; pass++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); Pump();
    }

    private static bool FeedbackVisible(ColorCopyFeedback feedback)
    {
        if (!feedback.IsOpen) return false;
        var popup = Field<OverlayPopupWindow>(feedback, "window");
        var source = Field<HwndSource>(popup, "source");
        return !source.IsDisposed && source.Handle != 0 && IsWindowVisible(source.Handle);
    }

    private static bool FeedbackOwnedBy(ColorCopyFeedback feedback, FrameworkElement target)
    {
        if (!feedback.IsOpen || PresentationSource.FromVisual(target) is not HwndSource owner) return false;
        return FeedbackOwner(feedback) == owner.Handle;
    }

    private static nint FeedbackOwner(ColorCopyFeedback feedback)
    {
        if (!feedback.IsOpen) return 0;
        var popup = Field<OverlayPopupWindow>(feedback, "window");
        var source = Field<HwndSource>(popup, "source");
        return source.IsDisposed ? 0 : GetWindow(source.Handle, 4);
    }

    private static string DesktopName()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder name, int length, out int needed);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
}
