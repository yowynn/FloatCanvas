using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class PresentationUiTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string locale = L.Preference; string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-presentation-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            Presets(folder, check); Configuration(folder, check); Creation(folder, check); Runtime(check); Tools(folder, check); Inspector(check); Modes(check);
            PresentationGlobalUiTests.Run(check, output is null ? null : Path.Combine(output, "global"));
            PresentationLayerParityTests.Run(check, folder);
            if (output is not null) Visuals(folder, output, check);
        }
        finally { L.Initialize(locale); AppTheme.Apply("light"); Directory.Delete(folder, true); }
    }
    private static void Presets(string folder, Action<string, bool> check)
    {
        var library = new ToolPresetLibrary(Path.Combine(folder, "official.json"));
        var presets = library.OfficialFor(EditorTool.Presentation);
        check("presentation appends its enum without renumbering existing tool and element IDs", (int)EditorTool.Canvas == 22 && (int)EditorTool.Presentation == 23 && (int)ElementKind.LiveBlur == 14 && (int)ElementKind.Presentation == 15);
        check("keyboard and mouse tools each provide three official recipes after the original 57", library.Official.Count == 63 && presets.Select(preset => preset.Id).SequenceEqual(new[] { "official.presentation-keys", "official.presentation-keycaps", "official.presentation-keyboard" }) && library.OfficialFor(EditorTool.PointerPresentation).Count == 3);
        var keys = library.Apply("official.presentation-keys"); var pointer = LegacyPointer(keys);
        check("key panel preset has bounded geometry canonical appearance and all-key recording", keys.DefaultWidth == 360 && keys.DefaultHeight == 120 && keys.Presentation is { Modules: PresentationModules.Keys, KeyDisplay: PresentationKeyDisplayMode.AllPhysicalKeys } && keys.Style.Foreground == 0xfff8fafc && keys.Style.Background == 0xdd20252f && keys.IsValid());
        check("legacy pointer tool snapshots remain readable without a new pointer-region recipe", pointer.IsValid() && library.Official.All(recipe => recipe.Id != "official.presentation-pointer"));
        var clone = pointer.Clone(); clone.Presentation!.TrailWidthPixels = 11;
        check("tool snapshot clones still own legacy presentation settings", pointer.Presentation!.TrailWidthPixels == 3 && keys.Presentation!.TrailWidthPixels == 3);
        var keyIcon = ToolParameterIcon.Create(keys); var pointerIcon = ToolParameterIcon.Create(pointer);
        var keyPaths = Descendants(keyIcon).OfType<System.Windows.Shapes.Path>().Select(path => path.Data?.ToString()).ToArray();
        var legacyPaths = Descendants(pointerIcon).OfType<System.Windows.Shapes.Path>().Select(path => path.Data?.ToString()).ToArray();
        check("legacy tool illustrations retain the same keyboard-style content cue", keyPaths.Length > 0 && keyPaths.SequenceEqual(legacyPaths));
    }
    private static void Configuration(string folder, Action<string, bool> check)
    {
        string path = Path.Combine(folder, "state.json"); var store = new ToolStateStore(path);
        var parameters = LegacyPointer(new ToolPresetLibrary(Path.Combine(folder, "unused.json")).Apply("official.presentation-keys"));
        parameters.Presentation!.KeyHoldMilliseconds = 1700; parameters.Presentation.KeyDisplay = PresentationKeyDisplayMode.AllPhysicalKeys;
        store.Tools[EditorTool.Presentation] = parameters; store.PreviewSources[EditorTool.Presentation] = parameters.Clone(); store.OfficialBaselines["official.presentation-pointer"] = parameters.Clone();
        store.Buttons[EditorTool.Blur] = EditorTool.Presentation; store.Save();
        var raw = JsonNode.Parse(File.ReadAllText(path))!; raw["Tools"]!["Presentation"]!["Presentation"]!["FutureParameter"] = 37;
        File.WriteAllText(path, raw.ToJsonString()); string before = File.ReadAllText(path); store = new(path);
        check("composite module flags survive all three tool snapshot dictionaries", new[] { store.Tools[EditorTool.Presentation], store.PreviewSources[EditorTool.Presentation], store.OfficialBaselines["official.presentation-pointer"] }.All(value => value.Presentation!.Modules == parameters.Presentation.Modules && value.Presentation.KeyHoldMilliseconds == 1700 && value.Presentation.KeyDisplay == PresentationKeyDisplayMode.AllPhysicalKeys));
        check("presentation configuration reads do not rewrite files or lose the More-tool alias", File.ReadAllText(path) == before && store.Buttons[EditorTool.Blur] == EditorTool.Presentation);
        store.Selections[EditorTool.Presentation] = "official.presentation-pointer"; store.Save(); raw = JsonNode.Parse(File.ReadAllText(path))!;
        check("unrelated tool-state saves preserve future presentation settings", raw["Tools"]!["Presentation"]!["Presentation"]!["FutureParameter"]!.GetValue<int>() == 37);
        var library = new ToolPresetLibrary(Path.Combine(folder, "personal.json")); var saved = library.Save("Complete presentation", parameters);
        check("personal presentation recipes retain every hidden setting and module bit", JsonSerializer.Serialize(new ToolPresetLibrary(Path.Combine(folder, "personal.json")).Apply(saved.Id)) == JsonSerializer.Serialize(parameters));
        var retired = parameters.Clone(); retired.Tool = EditorTool.Line;
        check("changing tool families preserves hidden presentation configuration without assigning it to source leaves", retired.Presentation!.TrailWidthPixels == parameters.Presentation.TrailWidthPixels && retired.IsValid());
    }
    private static void Creation(string folder, Action<string, bool> check)
    {
        var session = new EditorSession(new() { Viewport = new(0, 0, 1400, 900) }); var controller = new EditorController(session);
        controller.ApplyParameters(new ToolPresetLibrary(Path.Combine(folder, "create.json")).Apply("official.presentation-keys"));
        controller.Pointer(new(OverlayPointerKind.Down, new(300, 200), OverlayMouseButton.Left, OverlayModifiers.Control));
        controller.Pointer(new(OverlayPointerKind.Up, new(300, 200), OverlayMouseButton.Left, OverlayModifiers.Control));
        var created = session.Document.Elements.Single();
        check("presentation click creates one selectable ordinary authored region", created.Kind == ElementKind.Presentation && created.Presentation?.Modules == PresentationModules.Keys && created.Width == 360 && created.Height == 120 && session.Selection.SetEquals([created.Id]) && session.UndoCount == 1);
        controller.Presentation!.KeyHoldMilliseconds = 1900;
        check("created presentation settings do not alias future tool settings", created.Presentation!.KeyHoldMilliseconds == 900);
        check("presentation creation is a single reversible transaction", session.Undo() && session.Document.Elements.Count == 0 && session.Redo() && session.Document.Elements.Single().Presentation!.KeyHoldMilliseconds == 900);
        controller.Tool = EditorTool.Presentation;
        controller.Pointer(new(OverlayPointerKind.Down, new(600, 300), OverlayMouseButton.Left, OverlayModifiers.Control));
        controller.Pointer(new(OverlayPointerKind.Move, new(860, 470), OverlayMouseButton.Left, OverlayModifiers.Control));
        controller.Pointer(new(OverlayPointerKind.Up, new(860, 470), OverlayMouseButton.Left, OverlayModifiers.Control));
        var drawn = session.Document.Elements.Last();
        check("presentation drag creates a free-proportion common region", drawn.Width == 260 && drawn.Height == 170 && !ElementGeometry.PreservesAspect(drawn) && drawn.Presentation!.KeyHoldMilliseconds == 1900);
        var drawnSettings = drawn.Presentation!; drawnSettings.Enabled = false; drawnSettings.Modules = PresentationModules.None;
        check("a disabled empty presentation region remains pickable by its stable bounds", ElementGeometry.HitTest(drawn, new(700, 380), 0));

        var projected = new SceneDocument { Viewport = new(0, 0, 800, 700) };
        projected.Canvas.Projection.Enabled = true;
        projected.Canvas.Projection.Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1);
        projected.Canvas.Projection.Quad = [new(0, 0), new(800, 0), new(800, 700), new(0, 700)];
        session = new(projected); controller = new(session) { BillboardMode = true, BillboardWorkflow = BillboardCreationWorkflow.ScreenFirst };
        controller.ApplyParameters(LegacyPointer(new ToolPresetLibrary(Path.Combine(folder, "billboard.json")).Apply("official.presentation-keys")));
        controller.Pointer(new(OverlayPointerKind.Down, new(100, 100), OverlayMouseButton.Left, OverlayModifiers.Control));
        controller.Pointer(new(OverlayPointerKind.Move, new(300, 400), OverlayMouseButton.Left, OverlayModifiers.Control));
        check("presentation screen-first draft crosses the horizon without publishing source geometry", controller.ScreenBillboardDraft?.Kind == ElementKind.Presentation && session.Document.Elements.Count == 0 && session.UndoCount == 0);
        controller.Pointer(new(OverlayPointerKind.Up, new(300, 400), OverlayMouseButton.Left, OverlayModifiers.Control));
        created = session.Document.Elements.Single(); var projectedBounds = CanvasMapping.Create(session.Document).ProjectedBounds(created, false);
        check("legacy tool snapshots create keyboard billboards with exact placement and one undo", created.IsBillboard && Math.Abs(projectedBounds.X - 100) < 1e-6 && Math.Abs(projectedBounds.Y - 100) < 1e-6 && Math.Abs(projectedBounds.Width - 200) < 1e-6 && Math.Abs(projectedBounds.Height - 300) < 1e-6 && created.Presentation!.Modules == PresentationModules.Keys && controller.Presentation!.Modules == (PresentationModules.Cursor | PresentationModules.Clicks | PresentationModules.Trail) && session.UndoCount == 1 && session.Selection.SetEquals([created.Id]));
        string beforeCancel = JsonSerializer.Serialize(session.Document);
        controller.Pointer(new(OverlayPointerKind.Down, new(400, 100), OverlayMouseButton.Left, OverlayModifiers.Control));
        controller.Pointer(new(OverlayPointerKind.Move, new(600, 400), OverlayMouseButton.Left, OverlayModifiers.Control)); controller.Cancel();
        check("cancelled presentation billboard drafts leave no geometry or extra undo", JsonSerializer.Serialize(session.Document) == beforeCancel && session.UndoCount == 1 && !session.IsTransactionActive && controller.ScreenBillboardDraft is null);
    }
    private static void Runtime(Action<string, bool> check)
    {
        var panel = new PresentationRuntimeControls(); int toggles = 0, bakes = 0; Guid node = Guid.NewGuid();
        panel.ToggleRequested += () => toggles++; panel.BakeRequested += _ => bakes++;
        check("presentation runtime exposes status without global keyboard or bake buttons", panel.State == new PresentationRuntimeUiState(false, false, false) && !Descendants(panel).OfType<Button>().Any());
        panel.Refresh(_ => new(true, true, false, "Presentation.Status.Running"), node);
        check("running key status needs no manual start interaction", Field<TextBlock>(panel, "status").Text == L.Get("Presentation.Status.Running") && toggles == 0 && bakes == 0);
        panel.Refresh(_ => new(true, false, true, "Presentation.Status.NoLayers"), node);
        check("legacy bake availability cannot reintroduce an authored pointer action", !Descendants(panel).OfType<Button>().Any() && toggles == 0 && bakes == 0);
    }
    private static void Tools(string folder, Action<string, bool> check)
    {
        var session = new EditorSession(); var controller = new EditorController(session); controller.ApplyParameters(new ToolPresetLibrary(Path.Combine(folder, "tools.json")).Apply("official.presentation-keys"));
        var panel = new ToolOptionsPanel(controller, new(Path.Combine(folder, "tool-recipes.json")), new(Path.Combine(folder, "colors.json"))); panel.PrepareForShow();
        var settings = Field<PresentationSettingsPanel>(panel, "presentationSettings"); string document = JsonSerializer.Serialize(session.Document);
        panel.SetPresentationRuntimeUi(_ => new(true, true, false, "Presentation.Status.Running"));
        check("keyboard tool has no recording checkbox and preserves authored data", !Descendants(settings).OfType<CheckBox>().Any() && controller.Presentation is { Modules: PresentationModules.Keys } && JsonSerializer.Serialize(session.Document) == document && session.UndoCount == 0);
        var hold = Find<TextBox>(settings, "Presentation.KeyHold"); hold.Text = "42";
        check("out-of-range presentation text drafts cannot publish tool values", controller.Presentation!.KeyHoldMilliseconds == 900 && Field<TextBlock>(settings, "error").Visibility == Visibility.Visible);
        panel.RefreshPresentationRuntimeUi();
        check("runtime-only tool refresh preserves a local numeric draft and leaves source history untouched", hold.Text == "42" && controller.Presentation.KeyHoldMilliseconds == 900 && session.UndoCount == 0 && JsonSerializer.Serialize(session.Document) == document);
        hold.Text = "1800"; panel.PrepareForShow(); check("valid presentation values publish without changing hidden module parameters", controller.Presentation.KeyHoldMilliseconds == 1800 && controller.Presentation.TrailMilliseconds == 800 && controller.Presentation.KeyDisplay == PresentationKeyDisplayMode.AllPhysicalKeys);
    }
    private static void Inspector(Action<string, bool> check)
    {
        var first = new CanvasElement { Kind = ElementKind.Presentation, Width = 300, Height = 180, Presentation = new() { Modules = PresentationModules.Keys | PresentationModules.Cursor, CursorRadiusPixels = 18, KeyPanelMaximumWidth = 250 } };
        var second = first.Clone(true); second.Transform = Affine2D.Translation(400, 0); second.Presentation!.CursorRadiusPixels = 42; second.Presentation.KeyPanelMaximumWidth = 480;
        var session = new EditorSession(new() { Elements = [first, second] }); session.SetSelection([first.Id, second.Id]);
        string original = JsonSerializer.Serialize(session.Document); var inspector = new InspectorWindow(() => session); inspector.PrepareForShow();
        var settings = Field<PresentationSettingsPanel>(inspector, "presentationSettings");
        check("selected keyboard layers expose their automatic observation status", Field<PresentationRuntimeControls>(inspector, "presentationRuntime").Visibility == Visibility.Visible);
        check("multi-selected keyboard widths use the standard mixed-value placeholder", Find<TextBox>(settings, "Presentation.PanelWidth").Text == "—");
        var hold = Find<TextBox>(settings, "Presentation.KeyHold"); hold.Text = "1500"; inspector.CompletePendingEdit();
        check("multi-keyboard edits preserve each source's hidden legacy settings in one undo", session.Document.Elements.All(element => element.Presentation!.KeyHoldMilliseconds == 1500) && session.Document.Elements.Select(element => element.Presentation!.CursorRadiusPixels).SequenceEqual(new[] { 18d, 42d }) && session.Document.Elements.Select(element => element.Presentation!.KeyPanelMaximumWidth).SequenceEqual(new[] { 250d, 480d }) && session.UndoCount == 1);
        check("keyboard Inspector has no recording or layer-owned pointer switches", !Descendants(settings).OfType<CheckBox>().Any());
        check("presentation Inspector undo restores the exact owned source snapshot", session.Undo() && JsonSerializer.Serialize(session.Document) == original);
        inspector.PrepareForShow(); string unchanged = JsonSerializer.Serialize(session.Document); session.BeginTransaction("foreign");
        hold = Find<TextBox>(settings, "Presentation.KeyHold"); hold.Text = "1775";
        check("presentation controls cannot publish into a foreign transaction", session.IsTransactionActive && session.UndoCount == 0 && JsonSerializer.Serialize(session.Document) == unchanged);
        session.Cancel(); inspector.PrepareForShow();
        var group = session.CreateEmptyGroup(); session.SetSelection([first.Id, second.Id]); inspector.PrepareForShow(); string scoped = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount;
        session.EnterScope(group); session.LeaveScope(); session.SetSelection([first.Id, second.Id]);
        hold.Text = "1880";
        check("a stale hidden presentation form cannot publish after a scope round trip", session.UndoCount == undo && JsonSerializer.Serialize(session.Document) == scoped);
        SessionWindowCoordinator.Close(inspector);
        var sharedSource = new CanvasElement { Kind = ElementKind.Presentation, Width = 300, Height = 180, Presentation = new() };
        var sharedOwner = new EditorSession(new() { Elements = [sharedSource] }); sharedOwner.SetSelection([sharedSource.Id]);
        var prefab = sharedOwner.CreatePrefabFromSelection("Shared presentation"); sharedOwner.SetSelection([prefab.InstanceId]);
        string sharedBefore = JsonSerializer.Serialize(sharedOwner.Document); int sharedUndo = sharedOwner.UndoCount;
        var sharedInspector = new InspectorWindow(() => sharedOwner); sharedInspector.SetPresentationRuntimeUi(_ => new(true, true, true, "Presentation.Status.Running"));
        sharedInspector.PrepareForShow();
        check("instance selection keeps virtual definition settings read-only", Field<StackPanel>(sharedInspector, "propertyPanel").Visibility == Visibility.Collapsed);
        sharedInspector.RefreshPresentationRuntimeUi();
        check("runtime status never exposes pointer bake or edits shared source history", !Descendants(Field<PresentationRuntimeControls>(sharedInspector, "presentationRuntime")).OfType<Button>().Any() && sharedOwner.UndoCount == sharedUndo && JsonSerializer.Serialize(sharedOwner.Document) == sharedBefore);
        SessionWindowCoordinator.Close(sharedInspector);
        var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 120, Height = 80 };
        var ordinary = new EditorSession(new() { Elements = [rectangle] }); ordinary.SetSelection([rectangle.Id]);
        var ordinaryInspector = new InspectorWindow(() => ordinary); ordinaryInspector.SetPresentationRuntimeUi(_ => new(true, true, false, "Presentation.Status.Running")); ordinaryInspector.PrepareForShow();
        check("an auto-armed runtime cannot add presentation status to a non-keyboard Inspector", Field<PresentationRuntimeControls>(ordinaryInspector, "presentationRuntime").Visibility == Visibility.Collapsed);
        SessionWindowCoordinator.Close(ordinaryInspector);
    }
    private static void Modes(Action<string, bool> check)
    {
        var layout = PresentationKeyboardLayout.CreateDefault(); layout.Keys[0].Enabled = false;
        var first = new CanvasElement { Kind = ElementKind.Presentation, Width = 300, Height = 180, Presentation = new() { DisplayStyle = PresentationDisplayStyle.Log, KeyboardLayout = layout, RecentChordLimit = 2, KeyHoldMilliseconds = 700 } };
        var second = first.Clone(true); second.Presentation!.DisplayStyle = PresentationDisplayStyle.Keycaps; second.Presentation.RecentChordLimit = 7; second.Presentation.KeyHoldMilliseconds = 1900;
        var session = new EditorSession(new() { Elements = [first, second] }); session.SetSelection([first.Id, second.Id]);
        var inspector = new InspectorWindow(() => session); inspector.PrepareForShow();
        var panel = Field<PresentationSettingsPanel>(inspector, "presentationSettings");
        var mode = ModeControl(panel, "DisplayStyle"); var appearance = ModeControl(panel, "LogStyle");
        check("mixed recording appearances retain a selected recording mode and an unselected appearance", Equals(mode.SelectedValue, PresentationDisplayStyle.Log) && appearance.SelectedIndex == -1);
        string HiddenValues() => JsonSerializer.Serialize(session.Document.Elements.Select(element => new { element.Presentation!.RecentChordLimit, element.Presentation.KeyHoldMilliseconds, element.Presentation.KeyDisplay, element.Presentation.KeyPanelAlignment, element.Presentation.KeyPanelMaximumWidth, element.Presentation.KeyboardLayout }));
        string hidden = HiddenValues();
        mode.SelectedValue = PresentationDisplayStyle.Keyboard; inspector.CompletePendingEdit();
        check("switching to live keyboard edits only the existing style field", session.Document.Elements.All(element => element.Presentation!.DisplayStyle == PresentationDisplayStyle.Keyboard) && HiddenValues() == hidden);
        mode.SelectedValue = PresentationDisplayStyle.Log; inspector.CompletePendingEdit();
        check("returning to recording restores each selected layer's prior appearance", session.Document.Elements.Select(element => element.Presentation!.DisplayStyle).SequenceEqual(new[] { PresentationDisplayStyle.Log, PresentationDisplayStyle.Keycaps }) && appearance.SelectedIndex == -1 && HiddenValues() == hidden);
        appearance.SelectedValue = PresentationDisplayStyle.Keycaps; inspector.CompletePendingEdit();
        mode.SelectedValue = PresentationDisplayStyle.Keyboard; inspector.CompletePendingEdit(); mode.SelectedValue = PresentationDisplayStyle.Log; inspector.CompletePendingEdit();
        check("an explicitly unified recording appearance survives a mode round trip", session.Document.Elements.All(element => element.Presentation!.DisplayStyle == PresentationDisplayStyle.Keycaps) && Equals(appearance.SelectedValue, PresentationDisplayStyle.Keycaps) && HiddenValues() == hidden);
        session.Execute("mix keyboard modes", document => document.Elements[1].Presentation!.DisplayStyle = PresentationDisplayStyle.Keyboard); inspector.PrepareForShow();
        check("a mixed recording and live-keyboard selection has no false primary selection", mode.SelectedIndex == -1 && Field<Dictionary<string, FrameworkElement>>(panel, "rows")["KeyPanelAlignment"].Visibility == Visibility.Visible);
        SessionWindowCoordinator.Close(inspector);
        var detached = new PresentationSettingsPanel(); detached.Refresh([(first.Id, first.Presentation!), (second.Id, second.Presentation!)], new object());
        var fresh = Guid.NewGuid(); detached.Refresh([(fresh, new PresentationSettings { DisplayStyle = PresentationDisplayStyle.Keyboard })], new object());
        var cache = Field<Dictionary<Guid, PresentationDisplayStyle>>(detached, "rememberedLogStyles");
        check("recording appearance cache is bounded to the current editing context", cache.Count == 1 && cache.GetValueOrDefault(fresh) == PresentationDisplayStyle.Log);
    }
    private static SegmentedChoice ModeControl(PresentationSettingsPanel panel, string key)
    {
        var entries = Field<System.Collections.IDictionary>(panel, "entries"); var entry = entries[key]!;
        return (SegmentedChoice)entry.GetType().GetProperty("Control")!.GetValue(entry)!;
    }
    private static void Visuals(string folder, string output, Action<string, bool> check)
    {
        Directory.CreateDirectory(output);
        foreach (string locale in new[] { "zh-CN", "en" }) foreach (string theme in new[] { "light", "dark" })
        {
            L.Initialize(locale); AppTheme.Apply(theme);
            var parameters = new ToolPresetLibrary(Path.Combine(folder, "visual.json")).Apply("official.presentation-keys");
            var controller = new EditorController(new EditorSession()); controller.ApplyParameters(parameters);
            var panel = new ToolOptionsPanel(controller, new(Path.Combine(folder, "visual-recipes.json")), new(Path.Combine(folder, "visual-colors.json")));
            panel.SetPresentationRuntimeUi(_ => new(true, false, true, "Presentation.Status.PausedEditing")); panel.PrepareForShow();
            Save(panel, ToolOptionsPanel.PreferredWidth, 620, Path.Combine(output, $"presentation-tools-{locale}-{theme}.png"));
            var element = new CanvasElement { Kind = ElementKind.Presentation, Width = 500, Height = 300, Style = parameters.Style.Clone(), Presentation = parameters.Presentation!.Clone() };
            var session = new EditorSession(new() { Elements = [element] }); session.SetSelection([element.Id]); var inspector = new InspectorWindow(() => session);
            inspector.SetPresentationRuntimeUi(_ => new(true, false, true, "Presentation.Status.PausedEditing")); inspector.PrepareForShow(); var body = (FrameworkElement)inspector.Content;
            Save(body, 300, 700, Path.Combine(output, $"presentation-inspector-{locale}-{theme}.png"), inspector.Background);
            Descendants(body).OfType<ScrollViewer>().First().ScrollToEnd(); body.UpdateLayout();
            Save(body, 300, 700, Path.Combine(output, $"presentation-settings-{locale}-{theme}.png"), inspector.Background);
            var settings = Field<PresentationSettingsPanel>(inspector, "presentationSettings");
            check(locale + " " + theme + " presentation modules and values fit a 300-wide Inspector", settings.ActualWidth > 0 && settings.ActualWidth <= 300 && Descendants(settings).OfType<ComboBox>().All(control => control.ActualWidth > 0 && control.ActualWidth <= 300));
            check(locale + " " + theme + " presentation parameter captions wrap without ellipsis", Descendants(settings).OfType<TextBlock>().Where(label => label.Text.Contains(" (", StringComparison.Ordinal)).All(label => label.TextWrapping == TextWrapping.Wrap && label.TextTrimming == TextTrimming.None));
            check(locale + " " + theme + " presentation evidence has no native host", new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0 && PresentationSource.FromVisual(panel) is null);
            SessionWindowCoordinator.Close(inspector);
        }
    }
    private static void Save(FrameworkElement body, double width, double height, string path, Brush? background = null)
    {
        body.Measure(new(width, height)); body.Arrange(new(0, 0, width, height)); body.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        if (background is not null) { var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(background, null, new(0, 0, width, height)); bitmap.Render(visual); }
        bitmap.Render(body); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static T Find<T>(DependencyObject root, string labelKey) where T : DependencyObject
    {
        string label = L.Get(labelKey);
        if (typeof(T) == typeof(TextBox) && Descendants(root).OfType<NumericValueField>().SingleOrDefault(control => AutomationProperties.GetName(control) == label) is { } numeric)
            return (T)(object)numeric.Input;
        return Descendants(root).OfType<T>().Single(control => AutomationProperties.GetName(control) == label);
    }
    private static ToolParameters LegacyPointer(ToolParameters keyParameters)
    {
        var legacy = keyParameters.Clone(); legacy.DefaultWidth = 960; legacy.DefaultHeight = 540;
        legacy.Presentation!.Modules = PresentationModules.Cursor | PresentationModules.Clicks | PresentationModules.Trail;
        return legacy;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
