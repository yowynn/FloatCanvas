using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.PlatformTests;
using FloatCanvas.Windows;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

internal static partial class Program
{
    private static bool ProductionImeInteractive, ProductionImeReopen;
    private static string ProductionImeReport => Path.Combine(Root, "artifacts", "e2e", "production-ime-interactive.json");
    private static readonly List<object> ProductionImeChecks = [], ProductionImeEvents = [], ProductionImeStages = [];
    private static ObserverReady? ProductionImeObserver;
    private static object? ProductionImeHook, ProductionImeDocument;
    private static string? ProductionImeFinishReason;
    private static KeyboardSnapshot? ProductionImeBefore, ProductionImeDuring, ProductionImeAfter;
    private static long ProductionImeStarted;

    private static async Task RunProductionImeInteractive(Wpf.Application app)
    {
        ProductionImeStarted = Stopwatch.GetTimestamp();
        using var observer = await KeyboardObserverClient.StartAsync(); ProductionImeObserver = observer.Identity;
        await ActivateKeyboardFixture(observer); await observer.ResetAsync(); await PulseKeyboardX(observer, editing: false);
        ProductionImeBefore = await observer.SnapshotAsync();
        ProductionImeRequire(ProductionImeBefore.WmDown > 0 && ProductionImeBefore.WmUp > 0 && ProductionImeBefore.AsyncPresses > 0 && RawPositive(ProductionImeBefore),
            "The independent child's WM/raw/async X channels have a real positive control before the production editor opens");
        Session = Activator.CreateInstance(DesktopType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [app], null)!;
        ProductionImeObservation? observation = null;
        var status = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        KeyboardInputIsolation? isolation = null;
        string? previousStatus = null;
        try
        {
            await CallAsync("InitializeDocumentsAsync", false);
            Host = Field<OverlayHost>("_host"); Monitors = Host.Monitors;
            isolation = (KeyboardInputIsolation?)typeof(OverlayHost).GetField("_keyboardIsolation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host);
            Host.RegisterHotkeys(HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide);
            Host.KeyboardIsolationReleased += reason =>
            {
                ProductionImeEvents.Add(new { Kind = "ProductionIsolationReleased", At = Stopwatch.GetTimestamp(), Reason = reason.ToString() });
                Console.WriteLine("PRODUCTION_IME_ISOLATION_RELEASED: " + reason);
            };
            ProductionImeRequire(Field<EditorSession>("_live").Document.Elements.Count == 0 && Active().UndoCount == 0,
                "The isolated production profile starts with an empty live document and no undo history");
            ProductionImeRequire(Call("SetEditing", true) is true && Host.IsEditing && Host.IsKeyboardIsolationActive,
                "Real DesktopSession.SetEditing enters editing with actual production keyboard isolation active");
            var monitor = Monitors.First(m => m.IsPrimary);
            var point = new PointD(monitor.WorkArea.Left + 160, monitor.WorkArea.Top + 180);
            Call("ShowTextEditor", null, point);
            observation = ObserveProductionTextEditor();
            await ProductionImeWait(() => observation.HasTextFocus && Native.GetForegroundWindow() == observation.Handle,
                "The unmodified production TextBox did not acquire exact own-window text focus.", TimeSpan.FromSeconds(3));
            ProductionImeRequire(observation.Input.Text.Length == 0 && InputMethod.GetIsInputMethodEnabled(observation.Input),
                "The original production TextBox starts empty with IME enabled; the test assigns no Text");
            await observer.ResetAsync();
            status.Tick += (_, _) =>
            {
                if (observation is null || Host is null) return;
                string current = $"editing={Host.IsEditing}; isolation={Host.IsKeyboardIsolationActive}; textFocus={observation.HasTextFocus}; ime={KeyboardBridgeImeFixture.ReadImeState()}; language={InputLanguageManager.Current.CurrentInputLanguage.Name}; foreground={Native.GetForegroundWindow()}; dialogClosed={observation.IsClosed}; modifiers={Keyboard.Modifiers}";
                if (current == previousStatus) return;
                previousStatus = current;
                ProductionImeEvents.Add(new { Kind = "ProductionUiState", At = Stopwatch.GetTimestamp(), State = current });
                Console.WriteLine("PRODUCTION_IME_STATE: " + current);
            };
            status.Start();
            Console.WriteLine($"PRODUCTION_IME_READY: pid={Environment.ProcessId}; hwnd={observation.Handle}; title=FloatCanvas text input; stage=Create; timeoutTotal=300s; instructions=natural-code n/i/h/k+Space, Shift+Enter(newline), n/i/h/k+Space, X, Escape(cancel-preedit), Enter(commit); report={ProductionImeReport}");
            await WaitForProductionDialogClose(observation);
            await Task.Delay(120);
            ProductionImeDuring = await observer.SnapshotAsync();
            var live = Field<EditorSession>("_live");
            ProductionImeStages.Add(new { Stage = "Create", Actual = observation.Snapshot(), live.UndoCount, live.RedoCount });
            ProductionImeRequire(observation.EnterSeen&&observation.ShiftEnterSeen, "Actual Shift+Enter inserts a line and Enter completes production inline editing");
            ProductionImeRequire(observation.XPreeditObserved && observation.EscapeSeen && observation.EscapeRetainedDialog,
                "Actual X creates IME preedit and actual Escape cancels it while retaining inline editing");
            ProductionImeRequire(live.Document.Elements is [var text] && text.Kind == ElementKind.Text && text.Text.Replace("\r\n", "\n") == "你好\n你好",
                "The real production live document contains the two actually typed Chinese lines and no canceled X");
            ProductionImeRequire(live.UndoCount == 1 && live.RedoCount == 0 && !live.IsTransactionActive,
                "The two-line production text commit creates exactly one complete undo operation");
            ProductionImeRequire(ProductionImeDuring is not null && Quiet(ProductionImeDuring),
                "While actual production text/IME is edited, independent child WM/raw/async X remain silent");
            var element = live.Document.Elements.Single();
            live.SetSelection([element.Id]);
            ProductionImeRequire(live.SelectedElements().Single().Id == element.Id && !element.Locked && ElementGeometry.Bounds(element).Width > 0,
                "The production result remains a selectable retained text object with editable identity and geometry");
            observation.Dispose(); observation = null;

            if (ProductionImeReopen)
            {
                string beforeText = element.Text;
                Call("ShowTextEditor", element, element.Transform.TransformPoint(new(0, 0)));
                observation = ObserveProductionTextEditor();
                await ProductionImeWait(() => observation.HasTextFocus && Native.GetForegroundWindow() == observation.Handle,
                    "The real existing-object production editor did not acquire focus.", TimeSpan.FromSeconds(3));
                ProductionImeRequire(observation.Input.Text == beforeText && live.Selection.Contains(element.Id),
                    "Production ShowTextEditor(existing) reopens the same retained object's real text (method entry, not a double-click claim)");
                Console.WriteLine($"PRODUCTION_IME_READY: pid={Environment.ProcessId}; hwnd={observation.Handle}; title=FloatCanvas text input; stage=Reopen; instructions=actual Enter unchanged, or physically edit then Enter; remainingSeconds={Math.Max(0, 300 - Stopwatch.GetElapsedTime(ProductionImeStarted).TotalSeconds):F0}");
                await WaitForProductionDialogClose(observation); await Task.Delay(120);
                ProductionImeStages.Add(new { Stage = "Reopen", Actual = observation.Snapshot(), live.UndoCount, live.RedoCount });
                ProductionImeRequire(observation.EnterSeen && live.Document.Elements is [var edited] && edited.Id == element.Id && edited.Kind == ElementKind.Text,
                    "Actual Enter commits the reopened production editor without flattening or replacing object identity");
                int expectedUndo = live.Document.Elements.Single().Text == beforeText ? 1 : 2;
                ProductionImeRequire(live.UndoCount == expectedUndo && !live.IsTransactionActive,
                    "Reopen creates one undo only for a real text change and no extra undo for unchanged Enter");
                ProductionImeDuring = await observer.SnapshotAsync();
                ProductionImeRequire(Quiet(ProductionImeDuring), "The independent child's X channels remain silent through optional actual production re-editing");
                observation.Dispose(); observation = null;
            }
            ProductionImeDocument = new { live.UndoCount, live.RedoCount, live.IsTransactionActive,
                Elements = live.Document.Elements.Select(e => new { e.Id, e.Kind, Text = KeyboardBridgeImeFixture.ReportText(e.Text), e.Width, e.Height, e.Transform }).ToArray() };
            ProductionImeRequire(Host.IsEditing && Host.IsKeyboardIsolationActive,
                "Production remains in editable isolated mode after real text commits");
            Call("SetEditing", false);
            ProductionImeRequire(!Host.IsEditing && !Host.IsKeyboardIsolationActive, "The real production exit returns to pass-through before its final positive control");
            await ActivateKeyboardFixture(observer); await observer.ResetAsync(); await PulseKeyboardX(observer, editing: false);
            ProductionImeAfter = await observer.SnapshotAsync();
            ProductionImeRequire(ProductionImeAfter.WmDown > 0 && ProductionImeAfter.WmUp > 0 && ProductionImeAfter.AsyncPresses > 0 && RawPositive(ProductionImeAfter),
                "Fresh pass-through X restores the independent child's WM/raw/async channels after production editing");
            await CallAsync("FlushRecoveryAsync");
            ProductionImeFinishReason = "ProductionEnterCommitted";
        }
        finally
        {
            status.Stop();
            if (observation is not null) { ProductionImeStages.Add(new { Stage = "Unfinished", Actual = observation.Snapshot() }); observation.Dispose(); }
            if (Session is not null)
            {
                try { Call("SetEditing", false); }
                finally { ((IDisposable)Session).Dispose(); Session = null; Host = null; }
            }
            if (isolation is not null) ProductionImeHook = new { isolation.IsThreadAlive, isolation.IsLeaseActive, isolation.SuppressedCount,
                isolation.TailSuppressedCount, isolation.LastWin32Error, Failure = isolation.Failure?.ToString() };
        }
        ProductionImeRequire(isolation is not null && !isolation.IsThreadAlive && isolation.Failure is null && isolation.LastWin32Error == 0,
            "The real production session removes its isolation hook and worker cleanly");
    }

    private static ProductionImeObservation ObserveProductionTextEditor()
    {
        var (dialog,input)=ProductionInlineInput();
        return new(dialog, input, () => Host?.IsKeyboardIsolationActive == true);
    }
    private static (Wpf.Window Window,TextBox Input) ProductionInlineInput()
    {
        var surface=Field<object?>("_canvasTextInput")??throw new InvalidOperationException("Production inline text input was not created.");
        surface.GetType().GetMethod("Focus",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(surface,null);
        var window=(Wpf.Window)surface.GetType().GetField("window",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(surface)!;
        var edit=Field<object>("_inlineText");
        var input=(TextBox)edit.GetType().GetProperty("Input",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(edit)!;
        return(window,input);
    }
    private static async Task WaitForProductionDialogClose(ProductionImeObservation observation)
    {
        while (!observation.IsClosed)
        {
            if (Stopwatch.GetElapsedTime(ProductionImeStarted).TotalSeconds >= 300)
            { ProductionImeFinishReason = "Timeout300Seconds"; throw new TimeoutException("The real production IME editor did not finish within 300 seconds."); }
            if (Host?.IsEditing != true || Host.IsKeyboardIsolationActive != true)
            { ProductionImeFinishReason = "ProductionLeftEditingBeforeCommit"; throw new InvalidOperationException("Production stopped isolated editing before its text commit; no corrective input or focus action is performed."); }
            await Task.Delay(30);
        }
    }
    private static async Task ProductionImeWait(Func<bool> condition, string failure, TimeSpan timeout)
    {
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        { if (Stopwatch.GetElapsedTime(started) > timeout) throw new TimeoutException(failure); await Task.Delay(5); }
    }
    private static void ProductionImeRequire(bool value, string message)
    { ProductionImeChecks.Add(new { Name = message, Passed = value }); if (!value) throw new InvalidOperationException(message); }
    private static void WriteProductionImeReport(bool passed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProductionImeReport)!);
        File.WriteAllText(ProductionImeReport, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Passed = passed, Failure, Timestamp = DateTimeOffset.Now, Profile,
            ProductionDesktopSession = true, ProductionImeReopen, ProductionImeFinishReason, Checks = ProductionImeChecks,
            Stages = ProductionImeStages, Events = ProductionImeEvents, Observer = ProductionImeObserver,
            PositiveBefore = ProductionImeBefore, DuringProductionEditing = ProductionImeDuring, PositiveAfter = ProductionImeAfter,
            FinalDocument = ProductionImeDocument, HookAfterDispose = ProductionImeHook, CandidateUiVisuallyVerifiedByThisCli = false,
            Method = "The actual DesktopSession is constructed, InitializeDocumentsAsync(false) runs, SetEditing(true) enables actual production isolation, and the unmodified production ShowTextEditor(null,point) opens. This test attaches only read-only listeners to its original Window/TextBox; no added input/state window, assigned Text, synthetic TextInput/WM_CHAR or test keyboard bridge is used. A coordinator supplies physical Natural Code double-pinyin n/i/h/k+Space, a real Shift+Enter newline, a second phrase, X+Escape preedit cancellation and actual Enter. Only pre/post positive controls inject guarded X into the separately owned child.",
            ReopenMethod = "Optional --production-ime-reopen calls the same production ShowTextEditor(existing) with the retained ID and observes actual subsequent Enter. This is an explicit method entry, not evidence of a real double-click.",
            Scope = "The first actual production commit must retain exactly 你好 newline 你好 as one Text object and one undo operation. Actual X preedit/Escape must leave the same inline input surface open. Current Host editing/isolation, own text focus, input language and real IME state are printed only on changes. No input scheme, IME state or system profile is changed by this production probe.",
            Safety = "Only the fixture's own session/profile/window and separate child are created or cleaned up. Physical interactive keys are controlled by the coordinator. Programmatic positive-control X has exact child HWND/PID foreground guards. A 300-second total timeout or production lease exit stops the run and disposes only this session; no correcting input/focus is injected. Global Ctrl+Alt+Esc is handled by the unchanged production isolation component.",
            Privacy = "Only the self-created production TextBox and retained test object are read. Text outside the self-test nihaok/x alphabet, greeting characters, whitespace and apostrophe is redacted. The separate child records X counters only.",
            Limitations = new[] { "CLI does not recognize screenshots or candidate windows; coordinator visual evidence remains separate.",
                "One user's existing Natural Code input scheme and one two-line production text task do not establish every IME, property editor, reconversion, game, elevated process or physical HID boundary.",
                "Independent background-channel assertions cover X only. Optional reopen is a method entry and not an actual mouse double-click.",
                "The independent keyboard-bridge reports, default 218 report, performance and prior failures remain separate." }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
