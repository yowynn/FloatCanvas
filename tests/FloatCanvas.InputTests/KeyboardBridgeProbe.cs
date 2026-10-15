using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    private const uint BridgeNotifyMessage = 0x862D;
    private static bool KeyboardBridgeProbe;
    private static string KeyboardBridgeReport => Path.Combine(Root, "artifacts", "e2e", "keyboard-bridge-probe.json");
    private static readonly List<object> BridgeChecks = [], BridgePhases = [];
    private static readonly List<BridgePacketObservation> BridgePackets = [];
    private static readonly HashSet<ushort> BridgeHeldKeys = [];
    private static readonly List<object> BridgeInputTrace = [];
    private static object? BridgeHookDiagnostics;
    private static ObserverReady? BridgeObserver;
    private static object[] BridgeWpfEvents = [];
    private static Exception? BridgePumpFailure;
    private static bool BridgeRawPositiveBefore, BridgeRawPositiveAfter;
    private static bool? BridgeNativeXQuiet, BridgeWpfXQuiet, BridgeHeldExitQuiet;
    private static object[] BridgeModalEvents = [];
    private static bool? BridgeModalXQuiet;
    private static int BridgeNativeXEvents, BridgeNativeCtrlZEvents, BridgeTogglePackets, BridgeGlobalToggleEvents;

    private static async Task RunKeyboardBridgeProbe(Wpf.Application app)
    {
        // This experiment deliberately constructs no DesktopSession. It tests the actual Windows
        // component and actual WPF bridge against independent native/WPF targets first.
        using var observer = await KeyboardObserverClient.StartAsync();
        BridgeObserver = observer.Identity;
        await ActivateKeyboardFixture(observer);
        using var text = new KeyboardBridgeFixture(BridgeNotifyMessage);
        KeyboardInputIsolation? isolation = null;
        var localBridge = new PrivateKeyboardBridge();
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        long currentLease = 0;
        nint nativeInput = 0;
        OverlayHost? host = null;
        KeyboardBridgeModalFixture? modal = null;
        bool modalArmed = false;
        void Exit(KeyboardLeaseEndReason reason)
        {
            isolation?.EndLease(reason); host?.ExitEditing(restorePreviousForeground: false);
        }
        void Enter()
        {
            if (host is null || isolation is null) throw new InvalidOperationException("The private fixture is not ready.");
            if (!host.TryEnterEditing(out string? reason)) throw new InvalidOperationException("Own native fixture entry failed: " + reason);
            var p = observer.SafePoint(); nativeInput = host.Diagnostics.Single(w => w.Bounds.Contains(new(p.X, p.Y))).InputHandle;
            Native.SetFocus(nativeInput);
            currentLease = isolation.BeginLease(HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide);
        }
        void ProcessNotification()
        {
            if (isolation is null || !isolation.TryDequeue(out var packet) || packet is null) return;
            var sample = new BridgePacketObservation(packet); BridgePackets.Add(sample);
            bool completed = false;
            void CompleteOnce()
            {
                if (completed) return;
                completed = true; isolation.CompletePacket(packet.Sequence); sample.CompletedAt = Stopwatch.GetTimestamp();
            }
            try
            {
                switch (packet.Kind)
                {
                    case KeyboardInputPacketKind.Key: sample.Delivered = localBridge.Process(packet, CompleteOnce); break;
                    case KeyboardInputPacketKind.Toggle:
                        BridgeTogglePackets++; host?.ExitEditing(restorePreviousForeground: false); break;
                    case KeyboardInputPacketKind.Hide:
                        host?.SetVisible(false); break;
                    case KeyboardInputPacketKind.LeaseEnded:
                        if (packet.LeaseId == currentLease) host?.ExitEditing(restorePreviousForeground: false);
                        break;
                }
            }
            catch (Exception ex) { BridgePumpFailure = ex; isolation.EndLease(KeyboardLeaseEndReason.HookFailure); }
            finally
            {
                // Never drain in this callback. TranslateMessage's WM_CHAR must run before the
                // next notification changes the UI thread's keyboard state.
                CompleteOnce();
            }
        }
        text.Notification = ProcessNotification;
        try
        {
            await observer.ResetAsync(); await PulseKeyboardX(observer, editing: false);
            var before = await observer.SnapshotAsync(); BridgeRawPositiveBefore = RawPositive(before);
            BridgePhases.Add(new { Phase = "PassPositiveBefore", Snapshot = before });
            BridgeRequire(before.WmDown > 0 && before.WmUp > 0 && before.AsyncPresses > 0 && BridgeRawPositiveBefore,
                "The independent child positively observes ordinary, raw and async X before the isolation lease");
            BridgeRequire(!Keyboard.IsKeyToggled(Key.CapsLock), "English character fixture starts with Caps Lock off without changing the user's toggle state");

            host = new OverlayHost(); Host = host; Monitors = host.Monitors;
            host.KeyPressed += key =>
            {
                if (key == 0x58) BridgeNativeXEvents++;
                if (key == 0x5A && (OverlayHost.CurrentModifiers & OverlayModifiers.Control) != 0) BridgeNativeCtrlZEvents++;
                if (key == 0x4D && modalArmed)
                {
                    modalArmed = false;
                    modal = new KeyboardBridgeModalFixture(text.Window);
                    modal.ShowFromActualKey();
                }
            };
            host.ToggleRequested += () =>
            {
                BridgeGlobalToggleEvents++;
                if (host.IsEditing) Exit(KeyboardLeaseEndReason.Toggle); else Enter();
            };
            host.EditingFocusLost += () => isolation?.EndLease(KeyboardLeaseEndReason.ForegroundLost);
            host.RegisterHotkeys(HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide);
            isolation = await KeyboardInputIsolation.StartAsync(text.Handle, BridgeNotifyMessage);
            heartbeat.Tick += (_, _) => isolation.Heartbeat(); heartbeat.Start();
            Enter(); await BridgeWait(() => host.IsEditing && isolation.IsLeaseActive, "The initial private lease did not become active.");
            BridgeRequire(Native.GetForegroundWindow() == nativeInput, "The exact own native input HWND is foreground before any native-route keydown");

            await observer.ResetAsync(); int nativeXBefore = BridgeNativeXEvents;
            await PulseKeyboardX(observer, editing: true, nativeInput);
            var nativeX = await observer.SnapshotAsync(); BridgeNativeXQuiet = Quiet(nativeX);
            BridgePhases.Add(new { Phase = "NativeInputX", Snapshot = nativeX, HostXEvents = BridgeNativeXEvents - nativeXBefore });
            BridgeRequire(BridgeNativeXQuiet == true, "The independent child's WM/raw/async X channels stay silent while the real native input route is bridged");
            BridgeRequire(BridgeNativeXEvents > nativeXBefore, "The real OverlayHost native route receives the privately bridged X");
            int ctrlZBefore = BridgeNativeCtrlZEvents;
            BridgeChord(observer, nativeInput, [0x11, 0x5A]);
            await BridgeWait(() => BridgeNativeCtrlZEvents > ctrlZBefore, "The native route did not observe Ctrl+Z's local modifier state.");
            BridgeRequire(BridgeNativeCtrlZEvents > ctrlZBefore,
                "The native Host shortcut seam observes Ctrl+Z through packet-local keyboard state (not a DesktopSession undo test)");

            text.ActivateText(); await Task.Delay(40);
            BridgeRequire(Native.GetForegroundWindow() == text.Handle && text.HasTextFocus && isolation.IsLeaseActive,
                "The self-built WPF TextBox has exact focus while the isolation lease remains active");
            await observer.ResetAsync();
            await PulseKeyboardX(observer, editing: true, text.Handle);
            await BridgeWait(() => text.Input.Text == "x", "A real X key did not produce lowercase x in the WPF TextBox.");
            var wpfX = await observer.SnapshotAsync(); BridgeWpfXQuiet = Quiet(wpfX);
            BridgePhases.Add(new { Phase = "WpfLowercaseX", Snapshot = wpfX, Text = text.Snapshot() });
            BridgeRequire(BridgeWpfXQuiet == true && text.Input.Text == "x",
                "WPF receives a real lowercase character while independent WM/raw/async X remain silent");
            BridgeChord(observer, text.Handle, [0x10, 0x58], text);
            await BridgeWait(() => text.Input.Text == "xX", "Shift+X did not append uppercase X through the real WPF character pipeline.");
            BridgeRequire(text.Input.Text == "xX", "Shift modifies the real English character pipeline without assigning Text");
            BridgeChord(observer, text.Handle, [0x11, 0x41], text);
            await BridgeWait(() => text.Input.SelectionLength == 2, "Ctrl+A did not select both WPF characters.");
            BridgeRequire(text.Input.Text == "xX" && text.Input.SelectionLength == 2, "Ctrl+A executes WPF text selection without a global modifier stream");
            BridgeChord(observer, text.Handle, [0x08], text);
            await BridgeWait(() => text.Input.Text.Length == 0, "Backspace did not delete the WPF selection.");
            BridgeRequire(text.Input.Text.Length == 0, "Backspace edits the actual WPF selection");
            BridgeRequire(text.Events.Any(e => e.GetType().GetProperty("Kind")?.GetValue(e) as string == "PreviewTextInput"),
                "The WPF fixture observed real text-composition input events, not a programmatic Text assignment");
            BridgePhases.Add(new { Phase = "WpfShiftControlEditing", Text = text.Snapshot() });

            Native.SetForegroundWindow(nativeInput); Native.SetFocus(nativeInput); await Task.Delay(30);
            modalArmed = true;
            BridgeChord(observer, nativeInput, [0x4D]);
            await BridgeWait(() => modal is { Window.IsVisible: true, HasTextFocus: true } && Native.GetForegroundWindow() == modal.Handle,
                "The real M key did not open and focus the owned ShowDialog TextBox.");
            BridgeRequire(modal is not null && modal.Window.IsVisible && isolation.IsLeaseActive,
                "An actual privately bridged native M opens a real WPF ShowDialog while the lease stays active");
            await observer.ResetAsync();
            await PulseKeyboardX(observer, editing: true, modal!.Handle);
            await BridgeWait(() => modal.Input.Text == "x", "The nested modal pump did not receive a real isolated X character.");
            var modalX = await observer.SnapshotAsync(); BridgeModalXQuiet = Quiet(modalX);
            BridgeRequire(modal.Input.Text == "x" && BridgeModalXQuiet == true,
                "The real nested ShowDialog pump receives X while the independent child's WM/raw/async channels remain silent");
            BridgeChord(observer, modal.Handle, [0x1B]);
            await BridgeWait(() => modal.Returned && modal.ClosedByEscape && !modal.Window.IsVisible,
                "Actual Escape did not close and return from ShowDialog.");
            BridgeRequire(modal.Returned && modal.ClosedByEscape,
                "An actual isolated Escape closes the real modal dialog and returns to the native key handler");
            BridgePhases.Add(new { Phase = "ActualShowDialog", Snapshot = modalX, Text = KeyboardBridgeFixture.ReportText(modal.Input.Text), modal.ClosedByEscape, modal.Returned });

            // Leave through a swallowed Ctrl+Alt+F command packet, then enter through the real
            // registered hotkey while pass-through is active. Only owned release targets are allowed.
            Native.SetForegroundWindow(nativeInput); Native.SetFocus(nativeInput); await Task.Delay(30);
            int toggleBefore = BridgeTogglePackets;
            BridgeChord(observer, nativeInput, [0x11, 0x12, 0x46]);
            await BridgeWait(() => !host.IsEditing && !isolation.IsLeaseActive, "The isolated default F chord did not exit the lease.");
            BridgeRequire(BridgeTogglePackets > toggleBefore, "Ctrl+Alt+F exits editing through the private Toggle command rather than global reinjection");
            await ActivateKeyboardFixture(observer); int globalBefore = BridgeGlobalToggleEvents;
            BridgeChord(observer, observer.Handle, [0x11, 0x12, 0x46]);
            await BridgeWait(() => host.IsEditing && isolation.IsLeaseActive, "The real registered default F chord did not re-enter the private lease.");
            BridgeRequire(BridgeGlobalToggleEvents > globalBefore, "Pass-through Ctrl+Alt+F enters through real RegisterHotKey and opens a new private lease");

            Native.SetForegroundWindow(nativeInput); Native.SetFocus(nativeInput); await Task.Delay(30);
            await observer.ResetAsync(); int balancedBefore = BridgePackets.Count(p => p.LocalBalance && p.VirtualKey == 0x58);
            BridgeKey(observer, nativeInput, 0x58, up: false);
            await BridgeWait(() => BridgePackets.Any(p => p.LeaseId == currentLease && p.VirtualKey == 0x58 && !p.KeyUp && p.Delivered == true),
                "The held X keydown did not reach the local native route.");
            isolation.EndLease();
            await BridgeWait(() => BridgePackets.Count(p => p.LocalBalance && p.VirtualKey == 0x58) > balancedBefore &&
                !Keyboard.IsKeyDown(Key.X), "Lease exit did not balance the locally delivered held X.");
            BridgeRequire(!Keyboard.IsKeyDown(Key.X), "Ending the lease balances held X in the UI thread's local keyboard state");
            await ActivateKeyboardFixture(observer);
            BridgeKey(observer, observer.Handle, 0x58, up: true);
            await Task.Delay(80);
            var held = await observer.SnapshotAsync(); BridgeHeldExitQuiet = Quiet(held);
            BridgeRequire(BridgeHeldExitQuiet == true, "The physical tail keyup of a swallowed held X is quarantined instead of leaking an orphan release to the child");
            BridgePhases.Add(new { Phase = "HeldXLeaseExit", Snapshot = held, LocalBalancePackets = BridgePackets.Count(p => p.LocalBalance && p.VirtualKey == 0x58) - balancedBefore });

            await observer.ResetAsync(); await PulseKeyboardX(observer, editing: false);
            var after = await observer.SnapshotAsync(); BridgeRawPositiveAfter = RawPositive(after);
            BridgeRequire(after.WmDown > 0 && after.WmUp > 0 && after.AsyncPresses > 0 && BridgeRawPositiveAfter,
                "After lease exit a fresh pass-through X restores all three independent positive-control channels");
            BridgePhases.Add(new { Phase = "PassPositiveAfter", Snapshot = after });
        }
        finally
        {
            heartbeat.Stop();
            foreach (ushort key in BridgeHeldKeys.ToArray()) { Native.Key(key, true); BridgeHeldKeys.Remove(key); }
            if (KeyboardXHeld) { Native.Key(0x58, true); KeyboardXHeld = false; }
            if (isolation is not null)
            {
                isolation.EndLease();
                // Give private balancing packets a bounded opportunity to run before disposing the queue.
                await Task.Delay(50);
                isolation.Dispose();
                BridgeHookDiagnostics = new { isolation.IsLeaseActive, isolation.SuppressedCount, isolation.TailSuppressedCount,
                    isolation.QueuedPacketCount, isolation.IsThreadAlive, isolation.LastWin32Error, Failure = isolation.Failure?.ToString() };
            }
            BridgeWpfEvents = text.Events.ToArray(); text.Notification = null;
            BridgeModalEvents = modal?.Events.ToArray() ?? []; modal?.Dispose();
            host?.Dispose(); Host = null;
        }
        BridgeRequire(BridgePumpFailure is null, "The one-packet bridge pump completed without an exception");
        BridgeRequire(isolation is not null && !isolation.IsThreadAlive && isolation.Failure is null && isolation.LastWin32Error == 0,
            "The isolation component removes its hook and stops its thread cleanly");
    }

    private static bool Quiet(KeyboardSnapshot s) => s.WmDown == 0 && s.WmUp == 0 && s.RawDown == 0 && s.RawUp == 0 && s.AsyncPresses == 0 && s.AsyncHighSamples == 0 && s.RawErrors == 0;
    private static bool RawPositive(KeyboardSnapshot s) => s.RawDown > 0 && s.RawUp > 0 && s.RawErrors == 0;
    private static void BridgeRequire(bool value, string message)
    { BridgeChecks.Add(new { Name = message, Passed = value }); if (!value) throw new InvalidOperationException(message); }
    private static async Task BridgeWait(Func<bool> condition, string failure)
    {
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (BridgePumpFailure is not null) throw new InvalidOperationException("The bridge packet pump failed.", BridgePumpFailure);
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 2) throw new TimeoutException(failure);
            await Task.Delay(1);
        }
    }
    private static void BridgeChord(KeyboardObserverClient child, nint target, ushort[] keys, KeyboardBridgeFixture? text = null)
    {
        if (text is not null && !text.HasTextFocus) throw new InvalidOperationException("The exact WPF TextBox lost focus; no chord injected.");
        foreach (ushort key in keys) BridgeKey(child, target, key, up: false);
        foreach (ushort key in keys.Reverse()) BridgeKey(child, target, key, up: true);
    }
    private static void BridgeKey(KeyboardObserverClient child, nint target, ushort key, bool up)
    {
        nint foreground = Native.GetForegroundWindow(); KeyboardNative.GetWindowThreadProcessId(foreground, out uint pid);
        bool ownTarget = foreground == target && (target == child.Handle ? child.OwnsProcess() : pid == Environment.ProcessId);
        // Mode-switch releases may follow the private Toggle packet into any exact owned HWND.
        bool ownedRelease = up && BridgeHeldKeys.Contains(key) &&
            (foreground == child.Handle && child.OwnsProcess() || pid == Environment.ProcessId);
        if (!ownTarget && !ownedRelease) throw new InvalidOperationException("Bridge fixture keyboard foreground guard failed; no new keydown injected.");
        BridgeInputTrace.Add(new { At = Stopwatch.GetTimestamp(), Key = key, Up = up, Target = target.ToInt64(), Foreground = foreground.ToInt64(), Pid = pid });
        Native.Key(key, up); if (up) BridgeHeldKeys.Remove(key); else BridgeHeldKeys.Add(key);
    }
    private static void WriteKeyboardBridgeReport(bool passed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyboardBridgeReport)!);
        File.WriteAllText(KeyboardBridgeReport, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Passed = passed, Failure, Timestamp = DateTimeOffset.Now, Profile,
            IndependentFixture = true, DesktopSessionConstructed = false, FullInputIsolation = false,
            Checks = BridgeChecks, Phases = BridgePhases, Observer = BridgeObserver, Packets = BridgePackets,
            InputTrace = BridgeInputTrace, WpfEvents = BridgeWpfEvents, ModalEvents = BridgeModalEvents, Hook = BridgeHookDiagnostics,
            BridgeNativeXQuiet, BridgeWpfXQuiet, BridgeModalXQuiet, BridgeHeldExitQuiet, BridgeRawPositiveBefore, BridgeRawPositiveAfter,
            BridgeNativeXEvents, BridgeNativeCtrlZEvents, BridgeTogglePackets, BridgeGlobalToggleEvents,
            PumpFailure = BridgePumpFailure?.ToString(), Monitors,
            Method = "Actual KeyboardInputIsolation lease/hook and actual PrivateKeyboardBridge.Process(packet,complete) on this Dispatcher. The owned HwndSource dequeues one packet per private notification; the bridge completes it after dispatch or on ComponentDispatcher.EnterThreadModal. WM_CHAR is not synthesized. A real native M invokes ShowDialog, actual X edits its TextBox, and actual Escape closes it. Independent child process WM/Raw/async X counters remain separate.",
            EnglishEvidence = "The English fixture disables IME only on its own TextBox. Text is never assigned to satisfy the assertions: actual guarded VK_X, Shift+X, Ctrl+A and Backspace must produce real WPF character/composition/edit events. Caps Lock is checked off without changing it.",
            NativeShortcutEvidence = "The independent OverlayHost sees Ctrl+Z and its local modifier state. This is the native shortcut seam, not a production DesktopSession command or document undo acceptance.",
            InputMethodEvidence = "The nested modal pump is exercised with actual isolated English keys. No physical IME composition, Chinese candidate selection, reconversion or TSF candidate UI is established. English WM_CHAR/PreviewTextInput results cannot establish those properties; the separate coordinator-led interactive IME mode records them independently.",
            Safety = "Every new keydown requires the exact self-created native/WPF/child foreground HWND and its PID; mode-switch key releases are restricted to the same owned process or the exact child. Finally releases only synthesized held keys, ends/disposes the lease, closes owned windows and stops the child. No game or unrelated application is targeted.",
            Limitations = new[] { "This independent fixture does not construct DesktopSession or prove every app property dialog or menu. The one real owned ShowDialog is its measured modal boundary.",
                "Only the independent observer's X channels have before/after positive controls; other-key functionality does not prove every background channel for all keys.",
                "No mouse suppression, real game, elevation boundary, alternate desktop, Secure Attention Sequence or physical keyboard HID compatibility is established.",
                "Existing 218-check, keyboard-isolation, X-only suppression, performance and pointer-boundary reports are not overwritten." }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class BridgePacketObservation
    {
        public long At { get; } = Stopwatch.GetTimestamp();
        public long Sequence { get; }
        public long LeaseId { get; }
        public string Kind { get; }
        public uint VirtualKey { get; }
        public bool KeyUp { get; }
        public bool LocalBalance { get; }
        public string Modifiers { get; }
        public long Foreground { get; }
        public bool? Delivered { get; set; }
        public long? CompletedAt { get; set; }
        public BridgePacketObservation(KeyboardInputPacket packet)
        { Sequence = packet.Sequence; LeaseId = packet.LeaseId; Kind = packet.Kind.ToString(); VirtualKey = packet.VirtualKey; KeyUp = packet.IsKeyUp;
            LocalBalance = packet.IsLocalBalance; Modifiers = packet.Modifiers.ToString(); Foreground = packet.ForegroundWindow.ToInt64(); }
    }
}
