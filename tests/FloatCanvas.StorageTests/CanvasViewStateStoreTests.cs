using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class CanvasViewStateStoreTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-view-store-" + Guid.NewGuid().ToString("N"));
        var time = new DateTimeOffset(2026, 11, 3, 1, 0, 0, TimeSpan.FromHours(8));
        try
        {
            var store = new CanvasViewStateStore(directory, () => time);
            var viewport = new RectD(-1920, 0, 1920, 1080);
            var view = new CanvasViewTransform(4, new(55, -99), new(12, -34));
            store.Remember("source/ordinary-group/independent", view, viewport);
            store.Remember("source/root/overview", CanvasViewTransform.Identity, viewport);
            store.BindWorkspace("resource:live", "source");
            store.CopyNamespace("source", "copy");
            await store.SaveAsync();
            var reopened = new CanvasViewStateStore(directory, () => time);
            var larger = new RectD(0, 0, 3840, 2160);
            check(reopened.TryRestore("source/ordinary-group/independent", larger, out var restored) && restored.Zoom == 4 && restored.PlaneOffset == view.PlaneOffset &&
                restored.ToCanvas(larger.Center).DistanceTo(view.ToCanvas(viewport.Center)) < 1e-7,
                "local view survives reopening and moves the same observation center to a new viewport");
            check(!reopened.TryRestore("other-copy/ordinary-group/independent", larger, out _), "a different resource/copy cannot reuse a view accidentally");
            check(reopened.TryRestore("source/root/overview", viewport, out var identity) && identity == CanvasViewTransform.Identity,
                "identity is retained so applying and reopening does not trigger first-entry framing again");
            check(reopened.WorkspaceNamespace("resource:live") == "source" && reopened.TryRestore("copy/ordinary-group/independent", viewport, out var copied) && copied == view,
                "workspace association and all copied scope views survive local persistence");
            reopened.Remember("copy/ordinary-group/independent", CanvasViewTransform.Identity, viewport);
            check(reopened.TryRestore("source/ordinary-group/independent", viewport, out var original) && original == view,
                "editing a saved copy's view cannot overwrite the source view");
            reopened.CopyNamespace("source", "copy", overwrite: false);
            check(reopened.TryRestore("copy/ordinary-group/independent", viewport, out var retained) && retained == CanvasViewTransform.Identity,
                "reentering a history namespace preserves its own newer viewing state");
            time += TimeSpan.FromDays(31);
            check(!reopened.TryRestore("source/ordinary-group/independent", larger, out _), "expired views fall back without touching authored data");
            await File.WriteAllTextAsync(Path.Combine(directory, "canvas-view-state.json"), "{broken");
            var damaged = new CanvasViewStateStore(directory, () => time);
            check(damaged.ReadError is not null && !damaged.TryRestore("source/root/overview", viewport, out _), "damaged cache is nonfatal and reported independently");
            damaged.Remember("new", view, viewport); await damaged.SaveAsync();
            check(new CanvasViewStateStore(directory, () => time).TryRestore("new", viewport, out _), "cache recovers through a subsequent atomic save");
            check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "atomic cache save leaves no temporary files");
        }
        finally
        {
            // This test owns the exact unique directory; it never enumerates external paths for deletion.
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
