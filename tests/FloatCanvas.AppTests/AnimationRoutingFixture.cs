using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Opt-in production animation routing. Samples only the fixture's own transparent
/// overlay surface after DesktopSession.Draw, never pixels from the user's desktop.</summary>
internal static class AnimationRoutingFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    // The same independently encoded red/blue loop used by the image integration checks.
    private const string LoopGif = "R0lGODlhAwABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQFCgAAACwAAAAAAwABAAADBBgYGAkAIfkEBQoAAAAsAAAAAAMAAQAAAwQ4ODgJADs=";
    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host, EditorController editor,
        Window toolbar, string run, string scenario, Action<string> snapshot)
    {
        SaveToolbar(toolbar, Path.Combine(run, "production-toolbar.png"));
        if (scenario == "animation-liveblur")
        {
            // Compatibility alias: current WGC correctly excludes every window in
            // this process. Reuse the separate-PID pixel fixture; this entry now
            // explicitly requires session-local exclusion, not the legacy fallback.
            SessionCaptureRoutingFixture.Run(check, session, host, editor, toolbar, run, "session-capture-native", snapshot);
            return;
        }
        string gifPath = PrepareGifFixture(run);
        var native = host.Diagnostics.Single();
        check("animation fixture retains the real bounded native host and keyboard lease", native.Bounds.Width == 640 && native.Bounds.Height == 360 && host.IsEditing && host.IsKeyboardIsolationActive);
        editor.Cancel(); editor.Session.Execute("isolated animation fixture setup", document => document.Elements.Clear());
        Await((Task)Invoke(session, "ImportImagesAsync", new string[] { gifPath }, false)!);
        Pump(100);
        var image = editor.Session.Document.Elements.Single(element => element.Kind == ElementKind.Image);
        var original = File.ReadAllBytes(gifPath);
        check("production image import preserves the complete encoded animated GIF", image.AssetId is not null && editor.Session.Assets.TryGet(image.AssetId, out var asset) && asset.Data.Span.SequenceEqual(original));
        Guid imageId = image.Id;
        editor.Session.Execute("position animated fixture inside its own native overlay", document =>
        {
            var item = document.Elements.Single(element => element.Id == imageId);
            item.Width = 180; item.Height = 60; item.Transform = Affine2D.Translation(native.Bounds.Left + 70, native.Bounds.Top + 70);
        });
        editor.Session.SetSelection([imageId]);
        Pump(100);

        var buttons = Descendants(toolbar).OfType<Button>().Where(button => button.Tag is string).ToArray();
        Button ActionButton(string tag) => buttons.Single(button => Equals(button.Tag, tag));
        var crop = ActionButton("crop"); var capture = ActionButton("capture"); var import = ActionButton("import");
        check("the actual toolbar places crop immediately after import and capture", ReferenceEquals(crop.Parent, capture.Parent) && ReferenceEquals(capture.Parent, import.Parent) &&
            Grid.GetColumn(capture) == Grid.GetColumn(import) + 1 && Grid.GetColumn(crop) == Grid.GetColumn(capture) + 1);
        check("the actual crop button enables a single editable imported animated image", crop.IsEnabled && editor.CanCropSelection);
        SaveToolbar(toolbar, Path.Combine(run, "production-toolbar-image-selected.png"));
        editor.Session.SetSelection([]); Pump(40);
        check("the actual crop button is disabled without an image selection", !crop.IsEnabled && !editor.CanCropSelection);
        var other = image.Clone(true); other.Transform = Affine2D.Translation(native.Bounds.Left + 350, native.Bounds.Top + 70);
        var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 30, Transform = Affine2D.Translation(native.Bounds.Left + 540, native.Bounds.Top + 70) };
        editor.Session.Execute("isolated crop selection candidates", document => { document.Elements.Add(other); document.Elements.Add(rectangle); });
        editor.Session.SetSelection([imageId, other.Id]); Pump(40);
        check("the actual crop button is disabled for multiple images", !crop.IsEnabled && !editor.CanCropSelection);
        editor.Session.SetSelection([imageId, rectangle.Id]); Pump(40);
        check("the actual crop button is disabled for mixed selections", !crop.IsEnabled && !editor.CanCropSelection);
        editor.Session.SetSelection([imageId]); editor.Session.SetLocked(true); Pump(40);
        check("the actual crop button is disabled for a locked image", !crop.IsEnabled && !editor.CanCropSelection);
        editor.Session.Execute("restore isolated animation display", document =>
        {
            document.Elements.RemoveAll(element => element.Id != imageId);
            document.Elements.Single().Locked = false;
        });
        editor.Session.SetSelection([imageId]); Pump(100);

        var frames = new List<FrameSample>();
        var renderer = Field<SceneRenderer>(session, "_renderer");
        var clock = Field<Stopwatch>(session, "_animationClock");
        Action<SKSurface> sample = surface =>
        {
            using var rendered = surface.Snapshot(); using var bitmap = SKBitmap.FromImage(rendered);
            var color = bitmap.GetPixel(130, 100);
            frames.Add(new(Stopwatch.GetTimestamp(), renderer.AnimationTime.TotalMilliseconds, clock.Elapsed.TotalMilliseconds,
                color.Red, color.Green, color.Blue, color.Alpha, editor.Session.SceneRevision, editor.Session.UndoCount, host.IsVisible, host.IsEditing));
        };
        host.Draw += sample;
        object? layers = null; FieldInfo? canMutateField = null; Func<bool>? originalCanMutate = null;
        int panelChecks = 0;
        try
        {
            Invoke(session, "ToggleLayers"); Pump(120);
            layers = Field<object>(session, "_layers");
            canMutateField = layers.GetType().GetField("canMutate", Private)!;
            originalCanMutate = (Func<bool>)canMutateField.GetValue(layers)!;
            // Preserve the actual production gate; count its use by Refresh rather than
            // introducing a product counter or inferring refresh from unchanged text.
            var originalGate = originalCanMutate;
            canMutateField.SetValue(layers, (Func<bool>)(() => { panelChecks++; return originalGate(); }));
            layers.GetType().GetMethod("Refresh")!.Invoke(layers, null);
            check("the real panel refresh probe observes a deliberate production Refresh", panelChecks > 0);
            Pump(80); panelChecks = 0; frames.Clear();
            long revision = editor.Session.SceneRevision; int undo = editor.Session.UndoCount, redo = editor.Session.RedoCount;
            int changed = 0; EventHandler changedObserver = (_, _) => changed++;
            editor.Session.Changed += changedObserver;
            try { Pump(650); }
            finally { editor.Session.Changed -= changedObserver; }
            check("production timer rendering displays both encoded red and blue GIF frames", HasRed(frames) && HasBlue(frames) && frames.Count >= 4);
            check("animation ticks preserve revision, undo, redo and content notifications", editor.Session.SceneRevision == revision && editor.Session.UndoCount == undo && editor.Session.RedoCount == redo && changed == 0);
            check("animation-only renders do not refresh the actual open layer panel", panelChecks == 0);
            check("visible production animation has an active clock and scheduled timer", clock.IsRunning && Timer(session)?.IsEnabled == true);
            snapshot("animation-visible-playback");

            double beforeUndo = clock.Elapsed.TotalMilliseconds;
            editor.Session.Execute("isolated unrelated edit for animation clock", document => document.Elements.Add(rectangle.Clone(true)));
            Pump(50); editor.Session.Undo(); Pump(60);
            check("undoing an unrelated edit does not rewind the running animation clock", clock.Elapsed.TotalMilliseconds >= beforeUndo);

            // Use the same production action as the toolbar eye button, not a test-only timer stop.
            ActionButton("visibility").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
            SaveToolbar(toolbar, Path.Combine(run, "production-toolbar-canvas-hidden.png"));
            double hiddenTime = clock.Elapsed.TotalMilliseconds; int hiddenFrames = frames.Count;
            Pump(350);
            check("hiding the canvas stops both production animation clock and timer", !host.IsVisible && !clock.IsRunning && Timer(session)?.IsEnabled != true && Math.Abs(clock.Elapsed.TotalMilliseconds - hiddenTime) < 2);
            check("hidden canvas submits no further animation surfaces", frames.Count == hiddenFrames);
            ActionButton("visibility").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100); frames.Clear();
            SaveToolbar(toolbar, Path.Combine(run, "production-toolbar-canvas-visible.png"));
            double resumeTime = clock.Elapsed.TotalMilliseconds; Pump(450);
            check("showing the canvas resumes production frames and the paused clock", host.IsVisible && clock.IsRunning && clock.Elapsed.TotalMilliseconds > resumeTime + 250 && HasRed(frames) && HasBlue(frames));
            snapshot("animation-shown-again");

            ActionButton("done").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100); frames.Clear();
            revision = editor.Session.SceneRevision; undo = editor.Session.UndoCount; double passThroughTime = clock.Elapsed.TotalMilliseconds;
            Pump(450);
            check("pass-through releases editing input while the visible GIF continues playing", !host.IsEditing && !host.IsKeyboardIsolationActive && host.IsVisible && clock.Elapsed.TotalMilliseconds > passThroughTime + 250 && HasRed(frames) && HasBlue(frames));
            check("pass-through playback still creates no document revision or undo entry", editor.Session.SceneRevision == revision && editor.Session.UndoCount == undo);
            snapshot("animation-pass-through-playback");
            File.WriteAllText(Path.Combine(run, "animation-routing-metrics.json"), JsonSerializer.Serialize(new
            {
                Surface = "Own bounded transparent production overlay; not a desktop screenshot", Gif = gifPath,
                NativeBounds = native.Bounds, PanelRefreshChecksDuringAnimation = panelChecks, Frames = frames
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            host.Draw -= sample;
            if (layers is not null && canMutateField is not null && originalCanMutate is not null) canMutateField.SetValue(layers, originalCanMutate);
        }
    }

    internal static string PrepareGifFixture(string run)
    {
        string? configured = Environment.GetEnvironmentVariable("FLOATCANVAS_ANIMATION_GIF");
        if (configured is not null)
        {
            if (!File.Exists(configured)) throw new FileNotFoundException("The isolated red/blue GIF fixture is missing.", configured);
            return configured;
        }
        Directory.CreateDirectory(run);
        string path = Path.Combine(run, "two-color-loop.gif");
        File.WriteAllBytes(path, Convert.FromBase64String(LoopGif));
        return path;
    }

    private static void SaveToolbar(Window toolbar, string path)
    {
        toolbar.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(toolbar);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(toolbar.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(toolbar.ActualHeight * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(toolbar); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    private sealed record FrameSample(long Timestamp, double RendererTimeMs, double ClockTimeMs, byte Red, byte Green, byte Blue, byte Alpha, long Revision, int Undo, bool Visible, bool Editing);
    private static bool HasRed(IEnumerable<FrameSample> samples) => samples.Any(sample => sample.Alpha > 240 && sample.Red > 220 && sample.Blue < 30);
    private static bool HasBlue(IEnumerable<FrameSample> samples) => samples.Any(sample => sample.Alpha > 240 && sample.Blue > 220 && sample.Red < 30);
    private static DispatcherTimer? Timer(DesktopSession session) => (DispatcherTimer?)session.GetType().GetField("_animationTimer", Private)!.GetValue(session);
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static object? Invoke(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Await(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Pump(15);
        if (!task.IsCompleted) throw new TimeoutException("Production animated image import did not finish.");
        task.GetAwaiter().GetResult();
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false;
        try { timer.Start(); Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
}
