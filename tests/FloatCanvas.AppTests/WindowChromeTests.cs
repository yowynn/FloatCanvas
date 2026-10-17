using FloatCanvas.App;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;
using Media = System.Windows.Media;
using Shell = System.Windows.Shell;
using Peers = System.Windows.Automation.Peers;

/// <summary>Detached WPF templates: no Show, native window, or injected input.</summary>
internal static class WindowChromeTests
{
    public static void Run(Action<string, bool> assert)
    {
        var app = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        foreach (string mode in new[] { "light", "dark" })
        {
            AppTheme.Apply(mode);
            var body = new Controls.TextBox { Text = "Editable content" };
            var window = new Wpf.Window { Title = "FloatCanvas test", Content = body, Width = 360, Height = 240 };
            AppWindowChrome.Apply(window);
            window.ApplyTemplate();
            var close = (Controls.Button)window.Template.FindName("Close", window);
            var help = (Controls.Button)window.Template.FindName("Help", window);
            var chrome = Shell.WindowChrome.GetWindowChrome(window);
            assert(mode + " shared panel preserves the exact content and a compact draggable caption", ReferenceEquals(window.Content, body) &&
                window.WindowStyle == Wpf.WindowStyle.None && chrome.CaptionHeight == AppWindowChrome.CaptionHeight &&
                chrome.CaptionHeight == 36 && chrome.ResizeBorderThickness.Left > 0 && !chrome.UseAeroCaptionButtons);
            assert(mode + " caption buttons retain close invoke and accessible localized names", close.Command == Wpf.SystemCommands.CloseWindowCommand &&
                close.CommandTarget == window && Shell.WindowChrome.GetIsHitTestVisibleInChrome(close) &&
                Peers.FrameworkElementAutomationPeer.CreatePeerForElement(close).GetPattern(Peers.PatternInterface.Invoke) is not null &&
                System.Windows.Automation.AutomationProperties.GetName(close) == FloatCanvas.Localization.AppText.Get("Window.Close"));
            assert(mode + " floating panels offer neither minimize nor maximize controls or routed commands",
                window.Template.FindName("Minimize", window) is null && window.Template.FindName("Maximize", window) is null &&
                !Wpf.SystemCommands.MinimizeWindowCommand.CanExecute(null, window) &&
                !Wpf.SystemCommands.MaximizeWindowCommand.CanExecute(null, window));
            assert(mode + " panels do not repeat the application icon in their compact header",
                !Descendants((Wpf.DependencyObject)window.Template.FindName("TitleBar", window)).OfType<Controls.Image>().Any());
            assert(mode + " header help is optional until meaningful help is supplied", help.Visibility == Wpf.Visibility.Collapsed);
            AppWindowChrome.SetHelp(window, "Layer shortcuts", "Ctrl+D · Duplicate\nDelete · Delete selection");
            AppWindowChrome.SetHeaderDetail(window, "2 selected");
            var detail = (Controls.TextBlock)window.Template.FindName("HeaderDetail", window);
            assert(mode + " a panel can show selection context and discoverable help in its header", help.Visibility == Wpf.Visibility.Visible &&
                detail.Text == "2 selected" && System.Windows.Automation.AutomationProperties.GetName(help) == "Layer shortcuts" &&
                window.Template.FindName("HelpPopup", window) is Primitives.Popup);
            var closeGlyph = close.Content as System.Windows.Shapes.Path;
            var helpGlyph = help.Content as System.Windows.Shapes.Path;
            assert(mode + " help and close share the same vector size stroke and button target", close.Width == 28 && close.Height == 28 &&
                help.Width == close.Width && help.Height == close.Height && closeGlyph is not null && helpGlyph is not null &&
                closeGlyph.Width == 16 && closeGlyph.Height == 16 && helpGlyph.Width == 16 && helpGlyph.Height == 16 &&
                closeGlyph.StrokeThickness == 1.5 && helpGlyph.StrokeThickness == closeGlyph.StrokeThickness &&
                closeGlyph.StrokeStartLineCap == helpGlyph.StrokeStartLineCap && closeGlyph.StrokeEndLineCap == helpGlyph.StrokeEndLineCap);
            var grip = (Wpf.FrameworkElement)window.Template.FindName("ResizeGrip", window);
            var gripPaths = Descendants(grip).OfType<System.Windows.Shapes.Path>().ToArray();
            assert(mode + " resize grip keeps a small two-line cue within a usable transparent corner target", grip.Width == 20 && grip.Height == 20 &&
                gripPaths.Length > 0 && gripPaths.All(path => path.Data.Bounds.Width <= 12 && path.Data.Bounds.Height <= 12 &&
                    path.StrokeDashArray.Count == 0) && gripPaths.Sum(path => path.Data.GetFlattenedPathGeometry().Figures.Count) == 2);
            int bindings = window.CommandBindings.Count;
            AppWindowChrome.Apply(window);
            assert(mode + " reapplying chrome does not duplicate window commands", window.CommandBindings.Count == bindings);
            AppWindowChrome.ApplyDialog(window);
            window.ApplyTemplate();
            assert(mode + " dialog title bar has close without minimize or maximize", ((Controls.Button)window.Template.FindName("Close", window)).Visibility == Wpf.Visibility.Visible &&
                window.Template.FindName("Minimize", window) is null && window.Template.FindName("Maximize", window) is null);
            assert(mode + " title bar remains detached from native desktop", !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0);
            window.Close();

            var content = new Controls.Border { Width = 600, Height = 900 };
            var scroll = new Controls.ScrollViewer { Width = 180, Height = 120, Content = content,
                VerticalScrollBarVisibility = Controls.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Controls.ScrollBarVisibility.Auto };
            Layout(scroll);
            var vertical = (Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            var horizontal = (Primitives.ScrollBar)scroll.Template.FindName("PART_HorizontalScrollBar", scroll);
            scroll.ScrollToVerticalOffset(100); scroll.ScrollToHorizontalOffset(70); Layout(scroll);
            assert(mode + " shared scroll viewer retains real overflow and scroll offsets", scroll.ScrollableHeight > 700 && scroll.ScrollableWidth > 400 &&
                scroll.VerticalOffset == 100 && scroll.HorizontalOffset == 70 && vertical.Visibility == Wpf.Visibility.Visible && horizontal.Visibility == Wpf.Visibility.Visible);
            assert(mode + " horizontal and vertical scrollbars use custom theme tracks", vertical.Template.FindName("PART_Track", vertical) is Primitives.Track &&
                horizontal.Template.FindName("PART_Track", horizontal) is Primitives.Track && vertical.Width == 12 && horizontal.Height == 12);

            var check = new Controls.CheckBox { Content = "Layer visible", IsChecked = true, Width = 160, Height = 32 };
            Layout(check);
            var checkPeer = Peers.FrameworkElementAutomationPeer.CreatePeerForElement(check);
            assert(mode + " themed checkbox retains toggle pattern and custom check mark", checkPeer.GetPattern(Peers.PatternInterface.Toggle) is not null &&
                ((Wpf.UIElement)check.Template.FindName("Check", check)).Visibility == Wpf.Visibility.Visible);
            check.IsThreeState = true; check.IsChecked = null;
            assert(mode + " themed checkbox renders its indeterminate state", ((Wpf.UIElement)check.Template.FindName("Mixed", check)).Visibility == Wpf.Visibility.Visible);
            var slider = new Controls.Slider { Width = 160, Height = 30, Minimum = 1, Maximum = 40, Value = 12,
                Style = (Wpf.Style)app.FindResource(typeof(Controls.Slider)) };
            Layout(slider);
            var sliderTrack = (Primitives.Track)slider.Template.FindName("PART_Track", slider);
            assert(mode + " themed slider retains its native range and track binding", sliderTrack.Value == 12 &&
                Peers.FrameworkElementAutomationPeer.CreatePeerForElement(slider).GetPattern(Peers.PatternInterface.RangeValue) is not null);

            var menu = new Controls.MenuItem { Header = "Export", InputGestureText = "Ctrl+E", Icon = new Controls.TextBlock { Text = "+" }, Width = 250, Height = 40 };
            menu.Items.Add(new Controls.MenuItem { Header = "PNG" }); Layout(menu);
            assert(mode + " menu template preserves separate label shortcut icon and submenu", menu.Template.FindName("Icon", menu) is Controls.ContentPresenter &&
                menu.Template.FindName("PART_Popup", menu) is Primitives.Popup &&
                ((Wpf.UIElement)menu.Template.FindName("SubmenuArrow", menu)).Visibility == Wpf.Visibility.Visible);
        }
        AppTheme.Apply("light");
        var themed = new Controls.CheckBox { Width = 100, Height = 30, Style = (Wpf.Style)app.FindResource(typeof(Controls.CheckBox)) };
        var themeWindow = new Wpf.Window { Content = themed };
        AppWindowChrome.ApplyDialog(themeWindow); themeWindow.ApplyTemplate(); Layout(themed);
        var border = (Controls.Border)themed.Template.FindName("CheckBorder", themed);
        var light = ((Media.SolidColorBrush)border.Background).Color;
        AppTheme.Apply("dark");
        Layout(themed);
        assert("existing custom control changes palette without rebuilding its template", ((Media.SolidColorBrush)border.Background).Color != light);
        themeWindow.Close();
        assert("shared chrome checks leave no native or visible windows", app.Windows.Cast<Wpf.Window>().All(window =>
            !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0));
    }

    private static void Layout(Wpf.FrameworkElement element)
    {
        element.Measure(new Wpf.Size(element.Width, element.Height));
        element.Arrange(new Wpf.Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
    }

    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < Media.VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(Media.VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
