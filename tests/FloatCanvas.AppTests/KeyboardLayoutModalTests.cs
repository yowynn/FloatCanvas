using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Production Desktop/coordinator modal routing on an inactive private desktop; no physical input.</summary>
internal static class KeyboardLayoutModalTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        RequirePrivateDesktop();
        if (Application.Current is not null) throw new InvalidOperationException("Use a fresh private-desktop process for layout modal checks.");
        new UserSettings { Language = "en", Theme = "dark", PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; L.Initialize("en");
        bool smallWorkArea = Environment.GetCommandLineArgs().Contains("--layout-modal-small-workarea");
        var bounds = new PixelBounds(-24000, -24000, smallWorkArea ? 1200 : 1920, smallWorkArea ? 850 : 1080);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("layout-modal-private", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null,
            [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        host.EnableSystemInput(); // Initialize the actual backend; never request a lease or switch desktop.
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        var toolbar = Field<Window>(desktop, "_toolbar"); toolbar.ShowActivated = false;
        var coordinator = Field<SessionWindowCoordinator>(desktop, "_windowCoordinator");
        var editor = Field<EditorController>(desktop, "_editor"); editor.BackgroundPickingEnabled = false;
        var session = editor.Session;
        host.SetVisible(true); toolbar.Show();
        // The inactive desktop cannot own physical input. Control only the production modal branch predicate.
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, true);
        var layout = PresentationKeyboardLayout.CreateDefault();
        var layer = new CanvasElement { Kind = ElementKind.Presentation, Width = 760, Height = 260,
            Presentation = new() { DisplayStyle = PresentationDisplayStyle.Keyboard, KeyboardLayout = layout.Clone() } };
        session.Execute("private layout fixture", document => document.Elements = [layer]); session.SetSelection([layer.Id]);
        Call(desktop, "ToggleInspector");
        var inspector = Field<InspectorWindow>(desktop, "_inspector");
        Field<Button>(inspector, "pin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); inspector.PrepareForShow(); Pump();
        var panel = Field<PresentationSettingsPanel>(inspector, "presentationSettings");
        check("layout modal fixture uses the real initialized backend and registered production Desktop owner", host.UsesSystemInput && host.IsEditing &&
            ReferenceEquals(SessionWindowCoordinator.Find(inspector), coordinator) && ReferenceEquals(inspector.Owner, toolbar) && inspector.IsVisible);
        check("private modal setup never acquires physical editing input or a presentation observer", !PresentationDesktopAvailability.IsCurrentInputDesktop() && !host.HasEditingInput && !Field<PresentationRuntimeBroker>(desktop, "_presentationRuntime").Observing);
        try
        {
            foreach (string action in new[] { "apply", "cancel", "caption", "escape" })
            {
                inspector.PrepareForShow(); inspector.CompletePendingEdit();
                string before = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount;
                Exception? failure = null; KeyboardLayoutEditorWindow? shown = null; PresentationKeyboardLayout? result = null;
                int closed = 0; bool custom = false, ownerDisabled = false, nativeInputEnabled = false;
                bool standardChrome = false, standardFields = false, standardActions = false, standardSurfaces = false,
                    standardKeyboardFocus = false, captionsFit = false, previewFitsWorkArea = false;
                using var timeout = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(10), timeout.Token); }
                    catch (OperationCanceledException) { return; }
                    Console.Error.WriteLine("FAIL layout modal fixture exceeded ten seconds; own test process exits to avoid a stranded nested frame."); Environment.Exit(114);
                });
                app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    try
                    {
                        shown = app.Windows.OfType<KeyboardLayoutEditorWindow>().Single(window => window.IsVisible);
                        shown.Closed += (_, _) => closed++;
                        custom = shown.Resources.Contains("FloatCanvas.SessionWindowCoordinator.Modal") && ComponentDispatcher.IsThreadModal;
                        ownerDisabled = !IsWindowEnabled(new WindowInteropHelper(inspector).Handle) && !IsWindowEnabled(new WindowInteropHelper(toolbar).Handle);
                        nativeInputEnabled = host.Diagnostics.Count > 0 && host.Diagnostics.All(window => IsWindowEnabled(window.InputHandle) && IsWindowEnabled(window.DisplayHandle));
                        standardChrome = shown.WindowStyle == WindowStyle.None && WindowChrome.GetWindowChrome(shown) is { CaptionHeight: AppWindowChrome.CaptionHeight } &&
                            shown.Icon is not null && shown.Resources.Contains("FloatCanvas.SessionWindowLayout") && ReferenceEquals(SessionWindowCoordinator.Find(shown), coordinator);
                        var columns = ((DockPanel)shown.Content).Children.OfType<Grid>().Single();
                        standardFields = columns.ColumnDefinitions[1].Width.Value == PropertyPanelLayout.ContentWidth &&
                            shown.MinWidth <= 640 && shown.MinHeight <= 420 &&
                            Field<Dictionary<string, NumericValueField>>(shown, "coordinates").Values.All(field => field.Height == PropertyPanelLayout.ControlHeight && field.ActualHeight >= PropertyPanelLayout.ControlHeight - .01) &&
                            ReferenceEquals(Field<CheckBox>(shown, "enabled").Style, shown.FindResource("CanvasPropertySwitch"));
                        var cancel = Descendants(shown).OfType<Button>().Single(button => button.IsCancel);
                        standardActions = Field<Button>(shown, "apply").MinHeight == PropertyPanelLayout.ControlHeight && cancel.MinHeight == PropertyPanelLayout.ControlHeight &&
                            Field<Button>(shown, "apply").MinWidth == cancel.MinWidth && ((DockPanel)shown.Content).Margin == WindowLayout.ContentMargin();
                        var keyViews = Field<Dictionary<string, System.Windows.Controls.Primitives.Thumb>>(shown, "keyViews");
                        var operations = Descendants(shown).OfType<FrameworkElement>()
                            .Single(element => AutomationProperties.GetAutomationId(element) == "LayoutOperations");
                        standardSurfaces = operations is RoundedSurfaceBorder &&
                            Field<TextBlock>(shown, "identity").Parent is RoundedSurfaceBorder;
                        standardKeyboardFocus = keyViews.Values.All(view => view.FocusVisualStyle is null && view.Focusable);
                        captionsFit = keyViews.Count == 104 && keyViews.Values.All(view =>
                        {
                            if (view.Template.FindName("KeyCaption", view) is not TextBlock caption || caption.TextWrapping != TextWrapping.NoWrap || string.IsNullOrWhiteSpace(caption.Text)) return false;
                            var ink = caption.TransformToAncestor(view).TransformBounds(new Rect(caption.RenderSize));
                            var keyBounds = new Rect(new Point(), view.RenderSize); keyBounds.Inflate(.01, .01); return keyBounds.Contains(ink);
                        });
                        var previewCanvas = Field<Canvas>(shown, "preview"); var previewScroll = PreviewScroll(shown);
                        previewFitsWorkArea = smallWorkArea
                            ? shown.ActualWidth <= bounds.Width / VisualTreeHelper.GetDpi(shown).DpiScaleX + .5 && previewScroll.ViewportWidth < previewCanvas.Width && previewScroll.ScrollableWidth > 0
                            : shown.ActualWidth >= 1240 - .5 && previewScroll.ViewportWidth >= previewCanvas.Width - .5 && previewScroll.ScrollableWidth < .5;
                        Console.WriteLine($"LAYOUT MODAL action={action}; custom={custom}; ownerDisabled={ownerDisabled}; nativeInputEnabled={nativeInputEnabled}");
                        if (action == "apply" && Environment.GetEnvironmentVariable("FLOATCANVAS_LAYOUT_MODAL_EVIDENCE") is { Length: > 0 } evidence)
                        {
                            Directory.CreateDirectory(evidence);
                            Capture(shown, Path.Combine(evidence, smallWorkArea ? "keyboard-layout-workarea-clamped.png" : "keyboard-layout-default-wide.png"));
                            shown.SelectKey(layout.Keys.Single(key => key.VirtualKey == 0xA0).Id);
                            Capture(shown, Path.Combine(evidence, "keyboard-layout-modifier-matching.png"));
                            shown.SelectKey(layout.Keys.Single(key => key.VirtualKey == 0x14).Id);
                            Capture(shown, Path.Combine(evidence, "keyboard-layout-lock-indicator.png"));
                        }
                        shown.SelectKey(layout.Keys.Single(key => key.VirtualKey == 0x41).Id);
                        Field<TextBox>(shown, "label").Text = action == "apply" ? "Applied A" : "Discarded A";
                        if (action == "apply") Click(Field<Button>(shown, "apply"));
                        else if (action == "cancel") Click(Descendants(shown).OfType<Button>().Single(button => button.IsCancel));
                        else if (action == "caption") Click((Button)shown.Template.FindName("Close", shown));
                        else shown.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, (HwndSource)PresentationSource.FromVisual(shown)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                        result = shown.ResultLayout;
                    }
                    catch (Exception error)
                    {
                        failure = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
                        Console.WriteLine("LAYOUT MODAL OBSERVED " + failure);
                        if (shown?.IsVisible == true) SessionWindowCoordinator.SetModalResult(shown, false);
                    }
                });
                try { Entry<Button>(panel, "KeyboardLayout").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                finally { timeout.Cancel(); }
                inspector.CompletePendingEdit(); Pump();
                check(action + " enters the actual coordinator overlay modal branch instead of ShowDialog", custom && ownerDisabled && nativeInputEnabled);
                check(action + " completes without setting DialogResult on a modeless window", failure is null);
                if (action == "apply")
                {
                    check("layout editor reuses standard app chrome and inherited session placement", standardChrome);
                    check("layout form uses the shared property width numeric height and switch style", standardFields);
                    check("layout footer uses standard content insets and equal-height action buttons", standardActions);
                    check("layout operations and identity cards use the continuous rounded-surface entry",
                        standardSurfaces);
                    check("key thumbs retain keyboard focus without default focus adorners", standardKeyboardFocus);
                    check("all 104 physical key captions remain single-line within their actual key bounds", captionsFit);
                    check(smallWorkArea ? "small work area clamps the real default window while preserving horizontal preview scrolling" : "wide work area shows the complete standard preview beside the standard property form", previewFitsWorkArea);
                }
                check(action + " closes exactly once and leaves no modal window or dispatcher frame", shown is { IsVisible: false } && closed == 1 &&
                    !app.Windows.OfType<KeyboardLayoutEditorWindow>().Any(window => window.IsVisible) && !ComponentDispatcher.IsThreadModal && Field<IList>(coordinator, "modalWindows").Count == 0);
                check(action + " restores every WPF owner and native canvas carrier", IsWindowEnabled(new WindowInteropHelper(inspector).Handle) &&
                    IsWindowEnabled(new WindowInteropHelper(toolbar).Handle) && inspector.IsEnabled && toolbar.IsEnabled && inspector.IsVisible &&
                    host.Diagnostics.Count > 0 && host.Diagnostics.All(window => IsWindowEnabled(window.InputHandle) && IsWindowEnabled(window.DisplayHandle)));
                if (action == "apply")
                {
                    var stored = session.Document.Elements.Single().Presentation!.KeyboardLayout!;
                    check("registered Inspector Apply commits the edited layout in exactly one undo", shown!.Accepted && session.UndoCount == undo + 1 && stored.Keys.Single(key => key.VirtualKey == 0x41).Label == "Applied A");
                    check("the committed layout owns its values independently of dialog result and source", result is not null && !ReferenceEquals(stored, result) && !ReferenceEquals(result, layout) && layout.Keys.Single(key => key.VirtualKey == 0x41).Label == "A");
                    result!.Keys[0].Label = "mutated result";
                    check("mutating a returned result cannot alter the committed layer", stored.Keys[0].Label != "mutated result");
                    int completedUndo = session.UndoCount; Pump(); inspector.CompletePendingEdit();
                    check("late dispatcher turns do not publish the accepted layout twice", session.UndoCount == completedUndo && !session.IsTransactionActive);
                }
                else check(action + " discards its draft without publishing a scene write", !shown!.Accepted && JsonSerializer.Serialize(session.Document) == before && session.UndoCount == undo && !session.IsTransactionActive);
            }
            // Also retain the ordinary WPF ShowDialog completion contract, outside editing.
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, false);
            var ordinary = new KeyboardLayoutEditorWindow(layout) { Owner = inspector, ShowActivated = false };
            bool ordinaryPath = false;
            ordinary.Loaded += (_, _) => ordinary.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                ordinaryPath = !ordinary.Resources.Contains("FloatCanvas.SessionWindowCoordinator.Modal");
                Click(Descendants(ordinary).OfType<Button>().Single(button => button.IsCancel));
            });
            bool? ordinaryResult = SessionWindowCoordinator.ShowModal(ordinary); Pump();
            check("the same actual Cancel button also completes the ordinary ShowDialog path", ordinaryPath && ordinaryResult == false && !ordinary.Accepted && !ordinary.IsVisible && !ComponentDispatcher.IsThreadModal && IsWindowEnabled(new WindowInteropHelper(inspector).Handle));
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, true);
            foreach (var size in new[] { new Size(800, 480), new Size(760, 480), new Size(640, 420) })
            {
                double width = size.Width;
                var limited = new KeyboardLayoutEditorWindow(layout, inspector) { Width = size.Width, Height = size.Height, ShowActivated = false };
                bool scrolledToEnd = false, farKeyVisible = false; Exception? scrollFailure = null;
                string beforeScroll = JsonSerializer.Serialize(session.Document); int beforeScrollUndo = session.UndoCount;
                limited.Loaded += (_, _) => limited.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var scroll = PreviewScroll(limited); scroll.ScrollToRightEnd();
                    limited.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                    {
                        try
                        {
                            limited.UpdateLayout();
                            scrolledToEnd = scroll.ViewportWidth > 0 && scroll.ViewportWidth < 714 && scroll.ScrollableWidth > 0 &&
                                scroll.HorizontalOffset > 0 && Math.Abs(scroll.HorizontalOffset - scroll.ScrollableWidth) < .5;
                            var far = Field<Dictionary<string, System.Windows.Controls.Primitives.Thumb>>(limited, "keyViews").Values.OrderBy(view => Canvas.GetLeft(view) + view.ActualWidth).Last();
                            var rendered = far.TransformToAncestor(scroll).TransformBounds(new Rect(far.RenderSize));
                            farKeyVisible = rendered.Left >= -.5 && rendered.Right <= scroll.ViewportWidth + .5;
                            if (Environment.GetEnvironmentVariable("FLOATCANVAS_LAYOUT_MODAL_EVIDENCE") is { Length: > 0 } evidence)
                                Capture(limited, Path.Combine(evidence, $"keyboard-layout-{(int)size.Width}x{(int)size.Height}-{(smallWorkArea ? "clamped" : "wide")}-right-end.png"));
                        }
                        catch (Exception error) { scrollFailure = error; }
                        finally { SessionWindowCoordinator.SetModalResult(limited, false); }
                    });
                });
                SessionWindowCoordinator.ShowModal(limited); Pump();
                check(size.Width + "x" + size.Height + " DIP layout preview can scroll to and expose the rightmost physical key",
                    scrollFailure is null && scrolledToEnd && farKeyVisible);
                check(size.Width + "x" + size.Height + " DIP scrolling and Cancel preserve the layer and unwind its modal owner",
                    JsonSerializer.Serialize(session.Document) == beforeScroll && session.UndoCount == beforeScrollUndo &&
                    !limited.Accepted && !ComponentDispatcher.IsThreadModal && IsWindowEnabled(new WindowInteropHelper(inspector).Handle));
            }
            var unshown = new KeyboardLayoutEditorWindow(new() { Keys = [] });
            check("a zero-key layout disables Apply and explains that one key is required",
                !Field<Button>(unshown, "apply").IsEnabled &&
                Field<TextBlock>(unshown, "status").Text == L.Get("Presentation.Layout.NoEnabledKeys"));
            unshown.ResetToDefault(); unshown.Accept();
            check("unshown compatible empty-layout acceptance keeps the existing IsVisible modal guard", unshown.Accepted && unshown.ResultLayout is null && !unshown.IsVisible);
            SessionWindowCoordinator.Close(unshown);
        }
        finally
        {
            coordinator.CloseEditingModals(); typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, false);
            foreach (var window in app.Windows.OfType<KeyboardLayoutEditorWindow>().ToArray()) if (window.IsVisible) SessionWindowCoordinator.Close(window);
            host.SetVisible(false); toolbar.Hide();
        }
        check("layout modal regression never switches to or samples the user's input desktop", !PresentationDesktopAvailability.IsCurrentInputDesktop());
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    { yield return node; foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) foreach (var descendant in Descendants(child)) yield return descendant; }
    private static T Entry<T>(PresentationSettingsPanel panel, string key) where T : FrameworkElement
    { var entry = Field<IDictionary>(panel, "entries")[key]!; return (T)entry.GetType().GetProperty("Control")!.GetValue(entry)!; }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", Hidden)!.Invoke(button, null);
    private static ScrollViewer PreviewScroll(KeyboardLayoutEditorWindow window) => Descendants(window).OfType<ScrollViewer>().Single(scroll => ReferenceEquals(scroll.Content, Field<Canvas>(window, "preview")));
    private static void Capture(Window window, string path)
    {
        var visual = (FrameworkElement)VisualTreeHelper.GetChild(window, 0); visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static void Call(object target, string name) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, null);
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    private static void RequirePrivateDesktop()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _) || !name.ToString().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Use --isolated-ui-desktop --keyboard-layout-modal-only.");
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder name, uint size, out uint needed);
}
