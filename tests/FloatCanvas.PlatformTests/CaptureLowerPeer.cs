using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;

namespace FloatCanvas.PlatformTests;

/// <summary>A test-owned lower application. Its topmost background is not an in-process overlay tool.</summary>
internal sealed class CaptureLowerPeer : IDisposable
{
    private sealed record Ready(int ProcessId, long StartedUtcTicks, string Nonce, long Window);
    private sealed record ForegroundGrant(int PeerProcessId, long StartedUtcTicks, string Nonce, int ParentProcessId,
        long ForegroundWindow, bool Granted, int Clicks, int Ups);
    private readonly Process process;
    private readonly string closePath;
    private readonly Ready identity;
    private readonly string grantPath;
    private ForegroundGrant? foregroundGrant;
    private bool closed;
    internal object Evidence => new { parentProcessId = Environment.ProcessId, peerProcessId = identity.ProcessId,
        identity.StartedUtcTicks, identity.Nonce, window = identity.Window, closed, foregroundGrant,
        ownership = "PID, creation time, nonce and HWND process verified before use; peer destroys its own HWND after nonce close request." };
    internal NativeTestWindow Target { get; }
    private CaptureLowerPeer(Process process, string closePath, string grantPath, Ready identity)
    { this.process = process; this.closePath = closePath; this.grantPath = grantPath; this.identity = identity; Target = new((nint)identity.Window); }

    internal static async Task<CaptureLowerPeer> StartAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        string token = Guid.NewGuid().ToString("N");
        string readyPath = Path.Combine(directory, "lower-peer-" + token + ".ready.json"), closePath = Path.Combine(directory, "lower-peer-" + token + ".close");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string value in new[] { "--capture-lower-peer", readyPath, closePath, token, Environment.ProcessId.ToString() }) start.ArgumentList.Add(value);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the capture target peer.");
        try
        {
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(readyPath))
            {
                if (process.HasExited) throw new InvalidOperationException("Capture target peer exited before readiness: " + process.ExitCode);
                if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Capture target peer did not become ready.");
                await Task.Delay(10);
            }
            var ready = JsonSerializer.Deserialize<Ready>(await File.ReadAllTextAsync(readyPath))!;
            GetWindowThreadProcessId((nint)ready.Window, out uint windowProcess);
            if (ready.ProcessId != process.Id || ready.StartedUtcTicks != process.StartTime.ToUniversalTime().Ticks ||
                ready.Nonce != token || ready.Window == 0 || windowProcess != ready.ProcessId)
                throw new InvalidOperationException("Capture peer ownership did not match its PID, creation time, nonce and HWND.");
            return new(process, closePath, readyPath + ".foreground.json", ready);
        }
        catch
        {
            if (!process.HasExited) { File.WriteAllText(closePath, token); process.WaitForExit(2000); }
            process.Dispose(); throw;
        }
    }

    internal static int Run(string readyPath, string closePath, string nonce, int parentProcessId)
    {
        using var target = new NativeTestWindow();
        Native.SetWindowPos(target.Handle, -1, 0, 0, 0, 0, 0x13);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (File.Exists(closePath) && File.ReadAllText(closePath) == nonce) dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            if (target.Counts.Clicks > 0 && target.Counts.Ups > 0 && Native.GetForegroundWindow() == target.Handle && !File.Exists(readyPath + ".foreground.json"))
            {
                bool granted = AllowSetForegroundWindow((uint)parentProcessId);
                var grant = new ForegroundGrant(Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                    nonce, parentProcessId, target.Handle.ToInt64(), granted, target.Counts.Clicks, target.Counts.Ups);
                File.WriteAllText(readyPath + ".foreground.json.tmp", JsonSerializer.Serialize(grant));
                File.Move(readyPath + ".foreground.json.tmp", readyPath + ".foreground.json");
            }
        };
        timer.Start();
        dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(80);
            await File.WriteAllTextAsync(readyPath + ".tmp", JsonSerializer.Serialize(new Ready(Environment.ProcessId,
                Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, nonce, target.Handle.ToInt64())));
            File.Move(readyPath + ".tmp", readyPath);
        });
        try { Dispatcher.Run(); return 0; }
        finally { timer.Stop(); }
    }

    internal async Task GrantForegroundAsync(Native.Point point, nint hostDisplay)
    {
        RequireOwnedPoint(); Native.Move(point);
        RequireOwnedPoint();
        if (!Native.GetCursorPos(out var cursor) || cursor.X != point.X || cursor.Y != point.Y)
            throw new InvalidOperationException("Cursor left the owned capture target before the balanced test click.");
        // One checked, balanced input record cannot leave a held button behind.
        Native.Mouse(0x0002 | 0x0004);
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(grantPath))
        {
            if (process.HasExited || elapsed.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("Owned peer did not receive foreground and grant it to the fixture.");
            await Task.Delay(10);
        }
        var grant = JsonSerializer.Deserialize<ForegroundGrant>(await File.ReadAllTextAsync(grantPath))!;
        if (grant.PeerProcessId != identity.ProcessId || grant.StartedUtcTicks != identity.StartedUtcTicks || grant.Nonce != identity.Nonce ||
            grant.ParentProcessId != Environment.ProcessId || grant.ForegroundWindow != identity.Window || !grant.Granted || grant.Clicks < 1 || grant.Ups < 1)
            throw new InvalidOperationException("Foreground grant did not originate from the owned peer's real click.");
        foregroundGrant = grant;
        GetWindowThreadProcessId(hostDisplay, out uint hostProcess);
        if (hostProcess != Environment.ProcessId || process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartedUtcTicks)
            throw new InvalidOperationException("Capture target/display ownership changed before restoring the lower application order.");
        if (!Native.SetWindowPos(Target.Handle, hostDisplay, 0, 0, 0, 0, 0x13))
            throw new InvalidOperationException("Could not restore the owned peer below the owned overlay display.");

        void RequireOwnedPoint()
        {
            GetWindowThreadProcessId(Target.Handle, out uint owner);
            if (process.HasExited || process.Id != identity.ProcessId || process.StartTime.ToUniversalTime().Ticks != identity.StartedUtcTicks ||
                identity.Nonce.Length == 0 || owner != identity.ProcessId || Native.WindowFromPoint(point) != Target.Handle || !Target.OwnsPoint(point))
                throw new InvalidOperationException("Refusing input outside the PID/time/nonce-verified capture target.");
        }
    }

    public void Dispose()
    {
        Target.Dispose();
        if (!process.HasExited)
        {
            if (process.Id != identity.ProcessId || process.StartTime.ToUniversalTime().Ticks != identity.StartedUtcTicks)
                throw new InvalidOperationException("Capture peer process identity changed before cleanup.");
            File.WriteAllText(closePath, identity.Nonce);
            if (!process.WaitForExit(2000)) throw new TimeoutException("The owned capture target did not close: " + identity.ProcessId);
        }
        process.Dispose();
        closed = true;
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
}
