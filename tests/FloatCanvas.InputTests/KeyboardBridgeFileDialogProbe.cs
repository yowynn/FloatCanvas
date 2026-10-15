using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.PlatformTests;
using FloatCanvas.Windows;
using Microsoft.Win32;
using SkiaSharp;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

internal static partial class Program
{
    private static bool KeyboardBridgeFileDialogProbe;
    private const uint FileDialogNotifyMessage = 0x862F;
    private static string KeyboardBridgeFileDialogReport => Path.Combine(Root, "artifacts", "e2e", "keyboard-bridge-file-dialog.json");
    private static readonly List<object> FileDialogChecks = [], FileDialogPhases = [];
    private static readonly List<NativeModalPacket> FileDialogPackets = [];
    private static object? FileDialogHook;
    private static ObserverReady? FileDialogObserver;
    private static string? FileDialogProbeImage;

    private static async Task RunKeyboardBridgeFileDialogProbe(Wpf.Application app)
    {
        string directory = Path.Combine(Root, "artifacts", "e2e"); Directory.CreateDirectory(directory);
        FileDialogProbeImage = Path.Combine(directory, "floatcanvas-open-dialog-probe-" + Guid.NewGuid().ToString("N") + ".png");
        if (FileDialogProbeImage.Any(c => c > 127)) throw new InvalidOperationException("This explicit keyboard probe requires an ASCII fixture path.");
        using (var image = new SKBitmap(8, 8))
        {
            image.Erase(SKColors.CornflowerBlue);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(FileDialogProbeImage, data.ToArray());
        }
        var characters = FileDialogProbeImage.Select(c => FileDialogNative.Map(c)).ToArray();
        using var observer = await KeyboardObserverClient.StartAsync(); FileDialogObserver = observer.Identity;
        await ActivateKeyboardFixture(observer);
        using var owner = new KeyboardBridgeFixture(FileDialogNotifyMessage);
        KeyboardInputIsolation? isolation = null;
        OverlayHost? host = null;
        nint nativeInput = 0;
        FileDialogCase? current = null;
        var bridge = new PrivateKeyboardBridge();
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };

        void ProcessPacket()
        {
            if (isolation is null || !isolation.TryDequeue(out var packet) || packet is null) return;
            var observation = new NativeModalPacket(packet); FileDialogPackets.Add(observation);
            bool completed = false;
            void CompleteOnce()
            {
                if (completed) return;
                completed = true; isolation.CompletePacket(packet.Sequence); observation.CompletedAt = Stopwatch.GetTimestamp();
            }
            try { if (packet.Kind == KeyboardInputPacketKind.Key) observation.Delivered = bridge.Process(packet, CompleteOnce); }
            catch (Exception error) { NativeModalPumpFailure = error; isolation.EndLease(KeyboardLeaseEndReason.HookFailure); }
            finally { CompleteOnce(); }
        }
        void ActivateNative()
        {
            if (host is null) throw new InvalidOperationException("The own native input host is absent.");
            if (nativeInput != 0 && NativeModalNative.IsOwn(nativeInput))
            { Native.SetForegroundWindow(nativeInput); Native.SetFocus(nativeInput); }
            if (!host.TryEnterEditing(out var reason)) throw new InvalidOperationException("Cannot activate own native input: " + reason);
            nativeInput = Native.GetForegroundWindow();
            if (!host.Diagnostics.Any(d => d.InputHandle == nativeInput) || !NativeModalNative.IsOwn(nativeInput))
                throw new InvalidOperationException("The exact own native input HWND is not foreground.");
            Native.SetFocus(nativeInput);
        }
        async Task<bool> RunCase(bool leased)
        {
            ActivateNative();
            var sample = current = new FileDialogCase(leased);
            NativeModalChord(observer, nativeInput, [0x4F]);
            bool appeared = await NativeModalWait(() =>
            {
                nint window = Native.GetForegroundWindow();
                if (!NativeModalNative.IsOwn(window) || NativeModalNative.ClassName(window) != "#32770" ||
                    NativeModalNative.Caption(window) != sample.Title ||
                    KeyboardNative.GetWindowThreadProcessId(window, out _) != FileDialogNative.GetCurrentThreadId()) return false;
                sample.Handle = window; return true;
            }, 3000);
            FileDialogCheck(appeared, sample.Name + ": real same-thread owned OpenFileDialog appeared");
            if (!appeared) throw new TimeoutException("The exact own OpenFileDialog did not appear.");
            sample.Focus.Add(FileDialogNative.Focus("Opened"));
            NativeModalChord(observer, sample.Handle, [0x12, 0x4E]); // Actual Alt+N; no UIA or SetValue.
            await Task.Delay(100);
            sample.Focus.Add(FileDialogNative.Focus("AfterAltN"));
            NativeModalChord(observer, sample.Handle, [0x11, 0x41]);
            await observer.ResetAsync(); await PulseKeyboardX(observer, editing: true, sample.Handle);
            sample.ObserverDuringX = await observer.SnapshotAsync();
            sample.Focus.Add(FileDialogNative.Focus("AfterActualX"));
            if (leased) FileDialogCheck(Quiet(sample.ObserverDuringX), sample.Name + ": independent child X WM/raw/async remain silent");
            NativeModalChord(observer, sample.Handle, [0x11, 0x41]);
            foreach (var character in characters)
            {
                NativeModalChord(observer, sample.Handle, character.Shift ? [(ushort)0x10, character.VirtualKey] : [character.VirtualKey]);
                await Task.Delay(4); // Yield the real UI pump between actual key strokes; no synthetic character messages.
            }
            bool pathObserved = await NativeModalWait(() => string.Equals(FileDialogNative.FocusText(), FileDialogProbeImage,
                StringComparison.OrdinalIgnoreCase), 2000);
            sample.Focus.Add(FileDialogNative.Focus("AfterTypedPath"));
            FileDialogCheck(pathObserved, sample.Name + ": the actual focused native control contains the complete ASCII fixture path");
            NativeModalChord(observer, sample.Handle, [0x0D]);
            bool returned = await NativeModalWait(() => sample.Returned, 3000);
            sample.ReturnedBeforeCleanup = returned; sample.ResultBeforeCleanup = sample.Result;
            sample.SelectedFileBeforeCleanup = sample.SelectedFile;
            sample.QueueBeforeCleanup = isolation?.QueuedPacketCount ?? 0;
            sample.PendingBeforeCleanup = FileDialogPackets.Count(p => p.CompletedAt is null);
            bool selected = returned && sample.Result == true && string.Equals(sample.SelectedFile, FileDialogProbeImage, StringComparison.OrdinalIgnoreCase);
            FileDialogCheck(selected, sample.Name + ": actual Enter accepts exactly the self-created PNG through dialog.FileName");
            if (!sample.Returned)
            {
                sample.ForcedCleanup = true; NativeModalNative.CloseOwnDialog(sample.Handle);
                if (!await NativeModalWait(() => sample.Returned, 2000)) throw new TimeoutException("Own OpenFileDialog cleanup did not return.");
            }
            FileDialogPhases.Add(sample); current = null;
            await NativeModalWait(() => isolation is null || isolation.QueuedPacketCount == 0, 1000);
            return pathObserved && selected;
        }

        try
        {
            await observer.ResetAsync(); await PulseKeyboardX(observer, editing: false);
            var before = await observer.SnapshotAsync();
            FileDialogCheck(before.WmDown > 0 && before.WmUp > 0 && before.AsyncPresses > 0 && RawPositive(before),
                "Independent child X has real WM/raw/async positive controls before the file-dialog fixture");
            FileDialogPhases.Add(new { Name = "PassPositiveBefore", Snapshot = before });
            owner.Window.Show(); owner.Notification = ProcessPacket;
            host = new OverlayHost(); Host = host; Monitors = host.Monitors;
            host.KeyPressed += key =>
            {
                if (key != 0x4F || current is not { Started: false } sample) return;
                sample.Started = true;
                var dialog = new OpenFileDialog { Title = sample.Title, InitialDirectory = directory,
                    Filter = "PNG (*.png)|*.png", CheckFileExists = true, Multiselect = false, RestoreDirectory = true };
                // FileName is intentionally never assigned. Only the real keyboard path can choose it.
                sample.Result = dialog.ShowDialog(owner.Window);
                sample.SelectedFile = dialog.FileName; sample.Returned = true;
            };
            isolation = await KeyboardInputIsolation.StartAsync(owner.Handle, FileDialogNotifyMessage);
            heartbeat.Tick += (_, _) => isolation.Heartbeat(); heartbeat.Start();
            bool baseline = await RunCase(leased: false);
            if (baseline)
            {
                ActivateNative(); isolation.BeginLease(HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide);
                FileDialogCheck(isolation.IsLeaseActive, "The own native input enters a real keyboard isolation lease");
                await RunCase(leased: true);
            }
            else FileDialogPhases.Add(new { Name = "LeaseSkipped", Status = "BaselineNotEstablished",
                Reason = "The identical unleased native Alt+N/path/Enter route failed; no isolated success can be accepted." });
            isolation.EndLease(); host.ExitEditing(restorePreviousForeground: false); await Task.Delay(80);
            await ActivateKeyboardFixture(observer); await observer.ResetAsync(); await PulseKeyboardX(observer, editing: false);
            var after = await observer.SnapshotAsync();
            FileDialogCheck(after.WmDown > 0 && after.WmUp > 0 && after.AsyncPresses > 0 && RawPositive(after),
                "Fresh pass-through X restores the child's WM/raw/async positive controls after the file dialog");
            FileDialogPhases.Add(new { Name = "PassPositiveAfter", Snapshot = after });
        }
        finally
        {
            heartbeat.Stop();
            foreach (ushort key in NativeModalHeldKeys.ToArray()) { Native.Key(key, true); NativeModalHeldKeys.Remove(key); }
            if (KeyboardXHeld) { Native.Key(0x58, true); KeyboardXHeld = false; }
            if (current is { Returned: false, Handle: not 0 }) NativeModalNative.CloseOwnDialog(current.Handle);
            if (isolation is not null)
            {
                isolation.EndLease(); await Task.Delay(50); isolation.Dispose();
                FileDialogHook = new { isolation.IsLeaseActive, isolation.IsThreadAlive, isolation.LastWin32Error,
                    isolation.SuppressedCount, isolation.QueuedPacketCount, Failure = isolation.Failure?.ToString() };
            }
            owner.Notification = null; host?.Dispose(); Host = null;
        }
        FileDialogCheck(NativeModalPumpFailure is null, "The file-dialog private packet pump has no exception");
        FileDialogCheck(isolation is not null && !isolation.IsThreadAlive && isolation.Failure is null && isolation.LastWin32Error == 0,
            "The file-dialog fixture removes the hook and stops its thread without a native error");
        if (FileDialogChecks.Any(c => !(bool)c.GetType().GetProperty("Passed")!.GetValue(c)!))
            throw new InvalidOperationException("Actual OpenFileDialog keyboard-route checks failed; inspect native focus observations.");
    }

    private static void FileDialogCheck(bool value, string name) => FileDialogChecks.Add(new { Name = name, Passed = value });
    private static void WriteKeyboardBridgeFileDialogReport(bool passed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyboardBridgeFileDialogReport)!);
        File.WriteAllText(KeyboardBridgeFileDialogReport, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Passed = passed, Failure, Timestamp = DateTimeOffset.Now, Profile,
            IndependentFixture = true, DesktopSessionConstructed = false, FullInputIsolation = false,
            ProbeImage = FileDialogProbeImage, Checks = FileDialogChecks, Phases = FileDialogPhases, Packets = FileDialogPackets,
            InputTrace = NativeModalInputTrace, Observer = FileDialogObserver, Hook = FileDialogHook,
            PumpFailure = NativeModalPumpFailure?.ToString(), Monitors,
            Method = "Real same-thread WPF OpenFileDialog on the owned fixture. Actual O opens it, guarded Alt+N is sent, actual Ctrl+A/X/Ctrl+A and per-key ASCII path input precede actual Enter. FileName is only read after ShowDialog returns, never assigned. Native focus HWND/thread/class/parent/controlID/text are captured independently of UIA. Identical unleased baseline precedes the real isolation lease; X has independent before/after positive controls.",
            Safety = "Every keydown requires the exact own native input or #32770 foreground HWND and PID. Only a self-created PNG under artifacts/e2e is selected. A failed own dialog is closed by WM_CLOSE after failure is recorded, never counted as keyboard success. Cleanup releases only fixture-injected held keys and disposes its hook, host, windows and independent child.",
            Limitations = new[] { "This is the real Windows file-dialog keyboard path on an independent fixture, not the production ImportImages or SaveProject handlers.",
                "No UIA target/coordinate helper is used, so this separates native dialog behavior from Sky's reported 150% DPI mapping.",
                "It does not validate file-dialog mouse targeting, all shell accelerators, all IME/HID or game isolation. Historical MessageBox Alt+N baseline failure remains independent." }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class FileDialogCase(bool leased)
    {
        public string Name { get; } = leased ? "LeaseFileDialog" : "BaselineFileDialog";
        public string Title { get; } = "浮绘 · 自建文件键盘验证 " + Guid.NewGuid().ToString("N");
        public bool Started { get; set; }
        public bool Returned { get; set; }
        public bool? Result { get; set; }
        public string? SelectedFile { get; set; }
        [JsonIgnore] public nint Handle { get; set; }
        public long DialogHwnd => Handle.ToInt64();
        public List<object> Focus { get; } = [];
        public KeyboardSnapshot? ObserverDuringX { get; set; }
        public bool ReturnedBeforeCleanup { get; set; }
        public bool? ResultBeforeCleanup { get; set; }
        public string? SelectedFileBeforeCleanup { get; set; }
        public bool ForcedCleanup { get; set; }
        public int QueueBeforeCleanup { get; set; }
        public int PendingBeforeCleanup { get; set; }
    }
    private static class FileDialogNative
    {
        internal readonly record struct CharacterKey(ushort VirtualKey, bool Shift);
        internal static CharacterKey Map(char character)
        {
            short mapping = VkKeyScanExW(character, GetKeyboardLayout(0));
            if (mapping == -1 || ((mapping >> 8) & ~1) != 0)
                throw new InvalidOperationException("The active keyboard layout cannot type the fixture's ASCII character without Ctrl/Alt: " + character);
            return new((ushort)(mapping & 255), ((mapping >> 8) & 1) != 0);
        }
        internal static string FocusText() => Text(NativeModalNative.GetFocus());
        internal static object Focus(string phase)
        {
            nint focus = NativeModalNative.GetFocus(), foreground = Native.GetForegroundWindow();
            var chain = new List<object>(); nint window = focus;
            for (int i = 0; window != 0 && i < 10 && NativeModalNative.IsOwn(window); i++, window = GetParent(window))
            {
                uint thread = KeyboardNative.GetWindowThreadProcessId(window, out uint pid);
                chain.Add(new { Hwnd = window.ToInt64(), ThreadId = thread, ProcessId = pid, Class = NativeModalNative.ClassName(window),
                    Parent = GetParent(window).ToInt64(), ControlId = GetDlgCtrlID(window), Value = Text(window) });
                if (window == foreground) break;
            }
            return new { Phase = phase, At = Stopwatch.GetTimestamp(), UiThread = GetCurrentThreadId(),
                Foreground = foreground.ToInt64(), ForegroundClass = NativeModalNative.ClassName(foreground),
                Focus = focus.ToInt64(), Value = Text(focus), Chain = chain.ToArray() };
        }
        private static string Text(nint window)
        {
            if (!NativeModalNative.IsOwn(window)) return "";
            var value = new StringBuilder(4096); GetWindowTextW(window, value, value.Capacity); return value.ToString();
        }
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern nint GetParent(nint window);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(nint window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder value, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanExW(char character, nint keyboardLayout);
        [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    }
}
