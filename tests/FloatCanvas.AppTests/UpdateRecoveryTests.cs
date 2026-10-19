using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;

/// <summary>Real recovery packages, without starting windows, update workers or application shutdown.</summary>
internal static class UpdateRecoveryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<string, bool> check)
    {
        using (var f = new Fixture())
        {
            var previous = new WorkspaceState { LiveDocument = new() { Elements = [new() { Kind = ElementKind.Text, Text = "previous session" }] } };
            await f.Store.SaveAsync(previous, f.Assets);
            byte[] original = await File.ReadAllBytesAsync(f.Store.CurrentPath);
            f.BeginFresh();
            check("update fixture begins at an unwritten blank baseline", !f.History.ShouldWrite(f.Snapshot()));
            await f.Flush();
            var recovered = (await f.Store.LoadAsync())!;
            check("update preserves current blank canvas instead of reviving previous session", recovered.State.LiveDocument.Elements.Count == 0 && recovered.State.CaptureDocument is null);
            var archived = f.History.GetEntries().Single(entry => !entry.IsCurrent);
            byte[] archivedBytes = await File.ReadAllBytesAsync(Path.Combine(archived.DirectoryPath, "workspace.floatcanvas"));
            check("update archives earlier recovery before forcing a blank snapshot", original.SequenceEqual(archivedBytes));
            check("successful update snapshot records the committed fingerprint", Get<string>(f.Session, "_documentRecoveryFingerprint") == Fingerprint(f.Snapshot()));
            check("update snapshot finishes the recovery task and enables future normal saves", Get<Task>(f.Session, "_documentRecoveryTask").IsCompletedSuccessfully && f.History.ShouldWrite(f.Snapshot()));
        }
        using (var f = new Fixture())
        {
            using var pixel = new SKBitmap(new SKImageInfo(1, 1));
            pixel.Erase(SKColors.Red);
            using var encoded = pixel.Encode(SKEncodedImageFormat.Png, 100);
            string image = f.Assets.Add(encoded.ToArray());
            f.Live.Document.Elements.Add(new() { Kind = ElementKind.Image, AssetId = image, Width = 30, Height = 40 });
            var capture = f.Live.Document.CreateCapture(new(-200, 50, 600, 400), image);
            capture.Elements.Add(new() { Kind = ElementKind.Text, Text = "screenshot draft" });
            Set(f.Session, "_capture", new EditorSession(capture, f.Assets)); Set(f.Session, "_captureOrigin", new PointD(-200, 50));
            string before = JsonSerializer.Serialize(f.Snapshot());
            await f.Flush(); var recovered = (await f.Store.LoadAsync())!;
            check("update snapshot preserves live objects, screenshot draft and capture origin", JsonSerializer.Serialize(recovered.State) == before);
            check("update snapshot carries resources required by both documents", recovered.Assets.TryGet(image, out var resource) && resource.Data.Span.SequenceEqual(f.Assets.Get(image).Data.Span));
        }
        using (var f = new Fixture())
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(f.Session, "_documentRecoveryTask", pending.Task);
            Task save = f.Flush();
            check("update waits for the preceding recovery write", !save.IsCompleted && !File.Exists(f.Store.CurrentPath));
            pending.SetResult(); await save;
            check("update saves its own snapshot after the preceding recovery write", (await f.Store.LoadAsync()) is not null);
        }
        using (var f = new Fixture())
        {
            // Recovery storage preserves original resources even when an optional thumbnail cannot decode one.
            byte[] damagedImage = [137, 80, 78, 71, 13, 10, 26, 10];
            string image = f.Assets.Add(damagedImage);
            f.Live.Document.Elements.Add(new() { Kind = ElementKind.Image, AssetId = image, Width = 30, Height = 40 });
            await f.Flush();
            var recovered = (await f.Store.LoadAsync())!;
            check("optional update preview decode failure does not block recovery persistence", recovered.State.LiveDocument.Elements.Single().AssetId == image &&
                recovered.Assets.Get(image).Data.Span.SequenceEqual(damagedImage));
            check("failed update preview is omitted rather than saving an invalid thumbnail", await f.Store.LoadPreviewAsync() is null);
        }
        using (var f = new Fixture())
        {
            Directory.CreateDirectory(f.Root); File.WriteAllText(f.Store.DirectoryPath, "block recovery directory creation");
            string? before = NullableField(f.Session, "_documentRecoveryFingerprint") as string;
            bool failed = false; try { await f.Flush(); } catch (IOException) { failed = true; }
            check("update save failure propagates before installer handoff", failed && !File.Exists(f.Store.CurrentPath));
            check("failed update snapshot does not mark recovery as saved", before == NullableField(f.Session, "_documentRecoveryFingerprint") as string && Get<Task>(f.Session, "_documentRecoveryTask").IsCompletedSuccessfully);
        }
        using (var f = new Fixture())
        {
            await f.Store.SaveAsync(new WorkspaceState { LiveDocument = new() { Elements = [new() { Kind = ElementKind.Number, Number = 8 }] } }, f.Assets);
            byte[] original = await File.ReadAllBytesAsync(f.Store.CurrentPath);
            File.WriteAllText(Path.Combine(f.Store.DirectoryPath, "history"), "block history directory creation");
            f.BeginFresh();
            bool failed = false; try { await f.Flush(); } catch (IOException) { failed = true; }
            byte[] unchanged = await File.ReadAllBytesAsync(f.Store.CurrentPath);
            check("update aborts when preserving previous recovery history fails", failed && original.SequenceEqual(unchanged));
        }
        using (var f = new Fixture())
        {
            f.Lifetime.Cancel();
            bool cancelled = false; try { await f.Flush(); } catch (OperationCanceledException) { cancelled = true; }
            check("update rejects a canceled session before saving or handing off", cancelled && !File.Exists(f.Store.CurrentPath));
        }
    }

    private static string Fingerprint(WorkspaceState state) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state)));
    private static object? NullableField(object target, string field) => typeof(DesktopSession).GetField(field, Private)!.GetValue(target);
    private static T Get<T>(object target, string field) => (T)NullableField(target, field)!;
    private static void Set(object target, string field, object value) => typeof(DesktopSession).GetField(field, Private)!.SetValue(target, value);
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "FloatCanvas-update-recovery-" + Guid.NewGuid().ToString("N"));
        internal RecoveryStore Store { get; }
        internal AssetLibrary Assets { get; } = new();
        internal EditorSession Live { get; }
        internal WorkspaceRecoveryHistory History { get; }
        internal CancellationTokenSource Lifetime { get; } = new();
        private SceneRenderer Renderer { get; } = new();
        internal DesktopSession Session { get; } = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        internal Fixture()
        {
            Store = new(Path.Combine(Root, "recovery")); Live = new(new SceneDocument(), Assets); History = new(Store);
            Set(Session, "_recoveryStore", Store); Set(Session, "_workspaceRecoveryHistory", History);
            Set(Session, "_assets", Assets); Set(Session, "_live", Live); Set(Session, "_documentLifetime", Lifetime);
            Set(Session, "_renderer", Renderer);
            Set(Session, "_settings", new UserSettings());
            Set(Session, "_documentRecoveryTask", Task.CompletedTask);
            var files = typeof(DesktopSession).GetField("_documentFiles", Private)!; files.SetValue(Session, Activator.CreateInstance(files.FieldType));
        }
        internal WorkspaceState Snapshot() => (WorkspaceState)typeof(DesktopSession).GetMethod("SnapshotWorkspace", Private)!.Invoke(Session, null)!;
        internal void BeginFresh() { var snapshot = Snapshot(); History.BeginFreshWorkspace(snapshot); Set(Session, "_documentRecoveryFingerprint", Fingerprint(snapshot)); }
        internal Task Flush() => (Task)typeof(DesktopSession).GetMethod("FlushRecoveryForUpdateAsync", Private)!.Invoke(Session, null)!;
        public void Dispose() { Renderer.Dispose(); Lifetime.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
