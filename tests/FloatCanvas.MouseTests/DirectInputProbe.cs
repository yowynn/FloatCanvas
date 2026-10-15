using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;

namespace FloatCanvas.MouseTests;

internal static class DirectInputProbe
{
    private static readonly string[] Channels = ["Move", "LeftDown", "LeftUp", "RightDown", "RightUp", "MiddleDown", "MiddleUp", "X1Down", "X1Up", "X2Down", "X2Up", "Wheel", "HWheel"];
    private sealed record Check(string Name, bool Passed, JsonElement Actual);
    private sealed record StimulusRecord(long Timestamp, string Kind, PixelPoint? RequestedPoint, PixelPoint ActualCursor);

    public static async Task<int> RunAsync(string evidencePath)
    {
        var checks = new List<Check>();
        var evidence = new Dictionary<string, object?>
        {
            ["StartedUtc"] = DateTimeOffset.UtcNow, ["ProcessId"] = Environment.ProcessId,
            ["OS"] = Environment.OSVersion.ToString(), ["Runtime"] = Environment.Version.ToString(),
            ["TestOnly"] = true, ["FullIsolation"] = false, ["ProductionMouseIsolationImplemented"] = false, ["LowLevelHookInstalled"] = false,
            ["ActivationWaitSeconds"] = Program.ActivationWaitSeconds, ["ExclusiveWatchdogMilliseconds"] = 4000,
            ["EmergencySelfTerminationDeadlineMilliseconds"] = 4800,
            ["StimulusTag"] = Native.TagValue, ["InteropLayout"] = Native.InteropLayoutEvidence(),
            ["DirectInputInteropLayout"] = DirectInputNative.LayoutEvidence(),
            ["Scope"] = "Three own processes: foreground SysMouse DirectInput target; separate Raw INPUTSINK observer; separate background nonexclusive DirectInput observer. No LL hook or SendInput relay.",
            ["Limitations"] = new[] { "Synthetic absolute SendInput only; no real HID/relative/edge/acceleration equivalence established.",
                "DIMOUSESTATE2-compatible layout has X/Y/Z and eight button slots; no separate standard horizontal wheel axis. Raw/WM independently check horizontal wheel.",
                "No WPF control/IME/game/production route acceptance. A successful Acquire or zero inactive observer is not isolation evidence.",
                "Independent 4-second timer requests release on the dedicated device thread; release must measure within 5 seconds. A still-stuck exclusive native call triggers emergency termination of this test process at 4.8 seconds, so that abnormal case may have no final JSON." }
        };
        PixelPoint savedCursor = Native.Cursor(); nint savedForeground = Native.GetForegroundWindow();
        ProbeWindow? target = null; DirectInputWorker? device = null;
        ChildFixture? raw = null, direct = null;
        var stimuli = new GuardedStimulus();
        try
        {
            Assert(checks, "EntryButtonsReleased", Native.ButtonKeys.All(k => (Native.GetAsyncKeyState(k) & 0x8000) == 0), "All five mouse buttons released."); Require(checks[^1]);
            target = new ProbeWindow("浮绘：DirectInput 独占实验目标", "DirectInput experiment target\nActivate THIS window for the handshake. Esc or Close cancels.\nNo low-level hook is installed. Exclusive mouse leases expire after four seconds.");
            Console.WriteLine($"ACTIVATION HANDSHAKE: own target HWND={target.Handle} PID={Environment.ProcessId}; activate this window, then release every mouse button. Waiting up to {Program.ActivationWaitSeconds}s.");
            Native.GetWindowRect(target.Handle, out var rect);
            stimuli.AddOwn(target.Handle, Environment.ProcessId);
            evidence["TargetWindow"] = target.Handle.ToInt64(); evidence["Dpi"] = Native.GetDpiForWindow(target.Handle);
            raw = await ChildFixture.StartAsync("--observer", rect);
            direct = await ChildFixture.StartAsync("--directinput-observer", rect);
            ObserverSnapshot rawInfo = await raw.Peer.SendAsync("info"), directInfo = await direct.Peer.SendAsync("info");
            evidence["RawObserverInfo"] = rawInfo; evidence["DirectInputObserverInfo"] = directInfo;
            Assert(checks, "ThreeIndependentOwnProcesses", raw.Process.Id != direct.Process.Id && raw.Process.Id != Environment.ProcessId && direct.Process.Id != Environment.ProcessId &&
                rawInfo.ProcessId == raw.Process.Id && directInfo.ProcessId == direct.Process.Id && rawInfo.RawInputRegistered && !directInfo.RawInputRegistered &&
                directInfo.Role == "DirectInputBackgroundNonexclusive" && directInfo.DirectInput is not null,
                new { Parent = Environment.ProcessId, Raw = rawInfo.ProcessId, DirectInput = directInfo.ProcessId, rawInfo.RawInputRegistered, DirectInputRole = directInfo.Role }); Require(checks[^1]);
            await AwaitOwnActivationAsync(target.Handle);
            stimuli.PlaceCursorInside(target.Handle); await Task.Delay(120, Program.Cancellation);
            Assert(checks, "ActivationHandshakeAccepted", Native.GetForegroundWindow() == target.Handle && Native.ButtonKeys.All(k => (Native.GetAsyncKeyState(k) & 0x8000) == 0), target.Handle.ToInt64());
            device = await DirectInputWorker.CreateAsync(target.Handle, [(uint)raw.Process.Id, (uint)direct.Process.Id]);
            Assert(checks, "MainDeviceCapabilitiesExposeRequiredAxesFiveButtons", HasRequiredCapabilities(device.Snapshot()), device.Snapshot().Calls.Where(c => c.Method == "GetCapabilities").ToArray());
            Assert(checks, "ObserverCapabilitiesExposeRequiredAxesFiveButtons", directInfo.DirectInput is not null && HasRequiredCapabilities(directInfo.DirectInput), directInfo.DirectInput?.Calls.Where(c => c.Method == "GetCapabilities").ToArray());
            target.Message += (id, key, _) => { if (id is 0x10 or 0x211 or 0x231 || (id == 0x100 && key == 27)) device.RequestStop(); };
            await direct.Peer.SendAsync("start");
            await PositiveAsync("Before", target, device, raw.Peer, direct.Peer, stimuli, checks, evidence);

            await device.StartAsync(5, "exclusive"); // FOREGROUND | EXCLUSIVE, never BACKGROUND | EXCLUSIVE
            await Task.Delay(100, Program.Cancellation);
            await device.ResetAsync("exclusive");
            await raw.Peer.SendAsync("reset:exclusive"); await direct.Peer.SendAsync("reset:exclusive"); target.Reset();
            PixelPoint exclusiveAnchor = Native.Cursor();
            var exclusiveStimuli = await ExerciseAsync(target.Handle, stimuli, Program.Cancellation, () =>
            {
                var lease = device.Snapshot();
                return lease.Acquired && lease.LeaseMilliseconds < 3000;
            });
            await Task.Delay(120, Program.Cancellation);
            await device.EndCaptureAsync();
            var exclusiveMain = device.Snapshot();
            var exclusiveRaw = await raw.Peer.SendAsync("snapshot"); var exclusiveDirect = await direct.Peer.SendAsync("snapshot");
            var exclusiveOrdinary = new Dictionary<string, int>(target.OrdinaryMessages);
            evidence["Exclusive"] = new { Main = exclusiveMain, Raw = exclusiveRaw, BackgroundDirectInput = exclusiveDirect.DirectInput,
                Stimuli = exclusiveStimuli, TargetOrdinaryMessages = exclusiveOrdinary, TargetOrdinaryDetails = target.OrdinaryMessageDetails.ToArray(),
                SystemCursorAnchor = exclusiveAnchor, SystemCursorStayedAtAnchor = exclusiveStimuli.All(s => s.ActualCursor == exclusiveAnchor) };
            Assert(checks, "ExclusiveMainAcquiredAndReceivedXyWheelFiveButtons", RequiredChannelsExercised(exclusiveMain) && exclusiveMain.Acquired && exclusiveMain.CooperativeFlags == 5,
                Summary(exclusiveMain));
            Assert(checks, "ExclusiveRawSuppressed", exclusiveRaw.RawPackets == 0 && exclusiveRaw.RawMotionPackets == 0 && exclusiveRaw.RawButtons.Count == 0,
                new { exclusiveRaw.RawPackets, exclusiveRaw.RawMotionPackets, exclusiveRaw.RawButtons, exclusiveRaw.RawMessages });
            Assert(checks, "ExclusiveAsyncButtonsSuppressed", exclusiveRaw.Samples > 20 && Native.ButtonKeys.All(k => exclusiveRaw.ButtonHighSamples.GetValueOrDefault(k) == 0), exclusiveRaw.ButtonHighSamples);
            Assert(checks, "ExclusiveOrdinaryButtonsAndWheelsSuppressed", Channels.Where(c => c != "Move").All(c => exclusiveOrdinary.GetValueOrDefault(c) == 0), exclusiveOrdinary);
            var other = exclusiveDirect.DirectInput ?? throw new InvalidDataException("Missing independent DirectInput snapshot.");
            Assert(checks, "ExclusiveBackgroundNonexclusiveNoData", other.XChanges == 0 && other.YChanges == 0 && other.PositiveWheel == 0 && other.NegativeWheel == 0 && other.ButtonDown.Sum() == 0 && other.ButtonUp.Sum() == 0,
                Summary(other));
            Assert(checks, "ExclusiveAllObserversOwnForegroundOnly", exclusiveRaw.ForeignForegroundSamples == 0 && other.ForeignForegroundSamples == 0 && exclusiveMain.ForeignForegroundSamples == 0,
                new { RawForeign = exclusiveRaw.ForeignForegroundSamples, DirectInputForeign = other.ForeignForegroundSamples, MainForeign = exclusiveMain.ForeignForegroundSamples });
            await device.StopAsync("ExclusiveGroupComplete");
            DirectInputSnapshot released = device.Snapshot(); evidence["ExclusiveRelease"] = released;
            Assert(checks, "ExclusiveReleaseWithinFiveSeconds", !released.Acquired && released.LeaseMilliseconds <= 5000 && released.Failure is null, Summary(released));
            // Reacquire the background observer once explicitly for the recovery positive control; never loop on input-lost.
            await direct.Peer.SendAsync("start");
            await PositiveAsync("After", target, device, raw.Peer, direct.Peer, stimuli, checks, evidence);

            // No input in this group: prove release is driven by a separate timer/device thread rather than UI stimulus completion.
            await device.StartAsync(5, "watchdog-no-input");
            await Task.Delay(4250, Program.Cancellation);
            DirectInputSnapshot watchdog = device.Snapshot(); evidence["WatchdogNoInput"] = watchdog;
            Assert(checks, "IndependentWatchdogReleasedWithinFiveSeconds", watchdog.WatchdogFired && !watchdog.Acquired && watchdog.ReleaseReason == "IndependentWatchdog" && watchdog.LeaseMilliseconds <= 5000, Summary(watchdog));
            Assert(checks, "OwnNativeCallbacksHealthy", target.CallbackFailure is null, target.CallbackFailure?.ToString());
        }
        catch (Exception error)
        {
            if (error is ProbeInitializationException initialization) evidence["InitializationDiagnostic"] = initialization.Diagnostic;
            evidence["Failure"] = error.ToString(); Assert(checks, "RunnerCompleted", false, error.Message);
        }
        finally
        {
            if (device is not null)
            {
                try { await device.StopAsync("RunnerFinally"); } catch (Exception error) { evidence["DeviceStopError"] = error.ToString(); }
                device.Dispose(); DirectInputSnapshot stopped = device.Snapshot(); evidence["DeviceFinal"] = stopped;
                Assert(checks, "MainDeviceThreadExitedAndReleased", !stopped.ThreadAlive && !stopped.Acquired && stopped.Failure is null, Summary(stopped));
            }
            try { await stimuli.CleanupHeldAsync(); } catch (Exception error) { Assert(checks, "GuardedStimulusCleanup", false, error.ToString()); }
            Assert(checks, "NoRemainingSyntheticHeldButtons", stimuli.HeldCount == 0, stimuli.HeldCount);
            evidence["StimulusGuardCount"] = stimuli.GuardCount;
            foreach ((string name, ChildFixture? child) in new[] { ("RawObserver", raw), ("DirectInputObserver", direct) })
            {
                if (child is null) continue;
                evidence[name + "LastSnapshot"] = child.Peer.LastSnapshot;
                bool normal = await child.CloseAsync();
                evidence[name + "FinalSnapshot"] = child.Peer.LastSnapshot;
                Assert(checks, name + "NormalExit", normal, child.ExitEvidence);
            }
            target?.Dispose();
            evidence["CursorRestored"] = Native.SetCursorPos(savedCursor.X, savedCursor.Y) && Native.Cursor() == savedCursor;
            if (savedForeground != 0 && Native.IsWindow(savedForeground)) Native.SetForegroundWindow(savedForeground);
            evidence["ForegroundRestoreAccepted"] = Native.GetForegroundWindow() == savedForeground;
            evidence["FinishedUtc"] = DateTimeOffset.UtcNow; evidence["Checks"] = checks;
            evidence["Passed"] = checks.Count(c => c.Passed); evidence["Failed"] = checks.Count(c => !c.Passed); evidence["AllChecksPassed"] = checks.Count > 0 && checks.All(c => c.Passed);
            Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"DirectInput probe {checks.Count(c => c.Passed)}/{checks.Count}: {evidencePath}");
        }
        return checks.Count > 0 && checks.All(c => c.Passed) ? 0 : 1;
    }

    private static async Task PositiveAsync(string phase, ProbeWindow target, DirectInputWorker main, ObserverPeer raw, ObserverPeer direct,
        GuardedStimulus stimulus, List<Check> checks, Dictionary<string, object?> evidence)
    {
        EnsureOwnForeground(target.Handle);
        stimulus.PlaceCursorInside(target.Handle); await Task.Delay(100, Program.Cancellation);
        await main.StartAsync(6, phase); // FOREGROUND | NONEXCLUSIVE
        await main.ResetAsync(phase); await raw.SendAsync("reset:" + phase); await direct.SendAsync("reset:" + phase); target.Reset();
        var events = await ExerciseAsync(target.Handle, stimulus, Program.Cancellation); await Task.Delay(120, Program.Cancellation);
        await main.EndCaptureAsync(); var primary = main.Snapshot(); var rawData = await raw.SendAsync("snapshot"); var directData = await direct.SendAsync("snapshot");
        var native = new Dictionary<string, int>(target.OrdinaryMessages);
        evidence[phase] = new { Main = primary, Raw = rawData, BackgroundDirectInput = directData.DirectInput, Stimuli = events, TargetOrdinaryMessages = native };
        Assert(checks, phase + "RawAllAxesButtonsPositive", rawData.RawPackets > 0 && rawData.RawMotionPackets > 0 && Channels.Where(c => c != "Move").All(c => rawData.RawButtons.GetValueOrDefault(c) > 0),
            new { rawData.RawPackets, rawData.RawMotionPackets, rawData.RawButtons });
        RawMouseMessage[] rawMoves = rawData.RawMessages.Where(m => m.LastX != 0 || m.LastY != 0).ToArray();
        bool rawDirections = rawMoves.Select(m => m.LastX).Distinct().Count() >= 2 && rawMoves.Select(m => m.LastY).Distinct().Count() >= 2 &&
            new ushort[] { 0x400, 0x800 }.All(flag => rawData.RawMessages.Any(m => (m.ButtonFlags & flag) != 0 && m.ButtonData > 0) && rawData.RawMessages.Any(m => (m.ButtonFlags & flag) != 0 && m.ButtonData < 0));
        Assert(checks, phase + "RawXyAndBothWheelDirectionsPositive", rawDirections,
            rawData.RawMessages.Select(m => new { m.MotionFlags, m.LastX, m.LastY, m.ButtonFlags, m.ButtonData }).ToArray());
        Assert(checks, phase + "RawStimulusMarkersVerified", rawData.RawMessages.Length > 0 && rawData.RawMessages.All(m => m.Low32MatchesStimulusTag), rawData.RawMessages.Select(m => m.ExtraHex).ToArray());
        Assert(checks, phase + "AsyncAllFiveButtonsPositive", Native.ButtonKeys.All(k => rawData.ButtonHighSamples.GetValueOrDefault(k) > 0), rawData.ButtonHighSamples);
        Assert(checks, phase + "TargetOrdinaryAllAxesButtonsPositive", Channels.All(c => native.GetValueOrDefault(c) > 0), native);
        Assert(checks, phase + "MainDirectInputXyWheelFiveButtonsPositive", RequiredChannelsExercised(primary), Summary(primary));
        Assert(checks, phase + "BackgroundDirectInputXyWheelFiveButtonsPositive", directData.DirectInput is not null && RequiredChannelsExercised(directData.DirectInput), directData.DirectInput is null ? null : Summary(directData.DirectInput));
        Assert(checks, phase + "ObserversHealthyOwnForeground", primary.Failure is null && rawData.Failure is null && directData.DirectInput?.Failure is null && primary.ForeignForegroundSamples == 0 && rawData.ForeignForegroundSamples == 0 && directData.DirectInput?.ForeignForegroundSamples == 0,
            new { Main = primary.Failure, Raw = rawData.Failure, DirectInput = directData.DirectInput?.Failure });
        await main.StopAsync(phase + "Complete");
    }
    private static bool RequiredChannelsExercised(DirectInputSnapshot value) => value.Samples > 5 && value.XChanges > 0 && value.YChanges > 0 && value.PositiveWheel > 0 && value.NegativeWheel > 0 &&
        value.ButtonDown.Take(5).All(c => c > 0) && value.ButtonUp.Take(5).All(c => c > 0);
    private static bool HasRequiredCapabilities(DirectInputSnapshot value)
    {
        var call = value.Calls.LastOrDefault(c => c.Method == "GetCapabilities");
        if (call?.Detail is null || call.HResult < 0) return false;
        JsonElement caps = JsonSerializer.SerializeToElement(call.Detail);
        return caps.GetProperty("Axes").GetUInt32() >= 3 && caps.GetProperty("Buttons").GetUInt32() >= 5;
    }
    private static object Summary(DirectInputSnapshot value) => new { value.Phase, value.Acquired, value.CooperativeFlags, value.WatchdogFired, value.LeaseMilliseconds,
        value.ReleaseReason, value.ThreadAlive, value.Samples, value.XChanges, value.YChanges, value.PositiveWheel, value.NegativeWheel, value.ButtonDown, value.ButtonUp, value.ForeignForegroundSamples, value.Failure };
    private static async Task<List<StimulusRecord>> ExerciseAsync(nint window, GuardedStimulus stimulus, CancellationToken cancellation, Func<bool>? canStartStimulus = null)
    {
        var result = new List<StimulusRecord>(); var rect = Native.ClientBounds(window);
        for (int i = 0; i < 6; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (canStartStimulus is not null && !canStartStimulus()) throw new OperationCanceledException("Exclusive lease is stopping/near its deadline; no new stimulus.");
            var point = new PixelPoint(rect.Left + 160 + i * 23, rect.Top + 165 + i * 11);
            stimulus.Move(window, point); result.Add(new(Stopwatch.GetTimestamp(), "Move", point, Native.Cursor())); await Task.Delay(35, cancellation);
        }
        foreach (string button in new[] { "Left", "Right", "Middle", "X1", "X2" })
        {
            cancellation.ThrowIfCancellationRequested();
            if (canStartStimulus is not null && !canStartStimulus()) throw new OperationCanceledException("Exclusive lease is stopping/near its deadline; no new Down.");
            stimulus.Down(window, button); result.Add(new(Stopwatch.GetTimestamp(), button + "Down", null, Native.Cursor()));
            await Task.Delay(75, cancellation); stimulus.UpIfHeld(window, button); result.Add(new(Stopwatch.GetTimestamp(), button + "Up", null, Native.Cursor())); await Task.Delay(35, cancellation);
        }
        foreach (uint flags in new uint[] { 0x800, 0x1000 }) foreach (int delta in new[] { 120, -120 })
        {
            cancellation.ThrowIfCancellationRequested();
            if (canStartStimulus is not null && !canStartStimulus()) throw new OperationCanceledException("Exclusive lease is stopping/near its deadline; no new wheel stimulus.");
            stimulus.Wheel(window, flags, delta); result.Add(new(Stopwatch.GetTimestamp(), (flags == 0x800 ? "Wheel" : "HWheel") + delta, null, Native.Cursor())); await Task.Delay(40, cancellation);
        }
        return result;
    }
    private static void EnsureOwnForeground(nint window)
    {
        if (!Native.IsWindow(window) || !Native.IsWindowVisible(window) || Native.Pid(window) != Environment.ProcessId || Native.GetForegroundWindow() != window)
            throw new InvalidOperationException("The exact own target lost foreground/visibility; no reacquisition attempt or foreign stimulus is permitted.");
    }
    private static async Task AwaitOwnActivationAsync(nint window)
    {
        // One activation request only. The operator can explicitly activate this own HWND during the handshake.
        Native.SetWindowPos(window, 0, 0, 0, 0, 0, 0x13); Native.SetForegroundWindow(window);
        var deadline = Stopwatch.StartNew(); long? readySince = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(Program.ActivationWaitSeconds))
        {
            Program.Cancellation.ThrowIfCancellationRequested();
            if (!Native.IsWindow(window) || !Native.IsWindowVisible(window) || ProbeWindow.IsCancellationRequested(window)) throw new OperationCanceledException("Own activation target was closed/cancelled.");
            bool ready = Native.GetForegroundWindow() == window && Native.ButtonKeys.All(k => (Native.GetAsyncKeyState(k) & 0x8000) == 0);
            if (!ready) readySince = null;
            else
            {
                readySince ??= Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(readySince.Value).TotalMilliseconds >= 250) return;
            }
            await Task.Delay(50, Program.Cancellation);
        }
        throw new TimeoutException("Own target activation handshake timed out before any test stimulus.");
    }
    private static void Assert(List<Check> checks, string name, bool passed, object? actual)
    { checks.Add(new(name, passed, JsonSerializer.SerializeToElement(actual))); Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}"); }
    private static void Require(Check check) { if (!check.Passed) throw new InvalidOperationException(check.Name); }

    private sealed class ChildFixture
    {
        public Process Process { get; }
        public ObserverPeer Peer { get; }
        public object? ExitEvidence { get; private set; }
        private ChildFixture(Process process, ObserverPeer peer) { Process = process; Peer = peer; }
        public static async Task<ChildFixture> StartAsync(string role, Native.Rect bounds)
        {
            string pipeName = $"FloatCanvas.DirectInputProbe.{Environment.ProcessId}.{Guid.NewGuid():N}";
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Process? child = null; ObserverPeer? peer = null;
            try
            {
                string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Program.RepositoryRoot() };
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                foreach (string arg in new[] { role, "--pipe", pipeName, "--parent-pid", Environment.ProcessId.ToString(), "--x", bounds.Left.ToString(), "--y", bounds.Top.ToString(),
                    "--width", (bounds.Right - bounds.Left).ToString(), "--height", (bounds.Bottom - bounds.Top).ToString(), "--no-activate" }) start.ArgumentList.Add(arg);
                child = Process.Start(start) ?? throw new InvalidOperationException("Own observer child could not start.");
                await pipe.WaitForConnectionAsync(Program.Cancellation).WaitAsync(TimeSpan.FromSeconds(12));
                if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != child.Id) throw new InvalidOperationException("Own child named-pipe PID mismatch.");
                peer = new ObserverPeer(pipe); ObserverSnapshot info = await peer.SendAsync("info");
                if (info.ProcessId != child.Id || !Native.IsWindow((nint)info.WindowHandle) || Native.Pid((nint)info.WindowHandle) != child.Id) throw new InvalidOperationException("Observer HWND/process identity mismatch.");
                return new ChildFixture(child, peer);
            }
            catch (Exception error)
            {
                ObserverSnapshot? diagnostic = peer?.LastSnapshot;
                pipe.Dispose();
                if (child is not null) { if (!child.HasExited) child.Kill(); await child.WaitForExitAsync(); child.Dispose(); }
                if (diagnostic is not null) throw new ProbeInitializationException("Own observer startup failed; native HRESULTs are retained.", diagnostic, error);
                throw;
            }
        }
        public async Task<bool> CloseAsync()
        {
            string? error = null; bool forced = false;
            try { await Peer.SendAsync("stop"); } catch (Exception failure) { error = failure.ToString(); }
            Peer.Dispose();
            try { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
            catch (TimeoutException) { forced = true; Process.Kill(); await Process.WaitForExitAsync(); }
            int exit = Process.ExitCode;
            ExitEvidence = new { ExitCode = exit, ForcedOnlyOwnChild = forced, Error = error }; Process.Dispose();
            return !forced && exit == 0 && error is null;
        }
    }
}
