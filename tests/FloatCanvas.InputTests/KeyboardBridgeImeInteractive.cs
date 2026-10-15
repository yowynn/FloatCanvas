using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.PlatformTests;
using FloatCanvas.Windows;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

internal static partial class Program
{
    private static bool KeyboardBridgeImeInteractive;
    private static string KeyboardBridgeImeReport => Path.Combine(Root, "artifacts", "e2e", "keyboard-bridge-ime-interactive.json");
    private static readonly List<object> ImeChecks = [], ImeEvents = [];
    private static readonly List<ImePhaseObservation> ImePhases = [];
    private static readonly List<BridgePacketObservation> ImePackets = [];
    private static object[] ImeWpfEvents = [];
    private static object? ImeHookDiagnostics, ImeFinalText, ImeLanguageRestoration;
    private static ObserverReady? ImeObserver;
    private static string[] ImeAvailableLanguages = [];
    private static string? ImePreviousLanguage, ImeFinishReason, ImeLanguageSetupError;
    private static bool ImeRawPositiveBefore, ImeRawPositiveAfter;
    private static Exception? ImePumpFailure;

    private static async Task RunKeyboardBridgeImeInteractive(Wpf.Application app)
    {
        // No DesktopSession, global hotkey or input-language installation is involved. The
        // coordinator supplies actual physical letters/candidate selection in this own target.
        using var observer = await KeyboardObserverClient.StartAsync();
        ImeObserver = observer.Identity;
        await ActivateKeyboardFixture(observer);
        await observer.ResetAsync(); await PulseKeyboardX(observer, editing: false);
        var positiveBefore = await observer.SnapshotAsync(); ImeRawPositiveBefore = RawPositive(positiveBefore);
        ImeEvents.Add(new { Kind = "PassPositiveBefore", Snapshot = positiveBefore });
        ImeRequire(positiveBefore.WmDown > 0 && positiveBefore.WmUp > 0 && positiveBefore.AsyncPresses > 0 && ImeRawPositiveBefore,
            "The independent child's ordinary/raw/async X channels have a real positive control before interactive IME testing");

        var language = InputLanguageManager.Current;
        CultureInfo previous = language.CurrentInputLanguage; ImePreviousLanguage = previous.Name;
        ImeAvailableLanguages = language.AvailableInputLanguages.Cast<CultureInfo>().Select(c => c.Name).Distinct().ToArray();
        using var fixture = new KeyboardBridgeImeFixture(BridgeNotifyMessage);
        var bridge = new PrivateKeyboardBridge();
        KeyboardInputIsolation? isolation = null;
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        var finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activePhase = new ImePhaseObservation(1, "Baseline"); ImePhases.Add(activePhase);
        long currentLease = 0;
        bool transitionBusy = false;
        var compositions = new Dictionary<long, ImeCompositionOwnership>();
        InputMethodState? previousImeState = null;
        try { previousImeState = InputMethod.Current.ImeState; }
        catch (Exception ex) { ImeEvents.Add(new { Kind = "PriorImeStateUnavailable", Exception = ex.GetType().Name }); }

        void LanguageChanged(object sender, InputLanguageEventArgs e) => ImeEvents.Add(new
        { Kind = "InputLanguageChanged", At = Stopwatch.GetTimestamp(), activePhase.Id, activePhase.Mode, NewLanguage = e.NewLanguage.Name });
        language.InputLanguageChanged += LanguageChanged;
        fixture.UiProgress = () => isolation?.Heartbeat();
        fixture.CompositionObserved = observation =>
        {
            if (observation.Kind == "Start")
                compositions[observation.Id] = new ImeCompositionOwnership(transitionBusy ? null : activePhase, currentLease);
            if (!compositions.TryGetValue(observation.Id, out var ownership) || ownership.Phase is not { } phase) return;
            if (phase.EndedAt.HasValue && observation.At > phase.EndedAt.Value)
            {
                ImeEvents.Add(new { Kind = "CompositionOutsideItsOriginPhase", observation.Id, observation.At, OriginPhase = phase.Id });
                return;
            }
            switch (observation.Kind)
            {
                case "Start":
                    phase.CompositionStarts++;
                    if (phase.Mode == "Lease" && isolation?.IsLeaseActive == true) phase.ActiveLeaseCompositionStarts++;
                    break;
                case "Update": phase.CompositionUpdates++; break;
                case "Commit":
                    ownership.CommitAt = observation.At;
                    ownership.LeaseActiveAtCommit = phase.Mode == "Lease" && isolation?.IsLeaseActive == true && ownership.StartLeaseId == currentLease;
                    phase.Commits++;
                    bool hasHan = observation.Text.Any(c => c is >= '\u3400' and <= '\u9FFF');
                    phase.HasHanCommit |= hasHan;
                    if (ownership.LeaseActiveAtCommit) phase.HasActiveLeaseHanCommit |= hasHan;
                    break;
            }
        };
        fixture.SettledCommitObserved = settled =>
        {
            if (!compositions.TryGetValue(settled.Id, out var ownership) || ownership.Phase is not { } phase || ownership.CommitAt != settled.CommitAt) return;
            phase.SettledCommitObservations++;
            bool hasAddedHan = settled.RejectedReason is null && settled.AddedHan.Length != 0;
            phase.HasSettledHanCommit |= hasAddedHan;
            if (ownership.LeaseActiveAtCommit) phase.HasActiveLeaseSettledHanCommit |= hasAddedHan;
            ImeEvents.Add(new { Kind = "SettledCommitAssignedToOriginPhase", CompositionId = settled.Id, OriginPhase = phase.Id,
                OriginMode = phase.Mode, settled.CommitAt, ownership.LeaseActiveAtCommit, settled.RejectedReason,
                AddedHan = KeyboardBridgeImeFixture.ReportText(settled.AddedHan), CurrentPhase = activePhase.Id });
        };
        fixture.FinishRequested = reason => finished.TrySetResult(reason);
        void ProcessNotification()
        {
            if (isolation is null || !isolation.TryDequeue(out var packet) || packet is null) return;
            var observation = new BridgePacketObservation(packet); ImePackets.Add(observation);
            bool completed = false;
            void CompleteOnce()
            {
                if (completed) return;
                completed = true; isolation.CompletePacket(packet.Sequence); observation.CompletedAt = Stopwatch.GetTimestamp();
            }
            try
            {
                isolation.Heartbeat(); // Actual UI queue progress, never hook-thread self-renewal.
                if (packet.LeaseId != currentLease)
                {
                    observation.Delivered = false;
                    ImeEvents.Add(new { Kind = "StaleLeasePacketSkipped", packet.Sequence, packet.LeaseId, CurrentLeaseId = currentLease });
                    return;
                }
                if (packet.Kind == KeyboardInputPacketKind.Key)
                {
                    if (packet.LeaseId == currentLease && packet.VirtualKey == 0x58 && !packet.IsKeyUp && !packet.IsLocalBalance)
                        activePhase.IsolatedXKeyDownPackets++;
                    observation.Delivered = bridge.Process(packet, CompleteOnce);
                }
                else if (packet.Kind == KeyboardInputPacketKind.LeaseEnded && packet.LeaseId == currentLease)
                {
                    ImeEvents.Add(new { Kind = "LeaseEnded", At = Stopwatch.GetTimestamp(), packet.LeaseId, Reason = packet.EndReason?.ToString() });
                    activePhase.LeaseEndReason = packet.EndReason?.ToString();
                    fixture.SetLeaseEnded(packet.EndReason?.ToString() ?? "Unknown");
                    if (packet.EndReason == KeyboardLeaseEndReason.EmergencyEscape) finished.TrySetResult("CtrlAltEsc");
                }
            }
            catch (Exception ex)
            {
                ImePumpFailure = ex; isolation.EndLease(KeyboardLeaseEndReason.HookFailure); finished.TrySetResult("BridgePumpFailure");
            }
            finally { CompleteOnce(); }
        }
        fixture.Notification = ProcessNotification;
        fixture.ToggleRequested = async () =>
        {
            if (transitionBusy || finished.Task.IsCompleted) return;
            transitionBusy = true;
            fixture.SetTransition(true);
            try
            {
                // The old phase's independent counters are frozen before resetting them for the
                // next phase. The same TextBox and language are retained, without assigning Text.
                await fixture.WaitForSettledCommitsAsync();
                activePhase.Observer = await observer.SnapshotAsync(); activePhase.EndedAt = Stopwatch.GetTimestamp();
                bool enter = !isolation!.IsLeaseActive;
                if (!enter) { isolation.EndLease(); await Task.Delay(60); }
                await observer.ResetAsync();
                fixture.Input.Focus();
                if (enter)
                {
                    nint foreground = Native.GetForegroundWindow(); KeyboardNative.GetWindowThreadProcessId(foreground, out uint pid);
                    if (foreground != fixture.Handle || pid != Environment.ProcessId || !fixture.HasTextFocus)
                        throw new InvalidOperationException("The exact own IME window/TextBox must own focus before its lease begins.");
                    currentLease = isolation.BeginLease(HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide);
                }
                activePhase = new ImePhaseObservation(ImePhases.Count + 1, enter ? "Lease" : "Baseline"); ImePhases.Add(activePhase);
                fixture.SetPhase(activePhase.Id, activePhase.Mode);
                await fixture.RequestImeStateAsync(InputMethodState.On);
                ImeEvents.Add(new { Kind = "PhaseChanged", At = Stopwatch.GetTimestamp(), activePhase.Id, activePhase.Mode,
                    isolation.IsLeaseActive, LeaseId = currentLease, Snapshot = fixture.Snapshot() });
            }
            catch (Exception ex) { ImePumpFailure = ex; isolation?.EndLease(KeyboardLeaseEndReason.HookFailure); finished.TrySetResult("PhaseTransitionFailure"); }
            finally { transitionBusy = false; fixture.SetTransition(false); }
        };
        try
        {
            isolation = await KeyboardInputIsolation.StartAsync(fixture.Handle, BridgeNotifyMessage);
            heartbeat.Tick += (_, _) => { isolation.Heartbeat(); fixture.RefreshImeStatus(); }; heartbeat.Start();
            fixture.ActivateText();
            long activationStart = Stopwatch.GetTimestamp();
            if (KeyboardWaitForActivation && (Native.GetForegroundWindow() != fixture.Handle || !fixture.HasTextFocus))
                Console.WriteLine($"WAITING_OWN_IME_FIXTURE: pid={Environment.ProcessId}; hwnd={fixture.Handle}; title=浮绘 · 自建 IME 基线与键盘桥验证; timeout=30s");
            while (Native.GetForegroundWindow() != fixture.Handle || !fixture.HasTextFocus)
            {
                if (!KeyboardWaitForActivation || Stopwatch.GetElapsedTime(activationStart).TotalSeconds > 30)
                    throw new InvalidOperationException("Own interactive IME fixture could not acquire exact text focus.");
                await Task.Delay(100);
            }
            try
            {
                var chinese = CultureInfo.GetCultureInfo("zh-CN");
                InputLanguageManager.SetInputLanguage(fixture.Input, chinese);
                if (ImeAvailableLanguages.Contains(chinese.Name, StringComparer.OrdinalIgnoreCase)) language.CurrentInputLanguage = chinese;
                else ImeLanguageSetupError = "zh-CN is not among the UI thread's available input languages; no language or TIP is installed by this probe.";
                await fixture.RequestImeStateAsync(InputMethodState.On);
            }
            catch (Exception ex) { ImeLanguageSetupError = ex.ToString(); }
            fixture.SetLanguageStatus(ImeLanguageSetupError ?? "请求 zh-CN；是否真正启用 TIP 必须先由无隔离基线的候选及组合事件验证。");
            await observer.ResetAsync(); fixture.SetPhase(1, "Baseline");
            ImeEvents.Add(new { Kind = "InteractiveReady", At = Stopwatch.GetTimestamp(), Snapshot = fixture.Snapshot(), ImeLanguageSetupError });
            Console.WriteLine($"KEYBOARD_BRIDGE_IME_READY: pid={Environment.ProcessId}; hwnd={fixture.Handle}; phase=Baseline; language={language.CurrentInputLanguage.Name}; timeout=300s; report={KeyboardBridgeImeReport}");
            try { ImeFinishReason = await finished.Task.WaitAsync(TimeSpan.FromSeconds(300)); }
            catch (TimeoutException) { ImeFinishReason = "Timeout300Seconds"; throw; }
            long transitionWait = Stopwatch.GetTimestamp();
            while (transitionBusy)
            {
                if (Stopwatch.GetElapsedTime(transitionWait).TotalSeconds > 2) throw new TimeoutException("The own IME phase transition did not finish before cleanup.");
                await Task.Delay(1);
            }
            await Task.Delay(120); // A real F12 keyup and pending composition messages may still be queued.
            await fixture.WaitForSettledCommitsAsync();
            activePhase.Observer = await observer.SnapshotAsync(); activePhase.EndedAt = Stopwatch.GetTimestamp();
            ImeFinalText = fixture.Snapshot();
            ImeRequire(ImePumpFailure is null, "Interactive baseline/lease processing has no private keyboard-bridge exception");
            ImeRequire(ImeFinishReason is "F12" or "SaveButton", "The coordinator explicitly finishes through this own window's F12 handler or save button");
            ImeRequire(ImePhases.Any(p => p.Mode == "Baseline") && ImePhases.Any(p => p.Mode == "Lease"),
                "The same real TextBox is observed in separate baseline and isolation phases");
        }
        finally
        {
            heartbeat.Stop(); isolation?.EndLease();
            await Task.Delay(80);
            if (isolation is not null)
            {
                isolation.Dispose();
                ImeHookDiagnostics = new { isolation.IsLeaseActive, isolation.SuppressedCount, isolation.TailSuppressedCount,
                    isolation.QueuedPacketCount, isolation.IsThreadAlive, isolation.LastWin32Error, Failure = isolation.Failure?.ToString() };
            }
            try
            {
                // Both changes are confined to this testing Dispatcher/context, never another
                // application's thread or a system input-language configuration.
                language.CurrentInputLanguage = previous;
                InputLanguageManager.SetInputLanguage(fixture.Input, previous);
                if (previousImeState.HasValue) InputMethod.Current.ImeState = previousImeState.Value;
                ImeLanguageRestoration = new { Restored = language.CurrentInputLanguage.Equals(previous), Requested = previous.Name,
                    Actual = language.CurrentInputLanguage.Name, PreviousImeState = previousImeState?.ToString(), ActualImeState = KeyboardBridgeImeFixture.ReadImeState() };
            }
            catch (Exception ex) { ImeLanguageRestoration = new { Restored = false, Error = ex.ToString() }; Failure = (Failure ?? "") + "\nIME language restore: " + ex; }
            language.InputLanguageChanged -= LanguageChanged;
            ImeWpfEvents = fixture.Events.ToArray(); fixture.Notification = null;
            if (fixture.Window.IsVisible) fixture.Window.Hide();
        }

        await ActivateKeyboardFixture(observer); await observer.ResetAsync(); await PulseKeyboardX(observer, editing: false);
        var positiveAfter = await observer.SnapshotAsync(); ImeRawPositiveAfter = RawPositive(positiveAfter);
        ImeEvents.Add(new { Kind = "PassPositiveAfter", Snapshot = positiveAfter });
        ImeRequire(positiveAfter.WmDown > 0 && positiveAfter.WmUp > 0 && positiveAfter.AsyncPresses > 0 && ImeRawPositiveAfter,
            "After the interactive fixture ends its lease, fresh pass-through X restores all independent positive-control channels");
        ImeRequire(isolation is not null && !isolation.IsThreadAlive && isolation.Failure is null && isolation.LastWin32Error == 0,
            "The interactive fixture disposes its keyboard hook and worker without native errors");
    }

    private static void ImeRequire(bool value, string message)
    { ImeChecks.Add(new { Name = message, Passed = value }); if (!value) throw new InvalidOperationException(message); }

    private static void WriteKeyboardBridgeImeReport(bool passed)
    {
        bool baselineComposition = ImePhases.Any(p => p.Mode == "Baseline" && p.CompositionStarts > 0 && p.HasSettledHanCommit);
        bool leaseComposition = ImePhases.Any(p => p.Mode == "Lease" && p.ActiveLeaseCompositionStarts > 0 && p.HasActiveLeaseSettledHanCommit);
        Directory.CreateDirectory(Path.GetDirectoryName(KeyboardBridgeImeReport)!);
        File.WriteAllText(KeyboardBridgeImeReport, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Passed = passed, Failure, Timestamp = DateTimeOffset.Now, Profile,
            IndependentFixture = true, DesktopSessionConstructed = false, FullInputIsolation = false,
            LifecycleChecks = ImeChecks, ImeFinishReason, ImeObserver, ImeAvailableLanguages, ImePreviousLanguage, ImeLanguageSetupError,
            BaselineCompositionObserved = baselineComposition, LeaseCompositionObserved = leaseComposition,
            CompositionOutcome = !baselineComposition ? "BaselineNotEstablished" : leaseComposition ? "BothPhasesObserved" : "LeaseCompositionNotEstablished",
            CandidateUiVisuallyVerifiedByThisCli = false, Phases = ImePhases, Events = ImeEvents, WpfEvents = ImeWpfEvents,
            Packets = ImePackets, FinalText = ImeFinalText, Hook = ImeHookDiagnostics, LanguageRestoration = ImeLanguageRestoration,
            ImeRawPositiveBefore, ImeRawPositiveAfter, PumpFailure = ImePumpFailure?.ToString(),
            Method = "One self-built WPF TextBox requests zh-CN with IME enabled. It starts without a lease; an own window button opens or closes the real KeyboardInputIsolation lease while retaining the same control and input language. The user uses their existing Natural Code double-pinyin scheme: physical n/i/h/k + Space, which may show ni'hao. No scheme/profile is changed. A local state button and visible On/Off label expose actual IME state. The actual PrivateKeyboardBridge.Process(packet,complete) and one-packet notifications are used only in lease phases. This CLI never assigns expected Chinese, synthesizes TextInput/WM_CHAR or selects candidates programmatically.",
            Completion = "F12 is a local own-window key handler, not a registered global hotkey. The own save button is equivalent. Ctrl+Alt+Esc, close and 300-second timeout end the own lease and clean up only the fixture. Previous input language/IME state are restored on the testing UI thread.",
            Interpretation = "Passed describes fixture lifecycle/control and positive-control checks, not Chinese compatibility. Raw e.Text Han flags remain separate. CompositionOutcome requires an actual composition start plus newly added Han in the document after PreviewTextInput commit and two unchanged ContextIdle observations with no active/new composition. Preedit Han alone never passes. Each commit is assigned to its composition-start phase; lease-active/current-ID state is captured at the actual commit, not at the later settled callback. A zh-CN language label does not prove a working TIP. Without a successful baseline, missing isolated Chinese cannot be attributed to the bridge. Candidate visibility/selection requires the coordinator's independent screen evidence.",
            Privacy = "Only this explicitly self-created input target is observed. Text/composition strings outside nihaok/x letters, the expected greeting characters, whitespace and apostrophe are redacted; no unrelated window content is read. The independent child records only X channel counters.",
            Limitations = new[] { "No DesktopSession, property editor, game, elevated target, physical HID boundary, mouse isolation, reconversion or general TSF compatibility is established.",
                "The independent child's positive controls cover X only. A zero counter during Chinese letters without an isolated X sample does not establish isolation for those letters or devices.",
                "No candidate screenshot is captured by this CLI. English character events and current language alone do not prove actual IME composition or candidate UI.",
                "Existing default 218, performance, pointer, original keyboard isolation and English bridge reports remain separate." }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class ImePhaseObservation(int id, string mode)
    {
        public int Id { get; } = id;
        public string Mode { get; } = mode;
        public long StartedAt { get; } = Stopwatch.GetTimestamp();
        public long? EndedAt { get; set; }
        public int CompositionStarts { get; set; }
        public int ActiveLeaseCompositionStarts { get; set; }
        public int CompositionUpdates { get; set; }
        public int Commits { get; set; }
        public bool HasHanCommit { get; set; }
        public bool HasActiveLeaseHanCommit { get; set; }
        public int SettledCommitObservations { get; set; }
        public bool HasSettledHanCommit { get; set; }
        public bool HasActiveLeaseSettledHanCommit { get; set; }
        public int IsolatedXKeyDownPackets { get; set; }
        public string? LeaseEndReason { get; set; }
        public KeyboardSnapshot? Observer { get; set; }
    }

    private sealed class ImeCompositionOwnership(ImePhaseObservation? phase, long startLeaseId)
    {
        public ImePhaseObservation? Phase { get; } = phase;
        public long StartLeaseId { get; } = startLeaseId;
        public long? CommitAt { get; set; }
        public bool LeaseActiveAtCommit { get; set; }
    }
}
