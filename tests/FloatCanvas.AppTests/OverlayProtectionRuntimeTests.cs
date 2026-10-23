using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class OverlayProtectionRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        new UserSettings { Toggle = new(7, 0x79), Hide = new(7, 0x7A), ShowFloatingLogo = false }.Save();
        nint foreground = GetForegroundWindow();
        var protection = new OverlayBackendProtection(ApplicationPaths.ProfileDirectory, false, true, () => (true, "fixture"));
        var host = HiddenHost();
        check("runtime fixture initializes the actual enhanced window backend while hidden", protection.TryEnable(host.EnableSystemInput));
        DesktopSession? old = new(app, host, protection);
        DesktopSession? replacement = null;
        try
        {
            var initialize = (Task)typeof(DesktopSession).GetMethod("InitializeDocumentsAsync", Private)!.Invoke(old, [false])!;
            Wait(initialize);
            var editor = Get<EditorController>(old, "_editor");
            var live = editor.Session;
            live.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 30 });
            using var bitmap = SKSurface.Create(new SKImageInfo(2, 2));
            bitmap.Canvas.Clear(SKColors.Blue);
            using var image = bitmap.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            var assets = Get<AssetLibrary>(old, "_assets");
            string asset = assets.Add(png.ToArray(), "image/png");
            live.Add(new CanvasElement { Kind = ElementKind.Image, Width = 2, Height = 2, AssetId = asset });
            object binding = typeof(DesktopSession).GetMethod("FileState", Private)!.Invoke(old, [live])!;
            string boundPath = System.IO.Path.Combine(ApplicationPaths.ProfileDirectory, "bound.floatcanvas");
            binding.GetType().GetField("Path")!.SetValue(binding, boundPath);
            binding.GetType().GetField("SavedFingerprint")!.SetValue(binding, "saved-before-fallback");
            int oldChanges = 0;
            editor.Changed += () => oldChanges++;
            bool requested = false;
            old.DesktopFallbackRequested += _ => requested = true;
            check("typed native failure queues a one-way desktop fallback", old.TryRequestDesktopFallback(new COMException("fixture input failure")) && protection.IsDisabled);
            PumpUntil(() => requested);
            var capture = old.PrepareDesktopFallbackAsync();
            Wait(capture);
            var state = capture.GetAwaiter().GetResult();
            check("input owner is out of editing before presentation replacement", !host.IsEditing);
            check("fallback snapshot keeps the same document session and undo history", ReferenceEquals(state.Live, live) && live.CanUndo && live.Document.Elements.Count == 2);
            old.Dispose(); old = null;
            int detachedChanges = oldChanges;
            check("old system window scope is removed before constructing desktop controls", OverlayWindowScope.Current is null);
            var desktop = HiddenHost();
            replacement = new(app, desktop, protection, state);
            typeof(DesktopSession).GetMethod("RestoreFallbackWorkspace", Private)!.Invoke(replacement, [state]);
            check("replacement uses ordinary desktop windows and keyboard routing", !replacement.UsesEnhancedOverlay && desktop.IsKeyboardIsolationEnabled);
            var current = Get<EditorController>(replacement, "_editor");
            check("desktop presentation shares the original live document and assets", ReferenceEquals(current.Session, live) && ReferenceEquals(Get<AssetLibrary>(replacement, "_assets"), assets) && assets.TryGet(asset, out _));
            object restoredBinding = typeof(DesktopSession).GetMethod("FileState", Private)!.Invoke(replacement, [live])!;
            check("fallback retains bound filename and saved fingerprint", Equals(restoredBinding.GetType().GetField("Path")!.GetValue(restoredBinding), boundPath) && Equals(restoredBinding.GetType().GetField("SavedFingerprint")!.GetValue(restoredBinding), "saved-before-fallback"));
            check("undo works after the presentation and input backend are replaced", current.Session.Undo() && current.Session.Document.Elements.Count == 1);
            check("disposed editor no longer receives document notifications", oldChanges == detachedChanges);
            check("redo restores original content in desktop mode", current.Session.Redo() && current.Session.Document.Elements.Count == 2);
            check("fallback fixture never changes external foreground", GetForegroundWindow() == foreground);
        }
        finally { replacement?.Dispose(); old?.Dispose(); }

        using var rollbackHost = HiddenHost();
        var original = rollbackHost.Diagnostics.Select(pair => (pair.DisplayHandle, pair.InputHandle)).ToArray();
        var rollback = new OverlayBackendProtection(System.IO.Path.Combine(ApplicationPaths.ProfileDirectory, "rollback"), false, false, () => (true, "fixture"));
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(rollbackHost, true);
        void FailFirstFrame(SKSurface _) => throw new InvalidOperationException("fixture initial presentation failure");
        rollbackHost.Draw += FailFirstFrame;
        check("actual enhanced initialization failure returns to the original desktop HWNDs", !rollback.TryEnable(rollbackHost.EnableSystemInput) &&
            !rollbackHost.UsesSystemInput && original.SequenceEqual(rollbackHost.Diagnostics.Select(pair => (pair.DisplayHandle, pair.InputHandle))));
        rollbackHost.Draw -= FailFirstFrame;
        check("failed initialization never shows candidate or restored windows", rollbackHost.Diagnostics.All(pair => !pair.DisplayVisible && !pair.InputVisible));
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(rollbackHost, false);
        check("initialization rollback also preserves external foreground", GetForegroundWindow() == foreground);
    }

    private static T Get<T>(object value, string field) => (T)value.GetType().GetField(field, Private)!.GetValue(value)!;
    private static OverlayHost HiddenHost() => (OverlayHost)typeof(OverlayHost).GetConstructor(Private, null,
        [typeof(OverlayMonitorScope), typeof(bool)], null)!.Invoke([OverlayMonitorScope.PrimaryScreen, false]);
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> ready)
    {
        var frame = new DispatcherFrame();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || watch.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!ready()) throw new TimeoutException("Hidden fallback fixture did not finish.");
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
