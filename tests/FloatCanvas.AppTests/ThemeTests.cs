using FloatCanvas.App;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using Peers = System.Windows.Automation.Peers;

internal static class ThemeTests
{
    public static void Run(Action<string, bool> assert)
    {
        var app = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        foreach (string mode in new[] { "dark", "light" })
        {
            AppTheme.Apply(mode);
            var background = ((Media.SolidColorBrush)app.Resources["CanvasInput"]).Color;
            var foreground = ((Media.SolidColorBrush)app.Resources["CanvasText"]).Color;
            var hover = ((Media.SolidColorBrush)app.Resources["CanvasHover"]).Color;
            var list = new Controls.ListBox { Width = 260, Height = 100 };
            var item = new Controls.ListBoxItem { Content = "LAYER read", FontSize = 20, FontWeight = Wpf.FontWeights.SemiBold, Height = 40 };
            list.Items.Add(item);
            var normal = Render(list);
            assert(mode + " ListBox paints actual themed background", Same(normal.Color(230, 75), background));
            assert(mode + " ListBoxItem text has actual readable pixels", normal.CountNear(foreground) > 15 && Contrast(foreground, background) >= 7);
            item.IsSelected = true;
            var selected = Render(list);
            assert(mode + " selected layer uses themed background and readable text", Same(selected.Color(230, 25), hover) && selected.CountNear(foreground) > 15 && Contrast(foreground, hover) >= 7);

            var menu = new Controls.ContextMenu { Width = 260, Height = 100 };
            var command = new Controls.MenuItem { Header = "MENU read", FontSize = 20, FontWeight = Wpf.FontWeights.SemiBold, Height = 40 };
            menu.Items.Add(command);
            var menuPixels = Render(menu);
            assert(mode + " ContextMenu paints actual themed background", Same(menuPixels.Color(230, 75), background));
            assert(mode + " MenuItem renders readable header and retains native control types", menuPixels.CountNear(foreground) > 15 && Contrast(foreground, background) >= 7 && command is Controls.MenuItem && list.ItemContainerGenerator.ContainerFromIndex(0) is Controls.ListBoxItem);
            var layerPeer = Peers.FrameworkElementAutomationPeer.CreatePeerForElement(list).GetChildren().Single(peer => peer.GetAutomationControlType() == Peers.AutomationControlType.ListItem);
            var menuPeer = Peers.FrameworkElementAutomationPeer.CreatePeerForElement(command);
            assert(mode + " themed controls retain native accessible names and selection/invoke patterns", layerPeer.GetAutomationControlType() == Peers.AutomationControlType.ListItem &&
                layerPeer.GetPattern(Peers.PatternInterface.SelectionItem) is not null && menuPeer.GetAutomationControlType() == Peers.AutomationControlType.MenuItem &&
                menuPeer.GetName() == "MENU read" && menuPeer.GetPattern(Peers.PatternInterface.Invoke) is not null);
        }
        assert("theme raster checks leave no visible or native windows", app.Windows.Cast<Wpf.Window>().All(window => !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0));
        app.Shutdown();
    }

    private static Pixels Render(Wpf.FrameworkElement element)
    {
        element.Measure(new Wpf.Size(element.Width, element.Height));
        element.Arrange(new Wpf.Rect(0, 0, element.Width, element.Height)); element.UpdateLayout();
        var bitmap = new Imaging.RenderTargetBitmap((int)element.Width, (int)element.Height, 96, 96, Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        byte[] bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return new(bytes, bitmap.PixelWidth);
    }
    private static bool Same(Media.Color a, Media.Color b) => Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2 && a.A == b.A;
    private static double Contrast(Media.Color a, Media.Color b)
    {
        static double Channel(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Luminance(Media.Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
    private sealed record Pixels(byte[] Data, int Width)
    {
        public Media.Color Color(int x, int y) { int p = (y * Width + x) * 4; return Media.Color.FromArgb(Data[p + 3], Data[p + 2], Data[p + 1], Data[p]); }
        public int CountNear(Media.Color expected)
        {
            int count = 0;
            for (int p = 0; p < Data.Length; p += 4)
                if (Data[p + 3] > 250 && Math.Abs(Data[p + 2] - expected.R) < 25 && Math.Abs(Data[p + 1] - expected.G) < 25 && Math.Abs(Data[p] - expected.B) < 25) count++;
            return count;
        }
    }
}
