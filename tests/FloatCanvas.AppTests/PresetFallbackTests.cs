using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

/// <summary>Exercises automatic preset-link fallback separately from explicit recipe application.</summary>
internal static class PresetFallbackTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        nint foreground = GetForegroundWindow();
        string language = L.Preference;
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-preset-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            L.Initialize("zh-CN"); AppTheme.Apply("light");
            foreach (var tool in new[] { EditorTool.Line, EditorTool.Rectangle, EditorTool.Number })
            {
                DeleteLinkedPreset(folder, tool, hidden: false, check);
                DeleteLinkedPreset(folder, tool, hidden: true, check);
            }
            DeleteUnselectedPreset(folder, check);
            MissingLink(folder, check);
            AlreadyOfficialParameters(folder, check);
            check("preset fallback fixtures preserve the user's native foreground", GetForegroundWindow() == foreground);
        }
        finally { L.Initialize(language); AppTheme.Apply("light"); Directory.Delete(folder, true); }
    }

    private static void DeleteLinkedPreset(string folder, EditorTool tool, bool hidden, Action<string, bool> check)
    {
        string label = tool + (hidden ? " hidden" : " visible");
        using var fixture = new Fixture(folder, label, tool);
        var gallery = fixture.Gallery; var editor = fixture.Editor; var library = fixture.Library; var session = fixture.Session;
        string firstOfficial = library.OfficialFor(tool).First().Id;
        gallery.Apply(firstOfficial);
        editor.ApplyParameters(Edited(editor.Parameters, blue: true));
        gallery.SaveNamed("Blue saved recipe");
        var linked = library.User.Single();
        var other = library.Save("Unrelated recipe", library.OfficialFor(tool).First().Parameters);
        string otherBefore = Json(other);
        editor.ApplyParameters(Edited(editor.Parameters, blue: false));
        gallery.PrepareForShow();
        string working = Json(editor.Parameters), scene = Json(session.Document), official = Json(library.Official);
        var selection = session.Selection.ToArray();
        long sceneRevision = session.SceneRevision;
        int undoCount = session.UndoCount, applications = 0, stateChanges = 0;
        gallery.Applied += () => applications++;
        fixture.State.Changed += () => stateChanges++;
        if (hidden) fixture.SetVisible(false);
        check(label + " starts with an edited personal preset and a real presentation source", gallery.SelectedId == linked.Id &&
            PresentationSource.FromVisual(gallery) is HwndSource && gallery.IsVisible == !hidden);

        gallery.Delete(linked);
        check(label + " deletion preserves the complete current working parameters", Json(editor.Parameters) == working);
        check(label + " deletion changes only the linked selection to the first official recipe", gallery.SelectedId == firstOfficial && stateChanges > 0);
        check(label + " fallback never emits an explicit parameter application", applications == 0 && fixture.Failure is null);
        check(label + " deletes exactly one resource and keeps the other saved definition", library.User.Count == 1 && Json(library.User.Single()) == otherBefore &&
            !new ToolPresetLibrary(fixture.LibraryPath).User.Any(preset => preset.Id == linked.Id));
        check(label + " deletion creates only one resource history entry and leaves scene and selection intact", session.UndoCount == undoCount + 1 &&
            session.SceneRevision == sceneRevision && Json(session.Document) == scene && session.Selection.SequenceEqual(selection));
        if (hidden) fixture.SetVisible(true);
        else gallery.PrepareForShow();
        check(label + " fallback selects the official tile with its modified corner marker", fixture.SelectedTile().IsModified &&
            fixture.SelectedTile().Selected && Json(editor.Parameters) == working);

        check(label + " deletion can be undone as one resource operation", session.Undo());
        gallery.Refresh();
        check(label + " undo restores the saved recipe without reapplying any parameters", session.UndoCount == undoCount &&
            library.User.Any(preset => preset.Id == linked.Id) && Json(editor.Parameters) == working && gallery.SelectedId == firstOfficial && applications == 0);
        check(label + " deletion can be redone as one resource operation", session.Redo());
        gallery.Refresh();
        check(label + " redo preserves working state, modified marker, scene and selection", !library.User.Any(preset => preset.Id == linked.Id) &&
            Json(editor.Parameters) == working && gallery.SelectedId == firstOfficial && fixture.SelectedTile().IsModified && applications == 0 &&
            session.SceneRevision == sceneRevision && Json(session.Document) == scene && session.Selection.SequenceEqual(selection));

        fixture.State.Tools[tool] = editor.Parameters; fixture.State.Save();
        using (var restored = new Fixture(folder, label, tool))
        {
            check(label + " persisted working state and fallback link survive a fresh library/store/gallery", Json(restored.Editor.Parameters) == working &&
                restored.Gallery.SelectedId == firstOfficial && restored.SelectedTile().IsModified && restored.Library.User.Single().Id == other.Id);
        }

        gallery.Apply(firstOfficial);
        check(label + " explicitly selecting the fallback recipe still applies its complete official definition", Json(editor.Parameters) == Json(library.Apply(firstOfficial)) &&
            !fixture.SelectedTile().IsModified && applications == 1 && Json(library.Official) == official);
    }

    private static void DeleteUnselectedPreset(string folder, Action<string, bool> check)
    {
        using var fixture = new Fixture(folder, "unselected", EditorTool.Rectangle);
        fixture.Editor.ApplyParameters(Edited(fixture.Editor.Parameters, blue: true));
        fixture.Gallery.SaveNamed("Selected personal recipe");
        string selected = fixture.Gallery.SelectedId;
        var spare = fixture.Library.Save("Unselected personal recipe", new ToolParameters { Tool = EditorTool.Rectangle });
        fixture.Editor.ApplyParameters(Edited(fixture.Editor.Parameters, blue: false));
        fixture.Gallery.PrepareForShow();
        string working = Json(fixture.Editor.Parameters); int applications = 0;
        fixture.Gallery.Applied += () => applications++;
        fixture.Gallery.Delete(spare);
        check("deleting an unselected recipe preserves the active personal link, complete working state and modified marker", fixture.Gallery.SelectedId == selected &&
            Json(fixture.Editor.Parameters) == working && fixture.SelectedTile().IsModified && applications == 0 && fixture.Library.User.Single().Id == selected);
    }

    private static void MissingLink(string folder, Action<string, bool> check)
    {
        using var fixture = new Fixture(folder, "missing-link", EditorTool.Line);
        fixture.Editor.ApplyParameters(Edited(fixture.Editor.Parameters, blue: false));
        fixture.State.Selections[EditorTool.Line] = "user-no-longer-available";
        string working = Json(fixture.Editor.Parameters); int applications = 0;
        fixture.Gallery.Applied += () => applications++;
        fixture.Gallery.PrepareForShow();
        check("an unavailable linked recipe falls back during presentation without applying parameters", fixture.Gallery.SelectedId == fixture.Library.OfficialFor(EditorTool.Line).First().Id &&
            Json(fixture.Editor.Parameters) == working && fixture.SelectedTile().IsModified && applications == 0);
    }

    private static void AlreadyOfficialParameters(string folder, Action<string, bool> check)
    {
        using var fixture = new Fixture(folder, "already-official", EditorTool.Number);
        fixture.Editor.ApplyParameters(Edited(fixture.Editor.Parameters, blue: true)); fixture.Gallery.SaveNamed("Personal number recipe");
        var linked = fixture.Library.User.Single();
        var official = fixture.Library.OfficialFor(EditorTool.Number).First();
        fixture.Editor.ApplyParameters(official.Parameters);
        fixture.Gallery.PrepareForShow(); int applications = 0;
        fixture.Gallery.Applied += () => applications++;
        fixture.Gallery.Delete(linked);
        check("fallback has no modified marker when working parameters already equal the official definition", fixture.Gallery.SelectedId == official.Id &&
            Json(fixture.Editor.Parameters) == Json(official.Parameters) && !fixture.SelectedTile().IsModified && applications == 0);
    }

    private static ToolParameters Edited(ToolParameters current, bool blue)
    {
        var next = current.Clone();
        next.Style.Stroke = blue ? 0xFF2476D0 : 0xFF38A66B; next.Style.Fill = blue ? 0x68204090u : 0x75448822u;
        next.Style.StrokeWidth = blue ? 11 : 19; next.Style.Opacity = blue ? .71 : .43;
        next.Style.StrokePattern = blue ? StrokePattern.Dashed : StrokePattern.Dotted;
        next.Style.StartArrowHead = blue ? ArrowHeadStyle.Open : ArrowHeadStyle.Diamond;
        next.Style.EndArrowHead = blue ? ArrowHeadStyle.None : ArrowHeadStyle.Open;
        next.Style.ArrowHeadSize = blue ? 23 : 47; next.Style.LinkColors = !blue;
        next.Style.FontFamily = blue ? "Arial" : "Consolas"; next.Style.FontSize = blue ? 31 : 53;
        next.Style.TextAlignment = blue ? FloatCanvas.Core.TextAlignment.Center : FloatCanvas.Core.TextAlignment.Right;
        next.DefaultWidth = blue ? 213 : 347; next.DefaultHeight = blue ? 137 : 219;
        next.CornerRadius = blue ? 7 : 29; next.ConstrainAspect = !blue; next.EffectStrength = blue ? 17 : 37;
        next.NumberStart = blue ? 41 : 103; next.NumberStep = blue ? 3 : 7;
        return next;
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private sealed class Fixture : IDisposable
    {
        private readonly HwndSource source;
        internal readonly EditorSession Session;
        internal readonly EditorController Editor;
        internal readonly ToolPresetLibrary Library;
        internal readonly ToolStateStore State;
        internal readonly ToolPresetGallery Gallery;
        internal readonly string LibraryPath;
        internal string? Failure;

        internal Fixture(string folder, string name, EditorTool tool)
        {
            var sentinel = new CanvasElement { Kind = ElementKind.Rectangle, Width = 137, Height = 93, Style = new() { Stroke = 0xFFA85432 } };
            Session = new(new SceneDocument { Elements = [sentinel] }); Session.Selection.Add(sentinel.Id);
            LibraryPath = Path.Combine(folder, name + "-presets.json");
            Library = new(LibraryPath) { HistorySession = () => Session };
            State = new(Path.Combine(folder, name + "-working.json"));
            Editor = new(Session); Editor.ApplyParameters(State.Tools.GetValueOrDefault(tool) ?? Library.OfficialFor(tool).First().Parameters);
            Gallery = new(Editor, Library, State); Gallery.Failed += message => Failure = message;
            var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
            source = new HwndSource(new HwndSourceParameters("FloatCanvas.PresetFallback." + name)
            {
                Width = 280, Height = 540, PositionX = desktop.Left - 10000, PositionY = desktop.Top - 10000,
                WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080
            }) { RootVisual = new Border { Opacity = 0, Child = Gallery } };
            Gallery.PrepareForShow(); Pump();
        }

        internal SelectionTileButton SelectedTile() => Gallery.OfficialTiles.Children.OfType<SelectionTileButton>()
            .Concat(Gallery.UserTiles.Children.OfType<SelectionTileButton>()).Single(tile => Equals(tile.Tag, Gallery.SelectedId));

        internal void SetVisible(bool visible)
        {
            Gallery.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) Gallery.PrepareForShow();
            Pump();
        }

        public void Dispose() { source.RootVisual = null; source.Dispose(); }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
