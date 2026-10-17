using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

/// <summary>Exercises the production effect route with simulated OS capabilities and synthetic capture pixels.</summary>
internal static class LegacyWindowsEffectsTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        string originalProfile = ApplicationPaths.ProfileDirectory;
        var originalScope = ApplicationPaths.RequestedMonitorScope;
        string originalLanguage = L.Preference;
        var originalContext = SynchronizationContext.Current;
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-windows-effects", Guid.NewGuid().ToString("N"));
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var originalWindows = app.Windows.Cast<Window>().ToArray();
        Directory.CreateDirectory(profile);
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        new UserSettings { Theme = "light", Language = "zh-CN" }.Save();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        DesktopSession? session = null;
        OverlayHost? host = null;
        try
        {
            foreach (int build in new[] { 14393, 17763, 19041, 19044 })
            {
                NewSession(build);
                var editor = Field<EditorController>(session!, "_editor");
                var document = editor.Session.Document;
                var viewport = document.Viewport;
                var requested = new RectD(viewport.X + 12, viewport.Y + 8, 40, 28);
                int captures = 0;
                Func<Task<CapturedDesktop>> capture = () =>
                {
                    captures++;
                    if (build >= 19041) throw new InvalidOperationException("Live pixelation must not freeze the desktop.");
                    return Task.FromResult(SyntheticDesktop(host!.PrimaryMonitor.Bounds));
                };
                Task pending = (Task)Invoke("AddDesktopEffectCoreAsync", requested, ImageEffectKind.Pixelate, 7d, 0xFF112233u, capture)!;
                if (build < 19041) AwaitOnDispatcher(pending); else pending.GetAwaiter().GetResult();
                var element = editor.Session.Document.Elements.Single();
                check($"Windows {build} routes pixelation using its capture capability", element.Kind ==
                    (build < 19041 ? ElementKind.Image : ElementKind.LivePixelate));
                check($"Windows {build} performs exactly the required synthetic capture count", captures == (build < 19041 ? 1 : 0));
                check($"Windows {build} preserves region position and dimensions", element.Width == 40 && element.Height == 28 &&
                    element.Transform == Affine2D.Translation(requested.X, requested.Y));
                check($"Windows {build} retains an editable full-region pixelation effect", element.Effects is [{ Kind: ImageEffectKind.Pixelate, Amount: 7 }] &&
                    element.Effects[0].Bounds == new RectD(0, 0, 1, 1));
                check($"Windows {build} commits pixelation as one undo operation", editor.Session.UndoCount == 1 && !editor.Session.IsTransactionActive);
                check($"Windows {build} releases the document busy guard", !Field<bool>(session!, "_documentBusyValue"));
                if (build < 19041)
                {
                    check($"Windows {build} stores its frozen pixels as a self-contained image resource", element.AssetId is not null &&
                        editor.Session.Assets.TryGet(element.AssetId, out _) && editor.Session.Document.ReferencedAssets.All(id => editor.Session.Assets.TryGet(id, out _)));
                    using var source = SKBitmap.Decode(editor.Session.Assets.Get(element.AssetId!).Data.ToArray());
                    check($"Windows {build} freezes only the selected region at native dimensions", source.Width == 40 && source.Height == 28 &&
                        source.GetPixel(0, 0).Alpha == 255 && source.GetPixel(0, 0) != source.GetPixel(2, 0));
                    check($"Windows {build} reports the static fallback instead of claiming live capture", Field<TextBlock>(session!, "_status").Text == L.Get("Effect.LegacyPixelateFrozen"));
                    check($"Windows {build} starts no live worker or capture exclusion", Field<object?>(session!, "_liveDesktopCapture") is null &&
                        !Field<bool>(session!, "_liveCaptureExcluded") && Field<SceneRenderer>(session!, "_renderer").LiveBackdrop is null);
                }
                else
                    check($"Windows {build} live regions require no frozen image resource", element.AssetId is null && editor.Session.Assets.Count == 0);
                check($"Windows {build} undo removes the newly created region", editor.Session.Undo() && editor.Session.Document.Elements.Count == 0);
                check($"Windows {build} redo restores the same editable region", editor.Session.Redo() &&
                    editor.Session.Document.Elements.Single().Id == element.Id && editor.Session.Document.Elements.Single().Kind == element.Kind);
                check($"Windows {build} fixture never shows an overlay or captures user input", !host!.IsEditing &&
                    host.Diagnostics.All(pair => !pair.DisplayVisible && !pair.InputVisible) && app.Windows.Cast<Window>().Except(originalWindows).All(window => !window.IsVisible));
                CloseSession();
            }

            foreach (int build in new[] { 14393, 17763 })
            {
                NewSession(build);
                var editor = Field<EditorController>(session!, "_editor");
                var viewport = editor.Session.Document.Viewport;
                editor.Session.Document.Elements.Add(new CanvasElement { Kind = ElementKind.LivePixelate, Width = 40, Height = 28,
                    Transform = Affine2D.Translation(viewport.X + 12, viewport.Y + 8), Style = new() { StrokeWidth = 0 },
                    Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 7, Bounds = new(0, 0, 1, 1) }] });
                string before = JsonSerializer.Serialize(editor.Session.Document);
                var status = Field<TextBlock>(session!, "_status");
                status.Text = "Fixture unchanged status";
                editor.Session.Document.LayerOpacity = 0;
                Invoke("SyncLiveEffectsCapture");
                check($"Windows {build} zero-opacity live regions need neither capture nor a compatibility hint", status.Text == "Fixture unchanged status" &&
                    Field<object?>(session!, "_liveDesktopCapture") is null && !Field<bool>(session!, "_liveCaptureExcluded"));
                editor.Session.Document.LayerOpacity = 1;
                typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, false);
                Invoke("SyncLiveEffectsCapture");
                check($"Windows {build} logically hidden regions do not replace the current status", status.Text == "Fixture unchanged status" &&
                    Field<object?>(session!, "_liveDesktopCapture") is null && !Field<bool>(session!, "_liveCaptureExcluded"));
                typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
                foreach (string language in new[] { "zh-CN", "en" })
                {
                    L.Initialize(language);
                    Invoke("SyncLiveEffectsCapture");
                    string hint = Field<TextBlock>(session!, "_status").Text;
                    check($"Windows {build} loaded live region has a localized {language} compatibility hint", hint == L.Get("Effect.LegacyLiveRegion") &&
                        hint != "Effect.LegacyLiveRegion" && hint.Contains("2004", StringComparison.Ordinal) &&
                        (language == "en" ? hint.Contains("placeholder", StringComparison.Ordinal) : hint.Contains("占位", StringComparison.Ordinal)));
                    check($"Windows {build} loaded live region never starts a worker or requests exclusion ({language})",
                        Field<object?>(session!, "_liveDesktopCapture") is null && !Field<bool>(session!, "_liveCaptureExcluded") &&
                        Field<SceneRenderer>(session!, "_renderer").LiveBackdrop is null && Field<object?>(session!, "_desktopBackdrop") is null);
                }
                check($"Windows {build} compatibility handling leaves the loaded document and history intact", JsonSerializer.Serialize(editor.Session.Document) == before &&
                    editor.Session.UndoCount == 0 && editor.Session.Assets.Count == 0);
                using var rendered = SKBitmap.Decode(Field<SceneRenderer>(session!, "_renderer").ExportPng(editor.Session.Document, editor.Session.Assets, true));
                check($"Windows {build} missing live capture renders an opaque placeholder cover", rendered.GetPixel(30, 20) == new SKColor(0xFF50545B) &&
                    rendered.GetPixel(0, 0).Alpha == 0);
                check($"Windows {build} loaded-region checks keep all fixture HWNDs hidden", host!.Diagnostics.All(pair => !pair.DisplayVisible && !pair.InputVisible) && !host.IsEditing);
                CloseSession();
            }

            foreach (string language in new[] { "zh-CN", "en" })
            {
                L.Initialize(language);
                string message = L.Get("Effect.LegacyPixelateFrozen");
                check($"static compatibility feedback is translated for {language}", message != "Effect.LegacyPixelateFrozen" &&
                    (language == "en" ? message.Contains("frozen", StringComparison.Ordinal) : message.Contains("静态", StringComparison.Ordinal)));
            }
        }
        finally
        {
            CloseSession();
            foreach (var window in app.Windows.Cast<Window>().Except(originalWindows).ToArray()) window.Close();
            var restore = new List<string> { "--profile", originalProfile };
            if (originalScope is { } scope) restore.Add(scope == OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens");
            ApplicationPaths.Configure(restore.ToArray()); L.Initialize(originalLanguage);
            SynchronizationContext.SetSynchronizationContext(originalContext);
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        }

        void NewSession(int build)
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
            var bounds = new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 30, 128, 96);
            var bounded = primary with { Bounds = bounds, WorkArea = bounds };
            Func<IReadOnlyList<MonitorInfo>> provider = () => [bounded];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
                [OverlayMonitorScope.PrimaryScreen, provider, false, WindowsCapabilities.FromVersion(new Version(10, 0, build))], null)!;
            session = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", Private)!;
            (isolation.GetValue(host) as IDisposable)?.Dispose(); isolation.SetValue(host, null);
            // Only logical visibility is enabled for the command's production gate.
            // Native display/input HWNDs stay hidden throughout the fixture.
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
        }
        void CloseSession()
        {
            if (session is not null) session.Dispose(); else host?.Dispose();
            session = null; host = null;
        }
        object? Invoke(string name, params object[] args) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, args);
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;

    private static CapturedDesktop SyntheticDesktop(PixelBounds bounds)
    {
        using var bitmap = new SKBitmap(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++) bitmap.SetPixel(x, y, (x + y) % 4 < 2 ? SKColors.Red : SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(bounds, data.ToArray());
    }

    private static void AwaitOnDispatcher(Task task)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!task.IsCompleted) throw new TimeoutException("The synthetic legacy effect command did not complete.");
        task.GetAwaiter().GetResult();
    }
}
