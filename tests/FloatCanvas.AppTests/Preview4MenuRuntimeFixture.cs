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
        var main = Field<ContextMenu>(session, "_toolbarMenu");
        var entries = Preview4MenuTests.Items(main).ToArray();
        check("actual launcher menu gives all 53 commands and groups an icon", entries.Length == 53 && entries.All(item => item.Icon is FrameworkElement));
        var toolbarPaths = Descendants(Field<Button>(session, "_passThroughButton")).OfType<System.Windows.Shapes.Path>().ToArray();
        var pass = entries.Single(item => Equals(item.Header, L.Get("ToolbarV2.PassThrough")));
        check("actual pass-through menu and toolbar share the same artwork", toolbarPaths.Any(path => path.Data.ToString() == Preview4MenuTests.Data((FrameworkElement)pass.Icon)));
        var more = Field<ContextMenu>(session, "_moreToolsMenu");
        check("actual nested image tools have distinct non-placeholder symbols", more.Items.Count == 2 && Preview4MenuTests.Items(more).Select(item => Preview4MenuTests.Data((FrameworkElement)item.Icon)).Distinct().Count() == 2);
        check("new canvas has no dropdown or default-canvas menu",Field<NewCanvasButton>(session,"_newCanvasButton").ContextMenu is null&&!entries.Any(item=>Equals(item.Header,"新建默认画布")||Equals(item.Header,"New default canvas")));
        var tray = Field<System.Windows.Forms.NotifyIcon>(session, "_tray").ContextMenuStrip!;
        check("actual tray menu includes welcome and floating logo with all eight images", tray.Items.Count == 8 && tray.Items.Cast<System.Windows.Forms.ToolStripMenuItem>().All(item => item.Image is System.Drawing.Bitmap { Width: 36, Height: 36 }));
        main.IsOpen = true; Pump();
        var file = main.Items.OfType<MenuItem>().First(); file.IsSubmenuOpen = true; Pump();
        check("main menu and file submenu remain available in editing", main.IsOpen && file.IsSubmenuOpen && toolbar.IsVisible && host.IsEditing);
        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-menu-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Render((FrameworkElement)((HwndSource)PresentationSource.FromVisual(main)).RootVisual, Path.Combine(root, "main.png"));
        var popup = (Popup)file.Template.FindName("PART_Popup", file);
        Render((FrameworkElement)popup.Child, Path.Combine(root, "file.png"));
        Console.WriteLine("MENU_NATIVE_RENDER " + root);
        file.IsSubmenuOpen = false; main.IsOpen = false; Pump();
        check("closing native menus leaves canvas editing and toolbar intact", !main.IsOpen && toolbar.IsVisible && host.IsEditing);
        var welcomeAction=entries.Single(item=>Equals(item.Header,L.Get("Welcome.Open")));
        welcomeAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();
        var welcome=Field<Window>(session,"_welcomeWindow");
        check("launcher opens a manual welcome window",welcome.IsVisible&&Field<bool>(session,"_welcomeManual"));
        welcome.Close();Pump();
        tray.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Single(item=>item.Text==L.Get("Welcome.Open")).PerformClick();Pump();
        welcome=Field<Window>(session,"_welcomeWindow");
        check("tray reopens the manual welcome window",welcome.IsVisible&&Field<bool>(session,"_welcomeManual"));welcome.Close();Pump();
        var settings=Field<UserSettings>(session,"_settings");bool previous=settings.ShowPassThroughLauncher;
        entries.Single(item=>Equals(item.Header,L.Get("Settings.PassThroughLauncher"))).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
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
