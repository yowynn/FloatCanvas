using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class ApplicationDiagnosticsTests
{
    internal static void Run(Action<string, bool> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "diagnostic-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        BasicAndFailures(root, check);
        Rotation(root, check);
        Feedback(check);
        Console.WriteLine("Diagnostic log test artifacts: " + root);
    }

    private static void BasicAndFailures(string root, Action<string, bool> check)
    {
        string profile = Path.Combine(root, "explicit-test-profile");
        var log = new ApplicationDiagnostics(profile);
        check("diagnostic logger construction never writes the requested profile", !Directory.Exists(profile));
        Guid request = Guid.NewGuid();
        var outcome = Native("ZeroForeground", CaptureRestoreStatus.ForegroundChanged);
        check("capture metadata is written to the explicitly supplied test profile", log.Write("Capture.Outcome", outcome, requestId: request));
        using (var parsed = JsonDocument.Parse(File.ReadAllLines(log.LogPath).Single()))
        {
            var record = parsed.RootElement;
            check("log contains timestamp, process and running assembly version", record.GetProperty("AtUtc").GetDateTimeOffset().Offset == TimeSpan.Zero && record.GetProperty("ProcessId").GetInt32() == Environment.ProcessId && record.GetProperty("Version").GetString() is { Length: > 0 });
            check("log distinguishes requested profile from actual open-handle profile", record.GetProperty("RequestedProfile").GetString() == profile && string.Equals(record.GetProperty("ActualProfile").GetString(), profile, StringComparison.OrdinalIgnoreCase));
            check("capture operation, reason and request are independently traceable", record.GetProperty("RequestId").GetGuid() == request && record.GetProperty("Metadata").GetProperty("OperationId").GetInt64() == 1 && record.GetProperty("Metadata").GetProperty("Reason").GetString() == "ZeroForeground" && record.GetProperty("Metadata").GetProperty("Status").GetString() == "ForegroundChanged");
            var names = record.GetProperty("Metadata").EnumerateObject().Select(p => p.Name).ToArray();
            check("native diagnostic schema contains metadata without pixels, document text or key contents", names.Length == 20 && names.Contains("AllowedForegrounds") && names.Contains("ExternalInputs") && names.Contains("Strategy") && !names.Any(name => name is "PngBytes" or "Elements" or "Text" or "KeyboardState" or "CommandLine"));
        }
        Exception error;
        try { ThrowFixture(); throw new InvalidOperationException("unreachable"); }
        catch (InvalidDataException caught) { error = caught; }
        check("caught command errors retain their exception stack in the shared safe logger", log.Write("Command.Error", exception: error));
        using (var parsed = JsonDocument.Parse(File.ReadAllLines(log.LogPath).Last()))
            check("error record includes exception type, message and original throwing frame", parsed.RootElement.GetProperty("Exception").GetProperty("Type").GetString() == typeof(InvalidDataException).FullName && parsed.RootElement.GetProperty("Exception").GetProperty("Message").GetString() == "fixture failure" && parsed.RootElement.GetProperty("Exception").GetProperty("Stack").GetString()!.Contains(nameof(ThrowFixture), StringComparison.Ordinal));
        check("unhandled errors retain the existing errors.log filename", log.WriteUnhandled(error) && File.Exists(Path.Combine(profile, "errors.log")));
        int before = File.ReadAllLines(log.LogPath).Length;
        check("metadata serialization failure cannot escape into capture execution", !log.Write("Capture.SerializationFailure", new ThrowingMetadata()) && File.ReadAllLines(log.LogPath).Length == before);
        string blocked = Path.Combine(root, "profile-is-a-file"); File.WriteAllText(blocked, "keep");
        var unwritable = new ApplicationDiagnostics(blocked);
        check("log IO failure is contained without changing the profile or existing user data", !unwritable.Write("Capture.Begin") && File.ReadAllText(blocked) == "keep" && unwritable.LogPath == Path.Combine(blocked, ApplicationDiagnostics.FileName));
    }

    private static void Rotation(string root, Action<string, bool> check)
    {
        string profile = Path.Combine(root, "rotation-profile");
        const long limit = 4096;
        var log = new ApplicationDiagnostics(profile, limit);
        for (int i = 0; i < 80; i++)
            if (!log.Write("Capture.Outcome", new { Index = i, Detail = new string('x', 96) })) throw new InvalidOperationException("Fixture log could not be written.");
        var files = Directory.GetFiles(profile, ApplicationDiagnostics.FileName + "*");
        check("normal runtime diagnostics retain current plus only two rotated files", files.Length == 3 && files.All(file => file.EndsWith("diagnostics.log", StringComparison.Ordinal) || file.EndsWith("diagnostics.log.1", StringComparison.Ordinal) || file.EndsWith("diagnostics.log.2", StringComparison.Ordinal)));
        check("each rotated log and total local space stay bounded", files.All(file => new FileInfo(file).Length <= limit) && files.Sum(file => new FileInfo(file).Length) <= limit * 3);
        bool valid = true;
        foreach (var line in files.SelectMany(File.ReadAllLines))
        { try { using var json = JsonDocument.Parse(line); valid &= json.RootElement.TryGetProperty("Event", out _); } catch (JsonException) { valid = false; } }
        check("rotation preserves complete independently readable JSON lines", valid);
        using (var latest = JsonDocument.Parse(File.ReadAllLines(log.LogPath).Last()))
            check("rotation keeps the newest capture outcome", latest.RootElement.GetProperty("Metadata").GetProperty("Index").GetInt32() == 79);
        check("one oversized event is bounded without writing broken JSON", log.Write("Capture.Oversized", new { Detail = new string('x', 50000) }));
        using (var oversized = JsonDocument.Parse(File.ReadAllLines(log.LogPath).Last()))
            check("oversized payload is explicitly omitted while event identity remains", oversized.RootElement.GetProperty("EntryTruncated").GetBoolean() && oversized.RootElement.GetProperty("Event").GetString() == "Capture.Oversized" && oversized.RootElement.GetProperty("Metadata").ValueKind == JsonValueKind.Null);
        Parallel.For(0, 30, i => { if (!log.Write("Concurrent", new { Index = i })) throw new InvalidOperationException("Concurrent log failed."); });
        check("concurrent diagnostic writes remain bounded and individually parseable", Directory.GetFiles(profile, "diagnostics.log*").All(file =>
        {
            if (new FileInfo(file).Length > limit) return false;
            foreach (var line in File.ReadAllLines(file)) { using var json = JsonDocument.Parse(line); }
            return true;
        }));
    }

    private static void Feedback(Action<string, bool> check)
    {
        foreach (string reason in new[] { "ZeroForeground", "InvalidExpected", "LeaseFailure", "Failed" })
        {
            var outcome = Native(reason, CaptureRestoreStatus.Failed);
            check("unexpected " + reason + " remains visibly reportable after focus cleanup", SnapshotFailureFeedback.ShouldNotify(CaptureRestoreStatus.Failed, outcome, outcome, false, "ExternalFocus"));
        }
        foreach (string reason in new[] { "ExplicitCancel", "SessionChanged", "AppGuard", "ExternalInput", "UnexpectedForeground", "ForegroundEventMismatch" })
        {
            var outcome = Native(reason, CaptureRestoreStatus.ForegroundChanged);
            check("normal user departure " + reason + " is logged without a failure interruption", !SnapshotFailureFeedback.ShouldNotify(CaptureRestoreStatus.ForegroundChanged, outcome, outcome, false, "ExternalFocus"));
        }
        var ownedForeground = Native("OwnedForegroundUnexpected", CaptureRestoreStatus.ForegroundChanged);
        check("unexpected activation of an owned window remains visible instead of being classified as an external departure",
            SnapshotFailureFeedback.ShouldNotify(CaptureRestoreStatus.ForegroundChanged, ownedForeground, ownedForeground, false, "ExternalFocus"));
        var unknown = Native("Unknown", CaptureRestoreStatus.Failed);
        check("explicit Escape or hide suppresses feedback even if a late failure follows", !SnapshotFailureFeedback.ShouldNotify(CaptureRestoreStatus.Failed, unknown, unknown, true, "Escape"));
        var restored = Native("Restored", CaptureRestoreStatus.Restored);
        var external = restored with { ExternalInputObserved = true };
        check("observed external input after restoration suppresses a late failure", !SnapshotFailureFeedback.ShouldNotify(CaptureRestoreStatus.Failed, restored, external, false, null));
        var changed = restored with { UserIntentChanged = true };
        check("new explicit intent after restoration suppresses a late failure", !SnapshotFailureFeedback.ShouldNotify(CaptureRestoreStatus.Failed, restored, changed, false, null));
        check("failure cleanup's later intent increments cannot hide the original unexpected failure", SnapshotFailureFeedback.ShouldNotify(CaptureRestoreStatus.Failed, unknown, changed, false, "ExternalFocus"));
        check("CanvasOnly rendering does not notify after an ordinary external departure", !SnapshotFailureFeedback.ShouldNotify(CaptureRestoreStatus.Failed, null, null, false, "ExternalFocus"));
        check("a still-current raster exception is visibly reportable", SnapshotFailureFeedback.ShouldNotify(CaptureRestoreStatus.Failed, null, null, false, null));
        var session = new EditorSession(); var request = new SnapshotCaptureRequest(session, 3);
        request.Cancel("ExternalFocus"); request.Cancel("Escape", true);
        check("a later explicit cancellation always wins over earlier cleanup for notifications", request.Cancelled && request.UserCancellation && request.CancellationReason == "ExternalFocus");
        var preference = L.Preference;
        try
        {
            foreach (var language in new[] { "zh-CN", "en" })
            {
                L.Initialize(language);
                check(language + " snapshot failure identifies a concrete log location", L.Format("Snapshot.FailureLog", @"C:\test-profile\diagnostics.log").Contains(@"C:\test-profile\diagnostics.log", StringComparison.Ordinal));
                check(language + " snapshot feedback has translated title and unavailable-log text", L.Get("Snapshot.FailureTitle") != "Snapshot.FailureTitle" && L.Format("Snapshot.FailureLogUnavailable", "test-log").Contains("test-log", StringComparison.Ordinal));
            }
        }
        finally { L.Initialize(preference); }
    }

    private static CaptureDiagnosticEvent Native(string reason, CaptureRestoreStatus status) =>
        new(1, "Outcome", reason, status, 3, 4, 10, 0, 10, 0, 0, 0, 3, 0, 0, false, false);
    private static void ThrowFixture() => throw new InvalidDataException("fixture failure");
    private sealed class ThrowingMetadata { public string Value => throw new InvalidOperationException("fixture serialization failure"); }
}
