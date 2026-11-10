using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using L = FloatCanvas.Localization.AppText;

internal static class CanvasModeSwitchTests
{
    // Deliberately independent of Program.cs; the integrating runner can call Run.
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string language = L.Preference;
        if (output is not null) Directory.CreateDirectory(output);
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(locale); AppTheme.Apply(theme);
                string prefix = locale + "/" + theme + " ";
                VerifyPointerFocus(check, prefix);
                var layers = new LayersWindow(() => new FloatCanvas.Core.EditorSession()); layers.ApplyTemplate();
                var help = (Button)layers.Template.FindName("Help",layers); var close = (Button)layers.Template.FindName("Close",layers);
                var switchInCaption = ((StackPanel)layers.Resources["CanvasWindowActions"]).Children.OfType<CanvasModeSwitch>().Single();
                check(prefix + "mode-help and help-close have equal caption spacing", switchInCaption.Margin.Right + help.Margin.Left == help.Margin.Right + close.Margin.Left);
                layers.Close();
                foreach (var mode in new[] { GroupCanvasMode.Temporary, GroupCanvasMode.RootProject })
                {
                    var control = new CanvasModeSwitch(); control.Synchronize(mode, true); Layout(control);
                    var surface = (Grid)((Grid)control.Content).Children[0];
                    var left = (Border)surface.Children[0]; var right = (Border)surface.Children[1]; var handle = (Border)surface.Children[2];
                    bool independent = mode == GroupCanvasMode.Temporary;
                    Rect cover = handle.TransformToAncestor(surface).TransformBounds(new Rect(handle.RenderSize));
                    var active = independent ? left : right; var inactive = independent ? right : left;
                    Rect activeBounds = active.TransformToAncestor(surface).TransformBounds(new Rect(active.RenderSize));
                    Rect inactiveBounds = inactive.TransformToAncestor(surface).TransformBounds(new Rect(inactive.RenderSize));
                    check(prefix + mode + " keeps caption size and frame", control.ActualWidth == 62 && control.ActualHeight == 28 &&
                        ReferenceEquals(control.Style, Application.Current!.FindResource("CanvasCaptionButton")) && control.BorderThickness == new Thickness(1));
                    check(prefix + mode + " current icon travels on the selected tile", cover.Contains(activeBounds) &&
                        Rect.Intersect(cover, inactiveBounds).Width <= 0 && (independent ? cover.Left == 0 : cover.Left == 28) &&
                        handle.Child is Viewbox && IconPath(handle.Child).Data.ToString() == Geometry.Parse(independent ? CanvasModeVisuals.Independent : CanvasModeVisuals.Project).ToString());
                    var selectedInk = ((SolidColorBrush)IconPath(handle.Child).Stroke).Color;
                    var inactiveInk = ((SolidColorBrush)IconPath(inactive.Child).Stroke).Color;
                    check(prefix + mode + " selected icon has strong contrast on its accent tile", SystemParameters.HighContrast ||
                        Contrast(((SolidColorBrush)handle.Background).Color,selectedInk) >= 4.5);
                    check(prefix + mode + " selected and inactive icons have distinct emphasis without hiding the alternative", SystemParameters.HighContrast ||
                        Contrast(selectedInk,inactiveInk) >= 2 && Contrast(inactiveInk,((SolidColorBrush)control.Background).Color) >= 3);
                    var a = ((SolidColorBrush)control.FindResource("CanvasModeIndependent")).Color;
                    var b = ((SolidColorBrush)control.FindResource("CanvasModeProject")).Color;
                    check(prefix + mode + " uses two active accents", SystemParameters.HighContrast ||
                        (a != b && a.R != a.G && b.R != b.G && Contrast(a, Colors.White) >= 4.5 && Contrast(b, Colors.White) >= 4.5));
                    check(prefix + mode + " uses an opaque cover", handle.Background is SolidColorBrush { Color.A: 255, Opacity: 1 });
                    foreach (double x in new[] { 1d, 15d, 31d, 47d, 61d })
                    {
                        // Detached rendering has no input source; inspect visual
                        // coverage and the explicitly inert content instead.
                        var hit = VisualTreeHelper.HitTest(control, new Point(x, 14))?.VisualHit;
                        while (hit is not null && hit != control) hit = VisualTreeHelper.GetParent(hit);
                        check(prefix + mode + " has one click target at " + x, hit == control && !((Grid)control.Content).IsHitTestVisible);
                    }
                    int requests = 0; control.Click += (_, _) => requests++;
                    Click(control);
                    check(prefix + mode + " rejected request preserves state and handle", requests == 1 && control.Mode == mode &&
                        handle.TransformToAncestor(surface).TransformBounds(new Rect(handle.RenderSize)) == cover);
                    control.Synchronize(mode, true); Layout(control);
                    check(prefix + mode + " repeated owner refresh preserves handle", handle.TransformToAncestor(surface).TransformBounds(new Rect(handle.RenderSize)) == cover);
                    var next = independent ? GroupCanvasMode.RootProject : GroupCanvasMode.Temporary;
                    control.Synchronize(next, true); Layout(control);
                    check(prefix + mode + " accepted request moves the cover", control.Mode == next &&
                        ((TranslateTransform)handle.RenderTransform).X == (independent ? 28 : 0) &&
                        IconPath(handle.Child).Data.ToString() == Geometry.Parse(independent ? CanvasModeVisuals.Project : CanvasModeVisuals.Independent).ToString());
                    control.Synchronize(mode, true); Layout(control);
                    check(prefix + mode + " owner reversal restores the cover", handle.TransformToAncestor(surface).TransformBounds(new Rect(handle.RenderSize)) == cover);
                    VerifyKeyboard(check, prefix + mode + " ", control, () => requests);
                    var peer = UIElementAutomationPeer.CreatePeerForElement(control)!;
                    check(prefix + mode + " exposes one accessible button with current status", peer.GetAutomationControlType() == AutomationControlType.Button &&
                        peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider && peer.GetName() == L.Get("GroupCanvas.Display") &&
                        AutomationProperties.GetItemStatus(control) == L.Get(independent ? "GroupCanvas.Temporary" : "GroupCanvas.RootProject") &&
                        AutomationProperties.GetLiveSetting(control) == AutomationLiveSetting.Polite);
                    control.Synchronize(mode, false, "Busy reason"); int before = requests; Click(control);
                    SendKey(control, Key.Enter, Keyboard.PreviewKeyDownEvent); SendKey(control, Key.Space, Keyboard.PreviewKeyDownEvent);
                    check(prefix + mode + " disabled state is inert and explains why", requests == before && control.Mode == mode &&
                        ToolTipService.GetShowOnDisabled(control) && AutomationProperties.GetHelpText(control).EndsWith("\nBusy reason", StringComparison.Ordinal) &&
                        ((StackPanel)control.ToolTip).Children.OfType<TextBlock>().Last().Text == "Busy reason");
                    control.Synchronize(mode, false, " ");
                    check(prefix + mode + " supplies the localized default disabled reason", AutomationProperties.GetHelpText(control).EndsWith(L.Get("GroupCanvas.SwitchUnavailable"), StringComparison.Ordinal));
                    control.Synchronize(mode, true);
                    check(prefix + mode + " clears stale disabled help", !AutomationProperties.GetHelpText(control).Contains("Busy reason", StringComparison.Ordinal) &&
                        AutomationProperties.GetHelpText(control) == L.Get(independent ? "GroupCanvas.SwitchIndependent" : "GroupCanvas.SwitchProject"));
                    if (output is not null)
                    foreach (double scale in new[] { 1d, 1.5, 2d }) Save(control, Path.Combine(output, $"switch-{locale}-{theme}-{mode}-{scale}.png"), scale);
                    AppTheme.ApplyPalette(control.Resources, theme == "dark", highContrast:true);
                    check(prefix + mode + " live resource changes honor high contrast for both states", ((SolidColorBrush)handle.Background).Color == SystemColors.HighlightColor &&
                        ((SolidColorBrush)IconPath(handle.Child).Stroke).Color == SystemColors.HighlightTextColor &&
                        ((SolidColorBrush)IconPath(inactive.Child).Stroke).Color == SystemColors.WindowTextColor);
                }
            }
        }
        finally { L.Initialize(language); AppTheme.Apply("light"); }
    }

    private static void VerifyPointerFocus(Action<string, bool> check, string prefix)
    {
        var previous = new Button { Content = "Layer list" };
        var control = new CanvasModeSwitch(); control.Synchronize(GroupCanvasMode.RootProject, true);
        var content = new StackPanel(); content.Children.Add(previous); content.Children.Add(control);
        var window = new Window { Width = 240, Height = 140, Content = content, ShowInTaskbar = false };
        try
        {
            window.Show(); window.Activate(); previous.Focus();
            check(prefix + "pointer focus fixture starts on the list", previous.IsKeyboardFocused);
            control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            check(prefix + "pointer activation does not leave Space focused on the mode switch", previous.IsKeyboardFocused && !control.IsKeyboardFocused);
            control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            check(prefix + "mode remains reachable by keyboard", control.Focus() && control.IsKeyboardFocused);
            int requests = 0; control.Click += (_, _) => requests++;
            SendKey(control, Key.Space, Keyboard.PreviewKeyDownEvent);
            check(prefix + "explicit keyboard focus still activates the mode switch", requests == 1);
        }
        finally { window.Close(); }
    }

    private static void VerifyKeyboard(Action<string, bool> check, string prefix, CanvasModeSwitch control, Func<int> requests)
    {
        foreach (var key in new[] { Key.Enter, Key.Space })
        {
            int before = requests(); var mode = control.Mode;
            var down = SendKey(control, key, Keyboard.PreviewKeyDownEvent);
            var repeat = SendKey(control, key, Keyboard.PreviewKeyDownEvent, true);
            var up = SendKey(control, key, Keyboard.PreviewKeyUpEvent);
            SendKey(control, key, Keyboard.KeyUpEvent);
            check(prefix + key + " requests once across repeat and release", down.Handled && repeat.Handled && up.Handled && requests() == before + 1 && control.Mode == mode);
        }
    }

    private static KeyEventArgs SendKey(CanvasModeSwitch control, Key key, RoutedEvent routedEvent, bool repeat = false)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(control), 0, key) { RoutedEvent = routedEvent };
        if (repeat) typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(args, [true]);
        control.RaiseEvent(args); return args;
    }
    private static void Click(CanvasModeSwitch control) => typeof(CanvasModeSwitch).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, null);
    private static System.Windows.Shapes.Path IconPath(UIElement icon) => (System.Windows.Shapes.Path)((Canvas)((Viewbox)icon).Child).Children[0];
    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize)); element.UpdateLayout();
    }
    private static void Save(CanvasModeSwitch control, string path, double scale)
    {
        Layout(control);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(control.ActualWidth * scale), (int)Math.Ceiling(control.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(control); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Luminance(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        double a = Luminance(first), b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private sealed class DetachedSource(Visual visual) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = visual;
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
