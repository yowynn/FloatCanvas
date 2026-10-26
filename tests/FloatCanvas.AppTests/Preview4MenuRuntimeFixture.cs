using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class Preview4MenuRuntimeFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host, Window toolbar)
    {
        var main = Field<OverlayContextMenu>(session, "_toolbarMenu");
        var entries = Preview4MenuTests.Items(main).ToArray();
        check("actual launcher menu gives every command and group an icon", entries.Length > 0 && entries.All(item => item.Icon is FrameworkElement));
        var toolbarPaths = Descendants(Field<Button>(session, "_passThroughButton")).OfType<System.Windows.Shapes.Path>().ToArray();
        var pass = entries.Single(item => Equals(item.Header, L.Get("ToolbarV2.PassThrough")));
        check("actual pass-through menu and toolbar share the same artwork", toolbarPaths.Any(path => path.Data.ToString() == Preview4MenuTests.Data((FrameworkElement)pass.Icon)));
        var more = Field<ContextMenu>(session, "_moreToolsMenu");
        check("actual nested image tools have distinct non-placeholder symbols", more.Items.Count == 2 && Preview4MenuTests.Items(more).Select(item => Preview4MenuTests.Data((FrameworkElement)item.Icon)).Distinct().Count() == 2);
        check("new canvas has no dropdown or default-canvas menu",Field<NewCanvasButton>(session,"_newCanvasButton").ContextMenu is null&&!entries.Any(item=>Equals(item.Header,"新建默认画布")||Equals(item.Header,"New default canvas")));
        var tray = Field<System.Windows.Forms.NotifyIcon>(session, "_tray").ContextMenuStrip!;
        check("actual tray menu includes welcome and floating logo with all eight images", tray.Items.Count == 8 && tray.Items.Cast<System.Windows.Forms.ToolStripMenuItem>().All(item => item.Image is System.Drawing.Bitmap { Width: 36, Height: 36 }));
        main.IsOpen = true; Pump();
        var file = main.Items.OfType<MenuItem>().Single(item => Equals(item.Header, L.Get("ToolbarV2.File"))); file.IsSubmenuOpen = true; Pump();
        check("main menu and file submenu remain available in editing", main.IsOpen && file.IsSubmenuOpen && toolbar.IsVisible && host.IsEditing);
        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-menu-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Render((FrameworkElement)((HwndSource)PresentationSource.FromVisual(main)).RootVisual, Path.Combine(root, "main.png"));
        var popup = file.Template.FindName("PART_Popup", file);
        Render((FrameworkElement)(popup is Popup ordinary ? ordinary.Child : ((OverlayPopup)popup).Child)!, Path.Combine(root, "file.png"));
        Console.WriteLine("MENU_NATIVE_RENDER " + root);
        file.IsSubmenuOpen = false; main.IsOpen = false; Pump();
        check("closing native menus leaves canvas editing and toolbar intact", !main.IsOpen && toolbar.IsVisible && host.IsEditing);
        var help = main.Items.OfType<MenuItem>().Single(item => Equals(item.Header, L.Get("Audit.Help")));
        foreach (string label in new[] { L.Get("Audit.UserGuide"), L.Get("Settings.Shortcuts") })
        {
            EnsureEditing();
            main.IsOpen = true; help.IsSubmenuOpen = true; Pump();
            bool shownModal = false, closedAtOpen = false, closedAfterMove = false;
            toolbar.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.Cast<Window>().Single(window => window.Title == label);
                try
                {
                    shownModal = ComponentDispatcher.IsThreadModal && dialog.IsVisible;
                    closedAtOpen = !main.IsOpen && !help.IsSubmenuOpen;
                    Console.WriteLine($"MODAL_MENU title={label} modal={shownModal} main={main.IsOpen} base={((ContextMenu)main).IsOpen} submenu={help.IsSubmenuOpen}");
                    dialog.Left += 24; dialog.Top += 16; Pump();
                    closedAfterMove = !main.IsOpen && !help.IsSubmenuOpen && Field<object?>(main, "window") is null && host.IsEditing && dialog.IsVisible;
                    Console.WriteLine($"MODAL_MENU_MOVE title={label} closed={closedAfterMove} editing={host.IsEditing} dialog={dialog.IsVisible}");
                }
                finally { SessionWindowCoordinator.Close(dialog); }
            }));
            help.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            check(label + " opens modally after closing the whole launcher menu", shownModal && closedAtOpen);
            check(label + " cannot reveal a leftover launcher menu when moved", closedAfterMove);
        }
        EnsureEditing();
        void EnsureEditing()
        {
            if (host.IsEditing) return;
            typeof(NativeInputRoutingFixture).GetMethod("ActivateFixtureWindow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [new WindowInteropHelper(toolbar).Handle, new Action(() => typeof(DesktopSession).GetMethod("SetEditing", Private)!.Invoke(session, [true, false]))]);
            Pump();
        }
        var launcher = Field<ToolbarLauncher>(session, "_toolbarLauncher");
        var launcherPosition = launcher.PointToScreen(new Point(20, 20));
        main.IsOpen = true; Pump(); launcher.DismissMenuPress(new Point(20, 20));
        var gestures = Field<LauncherGestureState>(launcher, "gestures"); long now = Environment.TickCount64;
        check("closing the actual menu retains the launcher press", !main.IsOpen && gestures.IsPointerDown);
        Execute(gestures.PointerUp(launcherPosition.X, launcherPosition.Y, now));
        Execute(gestures.AdvanceTime(now + 10000));
        check("one actual menu-dismissal click leaves the menu closed", !main.IsOpen && !gestures.HasPendingClick);
        main.IsOpen = true; Pump(); launcher.DismissMenuPress(new Point(20, 20));
        gestures = Field<LauncherGestureState>(launcher, "gestures"); now = Environment.TickCount64;
        Execute(gestures.PointerUp(launcherPosition.X, launcherPosition.Y, now));
        gestures.PointerDown(launcherPosition.X, launcherPosition.Y, now + 30);
        bool canvasVisible = host.IsVisible;
        Execute(gestures.PointerUp(launcherPosition.X, launcherPosition.Y, now + 40)); Pump();
        check("double-click beginning on an open actual menu toggles the canvas eyes", !main.IsOpen && host.IsEditing && host.IsVisible != canvasVisible);
        typeof(DesktopSession).GetMethod("ToggleCanvasVisibility", Private)!.Invoke(session, null); Pump();
        main.IsOpen = true; Pump(); launcher.DismissMenuPress(new Point(20, 20));
        gestures = Field<LauncherGestureState>(launcher, "gestures"); now = Environment.TickCount64;
        bool dragRequested = false; launcher.DragRequested += () => dragRequested = true;
        Execute(gestures.PointerMove(launcherPosition.X + 100, launcherPosition.Y, now + 10));
        check("drag beginning on an open actual menu reaches the toolbar drag action", dragRequested && !main.IsOpen && host.IsEditing);
        launcher.CancelPendingGestures();
        void Execute(LauncherGestureAction action) => typeof(ToolbarLauncher).GetMethod("Execute", Private)!.Invoke(launcher, [action]);
        var welcomeAction=entries.Single(item=>Equals(item.Header,L.Get("Welcome.Open")));
        welcomeAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();
        var welcome=Field<Window>(session,"_welcomeWindow");
        check("launcher opens an ordinary welcome window after exiting editing",welcome.IsVisible&&Field<bool>(session,"_welcomeManual")&&!host.IsEditing&&!welcome.Topmost&&welcome.Owner is null);
        welcome.Close();Pump();
        tray.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Single(item=>item.Text==L.Get("Welcome.Open")).PerformClick();Pump();
        welcome=Field<Window>(session,"_welcomeWindow");
        check("tray reopens the manual welcome window",welcome.IsVisible&&Field<bool>(session,"_welcomeManual"));welcome.Close();Pump();
        var settings=Field<UserSettings>(session,"_settings");bool previous=settings.ShowPassThroughLauncher;
        entries.Single(item=>Equals(item.Header,L.Get(previous ? "Menu.HideFloatingLogo" : "Menu.ShowFloatingLogo"))).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check("launcher floating logo action persists the switched preference",settings.ShowPassThroughLauncher!=previous&&UserSettings.Load().ShowPassThroughLauncher==settings.ShowPassThroughLauncher);
        tray.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Single(item=>item.Text==L.Get("Settings.PassThroughLauncher")).PerformClick();
        check("tray floating logo action persists the same preference",settings.ShowPassThroughLauncher==previous&&UserSettings.Load().ShowPassThroughLauncher==previous);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(value).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void Render(FrameworkElement element, string path)
    {
        element.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen()) draw.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
}
