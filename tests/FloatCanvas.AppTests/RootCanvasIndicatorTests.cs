using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class RootCanvasIndicatorTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (Application.Current is null) ProductionWiring(check, output);
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string language = L.Preference;
        if (output is not null) Directory.CreateDirectory(output);
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" }) foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(locale); AppTheme.Apply(theme); string prefix = locale + "/" + theme + " ";
                foreach (bool expanded in new[] { false, true })
                {
                    var baseline = ToolbarSurface(expanded); Layout(baseline);
                    var actual = ToolbarSurface(expanded); var file = File(); file.Refresh(null, false, expanded);
                    var header = new RootCanvasToolbarHeader(file, () => { }, () => true); header.Refresh(RootCanvasKind.Main); actual.Children.Add(header); Layout(actual);
                    check(prefix + "Main preserves historical toolbar dimensions expanded=" + expanded, header.Visibility == Visibility.Collapsed && actual.DesiredSize == baseline.DesiredSize);
                    file.Refresh(LongPath(locale), true, expanded);
                    check(prefix + "Main header immediately follows a standalone file-binding refresh expanded=" + expanded,
                        header.Visibility == (expanded ? Visibility.Visible : Visibility.Collapsed));
                    header.Refresh(RootCanvasKind.Main); Layout(actual);
                    check(prefix + "Main bound filename adds no toolbar size expanded=" + expanded, actual.DesiredSize == baseline.DesiredSize);
                }
                foreach (var root in new[] { RootCanvasKind.Foreground, RootCanvasKind.Background })
                foreach (bool dirty in new[] { false, true })
                foreach (double width in new[] { 420d, 480d, 800d })
                {
                    var file = File(); file.Refresh(LongPath(locale), dirty, true);
                    var header = new RootCanvasToolbarHeader(file, () => { }, () => true) { Margin = new(0) }; header.Refresh(root);
                    Layout(header, width, 32);
                    var button = Field<Button>(header, "returnToMain");
                    var fileBounds = file.TransformToAncestor(header).TransformBounds(new Rect(file.RenderSize));
                    var buttonBounds = button.TransformToAncestor(header).TransformBounds(new Rect(button.RenderSize));
                    var label = Field<TextBlock>(file, "label");
                    check(prefix + root + " header reserves separate filename and return bounds width=" + width + " dirty=" + dirty,
                        fileBounds.Right <= buttonBounds.Left + .1 && buttonBounds.Right <= header.ActualWidth + .1 &&
                        buttonBounds.Height == ToolbarUpperRow.ButtonSize && label.ActualWidth <= label.MaxWidth + .1 && label.MaxWidth <= 300 &&
                        label.TextTrimming == TextTrimming.CharacterEllipsis && (string)file.ToolTip == LongPath(locale));
                    if (width == 420 && dirty)
                    {
                        check(prefix + root + " narrow header truncates the filename while preserving the explicit root action", label.MaxWidth < 300 &&
                            AutomationProperties.GetName(button).Contains(L.Get("RootCanvas." + root), StringComparison.Ordinal) &&
                            Field<TextBlock>(header, "caption").ActualWidth > 0 && button.Visibility == Visibility.Visible);
                        if (output is not null) Save(header, width, 32, Path.Combine(output, $"header-{root}-{locale}-{theme}.png"));
                        string rootName = L.Get("RootCanvas." + root), groupName = locale == "en" ? "Sketch" : "草图组";
                        var description = new EditingScopeDescription(rootName + " › " + groupName, L.Get("GroupCanvas.Temporary"), "", [(rootName, () => { })], groupName);
                        header.SetScope(description, true); Layout(header,width,ToolbarUpperRow.ExtensionHeight);
                        var parent = Field<Button>(header,"scopeParent");
                        check(prefix + root + " keeps a separate Main-return icon before the scoped path", button.Visibility == Visibility.Visible &&
                            button.Content is Viewbox && button.TranslatePoint(new(button.ActualWidth,0),header).X <= parent.TranslatePoint(new(),header).X &&
                            Field<TextBlock>(header,"current").Text == groupName && Field<TextBlock>(header,"modeLabel").Visibility == Visibility.Collapsed &&
                            !Descendants(header).OfType<TextBlock>().Any(text => text.Text.Contains(" · ",StringComparison.Ordinal)));
                        if (output is not null) Save(header,width,ToolbarUpperRow.ExtensionHeight,Path.Combine(output,$"header-scoped-{root}-{locale}-{theme}.png"));
                        header.SetScope(description, false); Layout(header,width,32);
                    }
                    file.Refresh(LongPath(locale), dirty, false); header.Refresh(root); Layout(header, width, 32);
                    check(prefix + root + " remains readable with the expanded toolbar and filename hidden width=" + width + " dirty=" + dirty,
                        header.Visibility == Visibility.Visible && button.Visibility == Visibility.Visible && file.Visibility == Visibility.Collapsed);
                }
                InlineArtwork(check, prefix, output, locale, theme);
                CommandGate(check, prefix);
            }
        }
        finally { L.Initialize(language); AppTheme.Apply("light"); }
    }

    private static void ProductionWiring(Action<string, bool> check, string? output)
    {
        string profile = Path.Combine(output ?? Path.GetTempPath(), "root-indicator-profile-" + Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        new UserSettings { Theme = "light", Language = "en", ShowFloatingLogo = false }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), hidden, null, [OverlayMonitorScope.PrimaryScreen, false], null)!;
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
            using var desktop = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
            var editor = Field<EditorController>(desktop, "_editor");
            var header = Field<RootCanvasToolbarHeader>(desktop, "_rootCanvasHeader");
            var button = Field<Button>(header, "returnToMain");
            void Refresh() => typeof(DesktopSession).GetMethod("RefreshToolbarState", hidden)!.Invoke(desktop, null);
            editor.Session.SetRootCanvas(RootCanvasKind.Foreground); Refresh();
            check("production toolbar refresh reflects a root switch without expansion toggling", header.Visibility == Visibility.Visible &&
                button.IsEnabled && AutomationProperties.GetItemStatus(button) == L.Get("RootCanvas.Foreground"));
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, false); Refresh();
            check("production toolbar disables root return while the canvas is hidden", !button.IsEnabled && header.Visibility == Visibility.Visible);
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true); Refresh();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Refresh();
            check("production root return invokes DesktopSession navigation and hides the Main indicator", editor.Session.ActiveRootCanvas == RootCanvasKind.Main && header.Visibility == Visibility.Collapsed);
            var menu = Field<ContextMenu>(desktop, "_toolbarMenu");
            var billboard = menu.Items.OfType<MenuItem>().SelectMany(item => item.Items.OfType<MenuItem>()).Single(item => Equals(item.Tag, "billboard"));
            void RefreshMenu() => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            editor.Session.Document.Canvas.Projection.Enabled = true; RefreshMenu();
            check("production billboard menu is enabled on the perspective Main root", billboard.IsEnabled);
            foreach (var root in new[] { RootCanvasKind.Foreground, RootCanvasKind.Background })
            {
                editor.Session.SetRootCanvas(root); RefreshMenu();
                check("production billboard menu is disabled on the " + root + " screen-plane root", !billboard.IsEnabled);
            }
            editor.Session.SetRootCanvas(RootCanvasKind.Main); editor.Session.Document.Canvas.Projection.Enabled = false; RefreshMenu();
            check("production billboard menu is disabled on a flat Main root", !billboard.IsEnabled);
            editor.Session.Document.Canvas.Projection.Enabled = true;
            var busy = typeof(DesktopSession).GetProperty("_documentBusy", hidden)!;
            busy.SetValue(desktop, true);
            try
            {
                RefreshMenu();
                check("production billboard menu is disabled while the document is busy", !billboard.IsEnabled);
            }
            finally { busy.SetValue(desktop, false); }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void CommandGate(Action<string, bool> check, string prefix)
    {
        var session = new EditorSession(); session.SetRootCanvas(RootCanvasKind.Foreground);
        string authored = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount, returned = 0; bool allowed = true;
        var file = File(); var header = new RootCanvasToolbarHeader(file, () => { returned++; session.SetRootCanvas(RootCanvasKind.Main); }, () => allowed);
        header.Refresh(session.ActiveRootCanvas); var button = Field<Button>(header, "returnToMain");
        allowed = false; button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(prefix + "a stale enabled root action rechecks its current operation guard", returned == 0 && session.ActiveRootCanvas == RootCanvasKind.Foreground);
        header.Refresh(session.ActiveRootCanvas); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(prefix + "busy root action stays visible disabled and inert", !button.IsEnabled && button.Visibility == Visibility.Visible && returned == 0);
        allowed = true; header.Refresh(session.ActiveRootCanvas); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        header.Refresh(session.ActiveRootCanvas);
        check(prefix + "returning to Main changes navigation without author edits or undo", returned == 1 && session.ActiveRootCanvas == RootCanvasKind.Main &&
            header.Visibility == Visibility.Collapsed && JsonSerializer.Serialize(session.Document) == authored && session.UndoCount == undo &&
            ReferenceEquals(button.Style, Application.Current.FindResource("CanvasToolbarInlineButton")));
    }

    private static void InlineArtwork(Action<string, bool> check, string prefix, string? output, string locale, string theme)
    {
        var file = File(); file.Refresh(LongPath(locale), true, true);
        var header = new RootCanvasToolbarHeader(file, () => { }, () => true) { Margin = new(0) };
        foreach (var mode in new[] { "GroupCanvas.Temporary", "GroupCanvas.RootProject" })
        {
            header.SetScope(new("Main › Group", L.Get(mode), "", [("Main", () => { })], "Group"), true);
            Layout(header,330,ToolbarUpperRow.ExtensionHeight);
            var icon = Field<Grid>(file,"projectIcon"); var dot = Field<System.Windows.Shapes.Ellipse>(file,"modifiedDot");
            Rect dotBounds = dot.TransformToAncestor(icon).TransformBounds(new Rect(dot.RenderSize));
            check(prefix + mode + " unsaved dot stays fully inside the compact icon", dot.Visibility == Visibility.Visible && new Rect(icon.RenderSize).Contains(dotBounds));
            var image = new RenderTargetBitmap(330,33,96,96,PixelFormats.Pbgra32); image.Render(header);
            var yellow = ((SolidColorBrush)dot.Fill).Color;
            var referenceVisual = new DrawingVisual();
            using(var drawing = referenceVisual.RenderOpen()) drawing.DrawEllipse(dot.Fill,null,new(4,4),2,2);
            var reference = new RenderTargetBitmap(8,8,96,96,PixelFormats.Pbgra32);reference.Render(referenceVisual);
            check(prefix + mode + " compact dot retains at least the expanded dot's visible area", SystemParameters.HighContrast || CountInk(image) >= CountInk(reference));
            int CountInk(BitmapSource bitmap)
            {
                var pixels = new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);int ink=0;
                for(int i=0;i<pixels.Length;i+=4)
                    if(pixels[i+3] == 255 && Math.Abs(pixels[i]-yellow.B)<12 && Math.Abs(pixels[i+1]-yellow.G)<12 && Math.Abs(pixels[i+2]-yellow.R)<12) ink++;
                return ink;
            }
            var badge = Field<Border>(header,"modeBadge"); var modeIcon = (FrameworkElement)Field<ContentControl>(header,"modeIcon").Content;
            Rect artwork = modeIcon.TransformToAncestor(badge).TransformBounds(new Rect(modeIcon.RenderSize));
            check(prefix + mode + " mode glyph has vertical room inside its badge", artwork.Top >= 2 && artwork.Bottom <= badge.ActualHeight-2 && artwork.Height >= 12);
            var area = Field<StackPanel>(header,"scopeArea");
            var arrow = area.Children.OfType<System.Windows.Shapes.Path>().Single();
            var current = Field<Button>(header,"scopeButton");
            check(prefix + mode + " breadcrumb vector aligns with the button and text row", Math.Abs(arrow.TranslatePoint(new(arrow.ActualWidth/2,arrow.ActualHeight/2),header).Y-
                current.TranslatePoint(new(current.ActualWidth/2,current.ActualHeight/2),header).Y) < .5);
            if(output is not null)
            foreach(double scale in new[] {1d,1.5,2d})
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(330*scale),(int)Math.Ceiling(33*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(header);
                var encoder = new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = System.IO.File.Create(Path.Combine(output,$"inline-{locale}-{theme}-{mode}-{scale}.png"));encoder.Save(stream);
            }
        }
    }

    private static BoundFileIndicator File() => new(() => { }, () => { }, () => { }, () => true, () => true);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        { var child=VisualTreeHelper.GetChild(root,i); yield return child; foreach(var nested in Descendants(child)) yield return nested; }
    }
    private static string LongPath(string locale) => Path.Combine(Path.GetTempPath(),
        string.Concat(Enumerable.Repeat(locale == "zh-CN" ? "一份用于验证文件名气泡和当前画布提示不重叠的长工程名称" : "A very long project name to verify filename truncation beside the root action ", 5)) + ".floatcanvas");
    private static Grid ToolbarSurface(bool expanded)
    {
        var launcher = new ToolbarLauncher();
        return launcher.CreateSurface(new Border { Width = 800, Height = 56, Visibility = expanded ? Visibility.Visible : Visibility.Collapsed });
    }
    private static void Layout(FrameworkElement element, double width = double.PositiveInfinity, double height = double.PositiveInfinity)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, double.IsFinite(width) ? width : element.DesiredSize.Width, double.IsFinite(height) ? height : element.DesiredSize.Height));
        element.UpdateLayout();
    }
    private static void Save(FrameworkElement element, double width, double height, string path)
    {
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = System.IO.File.Create(path); encoder.Save(stream);
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
