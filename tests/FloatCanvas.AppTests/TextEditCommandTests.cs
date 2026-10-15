using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;

internal static class TextEditCommandTests
{
    public static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var results = new List<(string Name, bool Passed)>();
        foreach (var kind in new[] { ElementKind.Text, ElementKind.Number })
        foreach (bool removed in new[] { false, true })
        {
            var session = new EditorSession();
            session.Add(new CanvasElement { Kind = kind, Text = "Original", Number = 7, Width = 160, Height = 40 });
            var id = session.Document.Elements.Single().Id;
            if (removed) session.DeleteSelection(); else session.SetLocked(true);
            string before = JsonSerializer.Serialize(session.Document);
            int undo = session.UndoCount, redo = session.RedoCount;
            bool committed = false; string? error = null; Exception? exception = null;
            try { committed = TextEditCommand.TryCommit(session, id, kind, kind == ElementKind.Number ? "42" : "Changed", new(), new(), renderer, out error); }
            catch (Exception ex) { exception = ex; }
            var name = $"{kind} editor rejects {(removed ? "removed" : "locked")} target without throwing or changing document/history";
            bool passed = !committed && exception is null && !string.IsNullOrWhiteSpace(error) &&
                JsonSerializer.Serialize(session.Document) == before && session.UndoCount == undo && session.RedoCount == redo;
            Console.WriteLine($"TEXT_SUBMIT_GUARD {name}: passed={passed}, committed={committed}, exception={exception?.GetType().Name ?? "none"}");
            results.Add((name, passed));
        }
        foreach (var result in results) check(result.Name, result.Passed);
        RetryAndTypeChanges(renderer, check);
        NormalCommits(renderer, check);
        LocalizedErrors(renderer, check);
    }

    private static EditorSession Existing(ElementKind kind)
    {
        var session = new EditorSession();
        session.Add(new CanvasElement { Kind = kind, Text = "Original", Number = 7, Width = 160, Height = 40,
            Transform = Affine2D.Translation(50, 60).Compose(Affine2D.Rotation(30)) });
        return session;
    }

    private static void RetryAndTypeChanges(SceneRenderer renderer, Action<string, bool> check)
    {
        foreach (var kind in new[] { ElementKind.Text, ElementKind.Number })
        {
            var session = Existing(kind); var id = session.Document.Elements.Single().Id;
            var draft = kind == ElementKind.Number ? "42" : "Kept draft";
            session.SetLocked(true);
            check(kind + " locked failure returns an actionable localized message", !TextEditCommand.TryCommit(session, id, kind, draft, new(), new(), renderer, out var error) &&
                error == L.Get("Text.TargetLocked"));
            session.SetLocked(false); int before = session.UndoCount;
            check(kind + " same draft commits after unlocking with one undo step", TextEditCommand.TryCommit(session, id, kind, draft, new(), new(), renderer, out error) && error is null &&
                session.UndoCount == before + 1 && (kind == ElementKind.Number ? session.Document.Elements.Single().Number == 42 : session.Document.Elements.Single().Text == draft));
            check(kind + " retried edit is undone atomically", session.Undo() &&
                (kind == ElementKind.Number ? session.Document.Elements.Single().Number == 7 : session.Document.Elements.Single().Text == "Original"));

            session = Existing(kind); id = session.Document.Elements.Single().Id;
            session.Execute("Change target type while editor is open", d => d.Elements[0].Kind = kind == ElementKind.Text ? ElementKind.Number : ElementKind.Text);
            string snapshot = JsonSerializer.Serialize(session.Document); before = session.UndoCount;
            check(kind + " editor rejects a changed type without mutation", !TextEditCommand.TryCommit(session, id, kind, draft, new(), new(), renderer, out error) &&
                error == L.Get("Text.TargetTypeChanged") && JsonSerializer.Serialize(session.Document) == snapshot && session.UndoCount == before);

            session = Existing(kind); id = session.Document.Elements.Single().Id;
            session.DeleteSelection(); before = session.UndoCount;
            check(kind + " removed failure returns a localized recovery hint", !TextEditCommand.TryCommit(session, id, kind, draft, new(), new(), renderer, out error) &&
                error == L.Get("Text.TargetMissing") && session.UndoCount == before);
            session.Undo(); before = session.UndoCount;
            check(kind + " same draft commits after undoing deletion", TextEditCommand.TryCommit(session, id, kind, draft, new(), new(), renderer, out error) &&
                error is null && session.UndoCount == before + 1 && session.Document.Elements.Single().Id == id);
        }
        var active = Existing(ElementKind.Text); var activeId = active.Document.Elements.Single().Id;
        active.BeginTransaction("Unfinished canvas action");
        string preview = JsonSerializer.Serialize(active.Document); int undo = active.UndoCount;
        check("Text commit cannot cancel or overwrite an active canvas transaction", !TextEditCommand.TryCommit(active, activeId, ElementKind.Text, "Pending", new(), new(), renderer, out var failure) &&
            failure == L.Get("Text.ActionInProgress") && active.IsTransactionActive && active.UndoCount == undo && JsonSerializer.Serialize(active.Document) == preview);
        active.Cancel();
    }

    private static void NormalCommits(SceneRenderer renderer, Action<string, bool> check)
    {
        var session = new EditorSession(); var style = new ElementStyle { FontSize = 28 };
        check("New text still commits through the production command", TextEditCommand.TryCommit(session, null, ElementKind.Text, "First\nSecond", style, new(123, 67), renderer, out var error) && error is null);
        var created = session.Document.Elements.Single();
        check("New text preserves content, style and requested position in one history entry", created.Kind == ElementKind.Text && created.Text == "First\nSecond" &&
            created.Style.FontSize == 28 && created.Transform == Affine2D.Translation(123, 67) && session.UndoCount == 1);
        check("New text undo removes the whole element and redo restores it", session.Undo() && session.Document.Elements.Count == 0 && session.Redo() && session.Document.Elements.Single().Id == created.Id);

        session = Existing(ElementKind.Text); var id = session.Document.Elements.Single().Id;
        session.Execute("Change current layout", d => { d.Elements[0].Width = 90; d.Elements[0].Style.FontSize = 32; });
        session.Undo(); session.Redo();
        string original = JsonSerializer.Serialize(session.Document); int before = session.UndoCount;
        session.SetSelection([]);
        check("Text continuation resolves the current element by stable ID even after undo/redo or selection changes", TextEditCommand.TryCommit(session, id, ElementKind.Text,
            "A longer replacement that wraps", new() { FontSize = 9 }, new(999, 999), renderer, out error) && error is null && session.Document.Elements.Single().Text == "A longer replacement that wraps");
        var updated = session.Document.Elements.Single();
        check("Text continuation preserves the current width, style and rotation and remeasures height", updated.Width == 90 && updated.Style.FontSize == 32 &&
            updated.Transform == Affine2D.Translation(50, 60).Compose(Affine2D.Rotation(30)) && updated.Height == renderer.MeasureText(updated.Text, updated.Style, 90).Height && session.UndoCount == before + 1);
        check("One undo restores ordinary text continuation without changing geometry", session.Undo() && JsonSerializer.Serialize(session.Document) == original);

        session = Existing(ElementKind.Number); id = session.Document.Elements.Single().Id;
        foreach (var invalid in new[] { "0", "-1", "1000001", "1.5", "abc" })
        {
            original = JsonSerializer.Serialize(session.Document); before = session.UndoCount;
            check("Invalid number '" + invalid + "' preserves content and history", !TextEditCommand.TryCommit(session, id, ElementKind.Number, invalid, new(), new(), renderer, out error) &&
                error == L.Get("Text.InvalidNumber") && JsonSerializer.Serialize(session.Document) == original && session.UndoCount == before);
        }
        before = session.UndoCount;
        check("Normal number continuation still accepts the upper bound in one undo step", TextEditCommand.TryCommit(session, id, ElementKind.Number, "1000000", new(), new(), renderer, out error) &&
            error is null && session.Document.Elements.Single().Number == 1_000_000 && session.UndoCount == before + 1);
        check("One undo restores the original number and its transform", session.Undo() && session.Document.Elements.Single().Number == 7 &&
            session.Document.Elements.Single().Transform == Affine2D.Translation(50, 60).Compose(Affine2D.Rotation(30)));
    }

    private static void LocalizedErrors(SceneRenderer renderer, Action<string, bool> check)
    {
        var preference = L.Preference;
        try
        {
            foreach (var language in new[] { "en", "zh-CN" })
            {
                L.Initialize(language);
                var session = Existing(ElementKind.Text); var id = session.Document.Elements.Single().Id;
                session.SetLocked(true);
                check(language + " locked-target failure uses the selected interface language", !TextEditCommand.TryCommit(session, id, ElementKind.Text, "Kept", new(), new(), renderer, out var error) &&
                    error == L.Get("Text.TargetLocked") && error == (language == "en" ?
                        "This element is locked. Your input has been kept. Unlock the element and retry." : "该元素已被锁定。输入已保留，解锁后可以重试。"));
            }
        }
        finally { L.Initialize(preference); }
    }
}
