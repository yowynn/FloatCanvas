using System.Reflection;
using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Input = System.Windows.Input;

/// <summary>Layer metadata, persistence and actual detached WPF rename handlers; never opens a window.</summary>
internal static class LayerRenameTests
{
    public static void Run(Action<string, bool> check)
    {
        if (Wpf.Application.Current is null)
        {
            _ = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
            AppTheme.Apply("light");
        }
        Core(check);
        string language = L.Preference;
        try { foreach (string locale in new[] { "zh-CN", "en" }) { L.Initialize(locale); Panel(check); } }
        finally { L.Initialize(language); }
        Storage(check).GetAwaiter().GetResult();
        LayersWindowTests.Run(check);
        InspectorLayerPanelTests.Run(check);
    }

    private static void Core(Action<string, bool> check)
    {
        var session = new EditorSession();
        var element = new CanvasElement { Kind = ElementKind.Text, Text = "actual text", Width = 100, Height = 40 };
        session.Add(element); int before = session.UndoCount; long revision = session.SceneRevision;
        session.RenameElement(element.Id, "  注释 — Example  ");
        var named = session.Document.Elements.Single();
        check("rename trims a bilingual label and commits exactly one content revision", named.Name == "注释 — Example" && session.UndoCount == before + 1 && session.SceneRevision == revision + 1);
        check("rename never changes text, geometry or selection", named.Text == element.Text && named.Width == element.Width && session.Selection.SetEquals([element.Id]));
        session.Undo(); check("rename undo restores the localized default label", session.Document.Elements.Single().Name is null);
        session.Redo(); check("rename redo restores the custom label", session.Document.Elements.Single().Name == "注释 — Example");
        before = session.UndoCount; session.RenameElement(element.Id, "注释 — Example");
        check("unchanged rename creates no history entry", session.UndoCount == before);
        session.DuplicateSelection();
        check("duplicate preserves the name with an independent element ID", session.SelectedElements().Single().Name == "注释 — Example" && session.Selection.Single() != element.Id);
        var snapshot = session.CreateSelectionSnapshot();
        check("editable selection and document snapshots preserve the custom layer name", snapshot.Elements.Single().Name == "注释 — Example" && session.Document.Clone(true).Elements.All(e => e.Name == "注释 — Example"));
        session.SetSelection([element.Id]); session.SetLocked(true); session.RenameElement(element.Id, "Locked layer");
        check("locked layers can be named without unlocking content", session.Document.Elements.Single(e => e.Id == element.Id) is { Name: "Locked layer", Locked: true });
        session.RenameElement(element.Id, " \t ");
        check("empty labels restore the default instead of persisting whitespace", session.Document.Elements.Single(e => e.Id == element.Id).Name is null);
        check("unnamed elements omit the optional JSON field", !JsonSerializer.Serialize(session.Document.Elements.Single(e => e.Id == element.Id)).Contains("\"Name\"", StringComparison.Ordinal));
        before = session.UndoCount; bool rejected = false;
        try { session.RenameElement(element.Id, new string('x', EditorSession.MaximumLayerNameLength + 1)); } catch (ArgumentException) { rejected = true; }
        check("oversized name is rejected without changing content or history", rejected && session.Document.Elements.Single(e => e.Id == element.Id).Name is null && session.UndoCount == before);
        var estimate = EditorSession.EstimateBytes(session.Document);
        session.RenameElement(element.Id, "12345");
        check("history budget accounts for layer name storage", EditorSession.EstimateBytes(session.Document) == estimate + 10);
    }

    private static void Panel(Action<string, bool> check)
    {
        string locale = L.CurrentLanguage;
        bool allowed = true;
        var session = new EditorSession();
        var a = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 40 };
        var b = new CanvasElement { Kind = ElementKind.Ellipse, Width = 40, Height = 40 };
        session.Add(a); session.Add(b); session.SetSelection([a.Id]);
        var window = new LayersWindow(() => session, () => allowed);
        window.PrepareForShow();
        var list = (Controls.ListBox)Field(window, "layers");
        Controls.ListBoxItem Item(Guid id) => list.Items.Cast<Controls.ListBoxItem>().Single(i => (Guid)i.Tag == id);
        Controls.TextBlock Label(Guid id) => ((Controls.Grid)Item(id).Content).Children.OfType<Controls.TextBlock>().Single();
        Controls.TextBox Editor(Guid id) => ((Controls.Grid)Item(id).Content).Children.OfType<Controls.TextBox>().Single();
        void ClickName(Guid id)
        {
            window.PrepareForShow();
            Label(id).RaiseEvent(new Input.MouseButtonEventArgs(Input.Mouse.PrimaryDevice, Environment.TickCount, Input.MouseButton.Left)
            { RoutedEvent = Input.Mouse.PreviewMouseDownEvent });
        }
        ClickName(b.Id);
        check(locale + " first click on an unselected name does not enter rename", !window.IsRenaming);
        session.SetSelection([a.Id]); window.PrepareForShow(); ClickName(a.Id);
        check(locale + " clicking an already selected name opens its inline editor", window.IsRenaming && Editor(a.Id).Visibility == Wpf.Visibility.Visible && Label(a.Id).Visibility == Wpf.Visibility.Collapsed);
        int before = session.UndoCount;
        Invoke(window, "HandleRenameKey", Input.Key.Enter);
        check(locale + " accepting an untouched default name is a no-op", !window.IsRenaming && session.Document.Elements.Single(e => e.Id == a.Id).Name is null && session.UndoCount == before);
        ClickName(a.Id); Editor(a.Id).Text = "保留中文 draft"; window.PrepareForShow();
        check(locale + " periodic refresh preserves the uncommitted draft", window.IsRenaming && Editor(a.Id).Text == "保留中文 draft");
        int routed = 0; window.CommandKey += _ => routed++;
        bool route = (bool)Invoke(window, "RouteCommandKey", 0x2E, false)!;
        check(locale + " text editing never routes Delete to scene commands", !route && routed == 0 && session.Document.Elements.Count == 2);
        SetField(window, "renameComposing", true);
        bool handled = (bool)Invoke(window, "HandleRenameKey", Input.Key.Enter)!;
        check(locale + " IME composition Enter is not treated as rename submit", !handled && window.IsRenaming && session.UndoCount == before);
        SetField(window, "renameComposing", false); Invoke(window, "HandleRenameKey", Input.Key.Enter);
        check(locale + " Enter saves the inline name in one step and refreshes the display label", !window.IsRenaming && session.Document.Elements.Single(e => e.Id == a.Id).Name == "保留中文 draft" && session.UndoCount == before + 1 && Label(a.Id).Text == "保留中文 draft");
        check(locale + " inspector title uses the same custom label", InspectorWindow.ElementName(session.Document.Elements.Single(e => e.Id == a.Id)) == "保留中文 draft");
        ClickName(a.Id); Editor(a.Id).Text = "discard"; Invoke(window, "HandleRenameKey", Input.Key.Escape);
        check(locale + " Esc cancels the rename without dismissing the panel or changing history", !window.IsRenaming && session.Document.Elements.Single(e => e.Id == a.Id).Name == "保留中文 draft" && session.UndoCount == before + 1);
        ClickName(a.Id); Editor(a.Id).Text = "Lost focus";
        Editor(a.Id).RaiseEvent(new Input.KeyboardFocusChangedEventArgs(Input.Keyboard.PrimaryDevice, Environment.TickCount, Editor(a.Id), list) { RoutedEvent = Input.Keyboard.LostKeyboardFocusEvent });
        check(locale + " losing text focus commits the current layer name", !window.IsRenaming && session.Document.Elements.Single(e => e.Id == a.Id).Name == "Lost focus");
        ClickName(a.Id); Editor(a.Id).Text = "   "; Invoke(window, "CommitRename");
        check(locale + " clearing the inline name restores its localized kind label", session.Document.Elements.Single(e => e.Id == a.Id).Name is null && Label(a.Id).Text == L.Get("Editor.ElementKind.Rectangle"));
        allowed = false; ClickName(a.Id);
        check(locale + " mutation guard blocks starting a rename", !window.IsRenaming);
        allowed = true; ClickName(a.Id); Editor(a.Id).Text = "stale";
        session = new EditorSession(new() { Elements = [new() { Kind = ElementKind.Rectangle, Width = 40, Height = 40 }] }); window.PrepareForShow();
        check(locale + " changing documents discards a stale rename without editing the replacement", !window.IsRenaming && session.Document.Elements.Single().Name is null);
        check(locale + " rename handler tests create no native windows", new System.Windows.Interop.WindowInteropHelper(window).Handle == 0 && !window.IsVisible);
    }

    private static async Task Storage(Action<string, bool> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-layer-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var session = new EditorSession();
            session.Add(new() { Kind = ElementKind.Rectangle, Width = 50, Height = 40, Name = "图层一 / Layer one" });
            session.Add(new() { Kind = ElementKind.Text, Width = 100, Height = 30, Text = "Actual content" });
            string path = Path.Combine(directory, "named.floatcanvas");
            var store = new ProjectStore();
            await store.SaveAsync(path, session.Document, session.Assets);
            var loaded = await store.LoadAsync(path);
            check("project roundtrip preserves named and unnamed layers with editable content", loaded.Document.Elements[0].Name == "图层一 / Layer one" && loaded.Document.Elements[1].Name is null && loaded.Document.Elements[1].Text == "Actual content");
            var loadedSession = new EditorSession(loaded.Document, loaded.Assets);
            loadedSession.RenameElement(loadedSession.Document.Elements[0].Id, "After load"); loadedSession.Undo();
            check("loaded custom names can be renamed and undone", loadedSession.Document.Elements[0].Name == "图层一 / Layer one");
            var recovery = new RecoveryStore(Path.Combine(directory, "recovery"));
            await recovery.SaveAsync(new() { LiveDocument = session.Document.Clone() }, session.Assets);
            var restored = await recovery.LoadAsync();
            check("recovery preserves custom layer names", restored?.State.LiveDocument.Elements[0].Name == "图层一 / Layer one");
            session.Document.Elements[0].Name = null;
            await store.SaveAsync(path, session.Document, session.Assets);
            var legacy = await store.LoadAsync(path);
            check("legacy documents without optional names load with default labels", legacy.Document.Elements.All(e => e.Name is null));
            session.Document.Elements[0].Name = new string('x', EditorSession.MaximumLayerNameLength + 1);
            bool rejected = false;
            try { await store.SaveAsync(path, session.Document, session.Assets); } catch (InvalidDataException) { rejected = true; }
            var kept = await store.LoadAsync(path);
            check("invalid names cannot replace an existing valid project", rejected && kept.Document.Elements.All(e => e.Name is null));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args)
    {
        var result = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
        if (target is LayersWindow window) window.PrepareForShow();
        return result;
    }
}
