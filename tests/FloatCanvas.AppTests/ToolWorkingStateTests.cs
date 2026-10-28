using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Real session reconstruction with isolated data and permanently hidden overlay HWNDs.</summary>
internal static class ToolWorkingStateTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        string originalProfile = ApplicationPaths.ProfileDirectory;
        var originalScope = ApplicationPaths.RequestedMonitorScope;
        string originalLanguage = L.Preference;
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-tool-working-state", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        new UserSettings { Theme = "light", Language = "zh-CN" }.Save();
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var initialWindows = app.Windows.Cast<Window>().ToArray();
        DesktopSession? session = null;
        OverlayHost? host = null;
        string? userPresetId = null;
        ToolParameters? expectedPen = null;
        try
        {
            NewSession();
            var editor = Field<EditorController>(session!, "_editor");
            var panel = Field<ToolOptionsPanel>(session!, "_toolOptionsPanel");
            var gallery = Field<ToolPresetGallery>(panel, "presets");
            var state = Field<ToolStateStore>(session!, "_toolStateStore");
            Change(p => { p.Style.Stroke = 0xFF448361; p.Style.Fill = 0; p.Style.StrokePattern = StrokePattern.Dotted; p.Style.StrokeWidth = 9; });
            check("working state changes use the real options entry without changing its selected official recipe", gallery.SelectedId == "official.freehand" && editor.Style.Stroke == 0xFF448361);
            string penBaseline = JsonSerializer.Serialize(Official(session!, "official.freehand"));
            check("working pen parameters remain separate from its fixed official definition", Official(session!, "official.freehand").Style.Stroke == ColorLibrary.ForegroundRed &&
                Official(session!, "official.freehand").Style.StrokePattern == StrokePattern.Solid);

            Activate(EditorTool.Line); gallery.Apply("official.dashed-line");
            string lineBaseline = JsonSerializer.Serialize(Official(session!, "official.dashed-line"));
            string lineBaselineStyle = JsonSerializer.Serialize(Official(session!, "official.dashed-line").Style);
            Change(p => { p.Style.Stroke = 0xFF9065B0; p.Style.StrokePattern = StrokePattern.Dotted; p.Style.StartArrowHead = ArrowHeadStyle.Open; p.Style.EndArrowHead = ArrowHeadStyle.Diamond; p.Style.StrokeWidth = 11; });
            check("working line changes retain its selected dashed recipe", gallery.SelectedId == "official.dashed-line" && editor.Style.StrokePattern == StrokePattern.Dotted);
            check("working line changes cannot mutate the fixed official definition", JsonSerializer.Serialize(Official(session!, "official.dashed-line")) == lineBaseline);

            Activate(EditorTool.Rectangle);
            Change(p => { p.Style.Stroke = 0xFFD9730D; p.Style.Fill = 0x338899AA; p.CornerRadius = 23; p.DefaultWidth = 210; p.DefaultHeight = 120; p.ConstrainAspect = true; });
            gallery.SaveNamed("Working-state rectangle"); userPresetId = gallery.SelectedId;
            var recipe = Field<ToolPresetLibrary>(session!, "_toolPresets").User.Single(p => p.Id == userPresetId).Parameters;
            string recipeSnapshot = JsonSerializer.Serialize(recipe);
            string recipesOnDisk = File.ReadAllText(Path.Combine(profile, "tool-presets.json"));
            Change(p => { p.Style.Stroke = 0xFFC14C8A; p.Style.Fill = 0; p.CornerRadius = 31; p.Style.StrokePattern = StrokePattern.Dashed; p.DefaultWidth = 257; p.DefaultHeight = 143; });
            check("saving a user recipe selects it before subsequent temporary edits", userPresetId.StartsWith("user-", StringComparison.Ordinal) && gallery.SelectedId == userPresetId);
            check("temporary rectangle edits leave the stored user definition unchanged", File.ReadAllText(Path.Combine(profile, "tool-presets.json")) == recipesOnDisk);

            Activate(EditorTool.Number);
            long beforeNumberStart = editor.Session.ContentVersion;
            Change(p => { p.NumberStart = 103; p.NumberStep = 7; p.Style.FontSize = 32; p.DefaultWidth = 177; p.DefaultHeight = 99; p.Style.Stroke = 0xFF9F6B53; });
            check("a linked number start updates document state without revealing a hidden fixture canvas", editor.Session.Document.NextNumber == 103 &&
                editor.Session.ContentVersion > beforeNumberStart && !editor.IsUpdatingNumberStart && !host!.IsVisible && !host.IsPreviewVisible);
            check("real number options update the independent tool snapshot", state.Tools[EditorTool.Number] is { NumberStart: 103, NumberStep: 7 } && state.Tools[EditorTool.Number].Style.FontSize == 32);
            var rememberedLine = state.Tools[EditorTool.Line].Clone();
            Activate(EditorTool.Line); gallery.Apply("official.dashed-line");
            var settings = Field<UserSettings>(session!, "_settings");
            check("applying a complete line preset resets its number fields without changing number preferences", editor.NumberStart == 1 && editor.NumberStep == 1 &&
                settings.NumberStart == 103 && settings.NumberStep == 7);
            editor.ApplyParameters(rememberedLine); Invoke("RefreshToolbarState");
            Activate(EditorTool.Number);
            check("returning from an explicitly applied line preset restores the number tool's own start and step", editor.NumberStart == 103 && editor.NumberStep == 7 &&
                settings.NumberStart == 103 && settings.NumberStep == 7);
            Activate(EditorTool.Pixelate); Change(p => p.EffectStrength = 41);
            Activate(EditorTool.Blur); Change(p => p.EffectStrength = 27);
            Activate(EditorTool.Pixelate);
            check("shared effect toolbar button retains the most recent concrete tool", state.Buttons[EditorTool.Blur] == EditorTool.Pixelate);
            check("blur and pixelate use separate working snapshots", state.Tools[EditorTool.Blur].EffectStrength == 27 && editor.EffectStrength == 41);
            Activate(EditorTool.Freehand); expectedPen = editor.Parameters;
            check("switching back before restart keeps the edited pen style", editor.Style.Stroke == 0xFF448361 && editor.Style.StrokePattern == StrokePattern.Dotted && editor.Style.StrokeWidth == 9);
            var capture = new EditorSession(new SceneDocument { Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, 640, 160) });
            SetField(session!, "_capture", capture); SetField(session!, "_documentLiveTool", EditorTool.Freehand);
            editor.ReplaceSession(capture, new(0, 0));
            Invoke("SwitchToolState", EditorTool.Select, false);
            Invoke("SwitchToolState", EditorTool.Text, false);
            Change(p => { p.Style.Stroke = 0xFF00AA11; p.Style.FontSize = 72; p.Style.StrokePattern = StrokePattern.Dashed; });
            Invoke("ReturnToLive"); SetField(session!, "_capture", null);
            check("internal capture text and selection transitions restore the live pen's own style", editor.Tool == EditorTool.Freehand &&
                JsonSerializer.Serialize(editor.Style) == JsonSerializer.Serialize(expectedPen.Style) && state.Tools[EditorTool.Text].Style.FontSize == 72);
            check("tool writes are coalesced rather than synchronous on every options change", !File.Exists(Path.Combine(profile, "tool-state.json")));

            var foreground = Field<ColorField>(panel, "foreground");
            var picker = Field<ColorPickerPanel>(foreground, "picker");
            var hex = Field<TextBox>(picker, "hex"); hex.Text = "#12";
            var saveTimer = Field<DispatcherTimer>(session!, "_toolStateSaveTimer");
            check("working state persistence uses a 500 ms dispatcher debounce", saveTimer.IsEnabled && saveTimer.Interval == TimeSpan.FromMilliseconds(500));
            PumpFor(TimeSpan.FromMilliseconds(750));
            check("the debounced save writes the current tool cache without shutdown", File.Exists(Path.Combine(profile, "tool-state.json")) &&
                new ToolStateStore(Path.Combine(profile, "tool-state.json")).Tools[EditorTool.Freehand].Style.StrokePattern == StrokePattern.Dotted);
            check("background saving preserves an incomplete detached color draft", hex.Text == "#12" && editor.Style.Stroke == expectedPen.Style.Stroke);
            Invoke("FlushToolState", false);
            check("ordinary tool-state flush preserves color input rather than submitting it", hex.Text == "#12" && editor.Style.Stroke == expectedPen.Style.Stroke);
            check("the first fixture session never shows or captures desktop input", !host!.IsEditing && host.Diagnostics.All(pair => !pair.DisplayVisible && !pair.InputVisible) &&
                app.Windows.Cast<Window>().Except(initialWindows).All(window => !window.IsVisible));
            session!.Dispose(); session = null; host = null; PumpFor(TimeSpan.FromMilliseconds(10));

            NewSession();
            editor = Field<EditorController>(session!, "_editor"); panel = Field<ToolOptionsPanel>(session!, "_toolOptionsPanel");
            gallery = Field<ToolPresetGallery>(panel, "presets"); state = Field<ToolStateStore>(session!, "_toolStateStore");
            check("new real session restores the working pen color line pattern and width", editor.Tool == EditorTool.Freehand && editor.Style.Stroke == 0xFF448361 &&
                editor.Style.Fill == 0 && editor.Style.StrokePattern == StrokePattern.Dotted && editor.Style.StrokeWidth == 9);
            check("internal capture transitions persist text state without contaminating the reloaded pen", state.Tools[EditorTool.Text].Style is
                { Stroke: 0xFF00AA11, FontSize: 72, StrokePattern: StrokePattern.Dashed } && editor.Style.FontSize == expectedPen.Style.FontSize);
            check("new session restores the selected recipe IDs for every previously configured tool", gallery.SelectedId == "official.freehand" &&
                state.Selections[EditorTool.Line] == "official.dashed-line" && state.Selections[EditorTool.Rectangle] == userPresetId && state.Selections[EditorTool.Number] == "official.number");
            check("reconstructing the session retains the fixed official pen definition", JsonSerializer.Serialize(Official(session!, "official.freehand")) == penBaseline);
            check("working state no longer creates personalized official baseline or preview caches", state.OfficialBaselines.Count == 0 && state.PreviewSources.Count == 0);
            check("reconstructing the session retains the shared effect button choice", state.Buttons[EditorTool.Blur] == EditorTool.Pixelate);
            check("number tool defaults survive instead of reverting to startup defaults", editor.NumberStart == 103 && editor.NumberStep == 7);

            Activate(EditorTool.Line);
            check("restored line cache includes custom color pattern and both arrow ends", gallery.SelectedId == "official.dashed-line" && editor.Style.Stroke == 0xFF9065B0 &&
                editor.Style.StrokePattern == StrokePattern.Dotted && editor.Style.StartArrowHead == ArrowHeadStyle.Open && editor.Style.EndArrowHead == ArrowHeadStyle.Diamond && editor.Style.StrokeWidth == 11);
            check("restored line official definition remains independent from the current parameters", JsonSerializer.Serialize(Official(session!, "official.dashed-line")) == lineBaseline);
            gallery.Apply("official.dashed-line");
            check("explicitly reselecting the same official recipe restores its original baseline after restart", JsonSerializer.Serialize(editor.Style) == lineBaselineStyle && gallery.SelectedId == "official.dashed-line");
            Activate(EditorTool.Arrow);
            check("explicit toolbar alias selection updates the shared selected recipe ID", gallery.SelectedId == "official.arrow" &&
                state.Selections[EditorTool.Line] == "official.arrow" && editor.Style.EndArrowHead == ArrowHeadStyle.FilledTriangle);
            Change(p => { p.Style.Stroke = 0xFF112233; p.Style.StrokePattern = StrokePattern.Dotted; p.Style.EndArrowHead = ArrowHeadStyle.Diamond; });
            Activate(EditorTool.Freehand); Activate(EditorTool.Line);
            check("ordinary line revisits preserve edited alias parameters rather than reapplying its recipe", gallery.SelectedId == "official.arrow" &&
                editor.Style.Stroke == 0xFF112233 && editor.Style.StrokePattern == StrokePattern.Dotted && editor.Style.EndArrowHead == ArrowHeadStyle.Diamond);
            Activate(EditorTool.Rectangle);
            check("restored user-based rectangle keeps its temporary attributes and selected recipe", gallery.SelectedId == userPresetId && editor.Style.Stroke == 0xFFC14C8A &&
                editor.Style.Fill == 0 && editor.Style.StrokePattern == StrokePattern.Dashed && editor.CornerRadius == 31 && editor.DefaultWidth == 257 && editor.DefaultHeight == 143 && editor.ConstrainAspect);
            gallery.Apply(userPresetId!);
            check("explicitly reselecting a user recipe restores its saved definition after restart", JsonSerializer.Serialize(editor.Style) == JsonSerializer.Serialize(recipe.Style) &&
                editor.CornerRadius == recipe.CornerRadius && editor.DefaultWidth == recipe.DefaultWidth && editor.DefaultHeight == recipe.DefaultHeight);
            check("working cache recovery never rewrites the user's recipe file", recipesOnDisk == File.ReadAllText(Path.Combine(profile, "tool-presets.json")) &&
                recipeSnapshot == JsonSerializer.Serialize(Field<ToolPresetLibrary>(session!, "_toolPresets").User.Single(p => p.Id == userPresetId).Parameters));
            Activate(EditorTool.Number);
            check("restored number parameters keep start step font size and default dimensions", editor.NumberStart == 103 && editor.NumberStep == 7 && editor.Style.FontSize == 32 &&
                editor.DefaultWidth == 177 && editor.DefaultHeight == 99 && editor.Style.Stroke == 0xFF9F6B53);
            Activate(EditorTool.Blur); check("restored blur strength is independent", editor.EffectStrength == 27);
            Activate(EditorTool.Pixelate); check("restored pixelate strength is independent", editor.EffectStrength == 41);
            Activate(EditorTool.Freehand); Invoke("FlushToolState", false);
            check("all reconstruction checks leave every fixture window hidden and input released", !host!.IsEditing &&
                host.Diagnostics.All(pair => !pair.DisplayVisible && !pair.InputVisible) && app.Windows.Cast<Window>().Except(initialWindows).All(window => !window.IsVisible));
            session!.Dispose(); session = null; host = null; PumpFor(TimeSpan.FromMilliseconds(10));

            string? output = Environment.GetEnvironmentVariable("FLOATCANVAS_TOOL_STATE_TEST_OUTPUT");
            if (!string.IsNullOrWhiteSpace(output))
            {
                string exported = Path.Combine(Path.GetFullPath(output), "profile"); Directory.CreateDirectory(exported);
                foreach (string file in Directory.EnumerateFiles(profile, "*.json")) File.Copy(file, Path.Combine(exported, Path.GetFileName(file)), overwrite: true);
                var persisted = new ToolStateStore(Path.Combine(exported, "tool-state.json"));
                File.WriteAllText(Path.Combine(output, "working-state-report.json"), JsonSerializer.Serialize(new
                { ProfileDirectory = exported, ExpectedFreehand = expectedPen, ExpectedFreehandSelection = persisted.Selections[EditorTool.Freehand],
                    ExpectedSelections = persisted.Selections, NativeWindowsShown = false, GlobalHotkeysRegistered = false }, new JsonSerializerOptions { WriteIndented = true }));
            }

            // Older cache records can retain an alias enum. The actual toolbar must
            // restore that family snapshot, not mistake its remembered alias for
            // a new explicit request to apply the official recipe.
            foreach (var (alias, family, presetId) in new[]
            {
                (EditorTool.Arrow, EditorTool.Line, "official.arrow"),
                (EditorTool.DoubleArrow, EditorTool.Line, "official.double-arrow"),
                (EditorTool.RoundedRectangle, EditorTool.Rectangle, "official.rounded-rectangle")
            })
            {
                NewSession();
                editor = Field<EditorController>(session!, "_editor"); panel = Field<ToolOptionsPanel>(session!, "_toolOptionsPanel");
                gallery = Field<ToolPresetGallery>(panel, "presets"); state = Field<ToolStateStore>(session!, "_toolStateStore");
                Activate(alias);
                Change(p => { p.Style.Stroke = 0xFF2468AC; p.Style.StrokeWidth = 13; p.Style.StrokePattern = StrokePattern.Dotted;
                    p.DefaultWidth = 233; p.DefaultHeight = 117; if (family == EditorTool.Rectangle) p.CornerRadius = 29; });
                var legacy = editor.Parameters; legacy.Tool = alias;
                editor.ApplyParameters(legacy); Invoke("RefreshToolbarState");
                var expected = legacy.Clone(); expected.Tool = family;
                Activate(EditorTool.Freehand);
                ClickToolbar(family);
                check($"actual toolbar restores edited {alias} instead of reapplying its remembered alias", SameParameters(editor.Parameters, expected) && gallery.SelectedId == presetId);

                editor.ApplyParameters(legacy); Invoke("RefreshToolbarState"); Activate(EditorTool.Freehand);
                Invoke("FlushToolState", false); session!.Dispose(); session = null; host = null; PumpFor(TimeSpan.FromMilliseconds(10));
                NewSession();
                editor = Field<EditorController>(session!, "_editor"); panel = Field<ToolOptionsPanel>(session!, "_toolOptionsPanel");
                gallery = Field<ToolPresetGallery>(panel, "presets"); state = Field<ToolStateStore>(session!, "_toolStateStore");
                check($"reconstruction retains the remembered {alias} toolbar record", state.Buttons[family] == alias);
                ClickToolbar(family);
                check($"actual toolbar restores edited {alias} after reconstruction", SameParameters(editor.Parameters, expected) && gallery.SelectedId == presetId);
                Activate(EditorTool.Freehand); Activate(alias);
                var baseline = Official(session!, presetId);
                check($"explicit {alias} command still applies its official recipe", JsonSerializer.Serialize(editor.Style) == JsonSerializer.Serialize(baseline.Style) &&
                    editor.CornerRadius == baseline.CornerRadius && gallery.SelectedId == presetId);
                session!.Dispose(); session = null; host = null; PumpFor(TimeSpan.FromMilliseconds(10));
            }

            void Activate(EditorTool tool) { Invoke("ActivateTool", tool); gallery.PrepareForShow(); }
            void Change(Action<ToolParameters> update) => typeof(ToolOptionsPanel).GetMethod("Change", Private)!.Invoke(panel, [update, false, null]);
            void ClickToolbar(EditorTool tool)
            {
                Field<Dictionary<EditorTool, Button>>(session!, "_toolbarToolButtons")[tool].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                gallery.PrepareForShow();
            }
            static bool SameParameters(ToolParameters first, ToolParameters second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
        }
        finally
        {
            session?.Dispose(); if (session is null) host?.Dispose();
            foreach (var window in app.Windows.Cast<Window>().Except(initialWindows).ToArray()) window.Close();
            var restore = new List<string> { "--profile", originalProfile };
            if (originalScope is { } scope) restore.Add(scope == OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens");
            ApplicationPaths.Configure(restore.ToArray()); L.Initialize(originalLanguage);
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        }

        void NewSession()
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
            var bounds = new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 30, 640, 160);
            var bounded = primary with { Bounds = bounds, WorkArea = bounds };
            Func<IReadOnlyList<MonitorInfo>> provider = () => [bounded];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            session = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", Private)!;
            (isolation.GetValue(host) as IDisposable)?.Dispose(); isolation.SetValue(host, null);
            // Preset IDs are reconciled when presented. Prepare only that gallery;
            // the fixture keeps its windows and unrelated property rows hidden.
            Field<ToolPresetGallery>(Field<ToolOptionsPanel>(session, "_toolOptionsPanel"), "presets").PrepareForShow();
        }
        object? Invoke(string name, params object[] args) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, args);
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static ToolParameters Official(DesktopSession session, string id) => Field<ToolPresetLibrary>(session, "_toolPresets").Official.Single(p => p.Id == id).Parameters;
    private static void SetField(object owner, string name, object? value) => owner.GetType().GetField(name, Private)!.SetValue(owner, value);

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
