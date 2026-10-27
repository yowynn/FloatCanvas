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
using L = FloatCanvas.Localization.AppText;

/// <summary>Opt-in production settings renders hosted in hidden native sources, without showing a user window.</summary>
internal static class CanvasGridLockUiEvidence
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
                window.SetReferenceMonitors([], null);
                var content = (FrameworkElement)window.Content; window.Content = null;
                var root = new Border { Width = 600, Height = 590, Child = content, Background = (Brush)Application.Current.Resources["CanvasSurface"] };
                root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, Application.Current.Resources["CanvasText"]);
                root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
                root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
                nint handle;
                try
                {
                    using var source = new HwndSource(new HwndSourceParameters("FloatCanvas grid lock settings fixture")
                    { Width = 600, Height = 590, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080 });
                    handle = source.Handle; source.RootVisual = root;
                    var settings = new CanvasSettings { Projection = new() { Enabled = true, Columns = 8, Rows = 6, CellSize = 105.1234, LockedGridCellSize = 100 } };
                    window.Refresh(settings, true); window.PrepareForShow(); Layout(root);
                    check(language + " " + theme + " settings host remains native and hidden", IsWindow(handle) && !IsWindowVisible(handle) && !window.IsVisible && PresentationSource.FromVisual(content) is HwndSource);
                    var toggle = Field<CheckBox>(window, "gridSizeLock"); var count = Field<TextBox>(window, "lockedColumns");
                    check(language + " " + theme + " grid lock label and fractional count fit default settings width", toggle.ActualWidth > 200 && count.ActualWidth > 80 &&
                        toggle.DesiredSize.Width <= toggle.ActualWidth + 1 && count.ActualHeight >= 30 && count.IsReadOnly);
                    Render(root, Path.Combine(output, "settings-locked-" + language + "-" + theme + ".png"));
                    settings.Projection.LockedGridCellSize = null; window.Refresh(settings, true); window.PrepareForShow(); Layout(root);
                    Render(root, Path.Combine(output, "settings-unlocked-" + language + "-" + theme + ".png"));
                    settings.Projection.Enabled = false; settings.Grid.FlatCellSize = 73.1;
                    window.Refresh(settings, false); window.PrepareForShow(); Layout(root);
                    var flatPanel = Field<StackPanel>(window, "flatSizePanel");
                    var size = Field<NumericValueField>(window, "gridSize");
                    var sizeBounds = size.Input.TransformToAncestor(root).TransformBounds(new Rect(size.Input.RenderSize));
                    check(language + " " + theme + " flat settings expose a precise size editor within the default window", flatPanel.Visibility == Visibility.Visible &&
                        size.TryGetValue(out double flatSize) && flatSize == 73.1 && size.Input.ActualWidth > 200 && size.Input.ActualHeight >= 30 &&
                        sizeBounds.Left >= 0 && sizeBounds.Right <= 600 && sizeBounds.Top >= 0 && sizeBounds.Bottom <= 590);
                    check(language + " " + theme + " flat settings omit calibration counts and the adjustment lock", Field<Grid>(window, "countPair").Visibility == Visibility.Collapsed &&
                        toggle.Visibility == Visibility.Collapsed && Field<TextBox>(window, "lockedColumns").Visibility == Visibility.Collapsed);
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
    private static void Layout(FrameworkElement root) { root.Measure(new Size(600, 590)); root.Arrange(new Rect(0, 0, 600, 590)); root.UpdateLayout(); }
    private static void Render(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap(600, 590, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
}
