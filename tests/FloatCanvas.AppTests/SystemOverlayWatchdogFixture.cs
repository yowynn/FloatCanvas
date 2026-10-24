using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;

/// <summary>Opt-in foreground gate: a slow owner survives and releases input normally after resuming.
/// Reuses the owned F6 receiver and actual production host.
/// Never included in default tests. The supervisor owns both children: up to 60s for real GUI
/// activation, then a separate 15s input phase, with a 75s total kill-on-close job deadline.</summary>
internal static class SystemOverlayWatchdogFixture
{
    private const string RunVariable = "FLOATCANVAS_WATCHDOG_GATE_DIRECTORY";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record ReceiverIdentity(int Process, long StartUtcTicks, long Handle);
    private sealed record OwnerState(int Process, string Stage, bool HasInput, int F6Down, long Foreground, long Tick);

    internal static int Run()
    {
        string root = Environment.GetEnvironmentVariable(RunVariable)
            ?? throw new InvalidOperationException("Set an external watchdog evidence directory before this explicit foreground test.");
        root = Path.GetFullPath(root);
        string run = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        string executable = Environment.ProcessPath!;
        Process? receiver = null, owner = null;
        var watch = Stopwatch.StartNew();
        var inputWatch = new Stopwatch();
        string? failure = null; long stallElapsed = -1; bool ownerSurvived = false;
        nint originalForeground = GetForegroundWindow(), receiverWindow = 0;
        using var lease = new UpdateLaunchLease();
        // The job also kills the two inherited children if the supervisor itself is killed.
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(94), null, 75000, Timeout.Infinite);
        using var inputDeadline = new System.Threading.Timer(_ => Environment.Exit(97), null, Timeout.Infinite, Timeout.Infinite);
        Write(run, "launch.json", new { Started = DateTimeOffset.UtcNow, Supervisor = Environment.ProcessId, Executable = executable,
            MaximumSeconds = 75, PreparationSeconds = 60, InputPhaseSeconds = 15,
            Input = "Three bounded OS SendInput F6 down/up pairs to exact owned foreground", OriginalForeground = (long)originalForeground });
        try
        {
            receiver = Start("--focus-external-helper", "--focus-helper-directory", run, "--focus-helper-watchdog");
            ReceiverIdentity? ready = null;
            Wait(() => (ready = ReadReceiver(run, receiver)) is not null, "owned receiver readiness", 2500);
            var identity = ready!;
            if (identity.Process != receiver.Id) throw new InvalidOperationException("Receiver PID mismatch.");
            receiverWindow = (nint)identity.Handle;
            Write(run, "receiver-identity.json", identity);
            // Only an actual GUI selection may activate this window. No foreground API,
            // activation message, fabricated foreground or input is used in preparation.
            Write(run, "prepared.json", new { Stage = "AwaitingGuiActivation", Receiver = receiver.Id, Handle = (long)receiverWindow,
                Title = "FloatCanvas Watchdog Receiver - click to start", InputSent = false, OwnerStarted = false,
                MaximumPreparationSeconds = 60, Instruction = "Select this exact owned window with GUI, then release all keys and mouse buttons. Input phase starts after 250ms stable foreground." });
            Console.WriteLine("WATCHDOG_PREPARED " + Path.Combine(run, "prepared.json"));
            long stableFrom = 0;
            Wait(() =>
            {
                RequireReceiverIdentity(identity);
                if (GetForegroundWindow() != receiverWindow || AnyKeysDown()) { stableFrom = 0; return false; }
                if (stableFrom == 0) stableFrom = Environment.TickCount64;
                return Environment.TickCount64 - stableFrom >= 250;
            }, "owned receiver GUI activation and released keys", Math.Max(1, 60000 - (int)watch.ElapsedMilliseconds));
            inputWatch.Start(); inputDeadline.Change(15000, Timeout.Infinite);
            Write(run, "input-phase.json", new { Started = DateTimeOffset.UtcNow, PreparationMilliseconds = watch.ElapsedMilliseconds, Foreground = (long)GetForegroundWindow(), InputPhaseDeadlineMilliseconds = 15000 });
            SendReceiverF6(identity); Wait(() => ReceiverCounts(run) == (1, 1), "baseline F6 down/up", 700);
            owner = Start("--system-overlay-watchdog-owner", run);
            Wait(() => ReadOwner(run)?.Stage == "ready", "production owner readiness", 2500);
            RequireReceiver(identity); RequireReleasedKeys();
            File.WriteAllText(Path.Combine(run, "enter.request"), "enter");
            Wait(() => ReadOwner(run) is { Stage: "delegated", HasInput: true }, "actual external input acknowledgement", 2000);
            var entered = ReadOwner(run)!;
            if (entered.Process != owner.Id || entered.Foreground != (long)receiverWindow) throw new InvalidOperationException("Delegation changed the expected external foreground.");
            SendReceiverF6(identity); Wait(() => ReadOwner(run)?.F6Down == 1, "F6 routed to production overlay", 700);
            if (ReceiverCounts(run) != (1, 1)) throw new InvalidOperationException("Delegated F6 leaked to the independent receiver.");
            File.WriteAllText(Path.Combine(run, "stall.request"), "stall-owner-STA");
            Wait(() => ReadOwner(run)?.Stage == "stalled", "owner STA stall checkpoint", 500);
            var stalled = ReadOwner(run)!;
            Wait(() => ReadOwner(run) is { Stage: "resumed", HasInput: false }, "owner survives slow operation and releases input", 7500);
            stallElapsed = Environment.TickCount64 - stalled.Tick;
            ownerSurvived = !owner.HasExited;
            if (!ownerSurvived || stallElapsed is < 6000 or > 7500) throw new InvalidOperationException("Owner failed to survive and finish the six-second operation.");
            // Do not reactivate or refocus: the owner's normal input release restores receiver routing.
            RequireReceiver(identity); SendReceiverF6(identity);
            Wait(() => ReceiverCounts(run) == (2, 2), "receiver F6 down/up restored after normal release", 800);
            string journal = Path.Combine(run, "profile", "overlay-input-safety.log");
            if (File.Exists(journal) && File.ReadAllText(journal).Contains("\"exitCode\":", StringComparison.Ordinal))
                throw new InvalidOperationException("A slow operation must not generate a native process-termination record.");
            return 0;
        }
        catch (Exception error) { failure = error.ToString(); return 1; }
        finally
        {
            // Never enumerate/kill by process name. Only Process handles created by this run are used.
            bool ownerStopped = Stop(owner), receiverStopped = Stop(receiver);
            if (!ownerStopped || !receiverStopped) Environment.Exit(96); // Preserve the kill-on-close job if exact child cleanup failed.
            if (GetForegroundWindow() == 0 || GetForegroundWindow() == receiverWindow)
                if (originalForeground != 0 && IsWindow(originalForeground)) SetForegroundWindow(originalForeground);
            Write(run, "result.json", new { Passed = failure is null, Failure = failure, ElapsedMilliseconds = watch.ElapsedMilliseconds,
                InputPhaseStarted = inputWatch.IsRunning, InputPhaseMilliseconds = inputWatch.ElapsedMilliseconds,
                OwnerSurvived = ownerSurvived, StallMilliseconds = stallElapsed, ReceiverCounts = new { ReceiverCounts(run).Down, ReceiverCounts(run).Up },
                InputProof = "Actual Windows SendInput queue routing, not hardware/RawInput/DirectInput/game acceptance", Profile = Path.Combine(run, "profile") });
            Console.WriteLine(Path.Combine(run, "result.json"));
        }

        Process Start(params string[] arguments)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            return Process.Start(start) ?? throw new InvalidOperationException("Could not create own fixture child.");
        }
        void Wait(Func<bool> done, string operation, int milliseconds)
        {
            long end = Environment.TickCount64 + milliseconds;
            while (!done())
            {
                if (watch.ElapsedMilliseconds >= 74000 || (inputWatch.IsRunning && inputWatch.ElapsedMilliseconds >= 14000) || Environment.TickCount64 > end) throw new TimeoutException(operation);
                if (receiver is { HasExited: true }) throw new InvalidOperationException("Owned receiver exited during " + operation);
                if (owner is { HasExited: true }) throw new InvalidOperationException("Owned overlay exited during " + operation);
                Thread.Sleep(10);
            }
        }
    }

    internal static int Owner(string run)
    {
        run = Path.GetFullPath(run);
        var identity = JsonSerializer.Deserialize<ReceiverIdentity>(File.ReadAllText(Path.Combine(run, "receiver-identity.json")))!;
        using var safety = new System.Threading.Timer(_ => Environment.Exit(95), null, 14000, Timeout.Infinite);
        ApplicationPaths.Configure(["--profile", Path.Combine(run, "profile")]);
        new UserSettings { Toggle = new(7, 0x7A), Hide = new(7, 0x79), ShowFloatingLogo = false, Language = "zh-CN" }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var host = new OverlayHost(OverlayMonitorScope.PrimaryScreen);
        host.EnableSystemInput(Path.Combine(ApplicationPaths.ProfileDirectory, "overlay-input-safety.log"));
        using var session = new DesktopSession(app, host);
        session.Start(["--startup", "--profile", ApplicationPaths.ProfileDirectory]);
        int received = 0; bool entered = false, acknowledged = false, stalledOnce = false;
        void State(string stage) => Write(run, "owner-state.json", new OwnerState(Environment.ProcessId, stage, host.HasEditingInput, received, (long)GetForegroundWindow(), Environment.TickCount64));
        host.KeyInput += key => { if (key.VirtualKey == 0x75) { received++; State("delegated"); } };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (!entered && File.Exists(Path.Combine(run, "enter.request")))
            {
                RequireReceiver(identity); RequireReleasedKeys(); entered = true;
                if (!host.TryEnterEditing(out var error)) throw new InvalidOperationException(error);
            }
            if (entered && host.HasEditingInput && !acknowledged) { acknowledged = true; State("delegated"); }
            if (!stalledOnce && File.Exists(Path.Combine(run, "stall.request")))
            {
                RequireReceiver(identity);
                if (!host.HasEditingInput || received != 1) throw new InvalidOperationException("Refusing to stall without acknowledged real input.");
                stalledOnce = true;
                State("stalled"); Thread.Sleep(6000); // A finite slow operation on the actual native HWND owner's STA.
                host.ExitEditing();
                State("resumed");
            }
        };
        State("ready"); timer.Start();
        try { return app.Run(); } finally { timer.Stop(); }
    }

    private static void RequireReceiver(ReceiverIdentity identity)
    {
        RequireReceiverIdentity(identity);
        if (GetForegroundWindow() != (nint)identity.Handle) throw new InvalidOperationException("Input gate requires the exact independently owned receiver foreground.");
    }
    private static void RequireReceiverIdentity(ReceiverIdentity identity)
    {
        using var receiver = Process.GetProcessById(identity.Process);
        nint window = (nint)identity.Handle;
        if (receiver.HasExited || receiver.StartTime.ToUniversalTime().Ticks != identity.StartUtcTicks ||
            !string.Equals(receiver.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
            GetWindowThreadProcessId(window, out uint pid) == 0 || pid != identity.Process)
            throw new InvalidOperationException("Input gate requires the exact independently owned receiver foreground.");
    }
    private static void RequireReleasedKeys()
    { if (AnyKeysDown()) throw new InvalidOperationException("A physical key or mouse button is held; no input is sent."); }
    private static bool AnyKeysDown()
    { for (int key = 1; key < 256; key++) if (GetAsyncKeyState(key) < 0) return true; return false; }
    private static void SendReceiverF6(ReceiverIdentity identity)
    {
        RequireReceiver(identity); RequireReleasedKeys();
        NativeInput[] inputs = [new() { Type = 1, Data = new() { Keyboard = new() { Key = 0x75 } } }, new() { Type = 1, Data = new() { Keyboard = new() { Key = 0x75, Flags = 2 } } }];
        uint sent = SendInput(2, inputs, Marshal.SizeOf<NativeInput>());
        if (sent == 1) SendInput(1, [inputs[1]], Marshal.SizeOf<NativeInput>()); // Release only our own admitted key-down tail.
        if (sent != 2) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    private static OwnerState? ReadOwner(string run)
    { try { using var stream = ReadShared(Path.Combine(run, "owner-state.json")); return JsonSerializer.Deserialize<OwnerState>(stream); } catch (IOException) { return null; } }
    private static ReceiverIdentity? ReadReceiver(string run, Process receiver)
    {
        try { using var stream = ReadShared(Path.Combine(run, "external-ready.json")); using var data = JsonDocument.Parse(stream); return new(data.RootElement.GetProperty("Process").GetInt32(), receiver.StartTime.ToUniversalTime().Ticks, data.RootElement.GetProperty("Handle").GetInt64()); }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }
    private static (int Down, int Up) ReceiverCounts(string run)
    {
        try { using var stream = ReadShared(Path.Combine(run, "external-input.json")); using var data = JsonDocument.Parse(stream); return (data.RootElement.GetProperty("F6Down").GetInt32(), data.RootElement.GetProperty("F6Up").GetInt32()); }
        catch (IOException) { return (0, 0); }
    }
    private static bool Stop(Process? child)
    {
        if (child is null) return true;
        try { if (!child.HasExited) child.Kill(); return child.WaitForExit(500); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        finally { child.Dispose(); }
    }
    private static void Write(string run, string name, object value)
    { string path = Path.Combine(run, name); File.WriteAllText(path + ".pending", JsonSerializer.Serialize(value, Json)); File.Move(path + ".pending", path, true); }
    private static FileStream ReadShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { internal uint Type; internal InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData { [FieldOffset(0)] internal KeyboardInput Keyboard; [FieldOffset(0)] internal MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { internal ushort Key, Scan; internal uint Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
