using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using FloatCanvas.PlatformTests;
using FloatCanvas.Windows;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

internal static partial class Program
{
    // Main owns dispatch and report routing; this experiment never writes the existing reports.
    private static string KeyboardSuppressionExperimentReport => Path.Combine(Root, "artifacts", "e2e", "keyboard-suppression-probe.json");
    private static readonly List<object> SuppressionChecks = [], SuppressionPhases = [];
    private static readonly List<string> SuppressionFailures = [];
    private static ObserverReady? SuppressionObserver;
    private static bool SuppressionRawControlBefore, SuppressionRawControlAfter;
    private static bool? SuppressionXChannelsBlocked;
    private static object? SuppressionHookDiagnostics;
    private static int SuppressionEditorXEvents;
    private static string? SuppressionCleanupFailure;

    private static async Task RunKeyboardSuppressionExperiment(Wpf.Application app)
    {
        using var fixture = await KeyboardObserverClient.StartAsync();
        SuppressionObserver = fixture.Identity;
        KeyboardSuppressionProbe? probe = null;
        OverlayHost? observedHost = null;
        int gate = 0;
        void ObserveKey(int key) { if (key == 0x58) Interlocked.Increment(ref SuppressionEditorXEvents); }
        try
        {
            await ActivateKeyboardFixture(fixture);
            Session = Activator.CreateInstance(DesktopType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [app], null)!;
            await CallAsync("InitializeDocumentsAsync", true);
            if (Field<DispatcherTimer?>("_documentRecoveryTimer") is { } timer)
            { timer.Stop(); timer.Interval = TimeSpan.FromHours(1); }
            Host = Field<OverlayHost>("_host"); Monitors = Host.Monitors;
            observedHost = Host; observedHost.KeyPressed += ObserveKey;

            await ActivateKeyboardFixture(fixture);
            await fixture.ResetAsync();
            await PulseKeyboardX(fixture, editing: false);
            var before = await fixture.SnapshotAsync();
            SuppressionRawControlBefore = before.RawDown > 0 && before.RawUp > 0 && before.RawErrors == 0;
            SuppressionPhases.Add(new { Phase = "PassPositiveBefore", Foreground = Native.GetForegroundWindow().ToInt64(), Snapshot = before });
            SuppressionRequire(before.WmDown > 0 && before.WmUp > 0,
                "Without the hook the independent foreground child receives an ordinary X down/up pair");
            SuppressionRequire(before.AsyncPresses > 0 && before.AsyncHighSamples > 0,
                "Without the hook the independent child positively observes asynchronous X state");
            SuppressionRequire(SuppressionRawControlBefore,
                "Without the hook the independent child positively observes a Raw Input X pair without errors");

            // Record production entry BEFORE explicit native-route setup.
            Call("SetEditing", true); await Task.Delay(90);
            nint entryForeground = Native.GetForegroundWindow();
            KeyboardNative.GetWindowThreadProcessId(entryForeground, out uint entryPid);
            bool entryOwned = Host.IsEditing && entryPid == Environment.ProcessId;
            SuppressionPhases.Add(new { Phase = "ProductionEditEntryBeforeSetup", Host.IsEditing,
                Foreground = entryForeground.ToInt64(), ForegroundPid = entryPid, OwnsForeground = entryOwned,
                TestForcedNativeActivation = false });
            SuppressionRequire(entryOwned, "Production editing initially owns a foreground HWND in its own process");
            if (!entryOwned) throw new InvalidOperationException("Production entry did not own foreground; no probe X injected.");

            // This activation is only native-target setup, never production-entry evidence.
            var point = fixture.SafePoint();
            nint input = Host.Diagnostics.Single(w => w.Bounds.Contains(new(point.X, point.Y))).InputHandle;
            Native.SetForegroundWindow(input); Native.SetFocus(input); await Task.Delay(40);
            GuardKeyboardDestination(fixture, editing: true, input);
            SuppressionPhases.Add(new { Phase = "ExplicitOwnNativeTargetSetup", NativeInput = input.ToInt64(),
                Foreground = Native.GetForegroundWindow().ToInt64(), TestForcedNativeActivation = true,
                Statement = "Controlled native input route setup only; not evidence that production entry chose this HWND." });

            // The hook thread reads only this volatile lease plus its native HWND/PID guards.
            Volatile.Write(ref gate, 1);
            probe = await KeyboardSuppressionProbe.StartAsync(input, () => Volatile.Read(ref gate) == 1);
            await fixture.ResetAsync();
            int eventsBefore = Volatile.Read(ref SuppressionEditorXEvents);
            await PulseKeyboardX(fixture, editing: true, input); // Guarded 180ms synthetic X pulse.
            var editing = await fixture.SnapshotAsync();
            int hostEvents = Volatile.Read(ref SuppressionEditorXEvents) - eventsBefore;
            bool quiet = editing.WmDown == 0 && editing.WmUp == 0 && editing.RawDown == 0 && editing.RawUp == 0 &&
                editing.AsyncPresses == 0 && editing.AsyncHighSamples == 0 && editing.RawErrors == 0;
            SuppressionXChannelsBlocked = SuppressionRawControlBefore ? quiet : null;
            SuppressionPhases.Add(new { Phase = "NativeInputXOnlySuppression", Snapshot = editing, HostXEvents = hostEvents,
                probe.SuppressedDown, probe.SuppressedUp, probe.RelayedDown, probe.RelayedUp,
                probe.SkippedOutsideScope, probe.LastHookError, HookFailure = probe.Failure?.ToString(),
                Foreground = Native.GetForegroundWindow().ToInt64(), RawChannelHasPositiveControl = SuppressionRawControlBefore });
            SuppressionRequire(editing.WmDown == 0 && editing.WmUp == 0,
                "The independent child receives no ordinary X messages during the scoped native-input probe");
            SuppressionRequire(editing.RawDown == 0 && editing.RawUp == 0 && editing.RawErrors == 0 && SuppressionRawControlBefore,
                "With an exercised positive control the independent child receives no Raw Input X during suppression");
            SuppressionRequire(editing.AsyncPresses == 0 && editing.AsyncHighSamples == 0,
                "The independent child observes no asynchronous X high state during suppression");
            SuppressionRequire(hostEvents > 0,
                "The production native OverlayHost receives the explicit local X message relay");
            SuppressionRequire(probe.SuppressedDown > 0 && probe.SuppressedDown == probe.SuppressedUp &&
                probe.RelayedDown == probe.SuppressedDown && probe.RelayedUp == probe.SuppressedUp,
                "The hook suppresses and locally relays balanced X down/up pairs");

            Volatile.Write(ref gate, 0);
            probe.Dispose();
            SuppressionHookDiagnostics = new { probe.SuppressedDown, probe.SuppressedUp, probe.RelayedDown, probe.RelayedUp,
                probe.SkippedOutsideScope, probe.LastHookError, HookFailure = probe.Failure?.ToString(), probe.IsThreadAlive };
            SuppressionRequire(!probe.IsThreadAlive && probe.LastHookError == 0 && probe.Failure is null,
                "Disposing the X-only scope removes the hook and stops its message thread without error");

            Call("SetEditing", false); await Task.Delay(50);
            await ActivateKeyboardFixture(fixture);
            await fixture.ResetAsync();
            await PulseKeyboardX(fixture, editing: false);
            var after = await fixture.SnapshotAsync();
            SuppressionRawControlAfter = after.RawDown > 0 && after.RawUp > 0 && after.RawErrors == 0;
            SuppressionPhases.Add(new { Phase = "PassPositiveAfterDispose", Snapshot = after,
                Foreground = Native.GetForegroundWindow().ToInt64(), Host.IsEditing });
            SuppressionRequire(!Host.IsEditing && after.WmDown > 0 && after.WmUp > 0,
                "After disposal pass-through restores ordinary X down/up in the independent child");
            SuppressionRequire(after.AsyncPresses > 0 && after.AsyncHighSamples > 0,
                "After disposal the independent child's asynchronous X positive control recovers");
            SuppressionRequire(SuppressionRawControlAfter,
                "After disposal the independent child's Raw Input X positive control recovers without errors");
            if (SuppressionFailures.Count > 0)
                throw new InvalidOperationException("X-only suppression probe failed: " + string.Join("; ", SuppressionFailures));
        }
        finally
        {
            Volatile.Write(ref gate, 0);
            SuppressionCleanup(() => probe?.Dispose());
            SuppressionCleanup(() =>
            {
                try { if (KeyboardXHeld) Native.Key(0x58, true); }
                finally { KeyboardXHeld = false; }
            });
            if (observedHost is not null) observedHost.KeyPressed -= ObserveKey;
            SuppressionCleanup(() => { if (Host?.IsEditing == true) Call("SetEditing", false); });
            if (probe is not null)
                SuppressionHookDiagnostics = new { probe.SuppressedDown, probe.SuppressedUp, probe.RelayedDown, probe.RelayedUp,
                    probe.SkippedOutsideScope, probe.LastHookError, HookFailure = probe.Failure?.ToString(), probe.IsThreadAlive };
            // The using scope shuts down only the independent child that this experiment started.
        }
        if (SuppressionCleanupFailure is not null)
            throw new InvalidOperationException("X-only suppression cleanup failed: " + SuppressionCleanupFailure);
    }

    private static void SuppressionCleanup(Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error)
        { SuppressionCleanupFailure = SuppressionCleanupFailure is null ? error.ToString() : SuppressionCleanupFailure + "\n" + error; }
    }

    private static void SuppressionRequire(bool passed, string name)
    {
        SuppressionChecks.Add(new { Name = name, Passed = passed });
        if (!passed) SuppressionFailures.Add(name);
    }

    private static void WriteKeyboardSuppressionExperimentReport(bool passed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyboardSuppressionExperimentReport)!);
        File.WriteAllText(KeyboardSuppressionExperimentReport, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Passed = passed && SuppressionFailures.Count == 0 && SuppressionCleanupFailure is null,
            Failure, Timestamp = DateTimeOffset.Now, Profile,
            VerdictScope = "Temporary WH_KEYBOARD_LL X-only suppression in the exact own native input foreground, with a local PostMessage relay to production OverlayHost and independent child WM/Raw/async observations. This is not a full input-isolation feature.",
            FullIsolation = false, XNativeObservedChannelsBlocked = SuppressionXChannelsBlocked,
            Observer = SuppressionObserver, Checks = SuppressionChecks, Phases = SuppressionPhases,
            ContractFailures = SuppressionFailures, Hook = SuppressionHookDiagnostics,
            SuppressionRawControlBefore, SuppressionRawControlAfter, SuppressionEditorXEvents,
            SuppressionCleanupFailure, KeyboardSafetyGuards, KeyboardKeyInjections, KeyboardSafetyAbort, Monitors,
            Method = "Pass-before and pass-after controls use a separate process registering keyboard RIDEV_INPUTSINK and polling only GetAsyncKeyState(VK_X)'s high bit. Editing entry is recorded before explicit own native input activation. A guarded 180ms SendInput X pulse is suppressed by an X-only hook and explicitly posted only to this process's native input HWND.",
            Safety = "Only the self-created child and this process's validated production native input HWND receive test input. Other keys are never suppressed; the hook predicate reads only a volatile lease, and its own HWND/PID checks fail open. Finally releases only a synthetic X held by this experiment, disposes the hook, removes the event subscription and leaves editing. Main retains normal application cleanup.",
            Limitations = new[] { "Only VK_X and the native input route are exercised, not toolbar text input, Chinese IME, modifiers, all keys, mouse or DirectInput.",
                "No actual game is activated, paused, injected or inspected.",
                "SendInput is not asserted equivalent to physical keyboard HID input. A missing Raw positive control is unexercised and cannot be a passing Raw isolation result.",
                "PostMessage demonstrates only the local Host key event; it does not establish faithful WPF/TSF text input.",
                "The original 9/18 scoped keyboard regressions and 218-check reports are not modified by this experiment." }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
