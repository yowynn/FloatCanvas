using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Pixel-correlated production preview measurements in hidden owned HWNDs.
/// The input mailbox retains only the newest offered pointer, like coalesced mouse movement.</summary>
internal static class UnifiedPreviewPerformanceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int Moves = 80, WarmupMoves = 8, IntervalMilliseconds = 16;
    private const double RefreshMilliseconds = 1000d / 60;
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowBand(nint window, out uint band);

    internal static void RunBlockedOnly(Action<string, bool> check, bool enhanced = false)
    {
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-blocked-preview", Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", profile]);
        new UserSettings { Language = "en", PreferGpuAcceleration = false }.Save();
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        CheckBlocked(check, MeasureBlockedWorker(app, enhanced));
    }

    internal static void Run(Action<string, bool> check, bool enhanced = false)
    {
        string evidence = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "artifacts",
            "FloatCanvas-unified-preview-performance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        ApplicationPaths.Configure(["--profile", Path.Combine(evidence, "profile")]);
        new UserSettings { Language = "en", PreferGpuAcceleration = false }.Save();
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        nint foreground = GetForegroundWindow();
        var comparisons = new List<Comparison>();
        foreach (var dimensions in new[] { (Width: 1920, Height: 1080), (Width: 3840, Height: 2160) })
        for (int repeat = 1; repeat <= 3; repeat++)
        {
            // Alternate the order to avoid always giving the later path warmer allocator/OS state.
            Measurement baseline, candidate;
            if (repeat % 2 == 1)
            {
                baseline = Measure(app, dimensions.Width, dimensions.Height, candidate: false, enhanced);
                candidate = Measure(app, dimensions.Width, dimensions.Height, candidate: true, enhanced);
            }
            else
            {
                candidate = Measure(app, dimensions.Width, dimensions.Height, candidate: true, enhanced);
                baseline = Measure(app, dimensions.Width, dimensions.Height, candidate: false, enhanced);
            }
            var comparison = new Comparison(dimensions.Width, dimensions.Height, repeat, baseline, candidate);
            comparisons.Add(comparison);
            Console.WriteLine("UNIFIED_PREVIEW_COMPARISON " + JsonSerializer.Serialize(new
            {
                Backend = enhanced ? "enhanced-hidden-no-input-lease" : "desktop-hidden",
                comparison.Width, comparison.Height, comparison.Repeat,
                BaselineP95 = baseline.ContentLatency.P95, CandidateP95 = candidate.ContentLatency.P95,
                CandidateQueueP95 = candidate.QueueLatency.P95, candidate.ChangedContentFrames,
                candidate.NativeSubmissions, candidate.FullOutputEquivalentSubmissions,
                candidate.FirstContentMilliseconds, candidate.FirstMoveMilliseconds, candidate.FinalReleaseMilliseconds,
                candidate.MaxContentGapMilliseconds, candidate.SelectionOnlyWorkerFrames,
                BaselineOfferCadence = baseline.OfferedCadence, CandidateOfferCadence = candidate.OfferedCadence
            }));
        }
        var blocked = MeasureBlockedWorker(app, enhanced);
        string summary = Path.Combine(evidence, "summary.json");
        File.WriteAllText(summary, JsonSerializer.Serialize(new
        {
            SchemaVersion = 2, Backend = enhanced ? "enhanced-hidden-no-input-lease" : "desktop-hidden",
            CompletedAt = DateTimeOffset.Now, Environment = new
            {
                Runtime = RuntimeInformation.FrameworkDescription, ProcessId = Environment.ProcessId,
                ForegroundUnchanged = foreground == GetForegroundWindow()
            },
            Method = new
            {
                Moves, WarmupMoves, IntervalMilliseconds, Input = "Latest-value mailbox; at most one queued Dispatcher Input callback; no native input injection.",
                Producer = "Stopwatch absolute deadlines; Sleep(1) while more than 2 ms remain, then SpinWait; no global timer settings.",
                PredicateWait = "One DispatcherFrame with a Background DispatcherTimer; no UI busy loop.",
                PreviousMethod = "Relative Thread.Sleep(interval) plus repeatedly allocated DispatcherFrame/background callbacks. Historical measurements retain their original method and artifacts.",
                Baseline = "Same EditorController, SceneRenderer, OverlaySceneCache, selected rectangle and hidden native OverlayHost. Direct baseline renders the full output once per coalesced request.",
                ContentSignal = "Pure-red rectangle position observed from the actual host raster, correlated with offered/applied pointer IDs and recorded after successful native submission.",
                Scope = "Software offer/apply-to-native-submit timing. Not physical mouse-to-photon or measured display FPS. Foreground is observed only; concurrent user activity is not attributed to the fixture."
            }, comparisons, blocked
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("UNIFIED_PREVIEW_EVIDENCE " + summary);

        foreach (var comparison in comparisons)
        {
            string name = $"{comparison.Width}x{comparison.Height} repeat {comparison.Repeat}";
            var candidate = comparison.Candidate;
            check(name + " presents the exact final rectangle position and keeps owned windows hidden", candidate.FinalPixelsCorrect && candidate.OwnWindowsStayedHidden);
            check(name + " uses the requested native presentation backend without acquiring input",
                candidate.NativeBackendProven && candidate.InputLeaseNeverAcquired && comparison.Baseline.NativeBackendProven && comparison.Baseline.InputLeaseNeverAcquired);
            check(name + " produces enough changed content during continuous movement", candidate.ChangedContentFrames >= Moves / 4);
            check(name + " content P95 is within direct baseline plus one refresh", candidate.ContentLatency.P95 <= comparison.Baseline.ContentLatency.P95 + RefreshMilliseconds);
            check(name + " content P95 is at most 33 ms", candidate.ContentLatency.P95 <= 33);
            check(name + " final released content is submitted within 33 ms", candidate.FinalReleaseMilliseconds <= 33);
            check(name + " input does not develop an increasing queue", candidate.QueueLatency.P95 <= RefreshMilliseconds && candidate.MaxQueuedInputCallbacks <= 1);
            check(name + " selection-only changes do not start the worker", candidate.SelectionOnlyWorkerFrames == 0);
            check(name + " local motion does not repeatedly submit whole outputs", candidate.FullOutputEquivalentSubmissions < candidate.NativeSubmissions / 4d);
            check(name + " content pixels carry matching production input/frame stamps and ordered stage timestamps",
                candidate.Samples.Where(sample => sample.Sequence > WarmupMoves && sample.SubmittedAt != 0).All(sample =>
                    sample.FrameSequence > 0 && sample.NativeContentSequence == sample.FrameSequence &&
                    sample.InputSequence > 0 && sample.NativeInputSequence == sample.InputSequence && sample.NativeControlsSequence > 0 &&
                    sample.AppliedAt <= sample.CapturedTimestamp && sample.CapturedTimestamp <= sample.RenderStartedTimestamp &&
                    sample.RenderStartedTimestamp <= sample.RenderCompletedTimestamp && sample.RenderCompletedTimestamp <= sample.SubmittedAt));
        }
        CheckBlocked(check, blocked);
    }

    private static void CheckBlocked(Action<string, bool> check, BlockedMeasurement blocked)
    {
        check("500 ms raster block keeps input dispatch P95 within one refresh", blocked.DispatchDelay.P95 <= RefreshMilliseconds);
        check("500 ms raster block keeps every input dispatch delay below 50 ms", blocked.DispatchDelay.Max <= 50);
        check("500 ms raster block keeps input dispatch gaps below 50 ms", blocked.DispatchGaps.Max <= 50);
        check("500 ms raster block keeps operation guides responsive", blocked.GuidesMovedWhileWorkerBlocked);
        check("500 ms raster block preserves the final released content", blocked.FinalPixelsCorrect);
        check("performance fixtures never show native input or display windows", blocked.OwnWindowsStayedHidden);
        check("blocked fixture uses the requested backend without requesting an input lease", blocked.NativeBackendProven && blocked.InputLeaseNeverAcquired);
    }

    private static Measurement Measure(Application app, int width, int height, bool candidate, bool enhanced)
    {
        using var fixture = new Fixture(app, width, height, candidate, enhanced);
        var tracking = new Tracking(fixture, Moves);
        fixture.Host.Draw += tracking.Observe;
        fixture.Host.FrameSubmitted += tracking.Submitted;
        try
        {
            long initial = Stopwatch.GetTimestamp(); tracking.Samples[0].OfferedAt = tracking.Samples[0].AppliedAt = initial;
            fixture.Initialize(); fixture.Until(() => tracking.LastPainted == 0 && !fixture.Pending && !fixture.Busy);
            fixture.Send(OverlayPointerKind.Down, 150); fixture.Settle();
            using (var source = new LatestPointerSource(app.Dispatcher, Moves, IntervalMilliseconds, tracking.Samples, sequence =>
            {
                tracking.Applied = sequence;
                fixture.Send(OverlayPointerKind.Move, 150 + sequence * 3);
                if (sequence == Moves)
                {
                    // Release at the last applied input, not at the later predicate timer tick.
                    tracking.ReleasedAt = Stopwatch.GetTimestamp();
                    fixture.Send(OverlayPointerKind.Up, 150 + Moves * 3);
                }
            }))
            {
                source.Start();
                fixture.Until(() => source.Done && tracking.Applied == Moves);
                source.ThrowFailure();
                fixture.Until(() => tracking.LastPainted == Moves && !fixture.Pending && !fixture.Busy);
                tracking.MaxQueuedInputs = source.MaxQueuedCallbacks;
            }
            bool final = Math.Abs(fixture.Editor.Session.Document.Elements.Single().Transform.OffsetX - (100 + Moves * 3)) < .01 &&
                tracking.LastPainted == Moves;
            long before = fixture.Preview?.CompletedFrames ?? 0;
            fixture.Editor.Session.SetSelection([]); fixture.Settle();
            long selectionFrames = (fixture.Preview?.CompletedFrames ?? 0) - before;
            return tracking.Finish(final, selectionFrames);
        }
        finally
        {
            fixture.Host.Draw -= tracking.Observe;
            fixture.Host.FrameSubmitted -= tracking.Submitted;
        }
    }

    private static BlockedMeasurement MeasureBlockedWorker(Application app, bool enhanced)
    {
        using var fixture = new Fixture(app, 3840, 2160, candidate: true, enhanced);
        fixture.Initialize(); fixture.Settle();
        fixture.Send(OverlayPointerKind.Down, 150); fixture.Settle();
        var preview = fixture.Preview!;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int first = 0;
        var renderField = typeof(CanvasPreviewPump).GetField("renderOverride", Private)!;
        var originalRender = renderField.GetValue(preview);
        // Keep the production pump, presented frame and monotonic sequence intact.
        // Replacing the pump here would remove the old frame while it is supposed
        // to remain on screen, causing the presenter to suspend stale-frame input.
        renderField.SetValue(preview, new Func<CanvasPreviewSnapshot, CanvasPreviewFrame>(snapshot =>
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                started.Set();
                if (!release.Wait(5000)) throw new TimeoutException("The owned blocked renderer was not released.");
            }
            using var renderer = new CanvasPreviewRenderer();
            return renderer.Render(snapshot);
        }));
        var delays = new List<double>(); var gaps = new List<double>();
        long lastDispatch = 0;
        int latestApplied = 0, lastPainted = -1;
        bool guideMoved = false, ownWindowsHidden = true, noInputLease = true;
        void Observe(SKSurface surface)
        {
            using var pixels = surface.PeekPixels();
            lastPainted = ReadRectangle(pixels);
            int edge = 100 + latestApplied * 3;
            var color = pixels.GetPixelColor(edge, 130);
            guideMoved |= preview.Busy && latestApplied > 3 && color.Blue > 140 && color.Blue > color.Red + 40;
            ownWindowsHidden &= Hidden(fixture.Host);
            noInputLease &= NoInputLease(fixture.Host);
        }
        fixture.Host.Draw += Observe;
        const int blockedMoves = 64;
        var samples = Enumerable.Range(0, blockedMoves + 1).Select(sequence => new InputSample { Sequence = sequence }).ToArray();
        try
        {
            preview.Request(); fixture.Until(() => started.IsSet);
            using var source = new LatestPointerSource(app.Dispatcher, blockedMoves, 8, samples, sequence =>
            {
                long now = Stopwatch.GetTimestamp();
                delays.Add(Stopwatch.GetElapsedTime(samples[sequence].OfferedAt, now).TotalMilliseconds);
                if (lastDispatch != 0) gaps.Add(Stopwatch.GetElapsedTime(lastDispatch, now).TotalMilliseconds);
                lastDispatch = now; latestApplied = sequence;
                fixture.Send(OverlayPointerKind.Move, 150 + sequence * 3);
                if (sequence == blockedMoves) fixture.Send(OverlayPointerKind.Up, 150 + blockedMoves * 3);
            });
            var releaser = new Thread(() => { Thread.Sleep(500); release.Set(); }) { IsBackground = true };
            releaser.Start(); source.Start();
            fixture.Until(() => source.Done && latestApplied == blockedMoves);
            source.ThrowFailure();
            fixture.Until(() => release.IsSet && lastPainted == blockedMoves && !fixture.Pending && !fixture.Busy);
            releaser.Join();
            var result = new BlockedMeasurement(Statistics.Of(delays), Statistics.Of(gaps), guideMoved,
                lastPainted == blockedMoves && Math.Abs(fixture.Editor.Session.Document.Elements.Single().Transform.OffsetX - (100 + blockedMoves * 3)) < .01,
                ownWindowsHidden && Hidden(fixture.Host), fixture.NativeBackendProven, noInputLease && NoInputLease(fixture.Host), Cadence.Of(samples));
            Console.WriteLine("UNIFIED_PREVIEW_BLOCKED " + JsonSerializer.Serialize(result));
            return result;
        }
        finally
        {
            release.Set(); fixture.Host.Draw -= Observe;
            fixture.Until(() => !fixture.Busy);
            renderField.SetValue(preview, originalRender);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Application app;
        private readonly SceneRenderer? directRenderer;
        private readonly OverlaySceneCache? directCache;
        private readonly Action<OverlayPointerEvent> sendPointer;
        private DispatcherOperation? directOperation;
        internal Fixture(Application app, int width, int height, bool candidate, bool enhanced)
        {
            this.app = app;
            var bounds = new PixelBounds(0, 0, width, height);
            Func<IReadOnlyList<MonitorInfo>> provider = () => [new("hidden-unified-preview", bounds, bounds, 96, 96, true)];
            Host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
                [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            if (enhanced)
            {
                try
                {
                    // This initializes hidden enhanced ownership only. No Begin/TryEnterEditing,
                    // hotkey registration, native injection or input delegation is requested.
                    Host.EnableSystemInput();
                    NativeBackendProven = Host.UsesSystemInput && Host.SystemWindowOwner != 0 &&
                        !IsWindowVisible(Host.SystemWindowOwner) && Host.Diagnostics.All(window =>
                            GetWindowBand(window.DisplayHandle, out uint displayBand) && displayBand == 16 &&
                            GetWindowBand(window.InputHandle, out uint inputBand) && inputBand == 16) && NoInputLease(Host);
                    if (!NativeBackendProven) throw new InvalidOperationException("Hidden enhanced presentation ownership was not established without an input lease.");
                }
                catch (Exception error)
                {
                    Host.Dispose();
                    Console.WriteLine("UNIFIED_ENHANCED_CAPABILITY " + JsonSerializer.Serialize(new
                    { Supported = false, Error = error.GetType().FullName, error.HResult, error.Message, AttemptedInputAcquisition = false }));
                    throw;
                }
            }
            else NativeBackendProven = !Host.UsesSystemInput;
            if (candidate)
            {
                Session = new DesktopSession(app, Host);
                Field<System.Windows.Forms.NotifyIcon>(Session, "_tray").Visible = false;
                Editor = Field<EditorController>(Session, "_editor");
                Preview!.Failed += error => Failure = error;
            }
            else
            {
                Editor = new(new EditorSession(new SceneDocument { Viewport = new(0, 0, width, height) }));
                directRenderer = new(); directCache = new(directRenderer);
                Host.Draw += DrawDirect; Editor.Changed += RequestDirect;
                Host.Pointer += Editor.Pointer;
            }
            sendPointer = (Action<OverlayPointerEvent>)typeof(OverlayHost).GetField("Pointer", Private)!.GetValue(Host)!;
            typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(Host, true);
            typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(Host, true);
            Host.ApplyRenderPreference(false); Host.SetGpuWorkload(false);
            Editor.Tool = EditorTool.Select;
        }
        internal OverlayHost Host { get; }
        internal bool NativeBackendProven { get; }
        internal DesktopSession? Session { get; }
        internal EditorController Editor { get; }
        internal CanvasPreviewPump? Preview => Session is null ? null : Field<CanvasPreviewPump>(Session, "_canvasPreview");
        internal Exception? Failure { get; set; }
        internal bool Busy => Preview?.Busy ?? false;
        internal bool Pending => directOperation?.Status == DispatcherOperationStatus.Pending || Session is not null &&
            (PendingOperation(Session, "_renderOperation") || PendingOperation(Session, "_presentOperation") || !Editor.PendingPicking.IsCompleted);
        internal void Initialize()
        {
            Editor.Session.Execute("unified preview rectangle fixture", doc => doc.Elements = [new()
            {
                Kind = ElementKind.Rectangle, Width = 100, Height = 60, Transform = Affine2D.Translation(100, 100),
                Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 }
            }]);
            Editor.Session.SelectAll();
        }
        internal void Send(OverlayPointerKind kind, double x) => sendPointer(new(kind, new(x, 130), OverlayMouseButton.Left, OverlayModifiers.None));
        internal void Settle() => Until(() => !Pending && !Busy);
        internal void Until(Func<bool> predicate)
        {
            if (Failure is not null) throw new InvalidOperationException("Owned preview failed.", Failure);
            if (predicate()) return;
            var timer = Stopwatch.StartNew();
            var frame = new DispatcherFrame();
            Exception? failure = null;
            var poll = new DispatcherTimer(DispatcherPriority.Background, app.Dispatcher) { Interval = TimeSpan.FromMilliseconds(1) };
            EventHandler tick = (_, _) =>
            {
                try
                {
                    if (Failure is not null) throw new InvalidOperationException("Owned preview failed.", Failure);
                    if (timer.ElapsedMilliseconds > 15000) throw new TimeoutException("Unified preview fixture did not settle.");
                    if (predicate()) frame.Continue = false;
                }
                catch (Exception error) { failure = error; frame.Continue = false; }
            };
            poll.Tick += tick; poll.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { poll.Stop(); poll.Tick -= tick; }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        private void RequestDirect()
        {
            if (directOperation?.Status == DispatcherOperationStatus.Pending) return;
            directOperation = app.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => { directOperation = null; Host.Render(); });
        }
        private void DrawDirect(SKSurface surface)
        {
            var span = Editor.RasterPreview;
            directCache!.DrawOnClearedRasterTarget(surface, Editor.Session.Document, Editor.Session.Assets,
                Editor.Session.SceneRevision, Editor.Session.TransactionId, span.First, span.Last, span.BypassCache);
            if (Editor.SelectionScreenCorners is not { Count: 4 } corners) return;
            using var paint = new SKPaint { Color = new SKColor(35, 131, 226), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            for (int index = 0; index < corners.Count; index++)
            {
                var from = corners[index]; var to = corners[(index + 1) % corners.Count];
                surface.Canvas.DrawLine((float)from.X, (float)from.Y, (float)to.X, (float)to.Y, paint);
                surface.Canvas.DrawRect((float)from.X - 5, (float)from.Y - 5, 10, 10, paint);
            }
            if (Editor.SelectionScreenRotationAnchor is { } anchor && Editor.SelectionScreenRotationHandle is { } handle)
            {
                surface.Canvas.DrawLine((float)anchor.X, (float)anchor.Y, (float)handle.X, (float)handle.Y, paint);
                surface.Canvas.DrawCircle((float)handle.X, (float)handle.Y, 6, paint);
            }
        }
        public void Dispose()
        {
            directOperation?.Abort();
            if (Session is not null) Session.Dispose();
            else { Editor.Changed -= RequestDirect; Editor.DetachSession(); Host.Dispose(); }
            directCache?.Dispose(); directRenderer?.Dispose();
        }
    }

    private sealed class LatestPointerSource(Dispatcher dispatcher, int count, int interval, InputSample[] samples, Action<int> apply) : IDisposable
    {
        private int latest, queued, stopping, done;
        private Thread? thread;
        private Exception? failure;
        internal bool Done => Volatile.Read(ref done) != 0;
        internal int MaxQueuedCallbacks { get; private set; }
        internal void Start()
        {
            thread = new(() =>
            {
                try
                {
                    long origin = Stopwatch.GetTimestamp();
                    for (int sequence = 1; sequence <= count && Volatile.Read(ref stopping) == 0; sequence++)
                    {
                        long deadline = origin + (long)(sequence - 1) * interval * Stopwatch.Frequency / 1000;
                        while (Volatile.Read(ref stopping) == 0)
                        {
                            double remaining = (deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency;
                            if (remaining <= 0) break;
                            if (remaining > 2) Thread.Sleep(1);
                            else Thread.SpinWait(64);
                        }
                        if (Volatile.Read(ref stopping) != 0) break;
                        samples[sequence].OfferedAt = Stopwatch.GetTimestamp();
                        Volatile.Write(ref latest, sequence); Queue();
                    }
                }
                catch (Exception error) { failure = error; }
                finally { Volatile.Write(ref done, 1); }
            }) { IsBackground = true, Name = "Owned preview pointer source" };
            thread.Start();
        }
        private void Queue()
        {
            if (Volatile.Read(ref stopping) != 0 || Interlocked.CompareExchange(ref queued, 1, 0) != 0) return;
            MaxQueuedCallbacks = 1;
            dispatcher.BeginInvoke(DispatcherPriority.Input, Drain);
        }
        private void Drain()
        {
            int sequence = Volatile.Read(ref latest);
            try
            {
                if (Volatile.Read(ref stopping) == 0)
                { samples[sequence].AppliedAt = Stopwatch.GetTimestamp(); apply(sequence); }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                Interlocked.Exchange(ref queued, 0);
                if (Volatile.Read(ref latest) != sequence) Queue();
            }
        }
        internal void ThrowFailure() { if (failure is not null) throw new InvalidOperationException("Owned pointer source failed.", failure); }
        public void Dispose() { Volatile.Write(ref stopping, 1); thread?.Join(); }
    }

    private sealed class Tracking(Fixture fixture, int moves)
    {
        internal readonly InputSample[] Samples = Enumerable.Range(0, moves + 1).Select(sequence => new InputSample { Sequence = sequence }).ToArray();
        internal int Applied, LastPainted = -1, MaxQueuedInputs;
        internal long ReleasedAt;
        private int observed = -1;
        private long previousChanged;
        private readonly List<double> contentGaps = [], renderTimes = [], presentTimes = [];
        private long submissions, pixels;
        private bool hidden = true, noInputLease = true;
        internal void Observe(SKSurface surface)
        {
            using var current = surface.PeekPixels(); observed = ReadRectangle(current);
            hidden &= Hidden(fixture.Host);
            noInputLease &= NoInputLease(fixture.Host);
        }
        internal void Submitted(OverlayFrameTiming timing)
        {
            if (!timing.Succeeded || timing.Outputs.Count == 0) return;
            submissions += timing.Outputs.Count; pixels += fixture.Host.LastRenderedPixelCount;
            renderTimes.Add(Stopwatch.GetElapsedTime(timing.StartedTimestamp, timing.CompletedTimestamp).TotalMilliseconds);
            presentTimes.Add(timing.Outputs.Sum(output => output.PresentMilliseconds));
            LastPainted = observed;
            if (observed < 0 || observed > moves || Samples[observed].AppliedAt == 0 || Samples[observed].SubmittedAt != 0) return;
            var sample = Samples[observed]; sample.SubmittedAt = timing.CompletedTimestamp;
            if (previousChanged != 0) contentGaps.Add(Stopwatch.GetElapsedTime(previousChanged, timing.CompletedTimestamp).TotalMilliseconds);
            previousChanged = timing.CompletedTimestamp;
            if (fixture.Preview?.Frame is { } frame)
            {
                sample.FrameSequence = Integer(frame, "Sequence"); sample.ContentRevision = Integer(frame, "ContentRevision");
                sample.InputSequence = Integer(frame, "InputSequence"); sample.CaptureMilliseconds = Number(frame, "CaptureMilliseconds");
                sample.CapturedTimestamp = Integer(frame, "CapturedTimestamp"); sample.RenderStartedTimestamp = Integer(frame, "RenderStartedTimestamp");
                sample.RenderCompletedTimestamp = Integer(frame, "RenderCompletedTimestamp");
                sample.NativeContentSequence = timing.ContentSequence; sample.NativeInputSequence = timing.InputSequence;
                sample.NativeControlsSequence = timing.ControlsSequence;
                sample.RenderMilliseconds = frame.RenderMilliseconds; sample.PublishedPixelBytes = frame.Pixels.CopiedBytes;
            }
        }
        internal Measurement Finish(bool final, long selectionFrames)
        {
            var changed = Samples.Where(sample => sample.Sequence > WarmupMoves && sample.SubmittedAt != 0).ToArray();
            var queued = Samples.Where(sample => sample.Sequence > WarmupMoves && sample.AppliedAt != 0).ToArray();
            return new(Statistics.Of(changed.Select(sample => Milliseconds(sample.OfferedAt, sample.SubmittedAt))),
                Statistics.Of(queued.Select(sample => Milliseconds(sample.OfferedAt, sample.AppliedAt))),
                Statistics.Of(renderTimes), Statistics.Of(presentTimes), changed.Length, submissions,
                pixels / (double)(fixture.Host.Monitors[0].Bounds.Width * (long)fixture.Host.Monitors[0].Bounds.Height),
                Milliseconds(Samples[0].OfferedAt, Samples[0].SubmittedAt),
                Samples.Skip(1).Where(sample => sample.SubmittedAt != 0).Select(sample => Milliseconds(sample.OfferedAt, sample.SubmittedAt)).FirstOrDefault(-1),
                Math.Max(0, Milliseconds(ReleasedAt, Samples[moves].SubmittedAt)),
                contentGaps.Count == 0 ? -1 : contentGaps.Max(), selectionFrames, MaxQueuedInputs,
                final, hidden && Hidden(fixture.Host), fixture.NativeBackendProven, noInputLease && NoInputLease(fixture.Host), Cadence.Of(Samples), Samples);
        }
    }

    private static int ReadRectangle(SKPixmap pixels)
    {
        for (int x = 90; x < Math.Min(850, pixels.Width); x++)
        {
            var color = pixels.GetPixelColor(x, 130);
            if (color.Alpha > 240 && color.Red > 240 && color.Green < 10 && color.Blue < 10)
                return (int)Math.Round((x - 100) / 3d);
        }
        return -1;
    }
    private static bool Hidden(OverlayHost host) => (host.SystemWindowOwner == 0 || !IsWindowVisible(host.SystemWindowOwner)) &&
        host.Diagnostics.All(window => !IsWindowVisible(window.InputHandle) && !IsWindowVisible(window.DisplayHandle));
    private static bool NoInputLease(OverlayHost host)
    {
        if (!host.UsesSystemInput) return true;
        var system = typeof(OverlayHost).GetField("_systemInput", Private)!.GetValue(host)!;
        return !(bool)system.GetType().GetProperty("Requested", Private)!.GetValue(system)! &&
            !(bool)system.GetType().GetProperty("Acknowledged", Private)!.GetValue(system)! && !host.HasEditingInput;
    }
    private static bool PendingOperation(object target, string name) => target.GetType().GetField(name, Private)?.GetValue(target) is DispatcherOperation { Status: DispatcherOperationStatus.Pending };
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static double Number(object target, string name) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target) is { } value ? Convert.ToDouble(value) : 0;
    private static long Integer(object target, string name) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target) is { } value ? Convert.ToInt64(value) : 0;
    private static double Milliseconds(long start, long end) => start == 0 || end == 0 ? -1 : Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;

    private sealed class InputSample
    {
        public int Sequence { get; init; }
        public long OfferedAt { get; set; }
        public long AppliedAt { get; set; }
        public long SubmittedAt { get; set; }
        public long FrameSequence { get; set; }
        public long ContentRevision { get; set; }
        public long InputSequence { get; set; }
        public long CapturedTimestamp { get; set; }
        public long RenderStartedTimestamp { get; set; }
        public long RenderCompletedTimestamp { get; set; }
        public long NativeContentSequence { get; set; }
        public long NativeInputSequence { get; set; }
        public long NativeControlsSequence { get; set; }
        public double CaptureMilliseconds { get; set; }
        public double RenderMilliseconds { get; set; }
        public long PublishedPixelBytes { get; set; }
    }
    private sealed record Statistics(int Count, double P50, double P95, double Max)
    {
        internal static Statistics Of(IEnumerable<double> input)
        {
            var values = input.Order().ToArray();
            return values.Length == 0 ? new(0, -1, -1, -1) : new(values.Length, values[(int)Math.Ceiling(values.Length * .5) - 1], values[(int)Math.Ceiling(values.Length * .95) - 1], values[^1]);
        }
    }
    private sealed record Cadence(int OfferedInputs, Statistics IntervalsMilliseconds, double OverallHertz)
    {
        internal static Cadence Of(InputSample[] samples)
        {
            var offered = samples.Where(sample => sample.Sequence > 0 && sample.OfferedAt != 0).ToArray();
            var intervals = offered.Zip(offered.Skip(1), (first, next) => Milliseconds(first.OfferedAt, next.OfferedAt)).ToArray();
            double elapsed = offered.Length > 1 ? Milliseconds(offered[0].OfferedAt, offered[^1].OfferedAt) : 0;
            return new(offered.Length, Statistics.Of(intervals), elapsed > 0 ? (offered.Length - 1) * 1000d / elapsed : 0);
        }
    }
    private sealed record Measurement(Statistics ContentLatency, Statistics QueueLatency, Statistics UiRender, Statistics NativePresent,
        int ChangedContentFrames, long NativeSubmissions, double FullOutputEquivalentSubmissions, double FirstContentMilliseconds,
        double FirstMoveMilliseconds, double FinalReleaseMilliseconds, double MaxContentGapMilliseconds, long SelectionOnlyWorkerFrames,
        int MaxQueuedInputCallbacks, bool FinalPixelsCorrect, bool OwnWindowsStayedHidden, bool NativeBackendProven,
        bool InputLeaseNeverAcquired, Cadence OfferedCadence, InputSample[] Samples);
    private sealed record Comparison(int Width, int Height, int Repeat, Measurement Baseline, Measurement Candidate);
    private sealed record BlockedMeasurement(Statistics DispatchDelay, Statistics DispatchGaps, bool GuidesMovedWhileWorkerBlocked,
        bool FinalPixelsCorrect, bool OwnWindowsStayedHidden, bool NativeBackendProven, bool InputLeaseNeverAcquired, Cadence OfferedCadence);
}
