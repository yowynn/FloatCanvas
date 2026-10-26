using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

/// <summary>Opt-in production worker GPU routing and actual settings in an isolated bounded canvas.
/// No desktop capture, injected input, or user profile. Native GPU fault fallback is covered by Phase2RenderTests.</summary>
internal static class GpuRoutingFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host,
        EditorController editor, Window toolbar, string run, string scenario, Action<string> snapshot)
    {
        if (scenario != "gpu-native") throw new ArgumentException(scenario);
        var settings = Field<UserSettings>(session, "_settings");
        var preview = Field<CanvasPreviewPump>(session, "_canvasPreview");
        var monitor = host.Diagnostics.Single();
        long epoch = host.EditingSessionVersion;
        check("GPU routing keeps its bounded canvas and keyboard lease", monitor.Bounds.Width == 640 && host.IsEditing && host.IsKeyboardIsolationActive);
        check("startup retains the default enabled GPU preference", settings.PreferGpuAcceleration && UserSettings.Load().PreferGpuAcceleration);
        Set(host, "_toggleHotkey", (HotkeyBinding?)settings.Toggle);
        Set(host, "_hideHotkey", (HotkeyBinding?)settings.Hide);
        using var bitmap = new SKBitmap(160, 80); bitmap.Erase(SKColors.Red);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        string asset = editor.Session.Assets.Add(encoded.ToArray());
        var image = new CanvasElement { Kind = ElementKind.Image, AssetId = asset, Width = 240, Height = 120,
            Transform = Affine2D.Translation(monitor.Bounds.Left + 80, monitor.Bounds.Top + 90),
            Style = new() { StrokeWidth = 0 }, Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(0, 0, 1, 1), Amount = 8 }] };
        editor.Cancel();
        Edit(() => editor.Session.Execute("GPU fixture filter", doc => { doc.Elements.Clear(); doc.Elements.Add(image); }), OverlayRenderBackend.Gpu);
        using (var pixels = PreviewTestPixels.Read(preview.Frame!.Pixels))
            check("worker completes a hardware GPU filter with correct pixels", !string.IsNullOrWhiteSpace(preview.Frame.GpuDetails) &&
                pixels.GetPixel(200, 150).Red > 220 && pixels.GetPixel(200, 150).Alpha > 240);
        check("UI presentation remains a CPU copy of completed worker pixels", host.LastCompletedRenderBackend == OverlayRenderBackend.Cpu);
        snapshot("gpu-worker-image-filter");
        Edit(() => editor.Session.Execute("remove effects", doc => doc.Elements.Single().Effects.Clear()), OverlayRenderBackend.Cpu);
        Edit(() => editor.Session.Undo(), OverlayRenderBackend.Gpu);
        Edit(() => editor.Session.Execute("transparent layer", doc => doc.LayerOpacity = 0), OverlayRenderBackend.Cpu);
        Edit(() => editor.Session.Undo(), OverlayRenderBackend.Gpu);
        Edit(() => editor.Session.Execute("offscreen filter", doc => doc.Elements.Single().Transform =
            Affine2D.Translation(doc.Viewport.Right + 300, doc.Viewport.Bottom + 300)), OverlayRenderBackend.Cpu);
        Edit(() => editor.Session.Undo(), OverlayRenderBackend.Gpu);

        var first = OpenSettings();
        check("actual settings show the default GPU preference", GpuCheckBox(first).IsChecked == true);
        GpuCheckBox(first).IsChecked = false;
        long before = preview.CompletedFrames; SaveSettings(first);
        Wait(() => preview.CompletedFrames > before && preview.Frame?.Backend == OverlayRenderBackend.Cpu);
        check("CPU preference applies to the worker immediately and persists", !settings.PreferGpuAcceleration && !UserSettings.Load().PreferGpuAcceleration);
        var second = OpenSettings();
        check("reopened settings retain the CPU preference", GpuCheckBox(second).IsChecked == false);
        GpuCheckBox(second).IsChecked = true; before = preview.CompletedFrames; SaveSettings(second);
        Wait(() => preview.CompletedFrames > before && preview.Frame?.Backend == OverlayRenderBackend.Gpu);
        check("GPU preference resumes worker GPU rendering without restart", settings.PreferGpuAcceleration && UserSettings.Load().PreferGpuAcceleration);
        check("worker rendering and settings retain the same active editing session", host.IsEditing && host.IsVisible &&
            host.IsKeyboardIsolationActive && host.EditingSessionVersion == epoch && toolbar.IsVisible);
        File.WriteAllText(Path.Combine(run, "gpu-routing-metrics.json"), System.Text.Json.JsonSerializer.Serialize(new
        { WorkerBackend = preview.Frame!.Backend.ToString(), preview.Frame.GpuDetails, preview.Frame.GpuFailure,
            UiBackend = host.LastCompletedRenderBackend.ToString(), preview.CompletedFrames, preview.GpuFrames, preview.CpuFrames, Epoch = epoch }));

        void Edit(Action action, OverlayRenderBackend expected)
        {
            long count = preview.CompletedFrames; action(); Invoke(session, "RequestRender");
            Wait(() => preview.CompletedFrames > count && preview.Frame?.Backend == expected && !preview.Busy);
            check("actual edit routes worker to " + expected, preview.Frame!.Backend == expected);
        }
        Window OpenSettings()
        {
            Invoke(session, "ShowHotkeySettings");
            Wait(() => Application.Current.Windows.Cast<Window>().Any(window => window is AppSettingsWindow && window.IsVisible));
            return Application.Current.Windows.Cast<Window>().Single(window => window is AppSettingsWindow && window.IsVisible);
        }
        void SaveSettings(Window window)
        {
            ((AppSettingsWindow)window).SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => !window.IsVisible);
        }
    }
    private static CheckBox GpuCheckBox(Window window)
    {
        var pages = Field<Grid>(window, "pages");
        var section = pages.Children.Cast<ScrollViewer>().Select(page => (Panel)page.Content).Single(panel =>
            Descendants(panel).OfType<CheckBox>().Any(box => AutomationProperties.GetName(box) == L.Get("Settings.PreferGpuAcceleration")));
        ((AppSettingsWindow)window).SelectSection(section); window.UpdateLayout();
        return Descendants(section).OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == L.Get("Settings.PreferGpuAcceleration"));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void Wait(Func<bool> condition) => Phase2RenderTests.Until(condition, timeout: 15000);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Invoke(object target, string name) => target.GetType().GetMethod(name, Private)!.Invoke(target, null);
}
