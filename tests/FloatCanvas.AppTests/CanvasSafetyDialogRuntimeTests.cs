using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Production safety dialog layout and bounded native preview. No desktop input injection or user profile.</summary>
internal static class CanvasSafetyDialogRuntimeTests
{
    internal static void Run(Action<string, bool> check)
    {
        WithEnvironment((app, output) =>
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            foreach (bool canRelocate in new[] { true, false })
            {
                L.Initialize(locale); AppTheme.Apply(theme);
                var dialog = CanvasSafetyDialog.Create(null, 3, canRelocate);
                var window = dialog.Window;
                window.ShowInTaskbar = true;
                window.ApplyTemplate();
                var content = (DockPanel)window.Content;
                window.Content = null;
                var root = new Border { Width = window.Width, Child = content, Background = window.Background };
                root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, window.Foreground);
                root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
                root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
                string name = $"{locale} {theme} relocate={canRelocate}";
                using var source = new HwndSource(new HwndSourceParameters("FloatCanvas hidden perspective safety dialog")
                { Width = 460, Height = 600, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080 });
                try
                {
                    source.RootVisual = root;
                    root.Measure(new Size(460, double.PositiveInfinity));
                    double height = Math.Ceiling(root.DesiredSize.Height);
                    root.Arrange(new Rect(0, 0, 460, height)); root.UpdateLayout(); Pump();
                    Verify(check, name, window, content, canRelocate, root);
                    check(name + " uses a hidden native source without showing the dialog", PresentationSource.FromVisual(content) is HwndSource && !window.IsVisible &&
                        new WindowInteropHelper(window).Handle == 0);
                    Render(root, Path.Combine(output, $"dialog-{locale}-{theme}-{canRelocate}.png"));
                    var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    window.RaiseEvent(escape);
                    check(name + " production Escape route cancels the adjustment", escape.Handled && dialog.Result() == CanvasSafetyAction.Cancel && !window.IsVisible);
                }
                finally { root.Child = null; source.RootVisual = null; window.Close(); }
                var close = CanvasSafetyDialog.Create(null, 3, canRelocate);
                close.Window.Close();
                check(name + " closing the window without an action cancels", close.Result() == CanvasSafetyAction.Cancel);
            }
            check("dialog checks retain their independent profile", string.Equals(ApplicationPaths.ProfileDirectory,
                Path.Combine(output, "profile"), StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>Show the two real production modal windows, advancing on close/cancel or a 60 second timeout each.</summary>
    internal static void RunPreview(Action<string, bool> check)
    {
        WithEnvironment((app, output) =>
        {
            foreach (var sample in new[] { (Locale: "zh-CN", Theme: "light", CanRelocate: true), (Locale: "en", Theme: "dark", CanRelocate: false) })
            {
                L.Initialize(sample.Locale); AppTheme.Apply(sample.Theme);
                var dialog = CanvasSafetyDialog.Create(null, 3, sample.CanRelocate);
                var window = dialog.Window;
                window.ShowInTaskbar = true;
                var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(60) };
                bool timedOut = false;
                Exception? callbackFailure = null;
                timer.Tick += (_, _) => { timedOut = true; timer.Stop(); SessionWindowCoordinator.Close(window); };
                window.Closed += (_, _) => timer.Stop();
                window.ContentRendered += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                {
                    try
                    {
                        var content = (DockPanel)window.Content;
                        Verify(check, $"native {sample.Locale} {sample.Theme}", window, content, sample.CanRelocate, content);
                        var buttons = Buttons(content);
                        var focused = Keyboard.FocusedElement as FrameworkElement;
                        Console.WriteLine($"PREVIEW_READY locale={sample.Locale} theme={sample.Theme} canRelocate={sample.CanRelocate} hwnd={new WindowInteropHelper(window).Handle:X} " +
                            $"keyboardFocus={focused?.GetType().Name}:{(focused is null ? "" : AutomationProperties.GetAutomationId(focused))} defaultAction={AutomationProperties.GetAutomationId(buttons.Single(b => b.IsDefault))} timeoutSeconds=60");
                        Console.Out.Flush();
                        Render(window, Path.Combine(output, $"native-dialog-{sample.Locale}-{sample.Theme}.png"));
                        timer.Start();
                    }
                    catch (Exception error) { callbackFailure = error; SessionWindowCoordinator.Close(window); }
                });
                try { SessionWindowCoordinator.ShowModal(window); }
                finally { timer.Stop(); if (window.IsVisible) window.Close(); }
                if (callbackFailure is not null) throw callbackFailure;
                check($"native {sample.Locale} {sample.Theme} dialog exits with a known decision", Enum.IsDefined(dialog.Result()));
                Console.WriteLine($"PREVIEW_CLOSED locale={sample.Locale} action={dialog.Result()} timedOut={timedOut}"); Console.Out.Flush();
            }
        });
    }

    private static void Verify(Action<string, bool> check, string name, Window window, DockPanel content, bool canRelocate, FrameworkElement root)
    {
        var buttons = Buttons(content);
        var body = (TextBlock)((ScrollViewer)content.Children[1]).Content;
        check(name + " presents the localized layer count and all three explicit decisions", window.Title == L.Get("Canvas.SafetyTitle") && body.Text.Contains(L.Format("Canvas.UnsafeLayers", 3), StringComparison.Ordinal) &&
            buttons.Length == 3 && Equals(buttons[0].Content, L.Get("Canvas.ApplyRelocate")) && Equals(buttons[1].Content, L.Get("Canvas.ApplyDelete")) &&
            Equals(buttons[2].Content, L.Get("Canvas.CancelAdjustment")));
        check(name + " explains an unavailable relocation and disables only that action", buttons[0].IsEnabled == canRelocate && buttons[1].IsEnabled && buttons[2].IsEnabled &&
            body.Text.Contains(L.Get("Canvas.RelocateFailed"), StringComparison.Ordinal) == !canRelocate);
        check(name + " defaults to relocation or cancellation and Escape is cancellation", buttons.Count(b => b.IsDefault) == 1 &&
            buttons[canRelocate ? 0 : 2].IsDefault && buttons[2].IsCancel && buttons.All(b => b.Focusable));
        check(name + " exposes stable accessibility action identifiers", buttons.Select(AutomationProperties.GetAutomationId)
            .SequenceEqual(new[] { "RelocateUnsafeLayers", "DeleteUnsafeLayers", "CancelCanvasAdjustment" }));
        check(name + " lays out full width actions with visible text", buttons.All(button =>
        {
            var text = new FormattedText((string)button.Content, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize, button.Foreground,
                VisualTreeHelper.GetDpi(button).PixelsPerDip);
            return button.ActualWidth >= 350 && button.ActualHeight >= 34 && text.WidthIncludingTrailingWhitespace <= button.ActualWidth - button.Padding.Left - button.Padding.Right + 1 &&
                text.Height <= button.ActualHeight - button.Padding.Top - button.Padding.Bottom + 1;
        }));
        var positions = buttons.Select(button => button.TranslatePoint(new Point(), root)).ToArray();
        check(name + " separates each action without overlapping or clipping the content", positions[1].Y >= positions[0].Y + buttons[0].ActualHeight + 7 &&
            positions[2].Y >= positions[1].Y + buttons[1].ActualHeight + 7 && buttons.All(button =>
            {
                var point = button.TranslatePoint(new Point(), root);
                return point.X >= -1 && point.Y >= -1 && point.X + button.ActualWidth <= root.ActualWidth + 1 && point.Y + button.ActualHeight <= root.ActualHeight + 1;
            }));
        check(name + " uses the selected opaque application palette", window.Background is SolidColorBrush { Color.A: 255 } background &&
            background.Color == ((SolidColorBrush)Application.Current.Resources["CanvasSurface"]).Color && buttons.All(b => b.Foreground is SolidColorBrush { Color.A: 255 }));
    }

    private static Button[] Buttons(DockPanel content) => ((StackPanel)content.Children[0]).Children.OfType<Button>().ToArray();

    private static void WithEnvironment(Action<Application, string> run)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = app.Resources;
        string language = L.Preference;
        string oldProfile = ApplicationPaths.ProfileDirectory;
        var oldScope = ApplicationPaths.RequestedMonitorScope;
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_SAFETY_DIALOG_OUTPUT") ?? Path.Combine(
            Path.GetTempPath(), "FloatCanvasSafetyDialogTests", Guid.NewGuid().ToString("N"));
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        ApplicationPaths.Configure(["--profile", Path.Combine(output, "profile"), "--primary-screen"]);
        app.Resources = new ResourceDictionary();
        try
        {
            new UserSettings { Language = "zh-CN", Theme = "light" }.Save();
            Console.WriteLine("SAFETY_DIALOG_EVIDENCE " + output); Console.Out.Flush();
            run(app, output);
        }
        finally
        {
            app.Resources = resources; L.Initialize(language);
            var args = new List<string> { "--profile", oldProfile };
            if (oldScope is OverlayMonitorScope.PrimaryScreen) args.Add("--primary-screen");
            else if (oldScope is OverlayMonitorScope.AllScreens) args.Add("--all-screens");
            ApplicationPaths.Configure(args.ToArray());
        }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Render(FrameworkElement root, string file)
    {
        root.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth)), height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(file); encoder.Save(stream);
    }
}
