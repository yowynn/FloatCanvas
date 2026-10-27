using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class SegmentedChoiceTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        output ??= Path.Combine(ApplicationPaths.ProfileDirectory, "segmented-choice-renders");
        Directory.CreateDirectory(output);
        string language = L.Preference;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(locale); AppTheme.Apply(theme);
                string prefix = locale + " " + theme;
                Behavior(check, prefix);
                foreach (double width in new[] { 464d, 252 }) Layout(check, prefix, output, width);
            }
        }
        finally { L.Initialize(language); }
    }

    private static void Behavior(Action<string, bool> check, string name)
    {
        var choice = SegmentedChoicePreviews.CanvasMode(); choice.SelectedValue = false;
        int changed = 0; choice.SelectionChanged += (_, _) => changed++;
        Click(choice.Items[1]);
        check(name + " native radio click changes exactly one value once", choice.SelectedValue is true && choice.Items.Count(item => item.IsChecked == true) == 1 && changed == 1);
        Click(choice.Items[1]);
        check(name + " reselecting the active option cannot uncheck it or repeat an edit", choice.SelectedValue is true && changed == 1);
        choice.SelectedIndex = -1;
        check(name + " mixed values leave every segment unselected", choice.Items.All(item => item.IsChecked != true));
        Click(choice.Items[0]); choice.SelectedValue = false;
        check(name + " choosing a mixed value commits once and model echoes stay silent", choice.SelectedValue is false && changed == 3);
        var other = SegmentedChoicePreviews.CanvasMode(); other.SelectedValue = true;
        check(name + " separate groups do not uncheck each other", choice.SelectedValue is false && choice.Items[0].IsChecked == true && other.SelectedValue is true);
        var peer = UIElementAutomationPeer.CreatePeerForElement(choice)!;
        check(name + " the group exposes a single-selection automation provider", peer.GetPattern(PatternInterface.Selection) is ISelectionProvider { CanSelectMultiple: false });
        check(name + " options expose named native radio peers", choice.Items.All(item => UIElementAutomationPeer.CreatePeerForElement(item) is { } option && option.GetAutomationControlType() == AutomationControlType.RadioButton && option.GetName() == (string)item.ToolTip));
        check(name + " arrows remain local while project shortcuts still route", !EditorShortcutPolicy.ShouldRoute(0x27, ModifierKeys.None, choice.Items[0]) && EditorShortcutPolicy.ShouldRoute(0x53, ModifierKeys.Control, choice.Items[0]));
        using (var source = new HwndSource(new HwndSourceParameters("SegmentedChoiceKeys") { Width = 1, Height = 1, PositionX = -10000, PositionY = -10000, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080 }))
        using (var modifiers = new ThreadModifierScope(ModifierKeys.None))
        {
            var right = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            typeof(SegmentedChoice).GetMethod("OnPreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(choice, [right]);
            check(name + " native-group keyboard navigation selects one adjacent option", right.Handled && choice.SelectedIndex == 1);
            choice.SelectedIndex = -1;
            var left = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Left) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            typeof(SegmentedChoice).GetMethod("OnPreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(choice, [left]);
            check(name + " navigating a mixed value starts at the matching group edge", left.Handled && choice.SelectedIndex == 1);
        }
        check(name + " settings choices preserve their existing storage indexes", SegmentedChoicePreviews.Appearance("dark").SelectedIndex == 2 &&
            SegmentedChoicePreviews.Language("en").SelectedIndex == 2 && SegmentedChoicePreviews.OverlayMode(OverlayBackendMode.Enhanced).SelectedIndex == 1);
        var color = SegmentedChoicePreviews.ColorStyle();
        foreach (var tool in new[] { EditorTool.Rectangle, EditorTool.RoundedRectangle, EditorTool.Ellipse, EditorTool.Triangle, EditorTool.Symbol })
        {
            SegmentedChoicePreviews.UpdateColorStyle(color, tool, SymbolKind.Heart);
            var first = (Image)((StackPanel)color.Items[0].Content).Children[0];
            var second = (Image)((StackPanel)color.Items[1].Content).Children[0];
            check(name + " " + tool + " has distinct previews for separate and unified colors", first.Source is BitmapSource && second.Source is BitmapSource && !ReferenceEquals(first.Source, second.Source));
            SegmentedChoicePreviews.UpdateColorStyle(color, Enum.Parse<ElementKind>(tool.ToString()), SymbolKind.Heart);
            check(name + " " + tool + " uses matching shape previews in the inspector", ReferenceEquals(first.Source, ((Image)((StackPanel)color.Items[0].Content).Children[0]).Source) &&
                ReferenceEquals(second.Source, ((Image)((StackPanel)color.Items[1].Content).Children[0]).Source));
        }
    }

    private static void Layout(Action<string, bool> check, string name, string output, double width)
    {
        var choices = new List<(string Label, SegmentedChoice Choice)>
        {
            (L.Get("Canvas.Mode"), SegmentedChoicePreviews.CanvasMode()),
            (L.Get("Color.Style"), SegmentedChoicePreviews.ColorStyle()),
            (L.Get("Settings.Theme"), SegmentedChoicePreviews.Appearance("system")),
            (L.Get("Settings.Language"), SegmentedChoicePreviews.Language("auto")),
            (L.Get("OverlaySafety.ModeSetting"), SegmentedChoicePreviews.OverlayMode(OverlayBackendMode.Auto))
        };
        SegmentedChoicePreviews.UpdateColorStyle(choices[1].Choice, EditorTool.Ellipse);
        Add("Inspector.StrokePattern", Enum.GetValues<StrokePattern>().Select(value => (L.Get("Canvas." + value), (object)value)));
        Add("Inspector.EndArrow", Enum.GetValues<ArrowHeadStyle>().Select(value => (L.Get(value switch { ArrowHeadStyle.FilledTriangle => "Inspector.ArrowFilledTriangle", ArrowHeadStyle.Open => "Inspector.ArrowOpen", ArrowHeadStyle.Diamond => "Inspector.ArrowDiamond", _ => "Inspector.ArrowNone" }), (object)value)));
        Add("Snapshot.Shape", Enum.GetValues<CaptureShape>().Select(value => (L.Get("Snapshot.Shape." + value), (object)value)));
        Add("Snapshot.Source", Enum.GetValues<CaptureSource>().Select(value => (L.Get("Snapshot.Source." + value), (object)value)));
        Add("Inspector.TextAlignment", Enum.GetValues<FloatCanvas.Core.TextAlignment>().Select(value => (L.Get(value switch { FloatCanvas.Core.TextAlignment.Left => "Inspector.AlignLeft", FloatCanvas.Core.TextAlignment.Center => "Inspector.AlignCenter", _ => "Inspector.AlignRight" }), (object)value)));
        string[] fiveNames = ["One", "Two", "Three", "Four", "Five"];
        var five = new SegmentedChoice(); for (int index = 0; index < 5; index++) five.Add(fiveNames[index], index, SegmentedChoicePreviews.Icon("flat"));
        choices.Add(("Five choices", five));

        var body = new StackPanel(); PropertyPanelLayout.Scope(body);
        foreach (var entry in choices)
        {
            if (entry.Choice.SelectedIndex < 0) entry.Choice.SelectedIndex = 0;
            AutomationProperties.SetName(entry.Choice, entry.Label);
            body.Children.Add(PropertyPanelLayout.Row(new PropertyLabel(entry.Label), entry.Choice));
        }
        var root = new Border { Padding = new(12), Child = body };
        root.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
        root.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "CanvasText");
        root.Measure(new(width, double.PositiveInfinity)); root.Arrange(new(0, 0, width, root.DesiredSize.Height)); root.UpdateLayout();
        foreach (var entry in choices)
        {
            var choice = entry.Choice;
            bool fits = choice.Items.All(item =>
            {
                var content = (StackPanel)item.Content;
                var bounds = content.TransformToAncestor(choice).TransformBounds(new Rect(content.RenderSize));
                return bounds.Left >= -.1 && bounds.Right <= choice.ActualWidth + .1 && bounds.Top >= -.1 && bounds.Bottom <= choice.ActualHeight + .1;
            });
            check(name + " " + width + " " + entry.Label + " keeps all names and glyphs inside the control", fits && choice.ActualHeight >= 34 && choice.Items.Count is >= 2 and <= 5);
            check(name + " " + width + " " + entry.Label + " keeps short option names on one line", choice.Items.All(item =>
                ((TextBlock)((StackPanel)item.Content).Children[1]).ActualHeight < 24));
            choice.SelectedIndex = choice.Items.Count - 1; root.UpdateLayout();
            var indicator = (Border)typeof(SegmentedChoice).GetField("indicator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(choice)!;
            var selected = choice.SelectedItem!;
            check(name + " " + width + " " + entry.Label + " aligns one selection block with its radio option", Math.Abs(indicator.ActualWidth - selected.ActualWidth) < 1 &&
                Math.Abs(indicator.TranslatePoint(new(), choice).X - selected.TranslatePoint(new(), choice).X) < 1);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name.Replace(' ', '-') + "-" + width + ".png")); png.Save(file);
        return;

        void Add(string key, IEnumerable<(string Label, object Value)> items)
        {
            var choice = new SegmentedChoice(); foreach (var item in items) SegmentedChoicePreviews.Add(choice, item.Label, item.Value);
            choices.Add((L.Get(key), choice));
        }
    }

    private static void Click(RadioButton option) => typeof(RadioButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(option, null);
}
