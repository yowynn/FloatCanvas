using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;

/// <summary>Refreshes the production reusable panel while inactive attribute rows retain their UI state.</summary>
internal static class HiddenToolPropertiesTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-hidden-properties-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var findings = new List<(string Name, bool Passed)>();
        var editor = new EditorController(new EditorSession());
        editor.Session.Document.NextNumber = 17;
        var initial = editor.Parameters;
        initial.Tool = EditorTool.Text; initial.Style.FontFamily = "Arial"; initial.Style.FontSize = 42;
        initial.Style.StrokeWidth = 5; initial.Style.Stroke = 0xFF112233; initial.Style.Fill = 0xFF445566;
        editor.ApplyParameters(initial);
        try
        {
            var panel = new ToolOptionsPanel(editor, new(Path.Combine(folder, "presets.json")), new(Path.Combine(folder, "colors.json"))); panel.PrepareForShow();
            var rows = Field<Dictionary<string, FrameworkElement>>(panel, "rows");
            var numbers = Field<Dictionary<string, NumericValueField>>(panel, "numericFields");
            var pickers = Field<Dictionary<string, ComboBox>>(panel, "pickers");
            var font = Field<ComboBox>(panel, "fontFamily");
            var foreground = Field<ColorField>(panel, "foreground");
            var background = Field<ColorField>(panel, "background");
            var symbols = Field<Panel>(panel, "symbolGallery");
            var emoji = Field<Panel>(panel, "emojiGallery");

            Observe("text entry synchronizes its visible font family and size", ToolOptionPreviews.SelectedFontName(font) == "Arial" && numbers["FontSize"].Value == 42);
            Apply(EditorTool.Number, p => { p.NumberStart = 5; p.NumberStep = 2; });
            Refresh();
            Observe("number entry synchronizes its visible counter values", numbers["NumberStart"].Value == 5 && numbers["NumberStep"].Value == 2 && numbers["NextNumber"].Value == 17);
            Apply(EditorTool.Symbol, p => { p.Symbol = SymbolKind.Heart; p.DefaultWidth = 120; p.DefaultHeight = 80; p.Style.LinkColors = false; });
            Refresh();
            Apply(EditorTool.Emoji, p => p.Emoji = EmojiArtwork.BuiltIn.First());
            Refresh();
            Apply(EditorTool.Text, p => { p.Style.FontFamily = "Arial"; p.Style.FontSize = 42; p.Style.TextAlignment = FloatCanvas.Core.TextAlignment.Left; });
            Refresh();

            foreach (var (tool, index) in new[] { (EditorTool.Freehand, 0), (EditorTool.Highlighter, 1) })
            {
                var oldNumbers = numbers.ToDictionary(pair => pair.Key, pair => pair.Value.Value);
                var oldPickers = pickers.ToDictionary(pair => pair.Key, pair => pair.Value.SelectedItem);
                string? oldFont = ToolOptionPreviews.SelectedFontName(font);
                uint oldBackground = background.Value;
                var oldSymbols = symbols.Children.OfType<Button>().Select(button => button.BorderBrush).ToArray();
                var oldEmoji = emoji.Children.OfType<Button>().Select(button => button.BorderBrush).ToArray();
                Apply(tool, p =>
                {
                    p.Style.FontFamily = "Consolas"; p.Style.FontSize = 77 + index;
                    p.Style.TextAlignment = FloatCanvas.Core.TextAlignment.Right;
                    p.NumberStart = 23 + index; p.NumberStep = 9 + index;
                    p.DefaultWidth = 333 + index; p.DefaultHeight = 222 + index;
                    p.Symbol = Enum.GetValues<SymbolKind>().First(value => value != SymbolKind.Heart);
                    p.Emoji = EmojiArtwork.BuiltIn.First(value => value != EmojiArtwork.BuiltIn.First());
                    p.CaptureShape = CaptureShape.Ellipse; p.CaptureSource = CaptureSource.CanvasOnly;
                    p.CaptureBorderWidth = 13 + index; p.CaptureBorderColor = 0xFF789ABC;
                    p.EffectStrength = 50 + index; p.CornerRadius = 11 + index;
                    p.Style.Fill = 0xFFABCDEF; p.Style.Opacity = .4;
                    p.Style.ArrowHeadSize = 37 + index; p.Style.StartArrowHead = ArrowHeadStyle.Diamond;
                    p.Style.EndArrowHead = ArrowHeadStyle.None; p.Style.StrokePattern = StrokePattern.Dashed;
                    p.Style.StrokeWidth = 24 + index; p.Style.Stroke = 0xFF123456;
                    p.ConstrainAspect = true;
                });
                editor.Session.Document.NextNumber = 58;
                Refresh();
                string label = tool.ToString();
                Observe(label + " keeps every hidden numeric attribute unchanged", numbers.Where(pair => rows[pair.Key].Visibility != Visibility.Visible)
                    .All(pair => pair.Value.Value == oldNumbers[pair.Key]));
                Observe(label + " keeps every hidden choice unchanged", pickers.Where(pair => rows[pair.Key].Visibility != Visibility.Visible)
                    .All(pair => ReferenceEquals(pair.Value.SelectedItem, oldPickers[pair.Key])));
                Observe(label + " does not synchronize the hidden font picker", rows["FontFamily"].Visibility == Visibility.Collapsed && ToolOptionPreviews.SelectedFontName(font) == oldFont);
                Observe(label + " leaves the hidden background color unchanged", rows["Background"].Visibility == Visibility.Collapsed && background.Value == oldBackground);
                Observe(label + " leaves hidden symbol and emoji galleries unchanged", oldSymbols.SequenceEqual(symbols.Children.OfType<Button>().Select(button => button.BorderBrush)) && oldEmoji.SequenceEqual(emoji.Children.OfType<Button>().Select(button => button.BorderBrush)));
                Observe(label + " synchronizes its visible stroke width, pattern and foreground", numbers["StrokeWidth"].Value == editor.Style.StrokeWidth &&
                    pickers["StrokePattern"].SelectedItem is ComboBoxItem { Tag: StrokePattern.Dashed } && foreground.Value == editor.Style.Stroke);

                var beforeSymbols = symbols.Children.OfType<Button>().Select(button => button.BorderBrush).ToArray();
                var beforeEmoji = emoji.Children.OfType<Button>().Select(button => button.BorderBrush).ToArray();
                numbers["StrokeWidth"].Input.Text = (31 + index).ToString();
                Observe(label + " visible property edits preserve hidden gallery state", editor.Style.StrokeWidth == 31 + index &&
                    beforeSymbols.SequenceEqual(symbols.Children.OfType<Button>().Select(button => button.BorderBrush)) && beforeEmoji.SequenceEqual(emoji.Children.OfType<Button>().Select(button => button.BorderBrush)));
            }

            Apply(EditorTool.Text, _ => { }); Refresh();
            Observe("returning to text synchronizes the latest previously hidden font and alignment", ToolOptionPreviews.SelectedFontName(font) == "Consolas" && numbers["FontSize"].Value == 78 &&
                pickers["TextAlignment"].SelectedItem is ComboBoxItem { Tag: FloatCanvas.Core.TextAlignment.Right });
            Apply(EditorTool.Number, _ => { }); Refresh();
            Observe("returning to number synchronizes the latest counter state", numbers["NumberStart"].Value == 24 && numbers["NumberStep"].Value == 10 && numbers["NextNumber"].Value == 58);
            Apply(EditorTool.Symbol, _ => { }); Refresh();
            Observe("returning to shape synchronizes the latest dimensions", numbers["DefaultWidth"].Value == 334 && numbers["DefaultHeight"].Value == 223);
            Apply(EditorTool.CaptureRegion, _ => { }); Refresh();
            Observe("entering capture synchronizes its previously hidden shape source and border", pickers["CaptureShape"].SelectedItem is ComboBoxItem { Tag: CaptureShape.Ellipse } &&
                pickers["CaptureSource"].SelectedItem is ComboBoxItem { Tag: CaptureSource.CanvasOnly } && numbers["CaptureBorderWidth"].Value == 14 && foreground.Value == 0xFF789ABC);
            Apply(EditorTool.Image, _ => { }); Refresh();
            Observe("entering image synchronizes its previously hidden opacity", numbers["Opacity"].Value == 40);
            Observe("attribute synchronization never edits scene contents or history", editor.Session.Document.Elements.Count == 0 && editor.Session.UndoCount == 0 && !editor.Session.IsTransactionActive);
            Observe("hidden property fixture creates no native presentation source", !PresentationSource.CurrentSources.Cast<PresentationSource>().Any());

            void Refresh()
            {
                string before = JsonSerializer.Serialize(editor.Parameters);
                panel.PrepareForShow();
                Observe("refresh for " + editor.Tool + " keeps the model unchanged", JsonSerializer.Serialize(editor.Parameters) == before);
            }
            void Apply(EditorTool tool, Action<ToolParameters> change)
            {
                var parameters = editor.Parameters; parameters.Tool = tool; change(parameters); editor.ApplyParameters(parameters);
            }
        }
        finally { editor.DetachSession(); Directory.Delete(folder, true); }
        foreach (var finding in findings) Console.WriteLine($"HIDDEN_PROPERTY_FINDING {finding.Passed}: {finding.Name}");
        foreach (var finding in findings) check(finding.Name, finding.Passed);

        void Observe(string name, bool passed) => findings.Add((name, passed));
    }

    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
}
