using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Real file open/revert/save routes on a never-shown desktop, using only injected synthetic capture sources.</summary>
internal static class WindowSourceDocumentBoundaryTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Use --isolated-ui-desktop --window-source-document-boundary-only.");
        if (Application.Current is not null) throw new InvalidOperationException("This fixture needs a fresh private-desktop child process.");
        string root = Path.Combine(output ?? AppContext.BaseDirectory, "window-document-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ApplicationPaths.Configure(["--profile", Path.Combine(root, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys();
        new UserSettings { Theme = "dark", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2,
            PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var bounds = new PixelBounds(-24000, -23000, 640, 360);
        IReadOnlyList<MonitorInfo> monitors = [new("window-document-boundary", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null,
            [OverlayMonitorScope.PrimaryScreen, (Func<IReadOnlyList<MonitorInfo>>)(() => monitors), false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        Field<Window>(desktop, "_toolbar").Opacity = 0;
        var editor = Field<EditorController>(desktop, "_editor");
        var runtime = Field<WindowSourceRuntime>(desktop, "_windowRuntime");
        check("document-boundary fixture is on a non-input private desktop without an editing lease", !PresentationDesktopAvailability.IsCurrentInputDesktop() && !host.HasEditingInput && !host.IsKeyboardIsolationActive);

        var assets = new AssetLibrary();
        using var blue = new SKBitmap(4, 4); blue.Erase(SKColors.Blue);
        using var blueImage = SKImage.FromBitmap(blue); using var png = blueImage.Encode(SKEncodedImageFormat.Png, 100);
        var image = new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 80,
            Transform = Affine2D.Translation(20, 20), AssetId = assets.Add(png.ToArray()),
            WindowSource = new() { BindingIntent = Guid.NewGuid(), TitleHint = "Synthetic only" }, Style = new() { StrokeWidth = 0 } };
        var document = new SceneDocument { Viewport = new(0, 0, 640, 360), Elements = [image] };
        document.Canvas.Grid.Visible = false; document.Canvas.BackgroundColor = 0;
        string firstPath = Path.Combine(root, "original.floatcanvas"), copyPath = Path.Combine(root, "selected-save-copy.floatcanvas");
        Await(new ProjectStore().SaveAsync(firstPath, document, assets));
        var initialOwner = editor.Session;
        Await((Task)Call("OpenProjectPathCoreAsync", firstPath, false)!);
        var owner = editor.Session;
        check("actual OpenProjectPathCoreAsync reads the temporary package and replaces the editor owner", !ReferenceEquals(owner, initialOwner) && owner.Document.ResourceId == document.ResourceId && owner.Document.Elements.Single().WindowSource!.BindingIntent == image.WindowSource.BindingIntent);
        check("opening a descriptor package never automatically binds or captures a hinted window", runtime.BindingCount == 0 && !(bool)Call("IsDirty", owner)!);

        var source = new FakeSource(); object originalToken = Call("WindowOwnerToken", owner)!;
        runtime.Bind(originalToken, owner.Document.ResourceId, new(null, image.Id), image.WindowSource.BindingIntent, source);
        Call("UpdateWindowSourceRuntime");
        check("real bridge input-desktop policy keeps the injected source inactive before synthetic delivery", !source.Active);
        Publish(owner, source, SKColors.Red, 1);
        using var beforeOpen = Retain(owner);
        check("controlled manager adoption provides a retained red source frame without native capture", beforeOpen is { Id.Sequence: 1 } && source.NativeReadCount > 0);

        owner.Execute("temporary opacity", scene => scene.Elements.Single().Style.Opacity = .75);
        owner.Undo(); Call("UpdateWindowSourceRuntime");
        using (var afterUndo = Retain(owner))
            check("ordinary Undo restores document state while preserving same-owner binding intent and frame", afterUndo is { Id.Sequence: 1 } && ReferenceEquals(originalToken, Call("WindowOwnerToken", owner)) && runtime.BindingCount == 1 && source.DisposeCount == 0);

        // SaveSessionAsync has no native-dialog injection. Exercise the real common
        // post-selection writer with the selected output path already in FileState.
        var file = Call("FileState", owner)!; SetPublic(file, "Path", copyPath); SetPublic(file, "DiskVersion", null);
        check("real post-path-selection save writes a separate file without operating a native SaveAs dialog", Await((Task<bool>)Call("SaveSessionAsync", owner, false)!) && File.Exists(copyPath));
        var savedCopy = Await(new ProjectStore().LoadAsync(copyPath));
        using (var afterSave = Retain(owner))
            check("saving to a selected new path preserves editor owner resource intent and native binding", ReferenceEquals(owner, editor.Session) && ReferenceEquals(originalToken, Call("WindowOwnerToken", owner)) && afterSave is { Id.Sequence: 1 } && source.DisposeCount == 0 && savedCopy.Document.ResourceId == document.ResourceId && savedCopy.Document.Elements.Single().WindowSource!.BindingIntent == image.WindowSource.BindingIntent);
        using (var fallback = SKBitmap.Decode(savedCopy.Assets.Get(savedCopy.Document.Elements.Single().AssetId!).Data.ToArray()))
            check("post-selection save packages current source pixels without clearing the live binding", fallback.GetPixel(0, 0) == SKColors.Red && runtime.BindingCount == 1);

        Await((Task)Call("OpenProjectPathCoreAsync", copyPath, false)!);
        var reopened = editor.Session;
        using (var afterOpen = Retain(reopened))
            check("actual explicit reopen of identical resource and intent forgets all prior binding state", !ReferenceEquals(reopened, owner) && reopened.Document.ResourceId == document.ResourceId && reopened.Document.Elements.Single().WindowSource!.BindingIntent == image.WindowSource.BindingIntent && afterOpen is null && runtime.BindingCount == 0 && source.DisposeCount == 1 && !ReferenceEquals(originalToken, Call("WindowOwnerToken", reopened)));
        using (var independentOldMedia = beforeOpen!.Retain())
            check("retained pre-open raster media remains independently alive but cannot resurrect a runtime binding", independentOldMedia.Id.Sequence == 1 && runtime.BindingCount == 0);

        var secondSource = new FakeSource(); object beforeRevertToken = Call("WindowOwnerToken", reopened)!;
        runtime.Bind(beforeRevertToken, reopened.Document.ResourceId, new(null, image.Id), image.WindowSource.BindingIntent, secondSource);
        Publish(reopened, secondSource, SKColors.Lime, 1);
        check("actual bound save establishes the revert baseline without a native file dialog", Await((Task<bool>)Call("SaveSessionAsync", reopened, false)!));
        var documentBeforeRevert = reopened.Document;
        reopened.Execute("unsaved width", scene => scene.Elements.Single().Width = 135);
        check("revert fixture reaches the production dirty and bound-file eligibility guard", (bool)Call("CanRevertActiveFile")!);
        Await((Task)Call("RevertActiveFileAsync")!);
        using (var afterRevert = Retain(reopened))
            check("actual RevertActiveFileAsync keeps its editor owner but forgets same-resource same-intent binding", ReferenceEquals(reopened, editor.Session) && !ReferenceEquals(documentBeforeRevert, reopened.Document) && reopened.Document.ResourceId == document.ResourceId && reopened.Document.Elements.Single().WindowSource!.BindingIntent == image.WindowSource.BindingIntent && afterRevert is null && runtime.BindingCount == 0 && secondSource.DisposeCount == 1 && !ReferenceEquals(beforeRevertToken, Call("WindowOwnerToken", reopened)));
        check("revert loads persisted geometry and static fallback without an automatic source reconnect", reopened.Document.Elements.Single().Width == 100 && !(bool)Call("IsDirty", reopened)! && runtime.BindingCount == 0);
        check("all actual file boundary operations retain the private-desktop no-input contract", !host.HasEditingInput && !host.IsKeyboardIsolationActive && !PresentationDesktopAvailability.IsCurrentInputDesktop());
        Console.WriteLine("WINDOW_DOCUMENT_BOUNDARY_EVIDENCE " + root);

        object? Call(string method, params object?[] arguments) => typeof(DesktopSession).GetMethod(method, Hidden)!.Invoke(desktop, arguments);
        WindowFrameLease? Retain(EditorSession target)
        {
            object?[] arguments = [target, new SourceNodeAddress(null, image.Id), image.WindowSource.BindingIntent, null];
            return (bool)typeof(DesktopSession).GetMethod("TryRetainWindowSourceFrame", Hidden)!.Invoke(desktop, arguments)! ? (WindowFrameLease?)arguments[3] : null;
        }
        void Publish(EditorSession target, FakeSource capture, SKColor color, long sequence)
        {
            Call("UpdateWindowSourceRuntime"); bool updating = Field<bool>(desktop, "_updatingWindowSources"); Set(desktop, "_updatingWindowSources", true);
            try
            {
                runtime.Configure(Call("WindowOwnerToken", target)!, target.Document.ResourceId,
                    [new(new(null, image.Id), image.WindowSource.BindingIntent)], true, new());
                capture.Publish(color, sequence);
                var entries = (System.Collections.IDictionary)typeof(WindowSourceRuntime).GetField("bindings", Hidden)!.GetValue(runtime)!;
                var entry = entries.Values.Cast<object>().Single(value => ReferenceEquals(value.GetType().GetField("Capture", Hidden)!.GetValue(value), capture));
                typeof(WindowSourceRuntime).GetMethod("Drain", Hidden)!.Invoke(runtime, [entry]);
            }
            finally { Set(desktop, "_updatingWindowSources", updating); }
        }
    }

    private sealed class FakeSource : IWindowCaptureSource
    {
        private static long streams;
        private LiveWindowFrame? latest;
        internal bool Active; internal int DisposeCount, NativeReadCount;
        public WindowCaptureTarget Target { get; } = new(1, 1, 1, 1, "Synthetic", "test", "Fake");
        public long StreamId { get; } = Interlocked.Increment(ref streams);
        public long ConnectionVersion => 1;
        public WindowCaptureStatus Status { get; private set; } = WindowCaptureStatus.Starting;
        public Exception? LastError => null;
        public Task Completion => Task.CompletedTask;
        public Task FirstResult => Task.CompletedTask;
        public event Action? FrameReady { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public void SetActive(bool active) => Active = active;
        public void Resume() { }
        public bool TryGetLatest([NotNullWhen(true)] out LiveWindowFrame? frame)
        { NativeReadCount++; frame = Interlocked.Exchange(ref latest, null); return frame is not null; }
        internal void Publish(SKColor color, long sequence)
        {
            var bitmap = new SKBitmap(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul); bitmap.Erase(color); bitmap.SetImmutable();
            Interlocked.Exchange(ref latest, new(StreamId, 1, sequence, new(4, 4), bitmap, 1))?.Dispose(); Status = WindowCaptureStatus.Live;
        }
        public void Dispose() { Active = false; DisposeCount++; Interlocked.Exchange(ref latest, null)?.Dispose(); }
    }
    private static T Field<T>(object owner, string field) => (T)owner.GetType().GetField(field, Hidden)!.GetValue(owner)!;
    private static void Set(object owner, string field, object? value) => owner.GetType().GetField(field, Hidden)!.SetValue(owner, value);
    private static void SetPublic(object owner, string field, object? value) => owner.GetType().GetField(field)!.SetValue(owner, value);
    private static void Await(Task task)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Private document-boundary task did not finish.");
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x5f61, 7, key)) continue;
            try { if (!RegisterHotKey(0, 0x5f62, 7, key + 1)) continue; UnregisterHotKey(0, 0x5f62); return (new(7, key), new(7, key + 1)); }
            finally { UnregisterHotKey(0, 0x5f61); }
        }
        throw new InvalidOperationException("No test hotkeys available.");
    }
    private static string DesktopName()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
