using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class PresetHistoryTests
{
    internal static void Run(Action<string, bool> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-preset-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Tools(root, check); Canvas(root, check); Colors(root, check); Mixed(root, check); Safety(root, check); }
        finally { Directory.Delete(root, true); }
    }

    private static void Tools(string root, Action<string, bool> check)
    {
        var session = new EditorSession(); string path = Path.Combine(root, "tools.json");
        var library = new ToolPresetLibrary(path) { HistorySession = () => session };
        var first = library.Save("first", new ToolParameters { Tool = EditorTool.Rectangle, DefaultWidth = 87 });
        var second = library.Save("second", new ToolParameters { Tool = EditorTool.Rectangle });
        check("preset history tool saves enter the shared queue", session.UndoCount == 2 && session.UndoLabel!.Contains("second"));
        session.Undo(); check("preset history undo creation persists removal of only last resource", library.User.Single().Id == first.Id && new ToolPresetLibrary(path).User.Single().Id == first.Id);
        session.Redo(); check("preset history redo creation keeps stable ID and definition", library.User[1].Id == second.Id && new ToolPresetLibrary(path).User[1].Id == second.Id);
        library.Rename(first.Id, "renamed"); session.Undo();
        check("preset history tool rename can undo", library.User[0].Name == "first"); session.Redo();
        check("preset history tool rename can redo", library.User[0].Name == "renamed");
        var changed = library.User[0].Parameters.Clone(); changed.DefaultWidth = 123; library.Update(first.Id, changed); session.Undo();
        check("preset history tool overwrite restores exact saved parameters", library.User[0].Parameters.DefaultWidth == 87); session.Redo();
        check("preset history tool overwrite can redo", library.User[0].Parameters.DefaultWidth == 123);
        library.Move(second.Id, first.Id); session.Undo(); check("preset history tool ordering can undo", library.User[0].Id == first.Id); session.Redo();
        check("preset history tool ordering can redo", library.User[0].Id == second.Id);
        library.Delete(first.Id); session.Undo(); check("preset history tool deletion restores identity name parameters and position", library.User[1].Id == first.Id && library.User[1].Name == "renamed" && library.User[1].Parameters.DefaultWidth == 123); session.Redo();
        check("preset history tool deletion can redo", library.User.Single().Id == second.Id);
        int count = session.UndoCount; library.Rename(second.Id, "second"); library.Update(second.Id, library.User.Single().Parameters); library.Move(second.Id, second.Id); library.Delete("missing");
        check("preset history no-op tool operations add no queue entries", session.UndoCount == count);
        session.Undo(); library.Save("branch", new ToolParameters()); check("preset history new resource edit clears abandoned redo branch", session.RedoCount == 0);
    }

    private static void Canvas(string root, Action<string, bool> check)
    {
        var session = new EditorSession(); string path = Path.Combine(root, "canvas.json");
        var library = new CanvasPresetLibrary(path) { HistorySession = () => session };
        var first = library.Save("canvas-first", new CanvasSettings { BackgroundColor = 0xFF123456 });
        var second = library.Save("canvas-second", new CanvasSettings()); session.Undo();
        check("preset history canvas creation can undo and persists", new CanvasPresetLibrary(path).User.Single().Id == first.Id); session.Redo();
        check("preset history canvas creation can redo", library.User[1].Id == second.Id);
        library.Rename(first.Id, "canvas-renamed"); session.Undo(); check("preset history canvas rename can undo", library.User[0].Name == "canvas-first"); session.Redo();
        library.Update(first.Id, new CanvasSettings { BackgroundColor = 0xFFABCDEF }); session.Undo();
        check("preset history canvas overwrite can undo", library.User[0].Parameters.BackgroundColor == 0xFF123456); session.Redo();
        check("preset history canvas overwrite can redo", library.User[0].Parameters.BackgroundColor == 0xFFABCDEF);
        library.Move(second.Id, first.Id); session.Undo(); check("preset history canvas ordering can undo", library.User[0].Id == first.Id); session.Redo();
        check("preset history canvas ordering can redo", library.User[0].Id == second.Id);
        library.Delete(first.Id); session.Undo(); check("preset history canvas deletion restores full identity and position", library.User[1].Id == first.Id && library.User[1].Name == "canvas-renamed"); session.Redo();
        check("preset history canvas deletion can redo", library.User.Single().Id == second.Id);
        int count = session.UndoCount; library.Rename(second.Id, "canvas-second"); library.Delete("missing"); library.Move(second.Id, second.Id);
        check("preset history no-op canvas operations add no queue entries", session.UndoCount == count);
    }

    private static void Colors(string root, Action<string, bool> check)
    {
        var session = new EditorSession(); string path = Path.Combine(root, "colors.json");
        var library = new ColorLibrary(path) { HistorySession = () => session };
        var first = library.Save("color-first", 0xFF123456); var second = library.Save("color-second", 0xFFABCDEF);
        session.Undo(); check("preset history color creation can undo and persists", new ColorLibrary(path).Swatches.Single() == first); session.Redo();
        check("preset history color creation can redo", library.Swatches[1] == second);
        library.Rename(first.Id, "color-renamed"); session.Undo(); check("preset history color rename can undo", library.Swatches[0] == first); session.Redo();
        library.Update(first.Id, 0x7F112233); session.Undo(); check("preset history color overwrite restores exact ARGB", library.Swatches[0].Value == first.Value); session.Redo();
        check("preset history color overwrite can redo", library.Swatches[0].Value == 0x7F112233);
        library.Move(second.Id, first.Id); session.Undo(); check("preset history color ordering can undo", library.Swatches[0].Id == first.Id); session.Redo();
        check("preset history color ordering can redo", library.Swatches[0].Id == second.Id);
        library.Delete(first.Id); library.Remember(0xFF102030); int count = session.UndoCount; session.Undo();
        check("preset history swatch undo preserves recent-color history", library.Swatches[1].Id == first.Id && library.History[0] == 0xFF102030 && session.UndoCount == count - 1);
        library.Remember(0xFF405060); session.Redo();
        check("preset history swatch redo preserves later recent-color history", library.Swatches.Single().Id == second.Id && library.History[0] == 0xFF405060);
        count = session.UndoCount; library.Rename(second.Id, "color-second"); library.Update(second.Id, second.Value); library.Move(second.Id, second.Id); library.Delete("missing");
        check("preset history no-op color operations add no queue entries", session.UndoCount == count);
        library.Save("duplicate", second.Value); library.RemovePinned(second.Value); session.Undo();
        check("preset history legacy remove-pinned restores every matching swatch", library.Swatches.Count == 2 && library.Swatches[0].Id == second.Id); session.Redo();
        check("preset history legacy remove-pinned can redo", library.Swatches.Count == 0);
    }

    private static void Mixed(string root, Action<string, bool> check)
    {
        var session = new EditorSession();
        var tools = new ToolPresetLibrary(Path.Combine(root, "mixed-tools.json")) { HistorySession = () => session };
        var canvas = new CanvasPresetLibrary(Path.Combine(root, "mixed-canvas.json")) { HistorySession = () => session };
        var colors = new ColorLibrary(Path.Combine(root, "mixed-colors.json")) { HistorySession = () => session };
        session.Execute("scene-A", scene => scene.Canvas.BackgroundColor = 0xFF111111); long version = session.ContentVersion, revision = session.SceneRevision;
        string snapshot = JsonSerializer.Serialize(session.Document); var document = session.Document;
        var tool = tools.Save("tool-A", new ToolParameters()); var board = canvas.Save("board-A", new CanvasSettings()); var color = colors.Save("color-A", 0xFF123456);
        check("preset history resources neither clone nor dirty the document", ReferenceEquals(document, session.Document) && session.ContentVersion == version && session.SceneRevision == revision && !session.HasPendingChanges && JsonSerializer.Serialize(session.Document) == snapshot);
        session.Execute("scene-B", scene => scene.Canvas.BackgroundColor = 0xFF222222);
        session.Undo(); check("preset history mixed queue undoes latest scene first", session.Document.Canvas.BackgroundColor == 0xFF111111 && tools.User.Count == 1 && colors.Swatches.Count == 1);
        session.Undo(); check("preset history mixed queue then undoes color", colors.Swatches.Count == 0 && canvas.User.Count == 1);
        session.Undo(); check("preset history mixed queue then undoes canvas preset", canvas.User.Count == 0 && tools.User.Count == 1);
        session.Undo(); check("preset history mixed queue then undoes tool preset", tools.User.Count == 0 && session.Document.Canvas.BackgroundColor == 0xFF111111);
        session.Undo(); check("preset history mixed queue finally undoes earliest scene", session.Document.Canvas.BackgroundColor == 0);
        for (int i = 0; i < 5; i++) session.Redo();
        check("preset history mixed redo restores all IDs and latest scene", session.Document.Canvas.BackgroundColor == 0xFF222222 && tools.User.Single().Id == tool.Id && canvas.User.Single().Id == board.Id && colors.Swatches.Single() == color);
        var other = new EditorSession(); tools.HistorySession = () => other; var later = tools.Save("other-document", new ToolParameters());
        tools.HistorySession = () => session; session.Undo(); session.Undo(); session.Undo(); session.Undo();
        check("preset history undo in one document keeps unrelated preset saved in another", tools.User.Single().Id == later.Id);
        session.Redo(); check("preset history redo does not erase unrelated resource", tools.User.Any(p => p.Id == later.Id) && tools.User.Any(p => p.Id == tool.Id));
    }

    private static void Safety(string root, Action<string, bool> check)
    {
        var session = new EditorSession(); string path = Path.Combine(root, "external.json");
        var tools = new ToolPresetLibrary(path) { HistorySession = () => session }; var first = tools.Save("owned", new ToolParameters());
        var external = new ToolPresetLibrary(path); var later = external.Save("external", new ToolParameters());
        string bytes = File.ReadAllText(path); int count = session.UndoCount; bool rejected = false;
        try { session.Undo(); } catch (IOException) { rejected = true; }
        check("preset history external writer blocks stale undo without data loss or queue movement", rejected && session.UndoCount == count && session.RedoCount == 0 && File.ReadAllText(path) == bytes && new ToolPresetLibrary(path).User.Any(p => p.Id == later.Id));
        rejected = false; try { tools.Save("blocked", new ToolParameters()); } catch (IOException) { rejected = true; }
        check("preset history external writer blocks stale forward save atomically", rejected && session.UndoCount == count && tools.User.Count == 1 && File.ReadAllText(path) == bytes);
        var active = new EditorSession(); var transactional = new ToolPresetLibrary(Path.Combine(root, "transaction.json")) { HistorySession = () => active }; active.BeginTransaction("gesture");
        rejected = false; try { transactional.Save("must-not-save", new ToolParameters()); } catch (InvalidOperationException) { rejected = true; }
        check("preset history active scene transaction rejects before resource write", rejected && !File.Exists(Path.Combine(root, "transaction.json")) && transactional.User.Count == 0 && active.UndoCount == 0); active.Cancel();
        string blocked = Path.Combine(root, "directory.json"); Directory.CreateDirectory(blocked);
        var failed = new ToolPresetLibrary(blocked) { HistorySession = () => active }; rejected = false;
        try { failed.Save("failed", new ToolParameters()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
        check("preset history failed initial save leaves library and queue unchanged", rejected && failed.User.Count == 0 && active.UndoCount == 0);
        string redoPath = Path.Combine(root, "redo-failure.json"); var redoSession = new EditorSession();
        var redoLibrary = new ToolPresetLibrary(redoPath) { HistorySession = () => redoSession }; redoLibrary.Save("redo", new ToolParameters()); redoSession.Undo();
        File.WriteAllText(redoPath, File.ReadAllText(redoPath) + " "); rejected = false;
        try { redoSession.Redo(); } catch (IOException) { rejected = true; }
        check("preset history failed redo keeps redo available and library unchanged", rejected && redoSession.RedoCount == 1 && redoSession.UndoCount == 0 && redoLibrary.User.Count == 0);
        string fullPath = Path.Combine(root, "capacity.json"); var fullColors = new ColorLibrary(fullPath);
        for (int i = 0; i < 100; i++) fullColors.Save("capacity-" + i, 0xFF000000 | (uint)i);
        var deleteSession = new EditorSession(); fullColors.HistorySession = () => deleteSession;
        string deletedId = fullColors.Swatches[0].Id; fullColors.Delete(deletedId);
        var laterSession = new EditorSession(); fullColors.HistorySession = () => laterSession; var replacement = fullColors.Save("later", 0xFF112233);
        string fullBytes = File.ReadAllText(fullPath); rejected = false;
        try { deleteSession.Undo(); } catch (InvalidDataException) { rejected = true; }
        check("preset history undo cannot exceed resource capacity after another document fills the library", rejected && deleteSession.UndoCount == 1 && fullColors.Swatches.Count == 100 && !fullColors.Swatches.Any(color => color.Id == deletedId) && fullColors.Swatches.Any(color => color.Id == replacement.Id) && File.ReadAllText(fullPath) == fullBytes && new ColorLibrary(fullPath).Swatches.Count == 100);
    }
}
