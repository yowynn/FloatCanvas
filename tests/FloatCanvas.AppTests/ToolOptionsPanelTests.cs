using System.IO;
using System.Reflection;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;

/// <summary>Tool options update future drawing parameters without a native window or document edit.</summary>
internal static class ToolOptionsPanelTests
{
    public static void Run(Action<string, bool> check)
    {
        var app = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        var windows = app.Windows.Cast<Wpf.Window>().ToArray();
        string preference = L.Preference;
        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-Tests", "tool-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            {
                L.Initialize(language);
                string folder = Path.Combine(root, language); Directory.CreateDirectory(folder);
                Parameters(folder, (name, passed) => check(language + " " + name, passed));
                Presets(folder, (name, passed) => check(language + " " + name, passed));
                ColorsAndTools(folder, (name, passed) => check(language + " " + name, passed));
                Layout(folder, (name, passed) => check(language + " " + name, passed));
                CaptureOptions(folder, (name, passed) => check(language + " " + name, passed));
            }
            check("tool option checks create no native or visible windows", app.Windows.Cast<Wpf.Window>().SequenceEqual(windows) &&
                app.Windows.Cast<Wpf.Window>().All(window => !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0));
        }
        finally { L.Initialize(preference); Directory.Delete(root, recursive: true); }
    }

    private static void Parameters(string root, Action<string, bool> check)
    {
        var controller = new EditorController(new EditorSession());
        var panel = Panel(controller, root, "parameters");
        var numbers = Field<Dictionary<string, Controls.TextBox>>(panel, "numbers");
        var pickers = Field<Dictionary<string, Controls.ComboBox>>(panel, "pickers");
        var status = Field<Controls.TextBlock>(panel, "status");
        controller.Tool = EditorTool.Freehand; panel.Refresh();
        string validWidth = numbers["StrokeWidth"].Text;
        numbers["StrokeWidth"].Text = "-1";
        numbers["StrokeWidth"].Text = validWidth;
        check("correcting an invalid draft back to the effective value clears the error", string.IsNullOrEmpty(status.Text));
        numbers["StrokeWidth"].Text = "-1";
        controller.Tool = EditorTool.Text; panel.Refresh();
        check("changing or reopening a tool clears stale validation feedback", string.IsNullOrEmpty(status.Text));
        int changes = 0; panel.ParametersChanged += () => changes++;
        string document = JsonSerializer.Serialize(controller.Session.Document);

        void Number(EditorTool tool, string key, string value, Func<bool> applied)
        {
            controller.Tool = tool; panel.Refresh(); int before = changes;
            numbers[key].Text = value;
            check(key + " text immediately changes the effective parameter once", applied() && changes == before + 1);
        }
        Number(EditorTool.Line, "StrokeWidth", "7.5", () => controller.Style.StrokeWidth == 7.5);
        Number(EditorTool.Rectangle, "Opacity", "65", () => controller.Style.Opacity == .65);
        Number(EditorTool.Text, "FontSize", "36", () => controller.Style.FontSize == 36);
        Number(EditorTool.Blur, "EffectStrength", "24", () => controller.EffectStrength == 24);
        Number(EditorTool.Rectangle, "CornerRadius", "9", () => controller.CornerRadius == 9);

        foreach (var (key, invalid) in new[] { ("StrokeWidth", "-1"), ("Opacity", "101"), ("FontSize", "0"), ("EffectStrength", "NaN"), ("CornerRadius", "-1") })
        {
            string before = Snapshot(controller); int count = changes; numbers[key].Text = invalid;
            check(key + " invalid draft keeps previous parameters and reports an error", Snapshot(controller) == before && changes == count &&
                numbers[key].Text == invalid && !string.IsNullOrWhiteSpace(status.Text));
            panel.Refresh();
            check(key + " refresh silently restores the effective value", changes == count && numbers[key].Text != invalid);
        }

        controller.Tool = EditorTool.Line; controller.Style.StartArrowHead = controller.Style.EndArrowHead = ArrowHeadStyle.FilledTriangle; panel.Refresh();
        Select(pickers["StartArrowHead"], ArrowHeadStyle.Open);
        Select(pickers["EndArrowHead"], ArrowHeadStyle.None);
        Select(pickers["StrokePattern"], StrokePattern.Dotted);
        check("independent endpoint and line style choices reach the controller", controller.Style.StartArrowHead == ArrowHeadStyle.Open &&
            controller.Style.EndArrowHead == ArrowHeadStyle.None && controller.Style.StrokePattern == StrokePattern.Dotted);
        controller.Tool = EditorTool.Rectangle; panel.Refresh(); int beforeAspect = changes;
        var aspect = Field<Controls.CheckBox>(panel, "aspect"); aspect.IsChecked = true;
        aspect.RaiseEvent(new Wpf.RoutedEventArgs(Controls.CheckBox.ClickEvent));
        check("aspect constraint takes effect immediately", controller.ConstrainAspect && changes == beforeAspect + 1);

        var external = controller.Parameters; external.Style.StrokeWidth = 11; external.Style.Opacity = .42; external.Style.FontSize = 44;
        external.Style.StartArrowHead = ArrowHeadStyle.FilledTriangle; external.Style.EndArrowHead = ArrowHeadStyle.Diamond;
        external.Style.StrokePattern = StrokePattern.Dashed; external.CornerRadius = 17; external.EffectStrength = 31; external.ConstrainAspect = false;
        controller.ApplyParameters(external); int beforeRefresh = changes; string effective = Snapshot(controller);
        panel.Refresh(); panel.Refresh();
        check("external state synchronizes all controls without parameter events", changes == beforeRefresh && Snapshot(controller) == effective &&
            numbers["StrokeWidth"].Text == "11" && numbers["Opacity"].Text == "42" && numbers["FontSize"].Text == "44" &&
            numbers["CornerRadius"].Text == "17" && numbers["EffectStrength"].Text == "31" &&
            Equals(((Controls.ComboBoxItem)pickers["StartArrowHead"].SelectedItem).Tag, ArrowHeadStyle.FilledTriangle) &&
            Equals(((Controls.ComboBoxItem)pickers["EndArrowHead"].SelectedItem).Tag, ArrowHeadStyle.Diamond) &&
            Equals(((Controls.ComboBoxItem)pickers["StrokePattern"].SelectedItem).Tag, StrokePattern.Dashed) &&
            Field<Controls.CheckBox>(panel, "aspect").IsChecked == false);
        check("tool parameter editing leaves document and undo history intact", JsonSerializer.Serialize(controller.Session.Document) == document &&
            controller.Session.UndoCount == 0 && !controller.Session.IsTransactionActive);
        check("numeric options retain native textbox input", numbers.Values.All(System.Windows.Input.InputMethod.GetIsInputMethodEnabled));
    }

    private static void Presets(string root, Action<string, bool> check) => Feedback4OptionsTests.Galleries(root, "panel-presets", check);

    private static void ColorsAndTools(string root, Action<string, bool> check)
    {
        var controller = new EditorController(new EditorSession()) { Tool = EditorTool.Rectangle };
        controller.Style.Stroke = 0x80112233; controller.Style.Fill = 0x40445566;
        var panel = Panel(controller, root, "tools"); var foreground = Field<ColorField>(panel, "foreground"); var background = Field<ColorField>(panel, "background");
        var rows = Field<Dictionary<string, Wpf.FrameworkElement>>(panel, "rows");
        int changes = 0; panel.ParametersChanged += () => changes++;
        check("tool colors use shared fields with explicit foreground and background roles", foreground.Role == ColorRole.Foreground &&
            background.Role == ColorRole.Background && foreground.Value == 0x80112233 && background.Value == 0x40445566);
        foreground.SetUserValue(0x7F778899);
        check("foreground selection immediately changes only stroke", controller.Style.Stroke == 0x7F778899 && controller.Style.Fill == 0x40445566 && changes == 1);
        background.SetUserValue(0x22665544);
        check("background selection immediately changes only fill", controller.Style.Stroke == 0x7F778899 && controller.Style.Fill == 0x22665544 && changes == 2);
        var requests = new List<ColorField>(); panel.EyedropperRequested += requests.Add;
        foreach (var field in new[] { foreground, background })
            Click(Descendants(Field<ColorPickerPanel>(field, "picker")).OfType<Controls.Button>().Single(button => Wpf.Automation.AutomationProperties.GetName(button) == L.Get("Color.Eyedropper")));
        check("eyedropper forwarding retains the requesting color role", requests.SequenceEqual(new[] { foreground, background }) && changes == 2);
        controller.Style.Stroke = 0x55123456; controller.Style.Fill = 0x12345678; panel.Refresh();
        check("external color refresh retains exact alpha and stays silent", foreground.Value == 0x55123456 && background.Value == 0x12345678 && changes == 2);

        foreach (var tool in new[] { EditorTool.Line, EditorTool.Arrow, EditorTool.DoubleArrow })
        {
            controller.Tool = tool; panel.Refresh();
            check(tool + " exposes foreground line pattern and both endpoints", Shown(rows, "Foreground", "StrokePattern", "StartArrowHead", "EndArrowHead") &&
                foreground.Visibility == Wpf.Visibility.Visible && !Shown(rows, "Background") && background.Visibility == Wpf.Visibility.Collapsed);
        }
        foreach (var tool in new[] { EditorTool.Rectangle, EditorTool.RoundedRectangle, EditorTool.Ellipse, EditorTool.Triangle, EditorTool.Symbol })
        {
            controller.Tool = tool; panel.Refresh();
            check(tool + " exposes shape colors and pattern without line endpoints", Shown(rows, "Foreground", "Background", "StrokePattern") &&
                foreground.Visibility == Wpf.Visibility.Visible && background.Visibility == Wpf.Visibility.Visible &&
                !Shown(rows, "StartArrowHead") && !Shown(rows, "EndArrowHead"));
        }
        controller.Tool = EditorTool.Number; panel.Refresh();
        check("number exposes foreground background and text size", Shown(rows, "Foreground", "Background", "FontSize"));
        controller.Tool = EditorTool.Text; panel.Refresh();
        check("text exposes font controls without fill or endpoints", Shown(rows, "Foreground", "FontSize", "FontFamily") &&
            !Shown(rows, "Background") && !Shown(rows, "StartArrowHead") && !Shown(rows, "EndArrowHead"));
        foreach (var tool in new[] { EditorTool.Blur, EditorTool.Pixelate })
        {
            controller.Tool = tool; panel.Refresh();
            check(tool + " exposes effect strength without drawing colors", Shown(rows, "EffectStrength") && !Shown(rows, "Foreground") && !Shown(rows, "Background"));
        }
        controller.Tool = EditorTool.Select; panel.Refresh();
        check("selection hides drawing color and endpoint options", !Shown(rows, "Foreground") && !Shown(rows, "Background") &&
            !Shown(rows, "StartArrowHead") && !Shown(rows, "EndArrowHead"));

        controller.Tool = EditorTool.Symbol; panel.Refresh();
        var symbols = Field<System.Windows.Controls.Primitives.UniformGrid>(panel, "symbolGallery").Children.OfType<Controls.Button>().ToArray();
        check("symbol gallery contains all nine editable geometric symbols", Shown(rows, "Symbols") && symbols.Length == 9 &&
            symbols.Select(button => button.Tag).OfType<SymbolKind>().Order().SequenceEqual(Enum.GetValues<SymbolKind>().Order()));
        int before = changes; Click(symbols.Single(button => Equals(button.Tag, SymbolKind.SpeechBubble)));
        check("symbol gallery choice changes the active symbol once", controller.Symbol == SymbolKind.SpeechBubble && changes == before + 1);
        controller.Tool = EditorTool.Emoji; panel.Refresh();
        var emoji = Field<System.Windows.Controls.Primitives.UniformGrid>(panel, "emojiGallery").Children.OfType<Controls.Button>().ToArray();
        check("emoji gallery provides a distinct ready-to-use collection", Shown(rows, "Emoji") && !Shown(rows, "Symbols") &&
            !Shown(rows, "Foreground") && !Shown(rows, "Background") && emoji.Length is >= 24 and <= 40 &&
            emoji.All(button => button.Tag is string text && !string.IsNullOrWhiteSpace(text)) && emoji.Select(button => button.Tag).Distinct().Count() == emoji.Length);
        var choice = emoji.First(button => !Equals(button.Tag, controller.Emoji)); before = changes; Click(choice);
        check("emoji choice changes the active glyph once", controller.Emoji == (string)choice.Tag && changes == before + 1);
    }

    private static void CaptureOptions(string root, Action<string, bool> check)
    {
        var controller = new EditorController(new EditorSession()) { Tool = EditorTool.CaptureRegion };
        var panel = Panel(controller, root, "snapshot");
        var rows = Field<Dictionary<string, Wpf.FrameworkElement>>(panel, "rows");
        var pickers = Field<Dictionary<string, Controls.ComboBox>>(panel, "pickers");
        var numbers = Field<Dictionary<string, Controls.TextBox>>(panel, "numbers");
        var foreground = Field<ColorField>(panel, "foreground");
        check("snapshot exposes source shape and optional border together", Shown(rows, "CaptureShape", "CaptureSource", "CaptureBorderWidth", "Foreground") &&
            !Shown(rows, "Background") && !Shown(rows, "StrokeWidth") && !Shown(rows, "Opacity"));
        check("snapshot choices contain all four shapes and all three sources", pickers["CaptureShape"].Items.Count == 4 && pickers["CaptureSource"].Items.Count == 3);
        pickers["CaptureShape"].SelectedItem = pickers["CaptureShape"].Items.OfType<Controls.ComboBoxItem>().Single(item => Equals(item.Tag, CaptureShape.PolygonLasso));
        pickers["CaptureSource"].SelectedItem = pickers["CaptureSource"].Items.OfType<Controls.ComboBoxItem>().Single(item => Equals(item.Tag, CaptureSource.CanvasOnly));
        numbers["CaptureBorderWidth"].Text = "6";
        foreground.SetUserValue(0xFFBB2233);
        check("snapshot controls update only capture parameters without editing drawing style", controller.CaptureShape == CaptureShape.PolygonLasso &&
            controller.CaptureSource == CaptureSource.CanvasOnly && controller.CaptureBorderWidth == 6 && controller.CaptureBorderColor == 0xFFBB2233 && controller.Style.Stroke == ColorLibrary.ForegroundRed);
        var gallery = Field<ToolPresetGallery>(panel, "presets");
        gallery.SaveNamed("Snapshot test");
        var parameters = controller.Parameters;
        numbers["CaptureBorderWidth"].Text = "0";
        check("zero snapshot border width switches off without changing the retained border color", controller.CaptureBorderWidth == 0 && controller.CaptureBorderColor == 0xFFBB2233);
        gallery.Apply(gallery.SelectedId);
        check("snapshot user preset restores shape source and border", JsonSerializer.Serialize(controller.Parameters) == JsonSerializer.Serialize(parameters));
        panel.Measure(new Wpf.Size(ToolOptionsPanel.PreferredWidth, double.PositiveInfinity)); panel.Arrange(new Wpf.Rect(panel.DesiredSize)); panel.UpdateLayout();
        check("snapshot parameters fit the normal bounded panel without folding", panel.ActualHeight <= 620 && rows.Values.Where(row => row.Visibility == Wpf.Visibility.Visible).All(row => row.ActualWidth > 0));
        check("changing snapshot controls creates no document edits or undo entries", controller.Session.Document.Elements.Count == 0 && controller.Session.UndoCount == 0);
    }

    private static void Layout(string root, Action<string, bool> check)
    {
        var controller = new EditorController(new EditorSession()) { Tool = EditorTool.Line };
        var panel = Panel(controller, root, "layout");
        var rows = Field<Dictionary<string, Wpf.FrameworkElement>>(panel, "rows");
        var official = Field<ToolPresetGallery>(panel, "presets").OfficialTiles;
        foreach (var tool in new[] { EditorTool.Line, EditorTool.Rectangle, EditorTool.Text, EditorTool.Number, EditorTool.Symbol, EditorTool.Emoji, EditorTool.Blur })
        {
            controller.Tool = tool; panel.Refresh();
            panel.Measure(new Wpf.Size(ToolOptionsPanel.PreferredWidth, double.PositiveInfinity)); panel.Arrange(new Wpf.Rect(panel.DesiredSize)); panel.UpdateLayout();
            check(tool + " options use a wide bounded panel without unnecessary folding", panel.ActualWidth == ToolOptionsPanel.PreferredWidth && panel.ActualHeight <= 620 &&
                rows.Values.Where(row => row.Visibility == Wpf.Visibility.Visible).All(row => row.ActualWidth > 0));
        }
        controller.Tool = EditorTool.Line; panel.Refresh();
        panel.Measure(new Wpf.Size(ToolOptionsPanel.PreferredWidth, double.PositiveInfinity)); panel.Arrange(new Wpf.Rect(panel.DesiredSize)); panel.UpdateLayout();
        foreach (var pair in new[] { ("StartArrowHead", "EndArrowHead"), ("StrokePattern", "ArrowHeadSize") })
            check(pair.Item1 + " and " + pair.Item2 + " share a row", Controls.Grid.GetRow(rows[pair.Item1]) == Controls.Grid.GetRow(rows[pair.Item2]) &&
                Controls.Grid.GetColumn(rows[pair.Item1]) == 0 && Controls.Grid.GetColumn(rows[pair.Item2]) == 1);
        var recipes = official.Children.OfType<Controls.Button>().Where(button => button.Visibility == Wpf.Visibility.Visible).ToArray();
        check("line recipes show only relevant choices in multiple columns", recipes.Length >= 4 &&
            recipes.All(button => ((string)button.Tag).Contains("line") || ((string)button.Tag).Contains("arrow")) &&
            recipes.Select(button => Math.Round(button.TranslatePoint(new Wpf.Point(), official).X)).Distinct().Count() >= 2);
        var below = DesktopSession.OptionsPlacements(new Wpf.Size(520, 380), new Wpf.Size(800, 100), 200, new Wpf.Rect(-100, -80, 1400, 900));
        check("options clear the complete mascot-height toolbar by eight DIP", below[0].Point.Y == 108 && below[0].Point.X == -60);
        var above = DesktopSession.OptionsPlacements(new Wpf.Size(520, 380), new Wpf.Size(800, 100), 770, new Wpf.Rect(-100, -600, 1000, 780));
        check("options prefer above near the work area bottom and clamp horizontally", above[0].Point.Y == -388 && above[0].Point.X == 380);
        var left = DesktopSession.OptionsPlacements(new Wpf.Size(520, 380), new Wpf.Size(800, 100), 10, new Wpf.Rect(0, -80, 1200, 900));
        check("options retain the gap while clamping at the left work area edge", left[0].Point.X == 0 && left[0].Point.Y == 108);
    }

    private static ToolOptionsPanel Panel(EditorController controller, string root, string name) => new(controller,
        new ToolPresetLibrary(Path.Combine(root, name + "-presets.json")), new ColorLibrary(Path.Combine(root, name + "-colors.json")));
    private static string Snapshot(EditorController controller) => JsonSerializer.Serialize(controller.Parameters);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Invoke(object target, string method, params object[] arguments) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static void Click(Controls.Button button) => button.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
    private static void Select(Controls.ComboBox picker, object value) => picker.SelectedItem = picker.Items.OfType<Controls.ComboBoxItem>().Single(item => Equals(item.Tag, value));
    private static bool Shown(Dictionary<string, Wpf.FrameworkElement> rows, params string[] keys) => keys.All(key => rows[key].Visibility == Wpf.Visibility.Visible);
    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject root)
    {
        yield return root;
        foreach (var child in Wpf.LogicalTreeHelper.GetChildren(root).OfType<Wpf.DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
