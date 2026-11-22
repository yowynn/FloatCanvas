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
        var pickers = Field<Dictionary<string, SegmentedChoice>>(panel, "pickers");
        var status = Field<Controls.TextBlock>(panel, "status");
        controller.Tool = EditorTool.Freehand; panel.PrepareForShow();
        string validWidth = numbers["StrokeWidth"].Text;
        numbers["StrokeWidth"].Text = "-1";
        numbers["StrokeWidth"].Text = validWidth;
        check("correcting an invalid draft back to the effective value clears the error", string.IsNullOrEmpty(status.Text));
        numbers["StrokeWidth"].Text = "-1";
        controller.Tool = EditorTool.Text; panel.PrepareForShow();
        check("changing or reopening a tool clears stale validation feedback", string.IsNullOrEmpty(status.Text));
        int changes = 0; panel.ParametersChanged += () => changes++;
        string document = JsonSerializer.Serialize(controller.Session.Document);

        void Number(EditorTool tool, string key, string value, Func<bool> applied)
        {
            controller.Tool = tool; panel.PrepareForShow(); int before = changes;
            numbers[key].Text = value;
            check(key + " text immediately changes the effective parameter once", applied() && changes == before + 1);
        }
        Number(EditorTool.Line, "StrokeWidth", "7.5", () => controller.Style.StrokeWidth == 7.5);
        controller.Tool = EditorTool.Image; panel.PrepareForShow();
        check("image presets do not expose layer opacity", Field<Dictionary<string, System.Windows.FrameworkElement>>(panel, "rows")["Opacity"].Visibility == System.Windows.Visibility.Collapsed);
        Number(EditorTool.Text, "FontSize", "36", () => controller.Style.FontSize == 36);
        Number(EditorTool.Blur, "EffectStrength", "24", () => controller.EffectStrength == 24);
        Number(EditorTool.Rectangle, "CornerRadius", "9", () => controller.CornerRadius == 9);

        controller.Tool = EditorTool.Highlighter; panel.PrepareForShow();
        var tipRows = Field<Dictionary<string, Wpf.FrameworkElement>>(panel, "rows");
        var colorField = Field<ColorField>(panel, "foreground");
        var colorCaption = Field<Dictionary<string, Controls.TextBlock>>(panel, "captions")["Foreground"];
        var colorPopup = (WindowPopup)Field<PopupHandle>(colorField, "popupDisplay").Key;
        check("highlighter color uses the stroke label on the row, picker and swatch",
            colorCaption.Text == L.Get("Color.Stroke") && colorPopup.Title == colorCaption.Text &&
            Wpf.Automation.AutomationProperties.GetName(colorField).StartsWith(colorCaption.Text + " ", StringComparison.Ordinal));
        check("highlighter starts with the round tip and hides the angle",
            Equals(pickers["HighlighterTip"].SelectedValue, HighlighterTip.Round) && tipRows["HighlighterTipAngle"].Visibility == Wpf.Visibility.Collapsed);
        Select(pickers["HighlighterTip"], HighlighterTip.Flat);
        check("flat tip immediately shows its vertical default", controller.Style.HighlighterTip == HighlighterTip.Flat &&
            tipRows["HighlighterTipAngle"].Visibility == Wpf.Visibility.Visible && numbers["HighlighterTipAngle"].Text == "0");
        Number(EditorTool.Highlighter, "HighlighterTipAngle", "35", () => controller.Style.HighlighterTipAngle == 35);
        Number(EditorTool.Highlighter, "HighlighterInkContrast", "55", () => controller.Style.HighlighterInkContrast == .55);
        Select(pickers["HighlighterTip"], HighlighterTip.Round);
        Select(pickers["HighlighterTip"], HighlighterTip.Flat);
        check("a tip toggle remembers the chosen angle",
            tipRows["HighlighterTipAngle"].Visibility == Wpf.Visibility.Visible && numbers["HighlighterTipAngle"].Text == "35");
        string tipPresetPath = Path.Combine(root, "flat-tip-preset.json");
        Select(pickers["StrokePattern"], StrokePattern.DashDot);
        var tipPreset = new ToolPresetLibrary(tipPresetPath).Save("Flat tip", controller.Parameters);
        var restored = new ToolPresetLibrary(tipPresetPath).Apply(tipPreset.Id);
        check("a saved highlighter preset restores the chosen tip, angle and dash-dot pattern",
            restored.Style is { HighlighterTip: HighlighterTip.Flat, HighlighterTipAngle: 35, HighlighterInkContrast: .55, StrokePattern: StrokePattern.DashDot });
        controller.Tool = EditorTool.Text; panel.PrepareForShow();
        check("switching to text renames the reused color field and picker",
            colorCaption.Text == L.Get("Color.TextInk") && colorPopup.Title == colorCaption.Text);
        Select(pickers["StyleVariant"], ElementAppearance.TextLabel); panel.PrepareForShow();
        check("the label theme updates the same field and picker caption", colorCaption.Text == L.Get("Color.LabelTheme") &&
            colorPopup.Title == colorCaption.Text && controller.Tool == EditorTool.Text);
        controller.Tool = EditorTool.Line; panel.PrepareForShow();
        Select(pickers["LinePathMode"], LinePathMode.Arc);
        var arcPreset = new ToolPresetLibrary(tipPresetPath).Save("Arc", controller.Parameters);
        check("the line path selector enables the default bend and the preset restores it",
            controller.Style.LineArcBulge == ElementGeometry.DefaultLineArcBulge &&
            new ToolPresetLibrary(tipPresetPath).Apply(arcPreset.Id).Style.LineArcBulge == ElementGeometry.DefaultLineArcBulge);
        Select(pickers["LinePathMode"], LinePathMode.Straight);
        check("the line path selector can return to straight drawing", controller.Style.LineArcBulge is null);

        foreach (var (tool, key, invalid) in new[] { (EditorTool.Line, "StrokeWidth", "-1"),
            (EditorTool.Text, "FontSize", "0"), (EditorTool.Blur, "EffectStrength", "NaN"), (EditorTool.Rectangle, "CornerRadius", "-1") })
        {
            controller.Tool = tool; panel.PrepareForShow();
            string before = Snapshot(controller); int count = changes; numbers[key].Text = invalid;
            check(key + " invalid draft keeps previous parameters and reports an error", Snapshot(controller) == before && changes == count &&
                numbers[key].Text == invalid && !string.IsNullOrWhiteSpace(status.Text));
            panel.PrepareForShow();
            check(key + " refresh silently restores the effective value", changes == count && numbers[key].Text != invalid);
        }

        controller.Tool = EditorTool.Line; controller.Style.StartArrowHead = controller.Style.EndArrowHead = ArrowHeadStyle.FilledTriangle; panel.PrepareForShow();
        Select(pickers["StartArrowHead"], ArrowHeadStyle.Open);
        Select(pickers["EndArrowHead"], ArrowHeadStyle.None);
        Select(pickers["StrokePattern"], StrokePattern.Dotted);
        check("independent endpoint and line style choices reach the controller", controller.Style.StartArrowHead == ArrowHeadStyle.Open &&
            controller.Style.EndArrowHead == ArrowHeadStyle.None && controller.Style.StrokePattern == StrokePattern.Dotted);
        controller.Tool = EditorTool.Rectangle; panel.PrepareForShow(); int beforeAspect = changes;
        var aspect = Field<System.Windows.Controls.Primitives.ToggleButton>(panel, "aspect"); aspect.IsChecked = true;
        aspect.RaiseEvent(new Wpf.RoutedEventArgs(Controls.CheckBox.ClickEvent));
        check("aspect constraint takes effect immediately", controller.ConstrainAspect && changes == beforeAspect + 1);

        var external = controller.Parameters; external.Style.StrokeWidth = 11; external.Style.Opacity = .42; external.Style.FontSize = 44;
        external.Style.StartArrowHead = ArrowHeadStyle.FilledTriangle; external.Style.EndArrowHead = ArrowHeadStyle.Diamond;
        external.Style.StrokePattern = StrokePattern.Dashed; external.CornerRadius = 17; external.EffectStrength = 31; external.ConstrainAspect = false;
        External(EditorTool.Line, () => numbers["StrokeWidth"].Text == "11" &&
            Equals(pickers["StartArrowHead"].SelectedValue, ArrowHeadStyle.FilledTriangle) &&
            Equals(pickers["EndArrowHead"].SelectedValue, ArrowHeadStyle.Diamond) &&
            Equals(pickers["StrokePattern"].SelectedValue, StrokePattern.Dashed));
        External(EditorTool.Image, () => controller.Style.Opacity == 1);
        External(EditorTool.Text, () => numbers["FontSize"].Text == "44" &&
            ToolOptionPreviews.SelectedFontName(Field<Controls.ComboBox>(panel, "fontFamily")) == external.Style.FontFamily);
        External(EditorTool.Rectangle, () => numbers["StrokeWidth"].Text == "11" && numbers["CornerRadius"].Text == "17" &&
            Field<System.Windows.Controls.Primitives.ToggleButton>(panel, "aspect").IsChecked == false);
        External(EditorTool.Blur, () => numbers["EffectStrength"].Text == "31");

        void External(EditorTool tool, Func<bool> synchronized)
        {
            external.Tool = tool; controller.ApplyParameters(external);
            int beforeRefresh = changes; string effective = Snapshot(controller);
            panel.PrepareForShow(); panel.PrepareForShow();
            check(tool + " external state synchronizes its visible controls without parameter events", changes == beforeRefresh &&
                Snapshot(controller) == effective && synchronized());
        }
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
        controller.Style.Stroke = 0x55123456; controller.Style.Fill = 0x12345678; panel.PrepareForShow();
        check("external color refresh retains exact alpha and stays silent", foreground.Value == 0x55123456 && background.Value == 0x12345678 && changes == 2);

        foreach (var tool in new[] { EditorTool.Line, EditorTool.Arrow, EditorTool.DoubleArrow })
        {
            controller.Tool = tool; panel.PrepareForShow();
            check(tool + " exposes foreground line pattern and both endpoints", Shown(rows, "Foreground", "StrokePattern", "StartArrowHead", "EndArrowHead") &&
                foreground.Visibility == Wpf.Visibility.Visible && !Shown(rows, "Background") && background.Visibility == Wpf.Visibility.Collapsed);
        }
        foreach (var tool in new[] { EditorTool.Rectangle, EditorTool.RoundedRectangle, EditorTool.Ellipse, EditorTool.Triangle, EditorTool.Symbol })
        {
            controller.Tool = tool; panel.PrepareForShow();
            check(tool + " exposes shape colors and pattern without line endpoints", Shown(rows, "Foreground", "Background", "StrokePattern") &&
                foreground.Visibility == Wpf.Visibility.Visible && background.Visibility == Wpf.Visibility.Visible &&
                !Shown(rows, "StartArrowHead") && !Shown(rows, "EndArrowHead"));
        }
        controller.Tool = EditorTool.Number; panel.PrepareForShow();
        check("number exposes foreground background and text size", Shown(rows, "Foreground", "Background", "FontSize"));
        controller.Tool = EditorTool.Text; panel.PrepareForShow();
        check("text exposes font controls without fill or endpoints", Shown(rows, "Foreground", "FontSize", "FontFamily") &&
            !Shown(rows, "Background") && !Shown(rows, "StartArrowHead") && !Shown(rows, "EndArrowHead"));
        foreach (var tool in new[] { EditorTool.Blur, EditorTool.Pixelate })
        {
            controller.Tool = tool; panel.PrepareForShow();
            check(tool + " exposes effect strength without drawing colors", Shown(rows, "EffectStrength") && !Shown(rows, "Foreground") && !Shown(rows, "Background"));
        }
        controller.Tool = EditorTool.Select; panel.PrepareForShow();
        check("selection hides drawing color and endpoint options", !Shown(rows, "Foreground") && !Shown(rows, "Background") &&
            !Shown(rows, "StartArrowHead") && !Shown(rows, "EndArrowHead"));

        controller.Tool = EditorTool.Symbol; panel.PrepareForShow();
        var symbols = Field<System.Windows.Controls.Primitives.UniformGrid>(panel, "symbolGallery").Children.OfType<Controls.Button>().ToArray();
        check("symbol gallery contains all eighteen built-in symbols", Shown(rows, "Symbols") && symbols.Length == 18 &&
            symbols.Select(button => button.Tag).OfType<SymbolKind>().Order().SequenceEqual(SymbolGeometry.BuiltIns.Order()));
        int before = changes; Click(symbols.Single(button => Equals(button.Tag, SymbolKind.SpeechBubble)));
        check("symbol gallery choice changes the active symbol once", controller.Symbol == SymbolKind.SpeechBubble && changes == before + 1);
        controller.Tool = EditorTool.Emoji; panel.PrepareForShow();
        var emoji = Field<EmojiField>(panel,"emojiField");
        var library = Field<EmojiPickerPanel>(panel,"emojiLibrary");
        var emojiFont = Field<EmojiFontPicker>(panel,"emojiFont");
        var inlineGrid=Field<Controls.ListBox>(library,"grid");
        check("emoji tool exposes one compact field and style selector without text font or color controls", Shown(rows,"Emoji","EmojiFont") && !Shown(rows,"Symbols") &&
            !Shown(rows,"Foreground") && !Shown(rows,"Background") && !Shown(rows,"FontFamily") &&
            emojiFont.Items.Count==4 && emoji.Value==controller.Emoji);
        var emojiFontLabels=emojiFont.Items.Cast<Controls.ComboBoxItem>().Select(item=>item.Content.ToString()).ToArray();
        check("emoji style labels use the same four product names in both languages",
            emojiFontLabels.SequenceEqual(new[]{"Windows","Google","OpenMoji","Blobmoji"}) ||
            emojiFontLabels.Zip(new[]{"Windows","Google","OpenMoji","Blobmoji"}).All(pair=>pair.First!.StartsWith(pair.Second,StringComparison.Ordinal)));
        before=changes;emojiFont.SetValue(EmojiFontIds.BlobmojiColor);
        check("emoji art style changes the active tool font preference once",controller.Style.EmojiFontId==EmojiFontIds.BlobmojiColor && changes==before+1);
        check("emoji tool embeds search categories and a full grid beside the current value",Shown(rows,"Emoji","EmojiLibrary") &&
            Field<Controls.Button>(emoji,"choose").Visibility==Wpf.Visibility.Collapsed);
        Field<List<Controls.Button>>(library,"categories")[1].RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
        check("embedded smiley category fills the virtualized grid",inlineGrid.Items.Count>100);
        var rendered=inlineGrid.ItemsSource;
        var chosen=(EmojiEntry)inlineGrid.Items.Cast<object>().First(entry=>((EmojiEntry)entry).Glyph!=controller.Emoji);
        typeof(EmojiPickerPanel).GetMethod("Choose",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(library,[chosen]);
        check("choosing an inline emoji preserves the rendered list and scroll surface",ReferenceEquals(rendered,inlineGrid.ItemsSource) && controller.Emoji==chosen.Glyph);
        before=changes;Field<Controls.TextBox>(emoji,"input").Text="👩🏽‍🎨";
        typeof(EmojiField).GetMethod("CommitInput",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(emoji,null);
        check("emoji field changes the active glyph once",controller.Emoji=="👩🏽‍🎨" && changes==before+1);
    }

    private static void CaptureOptions(string root, Action<string, bool> check)
    {
        var controller = new EditorController(new EditorSession()) { Tool = EditorTool.CaptureRegion };
        var panel = Panel(controller, root, "snapshot");
        var rows = Field<Dictionary<string, Wpf.FrameworkElement>>(panel, "rows");
        var pickers = Field<Dictionary<string, SegmentedChoice>>(panel, "pickers");
        var numbers = Field<Dictionary<string, Controls.TextBox>>(panel, "numbers");
        var foreground = Field<ColorField>(panel, "foreground");
        check("snapshot exposes source shape and optional border together", Shown(rows, "CaptureShape", "CaptureSource", "CaptureBorderWidth", "Foreground") &&
            !Shown(rows, "Background") && !Shown(rows, "StrokeWidth") && !Shown(rows, "Opacity"));
        check("snapshot choices contain all four shapes and all three sources", pickers["CaptureShape"].Items.Count == 4 && pickers["CaptureSource"].Items.Count == 3);
        pickers["CaptureShape"].SelectedItem = pickers["CaptureShape"].Items.OfType<Controls.RadioButton>().Single(item => Equals(item.Tag, CaptureShape.PolygonLasso));
        pickers["CaptureSource"].SelectedItem = pickers["CaptureSource"].Items.OfType<Controls.RadioButton>().Single(item => Equals(item.Tag, CaptureSource.CanvasOnly));
        numbers["CaptureBorderWidth"].Text = "6";
        foreground.SetUserValue(0xFFBB2233);
        check("snapshot controls update capture parameters and the primary border color", controller.CaptureShape == CaptureShape.PolygonLasso &&
            controller.CaptureSource == CaptureSource.CanvasOnly && controller.CaptureBorderWidth == 6 && controller.Style.PrimaryColor == 0xFFBB2233);
        var gallery = Field<ToolPresetGallery>(panel, "presets");
        gallery.SaveNamed("Snapshot test");
        var parameters = controller.Parameters;
        numbers["CaptureBorderWidth"].Text = "0";
        check("zero snapshot border width switches off without changing the retained border color", controller.CaptureBorderWidth == 0 && controller.Style.PrimaryColor == 0xFFBB2233);
        gallery.Apply(gallery.SelectedId);
        check("snapshot user preset restores shape source and border", JsonSerializer.Serialize(controller.Parameters) == JsonSerializer.Serialize(parameters));
        panel.Measure(new Wpf.Size(ToolOptionsPanel.PreferredWidth, double.PositiveInfinity)); panel.Arrange(new Wpf.Rect(new Wpf.Size(ToolOptionsPanel.PreferredWidth, panel.DesiredSize.Height))); panel.UpdateLayout();
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
            controller.Tool = tool; panel.PrepareForShow();
            panel.Measure(new Wpf.Size(ToolOptionsPanel.PreferredWidth, double.PositiveInfinity)); panel.Arrange(new Wpf.Rect(new Wpf.Size(ToolOptionsPanel.PreferredWidth, panel.DesiredSize.Height))); panel.UpdateLayout();
            check(tool + " options use a wide bounded panel without unnecessary folding", panel.ActualWidth == ToolOptionsPanel.PreferredWidth && panel.ActualHeight <= 620 &&
                rows.Values.Where(row => row.Visibility == Wpf.Visibility.Visible).All(row => row.ActualWidth > 0));
        }
        controller.Tool = EditorTool.Line; panel.PrepareForShow();
        panel.Measure(new Wpf.Size(ToolOptionsPanel.PreferredWidth, double.PositiveInfinity)); panel.Arrange(new Wpf.Rect(new Wpf.Size(ToolOptionsPanel.PreferredWidth, panel.DesiredSize.Height))); panel.UpdateLayout();
        foreach (var pair in new[] { ("StartArrowHead", "EndArrowHead"), ("StrokePattern", "ArrowHeadSize") })
            check(pair.Item1 + " and " + pair.Item2 + " retain full-width readable controls", Controls.Grid.GetRow(rows[pair.Item1]) != Controls.Grid.GetRow(rows[pair.Item2]) &&
                Controls.Grid.GetColumnSpan(rows[pair.Item1]) == 3 && Controls.Grid.GetColumnSpan(rows[pair.Item2]) == 3);
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

        var parameterGrid = Field<Controls.Grid>(panel, "parameterGrid");
        foreach (var tool in new[] { EditorTool.Rectangle, EditorTool.RoundedRectangle, EditorTool.Ellipse, EditorTool.Triangle, EditorTool.Symbol })
        {
            controller.Tool = tool;
            panel.PrepareForShow();
            if (controller.Tool == EditorTool.Symbol)
            {
                check(tool + " has one size directly before the shape picker", Shown(rows,"LinkColors","Colors","SymbolSize","Symbols") &&
                    rows["Size"].Visibility == Wpf.Visibility.Collapsed && Controls.Grid.GetRow(rows["SymbolSize"]) + 1 == Controls.Grid.GetRow(rows["Symbols"]));
                continue;
            }
            var size = rows["Size"];
            int sizeRow = Controls.Grid.GetRow(size);
            check(tool + " shows color style and colors first, and default dimensions last",
                Shown(rows, "LinkColors", "Colors", "Size") && Controls.Grid.GetRow(rows["LinkColors"]) == 0 &&
                Controls.Grid.GetRow(rows["Colors"]) == 1 &&
                parameterGrid.Children.OfType<Wpf.FrameworkElement>().Where(row => row != size && row.Visibility == Wpf.Visibility.Visible)
                    .All(row => Controls.Grid.GetRow(row) < sizeRow));
        }

        controller.Tool = EditorTool.Highlighter;
        var fields = Field<Dictionary<string, NumericValueField>>(panel, "numericFields");
        var tipChoice = Field<Dictionary<string, SegmentedChoice>>(panel, "pickers")["HighlighterTip"];
        var foreground = Field<ColorField>(panel, "foreground");
        foreach (double width in new[] { ToolOptionsPanel.PreferredWidth, panel.MinWidth })
        foreach (var tip in new[] { HighlighterTip.Round, HighlighterTip.Flat })
        {
            controller.Style.HighlighterTip = tip;
            panel.PrepareForShow();
            panel.Measure(new Wpf.Size(width, double.PositiveInfinity));
            panel.Arrange(new Wpf.Rect(0, 0, width, panel.DesiredSize.Height)); panel.UpdateLayout();
            var contrast = Bounds(fields["HighlighterInkContrast"]);
            var lineWidth = Bounds(fields["StrokeWidth"]);
            var preceding = tip == HighlighterTip.Round ? Bounds(tipChoice) : Bounds(fields["HighlighterTipAngle"]);
            check($"{tip} at {width} DIP gives ink contrast its own full-width numeric row " +
                $"(previous bottom {preceding.Bottom:0.#}, contrast {contrast}, foreground top {Bounds(foreground).Top:0.#})",
                Math.Abs(contrast.Top - preceding.Bottom - PropertyPanelLayout.RowGap) < 1 &&
                Math.Abs(Bounds(foreground).Top - contrast.Bottom - PropertyPanelLayout.RowGap) < 1 &&
                contrast.Height == PropertyPanelLayout.ControlHeight && Math.Abs(contrast.Left - lineWidth.Left) < 1 &&
                Math.Abs(contrast.Right - lineWidth.Right) < 1);
        }

        Wpf.Rect Bounds(Wpf.FrameworkElement element) => new(element.TranslatePoint(new(), panel), element.RenderSize);
    }

    private static ToolOptionsPanel Panel(EditorController controller, string root, string name)
    {
        var panel=new ToolOptionsPanel(controller,new ToolPresetLibrary(Path.Combine(root,name+"-presets.json")),new ColorLibrary(Path.Combine(root,name+"-colors.json")));
        // Detached interaction tests explicitly synchronize the form as its visible host would.
        panel.ParametersChanged+=panel.PrepareForShow;panel.PrepareForShow();return panel;
    }
    private static string Snapshot(EditorController controller) => JsonSerializer.Serialize(controller.Parameters);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Invoke(object target, string method, params object[] arguments) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static void Click(Controls.Button button) => button.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
    private static void Select(SegmentedChoice picker, object value) => picker.SelectedItem = picker.Items.OfType<Controls.RadioButton>().Single(item => Equals(item.Tag, value));
    private static bool Shown(Dictionary<string, Wpf.FrameworkElement> rows, params string[] keys) => keys.All(key => rows[key].Visibility == Wpf.Visibility.Visible);
    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject root)
    {
        yield return root;
        foreach (var child in Wpf.LogicalTreeHelper.GetChildren(root).OfType<Wpf.DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
