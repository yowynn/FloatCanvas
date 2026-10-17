using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FloatCanvas.App;
using L = FloatCanvas.Localization.AppText;
using DrawingPath = System.Windows.Shapes.Path;

/// <summary>Real routed Click handlers and detached production controls; no visible windows or input injection.</summary>
internal static class ClearCanvasButtonTests
{
    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string priorLanguage = L.Preference;
        var priorContext = SynchronizationContext.Current;
        var initialWindows = app.Windows.Cast<Window>().ToArray();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        try
        {
            ClickAndBusy(check);
            CancelAndGate(check);
            FailedClearRestores(check);
            foreach (string language in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(language); AppTheme.Apply(theme);
                Appearance(check, app, language, theme);
            }
            check("clear-canvas checks create no native or visible windows", app.Windows.Cast<Window>().SequenceEqual(initialWindows));
        }
        finally
        {
            L.Initialize(priorLanguage); AppTheme.Apply("light");
            SynchronizationContext.SetSynchronizationContext(priorContext);
        }
    }

    private static void ClickAndBusy(Action<string, bool> check)
    {
        int clears = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var button = new ClearCanvasButton(() => true, () => { clears++; return completion.Task; });
        string original = Glyph(button).Data.ToString();
        Click(button);
        check("first routed click arms confirmation without clearing", button.IsConfirming && clears == 0 && button.IsEnabled);
        check("confirmation swaps only the glyph within the existing button", Glyph(button).Data.ToString() != original && button.Width == 26 && button.Height == 26);
        Click(button);
        check("second routed click clears once and disables the same target while running", clears == 1 && !button.IsConfirming && !button.IsEnabled && !button.ConfirmationPopup.IsOpen);
        Click(button);
        check("even a third routed click cannot launch a second clear while busy", clears == 1 && !button.IsConfirming && !button.IsEnabled);
        completion.SetResult(); PumpUntil(() => button.IsEnabled);
        check("completed clear restores the initial trash action", !button.IsConfirming && Glyph(button).Data.ToString() == original && button.ToolTip as string == L.Get("Feedback4.Toolbar.ClearCanvas"));
        Click(button);
        check("a later first click requires a new confirmation instead of clearing again", button.IsConfirming && clears == 1);
        button.CancelConfirmation();
    }

    private static void CancelAndGate(Action<string, bool> check)
    {
        bool allowed = true; int clears = 0;
        var button = new ClearCanvasButton(() => allowed, () => { clears++; return Task.CompletedTask; });
        Click(button); button.CancelConfirmation();
        check("cancelling confirmation restores the idle action without clearing", !button.IsConfirming && clears == 0 && !button.ConfirmationPopup.IsOpen);
        Click(button);
        check("the first click after cancellation only arms again", button.IsConfirming && clears == 0);
        Click(button);
        check("the second click after cancellation performs exactly one clear", !button.IsConfirming && button.IsEnabled && clears == 1);
        allowed = false; Click(button); Click(button);
        check("a closed document-operation gate refuses both arming and clearing", !button.IsConfirming && clears == 1);
        allowed = true; Click(button); allowed = false; Click(button);
        check("a gate closing between the two clicks cancels instead of clearing", !button.IsConfirming && clears == 1);
        allowed = true; Click(button);
        button.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        check("unloading the toolbar cancels a pending confirmation", !button.IsConfirming && !button.ConfirmationPopup.IsOpen && clears == 1);
        Click(button);
        check("reopening after unload still requires two clicks", button.IsConfirming && clears == 1);
        button.CancelConfirmation(); button.CancelConfirmation();
        check("repeated cancellation is safe and preserves the idle control", !button.IsConfirming && button.IsEnabled && clears == 1);
    }

    private static void FailedClearRestores(Action<string, bool> check)
    {
        int clears = 0;
        var button = new ClearCanvasButton(() => true, () => { clears++; return Task.FromException(new IOException("fixture clear failure")); });
        Await(button.ActivateAsync());
        bool threw = false;
        try { Await(button.ActivateAsync()); }
        catch (IOException ex) when (ex.Message == "fixture clear failure") { threw = true; }
        check("a failed clear propagates to its caller while restoring the idle button", threw && clears == 1 && button.IsEnabled && !button.IsConfirming && !button.ConfirmationPopup.IsOpen);
        Await(button.ActivateAsync());
        check("a failed clear never leaves an armed destructive retry", button.IsConfirming && clears == 1);
        button.CancelConfirmation();
    }

    private static void Appearance(Action<string, bool> check, Application app, string language, string theme)
    {
        string context = language + " " + theme;
        var button = new ClearCanvasButton(() => true, () => Task.CompletedTask);
        var host = new Border { Width = 280, Height = 80, Padding = new Thickness(12), Child = button };
        host.Resources["CanvasButtonCornerRadius"] = new CornerRadius(3);
        host.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
        button.HorizontalAlignment = HorizontalAlignment.Left; button.VerticalAlignment = VerticalAlignment.Top;
        Layout(host);
        var chrome = (Border)button.Template.FindName("Chrome", button);
        var normal = ((SolidColorBrush)chrome.Background).Color;
        check(context + " idle name and tooltip describe clearing the canvas", AutomationProperties.GetName(button) == L.Get("Feedback4.Toolbar.ClearCanvas") && button.ToolTip as string == L.Get("Feedback4.Toolbar.ClearCanvas"));
        check(context + " danger action stays a compact 26-DIP target with the toolbar's 3-DIP corners", button.ActualWidth == 26 && button.ActualHeight == 26 && chrome.CornerRadius == new CornerRadius(3));
        check(context + " normal action paints the red danger surface with a white glyph", normal == ((SolidColorBrush)app.Resources["CanvasDanger"]).Color && normal.R > normal.G * 2 && Glyph(button).Stroke == Brushes.White && Glyph(button).StrokeThickness == 1.5);
        var popup = button.ConfirmationPopup;
        var bubble = (Border)popup.Child;
        var hint = (TextBlock)bubble.Child;
        check(context + " confirmation bubble is a noninteractive nonactivating hint beside the same target", popup.PlacementTarget == button && popup.Placement == PlacementMode.Bottom && popup.VerticalOffset > 0 && popup.AllowsTransparency && popup.StaysOpen && !popup.Focusable && !popup.IsHitTestVisible);
        check(context + " confirmation question is localized plain text", hint.Text == (language == "zh-CN" ? "确定清除吗？" : "Clear canvas?") && !string.IsNullOrWhiteSpace(hint.Text));
        check(context + " confirmation hint uses the same red emphasis as the action with white text", ((SolidColorBrush)bubble.Background).Color == normal && ((SolidColorBrush)bubble.BorderBrush).Color == normal && hint.Foreground == Brushes.White);
        SavePreview(host, "clear-" + language + "-" + theme + "-idle.png");
        Click(button); Layout(host);
        check(context + " armed action has its localized accessible name and no competing tooltip", AutomationProperties.GetName(button) == L.Get("Feedback4.Toolbar.ClearConfirm") && button.ToolTip is null);
        check(context + " armed action preserves the red surface white glyph and hit target geometry", ((SolidColorBrush)chrome.Background).Color == normal && Glyph(button).Stroke == Brushes.White && button.ActualWidth == 26 && button.ActualHeight == 26 && chrome.CornerRadius == new CornerRadius(3));
        SavePreview(host, "clear-" + language + "-" + theme + "-confirm.png");
        // Render the actual popup child detached, avoiding Popup.IsOpen and its native HWND.
        popup.Child = null;
        Layout(bubble); SavePreview(bubble, "clear-" + language + "-" + theme + "-hint.png");
        popup.Child = bubble;
        button.CancelConfirmation(); host.Child = null;
    }

    private static DrawingPath Glyph(ClearCanvasButton button) => (DrawingPath)((Canvas)((Viewbox)button.Content).Child).Children[0];
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        view.Arrange(new Rect(new Point(), view.DesiredSize)); view.UpdateLayout();
    }
    private static void SavePreview(FrameworkElement view, string filename)
    {
        string? directory = Environment.GetEnvironmentVariable("FLOATCANVAS_CLEAR_CANVAS_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(view.ActualWidth)), Math.Max(1, (int)Math.Ceiling(view.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(System.IO.Path.Combine(directory, filename)); encoder.Save(stream);
    }
    private static void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> complete)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!complete())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Clear canvas fixture did not complete.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
