using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Real scope changes retain document geometry while moving its reference screen.</summary>
internal static class MonitorCanvasOriginTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string previousProfile = ApplicationPaths.ProfileDirectory;
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-MonitorOrigin", Guid.NewGuid().ToString("N"));
        var foreground = GetForegroundWindow();
        var primary = Monitor("primary", new(-14000, -14000, 300, 180), true, 96);
        var secondary = Monitor("secondary", new(-13600, -14100, 460, 260), false, 144);
        var primaryCenter = Center(primary); var secondaryCenter = Center(secondary);
        DesktopSession? desktop = null;
        OverlayHost? host = null;
        try
        {
            ApplicationPaths.Configure(["--profile", profile]);
            new UserSettings { ShowFloatingLogo = false, Theme = "light" }.Save();
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
                [OverlayMonitorScope.PrimaryScreen, (Func<IReadOnlyList<MonitorInfo>>)(() => [secondary, primary]), false], null)!;
            desktop = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
            var editor = Field<EditorController>(desktop, "_editor");
            host.SetMonitorScope(OverlayMonitorScope.SpecificScreen, primary.DeviceName);
            var live = editor.Session;
            var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 20,
                Transform = Affine2D.Translation(primaryCenter.X + 15, primaryCenter.Y - 10) };
            live.Add(rectangle); live.SetSelection([rectangle.Id]);
            live.TransformSelection(Affine2D.Translation(8, 6)); live.Undo();
            string elementsBefore = JsonSerializer.Serialize(live.Document.Elements);
            int undo = live.UndoCount, redo = live.RedoCount;
            long contentVersion = live.ContentVersion;

            host.SetMonitorScope(OverlayMonitorScope.SpecificScreen, secondary.DeviceName);
            check("only A to only B moves existing content by the reference-screen center delta",
                ScreenCenter(editor, live.Document.Elements.Single()).DistanceTo(secondaryCenter + new PointD(30, 0)) < 1e-7);
            check("scope remapping preserves object geometry, selection and both history stacks",
                JsonSerializer.Serialize(live.Document.Elements) == elementsBefore && live.Selection.SetEquals([rectangle.Id]) &&
                live.UndoCount == undo && live.RedoCount == redo);
            check("a monitor change invalidates presentation without becoming a canvas content edit", live.ContentVersion == contentVersion);
            check("the active screen maps to the current document viewport in physical pixels",
                Same(live.Document.Viewport, DocumentBounds(secondary, editor.ScreenOrigin)));
            check("the serialized first-screen reference remains stable after moving to another display",
                live.Document.DisplayLayout[0].Bounds.Center.DistanceTo(primaryCenter) < 1e-7);

            check("redo after a scope change still transforms the same visible object on B", live.Redo() &&
                ScreenCenter(editor, live.Document.Elements.Single()).DistanceTo(secondaryCenter + new PointD(38, 6)) < 1e-7);
            check("undo after a scope change returns the same object to its B-relative position", live.Undo() &&
                ScreenCenter(editor, live.Document.Elements.Single()).DistanceTo(secondaryCenter + new PointD(30, 0)) < 1e-7);

            string project = Path.Combine(profile, "reference-origin.floatcanvas");
            var store = new ProjectStore();
            store.SaveAsync(project, live.CreateCommittedSnapshot(), live.Assets).GetAwaiter().GetResult();
            var loaded = store.LoadAsync(project).GetAwaiter().GetResult();
            var loadedSession = new EditorSession(loaded.Document, loaded.Assets);
            typeof(DesktopSession).GetField("_live", Private)!.SetValue(desktop, loadedSession);
            editor.ReplaceSession(loadedSession, default);
            Invoke(desktop, "OnMonitorsChanged");
            check("loading the saved project on B restores its reference mapping without translating geometry",
                ScreenCenter(editor, loadedSession.Document.Elements.Single()).DistanceTo(secondaryCenter + new PointD(30, 0)) < 1e-7 &&
                JsonSerializer.Serialize(loadedSession.Document.Elements) == elementsBefore && loadedSession.UndoCount == 0);

            host.SetMonitorScope(OverlayMonitorScope.AllScreens);
            check("only B to all screens returns the canvas reference to the primary screen",
                ScreenCenter(editor, loadedSession.Document.Elements.Single()).DistanceTo(primaryCenter + new PointD(30, 0)) < 1e-7);
            var expectedUnion = RectD.Union(DocumentBounds(primary, editor.ScreenOrigin), DocumentBounds(secondary, editor.ScreenOrigin));
            check("all-screen viewport includes both current outputs in document coordinates", Same(loadedSession.Document.Viewport, expectedUnion));
            host.SetMonitorScope(OverlayMonitorScope.PrimaryScreen);
            check("primary-only and all-screen modes use the same reference center",
                ScreenCenter(editor, loadedSession.Document.Elements.Single()).DistanceTo(primaryCenter + new PointD(30, 0)) < 1e-7);

            // Frozen captures keep their independent pixel geometry and placement contract.
            var frozen = new EditorSession(new SceneDocument { Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, 60, 40) });
            var frozenOrigin = secondaryCenter - new PointD(30, 20);
            typeof(DesktopSession).GetField("_capture", Private)!.SetValue(desktop, frozen);
            typeof(DesktopSession).GetField("_captureOrigin", Private)!.SetValue(desktop, frozenOrigin);
            editor.ReplaceSession(frozen, frozenOrigin);
            Invoke(desktop, "OnMonitorsChanged");
            check("monitor changes retain a frozen capture's separate pixel-space viewport", frozen.Document.Viewport == new RectD(0, 0, 60, 40));
            host.SetMonitorScope(OverlayMonitorScope.SpecificScreen, secondary.DeviceName);
            Invoke(desktop, "ReturnToLive");
            check("returning from a frozen capture restores the live canvas's current screen reference",
                ReferenceEquals(editor.Session, loadedSession) &&
                ScreenCenter(editor, loadedSession.Document.Elements.Single()).DistanceTo(secondaryCenter + new PointD(30, 0)) < 1e-7);
            check("monitor-origin fixture keeps its own outputs offscreen without entering editing or taking foreground",
                !host.IsEditing && host.Monitors.All(monitor => monitor.Bounds.Right < -10000 && monitor.Bounds.Bottom < -10000) &&
                host.Diagnostics.All(pair => !pair.InputVisible) && GetForegroundWindow() == foreground);
        }
        finally
        {
            host?.SetVisible(false); desktop?.Dispose(); host?.Dispose();
            foreach (var window in app.Windows.Cast<Window>().ToArray())
            {
                window.Close();
                if (new WindowInteropHelper(window).Handle is var handle && handle != 0)
                    HwndSource.FromHwnd(handle)?.Dispose();
            }
            ApplicationPaths.Configure(["--profile", previousProfile]);
            if (Directory.Exists(profile)) Directory.Delete(profile, true);
        }
        Sampling(check);
    }

    internal static void Sampling(Action<string, bool> check)
    {
        var viewport = new RectD(-120, -80, 100, 60);
        var origin = new PointD(800, -500);
        using var pixels = new SKBitmap(100, 60);
        for (int y = 0; y < pixels.Height; y++)
            for (int x = 0; x < pixels.Width; x++) pixels.SetPixel(x, y, x < 50 ? SKColors.Blue : SKColors.Yellow);
        using var encodedImage = SKImage.FromBitmap(pixels);
        using var encoded = encodedImage.Encode(SKEncodedImageFormat.Png, 100);
        var desktop = new CapturedDesktop(new(680, -580, 100, 60), encoded.ToArray());
        var green = new CanvasElement { Kind = ElementKind.Rectangle, Width = 12, Height = 10,
            Transform = Affine2D.Translation(-100, -60), Style = new() { Fill = 0xFF00FF00, StrokeWidth = 0 } };
        var mask = new CanvasElement { Kind = ElementKind.LivePixelate, Width = 20, Height = 20,
            Transform = Affine2D.Translation(-70, -60), Style = new() { StrokeWidth = 0 },
            Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 4, Bounds = new(0, 0, 1, 1) }] };
        var scene = new SceneDocument { Viewport = viewport, Elements = [green, mask] };
        var selection = new CaptureSelection(CaptureShape.Rectangle, CaptureSource.Background, new(-110, -70, 70, 40), [], 0, 0);
        var background = SnapshotImageCommand.Render(scene, [], selection, desktop, [viewport], screenOrigin: origin);
        using (var raster = SKBitmap.Decode(background.PngBytes))
            check("a remapped snapshot samples physical desktop pixels at the document-relative capture position",
                raster.Width == 70 && raster.Height == 40 && raster.GetPixel(5, 5) == SKColors.Blue && raster.GetPixel(50, 20) == SKColors.Yellow);
        var combined = SnapshotImageCommand.Render(scene, [], selection with { Source = CaptureSource.BackgroundAndCanvas }, desktop, [viewport], screenOrigin: origin);
        using (var raster = SKBitmap.Decode(combined.PngBytes))
            check("a remapped snapshot aligns canvas objects and live filtering with the same desktop pixels",
                raster.GetPixel(15, 15) == SKColors.Lime && raster.GetPixel(50, 20) == SKColors.Yellow);
        var justCanvas = SnapshotImageCommand.Render(scene, [], selection with { Source = CaptureSource.CanvasOnly }, desktop, [viewport], screenOrigin: origin);
        using (var raster = SKBitmap.Decode(justCanvas.PngBytes))
            check("a remapped canvas-only snapshot samples its live effect without baking the desktop background",
                raster.GetPixel(5, 5).Alpha == 0 && raster.GetPixel(50, 20) == SKColors.Yellow);

        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-MonitorSampling", Guid.NewGuid().ToString("N"));
        try
        {
            var correctStamps = Task.Run(async () =>
            {
                var library = new StampLibrary(profile); await library.LoadAsync();
                async Task<bool> Save(SceneDocument document, string name)
                {
                    var session = new EditorSession(document); session.SetSelection([mask.Id]);
                    var entry = await library.SaveSelectionAsync(session, name, StampSaveMode.RasterMerge,
                        renderContext: new(document.Clone(), session.Assets, ScreenCapture: desktop, ScreenOrigin: origin));
                    var merged = await library.LoadStampAsync(entry.Id);
                    var element = merged.Document.Elements.Single();
                    using var mergedPixels = SKBitmap.Decode(merged.Assets.Get(element.AssetId!).Data.ToArray());
                    return mergedPixels.GetPixel(mergedPixels.Width / 2, mergedPixels.Height / 2) == SKColors.Yellow;
                }
                bool normal = await Save(scene, "Remapped desktop region");
                var projected = scene.Clone();
                projected.Canvas.Projection = CanvasCalibration.CreateInitial(viewport);
                projected.Canvas.Projection.Enabled = true;
                projected.Canvas.Projection.Matrix = Projective2D.FromAffine(Affine2D.Translation(10, 0));
                return (Normal: normal, Projected: await Save(projected, "Remapped projected region"));
            }).GetAwaiter().GetResult();
            check("a remapped live-effect stamp applies screen origin before its own selection normalization", correctStamps.Normal);
            check("a remapped projected live-effect stamp samples the same physical desktop after projecting its frame", correctStamps.Projected);
        }
        finally { if (Directory.Exists(profile)) Directory.Delete(profile, true); }
    }

    private static PointD ScreenCenter(EditorController editor, CanvasElement element) =>
        CanvasMapping.Create(editor.Session.Document).ElementToScreen(element).TransformPoint(ElementGeometry.LocalBounds(element).Center) + editor.ScreenOrigin;
    private static MonitorInfo Monitor(string name, PixelBounds bounds, bool primary, uint dpi) => new(name, bounds, bounds, dpi, dpi, primary);
    private static PointD Center(MonitorInfo monitor) => new(monitor.Bounds.Left + monitor.Bounds.Width / 2d, monitor.Bounds.Top + monitor.Bounds.Height / 2d);
    private static RectD DocumentBounds(MonitorInfo monitor, PointD origin) => new(monitor.Bounds.Left - origin.X, monitor.Bounds.Top - origin.Y, monitor.Bounds.Width, monitor.Bounds.Height);
    private static bool Same(RectD first, RectD second) => new PointD(first.X, first.Y).DistanceTo(new(second.X, second.Y)) < 1e-7 &&
        Math.Abs(first.Width - second.Width) < 1e-7 && Math.Abs(first.Height - second.Height) < 1e-7;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Invoke(object target, string name) => target.GetType().GetMethod(name, Private)!.Invoke(target, null);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
