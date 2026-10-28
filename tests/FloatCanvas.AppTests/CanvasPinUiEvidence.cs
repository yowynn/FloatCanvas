using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Opt-in production settings renders hosted in hidden native sources, without showing a user window.</summary>
internal static class CanvasPinUiEvidence
{
    internal static void Run(string output, Action<string, bool> check)
    {
        Directory.CreateDirectory(output);
        string preference = L.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(language); AppTheme.Apply(theme);
                var window = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
                window.SetReferenceMonitors([new MonitorInfo("primary", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96, 96, true),
                    new MonitorInfo("secondary", new(1920, 0, 1280, 720), new(1920, 0, 1280, 680), 96, 96, false)], null);
                var content = (FrameworkElement)window.Content; window.Content = null;
                var root = new Border { Width = window.Width, Height = window.Height, Child = content, Background = (Brush)Application.Current.Resources["CanvasSurface"] };
                root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, Application.Current.Resources["CanvasText"]);
                root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
                root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
                nint handle;
                try
                {
                    using var source = new HwndSource(new HwndSourceParameters("FloatCanvas pin settings fixture")
                    { Width = (int)Math.Ceiling(root.Width), Height = (int)Math.Ceiling(root.Height), WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080 });
                    handle = source.Handle; source.RootVisual = root;
                    var settings = new CanvasSettings { Projection = new() { Enabled = true, Columns = 9, Rows = 9, CellSize = 100 } };
                    window.Refresh(settings); window.PrepareForShow(); Layout(root);
                    check(language + " " + theme + " settings host remains native and hidden", IsWindow(handle) && !IsWindowVisible(handle) && !window.IsVisible && PresentationSource.FromVisual(content) is HwndSource);
                    var selector = Field<CanvasPinSelector>(window, "pinSelector");
                    var points = Field<System.Windows.Controls.Primitives.ToggleButton[]>(selector, "points");
                    check(language + " " + theme + " spatial pin selector fits", selector.ActualHeight >= 96 && points.Length == 9 && points.All(p => p.ActualWidth >= 28));
                    check(language + " " + theme + " columns precede rows horizontally", Field<NumericValueField>(window, "columns").TransformToAncestor(root).Transform(new Point()).X <
                        Field<NumericValueField>(window, "rows").TransformToAncestor(root).Transform(new Point()).X);
                    Render(root, Path.Combine(output, "settings-pin-" + language + "-" + theme + ".png"));
                    settings.Projection.PinLocation = CanvasPinLocation.Center; window.Refresh(settings); window.PrepareForShow(); Layout(root);
                    Render(root, Path.Combine(output, "settings-center-" + language + "-" + theme + ".png"));
                    settings.Projection.LockedGridCellSize = 73.1; window.Refresh(settings); window.PrepareForShow(); Layout(root);
                    var automatic = Field<TextBox>(window, "lockedColumns");
                    check(language + " " + theme + " locked coverage and pin remain visible together", automatic.IsReadOnly &&
                        automatic.ActualHeight >= PropertyPanelLayout.ControlHeight && selector.ActualHeight >= 96 &&
                        Field<FrameworkElement>(window, "gridSizeLockRow").Visibility == Visibility.Visible);
                    check(language + " " + theme + " automatic columns also precede rows", automatic.TransformToAncestor(root).Transform(new Point()).X <
                        Field<TextBox>(window, "lockedRows").TransformToAncestor(root).Transform(new Point()).X);
                    Render(root, Path.Combine(output, "settings-locked-" + language + "-" + theme + ".png"));
                    settings.Projection.Enabled = false; settings.Grid.FlatCellSize = 73.1;
                    window.Refresh(settings); window.PrepareForShow(); Layout(root);
                    var flatPanel = Field<StackPanel>(window, "flatSizePanel");
                    var size = Field<NumericValueField>(window, "gridSize");
                    var sizeBounds = size.Input.TransformToAncestor(root).TransformBounds(new Rect(size.Input.RenderSize));
                    check(language + " " + theme + " flat settings expose a precise size editor within the default window", flatPanel.Visibility == Visibility.Visible &&
                        size.TryGetValue(out double flatSize) && flatSize == 73.1 && size.Input.ActualWidth > 200 &&
                        size.ActualHeight >= PropertyPanelLayout.ControlHeight && size.Input.ActualHeight >= size.Input.FontSize &&
                        sizeBounds.Left >= 0 && sizeBounds.Right <= root.ActualWidth && sizeBounds.Top >= 0 && sizeBounds.Bottom <= root.ActualHeight);
                    check(language + " " + theme + " flat settings omit calibration counts and the adjustment lock", Field<Grid>(window, "countPair").Visibility == Visibility.Collapsed &&
                        Field<FrameworkElement>(window, "pinRow").Visibility == Visibility.Collapsed);
                    Render(root, Path.Combine(output, "settings-flat-" + language + "-" + theme + ".png"));
                    source.RootVisual = null; root.Child = null;
                }
                finally { root.Child = null; window.Content = content; window.CloseForDispose(); }
                check(language + " " + theme + " settings host releases its native handle", !IsWindow(handle));
            }
        }
        finally { L.Initialize(preference); AppTheme.Apply("light"); }
        Console.WriteLine("GRID_LOCK_UI_NATIVE_RENDER " + output);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Layout(FrameworkElement root) { root.Measure(new Size(root.Width, root.Height)); root.Arrange(new Rect(0, 0, root.Width, root.Height)); root.UpdateLayout(); }
    private static void Render(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
}
