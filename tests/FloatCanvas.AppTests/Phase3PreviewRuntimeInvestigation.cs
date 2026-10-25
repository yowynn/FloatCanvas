using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Opt-in production render routing probe, using only our offscreen HWNDs.
/// No input lease, hotkeys, foreground changes or desktop capture.</summary>
internal static class Phase3PreviewRuntimeInvestigation
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        string root = Environment.GetEnvironmentVariable("FLOATCANVAS_PHASE3_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-phase3");
        string profile = Path.Combine(root, "routing-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile); ApplicationPaths.Configure(["--profile", profile]);
        new UserSettings { Language = "en", Theme = "light", ShowFloatingLogo = false }.Save();
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        nint foreground = GetForegroundWindow();
        int ownForegroundEvents = 0;
        WinEvent callback = (_, _, window, _, _, _, _) =>
        { GetWindowThreadProcessId(window, out uint process); if (process == Environment.ProcessId) ownForegroundEvents++; };
        nint hook = SetWinEventHook(3, 3, 0, callback, 0, 0, 0);
        if (hook == 0) throw new System.ComponentModel.Win32Exception();
        try
        {
        var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
        var monitors = new[] { primary with { Bounds = new(-20000, -20000, 640, 360) },
            primary with { DeviceName = "Offscreen.Second", IsPrimary = false, Bounds = new(-19000, -20000, 640, 360) } };
        Func<IReadOnlyList<MonitorInfo>> provider = () => monitors;
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
            [OverlayMonitorScope.AllScreens, provider, true], null)!;
        using var session = new DesktopSession(application, host);
        Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
        var editor = Field<EditorController>(session, "_editor");
        var preview = Field<CanvasPreviewPump>(session, "_canvasPreview");
        var frames = new List<OverlayFrameTiming>();
        host.FrameSubmitted += frames.Add;
        editor.Session.Execute("offscreen probe", doc => doc.Elements = [new() { Kind = ElementKind.Rectangle,
            Width = 40, Height = 30, Transform = Affine2D.Translation(-19980, -19980), Style = new() { Fill = 0xFF2383E2 } }]);
        Settle(); frames.Clear();
        Invoke("RequestRender"); Settle();
        var full = frames.Select(f => f.Outputs.Count).ToArray(); frames.Clear();
        Invoke("RequestPixelRender", new[] { monitors[0].Bounds }); Settle();
        var dirty = frames.Select(f => f.Outputs.Count).ToArray();
        var result = new { Case = "production-render-routing", FullEditOutputCounts = full, SingleMonitorDirtyOutputCounts = dirty,
            ForegroundUnchanged = foreground == GetForegroundWindow(), OwnForegroundEvents = ownForegroundEvents, host.IsEditing,
            Scope = "Actual production RenderFrame/PresentCanvasPreview and native submissions; offscreen ordinary backend" };
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "preview-routing-investigation.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PREVIEW_INVESTIGATION " + JsonSerializer.Serialize(result));
        check("routing probe uses two offscreen own displays without taking foreground or input", host.Monitors.Count == 2 && !host.IsEditing && ownForegroundEvents == 0);
        check("routing probe observes real native submissions", full.Length > 0 && dirty.Length > 0);

        void Settle()
        {
            var timer = Stopwatch.StartNew();
            // A Render operation may not have started its worker yet.
            do { Phase2RenderTests.Until(() => !preview.Busy, timeout: 10000); Pump(); }
            while ((preview.Busy || (bool)typeof(DesktopSession).GetProperty("IsRenderPending", Private)!.GetValue(session)!) && timer.ElapsedMilliseconds < 10000);
            if (preview.Busy) throw new TimeoutException("Offscreen preview did not settle.");
        }
        void Pump()
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            application.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        void Invoke(string method, params object?[] args) => typeof(DesktopSession).GetMethod(method, Private)!.Invoke(session, args);
        }
        finally { UnhookWinEvent(hook); GC.KeepAlive(callback); }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private delegate void WinEvent(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
