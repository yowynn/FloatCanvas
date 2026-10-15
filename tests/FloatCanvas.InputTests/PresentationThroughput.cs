using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.PlatformTests;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

internal static partial class Program
{
    private const int PresentationRate = 120, PresentationWarmupSeconds = 1, PresentationMeasuredSeconds = 10;
    private static bool PresentationThroughput;
    private static readonly string PresentationReport = Path.Combine(Root, "artifacts", "e2e",
        $"presentation-throughput-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Environment.ProcessId}.json");
    private static readonly List<object> PresentationCases = [];
    private static MonitorInfo? PresentationPhysicalPrimary, PresentationTile;
    private static object? PresentationIdle;
    private static readonly DateTimeOffset PresentationStartedAt = DateTimeOffset.Now;

    private static async Task RunPresentationThroughput(Wpf.Application app)
    {
        Check(KeyboardWaitForActivation, "Presentation throughput requires --wait-for-activation and the external activation handshake");
        OtherApplicationProcesses = new[] { "FloatCanvas", "FloatCanvas.SessionTests", "FloatCanvas.InputTests", "FloatCanvas.PlatformTests" }
            .SelectMany(Process.GetProcessesByName).Where(p => p.Id != Environment.ProcessId)
            .Select(p => { using (p) return p.ProcessName + ":" + p.Id; }).ToArray();
        Check(OtherApplicationProcesses.Length == 0, "No other FloatCanvas process is running; no application is closed by this probe");
        PerformanceHardware = ReadPerformanceHardware();
        Lower = new NativeTestWindow();
        await WaitForOwnFixtureActivation();
        Native.SetWindowPos(Lower.Handle, new nint(-1), 0, 0, 0, 0, 0x0013);
        using (var layout = new OverlayHost(OverlayMonitorScope.PrimaryScreen)) PresentationPhysicalPrimary = layout.PrimaryMonitor;
        PresentationTile = PresentationMeasurements.PrimaryTile(PresentationPhysicalPrimary!);
        Func<IReadOnlyList<MonitorInfo>> provider = () => new[] { PresentationTile! };
        var tileHost = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [OverlayMonitorScope.PrimaryScreen, provider], null)!;
        try
        {
            Session = Activator.CreateInstance(DesktopType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, [app, tileHost], null)!;
        }
        catch { tileHost.Dispose(); throw; }
        await CallAsync("InitializeDocumentsAsync", false);
        Host = Field<OverlayHost>("_host"); Monitors = Host.Monitors;
        Check(ReferenceEquals(Host, tileHost) && Monitors.Count == 1 && Monitors[0] == PresentationTile,
            "Production DesktopSession owns the actual 1920x1080 native host/DIB inside the real primary display");
        var origin = Lower.ScreenPoint(190, 220);
        Anchor = new(origin.X + 60, origin.Y + 40);
        foreach (int count in new[] { 100, 1006 })
            foreach (double opacity in new[] { 1d, .7d }) await MeasurePresentationCase(count, opacity, origin);

        Call("SetEditing", false);
        await WaitForRenderToSettle();
        using var process = Process.GetCurrentProcess();
        long idleFrames = Host.PresentedFrameCount, idleAt = Stopwatch.GetTimestamp();
        var idleCpu = process.TotalProcessorTime;
        await Task.Delay(3000);
        double idleSeconds = Stopwatch.GetElapsedTime(idleAt).TotalSeconds;
        double cpuSeconds = (process.TotalProcessorTime - idleCpu).TotalSeconds;
        PresentationIdle = new { Seconds = idleSeconds, PresentedOutputs = Host.PresentedFrameCount - idleFrames,
            CpuPercentOfOneCore = cpuSeconds / idleSeconds * 100, CpuPercentOfMachine = cpuSeconds / idleSeconds / Environment.ProcessorCount * 100 };
        Check(Host.PresentedFrameCount == idleFrames, "Static pass-through presents no frames during the final three-second CPU sample");
        await CallAsync("FlushRecoveryAsync");
    }

    private static async Task MeasurePresentationCase(int count, double opacity, Native.Point origin)
    {
        RequireOwnPresentationForeground();
        var editor = Active();
        var fixture = CreatePerformanceScene(PresentationTile!, origin, Field<AssetLibrary>("_assets"));
        Guid activeId = fixture.Elements[500].Id;
        if (count == 100)
            fixture.Elements = fixture.Elements.Take(93).Append(fixture.Elements[500]).Concat(fixture.Elements.Skip(1000)).ToList();
        Check(fixture.Elements.Count == count, $"Generated exact {count}-object scene with original synthetic PNG assets");
        fixture.LayerOpacity = opacity;
        editor.ReplaceDocument(new SceneDocument { Viewport = fixture.Viewport });
        editor.Execute("Presentation measurement fixture", document =>
        { document.Elements = fixture.Elements; document.LayerOpacity = opacity; document.Viewport = fixture.Viewport; });
        var recovery = Field<DispatcherTimer?>("_documentRecoveryTimer");
        Check(recovery is not null && recovery.Interval == TimeSpan.FromSeconds(1), "Default one-second recovery interval remains unchanged");
        // Settle the initial bulk commit only; recovery stays enabled throughout the measured gesture.
        await Task.Delay(1200);
        RequireOwnPresentationForeground();
        SelectTool("Select"); editor.SetSelection([activeId]);
        Check((bool)Call("SetEditing", true)!, "Production editing entry succeeded before any probe movement");
        await Task.Delay(100);
        nint input = Host!.Diagnostics.Single().InputHandle;
        RequireOwnPresentationForeground(allowToolbar: true);
        Native.SetForegroundWindow(input); Native.SetFocus(input);
        Check(Native.GetForegroundWindow() == input, "The input HWND activates only from an owned foreground, without fallback mouse clicks");
        await WaitForRenderToSettle();
        uint thread = PresentationProbeNative.OwnWindowThread(input);
        Native.GetClientRect(Lower!.Handle, out var client);
        var clientOrigin = Lower.ScreenPoint(0, 0);
        var safeClient = new PixelBounds(clientOrigin.X, clientOrigin.Y, client.Right, client.Bottom);
        Check(safeClient.Contains(new(Anchor.X, Anchor.Y)) &&
            safeClient.Contains(new(Anchor.X + 250, Anchor.Y + 80)), "Entire producer path and retained rectangle fit inside the owned lower client");
        Check(editor.HitTest(new(Anchor.X, Anchor.Y))?.Id == activeId, "The initial drag point hits the retained rectangle");
        var original = editor.Document.Elements.Single(e => e.Id == activeId).Transform;
        int undoBefore = editor.UndoCount;
        if (Native.GetForegroundWindow() != input || Native.WindowFromPoint(Anchor) != input)
            throw new InvalidOperationException("The exact owned input HWND is not foreground before targeted pointer-down.");
        PresentationProbeNative.PostButton(input, PresentationTile!, Anchor, true);
        bool postedDown = true;
        var state = new PresentationRunState { SequenceBase = PresentationCases.Count * PresentationMeasurements.SampleCapacity };
        long counterBefore = Host.PresentedFrameCount;
        var cacheBefore = ReadOverlayCache();
        var overlayCache = Field<OverlaySceneCache>("_overlayCache");
        long opacityFramesBefore = overlayCache.InPlaceOpacityFrameCount;
        var observed = new BoundedSamples<PresentationObservedMove>(PresentationMeasurements.SampleCapacity);
        var frames = new BoundedSamples<OverlayFrameTiming>(PresentationMeasurements.SampleCapacity);
        var rawMessages = new BoundedSamples<OverlayPointerMessageDiagnostic>(PresentationMeasurements.SampleCapacity);
        var pointerDecisions = new BoundedSamples<PresentationPointerDecision>(PresentationMeasurements.SampleCapacity);
        var lastObservedPoint = default(OverlayPoint);
        OverlayPoint? initialCursorBaseline = null;
        long initialCursorBaselineTimestamp = 0;
        int ignoredUntaggedDuplicates = 0;
        int staleTaggedMoves = 0;
        void NativeMessage(OverlayPointerMessageDiagnostic message)
        {
            if (!rawMessages.TryAdd(message)) state.Abort("Bounded native-message diagnostic storage overflowed.");
        }
        void Frame(OverlayFrameTiming sample)
        {
            if (!frames.TryAdd(sample) || !sample.Succeeded) state.Abort("Native frame timing failed or bounded recording overflowed.");
        }
        void Pointer(OverlayPointerEvent e)
        {
            if (e.Kind != OverlayPointerKind.Move || !state.ObserveMoves) return;
            long timestamp = Stopwatch.GetTimestamp();
            ulong lateExtra = PresentationProbeNative.CurrentMessageExtra();
            int sequence = PresentationMeasurements.DecodeSequence((nuint)lateExtra);
            void Decision(string decision)
            {
                if (!pointerDecisions.TryAdd(new(timestamp, e.Position, lastObservedPoint, lateExtra,
                    sequence, rawMessages.Count - 1, Volatile.Read(ref state.StartedTimestamp), decision)))
                    state.Abort("Bounded pointer-decision diagnostic storage overflowed.");
            }
            if (Volatile.Read(ref state.StartedTimestamp) == 0) { Decision("BeforeProducer"); return; }
            if (sequence == 0)
            {
                // Layered-window updates can generate a duplicate native Move at the unchanged
                // pointer position. Ignore only that no-op; do not count it as tagged producer input.
                if (e.Position == lastObservedPoint)
                { Decision("UntaggedSamePosition"); ignoredUntaggedDuplicates++; return; }
                Decision("UntaggedChangedPosition");
                state.Abort($"An untagged pointer move changed the owned producer position: current=({e.Position.X},{e.Position.Y}), previous=({lastObservedPoint.X},{lastObservedPoint.Y}), lateExtra=0x{lateExtra:X16}, nativeEntryIndex={rawMessages.Count - 1}."); return;
            }
            int slot = sequence - state.SequenceBase - 1;
            if (slot < 0 || slot >= PresentationRate * (PresentationWarmupSeconds + PresentationMeasuredSeconds))
            { Decision("StaleOtherCaseTag"); staleTaggedMoves++; lastObservedPoint = e.Position; return; }
            var planned = PresentationPoint(slot);
            if (Math.Abs(e.Position.X - planned.X) > 1 || Math.Abs(e.Position.Y - planned.Y) > 1)
            { Decision("TaggedCoordinateMismatch"); state.Abort("A tagged pointer move did not match its planned owned-client coordinates."); return; }
            Decision("TaggedMatch");
            if (!observed.TryAdd(new(sequence, Stopwatch.GetTimestamp(), e.Position.X, e.Position.Y)))
                state.Abort("Bounded pointer observation storage overflowed.");
            lastObservedPoint = e.Position;
        }
        void LayoutChanged() => state.Abort("The display layout changed during the presentation probe.");
        Host.FrameSubmitted += Frame; Host.PointerMessageObserved += NativeMessage; Host.Pointer += Pointer; Host.MonitorsChanged += LayoutChanged;
        ProducerResult? producer = null;
        string? caseFailure = null;
        try
        {
            await WaitPerformance(() => editor.IsTransactionActive && Host.CaptureHandle == input,
                "Targeted pointer-down did not acquire the production gesture and capture");
            // Targeted Down starts the gesture at Anchor without moving the physical cursor.
            // Establish the unchanged-position baseline from the real cursor after capture;
            // queued system repeats can still report the previous case's final position.
            if (!Native.GetCursorPos(out var initialCursor))
                throw new InvalidOperationException("The initial physical cursor position could not be read.");
            PresentationProbeNative.GuardMove(input, thread, safeClient, initialCursor);
            initialCursorBaseline = PresentationMeasurements.InitialPointerBaseline(new(initialCursor.X, initialCursor.Y), safeClient);
            lastObservedPoint = initialCursorBaseline.Value;
            initialCursorBaselineTimestamp = Stopwatch.GetTimestamp();
            if (state.Failure is { } initializationFailure) throw new InvalidOperationException(initializationFailure);
            producer = await Task.Run(() => ProducePresentationMoves(state, input, thread, safeClient));
            if (state.Failure is { } interrupted) throw new InvalidOperationException(interrupted);
            if (producer.Failure is { } failed) throw new InvalidOperationException(failed);
            int finalSequence = producer.Samples[^1].Sequence;
            await WaitPerformance(() => observed.Values.Any(x => x.Sequence == finalSequence), "The final tagged input was not observed by production input");
            if (state.Failure is { } observationFailure) throw new InvalidOperationException(observationFailure);
            state.ObserveMoves = false;
            PresentationProbeNative.PostButton(input, PresentationTile!, producer.LastPoint, false); postedDown = false;
            await WaitPerformance(() => !editor.IsTransactionActive && Native.GetCapture() == 0,
                "Targeted pointer-up did not commit and release the production gesture");
            await WaitForRenderToSettle();
            var moved = editor.Document.Elements.Single(e => e.Id == activeId);
            Check(Math.Abs(moved.Transform.OffsetX - original.OffsetX - producer.LastPoint.X + Anchor.X) <= 2 &&
                Math.Abs(moved.Transform.OffsetY - original.OffsetY - producer.LastPoint.Y + Anchor.Y) <= 2,
                "Production rectangle follows the complete tagged native movement stream");
            Check(editor.UndoCount == undoBefore + 1, "The complete continuous movement is one committed undo step");
            if (Native.GetForegroundWindow() != input) throw new InvalidOperationException("Owned input lost foreground before the guarded undo.");
            SendKey(0x11, false, false); SendKey(0x5A, false, false); SendKey(0x5A, true, false); SendKey(0x11, true, false);
            await Task.Delay(45);
            Check(editor.UndoCount == undoBefore && Near(editor.Document.Elements.Single(e => e.Id == activeId).Transform, original),
                "Guarded actual Ctrl+Z restores the retained rectangle after continuous measurement");
            Check(Lower.Counts.Clicks == 0 && Lower.Counts.DoubleClicks == 0 && Lower.Counts.DragMoves == 0,
                "No producer gesture reached the owned lower test application");
            Check(!frames.Overflowed && !observed.Overflowed, "Native frame and observed-input recording stayed bounded");
            Check(frames.Count > 0 && frames.Values.All(f => f.Succeeded && f.Outputs.Count == 1 && f.Outputs[0].Bounds == PresentationTile!.Bounds),
                "Every recorded native Render call successfully submits exactly the intended 1920x1080 output");
            Check(opacity < 1 ? overlayCache.InPlaceOpacityFrameCount > opacityFramesBefore :
                overlayCache.InPlaceOpacityFrameCount == opacityFramesBefore, "The production raster-opacity branch matches the requested layer opacity");
        }
        catch (Exception ex) { caseFailure = ex.ToString(); throw; }
        finally
        {
            string? cleanupFailure = null;
            bool cleanupSkippedDestroyedInput = false;
            try
            {
                if (postedDown)
                {
                    if (PresentationProbeNative.IsOwnWindow(input))
                    {
                        PresentationProbeNative.PostButton(input, PresentationTile!, producer?.LastPoint ?? Anchor, false);
                        PresentationProbeNative.CancelOwnedGesture(input);
                    }
                    else cleanupSkippedDestroyedInput = true;
                }
            }
            catch (Exception ex) { cleanupFailure = ex.ToString(); }
            // Cleanup diagnostics never replace the original producer/layout/gesture failure.
            Host.FrameSubmitted -= Frame; Host.PointerMessageObserved -= NativeMessage; Host.Pointer -= Pointer; Host.MonitorsChanged -= LayoutChanged;
            long start = Volatile.Read(ref state.StartedTimestamp) + Stopwatch.Frequency * PresentationWarmupSeconds;
            long end = start + Stopwatch.Frequency * PresentationMeasuredSeconds;
            var hotInputs = producer?.Samples.Where(x => x.BeforeTimestamp >= start && x.BeforeTimestamp < end).ToArray() ?? [];
            var sentSequences = hotInputs.Select(x => x.Sequence).ToHashSet();
            int uniqueObserved = observed.Values.Where(x => sentSequences.Contains(x.Sequence)).Select(x => x.Sequence).Distinct().Count();
            var summary = PresentationMeasurements.Summarize(frames.Values, start, end, Stopwatch.Frequency);
            PresentationCases.Add(new
            {
                ObjectCount = count, MixedVectors = count == 100 ? 94 : 1000, FreehandPoints = 2000, GeneratedImages = 5, LayerOpacity = opacity,
                Failure = caseFailure, CleanupFailure = cleanupFailure, CleanupSkippedDestroyedInput = cleanupSkippedDestroyedInput,
                MeasurementStartTimestamp = start, MeasurementEndTimestamp = end,
                InitialCursorBaseline = initialCursorBaseline, InitialCursorBaselineTimestamp = initialCursorBaselineTimestamp,
                DefaultRecoveryIntervalSeconds = recovery!.Interval.TotalSeconds, Native = summary,
                TotalHostOutputCounterDelta = Host.PresentedFrameCount - counterBefore,
                ActualInputMovesInMeasuredWindow = hotInputs.Length, UniqueTaggedMovesEventuallyObserved = uniqueObserved,
                CoalescedOrUnobservedMoves = hotInputs.Length - uniqueObserved,
                IgnoredSamePositionUntaggedMoves = ignoredUntaggedDuplicates,
                StaleTaggedMovesFromOtherCases = staleTaggedMoves,
                MergeDefinition = "Sent tagged Move sequences in the measured window minus distinct matching production Move observations after final-input drain; includes coalesced or otherwise unobserved moves, not a hardware loss claim.",
                CacheBefore = cacheBefore, CacheAfter = ReadOverlayCache(), Producer = producer,
                InPlaceOpacityFrames = overlayCache.InPlaceOpacityFrameCount - opacityFramesBefore,
                LastInjectedPoint = producer is null ? null : new { producer.LastPoint.X, producer.LastPoint.Y },
                ObservedMoves = observed.Values, FrameSamples = frames.Values,
                NativeMessageEntries = rawMessages.Values, PointerDecisions = pointerDecisions.Values,
                NativeSubmissionRateAtLeast60 = summary.RenderSubmissionsPerSecond >= 60,
                CompletionP95Within60HzBudget = summary.CompletionIntervalsMilliseconds.P95 <= 1000d / 60
            });
        }
        Call("SetEditing", false);
    }

    private static void RequireOwnPresentationForeground(bool allowToolbar = false)
    {
        nint foreground = Native.GetForegroundWindow();
        nint toolbar = allowToolbar ? new System.Windows.Interop.WindowInteropHelper(Field<Wpf.Window>("_toolbar")).Handle : 0;
        if (foreground != Lower!.Handle && !Host!.Diagnostics.Any(d => d.InputHandle == foreground) && !(allowToolbar && foreground == toolbar && toolbar != 0))
            throw new InvalidOperationException("Presentation probe lost its exact owned foreground between cases; it will not reclaim an external application.");
    }

    private static Native.Point PresentationPoint(int slot)
    {
        int phase = slot % 240, x = (phase <= 120 ? phase : 240 - phase) * 2;
        return new(Anchor.X + x, Anchor.Y + (int)Math.Round(Math.Sin(slot * .12) * 12));
    }

    private static ProducerResult ProducePresentationMoves(PresentationRunState state, nint input, uint thread, PixelBounds safeClient)
    {
        var samples = new BoundedSamples<PresentationInjectedMove>(PresentationMeasurements.SampleCapacity);
        long started = Stopwatch.GetTimestamp(); Volatile.Write(ref state.StartedTimestamp, started);
        long end = started + Stopwatch.Frequency * (PresentationWarmupSeconds + PresentationMeasuredSeconds);
        int slot = 0, skippedSlots = 0;
        Native.Point last = Anchor;
        string? failure = null;
        try
        {
            while (Stopwatch.GetTimestamp() < end)
            {
                if (state.Failure is not null) throw new InvalidOperationException(state.Failure);
                long now = Stopwatch.GetTimestamp();
                long due = started + (long)(slot * (double)Stopwatch.Frequency / PresentationRate);
                if (now < due) { Thread.Sleep(1); continue; }
                int currentSlot = (int)((now - started) * (double)PresentationRate / Stopwatch.Frequency);
                if (currentSlot > slot) { skippedSlots += currentSlot - slot; slot = currentSlot; }
                if (slot >= PresentationRate * (PresentationWarmupSeconds + PresentationMeasuredSeconds)) break;
                var point = PresentationPoint(slot);
                PresentationProbeNative.GuardMove(input, thread, safeClient, point);
                // Recheck cancellation immediately before each real SendInput movement.
                if (state.Failure is not null) throw new InvalidOperationException(state.Failure);
                long before = Stopwatch.GetTimestamp();
                if (before >= end) break;
                int sequence = state.SequenceBase + slot + 1;
                PresentationProbeNative.SendTaggedMove(point, sequence); PointerInjections++;
                long after = Stopwatch.GetTimestamp();
                long scheduled = started + (long)(slot * (double)Stopwatch.Frequency / PresentationRate);
                if (!samples.TryAdd(new(sequence, scheduled, before, after, point.X, point.Y)))
                    throw new InvalidOperationException("Bounded producer recording overflowed.");
                PointerGuards++; last = point; slot++;
            }
        }
        catch (Exception ex) { failure = ex.ToString(); state.Abort(ex.Message); }
        return new(started, Stopwatch.GetTimestamp(), PresentationRate, skippedSlots, samples.Values, last, failure);
    }

    private static void WritePresentationReport(bool passed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PresentationReport)!);
        File.WriteAllText(PresentationReport, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, CompletedWithoutHarnessErrors = passed, Failure, StartedAt = PresentationStartedAt, CompletedAt = DateTimeOffset.Now,
            Profile, Environment = PerformanceHardware, OtherApplicationProcesses,
            RealPhysicalPrimary = PresentationPhysicalPrimary, ActualNativePresentationTile = PresentationTile,
            PhysicalMonitorCount = Native.GetSystemMetrics(80), StopwatchFrequency = Stopwatch.Frequency,
            MarkerInterop = new { PresentationProbeNative.InputStructureBytes, PresentationProbeNative.InputMouseOffsetBytes,
                PresentationProbeNative.MouseExtraOffsetBytes, FirstExpectedMarker = $"0x{(ulong)PresentationMeasurements.EncodeSequence(1):X16}",
                Encoding = "Complete 32-bit 0x4643 prefix plus bounded low-16 sequence; decoding validates the full native value without masking. Four case sequence ranges remain disjoint.",
                Prior64BitMarkerFailureEvidence = "presentation-throughput-20261015T133534763Z-78032.json",
                NativeEntryDiagnostic = "Opt-in WM_MOUSEMOVE entry before NotifyInputProgress and all managed Pointer handlers; decisions also retain the later GetMessageExtraInfo result. Untagged changed positions still abort and all foreground/capture safety guards remain unchanged." },
            Method = "Production DesktopSession and native 1920x1080 DIB/ULW on a tile inside the real primary. Exact-HWND button messages begin/end capture; a separate bounded 120Hz producer offers uniquely tagged actual SendInput moves for 1s warmup + 10s measurement without waiting for previous renders.",
            InputSafety = "Every Move checks the exact own foreground HWND, hit HWND, process identity, client bounds, native capture and cancellation. No global synthetic mouse button is held. This is not a global button-isolation acceptance test.",
            DefaultRecoveryUnchanged = true,
            RecoveryCondition = "The production one-second timer and its committed-snapshot/fingerprint policy remain enabled; unchanged committed content can skip disk writes while snapshot/fingerprint work still runs. No per-frame diagnostic disk writes occur.",
            Checks, Cases = PresentationCases, StaticPassThrough = PresentationIdle,
            Limitation = "Counts and completion intervals end when UpdateLayeredWindowIndirect returns. Native submission rate is not DWM composition rate or physical display exposure. The tile is on the current physical desktop, not a physical 1080p-monitor, multi-monitor or all-hardware result. Opt-in timing collection adds measurement overhead. No live desktop pixels are sampled or retained."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Presentation throughput evidence: " + PresentationReport);
    }

    private sealed class PresentationRunState
    {
        internal long StartedTimestamp;
        internal int SequenceBase;
        internal bool ObserveMoves = true;
        private string? failure;
        internal string? Failure => Volatile.Read(ref failure);
        internal void Abort(string reason) => Interlocked.CompareExchange(ref failure, reason, null);
    }
    private sealed record PresentationInjectedMove(int Sequence, long ScheduledTimestamp, long BeforeTimestamp, long AfterTimestamp, int X, int Y);
    private sealed record PresentationObservedMove(int Sequence, long Timestamp, double X, double Y);
    private sealed record PresentationPointerDecision(long Timestamp, OverlayPoint Position, OverlayPoint PreviousObservedPosition,
        ulong LaterExtraInfo, int DecodedSequence, int NativeEntryIndex, long ProducerStartedTimestamp, string Decision)
    {
        public string LaterExtraInfoHex => $"0x{LaterExtraInfo:X16}";
    }
    private sealed record ProducerResult(long StartedTimestamp, long CompletedTimestamp, int RequestedMovesPerSecond,
        int SkippedScheduleSlots, IReadOnlyList<PresentationInjectedMove> Samples,
        [property: System.Text.Json.Serialization.JsonIgnore] Native.Point LastPoint, string? Failure);
}
