using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class PresentationGlobalUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string profile = ApplicationPaths.ProfileDirectory, locale = L.Preference;
        var scope = ApplicationPaths.RequestedMonitorScope;
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-global-presentation-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            ApplicationPaths.Configure(["--profile", folder]);
            PersonalConfiguration(folder, check); ToolOwnership(folder, check);
            if (output is not null) Visuals(output, check);
        }
        finally
        {
            var arguments = new List<string> { "--profile", profile };
            if (scope is FloatCanvas.Windows.OverlayMonitorScope.PrimaryScreen) arguments.Add("--primary-screen");
            else if (scope is FloatCanvas.Windows.OverlayMonitorScope.AllScreens) arguments.Add("--all-screens");
            ApplicationPaths.Configure(arguments.ToArray()); L.Initialize(locale); AppTheme.Apply("light");
            Directory.Delete(folder, true);
        }
    }

    private static void PersonalConfiguration(string folder, Action<string, bool> check)
    {
        var source = new PresentationGlobalSettings(); var preferences = new UserSettings { PresentationGlobal = source };
        source.Pointer.LeftClick.Color = 0xff102030;
        check("global presentation defaults capture every app without enabling pointer effects", preferences.PresentationGlobal.KeyboardScope == PresentationKeyboardScope.AllApplications && !preferences.PresentationGlobal.Pointer.Enabled && preferences.PresentationGlobal.Pointer.CursorEnabled && preferences.PresentationGlobal.Pointer.ClicksEnabled);
        check("personal presentation assignment owns all nested click styles", preferences.PresentationGlobal.Pointer.LeftClick.Color != source.Pointer.LeftClick.Color);
        preferences.PresentationGlobal = null!;
        check("null global preferences reset safely to valid independent defaults", preferences.PresentationGlobal.IsValid() && !preferences.PresentationGlobal.Pointer.Enabled);
        preferences.PresentationGlobal.Pointer.Enabled = true; preferences.PresentationGlobal.Pointer.TrailEnabled = true;
        preferences.PresentationGlobal.Pointer.RightClick.Shape = PresentationClickShape.Disc;
        preferences.PresentationGlobal.Pointer.MiddleClick.Color = 0xff123456; preferences.PresentationGlobal.Pointer.TrailWidthPixels = 9;
        preferences.PresentationGlobal.KeyboardScope = PresentationKeyboardScope.FloatCanvasOnly; preferences.Save();
        string path = Path.Combine(folder, "settings.json"); string before = File.ReadAllText(path);
        var loaded = UserSettings.Load();
        check("actual personal settings roundtrip independent buttons and global keyboard scope", JsonSerializer.Serialize(loaded.PresentationGlobal) == JsonSerializer.Serialize(preferences.PresentationGlobal));
        check("reading global presentation preferences never rewrites their source file", File.ReadAllText(path) == before);
        var raw = JsonNode.Parse(before)!;
        raw["PresentationGlobal"]!["FutureGlobalSetting"] = 17;
        raw["PresentationGlobal"]!["Pointer"]!["LeftClick"]!["FutureClickSetting"] = "preserve";
        raw["PresentationGlobal"]!["Pointer"]!["LeftClick"]!["RadiusPixels"] = 99999;
        raw["PresentationGlobal"]!["Pointer"]!["CursorOpacity"] = -5;
        raw["PresentationGlobal"]!["KeyboardScope"] = 99;
        File.WriteAllText(path, raw.ToJsonString()); before = File.ReadAllText(path);
        loaded = UserSettings.Load();
        check("invalid global fields use bounded defaults while valid sibling choices survive", loaded.PresentationGlobal.IsValid() && loaded.PresentationGlobal.Pointer.LeftClick.RadiusPixels == 24 && loaded.PresentationGlobal.Pointer.CursorOpacity == 1 && loaded.PresentationGlobal.KeyboardScope == PresentationKeyboardScope.AllApplications && loaded.PresentationGlobal.Pointer.RightClick.Shape == PresentationClickShape.Disc && loaded.PresentationGlobal.Pointer.MiddleClick.Color == 0xff123456 && loaded.PresentationGlobal.Pointer.TrailWidthPixels == 9);
        check("invalid personal input is normalized only in memory on read", File.ReadAllText(path) == before);
        loaded.Theme = "dark"; loaded.Save(); raw = JsonNode.Parse(File.ReadAllText(path))!;
        check("unrelated personal saves retain unknown global and button preference siblings", raw["PresentationGlobal"]!["FutureGlobalSetting"]!.GetValue<int>() == 17 && raw["PresentationGlobal"]!["Pointer"]!["LeftClick"]!["FutureClickSetting"]!.GetValue<string>() == "preserve");
    }

    private static void ToolOwnership(string folder, Action<string, bool> check)
    {
        var source = new CanvasElement { Kind = ElementKind.Rectangle, Width = 180, Height = 90 };
        var session = new EditorSession(new() { Elements = [source] }); session.SetSelection([source.Id]);
        var controller = new EditorController(session) { Tool = EditorTool.PointerPresentation };
        var library = new ToolPresetLibrary(Path.Combine(folder, "tool-presets.json"));
        var panel = new ToolOptionsPanel(controller, library, new ColorLibrary(Path.Combine(folder, "colors.json")));
        var global = new PresentationGlobalSettings(); int edits = 0; PresentationPointerSettings? last = null;
        panel.ParametersChanged += () => { edits++; last = controller.Parameters.PointerPresentation!; global.Pointer = last.Clone(); panel.RefreshPresentationGlobalUi(); };
        panel.SetPresentationGlobalUi(() => global.Clone()); panel.PrepareForShow();
        var editor = Field<PresentationGlobalSettingsPanel>(panel, "presentationGlobalSettings");
        string document = JsonSerializer.Serialize(session.Document), recipes = JsonSerializer.Serialize(library.User);
        check("mouse tool refresh is silent and leaves its master off with editable options", edits == 0 && !global.Pointer.Enabled && Control<CheckBox>(editor, "CursorEnabled").IsEnabled && Field<Dictionary<string, FrameworkElement>>(editor, "rows")["CursorColor"].Visibility == Visibility.Visible);
        check("mouse colors are visible swatches and have no second opacity controls", Field<Grid>(Control<ColorField>(editor, "CursorColor"), "swatch").Children.Count == 1 && !Entries(editor).Contains("CursorOpacity") && !Entries(editor).Contains("TrailOpacity") && !Entries(editor).Contains("RightClick.Opacity"));
        Change(editor, "PointerEnabled", true);
        check("mouse master publishes standard tool parameters without a scene edit", edits == 1 && global.Pointer.AnyEnabled && controller.Parameters.PointerPresentation!.Enabled && session.UndoCount == 0 && JsonSerializer.Serialize(session.Document) == document);
        Control<ColorField>(editor, "RightClick.Color").SetUserValue(0x8c2468ac);
        Control<SegmentedChoice>(editor, "RightClick.Shape").SelectedValue = PresentationClickShape.Disc;
        check("click color alpha is the only opacity value and disc hides ring width", global.Pointer.RightClick.Color == 0x8c2468ac && global.Pointer.RightClick.Opacity == 1 && global.Pointer.RightClick.Shape == PresentationClickShape.Disc && global.Pointer.LeftClick.Color == 0xffffc107 && Field<Dictionary<string, FrameworkElement>>(editor, "rows")["RightClick.StrokeWidthPixels"].Visibility == Visibility.Collapsed);
        foreach (string prefix in new[] { "LeftClick", "RightClick", "MiddleClick" })
        {
            string retainedStyle = JsonSerializer.Serialize(global.Pointer);
            Change(editor, prefix + ".Enabled", false);
            var condition = Field<Dictionary<string, PropertyChangeRegion>>(editor, "conditions")[prefix + ".Enabled"];
            int beforeHidden = edits; Control<NumericValueField>(editor, prefix + ".RadiusPixels").Input.Text = "50";
            check(prefix + " options collapse below their switch and reject hidden stale edits", Field<Dictionary<string, FrameworkElement>>(editor, "rows")[prefix + ".Color"].Visibility == Visibility.Collapsed && edits == beforeHidden);
            Change(editor, prefix + ".Enabled", true);
            check(prefix + " reenable restores its independently retained style", Field<Dictionary<string, FrameworkElement>>(editor, "rows")[prefix + ".Color"].Visibility == Visibility.Visible && JsonSerializer.Serialize(global.Pointer) == retainedStyle);
        }
        var radius = Control<NumericValueField>(editor, "CursorRadiusPixels"); int beforeInvalid = edits; radius.Input.Text = "9999";
        check("out-of-range mouse numeric drafts cannot publish tool values", edits == beforeInvalid && global.Pointer.CursorRadiusPixels == 18 && Field<TextBlock>(panel, "status").Visibility == Visibility.Visible && Field<TextBlock>(panel, "status").Text.StartsWith("⚠ ", StringComparison.Ordinal));
        panel.RefreshPresentationRuntimeUi();
        check("runtime status refresh leaves the user's mouse numeric draft untouched", radius.Input.Text == "9999" && edits == beforeInvalid);
        radius.Input.Text = "36"; last!.RightClick.Color = 0xff000000;
        check("pointer edits own their clones without altering unrelated personal settings", global.Pointer.RightClick.Color == 0x8c2468ac && global.Pointer.CursorRadiusPixels == 36 && global.KeyboardScope == PresentationKeyboardScope.AllApplications);
        Change(editor, "TrailEnabled", true); Control<NumericValueField>(editor, "TrailWidthPixels").Input.Text = "7";
        Control<NumericValueField>(editor, "TrailMilliseconds").Input.Text = "1200"; Control<ColorField>(editor, "TrailColor").SetUserValue(0xb3aabbcc);
        check("mouse trail exposes width lifetime and color alpha without opacity multiplier", global.Pointer.TrailEnabled && global.Pointer.TrailWidthPixels == 7 && global.Pointer.TrailMilliseconds == 1200 && global.Pointer.TrailOpacity == 1 && global.Pointer.TrailColor == 0xb3aabbcc);
        Change(editor, "PointerEnabled", false);
        var retained = global.Pointer.Clone(); retained.TrailWidthPixels = 11; int pausedEdits = edits;
        Control<NumericValueField>(editor, "TrailWidthPixels").Input.Text = "11";
        check("mouse master off keeps options editable without enabling effects or altering other styles", !global.Pointer.AnyEnabled && edits == pausedEdits + 1 && JsonSerializer.Serialize(global.Pointer) == JsonSerializer.Serialize(retained));
        panel.SetPresentationGlobalUi(null);
        check("mouse tool has no hidden global keyboard scope editor", !Field<Dictionary<string, FrameworkElement>>(panel, "rows").ContainsKey("PresentationKeyboardScope") && edits == pausedEdits + 1);
        check("mouse parameters and keyboard scope edits leave authored source history and recipes unchanged", session.UndoCount == 0 && JsonSerializer.Serialize(session.Document) == document && JsonSerializer.Serialize(library.User) == recipes);
        check("keyboard runtime status has no global Start Stop or Bake button", !Descendants(Field<PresentationRuntimeControls>(panel, "presentationRuntime")).OfType<Button>().Any());
        controller.Tool = EditorTool.PointerPresentation; panel.PrepareForShow();
        var on = controller.Parameters; on.PointerPresentation!.Enabled = true; controller.ApplyParameters(on); panel.PrepareForShow();
        Field<ToolPresetGallery>(panel, "presets").Apply("official.presentation-pointer-trail");
        check("applying a mouse preset retains the currently enabled global master", controller.Parameters.PointerPresentation is { Enabled: true, TrailEnabled: true });
        var off = controller.Parameters; off.PointerPresentation!.Enabled = false; controller.ApplyParameters(off);
        Field<ToolPresetGallery>(panel, "presets").Apply("official.presentation-pointer-halo");
        check("applying another mouse preset cannot enable a disabled global master", controller.Parameters.PointerPresentation is { Enabled: false, CursorEnabled: true });
    }
    private static void Visuals(string output, Action<string, bool> check)
    {
        Directory.CreateDirectory(output);
        foreach (string locale in new[] { "zh-CN", "en" }) foreach (string theme in new[] { "light", "dark" })
        {
            L.Initialize(locale); AppTheme.Apply(theme);
            var settings = new PresentationGlobalSettings(); settings.Pointer.Enabled = true; settings.Pointer.TrailEnabled = true;
            var panel = new PresentationGlobalSettingsPanel(); PropertyPanelLayout.Scope(panel); panel.Refresh(settings);
            var scroller = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var body = new Border { Child = scroller, Padding = new(10), Width = 300, Height = 700 };
            body.SetResourceReference(Border.BackgroundProperty, "CanvasSurface"); body.SetResourceReference(TextElement.ForegroundProperty, "CanvasText");
            Save(body, Path.Combine(output, $"presentation-global-top-{locale}-{theme}.png"));
            bool initialCaptionsReady = Descendants(panel).OfType<SegmentedChoice>().Count() == 3 && Descendants(panel).OfType<SegmentedChoice>().All(choice => choice.Items.Count == 2 && choice.SelectedItem is { ActualWidth: > 0, ActualHeight: > 0 });
            scroller.ScrollToVerticalOffset(440); body.UpdateLayout();
            Save(body, Path.Combine(output, $"presentation-global-clicks-{locale}-{theme}.png"));
            scroller.ScrollToEnd(); body.UpdateLayout();
            Save(body, Path.Combine(output, $"presentation-global-trail-{locale}-{theme}.png"));
            check(locale + " " + theme + " global presentation controls fit a 300-wide panel with drawable first-frame selected captions", initialCaptionsReady && panel.ActualWidth is > 0 and < 300 && scroller.ScrollableHeight > 0 && Descendants(panel).OfType<NumericValueField>().All(control => control.ActualWidth > 80 && control.ActualWidth < 300) && Descendants(panel).OfType<ColorField>().All(control => control.ActualWidth > 80 && control.ActualWidth < 300));
            check(locale + " " + theme + " pointer options use the existing conditional regions immediately after their toggles", ConditionsFollowToggles(panel) && !Entries(panel).Contains("KeyboardScope"));
            check(locale + " " + theme + " synthetic evidence creates no native presentation window", PresentationSource.FromVisual(body) is null);
        }
    }

    private static void Change(PresentationGlobalSettingsPanel panel, string key, bool value)
    {
        if (key == "PointerEnabled") { var master = Control<CaptionModeSwitch>(panel, key); if (master.SelectedFirst != value) master.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        else { var box = Control<CheckBox>(panel, key); box.IsChecked = value; box.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); }
    }
    private static T Control<T>(PresentationGlobalSettingsPanel panel, string key) where T : FrameworkElement
    {
        var entries = (IDictionary)typeof(PresentationGlobalSettingsPanel).GetField("entries", Hidden)!.GetValue(panel)!;
        return (T)entries[key]!.GetType().GetProperty("Control")!.GetValue(entries[key])!;
    }
    private static IDictionary Entries(PresentationGlobalSettingsPanel panel) => (IDictionary)typeof(PresentationGlobalSettingsPanel).GetField("entries", Hidden)!.GetValue(panel)!;
    private static bool ConditionsFollowToggles(PresentationGlobalSettingsPanel panel)
    {
        var rows = Field<Dictionary<string, FrameworkElement>>(panel, "rows");
        return Field<Dictionary<string, PropertyChangeRegion>>(panel, "conditions").All(pair =>
            LogicalTreeHelper.GetParent(pair.Value) is Panel parent && ReferenceEquals(LogicalTreeHelper.GetParent(rows[pair.Key]), parent) &&
            parent.Children.IndexOf(pair.Value) == parent.Children.IndexOf(rows[pair.Key]) + 1);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var value in Descendants(VisualTreeHelper.GetChild(root, index))) yield return value; }
    private static bool SelectedCaptionReady(ComboBox control)
    {
        var selection = control.Template?.FindName("Selection", control) as ContentPresenter;
        return control.SelectedIndex >= 0 && control.SelectedItem is ComboBoxItem { Content: TextBlock caption } && !string.IsNullOrWhiteSpace(caption.Text) &&
            selection is { ActualWidth: > 0, ActualHeight: > 0 } &&
            control.SelectionBoxItem is System.Windows.Shapes.Rectangle { ActualWidth: > 0, ActualHeight: > 0, Fill: VisualBrush brush } && brush.Viewbox.Width > 0 && brush.Viewbox.Height > 0;
    }
    private static void Save(FrameworkElement body, string path)
    {
        body.Measure(new(300, 700)); body.Arrange(new(0, 0, 300, 700)); body.UpdateLayout();
        // Detached UIElement selection previews need a second LayoutUpdated pass
        // after their source TextBlock has acquired a nonzero render size.
        body.InvalidateArrange(); body.UpdateLayout();
        var bitmap = new RenderTargetBitmap(300, 700, 96, 96, PixelFormats.Pbgra32); bitmap.Render(body);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
}
