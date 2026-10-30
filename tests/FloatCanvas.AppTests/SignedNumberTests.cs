using System.IO;
using System.Reflection;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;

internal static class SignedNumberTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-signed-number-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var (start, step) in new[] { (0, 0), (5, -2), (-3, 3), (-2, -1), (int.MaxValue - 1, 999999), (int.MinValue + 1, -999999) })
                Drawing(start, step, check);
            CoreAdd(check);
            Persistence(folder, check);
            SessionRestoration(folder, check);
            Controls(folder, check);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Drawing(int start, int step, Action<string, bool> check)
    {
        var session = new EditorSession(new SceneDocument { NextNumber = start });
        var editor = new EditorController(session) { Tool = EditorTool.Number, NumberStep = step };
        var expected = new List<int>(); int next = start;
        bool created = true;
        try
        {
            for (int i = 0; i < 3; i++)
            {
                expected.Add(next); next = (int)Math.Clamp((long)next + step, int.MinValue, int.MaxValue);
                Send(editor, OverlayPointerKind.Down, 100 + i * 180);
                Send(editor, OverlayPointerKind.Up, 100 + i * 180);
            }
        }
        catch (ArgumentOutOfRangeException) { created = false; }
        check($"real number tool places start={start}, step={step} without rejecting signed or zero values", created &&
            session.Document.Elements.Select(element => element.Number).SequenceEqual(expected) && session.Document.NextNumber == next);
        check($"start={start}, step={step} consumes one history item for each completed marker", session.UndoCount == 3 && !session.IsTransactionActive);
        int beforeLast = expected[^1];
        check($"start={start}, step={step} undo restores the preceding counter exactly", session.Undo() && session.Document.Elements.Count == 2 &&
            session.Document.NextNumber == beforeLast);
        check($"start={start}, step={step} redo restores the sequence and counter together", session.Redo() && session.Document.NextNumber == next &&
            session.Document.Elements.Select(element => element.Number).SequenceEqual(expected));
        Send(editor, OverlayPointerKind.Down, 700); editor.Cancel();
        check($"start={start}, step={step} cancellation does not consume a number", session.Document.Elements.Count == 3 && session.Document.NextNumber == next && session.UndoCount == 3);
    }

    private static void CoreAdd(Action<string, bool> check)
    {
        foreach (int step in new[] { -2, 0, 2 })
        {
            var session = new EditorSession(new SceneDocument { NextNumber = -3 });
            session.Add(new CanvasElement { Kind = ElementKind.Number, Number = -3 }, numberStep: step);
            check("core number insertion accepts signed step " + step, session.Document.NextNumber == -3 + step);
        }
        var existing = new EditorSession(new SceneDocument { NextNumber = 99 });
        existing.Add(new CanvasElement { Kind = ElementKind.Number, Number = 2 });
        check("default positive insertion retains the established counter for older marker values", existing.Document.NextNumber == 99);
    }

    private static void Persistence(string folder, Action<string, bool> check)
    {
        var store = new ProjectStore();
        foreach (int value in new[] { int.MinValue, -23, 0, int.MaxValue })
        {
            var document = new SceneDocument { NextNumber = value, Elements = [new CanvasElement { Kind = ElementKind.Number, Number = value }] };
            string file = Path.Combine(folder, "number-" + value + ".floatcanvas");
            store.SaveAsync(file, document, new AssetLibrary()).GetAwaiter().GetResult();
            var restored = store.LoadAsync(file).GetAwaiter().GetResult();
            check("project package round-trips signed marker and counter " + value, restored.Document.NextNumber == value && restored.Document.Elements.Single().Number == value);
        }
        var parameters = new ToolParameters { Tool = EditorTool.Number, NumberStart = -5, NumberStep = -3 };
        check("signed tool definitions preserve existing non-number validity limits", parameters.IsValid() &&
            !new ToolParameters { NumberStart = -1000000 }.IsValid() && !new ToolParameters { NumberStep = 1000000 }.IsValid() &&
            !new ToolParameters { Style = new() { StrokeWidth = -1 } }.IsValid());
        string stateFile = Path.Combine(folder, "tool-state.json");
        var state = new ToolStateStore(stateFile); state.Tools[EditorTool.Number] = parameters; state.Save();
        var restoredState = new ToolStateStore(stateFile);
        check("working-state cache restores negative numbering fields", restoredState.Tools[EditorTool.Number].NumberStart == -5 && restoredState.Tools[EditorTool.Number].NumberStep == -3);
        string libraryFile = Path.Combine(folder, "tool-presets.json");
        var library = new ToolPresetLibrary(libraryFile); var saved = library.Save("descending", parameters);
        check("personal preset saves and restores descending numbering", new ToolPresetLibrary(libraryFile).Apply(saved.Id).NumberStep == -3);
        parameters.NumberStep = 0; library.Update(saved.Id, parameters);
        check("personal preset saves and restores a stationary sequence", new ToolPresetLibrary(libraryFile).Apply(saved.Id).NumberStep == 0);
        string settingsFile = Path.Combine(folder, "settings.json");
        File.WriteAllText(settingsFile, "{\"NumberStart\":-8,\"NumberStep\":0}");
        var settings = new CompatibleConfiguration(settingsFile).Read<UserSettings>(new());
        check("compatible user settings retain zero step and signed start", settings.NumberStart == -8 && settings.NumberStep == 0);
    }

    private static void Controls(string folder, Action<string, bool> check)
    {
        var editor = new EditorController(new EditorSession()) { Tool = EditorTool.Number };
        var panel = new ToolOptionsPanel(editor, new ToolPresetLibrary(Path.Combine(folder, "ui-presets.json")), new ColorLibrary(Path.Combine(folder, "ui-colors.json")));
        panel.PrepareForShow();
        var fields = (Dictionary<string, NumericValueField>)typeof(ToolOptionsPanel).GetField("numericFields", Private)!.GetValue(panel)!;
        check("number-step rail places zero at its middle", fields["NumberStep"].Slider.Minimum == -10 && fields["NumberStep"].Slider.Maximum == 10);
        check("start and current-counter rails keep their familiar positive range", fields["NumberStart"].Slider.Minimum == 1 && fields["NextNumber"].Slider.Minimum == 1);
        fields["NumberStep"].Input.Text = "0";
        check("tool UI accepts a zero step", editor.NumberStep == 0);
        fields["NumberStep"].Input.Text = "-3";
        fields["NumberStart"].Input.Text = "-5";
        fields["NextNumber"].Input.Text = "-9";
        check("tool UI applies negative start step and current counter", editor.NumberStep == -3 && editor.NumberStart == -5 && editor.Session.Document.NextNumber == -9);
        fields["NumberStep"].Input.Text = "-2.5";
        check("number controls still reject fractional input", editor.NumberStep == -3);
        var scrubber = fields["NextNumber"].AnchorScrubber!;
        check("signed counter icon starts its ordinary numeric gesture", scrubber.TryBegin(0));
        scrubber.Update(-30); scrubber.End(true);
        check("counter icon scrubs into negative numbers without a positive rail clamp", editor.Session.Document.NextNumber < -9);
        check("numeric controls remain detached and do not create desktop windows", !panel.IsVisible && PresentationSource.FromVisual(panel) is null);
    }

    private static void SessionRestoration(string folder, Action<string, bool> check)
    {
        string originalProfile = ApplicationPaths.ProfileDirectory;
        var originalScope = ApplicationPaths.RequestedMonitorScope;
        string language = FloatCanvas.Localization.AppText.Preference;
        try
        {
            foreach (bool cached in new[] { false, true })
            {
                string profile = Path.Combine(folder, cached ? "cached-session" : "settings-session"); Directory.CreateDirectory(profile);
                ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
                new UserSettings { NumberStart = -8, NumberStep = 0, Theme = "light" }.Save();
                if (cached)
                {
                    var state = new ToolStateStore();
                    state.Tools[EditorTool.Number] = new() { Tool = EditorTool.Number, NumberStart = -5, NumberStep = -3 };
                    state.Save();
                }
                var monitor = new MonitorInfo("signed-number-fixture", new(0, 0, 640, 160), new(0, 0, 640, 160), 96, 96, true);
                Func<IReadOnlyList<MonitorInfo>> provider = () => [monitor];
                var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
                DesktopSession? session = null;
                try
                {
                    session = new DesktopSession(Application.Current, host);
                    ((System.Windows.Forms.NotifyIcon)typeof(DesktopSession).GetField("_tray", Private)!.GetValue(session)!).Visible = false;
                    var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", Private)!;
                    (isolation.GetValue(host) as IDisposable)?.Dispose(); isolation.SetValue(host, null);
                    var editor = (EditorController)typeof(DesktopSession).GetField("_editor", Private)!.GetValue(session)!;
                    int expectedStart = cached ? -5 : -8, expectedStep = cached ? -3 : 0;
                    check("real session startup preserves " + (cached ? "cached descending" : "stationary configured") + " numbering", editor.NumberStart == expectedStart && editor.NumberStep == expectedStep);
                    typeof(DesktopSession).GetMethod("SwitchToolState", Private)!.Invoke(session, [EditorTool.Number, false]);
                    check("first number activation restores its signed working state", editor.NumberStart == expectedStart && editor.NumberStep == expectedStep);
                    check("signed-number reconstruction keeps all overlay carriers hidden and input released", !host.IsEditing && !host.IsKeyboardIsolationActive &&
                        host.Diagnostics.All(pair => !pair.DisplayVisible && !pair.InputVisible));
                }
                finally { if (session is not null) session.Dispose(); else host.Dispose(); }
            }
        }
        finally
        {
            var args = new List<string> { "--profile", originalProfile };
            if (originalScope is { } scope) args.Add(scope == OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens");
            ApplicationPaths.Configure(args.ToArray()); FloatCanvas.Localization.AppText.Initialize(language); AppTheme.Apply("light");
        }
    }

    private static void Send(EditorController editor, OverlayPointerKind kind, double x) =>
        editor.Pointer(new(kind, new(x, 100), OverlayMouseButton.Left, OverlayModifiers.None));
}
