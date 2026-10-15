using System.Diagnostics;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Wpf = System.Windows;

namespace FloatCanvas.SessionTests;

internal static partial class Program
{
    // Independent ordinary WPF application for actual pointer acceptance. No session,
    // hooks, Raw Input registration, injected input or production method calls.
    private static int ShowPointerSurface()
    {
        var application = new Wpf.Application();
        var clock = Stopwatch.StartNew();
        var canvas = new Canvas { Background = Brushes.White };
        var window = new Wpf.Window
        {
            Title = "浮绘鼠标验收 · 普通应用", Content = canvas,
            Topmost = true, WindowState = Wpf.WindowState.Maximized,
            WindowStyle = Wpf.WindowStyle.None, ResizeMode = Wpf.ResizeMode.NoResize
        };
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == Wpf.WindowState.Normal)
                window.WindowState = Wpf.WindowState.Maximized;
        };
        var status = new TextBlock { FontSize = 20, Foreground = Brushes.Black };
        int sequence = 0, clicks = 0, doubleClicks = 0, drags = 0, wheelEvents = 0, downs = 0;
        double dragLeft = 350, dragTop = 500;
        var scroll = new ScrollViewer { Width = 280, Height = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
        void State(string kind)
        {
            status.Text = $"点击 {clicks} · 双击 {doubleClicks} · 拖动 {drags} ({dragLeft:F0},{dragTop:F0}) · 滚动 {scroll.VerticalOffset:F0} · 滚轮 {wheelEvents}";
            Console.WriteLine("POINTER_STATE " + JsonSerializer.Serialize(new
            {
                Sequence = ++sequence, Timestamp = DateTimeOffset.Now, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                ProcessId = Environment.ProcessId, Kind = kind, Clicks = clicks, DoubleClicks = doubleClicks,
                DragGestures = drags, DragLeft = dragLeft, DragTop = dragTop,
                ScrollOffset = scroll.VerticalOffset, WheelEvents = wheelEvents, MouseDowns = downs
            }));
        }
        void Place(Wpf.UIElement child, double x, double y)
        {
            Canvas.SetLeft(child, x); Canvas.SetTop(child, y); canvas.Children.Add(child);
        }
        Place(new TextBlock { Text = "普通窗口鼠标验收 · 穿透 / 编辑 / 穿透", FontSize = 30 }, 48, 150);
        Place(new TextBlock { Text = "同一位置：点击按钮、双击按钮、拖动色块、滚动列表。只记录本窗口实际响应。", FontSize = 18 }, 48, 210);
        Place(status, 48, 290);
        var click = new Button { Content = "点击计数", Width = 180, Height = 60 };
        click.Click += (_, _) => { clicks++; State("Click"); };
        Place(click, 80, 500);
        var doubleClick = new Button { Content = "双击计数", Width = 180, Height = 60 };
        doubleClick.PreviewMouseDoubleClick += (_, _) => { doubleClicks++; State("DoubleClick"); };
        Place(doubleClick, 80, 620);
        var tile = new Border { Background = Brushes.LightBlue, Width = 170, Height = 90,
            Child = new TextBlock { Text = "拖动此色块", FontSize = 20, Margin = new Wpf.Thickness(16) } };
        bool dragging = false;
        Wpf.Point start = default;
        double startLeft = 0, startTop = 0;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(canvas); startLeft = dragLeft; startTop = dragTop;
            dragging = tile.CaptureMouse(); e.Handled = true;
        };
        tile.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var point = e.GetPosition(canvas);
            dragLeft = startLeft + point.X - start.X; dragTop = startTop + point.Y - start.Y;
            Canvas.SetLeft(tile, dragLeft); Canvas.SetTop(tile, dragTop);
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            dragging = false; tile.ReleaseMouseCapture(); drags++; State("DragCompleted"); e.Handled = true;
        };
        tile.LostMouseCapture += (_, _) => dragging = false;
        Place(tile, dragLeft, dragTop);
        var rows = new StackPanel();
        for (int i = 0; i < 80; i++) rows.Children.Add(new TextBlock { Text = $"滚动项目 {i + 1:00}", FontSize = 20, Height = 40 });
        scroll.Content = rows;
        scroll.PreviewMouseWheel += (_, _) => { wheelEvents++; State("Wheel"); };
        scroll.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) State("ScrollChanged"); };
        Place(scroll, 850, 450);
        window.PreviewMouseDown += (_, _) => { downs++; State("MouseDown"); };
        var close = new Button { Content = "关闭鼠标验收窗口", Padding = new Wpf.Thickness(12, 6, 12, 6) };
        close.Click += (_, _) => window.Close();
        Place(close, 48, 20);
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
        deadline.Tick += (_, _) => window.Close();
        window.ContentRendered += (_, _) => State("Ready");
        window.Closed += (_, _) => { deadline.Stop(); State("Closed"); };
        deadline.Start(); application.Run(window); return 0;
    }
}
