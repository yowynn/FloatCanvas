using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class Feedback4OptionsTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-options4-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string language = L.Preference;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            {
                L.Initialize(locale); AppTheme.Apply("light");
                Galleries(folder, locale, check); CachedSymbolGallery(folder, locale, check); Parameters(folder, locale, check); Colors(folder, locale, check);
            }
        }
        finally { L.Initialize(language); Directory.Delete(folder, true); }
    }
    private static void CachedSymbolGallery(string folder, string locale, Action<string, bool> check)
    {
        var library = new ToolPresetLibrary(Path.Combine(folder, locale + "-symbol-gallery.json"));
        string cachePath = Path.Combine(folder, locale + "-symbol-state.json");
        var state = new ToolStateStore(cachePath);
        var custom = new ToolParameters { Tool = EditorTool.Symbol, Symbol = SymbolKind.Heart, DefaultWidth = 234, DefaultHeight = 123 };
        custom.Style.Stroke = 0xAB123456; custom.Style.StrokeWidth = 7;
        var personal = library.Save("Custom symbol", custom);
        foreach (var preset in library.OfficialFor(EditorTool.Symbol))
        {
            var baseline = custom.Clone(); baseline.Symbol = preset.Parameters.Symbol;
            state.OfficialBaselines[preset.Id] = baseline;
        }
        state.Tools[EditorTool.Symbol] = custom.Clone(); state.Save();
        byte[] originalCache = File.ReadAllBytes(cachePath);
        var loaded = new ToolStateStore(cachePath);
        var controller = new EditorController(new EditorSession()); controller.ApplyParameters(custom);
        var gallery = new ToolPresetGallery(controller, library, loaded);
        foreach (var button in gallery.OfficialTiles.Children.OfType<Button>())
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(locale + " clicking cached " + button.Tag + " restores 72 by 72 while preserving cached style",
                controller.DefaultWidth == 72 && controller.DefaultHeight == 72 && controller.Style.Stroke == custom.Style.Stroke && controller.Style.StrokeWidth == 7);
        }
        gallery.UserTiles.Children.OfType<Button>().Single(button => Equals(button.Tag, personal.Id)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(locale + " clicking a personal symbol keeps its saved independent dimensions", controller.DefaultWidth == 234 && controller.DefaultHeight == 123);
        check(locale + " applying new official symbol dimensions leaves existing saved cache and baseline records intact",
            File.ReadAllBytes(cachePath).SequenceEqual(originalCache) && loaded.OfficialBaselines.Values.All(p => p.DefaultWidth == 234 && p.DefaultHeight == 123) &&
            loaded.Tools[EditorTool.Symbol].DefaultWidth == 234 && loaded.Tools[EditorTool.Symbol].DefaultHeight == 123);
    }
    internal static void Galleries(string folder, string locale, Action<string, bool> check)
    {
        var library = new ToolPresetLibrary(Path.Combine(folder, locale + "-gallery.json"));
        var controller = new EditorController(new EditorSession()) { Tool = EditorTool.Rectangle };
        controller.Style.Stroke = 0xAB123456; controller.Style.StrokeWidth = 7;
        var rectangle = library.Save("Rectangle", controller.Parameters); controller.Tool = EditorTool.Freehand;
        var pen = library.Save("Pen", controller.Parameters); controller.Tool = EditorTool.Rectangle;
        var second = library.Save("Rectangle 2", controller.Parameters);
        var gallery = new ToolPresetGallery(controller, library); int changes = 0; gallery.Applied += () => changes++;
        gallery.Measure(new Size(250, 1000)); gallery.Arrange(new Rect(0, 0, 250, gallery.DesiredSize.Height)); gallery.UpdateLayout();
        check(locale + " every drawing tool has official defaults", Enum.GetValues<EditorTool>().Where(tool => tool is not (EditorTool.Redact or EditorTool.Canvas)).All(tool => library.OfficialFor(tool).Count > 0));
        check(locale + " Canvas uses its dedicated canvas settings rather than drawing recipes", library.OfficialFor(EditorTool.Canvas).Count == 0);
        check(locale + " retired solid tool has no official defaults", library.OfficialFor(EditorTool.Redact).Count == 0);
        check(locale + " personal recipes only show the current tool", gallery.UserTiles.Children.OfType<Button>().Select(b => b.Tag).SequenceEqual(new[] { rectangle.Id, second.Id }));
        check(locale + " both galleries use equal square previews with separate selection indicators", gallery.OfficialTiles.Children.OfType<Button>().Concat(gallery.UserTiles.Children.OfType<Button>()).All(button => Math.Abs(button.ActualWidth - 36) < .01 && Math.Abs(button.ActualHeight - 36) < .01 && Descendants((DependencyObject)button.Content).OfType<Image>().Count() == 1 && button.ToolTip is string && button.BorderThickness.Left > 0));
        check(locale + " a first official preset starts selected", gallery.SelectedId == library.OfficialFor(controller.Tool).First().Id);
        gallery.UserTiles.Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(locale + " one click applies and selects a personal preset", gallery.SelectedId == rectangle.Id && controller.Style.StrokeWidth == 7 && changes == 1);
        controller.Style.StrokeWidth = 18; gallery.Refresh();
        check(locale + " editing keeps the chosen recipe selected", gallery.SelectedId == rectangle.Id);
        check(locale + " modified selected recipe exposes its distinct accessible state", System.Windows.Automation.AutomationProperties.GetItemStatus(gallery.UserTiles.Children.OfType<Button>().Single(button => Equals(button.Tag, rectangle.Id))) == L.Get("Audit.SelectedModified"));
        gallery.Apply(rectangle.Id);
        check(locale + " clicking the same personal recipe restores saved parameters", controller.Style.StrokeWidth == 7 && changes == 2);
        controller.Style.Stroke = 0xBE456789; controller.Style.StrokeWidth = 11;
        gallery.Apply("official.rounded-rectangle");
        check(locale + " first official application preserves current colors", controller.Style.Stroke == 0xBE456789 && controller.Style.StrokeWidth == 11 && controller.CornerRadius == 12);
        controller.Style.StrokeWidth = 21; controller.Style.Stroke = 0xFF000000; gallery.Refresh(); gallery.Apply("official.rounded-rectangle");
        check(locale + " clicking the same official restores its session baseline", controller.Style.Stroke == 0xBE456789 && controller.Style.StrokeWidth == 11 && controller.CornerRadius == 12);
        controller.Tool = EditorTool.Freehand; gallery.Refresh(); check(locale + " changing tools filters personal recipes", gallery.UserTiles.Children.OfType<Button>().Single().Tag.Equals(pen.Id));
        controller.Tool = EditorTool.Rectangle; gallery.Refresh(); check(locale + " changing back remembers that tools selected recipe", gallery.SelectedId == "official.rounded-rectangle");
        library.Move(second.Id, rectangle.Id); gallery.Refresh();
        check(locale + " reordering persists without applying or selecting", new ToolPresetLibrary(Path.Combine(folder, locale + "-gallery.json")).UserFor(EditorTool.Rectangle).First().Id == second.Id && gallery.SelectedId == "official.rounded-rectangle");
        gallery.Apply(rectangle.Id); gallery.Delete(rectangle);
        check(locale + " deleting a selected personal recipe applies the first official", gallery.SelectedId == library.OfficialFor(controller.Tool).First().Id && library.User.All(p => p.Id != rectangle.Id));
        check(locale + " personal context menus only manage recipes", gallery.UserTiles.Children.OfType<Button>().Single().ContextMenu.Items.OfType<MenuItem>().Count() == 3);
    }
    internal static void Parameters(string folder, string locale, Action<string, bool> check)
    {
        var controller = new EditorController(new EditorSession()) { Tool = EditorTool.Line };
        var panel = new ToolOptionsPanel(controller, new(Path.Combine(folder, locale + "-panel.json")), new(Path.Combine(folder, locale + "-panel-colors.json")));
        var rows = Field<Dictionary<string, FrameworkElement>>(panel, "rows"); var fields = Field<Dictionary<string, NumericValueField>>(panel, "numericFields");
        check(locale + " lines have no default length or height", rows["DefaultWidth"].Visibility == Visibility.Collapsed && rows["DefaultHeight"].Visibility == Visibility.Collapsed);
        check(locale + " pure color tools do not duplicate color opacity", rows["Opacity"].Visibility == Visibility.Collapsed);
        var pickers = Field<Dictionary<string, ComboBox>>(panel, "pickers");
        check(locale + " line pattern and arrow choices include diagrams", new[] { "StrokePattern", "StartArrowHead", "EndArrowHead" }.All(key => pickers[key].Items.OfType<ComboBoxItem>().All(item => item.Content is StackPanel)));
        controller.Tool = EditorTool.Symbol; panel.Refresh(); fields["DefaultWidth"].Input.Text = "234"; fields["DefaultHeight"].Input.Text = "123";
        check(locale + " symbols have editable independent default dimensions", controller.DefaultWidth == 234 && controller.DefaultHeight == 123 && rows["DefaultWidth"].Visibility == Visibility.Visible);
        controller.Tool = EditorTool.Number; panel.Refresh(); fields["NumberStart"].Input.Text = "8"; fields["NumberStep"].Input.Text = "3";
        fields["NextNumber"].Input.Text = "42";
        check(locale + " number rules are tool state while next number is document state", controller.NumberStart == 8 && controller.NumberStep == 3 && controller.Session.Document.NextNumber == 42 && controller.Session.UndoCount == 1 && !JsonSerializer.Serialize(controller.Parameters).Contains("NextNumber"));
        check(locale + " document number counter is undoable", controller.Session.Undo() && controller.Session.Document.NextNumber == 1);
        panel.RefreshDocumentState(); check(locale + " current counter refreshes independently from tool parameters", fields["NextNumber"].Value == 1);
        var fonts = Field<ComboBox>(panel, "fontFamily");
        check(locale + " text font picker reads system fonts with visual samples", fonts.Items.Count > 10 && fonts.Items.OfType<ComboBoxItem>().All(item => item.Content is TextBlock text && text.FontFamily is not null));
        controller.Tool = EditorTool.Emoji; panel.Refresh();
        check(locale + " emoji size uses a size label and a generous slider range", ((TextBlock)((StackPanel)rows["FontSize"]).Children[0]).Text == L.Get("ToolOptions.Size") && fields["FontSize"].Slider.Maximum == 512);
        fields["FontSize"].Input.Text = "32"; check(locale + " precise emoji size input remains editable", controller.Style.FontSize == 32);
        check(locale + " naming is not an inline parameter", !rows.ContainsKey("PresetName"));
        var thickness = new NumericValueField(0, 100); var preview = (Border)ToolOptionPreviews.StrokeWidth(thickness);
        thickness.Slider.Value = 9; check(locale + " line-width preview follows the new slider value without lag", ((Border)preview.Child).Height == 9);
        RenderPreviews(panel, controller, locale);
    }
    private static void RenderPreviews(ToolOptionsPanel panel, EditorController controller, string locale)
    {
        string? output = Environment.GetEnvironmentVariable("FLOATCANVAS_OPTIONS4_PREVIEW_DIR"); if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output); var host = new Window { Content = panel, Width = ToolOptionsPanel.PreferredWidth + 20, Height = 680 };
        try
        {
            foreach (string theme in new[] { "light", "dark" })
            {
                AppTheme.Apply(theme);
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                foreach (var tool in new[] { EditorTool.Line, EditorTool.Rectangle, EditorTool.Number })
                {
                    controller.Tool = tool; panel.Refresh(); panel.Measure(new Size(ToolOptionsPanel.PreferredWidth, 620)); panel.Arrange(new Rect(0, 0, ToolOptionsPanel.PreferredWidth, panel.DesiredSize.Height)); panel.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth * 1.5), (int)Math.Ceiling(panel.ActualHeight * 1.5), 144, 144, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(panel); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, $"options4-{locale}-{theme}-{tool}.png")); encoder.Save(file);
                }
            }
        }
        finally { host.Content = null; host.Close(); }
    }
    internal static void Colors(string folder, string locale, Action<string, bool> check)
    {
        string path = Path.Combine(folder, locale + "-colors.json"); var library = new ColorLibrary(path);
        var field = new ColorField(ColorRole.Foreground, library) { Value = 0xFF2383E2 }; int changed = 0; field.ValueChanged += _ => changed++;
        field.Measure(new Size(300, 100)); field.Arrange(new Rect(field.DesiredSize)); field.UpdateLayout();
        var row = (Grid)field.Content; var palette = row.Children.OfType<Panel>().Single();
        check(locale + " primary color field exposes ten swatches in two rows", palette.Children.Count == 10 &&
            palette.Children.OfType<Button>().GroupBy(tile => Math.Round(tile.TranslatePoint(new Point(), palette).Y, 5)).Select(group => group.Count()).SequenceEqual([5, 5]));
        palette.Children.OfType<Button>().ElementAt(2).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(locale + " a primary swatch applies directly without opening a popup", field.Value == ColorLibrary.Palette(ColorRole.Foreground)[2].Value && changed == 1 && !field.IsPickerOpen);
        var picker = Field<ColorPickerPanel>(field, "picker");
        check(locale + " secondary colors omit the role palette and confirmation button", !Descendants(picker).OfType<Button>().Any(button => Equals(button.ToolTip, L.Get("Color.Red")) || System.Windows.Automation.AutomationProperties.GetName(button) == L.Get("Color.Apply")));
        var first = library.Save("First", 0x7F123456); var second = library.Save("Second", 0xFF987654);
        library.Move(second.Id, first.Id); library.Rename(first.Id, "Renamed"); library.Update(second.Id, 0xFF112233);
        var loaded = new ColorLibrary(path);
        check(locale + " named swatches persist colors names IDs and order", loaded.Swatches.Select(s => s.Id).SequenceEqual(new[] { second.Id, first.Id }) && loaded.Swatches[0].Value == 0xFF112233 && loaded.Swatches[1].Name == "Renamed");
        picker.Value = 0x80336699; Field<Slider>(picker, "alpha").Value = 25;
        uint committed = 0; int commits = 0; picker.CommitDraft(value => { committed = value; commits++; });
        check(locale + " close commits one exact draft including opacity", commits == 1 && committed == 0x40336699);
        Field<TextBox>(picker, "hex").Text = "invalid"; picker.CommitDraft(_ => commits++);
        check(locale + " invalid drafts cannot overwrite the active color", commits == 1);
        for (uint i = 0; i < 30; i++) library.Remember(0xFF000000 | i);
        check(locale + " history remains a bounded recency stack", library.History.Count == 20 && library.History.First() == 0xFF00001D);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var descendant in Descendants(child)) yield return descendant; }
}
