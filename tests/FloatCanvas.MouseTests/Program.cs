using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace FloatCanvas.MouseTests;

internal static class Program
{
    internal static int ActivationWaitSeconds { get; private set; } = 60;
    internal static CancellationToken Cancellation => CancellationSource.Token;
    private static readonly CancellationTokenSource CancellationSource = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] InputNames = ["Move", "LeftDown", "LeftUp", "RightDown", "RightUp", "MiddleDown", "MiddleUp", "X1Down", "X1Up", "X2Down", "X2Up", "Wheel", "HWheel"];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--check-format-layout"))
        {
            try { return SdkFormatCheck.Run(Value(args, "--check-format-layout")); }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (!args.Contains("--run-probe") && !args.Contains("--directinput-probe") && !args.Contains("--observer") && !args.Contains("--directinput-observer"))
        {
            Console.WriteLine("No windows, hooks or input were created. Explicit opt-in: --run-probe or --directinput-probe [--wait-for-activation 60..300] [--evidence path]");
            return 0;
        }
        if (new[] { "--run-probe", "--directinput-probe", "--observer", "--directinput-observer" }.Count(args.Contains) != 1) return 2;
        if (OptionalValue(args, "--wait-for-activation") is string wait && (!int.TryParse(wait, out int seconds) || seconds is < 60 or > 300))
        { Console.Error.WriteLine("--wait-for-activation requires 60..300 seconds."); return 2; }
        if (OptionalValue(args, "--wait-for-activation") is string duration) ActivationWaitSeconds = int.Parse(duration);
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; CancellationSource.Cancel(); };
        int exitCode = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                if (args.Contains("--observer"))
                {
                    string pipe = Value(args, "--pipe"); int parent = int.Parse(Value(args, "--parent-pid"));
                    await Observer.RunChildAsync(pipe, parent, int.Parse(Value(args, "--x")), int.Parse(Value(args, "--y")),
                        int.Parse(Value(args, "--width")), int.Parse(Value(args, "--height")), app.Dispatcher, !args.Contains("--no-activate"));
                    exitCode = 0;
                }
                else if (args.Contains("--directinput-observer"))
                {
                    await DirectInputObserver.RunChildAsync(Value(args, "--pipe"), int.Parse(Value(args, "--parent-pid")),
                        int.Parse(Value(args, "--x")), int.Parse(Value(args, "--y")), int.Parse(Value(args, "--width")), int.Parse(Value(args, "--height")));
                    exitCode = 0;
                }
                else if (args.Contains("--directinput-probe"))
                {
                    string evidence = OptionalValue(args, "--evidence") ?? Path.Combine(RepositoryRoot(), "artifacts", "e2e", "directinput-isolation-probe.json");
                    exitCode = await DirectInputProbe.RunAsync(Path.GetFullPath(evidence));
                }
                else
                {
                    string evidence = OptionalValue(args, "--evidence") ?? Path.Combine(RepositoryRoot(), "artifacts", "e2e", "mouse-isolation-probe.json");
                    exitCode = await RunProbeAsync(Path.GetFullPath(evidence));
                }
            }
            catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
            finally { app.Shutdown(exitCode); }
        };
        app.Run(); return exitCode;
    }

    private static async Task<int> RunProbeAsync(string evidencePath)
    {
        var checks = new List<Check>();
        var data = new Dictionary<string, object?>
        {
            ["StartedUtc"] = DateTimeOffset.UtcNow, ["ProcessId"] = Environment.ProcessId,
            ["OS"] = Environment.OSVersion.ToString(), ["Runtime"] = Environment.Version.ToString(),
            ["InteropLayout"] = Native.InteropLayoutEvidence(),
            ["TestOnly"] = true, ["ProductionMouseIsolationImplemented"] = false, ["FullIsolation"] = false,
            ["Scope"] = "Exact own foreground HWND; guarded synthetic absolute SendInput stimuli. Private relay uses an in-process queue, never SendInput.",
            ["Limitations"] = new[] { "No real HID/relative-motion/edge/acceleration or multi-device equivalence established.",
                "No WPF controls, non-client drag, IME candidates, game or production OverlayHost isolation validation.",
                "Raw and async isolation results require independent-process positive controls for every exercised button/axis.",
                "System-cursor freeze and private point delivery are measured separately. This is not a general virtual mouse implementation." }
        };
        PixelPoint savedCursor = Native.Cursor(); nint savedForeground = Native.GetForegroundWindow();
        ProbeWindow? target = null; Process? child = null; ObserverPeer? peer = null;
        MouseSuppressionScope? scope = null;
        var stimulus = new GuardedStimulus();
        var privatePackets = new List<MousePacket>();
        try
        {
            CheckThat(checks, "EntryAllButtonsReleased", Native.ButtonKeys.All(k => (Native.GetAsyncKeyState(k) & 0x8000) == 0), "All five mouse buttons must be released before any fixture input.");
            Require(checks[^1]);
            target = new ProbeWindow("浮绘：鼠标接管探针目标", "Test-only private mouse target\nOnly this native HWND can own the temporary hook lease.\nThe system cursor and private points are measured separately.");
            target.PrivatePacket += privatePackets.Add;
            stimulus.AddOwn(target.Handle, Environment.ProcessId);
            data["TargetWindow"] = target.Handle.ToInt64(); data["Dpi"] = Native.GetDpiForWindow(target.Handle);
            Native.GetWindowRect(target.Handle, out var outer);
            string pipeName = $"FloatCanvas.MouseProbe.{Environment.ProcessId}.{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                child = StartObserver(pipeName, outer);
                await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(12));
                if (!Native.GetNamedPipeClientProcessId(server.SafePipeHandle, out uint clientPid) || clientPid != child.Id)
                    throw new InvalidOperationException("The independent observer pipe client PID does not match the launched Process.");
                peer = new ObserverPeer(server); server = null!;
            }
            finally { server?.Dispose(); }
            ObserverSnapshot info = await peer.SendAsync("info");
            nint observerHandle = (nint)info.WindowHandle;
            CheckThat(checks, "IndependentObserverProcess", info.ProcessId == child.Id && info.ProcessId != Environment.ProcessId &&
                Native.IsWindow(observerHandle) && Native.Pid(observerHandle) == child.Id && info.RawInputRegistered,
                new { info.ProcessId, info.WindowHandle, info.RawInputRegistered, info.RawError }); Require(checks[^1]);
            stimulus.AddOwn(observerHandle, child.Id); data["ObserverInfo"] = info;
            Console.WriteLine($"Own target HWND={target.Handle}, observer HWND={observerHandle}. Only these two fixtures may receive guarded stimuli.");

            // A successful unfiltered baseline is mandatory; absent Raw/async positive controls never become a skip.
            ObserverSnapshot before = await PositiveControlAsync(peer, observerHandle, stimulus, "before");
            data["Before"] = before; CheckPositive(checks, before, "Before");

            await ActivateAsync(target.Handle, Environment.ProcessId, null);
            stimulus.PlaceCursorInside(target.Handle);
            await Task.Delay(100);
            bool rejectedHeldEntry = false;
            try
            {
                stimulus.Down(target.Handle, "Left"); await Task.Delay(65);
                try { using var unexpected = await MouseSuppressionScope.StartAsync(target.Handle); }
                catch (InvalidOperationException error) when (error.Message.Contains("Release all mouse buttons", StringComparison.Ordinal)) { rejectedHeldEntry = true; }
            }
            finally { stimulus.UpIfHeld(target.Handle, "Left"); }
            await Task.Delay(100);
            CheckThat(checks, "HeldButtonEntryRejected", rejectedHeldEntry && Native.ButtonKeys.All(k => (Native.GetAsyncKeyState(k) & 0x8000) == 0), rejectedHeldEntry);
            stimulus.PlaceCursorInside(target.Handle);
            await Task.Delay(100);
            // Exclude activation/SetCursorPos preparation messages; only the installed-scope phase is asserted below.
            target.Reset(); privatePackets.Clear();
            PixelPoint anchor = Native.Cursor();
            await peer.SendAsync("reset:suppressed");
            scope = await MouseSuppressionScope.StartAsync(target.Handle);
            var actualPositions = await ExerciseAsync(target.Handle, stimulus, scope, target);
            await Task.Delay(120); scope.DrainTo(target);
            ObserverSnapshot during = await peer.SendAsync("snapshot");
            data["During"] = during; data["SystemCursorAnchor"] = anchor; data["SystemCursorAfterMoves"] = actualPositions;
            MousePacket[] duringPackets = [.. privatePackets];
            var privateCounts = new Dictionary<string, int>(target.PrivateMessages);
            var ordinaryCounts = new Dictionary<string, int>(target.OrdinaryMessages);
            OrdinaryMouseMessage[] ordinaryDetails = [.. target.OrdinaryMessageDetails];
            data["PrivatePackets"] = duringPackets;
            data["PrivateMessageCounts"] = privateCounts;
            data["TargetOrdinaryMessageCounts"] = ordinaryCounts;
            data["TargetOrdinaryMessageDetails"] = ordinaryDetails;
            CheckThat(checks, "PrivateAllButtonsAxesAndMoveDelivered", InputNames.All(name => privateCounts.GetValueOrDefault(name) > 0), privateCounts);
            CheckThat(checks, "PrivateSequenceOrdered", privatePackets.Count > 0 && privatePackets.Select(p => p.Sequence).SequenceEqual(Enumerable.Range(1, privatePackets.Count).Select(i => (long)i)), privatePackets.Count);
            CheckThat(checks, "PrivateMovePointsChange", target.PrivatePoints.Distinct().Count() >= 5, target.PrivatePoints.ToArray());
            CheckThat(checks, "PrivateAllTaggedStimuli", duringPackets.Length > 0 && duringPackets.All(p => p.IsStimulus && (p.Flags & 1) != 0),
                duringPackets.Select(p => new { p.Kind, p.Flags, p.IsStimulus, p.ActualExtra, p.ExpectedTag, p.ActualExtraHex, p.ExpectedTagHex, p.Low32MatchesExpectedTag }).ToArray());
            CheckThat(checks, "PrivateButtonsBalanced", target.PrivateHeldButtons.Count == 0 && privatePackets.LastOrDefault()?.HeldButtons.Length == 0, target.PrivateHeldButtons.ToArray());
            CheckThat(checks, "ObserverRawSuppressed", during.RawPackets == 0 && during.RawButtons.Count == 0 && during.RawMotionPackets == 0, new { during.RawPackets, during.RawButtons, during.RawMotionPackets });
            CheckThat(checks, "ObserverAsyncButtonsSuppressed", Native.ButtonKeys.All(k => during.ButtonHighSamples.GetValueOrDefault(k) == 0) && during.Samples >= 20, during.ButtonHighSamples);
            CheckThat(checks, "ObserverOrdinaryMessagesSuppressed", during.OrdinaryMessages.Values.Sum() == 0, during.OrdinaryMessages);
            CheckThat(checks, "OwnTargetOrdinaryButtonWheelMessagesSuppressed", InputNames.Where(name => name != "Move").All(name => ordinaryCounts.GetValueOrDefault(name) == 0), ordinaryCounts);
            CheckThat(checks, "OwnTargetOrdinaryMetadataComplete", ordinaryCounts.Values.Sum() == ordinaryDetails.Length, new { Count = ordinaryCounts.Values.Sum(), Details = ordinaryDetails.Length });
            OrdinaryMouseMessage[] moves = ordinaryDetails.Where(m => m.Kind == "Move").ToArray();
            var suspiciousMoves = moves.Where(m => m.MessagePoint != anchor || m.ActualCursor != anchor || m.MatchesExpectedTag || m.Low32MatchesExpectedTag || (m.SourceAvailable && m.SourceOriginId == 2)).ToArray();
            // A stationary untagged WM_MOUSEMOVE can be synthesized by window-manager hit testing/painting.
            // Keep every message and its source; do not label all WM_MOVE as leaked physical motion, nor declare unknown origin benign.
            data["OwnTargetMoveAssessment"] = new { Total = moves.Length, TaggedOrDisplacedOrInjectedSource = suspiciousMoves,
                StationaryUntagged = moves.Except(suspiciousMoves).ToArray(),
                Interpretation = "Stationary untagged messages are not confirmed physical input leakage. Unavailable source remains unattributed; this does not override the independent Raw suppression assertion." };
            CheckThat(checks, "OwnTargetNoTaggedDisplacedOrInjectedSourceMoves", suspiciousMoves.Length == 0, suspiciousMoves);
            bool cursorFrozen = actualPositions.Count >= 5 && actualPositions.All(p => p == anchor) && during.CursorSamples.All(p => p == anchor);
            data["SystemCursorFrozen"] = cursorFrozen;
            CheckThat(checks, "SystemCursorFreezeMeasured", cursorFrozen, new { Anchor = anchor, Actual = actualPositions, during.CursorTransitions });
            CheckThat(checks, "ProbeStayedWithinOwnForegroundLease", !scope.ScopeLost && during.ForeignForegroundSamples == 0, new { scope.ScopeLost, during.ForeignForegroundSamples });
            scope.Dispose(); scope.DrainTo(target);
            CheckScope(checks, scope, "BalancedScope");
            data["BalancedScope"] = ScopeEvidence(scope); scope = null;

            // Dispose while a private-only Down is held: cancel local state, then send its known stimulus Up only to our own target.
            await Task.Delay(80);
            scope = await MouseSuppressionScope.StartAsync(target.Handle);
            stimulus.Down(target.Handle, "Left"); await Task.Delay(70); scope.DrainTo(target);
            bool privateHeld = target.PrivateHeldButtons.SetEquals(["Left"]);
            bool globalNeverHeld = Native.ButtonKeys.All(k => (Native.GetAsyncKeyState(k) & 0x8000) == 0);
            scope.Dispose(); scope.DrainTo(target); target.CancelPrivateHeld(); stimulus.UpIfHeld(target.Handle, "Left");
            await Task.Delay(80);
            data["HeldExitScope"] = ScopeEvidence(scope);
            CheckThat(checks, "HeldExitCancelsPrivateLedger", privateHeld && scope.CanceledHeldButtons.SequenceEqual(["Left"]) && target.PrivateHeldButtons.Count == 0, scope.CanceledHeldButtons);
            CheckThat(checks, "HeldExitLeavesGlobalButtonsReleased", globalNeverHeld && Native.ButtonKeys.All(k => (Native.GetAsyncKeyState(k) & 0x8000) == 0), globalNeverHeld);
            CheckScope(checks, scope, "HeldExitScope"); scope = null;

            ObserverSnapshot after = await PositiveControlAsync(peer, observerHandle, stimulus, "after");
            data["After"] = after; CheckPositive(checks, after, "After");
            CheckThat(checks, "OwnNativeCallbacksNoFailure", target.CallbackFailure is null, target.CallbackFailure?.ToString());
        }
        catch (Exception error) { data["Failure"] = error.ToString(); CheckThat(checks, "RunnerCompleted", false, error.Message); }
        finally
        {
            scope?.Dispose();
            if (scope is not null) { data["EmergencyScopeCleanup"] = ScopeEvidence(scope); target?.CancelPrivateHeld(); }
            try { await stimulus.CleanupHeldAsync(); }
            catch (Exception error) { CheckThat(checks, "GuardedStimulusCleanup", false, error.ToString()); }
            data["StimulusGuardCount"] = stimulus.GuardCount; data["RemainingStimulusHeldButtons"] = stimulus.HeldCount;
            CheckThat(checks, "NoRemainingStimulusHeldButtons", stimulus.HeldCount == 0, stimulus.HeldCount);
            if (peer is not null)
            {
                try { await peer.SendAsync("stop"); } catch (Exception error) { data["ObserverStopError"] = error.Message; }
                peer.Dispose();
            }
            if (child is not null)
            {
                try
                {
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
                    CheckThat(checks, "ObserverNormalExit", child.ExitCode == 0, child.ExitCode);
                }
                catch (TimeoutException) { child.Kill(); await child.WaitForExitAsync(); CheckThat(checks, "ObserverNormalExit", false, "Only the self-started child Process was terminated after timeout."); }
                child.Dispose();
            }
            target?.Dispose();
            bool cursorRestored = Native.SetCursorPos(savedCursor.X, savedCursor.Y) && Native.Cursor() == savedCursor;
            data["CursorRestored"] = cursorRestored;
            if (savedForeground != 0 && Native.IsWindow(savedForeground)) Native.SetForegroundWindow(savedForeground);
            data["ForegroundRestoreAccepted"] = Native.GetForegroundWindow() == savedForeground;
            data["FinishedUtc"] = DateTimeOffset.UtcNow; data["Checks"] = checks;
            data["Passed"] = checks.Count(c => c.Passed); data["Failed"] = checks.Count(c => !c.Passed);
            data["AllChecksPassed"] = checks.Count > 0 && checks.All(c => c.Passed);
            Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(data, JsonOptions));
            Console.WriteLine($"Mouse probe {checks.Count(c => c.Passed)}/{checks.Count}: {evidencePath}");
        }
        return checks.Count > 0 && checks.All(c => c.Passed) ? 0 : 1;
    }

    private static object ScopeEvidence(MouseSuppressionScope scope) => new { scope.Suppressed, scope.ForwardedOutsideLease,
        scope.ScopeLost, scope.LastError, scope.IsThreadAlive, scope.UnhookSucceeded, scope.CanceledHeldButtons, Failure = scope.Failure?.ToString() };
    private static void CheckScope(List<Check> checks, MouseSuppressionScope scope, string prefix)
        => CheckThat(checks, prefix + "CleanDispose", !scope.IsThreadAlive && scope.UnhookSucceeded && scope.LastError == 0 && scope.Failure is null, ScopeEvidence(scope));
    private static void CheckPositive(List<Check> checks, ObserverSnapshot snap, string prefix)
    {
        CheckThat(checks, prefix + "OrdinaryAllButtonsAxesMove", InputNames.All(name => snap.OrdinaryMessages.GetValueOrDefault(name) > 0), snap.OrdinaryMessages);
        CheckThat(checks, prefix + "RawAllButtonsAxesMove", snap.RawPackets > 0 && snap.RawMotionPackets > 0 && InputNames.Where(n => n != "Move").All(name => snap.RawButtons.GetValueOrDefault(name) > 0), new { snap.RawPackets, snap.RawMotionPackets, snap.RawButtons });
        CheckThat(checks, prefix + "AsyncAllButtonsExercised", Native.ButtonKeys.All(key => snap.ButtonHighSamples.GetValueOrDefault(key) > 0), snap.ButtonHighSamples);
        CheckThat(checks, prefix + "CursorMoves", snap.CursorTransitions >= 4 && snap.CursorSamples.Distinct().Count() >= 5, snap.CursorSamples);
        CheckThat(checks, prefix + "ObserverHealthyOwnForeground", snap.Failure is null && snap.ForeignForegroundSamples == 0 && snap.Samples >= 20, new { snap.Failure, snap.ForeignForegroundSamples, snap.Samples });
    }
    private static async Task<ObserverSnapshot> PositiveControlAsync(ObserverPeer peer, nint observer, GuardedStimulus stimulus, string phase)
    {
        Native.AllowSetForegroundWindow(Native.Pid(observer));
        await ActivateAsync(observer, (int)Native.Pid(observer), peer);
        stimulus.PlaceCursorInside(observer); await Task.Delay(100);
        await peer.SendAsync("reset:" + phase);
        await ExerciseAsync(observer, stimulus, null, null); await Task.Delay(120);
        return await peer.SendAsync("snapshot");
    }
    private static async Task<List<PixelPoint>> ExerciseAsync(nint window, GuardedStimulus stimulus, MouseSuppressionScope? scope, ProbeWindow? privateTarget)
    {
        var rect = Native.ClientBounds(window); var actual = new List<PixelPoint>();
        for (int i = 0; i < 6; i++)
        {
            var point = new PixelPoint(rect.Left + 160 + i * 23, rect.Top + 165 + i * 11);
            stimulus.Move(window, point); await Task.Delay(30);
            if (scope is not null) scope.DrainTo(privateTarget!);
            actual.Add(Native.Cursor());
        }
        foreach (string button in new[] { "Left", "Right", "Middle", "X1", "X2" })
        {
            stimulus.Down(window, button); await Task.Delay(70); scope?.DrainTo(privateTarget!);
            stimulus.UpIfHeld(window, button); await Task.Delay(30); scope?.DrainTo(privateTarget!);
        }
        foreach (uint flags in new uint[] { 0x800, 0x1000 })
            foreach (int delta in new[] { 120, -120 })
            { stimulus.Wheel(window, flags, delta); await Task.Delay(35); scope?.DrainTo(privateTarget!); }
        return actual;
    }
    private static async Task ActivateAsync(nint window, int pid, ObserverPeer? peer)
    {
        if (!Native.IsWindow(window) || Native.Pid(window) != pid) throw new InvalidOperationException("Cannot activate an invalid/unowned fixture HWND.");
        if (peer is not null) await peer.SendAsync("activate");
        else { Native.SetWindowPos(window, 0, 0, 0, 0, 0, 0x13); Native.SetForegroundWindow(window); }
        var timer = Stopwatch.StartNew();
        Console.WriteLine($"Waiting for own fixture foreground HWND={window} PID={pid} (no unlock keys or foreign-window input). ");
        while (Native.GetForegroundWindow() != window && timer.Elapsed < TimeSpan.FromSeconds(ActivationWaitSeconds))
        {
            Cancellation.ThrowIfCancellationRequested();
            if (!Native.IsWindowVisible(window) || ProbeWindow.IsCancellationRequested(window)) throw new OperationCanceledException("The own fixture was closed/cancelled during activation handshake.");
            if (peer is not null && (await peer.SendAsync("info")).CancellationRequested) throw new OperationCanceledException("Own observer activation cancelled.");
            await Task.Delay(50, Cancellation);
        }
        if (Native.GetForegroundWindow() != window) throw new InvalidOperationException("Windows refused own fixture foreground activation; no stimulus was sent to another application.");
    }
    private static Process StartObserver(string pipe, Native.Rect rect)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = RepositoryRoot() };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in new[] { "--observer", "--pipe", pipe, "--parent-pid", Environment.ProcessId.ToString(), "--x", rect.Left.ToString(), "--y", rect.Top.ToString(), "--width", (rect.Right - rect.Left).ToString(), "--height", (rect.Bottom - rect.Top).ToString() }) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Independent observer process could not start.");
    }
    private static void CheckThat(List<Check> checks, string name, bool passed, object? actual)
    {
        // Assertion evidence must not change when later held-exit or cleanup phases mutate a window's dictionaries.
        JsonElement snapshot = JsonSerializer.SerializeToElement(actual);
        checks.Add(new(name, passed, snapshot)); Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
    }
    private static void Require(Check check) { if (!check.Passed) throw new InvalidOperationException(check.Name); }
    private static string Value(string[] args, string name) => OptionalValue(args, name) ?? throw new ArgumentException($"Missing {name}.");
    private static string? OptionalValue(string[] args, string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    internal static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FloatCanvas.slnx"))) return dir.FullName;
        return Environment.CurrentDirectory;
    }
    private sealed record Check(string Name, bool Passed, object? Actual);
}

internal sealed class ObserverPeer : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    public ObserverSnapshot? LastSnapshot { get; private set; }
    internal ObserverPeer(NamedPipeServerStream pipe) { _pipe = pipe; _reader = new StreamReader(pipe); _writer = new StreamWriter(pipe) { AutoFlush = true }; }
    public async Task<ObserverSnapshot> SendAsync(string command)
    {
        await _writer.WriteLineAsync(JsonSerializer.Serialize(command));
        string line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(4)) ?? throw new EndOfStreamException("Observer pipe closed.");
        var reply = JsonSerializer.Deserialize<ObserverReply>(line) ?? throw new InvalidDataException("Invalid observer response.");
        LastSnapshot = reply.Snapshot;
        if (reply.Command != command || reply.Error is not null || reply.Snapshot is null) throw new InvalidDataException(reply.Error ?? "Observer command response mismatch.");
        if (command != "stop" && reply.Snapshot.CancellationRequested) throw new OperationCanceledException("Own observer requested cancellation.");
        return reply.Snapshot;
    }
    public void Dispose() { _writer.Dispose(); _reader.Dispose(); _pipe.Dispose(); }
}

internal sealed class GuardedStimulus
{
    private readonly Dictionary<nint, int> _owned = [];
    private readonly Dictionary<string, nint> _held = [];
    public int GuardCount { get; private set; }
    public int HeldCount => _held.Count;
    public void AddOwn(nint window, int pid)
    {
        if (!Native.IsWindow(window) || Native.Pid(window) != pid) throw new InvalidOperationException("Invalid fixture ownership registration.");
        _owned.Add(window, pid);
    }
    private void Guard(nint window, PixelPoint? destination = null, bool requireCurrentOwn = true, bool allowCancelled = false)
    {
        if (!allowCancelled && (Program.Cancellation.IsCancellationRequested || ProbeWindow.IsCancellationRequested(window)))
            throw new OperationCanceledException("Own probe cancellation requested; no new stimulus is permitted.");
        if (!_owned.TryGetValue(window, out int pid) || !Native.IsWindow(window) || !Native.IsWindowVisible(window) || Native.Pid(window) != pid || Native.GetForegroundWindow() != window)
            throw new InvalidOperationException("Stimulus rejected: exact registered own foreground HWND/PID guard failed.");
        Native.Rect bounds = Native.ClientBounds(window);
        if (requireCurrentOwn)
        {
            var current = Native.Cursor();
            if (!bounds.Contains(current) || Native.WindowFromPoint(new Native.Point(current.X, current.Y)) != window)
                throw new InvalidOperationException("Stimulus rejected: actual cursor is not over the registered own client surface.");
        }
        if (destination is not null && (!bounds.Contains(destination) || Native.WindowFromPoint(new Native.Point(destination.X, destination.Y)) != window))
            throw new InvalidOperationException("Stimulus rejected: requested point does not hit the registered own client surface.");
        GuardCount++;
    }
    public void PlaceCursorInside(nint window, bool allowCancelled = false)
    {
        var bounds = Native.ClientBounds(window); var point = new PixelPoint(bounds.Left + 120, bounds.Top + 140);
        Guard(window, point, false, allowCancelled);
        if (!Native.SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("Could not position the cursor inside the own fixture.");
        Guard(window, allowCancelled: allowCancelled);
    }
    public void Move(nint window, PixelPoint point) { Guard(window, point); Native.SendMove(point); }
    public void Down(nint window, string button)
    {
        Guard(window);
        if (_held.ContainsKey(button)) throw new InvalidOperationException("Duplicate synthetic mouse Down.");
        (uint down, _, uint data) = Flags(button); Native.SendMouse(down, data); _held.Add(button, window);
    }
    public void UpIfHeld(nint window, string button)
    {
        if (!_held.TryGetValue(button, out nint owner)) return;
        if (owner != window) throw new InvalidOperationException("Synthetic Up target differs from its Down owner.");
        Guard(window, allowCancelled: true); (_, uint up, uint data) = Flags(button); Native.SendMouse(up, data); _held.Remove(button);
    }
    public void Wheel(nint window, uint flags, int delta) { Guard(window); Native.SendMouse(flags, unchecked((uint)delta)); }
    public async Task CleanupHeldAsync()
    {
        foreach ((string button, nint window) in _held.ToArray())
        {
            // Recovery activation is confined to the exact self-created HWND; never release a passed Down into another app.
            Native.SetForegroundWindow(window); await Task.Delay(80);
            PlaceCursorInside(window, true); UpIfHeld(window, button);
        }
    }
    private static (uint Down, uint Up, uint Data) Flags(string button) => button switch
    {
        "Left" => (2, 4, 0), "Right" => (8, 16, 0), "Middle" => (32, 64, 0),
        "X1" => (128, 256, 1), "X2" => (128, 256, 2), _ => throw new ArgumentOutOfRangeException(nameof(button))
    };
}
