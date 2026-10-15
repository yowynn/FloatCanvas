using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.PlatformTests;
using FloatCanvas.Windows;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

internal static partial class Program
{
    private static bool PointerBoundaryProbe, PointerBoundaryAwaitFinal;
    private static string PointerBoundaryReport => Path.Combine(Root, "artifacts", "e2e",
        PointerBoundaryAwaitFinal ? "pointer-boundary-probe-await-final.json" : "pointer-boundary-probe.json");
    private static readonly List<BoundaryInjection> BoundaryInjections = [];
    private static readonly List<BoundaryEvent> BoundaryEvents = [];
    private static Native.Point[] BoundaryPath = [];
    private static SceneDocument? BoundaryDocument;
    private static object? BoundaryFinalState, BoundaryAwaitResult;
    private static object? BoundaryOriginalCutoffState, BoundaryCompletion, BoundaryStaticFrames;
    private static SceneDocument? BoundaryOriginalCutoffDocument;
    private static long BoundaryStarted;

    private static async Task RunPointerBoundaryProbe(Wpf.Application app)
    {
        Lower = new NativeTestWindow(); Lower.Activate();
        if (KeyboardWaitForActivation && Native.GetForegroundWindow() != Lower.Handle)
        {
            Console.WriteLine($"WAITING_OWN_POINTER_FIXTURE: pid={Environment.ProcessId}; hwnd={Lower.Handle}; title=浮绘 · 自建输入验证窗口; timeout=30s");
            long started = Stopwatch.GetTimestamp();
            while (Native.GetForegroundWindow() != Lower.Handle)
            {
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 30) throw new TimeoutException("Own pointer fixture activation timed out; no input injected.");
                await Task.Delay(60);
            }
            Directory.CreateDirectory(Profile!); string marker = Path.Combine(Profile!, "activation.ready");
            Console.WriteLine("WAITING_ACTIVATION_ACTION_COMPLETE: " + marker);
            while (!File.Exists(marker))
            {
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 45) throw new TimeoutException("Activation action was not acknowledged; no input injected.");
                await Task.Delay(60);
            }
            if (Native.GetForegroundWindow() != Lower.Handle) throw new InvalidOperationException("Own pointer fixture lost foreground before production construction.");
        }
        Session = Activator.CreateInstance(DesktopType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [app], null)!;
        await CallAsync("InitializeDocumentsAsync", true);
        if (Field<DispatcherTimer?>("_documentRecoveryTimer") is { } recovery) { recovery.Stop(); recovery.Interval = TimeSpan.FromHours(1); }
        Host = Field<OverlayHost>("_host"); Monitors = Host.Monitors;
        var first = Lower.ScreenPoint(170, 190); var last = Lower.ScreenPoint(310, 270); Anchor = first;
        Check(Lower.OwnsPoint(new(first.X - 50, first.Y - 60)) && Lower.OwnsPoint(new(last.X + 100, last.Y + 100)),
            "Original tool and transform rectangle is strictly inside our self-owned native client");
        Call("SetEditing", true); await Task.Delay(70); await ActivateInput();
        var editor = Active(); var document = editor.Document.Clone(); document.Elements.Clear(); document.NextNumber = 1;
        editor.ReplaceDocument(document); SelectTool("Freehand"); await ActivateInput();
        BoundaryPath = Enumerable.Range(0, 25).Select(i => new Native.Point(first.X + (last.X - first.X) * i / 24,
            first.Y + 38 + (int)Math.Round(Math.Sin(i * Math.PI * 2 / 24) * 25))).ToArray();
        BoundaryStarted = Stopwatch.GetTimestamp();
        void Observe(OverlayPointerEvent e)
        {
            var element = editor.Document.Elements.FirstOrDefault();
            BoundaryEvents.Add(new(BoundaryEvents.Count, Stopwatch.GetTimestamp(), PointerBoundaryNative.GetMessageTime(), e.Kind.ToString(),
                e.Position.X, e.Position.Y, e.Button.ToString(), e.Modifiers.ToString(), e.WheelDelta, BoundaryCursor(),
                editor.IsTransactionActive, element?.Points.Count ?? 0, element?.Points.LastOrDefault(), element?.Transform));
        }
        Host.Pointer += Observe;
        try
        {
            BoundarySend("Move", 0, BoundaryPath[0], () => Move(BoundaryPath[0]));
            BoundarySend("Down", 0, null, Down);
            try
            {
                for (int i = 1; i < BoundaryPath.Length; i++)
                {
                    int step = i;
                    BoundarySend("Move", step, BoundaryPath[step], () => Move(BoundaryPath[step]));
                    // Baseline is exactly the existing input fixture cadence, including after the last Move.
                    await Task.Delay(6);
                }
                if (PointerBoundaryAwaitFinal) await AwaitBoundaryFinalPoint();
            }
            finally { BoundarySend("Up", BoundaryPath.Length - 1, null, Up); }
            await Task.Delay(35); await Task.Delay(55);
            BoundaryOriginalCutoffDocument = editor.Document.Clone();
            BoundaryOriginalCutoffState = new { Cursor = BoundaryCursor(), editor.IsTransactionActive, editor.UndoCount,
                CaptureHandle = Native.GetCapture().ToInt64(), UpEvents = BoundaryEvents.Count(e => e.Kind == nameof(OverlayPointerKind.Up)) };
            long completionStarted = Stopwatch.GetTimestamp(); bool completed;
            while (true)
            {
                bool sawUp = BoundaryEvents.Any(e => e.Kind == nameof(OverlayPointerKind.Up));
                completed = sawUp && !editor.IsTransactionActive && editor.UndoCount == 1 && Native.GetCapture() == 0;
                BoundaryCompletion = new { Completed = completed, SawActualHostUp = sawUp, editor.IsTransactionActive, editor.UndoCount,
                    CaptureHandle = Native.GetCapture().ToInt64(), ObservedAt = Stopwatch.GetTimestamp(),
                    AdditionalObservationMilliseconds = Stopwatch.GetElapsedTime(completionStarted).TotalMilliseconds };
                if (completed || Stopwatch.GetElapsedTime(completionStarted).TotalSeconds >= 2) break;
                await Task.Delay(1);
            }
            Check(completed, "The original real Up reaches Host, commits one history item and releases capture within the 2-second observation deadline");
            Check(editor.Document.Elements.Count == 1, "Freehand: real WM pointer input created exactly one object");
            var element = editor.Document.Elements.Single();
            Check(element.Kind == ElementKind.Freehand, "Freehand: actual controller tool determines retained element kind");
            var bounds = ElementGeometry.Bounds(element, includeStroke: false);
            Check(Math.Abs(bounds.Left - first.X) <= 2 && Math.Abs(bounds.Right - last.X) <= 2 && bounds.Height >= 40,
                "Freehand: original physical-coordinate geometry assertion without relaxed thresholds");
            long settleStarted = Stopwatch.GetTimestamp();
            while ((bool)(DesktopType.GetProperty("IsRenderPending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Session)!))
            {
                if (Stopwatch.GetElapsedTime(settleStarted).TotalSeconds >= 2) throw new TimeoutException("Committed scene rendering did not settle within two seconds.");
                await Task.Delay(1);
            }
            long staticFrames = Host.PresentedFrameCount, staticStarted = Stopwatch.GetTimestamp();
            await Task.Delay(250);
            long staticDelta = Host.PresentedFrameCount - staticFrames;
            BoundaryStaticFrames = new { PresentedBefore = staticFrames, PresentedAfter = Host.PresentedFrameCount, Delta = staticDelta,
                ActualObservationMilliseconds = Stopwatch.GetElapsedTime(staticStarted).TotalMilliseconds, Scope = "After actual Up/commit and pending render completion; no more input injected." };
            Check(staticDelta == 0, "The committed stationary scene presents no additional frames during a 250ms passive observation");
        }
        finally
        {
            BoundaryDocument = editor.Document.Clone();
            BoundaryFinalState = new { Host.IsEditing, CaptureHandle = Host.CaptureHandle.ToInt64(), Foreground = Native.GetForegroundWindow().ToInt64(),
                editor.IsTransactionActive, editor.UndoCount, editor.RedoCount, Cursor = BoundaryCursor() };
            Host.Pointer -= Observe;
        }
    }

    private static void BoundarySend(string operation, int step, Native.Point? expected, Action send)
    {
        var sample = new BoundaryInjection(BoundaryInjections.Count, operation, step, expected?.X, expected?.Y, BoundaryCursor());
        BoundaryInjections.Add(sample);
        try { send(); }
        catch (Exception ex) { sample.Error = ex.Message; throw; }
        finally { sample.After = BoundaryCursor(); }
    }
    private static BoundaryCursorSample BoundaryCursor()
    {
        bool valid = Native.GetCursorPos(out var point);
        return new(Stopwatch.GetTimestamp(), valid, point.X, point.Y, Native.GetForegroundWindow().ToInt64(), Native.GetCapture().ToInt64(),
            Host?.IsEditing ?? false, Host?.PresentedFrameCount ?? 0);
    }
    private static async Task AwaitBoundaryFinalPoint()
    {
        var final = BoundaryPath[^1]; long started = Stopwatch.GetTimestamp();
        while (true)
        {
            var cursor = BoundaryCursor(); var move = BoundaryEvents.LastOrDefault(e => e.Kind == nameof(OverlayPointerKind.Move));
            bool cursorAtFinal = cursor.Valid && Math.Abs(cursor.X - final.X) <= 1 && Math.Abs(cursor.Y - final.Y) <= 1;
            bool hostAtFinal = move is not null && Math.Abs(move.X - final.X) <= 1 && Math.Abs(move.Y - final.Y) <= 1;
            BoundaryAwaitResult = new { CursorAtFinal = cursorAtFinal, HostMoveAtFinal = hostAtFinal,
                ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds, Cursor = cursor, LastHostMove = move };
            if (cursorAtFinal && hostAtFinal) return;
            if (!Host!.IsEditing || Stopwatch.GetElapsedTime(started).TotalSeconds > 2)
                throw new TimeoutException("The original final Move did not reach both Cursor and Host within two seconds; no corrective Move injected.");
            await Task.Delay(1);
        }
    }
    private static void WritePointerBoundaryReport(bool passed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PointerBoundaryReport)!);
        File.WriteAllText(PointerBoundaryReport, JsonSerializer.Serialize(new
        {
            SchemaVersion = 2, Passed = passed, Failure, Timestamp = DateTimeOffset.Now, Profile, Checks, Monitors,
            Variant = PointerBoundaryAwaitFinal ? "AwaitOriginalFinalCursorAndHostMoveBeforeUp" : "OriginalCadenceBaseline",
            Method = "One production freehand gesture, exactly the original 25-point sine path and 6ms waits between subsequent Move injections. Each SendInput wrapper is sampled before and immediately after. An appended Host.Pointer observer records actual full event type/position/button/modifiers/QPC/current message time, after the production controller callback.",
            Baseline = "Adds no endpoint wait, retry, corrected Move, timing stabilization or geometry-threshold change. Logging Win32/QPC values has instrumentation overhead which can affect scheduling.",
            OptionalVariant = "Only with --await-final-point: waits up to 2s until GetCursorPos and the last observed Host Move are within one physical pixel of the original final planned point, then emits the original Up. No additional Move is injected. The original geometry assertion still uses its unchanged 2px threshold.",
            CompletionObservation = "After the unchanged original 35ms+55ms observation cutoff, saves that exact cutoff state, then observes for at most 2 additional seconds for actual Host Up, committed history and released capture. This adds observation after the original input sequence, not injection delay or corrected Move. A further 250ms passive stationary frame count begins only after pending renders settle.",
            Units = new { StopwatchFrequency = Stopwatch.Frequency, BoundaryStarted, Qpc = "Stopwatch ticks", MessageTime = "GetMessageTime, Win32 millisecond timestamp of the current queued message" },
            PlannedPath = BoundaryPath.Select((p, i) => new { Step = i, p.X, p.Y }).ToArray(),
            Injections = BoundaryInjections, HostEvents = BoundaryEvents, AwaitResult = BoundaryAwaitResult,
            OriginalCutoffState = BoundaryOriginalCutoffState, OriginalCutoffDocument = BoundaryOriginalCutoffDocument,
            GestureCompletion = BoundaryCompletion, StaticFrames = BoundaryStaticFrames, EndpointTiming = BoundaryEndpointTiming(),
            FinalDocument = BoundaryDocument, FinalStateBeforeCleanup = BoundaryFinalState,
            PointerGuards, PointerInjections,
            Safety = "Original exact own InputHandle/WindowFromPoint and lower-client guards are unchanged. No other application is targeted. Main finally releases input, disposes owned windows and restores original cursor/foreground. No screen images are read or written.",
            Scope = "Diagnostic stream boundary evidence only; one variant run is not a full 218-check regression or a proof of the cause of any earlier failure. Reports are isolated from all existing evidence."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static object BoundaryEndpointTiming()
    {
        var send = BoundaryInjections.LastOrDefault(i => i.Operation == "Move");
        var upSend = BoundaryInjections.LastOrDefault(i => i.Operation == "Up");
        var final = BoundaryPath.LastOrDefault();
        var move = BoundaryEvents.FirstOrDefault(e => e.Kind == nameof(OverlayPointerKind.Move) && send?.After is { } after && e.At >= after.At &&
            Math.Abs(e.X - final.X) <= 1 && Math.Abs(e.Y - final.Y) <= 1);
        var up = BoundaryEvents.FirstOrDefault(e => e.Kind == nameof(OverlayPointerKind.Up));
        return new
        {
            FinalMoveInjectionAfter = send?.After?.At, FirstHostFinalMove = move?.At,
            FinalMoveAfterToHostMilliseconds = send?.After is { } moved && move is not null ? (move.At - moved.At) * 1000d / Stopwatch.Frequency : (double?)null,
            UpInjectionAfter = upSend?.After?.At, FirstHostUp = up?.At,
            UpAfterToHostMilliseconds = upSend?.After is { } released && up is not null ? (up.At - released.At) * 1000d / Stopwatch.Frequency : (double?)null,
            HostMoveEvents = BoundaryEvents.Count(e => e.Kind == nameof(OverlayPointerKind.Move)),
            HostUpEvents = BoundaryEvents.Count(e => e.Kind == nameof(OverlayPointerKind.Up)),
            Meaning = "Observer-after-controller timestamps minus SendInput-wrapper-return timestamps, not raw OS dispatch latency or screen exposure."
        };
    }
    private sealed class BoundaryInjection(int index, string operation, int step, int? expectedX, int? expectedY, BoundaryCursorSample before)
    {
        public int Index { get; } = index;
        public string Operation { get; } = operation;
        public int Step { get; } = step;
        public int? ExpectedX { get; } = expectedX;
        public int? ExpectedY { get; } = expectedY;
        public BoundaryCursorSample Before { get; } = before;
        public BoundaryCursorSample? After { get; set; }
        public string? Error { get; set; }
    }
    private sealed record BoundaryCursorSample(long At, bool Valid, int X, int Y, long Foreground, long Capture, bool IsEditing, long PresentedFrames);
    private sealed record BoundaryEvent(int Index, long At, int MessageTime, string Kind, double X, double Y, string Button, string Modifiers,
        int WheelDelta, BoundaryCursorSample CursorAtEvent, bool TransactionActive, int DocumentPoints, PointD? LastLocalPoint, Affine2D? Transform);
    private static class PointerBoundaryNative { [DllImport("user32.dll")] internal static extern int GetMessageTime(); }
}
