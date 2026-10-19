using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.PlatformTests;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

internal static partial class Program
{
    private static bool KeyboardIsolation;
    private static bool KeyboardWaitForActivation;
    private static string KeyboardIsolationReport => Path.Combine(Root, "artifacts", "e2e", "keyboard-isolation.json");
    private static readonly List<object> KeyboardPhases = [];
    private static readonly List<string> KeyboardContractFailures = [];
    private static int KeyboardEditorXEvents;
    private static bool KeyboardXHeld;
    private static long KeyboardSafetyGuards, KeyboardKeyInjections;
    private static ObserverReady? KeyboardObserverIdentity;
    private static string? KeyboardSafetyAbort;
    private static readonly List<object> KeyboardBackgroundObservations = [];
    private static bool KeyboardRawControlExercised, KeyboardAsyncDetectedInEdit, KeyboardRawDetectedInEdit;
    private static bool KeyboardEntryOwnershipVerified, KeyboardFocusedMessagesVerified = true, KeyboardFocusLossVerified;
    private static int KeyboardEditRoutesObserved;

    private static async Task RunKeyboardIsolation(Wpf.Application app)
    {
        using var fixture = await KeyboardObserverClient.StartAsync();
        KeyboardObserverIdentity = fixture.Identity;
        fixture.Activate();
        await ActivateKeyboardFixture(fixture);
        Session = Activator.CreateInstance(DesktopType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, [app], null)!;
        await CallAsync("InitializeDocumentsAsync", true);
        if (Field<DispatcherTimer?>("_documentRecoveryTimer") is { } timer) { timer.Stop(); timer.Interval = TimeSpan.FromHours(1); }
        Host = Field<OverlayHost>("_host"); Monitors = Host.Monitors;
        Host.KeyPressed += key => { if (key == 0x58) KeyboardEditorXEvents++; };
        try
        {
            await ActivateKeyboardFixture(fixture);
            await fixture.ResetAsync();
            await PulseKeyboardX(fixture, editing: false);
            var passBefore = await fixture.SnapshotAsync();
            KeyboardPhases.Add(new { Phase = "PassPositiveBefore", Foreground = Native.GetForegroundWindow().ToInt64(), Snapshot = passBefore });
            KeyboardRequire(passBefore.WmDown > 0 && passBefore.WmUp > 0, "Pass-through positive control receives ordinary X down and up in the independent lower process");
            KeyboardRequire(passBefore.AsyncPresses > 0 && passBefore.AsyncHighSamples > 0, "Pass-through positive control detects X through independent-process asynchronous polling");
            KeyboardRawControlExercised = passBefore.RawDown > 0 && passBefore.RawUp > 0;

            // No test focus helper is called here: the production mode transition itself must own focus.
            Call("SetEditing", true); await Task.Delay(90);
            nint foreground = Native.GetForegroundWindow();
            bool ownsInput = IsProductionInputForeground();
            nint toolbarHandle = new WindowInteropHelper(Field<Wpf.Window>("_toolbar")).Handle;
            KeyboardNative.GetWindowThreadProcessId(foreground, out uint foregroundPid);
            bool ownsForeground = foregroundPid == Environment.ProcessId;
            KeyboardEntryOwnershipVerified = Host.IsEditing && ownsForeground;
            KeyboardPhases.Add(new { Phase = "ProductionEditTransition", Foreground = foreground.ToInt64(), ForegroundPid = foregroundPid,
                ToolbarHandle = toolbarHandle.ToInt64(), ProductionPid = Environment.ProcessId,
                InputHandles = Host.Diagnostics.Select(w => w.InputHandle.ToInt64()).ToArray(), OwnsInput = ownsInput, OwnsForeground = ownsForeground,
                Role = foreground == toolbarHandle ? "OwnToolbar" : ownsInput ? "OwnNativeInput" : ownsForeground ? "OtherOwnWindow" : "ExternalOrNone" });
            KeyboardRequire(Host.IsEditing && ownsForeground, "Production edit transition owns keyboard foreground in its process, including a legitimate toolbar, without test-forced correction");
            if (Host.IsEditing && ownsForeground)
            {
                // These controlled route activations happen AFTER the uncorrected entry assertion above.
                // An own toolbar is legitimate editing focus and must not be mistaken for the game.
                Native.SetForegroundWindow(toolbarHandle); await Task.Delay(30);
                await ObserveKeyboardEditRoute(fixture, "OwnToolbar", toolbarHandle, requireHostXEvent: false);
                var point = fixture.SafePoint();
                nint input = Host.Diagnostics.Single(w => w.Bounds.Contains(new(point.X, point.Y))).InputHandle;
                Native.SetForegroundWindow(input); Native.SetFocus(input); await Task.Delay(30);
                await ObserveKeyboardEditRoute(fixture, "OwnNativeInput", input, requireHostXEvent: true);
            }
            else KeyboardPhases.Add(new { Phase = "EditXSkippedForSafety", Reason = "The production input HWND was not foreground; no X keydown was injected in this state." });

            // A visible input layer is insufficient when a different process owns the keyboard.
            // Transfer foreground only to the separate process created by this test, never to a game.
            fixture.Activate(); await Task.Delay(160);
            bool lowerOwnsForeground = Native.GetForegroundWindow() == fixture.Handle && fixture.OwnsProcess();
            bool toolbarVisible = Field<Wpf.Window>("_toolbar").IsVisible;
            KeyboardFocusLossVerified = lowerOwnsForeground && !Host.IsEditing && !toolbarVisible;
            KeyboardPhases.Add(new { Phase = "DeliberateOwnedForegroundLoss", LowerOwnsForeground = lowerOwnsForeground,
                Foreground = Native.GetForegroundWindow().ToInt64(), Host.IsEditing, ToolbarVisible = toolbarVisible });
            KeyboardRequire(lowerOwnsForeground, "The deliberate foreground-loss control successfully activates only the independent test process");
            KeyboardRequire(lowerOwnsForeground && !Host.IsEditing && !toolbarVisible,
                "Losing foreground to the independent process automatically returns to pass-through and hides the edit toolbar");

            Call("SetEditing", false); await Task.Delay(50);
            await ActivateKeyboardFixture(fixture); await fixture.ResetAsync();
            await PulseKeyboardX(fixture, editing: false);
            var passAfter = await fixture.SnapshotAsync();
            KeyboardPhases.Add(new { Phase = "PassPositiveAfter", Foreground = Native.GetForegroundWindow().ToInt64(), Snapshot = passAfter });
            KeyboardRequire(!Host.IsEditing && passAfter.WmDown > 0 && passAfter.WmUp > 0 && passAfter.AsyncPresses > 0,
                "Returning to pass-through restores both ordinary and asynchronous X handling in the lower process");
            await CheckAdditionalKeyboardFocusCases(fixture);
            if (KeyboardContractFailures.Count > 0) throw new InvalidOperationException("Keyboard isolation contract failed: " + string.Join("; ", KeyboardContractFailures));
        }
        finally
        {
            if (KeyboardXHeld) { Native.Key(0x58, true); KeyboardXHeld = false; }
            // Dispose only the helper process we started, via its private pipe protocol.
        }
    }

    private static void KeyboardRequire(bool value, string name)
    {
        Checks.Add(new { Name = name, Passed = value }); if (!value) KeyboardContractFailures.Add(name);
    }
    private static async Task CheckAdditionalKeyboardFocusCases(KeyboardObserverClient fixture)
    {
        Call("SetEditing", true); await Task.Delay(60);
        var toolbar = Field<Wpf.Window>("_toolbar");
        KeyboardRequire(Host!.IsEditing, "The application can re-enter editing for the additional focus regressions");
        var text = new Wpf.Window { Title = "浮绘 · 自建文本焦点验证", Width = 320, Height = 160, Owner = toolbar,
            Content = new System.Windows.Controls.TextBox { Text = "自建中文文本夹具", AcceptsReturn = true } };
        try
        {
            text.Show(); text.Activate(); await Task.Delay(90);
            KeyboardNative.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foregroundPid);
            bool ownTextForeground = foregroundPid == Environment.ProcessId && Native.GetForegroundWindow() == new WindowInteropHelper(text).Handle;
            KeyboardPhases.Add(new { Phase = "OwnWpfTextWindow", OwnsForeground = ownTextForeground, Host.IsEditing, ToolbarVisible = toolbar.IsVisible,
                RealImeExercised = false, Statement = "An own WPF TextBox window is activated; this is not a physical IME acceptance test." });
            KeyboardRequire(ownTextForeground && Host.IsEditing && toolbar.IsVisible,
                "A legitimate own WPF text/dialog foreground keeps editing and the toolbar visible");
        }
        finally { text.Close(); }
        await Task.Delay(40);
        var draftPoint = fixture.SafePoint();
        var textSession = Active(); int beforeTextCount=textSession.Document.Elements.Count; int beforeTextHistory = textSession.UndoCount;
        Call("ShowTextEditor", null, new PointD(draftPoint.X, draftPoint.Y)); await Task.Delay(70);
        var (draftWindow,draftInput)=ProductionInlineInput();
        const string draftText = "自建未提交文字草稿\n第二行";
        draftInput.Text = draftText;
        KeyboardRequire(Host.IsEditing && draftWindow.IsVisible,
            "The real own text-editor window is a legitimate editing foreground");
        fixture.Activate(); await Task.Delay(180);
        bool preservedText = !Host.IsEditing && !draftWindow.IsVisible && Field<object?>("_inlineText") is null &&
            textSession.Document.Elements.Count==beforeTextCount+1&&textSession.Document.Elements[^1].Text==draftText&&textSession.UndoCount==beforeTextHistory+1&&!textSession.IsTransactionActive;
        KeyboardPhases.Add(new { Phase = "ProductionTextDraftFocusLoss", PreservedText = preservedText, TextAssignedProgrammatically = true,
            RealImeExercised = false, DraftText = draftInput.Text, WindowVisible = draftWindow.IsVisible });
        KeyboardRequire(preservedText, "Automatic foreground loss commits production inline text once and removes its invisible input surface");
        Call("SetEditing", true); await Task.Delay(80);
        bool textResumed = Host.IsEditing && ReferenceEquals(Active(), textSession) && Field<object?>("_inlineText") is null &&
            textSession.Document.Elements[^1].Text==draftText&&textSession.UndoCount==beforeTextHistory+1;
        KeyboardPhases.Add(new { Phase = "ProductionTextDraftResume", PreservedText = textResumed, DraftText = draftInput.Text });
        KeyboardRequire(textResumed, "Re-entering the same document retains committed text without reopening an editor");
        var live = Active(); var point = fixture.SafePoint();
        live.Execute("自建失焦事务已提交夹具", d => d.Elements.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 50, Height = 35,
            Transform = Affine2D.Translation(point.X, point.Y), Style = new() { Stroke = 0xFF2383E2 } }));
        string committed = JsonSerializer.Serialize(live.Document); int history = live.UndoCount;
        live.BeginTransaction("自建尚未提交手势");
        live.Document.Elements.Add(new CanvasElement { Kind = ElementKind.Ellipse, Width = 30, Height = 30,
            Transform = Affine2D.Translation(point.X + 70, point.Y), Style = new() { Stroke = 0xFFEF4444 } });
        live.NotifyChanged();
        var busy = DesktopType.GetProperty("_documentBusy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        busy.SetValue(Session, true);
        try
        {
            fixture.Activate(); await Task.Delay(180);
            bool cancelled = !live.IsTransactionActive && JsonSerializer.Serialize(live.Document) == committed && live.UndoCount == history;
            KeyboardPhases.Add(new { Phase = "BusyUncommittedFocusLoss", SyntheticCoreTransaction = true, Host.IsEditing,
                ToolbarVisible = toolbar.IsVisible, CancelledToCommittedDocument = cancelled, UndoCount = live.UndoCount, ExpectedUndoCount = history });
            KeyboardRequire(!Host.IsEditing && !toolbar.IsVisible,
                "Document busy state does not block releasing input and hiding the toolbar on external foreground");
            KeyboardRequire(cancelled, "Focus loss cancels the uncommitted Core gesture while preserving committed content and history");
        }
        finally { busy.SetValue(Session, false); if (live.IsTransactionActive) live.Cancel(); }

        // A synthetic capture document tests the real session transition without reading desktop pixels.
        var assets = Field<AssetLibrary>("_assets"); string image = assets.Add(CreatePerformanceImage(11));
        var frozen = new SceneDocument { Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, 280, 180),
            Background = new CanvasElement { Kind = ElementKind.Image, AssetId = image, Width = 280, Height = 180, Locked = true },
            Elements = [new CanvasElement { Kind = ElementKind.Text, Text = "自建冻结草稿", Width = 180, Height = 35,
                Transform = Affine2D.Translation(15, 20), Style = new() { Stroke = 0xFF2383E2, FontSize = 22 } }] };
        var capture = new EditorSession(frozen, assets);
        DesktopType.GetField("_capture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(Session, capture);
        DesktopType.GetField("_captureOrigin", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(Session, new PointD(point.X, point.Y));
        Call("ResumeCapture"); await Task.Delay(80);
        KeyboardRequire(ReferenceEquals(Active(), capture) && Host.IsEditing,
            "Production ResumeCapture activates the editable synthetic frozen draft before the focus-loss check");
        string draft = JsonSerializer.Serialize(capture.Document);
        fixture.Activate(); await Task.Delay(180);
        bool keptDraft = ReferenceEquals(Field<EditorSession?>("_capture"), capture) && JsonSerializer.Serialize(capture.Document) == draft;
        bool returnedLive = ReferenceEquals(Active(), Field<EditorSession>("_live"));
        KeyboardPhases.Add(new { Phase = "FrozenDraftFocusLoss", SyntheticImageOnly = true, returnedLive, keptDraft,
            Host.IsEditing, ToolbarVisible = toolbar.IsVisible });
        KeyboardRequire(returnedLive && keptDraft && !Host.IsEditing && !toolbar.IsVisible,
            "Frozen draft focus loss returns to the live pass-through session and preserves the editable draft");
    }
    private static async Task ObserveKeyboardEditRoute(KeyboardObserverClient fixture, string route, nint inputWindow, bool requireHostXEvent)
    {
        await fixture.ResetAsync(); int before = KeyboardEditorXEvents;
        await PulseKeyboardX(fixture, editing: true, inputWindow);
        var edit = await fixture.SnapshotAsync();
        KeyboardEditRoutesObserved++;
        KeyboardFocusedMessagesVerified &= edit.WmDown == 0 && edit.WmUp == 0 && (!requireHostXEvent || KeyboardEditorXEvents > before);
        KeyboardPhases.Add(new { Phase = "EditXIsolation", Route = route, Foreground = Native.GetForegroundWindow().ToInt64(), Snapshot = edit, EditorXEvents = KeyboardEditorXEvents - before });
        if (requireHostXEvent) KeyboardRequire(KeyboardEditorXEvents > before, "The real native production input HWND receives X in editing mode");
        KeyboardRequire(edit.WmDown == 0 && edit.WmUp == 0, route + ": editing keeps ordinary X messages out of the independent lower process");
        KeyboardAsyncDetectedInEdit |= edit.AsyncPresses > 0 || edit.AsyncHighSamples > 0;
        KeyboardRawDetectedInEdit |= edit.RawDown > 0 || edit.RawUp > 0;
        KeyboardBackgroundObservations.Add(new { Route = route, RawChannelExercisedByPassControl = KeyboardRawControlExercised,
            AsyncXDetected = edit.AsyncPresses > 0 || edit.AsyncHighSamples > 0, RawXDetected = edit.RawDown > 0 || edit.RawUp > 0,
            Statement = "Background-channel observation only. This does not identify the cause of a particular game's response. A passing foreground/ordinary-message regression is not complete input isolation." });
    }
    private static bool IsProductionInputForeground() => Host is not null && Host.Diagnostics.Any(w => w.InputHandle == Native.GetForegroundWindow());
    private static async Task PulseKeyboardX(KeyboardObserverClient fixture, bool editing, nint editWindow = default)
    {
        int[] required = [0x58, 0x10, 0x11, 0x12, 0x5B, 0x5C];
        long started = Stopwatch.GetTimestamp(), clearSince = 0;
        while (true)
        {
            int[] held = required.Where(key => KeyboardNative.GetAsyncKeyState(key) < 0).ToArray();
            if (held.Length == 0)
            {
                if (clearSince == 0) clearSince = Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(clearSince).TotalMilliseconds >= 80) break;
            }
            else clearSince = 0;
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 2)
                throw new InvalidOperationException("Required keys did not remain released for 80ms; no keydown injected. Last held virtual keys: " + string.Join(",", held.Select(k => k.ToString("X2"))));
            await Task.Delay(10);
        }
        GuardKeyboardDestination(fixture, editing, editWindow);
        Native.Key(0x58); KeyboardKeyInjections++; KeyboardXHeld = true;
        try { await Task.Delay(180); }
        finally
        {
            // On an unexpected focus change, release only our held synthetic key and abort new input.
            try { GuardKeyboardDestination(fixture, editing, editWindow); }
            catch { Native.Key(0x58, true); KeyboardXHeld = false; throw; }
            Native.Key(0x58, true); KeyboardKeyInjections++; KeyboardXHeld = false;
        }
        await Task.Delay(80);
        GuardKeyboardDestination(fixture, editing, editWindow);
    }
    private static void GuardKeyboardDestination(KeyboardObserverClient fixture, bool editing, nint editWindow)
    {
        KeyboardSafetyGuards++;
        nint foreground = Native.GetForegroundWindow();
        KeyboardNative.GetWindowThreadProcessId(foreground, out uint foregroundPid);
        bool own = editing ? editWindow != 0 && foreground == editWindow && foregroundPid == Environment.ProcessId
            : foreground == fixture.Handle && fixture.OwnsProcess();
        if (own) return;
        KeyboardSafetyAbort = "Unexpected keyboard foreground; required " + (editing ? "the exact own production route HWND and PID" : "our independent helper HWND and PID") + ". No new keydown sent.";
        throw new InvalidOperationException(KeyboardSafetyAbort);
    }
    private static async Task ActivateKeyboardFixture(KeyboardObserverClient fixture)
    {
        fixture.Activate();
        if (Native.GetForegroundWindow() != fixture.Handle)
        {
            if (KeyboardWaitForActivation)
            {
                Console.WriteLine($"WAITING_OWN_KEYBOARD_FIXTURE: pid={fixture.Identity.ProcessId}; hwnd={fixture.Identity.Handle}; title=浮绘 · 独立键盘隔离验证窗口; timeout=30s");
                long started = Stopwatch.GetTimestamp();
                while (Native.GetForegroundWindow() != fixture.Handle)
                {
                    if (!fixture.OwnsProcess() || Stopwatch.GetElapsedTime(started).TotalSeconds > 30)
                        throw new InvalidOperationException("The owned fixture was not activated within 30 seconds; no keyboard input injected.");
                    await Task.Delay(60);
                }
                return;
            }
            var point = fixture.SafePoint();
            void Guard()
            {
                KeyboardSafetyGuards++;
                if (!fixture.OwnsProcess() || !fixture.OwnsPoint(point) || Native.WindowFromPoint(point) != fixture.Handle)
                    throw new InvalidOperationException($"Cannot activate our exact independent fixture safely; no mouse input injected. expected={fixture.Handle}, point=({point.X},{point.Y}), hit={Native.WindowFromPoint(point)}, ownsPid={fixture.OwnsProcess()}, ownsPoint={fixture.OwnsPoint(point)}, foreground={Native.GetForegroundWindow()}");
            }
            Guard(); Native.Move(point); Guard(); Native.Mouse(2); Guard(); Native.Mouse(4); await Task.Delay(35);
        }
        if (Native.GetForegroundWindow() != fixture.Handle) throw new InvalidOperationException("Our independent keyboard fixture did not gain foreground; no keyboard input injected.");
    }
    private static void WriteKeyboardIsolationReport(bool passed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyboardIsolationReport)!);
        File.WriteAllText(KeyboardIsolationReport, JsonSerializer.Serialize(new
        {
            SchemaVersion = 2, Passed = passed, Failure, Timestamp = DateTimeOffset.Now, Profile, Checks,
            VerdictScope = "Foreground entry ownership, ordinary WM X isolation in own toolbar/native input, automatic pass-through on external foreground, and pass-through positive controls. Background polling/raw observations are separate and a passing scoped regression does not prove complete keyboard isolation.",
            Observer = KeyboardObserverIdentity, Phases = KeyboardPhases, ContractFailures = KeyboardContractFailures,
            BackgroundObservations = KeyboardBackgroundObservations, KeyboardRawControlExercised, KeyboardAsyncDetectedInEdit, KeyboardRawDetectedInEdit,
            FocusedWindowMessageIsolation = new { Passed = KeyboardEntryOwnershipVerified && KeyboardEditRoutesObserved == 2 && KeyboardFocusedMessagesVerified,
                EntryOwnership = KeyboardEntryOwnershipVerified, ExercisedRoutes = KeyboardEditRoutesObserved, OrdinaryMessagesSuppressed = KeyboardFocusedMessagesVerified },
            FocusLoss = new { Passed = KeyboardFocusLossVerified },
            BackgroundReadChannels = new { RawInputExercised = KeyboardRawControlExercised, AsyncXDetected = KeyboardAsyncDetectedInEdit,
                RawXDetected = KeyboardRawDetectedInEdit, Observations = KeyboardBackgroundObservations },
            StrongIsolationOverall = !passed || KeyboardAsyncDetectedInEdit || KeyboardRawDetectedInEdit ? (bool?)false
                : KeyboardRawControlExercised && KeyboardEditRoutesObserved == 2 ? true : null,
            KeyboardEditorXEvents, KeyboardSafetyGuards, KeyboardKeyInjections, KeyboardSafetyAbort, Monitors,
            Method = "A separate purpose-built process receives ordinary keyboard messages, registers keyboard RIDEV_INPUTSINK, and polls only GetAsyncKeyState(VK_X) high bit from its own background thread. Pass-before and pass-after are positive controls. Initial editing entry is checked before test-forced correction and allows any foreground HWND owned by this process. Subsequent explicit own-toolbar/native-input activations exercise both X routes. Deliberate transfer to the same independent test HWND checks automatic pass-through and toolbar hiding on foreground loss.",
            Safety = "Every keydown and normal keyup requires the exact owned foreground HWND; helper HWND ownership is checked against the child PID. Unexpected focus stops new input and releases only the held synthetic key. No game or other application is activated, inspected or targeted.",
            RawInputBoundary = "SendInput inserts input-stream events and is not asserted equivalent to physical HID raw input. A missing pass-control raw pair makes that channel unexercised, never a passing raw-isolation claim.",
            Limitations = new[] { "This fixture does not reproduce a particular game, anti-cheat, driver, elevation level or hardware device.",
                "Asynchronous polling reads only VK_X's high bit; it does not record other keys or text.",
                "RawInput and async observations concern the separate test process; no third-party process memory or input log is read.",
                "Archived 218-check and performance reports are not modified by this test mode." }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record ObserverReady(long Handle, int ProcessId, bool RawRegistered);
    private sealed record KeyboardSnapshot(long WmDown, long WmUp, long RawDown, long RawUp, long AsyncPresses, long AsyncHighSamples, long RawErrors);
    private sealed class KeyboardObserverClient : IDisposable
    {
        private readonly NamedPipeServerStream pipe;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly Process child;
        public ObserverReady Identity { get; }
        public nint Handle => new(Identity.Handle);
        private KeyboardObserverClient(NamedPipeServerStream pipe, Process child, StreamReader reader, StreamWriter writer, ObserverReady identity)
        { this.pipe = pipe; this.child = child; this.reader = reader; this.writer = writer; Identity = identity; }
        public static async Task<KeyboardObserverClient> StartAsync()
        {
            string pipeName = "FloatCanvas.KeyboardFixture." + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--keyboard-observer"); start.ArgumentList.Add(pipeName);
            var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start our keyboard observer.");
            try
            {
                await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(8));
                var reader = new StreamReader(pipe, leaveOpen: true); var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                string ready = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new EndOfStreamException();
                var identity = JsonSerializer.Deserialize<ObserverReady>(ready) ?? throw new InvalidDataException("Missing observer identity.");
                if (identity.ProcessId != child.Id || !identity.RawRegistered) throw new InvalidDataException("Observer identity/registration mismatch.");
                return new(pipe, child, reader, writer, identity);
            }
            catch { pipe.Dispose(); if (!child.HasExited) child.Kill(); child.Dispose(); throw; }
        }
        public bool OwnsProcess() => KeyboardNative.GetWindowThreadProcessId(Handle, out uint processId) != 0 && processId == Identity.ProcessId && !child.HasExited;
        public void Activate() { if (!OwnsProcess()) throw new InvalidOperationException("Observer HWND no longer belongs to our child process."); Native.SetForegroundWindow(Handle); }
        public Native.Point SafePoint() { var point = new Native.Point(150, 180); Native.ClientToScreen(Handle, ref point); return point; }
        public bool OwnsPoint(Native.Point point)
        {
            Native.GetClientRect(Handle, out var rect); Native.ScreenToClient(Handle, ref point);
            return point.X >= 0 && point.Y >= 0 && point.X < rect.Right && point.Y < rect.Bottom;
        }
        public async Task ResetAsync() { await writer.WriteLineAsync("reset"); if (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) != "reset") throw new InvalidDataException("Observer reset failed."); }
        public async Task<KeyboardSnapshot> SnapshotAsync()
        {
            await writer.WriteLineAsync("snapshot"); return JsonSerializer.Deserialize<KeyboardSnapshot>(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) ?? throw new EndOfStreamException())
                ?? throw new InvalidDataException("Missing observer snapshot.");
        }
        public void Dispose()
        {
            try
            {
                try { if (pipe.IsConnected) writer.WriteLine("shutdown"); }
                catch (IOException) { /* Still reap only the child process created by this fixture. */ }
            }
            finally
            {
                try { if (!child.HasExited && !child.WaitForExit(3000)) { child.Kill(); child.WaitForExit(3000); } }
                finally { writer.Dispose(); reader.Dispose(); pipe.Dispose(); child.Dispose(); }
            }
        }
    }

    private static int RunKeyboardObserver(string pipeName)
    {
        using var observer = new KeyboardObserverWindow();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(6000);
        using var reader = new StreamReader(pipe, leaveOpen: true); using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        writer.WriteLine(JsonSerializer.Serialize(new ObserverReady(observer.Handle.ToInt64(), Environment.ProcessId, observer.RawRegistered)));
        using var stop = new CancellationTokenSource();
        Task poll = Task.Run(() =>
        {
            bool previous = false;
            while (!stop.IsCancellationRequested)
            {
                bool high = KeyboardNative.GetAsyncKeyState(0x58) < 0;
                observer.Poll(high, high && !previous); previous = high; Thread.Sleep(2);
            }
        });
        Task commands = Task.Run(() =>
        {
            try
            {
                while (reader.ReadLine() is { } command)
                {
                    if (command == "shutdown") break;
                    if (command == "reset") { observer.Reset(); writer.WriteLine("reset"); }
                    else if (command == "snapshot") writer.WriteLine(JsonSerializer.Serialize(observer.Snapshot()));
                }
            }
            finally { KeyboardNative.PostMessageW(observer.Handle, 0x10, 0, 0); }
        });
        try { while (KeyboardNative.GetMessageW(out var message, 0, 0, 0) > 0) { KeyboardNative.TranslateMessage(ref message); KeyboardNative.DispatchMessageW(ref message); } }
        finally { stop.Cancel(); poll.Wait(1000); pipe.Dispose(); }
        return 0;
    }
    private sealed class KeyboardObserverWindow : IDisposable
    {
        private readonly string className = "FloatCanvas.KeyboardObserver." + Guid.NewGuid().ToString("N");
        private readonly Native.WndProc procedure;
        private readonly object gate = new();
        private long wmDown, wmUp, rawDown, rawUp, asyncPresses, asyncHighSamples, rawErrors;
        public nint Handle { get; private set; }
        public bool RawRegistered { get; }
        public KeyboardObserverWindow()
        {
            procedure = OnMessage;
            var cls = new Native.WindowClass { Size = (uint)Marshal.SizeOf<Native.WindowClass>(), WindowProc = Marshal.GetFunctionPointerForDelegate(procedure),
                Instance = Native.GetModuleHandle(null), Background = new nint(6), ClassName = className };
            if (Native.RegisterClassEx(ref cls) == 0) throw new Win32Exception();
            Handle = Native.CreateWindowEx(0, className, "浮绘 · 独立键盘隔离验证窗口", 0x10CF0000, 40, 40, 760, 500, 0, 0, cls.Instance, 0);
            if (Handle == 0) throw new Win32Exception();
            var devices = new[] { new KeyboardNative.RawInputDevice { UsagePage = 1, Usage = 6, Flags = 0x100, Target = Handle } };
            RawRegistered = KeyboardNative.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<KeyboardNative.RawInputDevice>());
            if (!RawRegistered) throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.SetWindowPos(Handle, new nint(-1), 0, 0, 0, 0, 0x13); Native.ShowWindow(Handle, 5); Native.UpdateWindow(Handle);
        }
        public void Poll(bool high, bool rising) { lock (gate) { if (high) asyncHighSamples++; if (rising) asyncPresses++; } }
        public void Reset() { lock (gate) wmDown = wmUp = rawDown = rawUp = asyncPresses = asyncHighSamples = rawErrors = 0; }
        public KeyboardSnapshot Snapshot() { lock (gate) return new(wmDown, wmUp, rawDown, rawUp, asyncPresses, asyncHighSamples, rawErrors); }
        private nint OnMessage(nint window, uint message, nuint wParam, nint lParam)
        {
            if (message == 0xF)
            {
                nint dc = Native.BeginPaint(window, out var paint); Native.GetClientRect(window, out var rect); Native.FillRect(dc, ref rect, new nint(6));
                var text = new Native.Rect(20, 20, 700, 130);
                Native.DrawText(dc, "FloatCanvas independent keyboard observer\n仅统计自建测试的 X：WM / Raw Input / async high bit\n不记录其他按键、文本或其他应用内容。", -1, ref text, 0x10); Native.EndPaint(window, ref paint); return 0;
            }
            if ((message is 0x100 or 0x104 or 0x101 or 0x105) && wParam == 0x58)
            { lock (gate) { if (message is 0x100 or 0x104) wmDown++; else wmUp++; } return 0; }
            if (message == 0xFF) ReadRaw(lParam);
            if (message == 0x10) { Native.DestroyWindow(window); return 0; }
            if (message == 2) { KeyboardNative.PostQuitMessage(0); return 0; }
            return Native.DefWindowProc(window, message, wParam, lParam);
        }
        private void ReadRaw(nint raw)
        {
            uint bytes = 0, headerSize = (uint)Marshal.SizeOf<KeyboardNative.RawInputHeader>();
            if (KeyboardNative.GetRawInputData(raw, 0x10000003, 0, ref bytes, headerSize) == uint.MaxValue || bytes < headerSize || bytes > 16384)
            { lock (gate) rawErrors++; return; }
            nint buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (KeyboardNative.GetRawInputData(raw, 0x10000003, buffer, ref bytes, headerSize) == uint.MaxValue) { lock (gate) rawErrors++; return; }
                var header = Marshal.PtrToStructure<KeyboardNative.RawInputHeader>(buffer);
                if (header.Type != 1 || bytes < headerSize + Marshal.SizeOf<KeyboardNative.RawKeyboard>()) return;
                var keyboard = Marshal.PtrToStructure<KeyboardNative.RawKeyboard>(buffer + (int)headerSize);
                if (keyboard.VKey != 0x58) return;
                lock (gate) { if ((keyboard.Flags & 1) == 0) rawDown++; else rawUp++; }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public void Dispose()
        {
            if (Handle != 0) { Native.DestroyWindow(Handle); Handle = 0; }
            Native.UnregisterClass(className, Native.GetModuleHandle(null)); GC.KeepAlive(procedure);
        }
    }
    private static class KeyboardNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public nint Target; }
        [StructLayout(LayoutKind.Sequential)] internal struct RawInputHeader { public uint Type, Size; public nint Device; public nuint WParam; }
        [StructLayout(LayoutKind.Sequential)] internal struct RawKeyboard { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }
        [StructLayout(LayoutKind.Sequential)] internal struct NativeMessage { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public Native.Point Point; public uint Private; }
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
        [DllImport("user32.dll")] internal static extern int GetMessageW(out NativeMessage message, nint window, uint min, uint max);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(ref NativeMessage message);
        [DllImport("user32.dll")] internal static extern nint DispatchMessageW(ref NativeMessage message);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] internal static extern void PostQuitMessage(int exitCode);
    }
}
