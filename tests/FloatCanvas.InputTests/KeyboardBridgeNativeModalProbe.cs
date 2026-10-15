using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.PlatformTests;
using FloatCanvas.Windows;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

internal static partial class Program
{
    private static bool KeyboardBridgeNativeModalProbe;
    private static bool NativeModalOriginalOnly;
    private static bool? NativeModalMnemonicBaselineEstablished;
    private const uint NativeModalNotifyMessage = 0x862E;
    private static string KeyboardBridgeNativeModalReport => Path.Combine(Root, "artifacts", "e2e", "keyboard-bridge-native-modal.json");
    private static readonly List<NativeModalCheckResult> NativeModalChecks = [];
    private static readonly List<object> NativeModalPhases = [], NativeModalInputTrace = [];
    private static readonly List<NativeModalPacket> NativeModalPackets = [];
    private static readonly HashSet<ushort> NativeModalHeldKeys = [];
    private static Exception? NativeModalPumpFailure;
    private static object? NativeModalHook;
    private static ObserverReady? NativeModalObserver;

    // This deliberately leaves PrivateKeyboardBridge unchanged for a red run. Native modal
    // windows are closed by WM_CLOSE only after a failed observation, never to satisfy a check.
    private static async Task RunKeyboardBridgeNativeModalProbe(Wpf.Application app)
    {
        using var observer = await KeyboardObserverClient.StartAsync();
        NativeModalObserver = observer.Identity;
        await ActivateKeyboardFixture(observer);
        using var owner = new KeyboardBridgeFixture(NativeModalNotifyMessage);
        KeyboardInputIsolation? isolation = null;
        OverlayHost? host = null;
        var bridge = new PrivateKeyboardBridge();
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        KeyboardBridgeModalFixture? outer = null, inner = null;
        NativeModalCase? nativeCase = null;
        bool openOuter = false;
        nint nativeInput = 0;

        void ProcessPacket()
        {
            if (isolation is null || !isolation.TryDequeue(out var packet) || packet is null) return;
            var observation = new NativeModalPacket(packet); NativeModalPackets.Add(observation);
            bool completed = false;
            void CompleteOnce()
            {
                if (completed) return;
                completed = true; isolation.CompletePacket(packet.Sequence);
                observation.CompletedAt = Stopwatch.GetTimestamp();
            }
            try
            {
                if (packet.Kind == KeyboardInputPacketKind.Key)
                    observation.Delivered = bridge.Process(packet, CompleteOnce);
            }
            catch (Exception error) { NativeModalPumpFailure = error; isolation.EndLease(KeyboardLeaseEndReason.HookFailure); }
            finally { CompleteOnce(); }
        }

        void ActivateNative()
        {
            if (host is null) throw new InvalidOperationException("The own native input host is absent.");
            // TryEnterEditing deliberately accepts any own tool window while already editing.
            // This fixture needs the exact native target for its B/M setup gesture.
            if (nativeInput != 0 && NativeModalNative.IsOwn(nativeInput))
            { Native.SetForegroundWindow(nativeInput); Native.SetFocus(nativeInput); }
            if (!host.TryEnterEditing(out var reason)) throw new InvalidOperationException("Cannot activate own input: " + reason);
            nativeInput = Native.GetForegroundWindow();
            if (!host.Diagnostics.Any(d => d.InputHandle == nativeInput) || !NativeModalNative.IsOwn(nativeInput))
                throw new InvalidOperationException("Editing did not select an exact own native input HWND.");
            Native.SetFocus(nativeInput);
        }

        async Task NativeCase(bool leased, NativeModalAction action)
        {
            ActivateNative();
            var sample = nativeCase = new NativeModalCase(leased, action);
            NativeModalChord(observer, nativeInput, [0x42]);
            bool appeared = await NativeModalWait(() =>
            {
                nint foreground = Native.GetForegroundWindow();
                if (!NativeModalNative.IsOwn(foreground) || NativeModalNative.ClassName(foreground) != "#32770" ||
                    NativeModalNative.Caption(foreground) != sample.Title) return false;
                sample.Handle = foreground; return true;
            }, 2000);
            NativeModalCheck(appeared, sample.Name + ": actual native MessageBox appeared from the real B handler", sample.IsMnemonic);
            if (!appeared) throw new TimeoutException("The exact owned native MessageBox did not appear.");
            sample.Buttons = NativeModalNative.Buttons(sample.Handle);
            sample.InitialFocus = NativeModalNative.GetFocus();
            if (leased) { await observer.ResetAsync(); await PulseKeyboardX(observer, editing: true, sample.Handle); }
            if (action == NativeModalAction.TabEnter)
            {
                NativeModalChord(observer, sample.Handle, [0x09]);
                await Task.Delay(100);
                sample.FocusAfterTab = NativeModalNative.GetFocus();
                sample.TabMovedFocus = sample.FocusAfterTab != 0 && sample.FocusAfterTab != sample.InitialFocus &&
                    NativeModalNative.IsChild(sample.Handle, sample.FocusAfterTab);
                NativeModalCheck(sample.TabMovedFocus, sample.Name + ": actual Tab moves between native dialog buttons");
                NativeModalChord(observer, sample.Handle, [0x0D]);
            }
            else if (action == NativeModalAction.AltN) NativeModalChord(observer, sample.Handle, [0x12, 0x4E]);
            else NativeModalChord(observer, sample.Handle, [0x1B]);
            bool returnedByKeyboard = await NativeModalWait(() => sample.Returned, 1200);
            sample.ReturnedBeforeCleanup = returnedByKeyboard;
            sample.ResultBeforeCleanup = sample.Result;
            sample.QueueBeforeCleanup = isolation?.QueuedPacketCount ?? 0;
            sample.PendingPacketsBeforeCleanup = NativeModalPackets.Count(p => p.CompletedAt is null);
            sample.LeaseActiveBeforeCleanup = isolation?.IsLeaseActive == true;
            NativeModalCheck(returnedByKeyboard && sample.Result == sample.ExpectedResult,
                sample.Name + ": actual " + action + " returns " + sample.ExpectedResult + " without forced cleanup", sample.IsMnemonic);
            if (sample.IsMnemonic && !leased)
                NativeModalMnemonicBaselineEstablished = returnedByKeyboard && sample.Result == sample.ExpectedResult;
            if (leased)
            {
                sample.ObserverDuring = await observer.SnapshotAsync();
                NativeModalCheck(Quiet(sample.ObserverDuring), sample.Name + ": independent X WM/raw/async remain silent", sample.IsMnemonic);
            }
            if (!sample.Returned)
            {
                sample.ForcedCleanup = true;
                NativeModalNative.CloseOwnDialog(sample.Handle);
                if (!await NativeModalWait(() => sample.Returned, 2000)) throw new TimeoutException("Own native dialog cleanup did not return.");
            }
            NativeModalPhases.Add(sample);
            nativeCase = null;
            await NativeModalWait(() => isolation is null || isolation.QueuedPacketCount == 0, 1000);
        }

        async Task NestedCase(bool leased)
        {
            ActivateNative(); openOuter = true;
            NativeModalChord(observer, nativeInput, [0x4D]);
            if (!await NativeModalWait(() => outer is { Window.IsVisible: true, HasTextFocus: true }, 2000))
                throw new TimeoutException("The first actual ShowDialog did not open.");
            NativeModalChord(observer, outer!.Handle, [0x4E]);
            if (!await NativeModalWait(() => inner is { Window.IsVisible: true, HasTextFocus: true }, 2000))
                throw new TimeoutException("The nested actual ShowDialog did not open.");
            if (leased) await observer.ResetAsync();
            await PulseKeyboardX(observer, editing: true, inner!.Handle);
            NativeModalChord(observer, inner.Handle, [0x1B]);
            bool returned = await NativeModalWait(() => inner.Returned, 1200);
            var during = leased ? await observer.SnapshotAsync() : null;
            bool completedWithoutCleanup = returned && inner.ClosedByEscape && inner.Input.Text == "x";
            NativeModalCheck(completedWithoutCleanup,
                (leased ? "Lease" : "Baseline") + ": second-level ShowDialog receives actual x and closes on actual Escape");
            if (leased) NativeModalCheck(during is not null && Quiet(during), "Nested lease: independent X WM/raw/async remain silent");
            NativeModalPhases.Add(new { Name = leased ? "LeaseNestedWpf" : "BaselineNestedWpf", ReturnedBeforeCleanup = returned,
                ClosedByEscapeBeforeCleanup = inner.ClosedByEscape, TextBeforeCleanup = inner.Input.Text,
                PendingPacketsBeforeCleanup = NativeModalPackets.Count(p => p.CompletedAt is null),
                QueueBeforeCleanup = isolation?.QueuedPacketCount ?? 0, ObserverDuring = during,
                ForcedCleanup = !returned, InnerEvents = inner.Events.ToArray(), OuterEvents = outer.Events.ToArray() });
            if (!inner.Returned) inner.Dispose();
            await NativeModalWait(() => inner.Returned, 1000);
            if (outer.Window.IsVisible) outer.Dispose();
            await NativeModalWait(() => outer.Returned, 1000);
            inner.Dispose(); outer.Dispose(); inner = null; outer = null;
            await NativeModalWait(() => isolation is null || isolation.QueuedPacketCount == 0, 1000);
        }

        try
        {
            await observer.ResetAsync(); await PulseKeyboardX(observer, editing: false);
            var before = await observer.SnapshotAsync();
            NativeModalCheck(before.WmDown > 0 && before.WmUp > 0 && before.AsyncPresses > 0 && RawPositive(before),
                "Independent child X has real WM/raw/async positive controls before this experiment");
            NativeModalPhases.Add(new { Name = "PassPositiveBefore", Snapshot = before });
            NativeModalCheck(!Keyboard.IsKeyToggled(Key.CapsLock), "The real English fixture begins with Caps Lock off without changing it");
            owner.Window.Show(); owner.Notification = ProcessPacket;
            host = new OverlayHost(); Host = host; Monitors = host.Monitors;
            host.KeyPressed += key =>
            {
                if (key == 0x42 && nativeCase is { Started: false } sample)
                {
                    sample.Started = true;
                    sample.Result = Wpf.MessageBox.Show(owner.Window, "Own native dialog keyboard probe. Escape or Tab then Enter must cancel.",
                        sample.Title, sample.IsMnemonic ? Wpf.MessageBoxButton.YesNoCancel : Wpf.MessageBoxButton.OKCancel,
                        Wpf.MessageBoxImage.Information, sample.IsMnemonic ? Wpf.MessageBoxResult.Yes : Wpf.MessageBoxResult.OK);
                    sample.Returned = true;
                }
                if (key == 0x4D && openOuter)
                {
                    openOuter = false;
                    outer = new KeyboardBridgeModalFixture(owner.Window);
                    outer.Input.PreviewKeyDown += (_, e) =>
                    {
                        if (e.Key != Key.N || inner is not null) return;
                        e.Handled = true;
                        inner = new KeyboardBridgeModalFixture(outer.Window);
                        inner.ShowFromActualKey();
                    };
                    outer.ShowFromActualKey();
                }
            };
            isolation = await KeyboardInputIsolation.StartAsync(owner.Handle, NativeModalNotifyMessage);
            heartbeat.Tick += (_, _) => isolation.Heartbeat(); heartbeat.Start();
            // Native positive controls distinguish an inadequate fixture from a bridge regression.
            await NativeCase(leased: false, NativeModalAction.Escape);
            await NativeCase(leased: false, NativeModalAction.TabEnter);
            if (!NativeModalOriginalOnly) await NativeCase(leased: false, NativeModalAction.AltN);
            await NestedCase(leased: false);
            if (NativeModalChecks.Any(c => c.Scope == "Original21" && !c.Passed))
                throw new InvalidOperationException("An unleased dialog baseline failed; no lease assertions can be accepted.");
            ActivateNative(); isolation.BeginLease(HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide);
            NativeModalCheck(isolation.IsLeaseActive, "The exact own native input acquires a real keyboard isolation lease");
            await NativeCase(leased: true, NativeModalAction.Escape);
            await NativeCase(leased: true, NativeModalAction.TabEnter);
            if (!NativeModalOriginalOnly)
            {
                if (NativeModalMnemonicBaselineEstablished == true) await NativeCase(leased: true, NativeModalAction.AltN);
                else NativeModalPhases.Add(new { Name = "LeaseNativeAltNSkipped", Status = "BaselineNotEstablished",
                    Reason = "The same native Alt+N baseline did not return No; no isolated mnemonic success can be inferred." });
            }
            await NestedCase(leased: true);
            isolation.EndLease(); host.ExitEditing(restorePreviousForeground: false);
            await Task.Delay(80);
            await ActivateKeyboardFixture(observer);
            await observer.ResetAsync(); await PulseKeyboardX(observer, editing: false);
            var after = await observer.SnapshotAsync();
            NativeModalCheck(after.WmDown > 0 && after.WmUp > 0 && after.AsyncPresses > 0 && RawPositive(after),
                "Fresh pass-through X restores the independent child's three positive-control channels");
            NativeModalPhases.Add(new { Name = "PassPositiveAfter", Snapshot = after });
        }
        finally
        {
            heartbeat.Stop();
            foreach (ushort key in NativeModalHeldKeys.ToArray()) { Native.Key(key, true); NativeModalHeldKeys.Remove(key); }
            if (KeyboardXHeld) { Native.Key(0x58, true); KeyboardXHeld = false; }
            if (nativeCase is { Returned: false, Handle: not 0 }) NativeModalNative.CloseOwnDialog(nativeCase.Handle);
            inner?.Dispose(); outer?.Dispose();
            if (isolation is not null)
            {
                isolation.EndLease(); await Task.Delay(50); isolation.Dispose();
                NativeModalHook = new { isolation.IsLeaseActive, isolation.IsThreadAlive, isolation.LastWin32Error,
                    isolation.SuppressedCount, isolation.TailSuppressedCount, isolation.QueuedPacketCount, Failure = isolation.Failure?.ToString() };
            }
            owner.Notification = null; host?.Dispose(); Host = null;
        }
        NativeModalCheck(NativeModalPumpFailure is null, "Private packet processing completes without an exception");
        NativeModalCheck(isolation is not null && !isolation.IsThreadAlive && isolation.Failure is null && isolation.LastWin32Error == 0,
            "The hook is removed and its message thread stops without a native error");
        if (NativeModalChecks.Any(c => !c.Passed))
            throw new InvalidOperationException("Native/nested modal bridge regression checks failed; see independent report.");
    }

    private static void NativeModalCheck(bool value, string name, bool mnemonic = false) =>
        NativeModalChecks.Add(new(name, value, mnemonic ? "AdditionalMnemonic" : "Original21"));
    private static async Task<bool> NativeModalWait(Func<bool> condition, int milliseconds)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (NativeModalPumpFailure is not null) throw new InvalidOperationException("The modal bridge packet pump failed.", NativeModalPumpFailure);
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > milliseconds) return false;
            await Task.Delay(5);
        }
        return true;
    }
    private static void NativeModalChord(KeyboardObserverClient child, nint target, ushort[] keys)
    {
        foreach (ushort key in keys) Send(key, up: false);
        foreach (ushort key in keys.Reverse()) Send(key, up: true);
        void Send(ushort key, bool up)
        {
            nint foreground = Native.GetForegroundWindow();
            bool exactOwn = foreground == target && NativeModalNative.IsOwn(target);
            bool ownedRelease = up && NativeModalHeldKeys.Contains(key) &&
                (NativeModalNative.IsOwn(foreground) || foreground == child.Handle && child.OwnsProcess());
            if (!exactOwn && !ownedRelease) throw new InvalidOperationException("Native modal exact foreground/PID guard failed; no new keydown injected.");
            NativeModalInputTrace.Add(new { At = Stopwatch.GetTimestamp(), Key = key, Up = up, Target = target.ToInt64(), Foreground = foreground.ToInt64() });
            Native.Key(key, up); if (up) NativeModalHeldKeys.Remove(key); else NativeModalHeldKeys.Add(key);
        }
    }
    private static void WriteKeyboardBridgeNativeModalReport(bool passed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyboardBridgeNativeModalReport)!);
        File.WriteAllText(KeyboardBridgeNativeModalReport, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Passed = passed, Failure, Timestamp = DateTimeOffset.Now, Profile,
            IndependentFixture = true, DesktopSessionConstructed = false, FullInputIsolation = false,
            Checks = NativeModalChecks, Phases = NativeModalPhases, Packets = NativeModalPackets,
            NativeModalOriginalOnly,
            OriginalRegression = new { ExpectedCount = 21, Checks = NativeModalChecks.Where(c => c.Scope == "Original21").ToArray(),
                Passed = NativeModalChecks.Count(c => c.Scope == "Original21") == 21 && NativeModalChecks.Where(c => c.Scope == "Original21").All(c => c.Passed) },
            MnemonicExtension = new { Checks = NativeModalChecks.Where(c => c.Scope == "AdditionalMnemonic").ToArray(),
                Status = NativeModalOriginalOnly ? "NotRequested" : NativeModalMnemonicBaselineEstablished == true ? "BaselineEstablished" : "BaselineNotEstablished",
                Passed = NativeModalMnemonicBaselineEstablished == true && NativeModalChecks.Count(c => c.Scope == "AdditionalMnemonic") == 5 &&
                    NativeModalChecks.Where(c => c.Scope == "AdditionalMnemonic").All(c => c.Passed) },
            InputTrace = NativeModalInputTrace, Observer = NativeModalObserver, Hook = NativeModalHook,
            PumpFailure = NativeModalPumpFailure?.ToString(), Monitors,
            Method = "Original21 replays the recorded 21 checks: actual B opens WPF MessageBox.Show (native #32770), actual Escape or Tab/Enter must return Cancel; actual M opens ShowDialog, actual N opens a second ShowDialog, actual X edits and Escape closes it. AdditionalMnemonic separately checks native YesNoCancel actual Alt+N returning No. Each path first passes an unleased positive control. The real PrivateKeyboardBridge and KeyboardInputIsolation are used without Text assignment or synthetic text events.",
            Safety = "Only exact self-created native/WPF/dialog HWNDs with this PID receive guarded keydowns. A failed dialog is closed using WM_CLOSE or its WPF Close only after the outcome and pending queue are recorded; forced cleanup is never counted as keyboard success. Finally releases only injected held keys, ends the lease, disposes the hook and owned windows, and stops the independent child.",
            Limitations = new[] { "This is a native/nested modal regression fixture, not a production file-selection or complete UI acceptance.",
                "Only the independent child's X WM/raw/async channels have positive controls; no mouse, real HID, alternate desktop, game or full IME coverage is established.",
                "Existing 218, production X18, bridge22 and natural-code reports are not overwritten." }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private enum NativeModalAction { Escape, TabEnter, AltN }
    private sealed record NativeModalCheckResult(string Name, bool Passed, string Scope);
    private sealed class NativeModalCase(bool leased, NativeModalAction action)
    {
        public string Name { get; } = (leased ? "Lease" : "Baseline") + "Native" + action;
        public bool IsMnemonic { get; } = action == NativeModalAction.AltN;
        public Wpf.MessageBoxResult ExpectedResult { get; } = action == NativeModalAction.AltN ? Wpf.MessageBoxResult.No : Wpf.MessageBoxResult.Cancel;
        public string Title { get; } = "浮绘 · 自建原生模态 " + Guid.NewGuid().ToString("N");
        public bool Started { get; set; }
        public bool Returned { get; set; }
        public Wpf.MessageBoxResult Result { get; set; }
        [JsonIgnore] public nint Handle { get; set; }
        [JsonIgnore] public nint InitialFocus { get; set; }
        [JsonIgnore] public nint FocusAfterTab { get; set; }
        public long DialogHwnd => Handle.ToInt64();
        public long InitialFocusHwnd => InitialFocus.ToInt64();
        public long FocusAfterTabHwnd => FocusAfterTab.ToInt64();
        public bool TabMovedFocus { get; set; }
        public bool ReturnedBeforeCleanup { get; set; }
        public Wpf.MessageBoxResult ResultBeforeCleanup { get; set; }
        public bool ForcedCleanup { get; set; }
        public int QueueBeforeCleanup { get; set; }
        public int PendingPacketsBeforeCleanup { get; set; }
        public bool LeaseActiveBeforeCleanup { get; set; }
        public KeyboardSnapshot? ObserverDuring { get; set; }
        public object[] Buttons { get; set; } = [];
    }
    private sealed class NativeModalPacket(KeyboardInputPacket packet)
    {
        public long Sequence { get; } = packet.Sequence;
        public long LeaseId { get; } = packet.LeaseId;
        public string Kind { get; } = packet.Kind.ToString();
        public uint VirtualKey { get; } = packet.VirtualKey;
        public bool KeyUp { get; } = packet.IsKeyUp;
        public bool LocalBalance { get; } = packet.IsLocalBalance;
        public uint Flags { get; } = packet.Flags;
        public uint NativeMessage { get; } = packet.NativeMessage;
        public string Modifiers { get; } = packet.Modifiers.ToString();
        public long At { get; } = Stopwatch.GetTimestamp();
        public bool? Delivered { get; set; }
        public long? CompletedAt { get; set; }
    }
    private static class NativeModalNative
    {
        internal static bool IsOwn(nint window) => window != 0 &&
            KeyboardNative.GetWindowThreadProcessId(window, out uint pid) != 0 && pid == Environment.ProcessId;
        internal static string ClassName(nint window) { var text = new StringBuilder(256); GetClassNameW(window, text, text.Capacity); return text.ToString(); }
        internal static string Caption(nint window) { var text = new StringBuilder(256); GetWindowTextW(window, text, text.Capacity); return text.ToString(); }
        internal static object[] Buttons(nint dialog)
        {
            var buttons = new List<object>();
            if (!IsOwn(dialog) || ClassName(dialog) != "#32770") return [];
            EnumChildWindows(dialog, (window, _) =>
            {
                if (IsOwn(window) && ClassName(window) == "Button")
                    buttons.Add(new { Hwnd = window.ToInt64(), Id = GetDlgCtrlID(window), Caption = Caption(window) });
                return true;
            }, 0);
            return buttons.ToArray();
        }
        internal static void CloseOwnDialog(nint window)
        {
            if (!IsOwn(window) || ClassName(window) != "#32770") return;
            KeyboardNative.PostMessageW(window, 0x10, 0, 0);
        }
        [DllImport("user32.dll")] internal static extern nint GetFocus();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsChild(nint parent, nint child);
        private delegate bool EnumWindowCallback(nint window, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(nint parent, EnumWindowCallback callback, nint parameter);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(nint window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder value, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder value, int capacity);
    }
}
