using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class StampLibraryThemeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Run --isolated-ui-desktop --stamp-library-theme-only.");
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        output ??= Path.Combine(ApplicationPaths.ProfileDirectory, "stamp-library-theme");
        Directory.CreateDirectory(output);
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var results = new List<(string Name, bool Passed)>();
        var reports = new List<object>();
        AppTheme.Apply("dark");
        var library = Task.Run(async () =>
        {
            var saved = new StampLibrary(Path.Combine(output, "profile")); await saved.LoadAsync();
            if (saved.Items.Count == 0)
            {
                var session = new EditorSession();
                session.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 64,
                    Style = new() { StrokeWidth = 0, Fill = 0xFF367DC4 } });
                for (int index = 0; index < 31; index++) await saved.SaveSelectionAsync(session, "Theme specimen " + (index + 1).ToString("00"));
            }
            await saved.PinAsync(9, null);
            return saved;
        }).GetAwaiter().GetResult();
        var window = new StampLibraryWindow(library, () => new EditorSession(), _ => { })
        { ShowActivated = false, ShowInTaskbar = false, Topmost = false, Left = 30, Top = 30 };
        var list = Field<ListBox>(window, "grid");
        var gate = Field<SemaphoreSlim>(window, "thumbnailGate");
        gate.Wait(); gate.Wait(); bool held = true;
        try
        {
            window.Show(); window.PrepareForShow(); Pump();
            foreach (double scale in new[] { 1d, 1.5 })
            {
                Layout(scale);
                var loading = Realized().SelectMany(Visuals).OfType<TextBlock>().FirstOrDefault(text => text.Text == L.Get("Recovery.PreviewLoading") && text.IsVisible);
                Observe("dark loading " + scale + " retains readable real loading text", loading is not null && Same(loading.Foreground, Brush("CanvasText")));
                Snapshot("dark-loading-" + (int)(96 * scale), "dark", scale);
            }
            held = false; gate.Release(2);
            PumpUntil(() => Realized().Any() && Realized().All(item => Visuals(item).OfType<Image>().Any(image => image.Source is not null)));
            foreach (string theme in new[] { "dark", "light", "dark" })
            {
                AppTheme.Apply(theme); Pump();
                foreach (double scale in new[] { 1.25, 1.75, 2 })
                {
                    Layout(scale);
                    CheckSlots(theme + "-" + (int)(96 * scale), scale);
                }
                foreach (double scale in new[] { 1d, 1.5 })
                {
                    Layout(scale);
                    list.SelectedIndex = 0; Pump();
                    string name = theme + "-" + (int)(96 * scale);
                    Snapshot(name, theme, scale);
                    var selected = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)!;
                    var chrome = selected.Template.FindName("Chrome", selected) as Border;
                    Observe(name + " keeps the production selection border blue and its text readable", chrome is not null && Same(chrome.BorderBrush, Brush("CanvasAccent")) && Same(selected.Foreground, Brush("CanvasText")));
                    CheckSlots(name, scale);
                    var scroll = Visuals(list).OfType<ScrollViewer>().First();
                    Observe(name + " retains a usable themed scrollbar for the real catalog", scroll.ScrollableHeight > 0 && Visuals(scroll).OfType<ScrollBar>().Any(bar => bar.Orientation == Orientation.Vertical && bar.IsVisible));
                    var search = Field<TextBox>(window, "search");
                    search.Text = "no such theme specimen"; PumpUntil(() => list.Items.Count == 0);
                    var empty = Field<TextBlock>(window, "emptyText");
                    Observe(name + " shows a readable real search-empty message", empty.IsVisible && !string.IsNullOrWhiteSpace(empty.Text) && Same(empty.Foreground, Brush("CanvasText")));
                    Snapshot(name + "-empty", theme, scale);
                    search.Text = ""; PumpUntil(() => list.Items.Count == library.Items.Count && Realized().Any());
                }
            }
        }
        finally
        {
            if (held) gate.Release(2);
            window.Close(); Pump(); SynchronizationContext.SetSynchronizationContext(previousContext);
            File.WriteAllText(Path.Combine(output, "appearance.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(results.Select(result => new { name = result.Name, passed = result.Passed })));
        }
        foreach (var (name, passed) in results) check(name, passed);

        void Observe(string name, bool passed) => results.Add((name, passed));
        Brush Brush(string key) => (Brush)window.FindResource(key);
        IEnumerable<ListBoxItem> Realized() => list.Items.Cast<object>().Select(item => list.ItemContainerGenerator.ContainerFromItem(item)).OfType<ListBoxItem>();
        void Layout(double scale)
        {
            VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
            window.Width = StampLibraryWindow.WidthForColumns(StampLibraryWindow.DefaultTileColumns, scale);
            window.InvalidateMeasure(); window.InvalidateArrange(); window.UpdateLayout(); Pump();
        }
        void CheckSlots(string name, double scale)
        {
            var shelf = Field<UniformGrid>(window, "slotShelf");
            var slots = Field<Dictionary<int, Button>>(window, "slotButtons");
            var hover = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var press = (DependencyPropertyKey)typeof(ButtonBase).GetField("IsPressedPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            foreach (bool selectThird in new[] { false, true })
            {
                if (selectThird) slots[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale),
                    96 * scale, 96 * scale, PixelFormats.Pbgra32);
                image.Render(window);
                bool correct = slots.Count == 10 && library.Pins[2] is not null && library.Pins[9] is null;
                foreach (FrameworkElement host in shelf.Children)
                {
                    var button = slots[(int)host.Tag];
                    var frame = (Border)button.Template.FindName("Chrome", button);
                    var origin = button.TranslatePoint(new Point(), window);
                    int x = (int)Math.Floor((origin.X + button.ActualWidth / 2) * scale);
                    int y = (int)Math.Round(origin.Y * scale);
                    int thickness = (int)Math.Round(2 * scale);
                    var row = new byte[4 * (thickness + 1)];
                    image.CopyPixels(new Int32Rect(x, y, 1, thickness + 1), row, 4, 0);
                    var edge = ((SolidColorBrush)Brush(selectThird && (int)host.Tag == 2 ? "CanvasAccent" : "CanvasBorder")).Color;
                    var fill = ((SolidColorBrush)frame.Background).Color;
                    bool ColorAt(int i, Color c) => row[i * 4] == c.B && row[i * 4 + 1] == c.G && row[i * 4 + 2] == c.R;
                    correct &= ColorAt(0, edge) && ColorAt(thickness - 1, edge) && ColorAt(thickness, fill)
                        && frame.RenderSize == button.RenderSize;
                }
                Observe(name + $" all ten slots paint one 2-DIP outline (third selected={selectThird})", correct);
            }
            var third = slots[2];
            var selectedFrame = (Border)third.Template.FindName("Chrome", third);
            third.SetValue(hover, true);
            bool hovered = Same(selectedFrame.Background, Brush("CanvasButtonHover")) && Same(selectedFrame.BorderBrush, Brush("CanvasAccent"));
            third.SetValue(press, true);
            Observe(name + " hover and press change the slot fill and retain the selected outline",
                hovered && Same(selectedFrame.Background, Brush("CanvasPressed")) && Same(selectedFrame.BorderBrush, Brush("CanvasAccent")));
            third.SetValue(press, false); third.SetValue(hover, false);
            slots[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        void Snapshot(string name, string theme, double scale)
        {
            var pixels = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            pixels.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixels));
            using (var stream = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(stream);
            Point point = list.TranslatePoint(new(list.ActualWidth / 2, list.ActualHeight - 3), window);
            byte[] sample = new byte[4];
            pixels.CopyPixels(new Int32Rect((int)Math.Clamp(point.X * scale, 0, pixels.PixelWidth - 1), (int)Math.Clamp(point.Y * scale, 0, pixels.PixelHeight - 1), 1, 1), sample, 4, 0);
            var color = Color.FromArgb(sample[3], sample[2], sample[1], sample[0]);
            var expected = ((SolidColorBrush)Brush("CanvasInput")).Color;
            reports.Add(new { name, theme, scale, background = (list.Background as SolidColorBrush)?.Color.ToString(), foreground = (list.Foreground as SolidColorBrush)?.Color.ToString(), pixel = color.ToString(), expected = expected.ToString(), template = list.Template?.TargetType.Name, items = list.Items.Count });
            Observe(name + " paints the actual gallery background with the current theme", Same(list.Background, Brush("CanvasInput")) && color == expected);
            Observe(name + " uses readable gallery foreground and the production scroll template", Same(list.Foreground, Brush("CanvasText")) && list.Template?.FindName("PART_ScrollViewer", list) is ScrollViewer);
        }
    }

    private static bool Same(Brush? first, Brush? second) => first is SolidColorBrush a && second is SolidColorBrush b && a.Color == b.Color;
    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        yield return parent;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> complete)
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(1); }
        if (!complete()) throw new TimeoutException("The isolated stamp theme fixture did not finish loading.");
        Pump();
    }
    private static string DesktopName()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder name, int length, out int needed);
}
