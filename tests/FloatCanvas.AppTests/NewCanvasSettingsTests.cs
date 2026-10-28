using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class NewCanvasSettingsTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string originalProfile = ApplicationPaths.ProfileDirectory;
        var originalScope = ApplicationPaths.RequestedMonitorScope;
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-new-canvas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            ApplicationPaths.Configure(["--profile", folder, "--primary-screen"]);
            new UserSettings { Theme = "light", OverlayMode = "normal", ShowFloatingLogo = false }.Save();
            using var fixture = new Fixture(app);
            var settings = Field<UserSettings>(fixture.Session, "_settings");
            var reference = (RectD)fixture.Invoke("CanvasReferenceViewport")!;
            fixture.Live.Document.Canvas.Grid.FlatOrigin = new(-111, -222);
            fixture.Invoke("SynchronizeFlatGridOrigin");
            check("flat-grid screen reference is the current primary center rather than a saved movable point",
                fixture.Live.Document.Canvas.Grid.FlatOrigin == reference.Center);
            var changed = CanvasPresetLibrary.Apply(new CanvasPresetParameters
            { BackgroundColor = 0xFF123456, ProjectionEnabled = true, GridVisible = true, GridColor = 0xFF3155AA,
                GridWidth = 3.5, Columns = 12, Rows = 9, DepthPriorityOcclusion = true }, new(), reference, fixture.Live.Document.Viewport);
            changed.Projection.LockedGridCellSize = changed.Projection.CellSize;
            fixture.Live.ExecuteSettings("fixture custom canvas", document => document.Canvas = changed);
            var previous = fixture.Live.Document;
            string originalCanvas = Json(previous.Canvas);
            var expectedDefault = CanvasPresetLibrary.Apply(CanvasPresetLibrary.OfficialOnly().Official.First().Parameters, new(), reference, previous.Viewport);
            fixture.Run("StartNewCanvasAsync");
            check("new canvas defaults to the complete first official recipe instead of inheriting customized canvas values",
                Json(fixture.Live.Document.Canvas) == Json(expectedDefault));
            check("new canvas leaves the previous canvas settings intact for its archived workspace", Json(previous.Canvas) == originalCanvas);
            check("new canvas resets content while retaining viewport and display metadata", fixture.Live.Document.Elements.Count == 0 &&
                fixture.Live.Document.Viewport == previous.Viewport && Json(fixture.Live.Document.DisplayLayout) == Json(previous.DisplayLayout));
            var preference = typeof(UserSettings).GetProperty("PreserveCanvasSettingsOnNewCanvas")!;
            check("retaining canvas settings is off by default", preference.GetValue(new UserSettings()) is false);
            preference.SetValue(settings, true); settings.Save();
            check("the retention choice persists in user settings", preference.GetValue(UserSettings.Load()) is true);
            fixture.Live.ExecuteSettings("fixture retained canvas", document => document.Canvas = changed.Clone());
            var retained = fixture.Live.Document.Canvas;
            string retainedJson = Json(retained);
            fixture.Run("StartNewCanvasAsync");
            check("opting in retains every canvas setting including geometry and grid lock", Json(fixture.Live.Document.Canvas) == retainedJson &&
                !ReferenceEquals(fixture.Live.Document.Canvas, retained));
            fixture.Live.Document.Canvas.Grid.Color = 0xFFAA2244;
            check("a retained new canvas owns an independent settings clone", Json(retained) == retainedJson);
            preference.SetValue(settings, false);
            fixture.Live.Execute("fixture annotation", document => document.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 35, Height = 25 }));
            string beforeClear = Json(fixture.Live.Document.Canvas);
            fixture.Run("ClearCurrentCanvasAsync");
            check("clearing annotations keeps canvas settings even with new-canvas retention disabled",
                fixture.Live.Document.Elements.Count == 0 && Json(fixture.Live.Document.Canvas) == beforeClear);
            fixture.Run("StartNewCanvasAsync");
            check("turning retention off again restores the official recipe on the next new canvas", Json(fixture.Live.Document.Canvas) == Json(expectedDefault));
            var captureOrigin = new PointD(-350, 900);
            var capture = new EditorSession(new SceneDocument { Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, 150, 120) });
            typeof(DesktopSession).GetField("_capture", Private)!.SetValue(fixture.Session, capture);
            typeof(DesktopSession).GetField("_captureOrigin", Private)!.SetValue(fixture.Session, captureOrigin);
            Field<EditorController>(fixture.Session, "_editor").ReplaceSession(capture, captureOrigin);
            fixture.Run("StartNewCanvasAsync");
            check("creating a canvas from a capture uses the live reference rather than the capture screen origin",
                Json(fixture.Live.Document.Canvas) == Json(expectedDefault) &&
                Field<EditorController>(fixture.Session, "_editor").ScreenOrigin == Field<PointD>(fixture.Session, "_liveOrigin"));
            check("new canvas fixtures never enter editing or capture desktop input", !fixture.Host.IsEditing &&
                !fixture.Host.IsKeyboardIsolationActive && fixture.Host.CaptureHandle == 0);
            check("new canvas fixtures keep every interactive window hidden", app.Windows.Cast<Window>().All(window => !window.IsVisible));
        }
        finally
        {
            var restore = new List<string> { "--profile", originalProfile };
            if (originalScope is { } scope) restore.Add(scope == OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens");
            ApplicationPaths.Configure(restore.ToArray()); Directory.Delete(folder, true);
        }
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private sealed class Fixture : IDisposable
    {
        internal readonly OverlayHost Host;
        internal readonly DesktopSession Session;
        internal EditorSession Live => Field<EditorSession>(Session, "_live");
        internal Fixture(Application app)
        {
            var monitor = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
            var bounds = new PixelBounds(monitor.Bounds.Left - 10000, monitor.Bounds.Top - 10000, 640, 360);
            Func<IReadOnlyList<MonitorInfo>> provider = () => [monitor with { Bounds = bounds, WorkArea = bounds }];
            Host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            Session = new DesktopSession(app, Host); Field<System.Windows.Forms.NotifyIcon>(Session, "_tray").Visible = false;
        }
        internal object? Invoke(string method) => typeof(DesktopSession).GetMethod(method, Private)!.Invoke(Session, null);
        internal void Run(string method)
        {
            var task = (Task)Invoke(method)!; var timer = Stopwatch.StartNew();
            while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(30))
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                Dispatcher.PushFrame(frame); Thread.Sleep(1);
            }
            if (!task.IsCompleted) throw new TimeoutException(method);
            task.GetAwaiter().GetResult();
        }
        public void Dispose() => Session.Dispose();
    }
}
