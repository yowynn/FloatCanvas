using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class NumberStartLinkTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-number-start-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var library = new ToolPresetLibrary(Path.Combine(folder, "presets.json"));
            Presets(library, check);
            Controls(folder, library, check);
            ToolRestoration(folder, library, check);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static EditorController Editor(int start, int current, bool withLayer = false)
    {
        var document = new SceneDocument { NextNumber = current, Viewport = new(0, 0, 800, 600) };
        if (withLayer) document.Elements.Add(new() { Kind = ElementKind.Number, Number = 71, Width = 72, Height = 72 });
        var editor = new EditorController(new(document)) { Tool = EditorTool.Number, NumberStart = start };
        editor.Session.Document.NextNumber = current;
        return editor;
    }

    private static void Presets(ToolPresetLibrary library, Action<string, bool> check)
    {
        foreach (string id in library.OfficialFor(EditorTool.Number).Select(p => p.Id))
        {
            var editor = Editor(100, 100);
            new ToolPresetGallery(editor, library).Apply(id);
            check(id + " follows the old start before the first insertion", editor.NumberStart == 1 && editor.Session.Document.NextNumber == 1);
        }
        var personal = library.Save("Custom start", new() { Tool = EditorTool.Number, NumberStart = -5, NumberStep = -3 });
        foreach (var (current, withLayer) in new[] { (100, false), (100, true), (101, false), (101, true), (0, false) })
        {
            var editor = Editor(100, current, withLayer);
            string layers = JsonSerializer.Serialize(editor.Session.Document.Elements);
            new ToolPresetGallery(editor, library).Apply(personal.Id);
            check($"personal start uses equality for current={current} withLayer={withLayer}", editor.NumberStart == -5 &&
                editor.NumberStep == -3 && editor.Session.Document.NextNumber == (current == 100 ? -5 : current));
            check("a preset never rewrites existing numbered layers", JsonSerializer.Serialize(editor.Session.Document.Elements) == layers);
        }
        var stationary = Editor(100, 100); stationary.NumberStep = 0;
        stationary.Pointer(new(FloatCanvas.Windows.OverlayPointerKind.Down, new(200, 200), FloatCanvas.Windows.OverlayMouseButton.Left, FloatCanvas.Windows.OverlayModifiers.None));
        stationary.Pointer(new(FloatCanvas.Windows.OverlayPointerKind.Up, new(200, 200), FloatCanvas.Windows.OverlayMouseButton.Left, FloatCanvas.Windows.OverlayModifiers.None));
        new ToolPresetGallery(stationary, library).Apply("official.number");
        check("an inserted zero-step sequence still follows when current equals the old start", stationary.Session.Document.Elements.Single().Number == 100 &&
            stationary.Session.Document.NextNumber == 1 && stationary.NumberStep == 1);
        var boundary = Editor(999999, int.MaxValue);
        new ToolPresetGallery(boundary, library).Apply(personal.Id);
        check("an advanced full-range counter survives a preset with a different start", boundary.Session.Document.NextNumber == int.MaxValue);
        var notification = Editor(100, 100, withLayer: true);
        long scene = notification.Session.SceneRevision, elements = notification.Session.PreviewElementsRevision;
        int events = 0; notification.Session.Changed += (_, _) => events++;
        new ToolPresetGallery(notification, library).Apply(personal.Id);
        check("linked counter changes notify once as a setting without invalidating existing element pixels", events == 1 &&
            notification.Session.SceneRevision == scene + 1 && notification.Session.PreviewElementsRevision == elements && notification.Session.UndoCount == 0);
    }

    private static void Controls(string folder, ToolPresetLibrary library, Action<string, bool> check)
    {
        var editor = Editor(100, 100, withLayer: true);
        var panel = new ToolOptionsPanel(editor, library, new ColorLibrary(Path.Combine(folder, "colors.json")));
        panel.PrepareForShow();
        var fields = (Dictionary<string, NumericValueField>)typeof(ToolOptionsPanel).GetField("numericFields", Private)!.GetValue(panel)!;
        fields["NumberStart"].Input.Text = "5";
        check("typing a new start follows an untouched current counter", editor.NumberStart == 5 && editor.Session.Document.NextNumber == 5);
        fields["NumberStart"].Slider.Value = 20;
        check("moving the start rail follows an equal current counter", editor.NumberStart == 20 && editor.Session.Document.NextNumber == 20);
        editor.Session.Document.NextNumber = 99;
        fields["NumberStart"].Input.Text = "30";
        fields["NumberStep"].Input.Text = "-2";
        check("typing a start after advancement preserves current and applies the independent step", editor.NumberStart == 30 && editor.NumberStep == -2 && editor.Session.Document.NextNumber == 99);
        fields["NumberStart"].Input.Text = "2.5";
        check("an invalid fractional start changes neither start nor counter", editor.NumberStart == 30 && editor.Session.Document.NextNumber == 99);
        foreach (bool advanced in new[] { false, true })
        {
            editor.NumberStart = 100; editor.Session.Document.NextNumber = advanced ? 110 : 100; panel.PrepareForShow();
            var scrubber = fields["NumberStart"].AnchorScrubber!;
            check("start scrub begins without a document transaction", scrubber.TryBegin(0));
            scrubber.Update(40);
            check("live start scrub respects its initial equality boundary", editor.NumberStart > 100 &&
                editor.Session.Document.NextNumber == (advanced ? 110 : editor.NumberStart));
            scrubber.End(false);
            check("cancelling a start scrub restores start and its linked counter", editor.NumberStart == 100 &&
                editor.Session.Document.NextNumber == (advanced ? 110 : 100));
        }
        editor.NumberStart = 100; editor.Session.Document.NextNumber = 110; panel.PrepareForShow();
        var crossing = fields["NumberStart"].AnchorScrubber!;
        check("an advanced start scrub can begin before crossing the current counter", crossing.TryBegin(0));
        crossing.Update(60);
        check("an advanced scrub can reach the existing counter without moving it", editor.NumberStart == 110 && editor.Session.Document.NextNumber == 110);
        crossing.Update(80);
        check("an advanced scrub keeps its initial independence after crossing the counter", editor.NumberStart > 110 && editor.Session.Document.NextNumber == 110);
        crossing.End(false);
        check("cancelling a crossing scrub restores the original advanced counter", editor.NumberStart == 100 && editor.Session.Document.NextNumber == 110);
        foreach (bool commit in new[] { false, true })
        {
            editor.NumberStart = 10; editor.Session.Document.NextNumber = 20; panel.PrepareForShow();
            var start = fields["NumberStart"];
            check("a start rail gesture begins with its original equality boundary", start.BeginGesture());
            start.Slider.Value = 20; start.Slider.Value = 30;
            check("a start rail cannot start following after passing an advanced current counter", editor.NumberStart == 30 && editor.Session.Document.NextNumber == 20);
            start.EndGesture(commit);
            check("committing or cancelling a crossing rail retains the advanced counter", editor.NumberStart == (commit ? 30 : 10) && editor.Session.Document.NextNumber == 20);
        }
        editor.NumberStart = 20; panel.PrepareForShow(); fields["NumberStart"].Input.Text = "40";
        check("a later typed start recomputes equality after the numeric gesture has ended", editor.NumberStart == 40 && editor.Session.Document.NextNumber == 40);
        check("editing sequence rules leaves existing marker values and document history alone", editor.Session.Document.Elements.Single().Number == 71 && editor.Session.UndoCount == 0);
        editor.NumberStart = 100; editor.Session.Document.NextNumber = 100; panel.PrepareForShow();
        var source = editor.Session;
        var changingContext = fields["NumberStart"].AnchorScrubber!;
        check("a start scrub begins before a document replacement", changingContext.TryBegin(0));
        changingContext.Update(40); int lastSourceCounter = source.Document.NextNumber;
        var replacement = new EditorSession(new() { NextNumber = 777 });
        editor.ReplaceSession(replacement, new(0, 0));
        changingContext.End(false);
        check("cancelling a stale start scrub never rewrites the replacement document counter", replacement.Document.NextNumber == 777 &&
            source.Document.NextNumber == lastSourceCounter && replacement.UndoCount == 0);
        editor.NumberStart = 100; editor.Session.Document.NextNumber = 100; panel.PrepareForShow();
        check("a start scrub begins before a tool context change", changingContext.TryBegin(0));
        changingContext.Update(40); int beforeToolChange = editor.Session.Document.NextNumber;
        editor.Tool = EditorTool.Freehand; changingContext.End(false);
        check("cancelling a stale start scrub never changes another tool's counter or parameters", editor.Tool == EditorTool.Freehand &&
            editor.NumberStart == beforeToolChange && editor.Session.Document.NextNumber == beforeToolChange);
        editor.Tool = EditorTool.Number; editor.NumberStart = 10; editor.Session.Document.NextNumber = 10; panel.PrepareForShow();
        var originalDocument = editor.Session.Document;
        var replacingDocument = fields["NumberStart"];
        check("a start rail begins before replacing a document in the same session", replacingDocument.BeginGesture());
        replacingDocument.Slider.Value = 20;
        var replacementDocument = new SceneDocument { NextNumber = 777 };
        editor.Session.ReplaceDocument(replacementDocument);
        replacingDocument.Slider.Value = 30;
        check("a stale start rail cannot update a replaced document in the same session", ReferenceEquals(editor.Session.Document, replacementDocument) &&
            replacementDocument.NextNumber == 777 && originalDocument.NextNumber == 20 && editor.NumberStart == 20);
        replacingDocument.EndGesture(false);
        check("cancelling a start rail after same-session document replacement preserves both documents", replacementDocument.NextNumber == 777 &&
            originalDocument.NextNumber == 20 && editor.NumberStart == 20 && !replacingDocument.IsInteracting);
    }

    private static void ToolRestoration(string folder, ToolPresetLibrary library, Action<string, bool> check)
    {
        foreach (int current in new[] { 1, 100, 103 })
        {
            var editor = Editor(100, current);
            var state = new ToolStateStore(Path.Combine(folder, "state-" + current + ".json"));
            state.Tools[EditorTool.Number] = editor.Parameters; state.Save();
            editor.ApplyParameters(library.Apply("official.freehand"));
            check("an unrelated tool snapshot cannot reset the canvas counter", editor.NumberStart == 1 && editor.Session.Document.NextNumber == current);
            editor.ApplyParameters(new ToolStateStore(Path.Combine(folder, "state-" + current + ".json")).Tools[EditorTool.Number]);
            check("restoring number state compares against the actual previous number start, not hidden pen defaults", editor.NumberStart == 100 && editor.Session.Document.NextNumber == current);
        }
        var untouched = Editor(100, 100);
        untouched.ApplyParameters(library.Apply("official.freehand"));
        untouched.ApplyParameters(library.Apply("official.number"));
        check("a number preset reached from another tool still follows its own previous sequence start", untouched.NumberStart == 1 && untouched.Session.Document.NextNumber == 1);
        foreach (int next in new[] { 1, 100 })
        {
            var editor = Editor(100, 100);
            var previous = editor.Session;
            editor.ApplyParameters(library.Apply("official.freehand"));
            var replacement = new EditorSession(new() { NextNumber = next });
            editor.ReplaceSession(replacement, new(0, 0));
            editor.ApplyParameters(new() { Tool = EditorTool.Number, NumberStart = 25 });
            check("switching documents applies equality to the new document only", replacement.Document.NextNumber == (next == 100 ? 25 : next) &&
                previous.Document.NextNumber == 100 && previous.Document.Elements.Count == 0);
        }
    }
}
