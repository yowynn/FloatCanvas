using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

internal static class WindowSourcePersistenceDesktopTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal)) throw new InvalidOperationException("Use --isolated-ui-desktop --window-source-persistence-desktop-only.");
        if (Application.Current is not null) throw new InvalidOperationException("Use a fresh test process.");
        string root = Path.Combine(output ?? AppContext.BaseDirectory, "window-persistence-desktop-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        ApplicationPaths.Configure(["--profile", Path.Combine(root, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys(); new UserSettings { Theme = "light", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2, PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var bounds = new PixelBounds(-23000, -23000, 640, 360);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("window-persistence", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false; Field<Window>(desktop, "_toolbar").Opacity = 0;
        ((IDisposable?)typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.GetValue(host))?.Dispose(); typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.SetValue(host, null);
        var editor = Field<EditorController>(desktop, "_editor"); var owner = editor.Session;
        var media = Field<WindowMediaCheckpointState>(desktop, "_windowMediaCheckpoint");
        Console.WriteLine("WINDOW_MEDIA_BASELINE " + JsonSerializer.Serialize(new { Elements = owner.Document.Elements.Count,
            Bindings = Field<WindowSourceRuntime>(desktop, "_windowRuntime").BindingCount, media.Generation }));
        check("fresh empty Desktop without window sources or accepted native media keeps generation zero", owner.Document.Elements.Count == 0 &&
            Field<WindowSourceRuntime>(desktop, "_windowRuntime").BindingCount == 0 && media.Generation == 0);
        var ordinary = new CanvasElement { Kind = ElementKind.Rectangle, Width = 20, Height = 20 };
        owner.Add(ordinary);
        owner.ExecuteSettings("ordinary settings only", document => document.Canvas.Grid.FlatCellSize = 27);
        check("author-only ordinary shape and canvas settings cannot manufacture window media generations", media.Generation == 0 &&
            Field<WindowSourceRuntime>(desktop, "_windowRuntime").BindingCount == 0);
        owner.Undo(); owner.Undo();
        using var black = new SKBitmap(2, 2); black.Erase(SKColors.Black); using var blackImage = SKImage.FromBitmap(black); using var blackPng = blackImage.Encode(SKEncodedImageFormat.Png, 100);
        string fallback = owner.Assets.Add(blackPng.ToArray());
        var image = new CanvasElement { Kind = ElementKind.Image, AssetId = fallback, Width = 80, Height = 80,
            Transform = Affine2D.Translation(owner.Document.Viewport.Left + 20, owner.Document.Viewport.Top + 20),
            WindowSource = new() { BindingIntent = Guid.NewGuid() }, ImageCrop = new(.25, 0, .5, 1) };
        owner.Add(image); Invoke("UpdateWindowSourceRuntime");
        Field<WindowSourceRuntime>(desktop, "_windowRuntime").Dispose();
        // Keep this save-only fixture independent of the real bridge's input-desktop
        // policy and its synchronous render->reconfigure callback. The real runtime
        // adoption and Dispatcher contracts have a separate independent audit.
        var runtime = new WindowSourceRuntime(app.Dispatcher, (_, _) => { });
        runtime.MediaFrameAccepted += () => Invoke("ScheduleWindowMediaCheckpoint");
        typeof(DesktopSession).GetField("_windowRuntime", Hidden)!.SetValue(desktop, runtime);
        var token = Invoke("WindowOwnerToken", owner)!; var source = new FakeSource();
        runtime.Bind(token, owner.Document.ResourceId, new(null, image.Id), image.WindowSource.BindingIntent, source);
        // An undisplayed private desktop must fail the actual input-desktop gate.
        // Only the manager demand is simulated; the save/recovery methods remain real.
        PublishFixtureFrame(SKColors.Red, 1); PumpUntil(() => runtime.State(token, image.WindowSource.BindingIntent).HasFrame);
        check("private Desktop save fixture retains synthetic pixels using an explicitly simulated manager demand", runtime.State(token, image.WindowSource.BindingIntent).HasFrame);
        check("only a successfully adopted fake native frame advances the real Desktop media generation", media.Generation > 0);
        string before = JsonSerializer.Serialize(owner.Document); long version = owner.ContentVersion; int undo = owner.UndoCount, assetCount = owner.Assets.Count;
        string path = Path.Combine(root, "saved.floatcanvas"); var state = Invoke("FileState", owner)!; Set(state, "Path", path);
        check("actual SaveSessionAsync accepts a bound output without opening a native file dialog", Await((Task<bool>)Invoke("SaveSessionAsync", owner, false)!));
        var loaded = Await(new ProjectStore().LoadAsync(path));
        using (var pixels = SKBitmap.Decode(loaded.Assets.Get(loaded.Document.Elements.Single().AssetId!).Data.ToArray()))
            check("actual project save writes the complete latest red source frame", pixels.Width == 4 && pixels.Height == 4 && pixels.GetPixel(0, 0) == SKColors.Red);
        check("actual SaveSession changes no active fallback asset document content version or undo", JsonSerializer.Serialize(owner.Document) == before && owner.ContentVersion == version && owner.UndoCount == undo && owner.Assets.Count == assetCount && !(bool)Invoke("IsDirty", owner)!);
        string savedFingerprint = (string)state.GetType().GetField("SavedFingerprint")!.GetValue(state)!; var firstDisk = Await(ProjectFileVersion.ReadAsync(path));
        PublishFixtureFrame(SKColors.Blue, 2); PumpUntil(() => runtime.TryRetainFrame(token, owner.Document.ResourceId, new(null, image.Id), image.WindowSource.BindingIntent, out var frame) && CheckAndDispose(frame, 2));
        check("streaming blue frame remains outside the author dirty signal", !(bool)Invoke("IsDirty", owner)! && (bool)Invoke("HasWindowMediaPending", owner)!);
        check("actual second explicit SaveSession persists new pixels without an author edit", Await((Task<bool>)Invoke("SaveSessionAsync", owner, false)!));
        var second = Await(new ProjectStore().LoadAsync(path));
        using (var pixels = SKBitmap.Decode(second.Assets.Get(second.Document.Elements.Single().AssetId!).Data.ToArray()))
            check("second explicit save replaces the package fallback with blue source pixels", pixels.GetPixel(0, 0) == SKColors.Blue);
        check("media-only project save keeps author fingerprint and content but changes strict disk identity", Equals(savedFingerprint, state.GetType().GetField("SavedFingerprint")!.GetValue(state)) &&
            Await(ProjectFileVersion.ReadAsync(path)) != firstDisk && owner.ContentVersion == version && owner.UndoCount == undo && owner.Assets.Count == assetCount);
        var snapshot = (WorkspaceState)Invoke("SnapshotWorkspace")!;
        Await((Task)Invoke("SaveRecoverySnapshotAsync", snapshot, null, null)!);
        var recovered = Await(Field<RecoveryStore>(desktop, "_recoveryStore").LoadAsync())!;
        using (var pixels = SKBitmap.Decode(recovered.Assets.Get(recovered.State.LiveDocument.Elements.Single().AssetId!).Data.ToArray()))
            check("actual recovery helper packages the latest source frame with a local library", pixels.GetPixel(0, 0) == SKColors.Blue);
        check("recovery preparation leaves the editor snapshot history and asset table unchanged", JsonSerializer.Serialize(owner.Document) == before && owner.ContentVersion == version && owner.UndoCount == undo && owner.Assets.Count == assetCount);
        Console.WriteLine("WINDOW_PERSISTENCE_DESKTOP_EVIDENCE " + root);

        object? Invoke(string name, params object?[] args) => typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(desktop, args);
        void PublishFixtureFrame(SKColor color, long sequence)
        {
            runtime.Configure(token, owner.Document.ResourceId, [new(new(null, image.Id), image.WindowSource.BindingIntent)], true, new());
            source.Publish(color, sequence);
            // Deliver on the UI thread before the real private-desktop render callback
            // restores its unavailable-input-desktop gate. Dispatcher delivery itself
            // is covered by WindowSourceRuntimeTests, not by this save-only fixture.
            var bindings = (System.Collections.IDictionary)typeof(WindowSourceRuntime).GetField("bindings", Hidden)!.GetValue(runtime)!;
            typeof(WindowSourceRuntime).GetMethod("Drain", Hidden)!.Invoke(runtime, [bindings.Values.Cast<object>().Single()]);
        }
        void PumpUntil(Func<bool> done)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew(); while (!done()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Synthetic Desktop frame was not adopted."); Pump(); }
        }
    }
    private static bool CheckAndDispose(FloatCanvas.Rendering.WindowFrameLease? frame, long sequence) { using (frame) return frame?.Id.Sequence == sequence; }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name)!.SetValue(owner, value);
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void Await(Task task) { while (!task.IsCompleted) Pump(); task.GetAwaiter().GetResult(); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private sealed class FakeSource : IWindowCaptureSource
    {
        private LiveWindowFrame? latest; public WindowCaptureTarget Target { get; } = new(1, 1, 1, 1, "Synthetic", "test", "Fake");
        public long StreamId => 1; public long ConnectionVersion => 1; public WindowCaptureStatus Status => WindowCaptureStatus.Live;
        public Exception? LastError => null; public Task Completion => Task.CompletedTask; public Task FirstResult => Task.CompletedTask;
        public event Action? FrameReady; public event Action? StateChanged { add { } remove { } }
        internal bool Active; public void SetActive(bool value) => Active = value; public void Resume() { }
        public bool TryGetLatest([NotNullWhen(true)] out LiveWindowFrame? frame) { frame = Interlocked.Exchange(ref latest, null); return frame is not null; }
        internal void Publish(SKColor color, long sequence) { var bitmap = new SKBitmap(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul); bitmap.Erase(color); bitmap.SetImmutable(); Interlocked.Exchange(ref latest, new(1, 1, sequence, new(4, 4), bitmap, 1))?.Dispose(); FrameReady?.Invoke(); }
        public void Dispose() { Active = false; Interlocked.Exchange(ref latest, null)?.Dispose(); }
    }
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x5c01, 7, key)) continue;
            try { if (!RegisterHotKey(0, 0x5c02, 7, key + 1)) continue; UnregisterHotKey(0, 0x5c02); return (new(7, key), new(7, key + 1)); }
            finally { UnregisterHotKey(0, 0x5c01); }
        }
        throw new InvalidOperationException("No test hotkeys available.");
    }
    private static string DesktopName() { var value = new StringBuilder(256); if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, value, value.Capacity * 2, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); return value.ToString(); }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
