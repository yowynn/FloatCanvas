using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Real enhanced and ordinary HWNDs, all outside every physical monitor.
/// No input lease, desktop capture, hotkeys or foreground activation.</summary>
internal static class Phase3SystemScreenshotTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? captureProcess = null)
    {
        bool externalCapture = captureProcess is not null;
        string root = Environment.GetEnvironmentVariable("FLOATCANVAS_PHASE3_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-phase3-screenclip");
        string profile = Path.Combine(root, "offscreen-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile); ApplicationPaths.Configure(["--profile", profile]);
        new UserSettings { Language = "en", Theme = "light", ShowFloatingLogo = false }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int ownForeground = 0;
        WinEvent foreground = (_, _, window, _, _, _, _) => { GetWindowThreadProcessId(window, out uint pid); if (pid == Environment.ProcessId) ownForeground++; };
        nint hook = SetWinEventHook(3, 3, 0, foreground, 0, 0, 0);
        var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
        var tile = new PixelBounds(-20000, -20000, 640, 360);
        Func<IReadOnlyList<MonitorInfo>> provider = () => [primary with { Bounds = tile, WorkArea = tile }];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, true], null)!;
        host.EnableSystemInput();
        using var peer = new HwndSource(new HwndSourceParameters("FloatCanvas own screenclip peer") { PositionX = tile.Left, PositionY = tile.Top,
            Width = tile.Width, Height = tile.Height, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000088 });
        using var session = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
        var editor = Field<EditorController>(session, "_editor");
        string before = JsonSerializer.Serialize(editor.Session.Document);
        Window? tool = null, retained = null;
        ScreenshotPeerFixture? external = null;
        try
        {
            tool = Tool(Brushes.LimeGreen, 100, 50);
            retained = Tool(Brushes.Red, 320, 100);
            var rendered = SKColors.Blue;
            host.Draw += surface => surface.Canvas.Clear(rendered);
            host.Render();
            var originals = host.Diagnostics.Select(d => d.DisplayHandle).ToArray();
            check("screenshot fixture starts with a real enhanced display", GetWindowBand(originals[0], out uint originalBand) && originalBand == 16);
            if (externalCapture)
            {
                foreach (string mode in new[] { "small", "bordered", "notopmost", "transparent", "main" })
                {
                    using var rejected = ScreenshotPeerFixture.Start(mode);
                    Invoke("RepairWindowOrder");
                    check($"ShareX {mode} window does not interrupt the enhanced canvas", !host.IsSystemScreenshotActive && originals.All(IsWindowVisible));
                }
                using (var unrelated = ScreenshotPeerFixture.Start("region", "UnlistedCapture"))
                {
                    Invoke("RepairWindowOrder");
                    check("an unlisted fullscreen capture-like window does not trigger automatic handoff", !host.IsSystemScreenshotActive && originals.All(IsWindowVisible));
                }
            }
            // Arm the session policy without acquiring any real input lease.
            typeof(OverlayHost).GetField("<IsEditing>k__BackingField", Private)!.SetValue(host, true);
            if (externalCapture)
            {
                external = ScreenshotPeerFixture.Start("region", captureProcess!);
                Invoke("RepairWindowOrder");
                check($"{captureProcess} region UI triggers production handoff and hides the enhanced canvas", host.IsSystemScreenshotActive && originals.All(handle => !IsWindowVisible(handle)));
            }
            else Invoke("StartSystemScreenshot", peer.Handle, true);
            nint peerHandle = external?.Handle ?? peer.Handle;
            check("system screenshot releases editing even when global foreground did not switch", host.IsSystemScreenshotActive && !host.IsEditing);
            Field<UserSettings>(session, "_settings").ShowPassThroughLauncher = true;
            typeof(DesktopSession).GetMethod("RefreshPassThroughLauncher", Private)!.Invoke(session, null);
            check("focus-loss refresh cannot create a new enhanced launcher above screenshot UI", typeof(DesktopSession).GetField("_passThroughLauncher", Private)!.GetValue(session) is null);
            var snapshots = (System.Collections.IEnumerable)Field<object>(host, "_systemScreenshot");
            object item = snapshots.Cast<object>().Single();
            var state = item.GetType().GetField("Item1")!.GetValue(item)!;
            nint mirror = (nint)state.GetType().GetField("Handle", Private | BindingFlags.Public)!.GetValue(state)!;
            var buffer = item.GetType().GetField("Item2")!.GetValue(item)!;
            var surface = (SKSurface)buffer.GetType().GetProperty("Surface", Private)!.GetValue(buffer)!;
            using (var image = surface.Snapshot())
            using (var pixels = SKBitmap.FromImage(image))
            {
                check("desktop screenshot retains the last canvas pixels", pixels.GetPixel(20, 20) == SKColors.Blue);
                check("desktop screenshot composites the real WPF tool over canvas", pixels.GetPixel(130, 70) == new SKColor(50, 205, 50));
            }
            check("screenshot presentation uses the ordinary desktop band", GetWindowBand(mirror, out uint mirrorBand) && mirrorBand == 1 && IsWindowVisible(mirror));
            check("live enhanced displays and tools cannot cover system screenshot UI", originals.All(handle => !IsWindowVisible(handle)) &&
                !IsWindowVisible(new WindowInteropHelper(tool).Handle) && !IsWindowVisible(new WindowInteropHelper(retained).Handle));
            check("native hiding preserves logical window state for the real focus-loss policy", tool.IsVisible && retained.IsVisible && !host.IsEditing);
            SetWindowPos(peerHandle, -1, 0, 0, 0, 0, 0x53);
            check("a later ordinary topmost screenshot peer can cover the snapshot", Above(peerHandle, mirror));
            if (externalCapture)
            {
                Invoke("HandleSystemScreenshotMessage", 0x113u, (nuint)0x464D);
                check($"{captureProcess} region UI keeps the handoff active across timer ticks", host.IsSystemScreenshotActive && Above(peerHandle, mirror));
            }
            rendered = SKColors.Red; host.Render();
            using (var image = surface.Snapshot())
            using (var pixels = SKBitmap.FromImage(image)) check("new live rendering does not alter the frozen screenshot presentation", pixels.GetPixel(20, 20) == SKColors.Blue);
            tool.Hide();
            if (externalCapture)
            {
                external!.Dispose(); external = null;
                Invoke("HandleSystemScreenshotMessage", 0x113u, (nuint)0x464D);
            }
            else Invoke("EndSystemScreenshot", true);
            check("ending the presentation destroys its temporary HWND and restores enhanced display", !IsWindow(mirror) && originals.All(IsWindowVisible));
            check("tools closed by focus loss stay closed while retained visuals recover", !tool.IsVisible && !IsWindowVisible(new WindowInteropHelper(tool).Handle) &&
                retained.IsVisible && IsWindowVisible(new WindowInteropHelper(retained).Handle));
            check("screenshot presentation leaves the document and input ownership unchanged", before == JsonSerializer.Serialize(editor.Session.Document) && !host.IsEditing && !host.HasEditingInput);
            if (externalCapture)
                foreach (string mode in new[] { "localized", "fullscreen-editor" })
                {
                    using var recognized = ScreenshotPeerFixture.Start(mode, captureProcess!);
                    check($"{captureProcess} {mode} uses the same caption-independent capture policy", (bool)typeof(OverlayHost).GetMethod("IsSystemScreenshotWindow", Private)!.Invoke(host, [recognized.Handle])!);
                }
            var captureTools = host.CaptureToolVisuals;
            host.CaptureToolVisuals = () => throw new InvalidOperationException("own screenshot allocation failure");
            typeof(OverlayHost).GetField("<IsEditing>k__BackingField", Private)!.SetValue(host, true);
            Invoke("StartSystemScreenshot", peer.Handle, true);
            check("a failed presentation still releases editing and leaves no temporary snapshot", !host.IsSystemScreenshotActive && !host.IsEditing);
            host.CaptureToolVisuals = captureTools;
            Invoke("StartSystemScreenshot", peer.Handle, true);
            object rebuildItem = ((System.Collections.IEnumerable)Field<object>(host, "_systemScreenshot")).Cast<object>().Single();
            var rebuildState = rebuildItem.GetType().GetField("Item1")!.GetValue(rebuildItem)!;
            nint rebuildMirror = (nint)rebuildState.GetType().GetField("Handle", Private | BindingFlags.Public)!.GetValue(rebuildState)!;
            Invoke("RebuildMonitors");
            check("monitor rebuilding clears temporary presentation without losing retained tools", !host.IsSystemScreenshotActive && !IsWindow(rebuildMirror) &&
                retained.IsVisible && IsWindowVisible(new WindowInteropHelper(retained).Handle));
            Pump(); check("offscreen screenshot regression never takes foreground", ownForeground == 0);
        }
        finally { external?.Dispose(); tool?.Close(); retained?.Close(); UnhookWinEvent(hook); GC.KeepAlive(foreground); }

        Window Tool(Brush background, int x, int y)
        {
            var window = new Window { Width = 100, Height = 50, Left = -20000, Top = -20000, ShowActivated = false,
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = background, Style = null };
            new WindowInteropHelper(window).Owner = host.SystemWindowOwner;
            window.Show();
            SetWindowPos(new WindowInteropHelper(window).Handle, 0, tile.Left + x, tile.Top + y, 100, 50, 0x14);
            Pump(); return window;
        }
        void Invoke(string method, params object[] args) => typeof(OverlayHost).GetMethod(method, Private)!.Invoke(host, args);
        void Pump()
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }
    private static bool Above(nint first, nint second)
    {
        for (nint window = GetWindow(first, 2); window != 0; window = GetWindow(window, 2)) if (window == second) return true;
        return false;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private delegate void WinEvent(nint hook, uint id, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowBand(nint window, out uint band);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
