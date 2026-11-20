using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;

internal static class ToolStyleCoverageTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var library = new ToolPresetLibrary(Path.Combine(ApplicationPaths.ProfileDirectory, "style-audit-presets.json"));
        var picture = new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 100 };
        var session = new EditorSession(new() { Elements = [picture] }); session.SetSelection([picture.Id]);
        var controller = new EditorController(session);
        var panel = new ToolOptionsPanel(controller, library, new(Path.Combine(ApplicationPaths.ProfileDirectory, "style-audit-colors.json")));
        var rows = Field<Dictionary<string, FrameworkElement>>(panel, "rows");
        var choices = Field<Dictionary<string, SegmentedChoice>>(panel, "pickers");
        foreach (var family in Enum.GetValues<EditorTool>().Select(ToolPresetLibrary.Family).Distinct().Where(tool => tool != EditorTool.Canvas))
        {
            controller.ApplyParameters(library.OfficialFor(family).First().Parameters);
            panel.PrepareForShow();
            string? expected = family switch
            {
                EditorTool.Highlighter => "HighlighterTip", EditorTool.Line => "LinePathMode",
                EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Symbol => "LinkColors",
                EditorTool.Number or EditorTool.Text => "StyleVariant", EditorTool.Presentation => "PresentationSettings",
                _ => null
            };
            string[] styleFields = ["HighlighterTip", "LinePathMode", "LinkColors", "StyleVariant", "PresentationSettings"];
            var visible = styleFields.Where(key => rows[key].Visibility == Visibility.Visible).ToArray();
            check(family + " exposes the intended primary style control", controller.Tool == family &&
                (expected is null ? visible.Length == 0 : visible.SequenceEqual([expected])));
            if (expected is not null && family != EditorTool.Presentation)
                check(family + " names the control Style", ((TextBlock)((Grid)rows[expected]).Children[0]).Text == L.Get("Color.Style"));
        }

        var variants = AppearanceRegistry.VariantsFor(ElementKind.Text).Select(v => v.Id).ToArray();
        check("plain, outline and label are available as official text examples",
            library.OfficialFor(EditorTool.Text).Select(p => p.Parameters.Style.Variant).Distinct().SequenceEqual(variants));
        controller.ApplyParameters(library.Apply("official.text")); panel.PrepareForShow();
        var selector = choices["StyleVariant"];
        check("text style controls offer three named choices and one color", selector.Items.Select(item => item.Tag).SequenceEqual(variants) &&
            rows["Foreground"].Visibility == Visibility.Visible && rows["Background"].Visibility == Visibility.Collapsed);
        selector.SelectedValue = ElementAppearance.TextOutline; panel.PrepareForShow();
        var edge = Field<ColorField>(panel, "background"); var ink = Field<ColorField>(panel, "foreground");
        check("only outlined text exposes stroke and glyph color fields", rows["Background"].Visibility == Visibility.Visible &&
            ((TextBlock)((Grid)rows["Background"]).Children[0]).Text == L.Get("Color.Outline") && edge.Role == ColorRole.Background);
        edge.SetUserValue(0x80339977); ink.SetUserValue(0xFFE0E0E0);
        check("choosing another glyph ink leaves the manual outline untouched", controller.Style.SecondaryColor == 0x80339977);
        selector.SelectedValue = ElementAppearance.TextLabel; panel.PrepareForShow();
        selector.SelectedValue = ElementAppearance.TextOutline; panel.PrepareForShow();
        check("returning to outline restores the last authored alpha and RGB", controller.Style.SecondaryColor == 0x80339977);
        edge.SetUserValue(0); selector.SelectedValue = ElementAppearance.Default; panel.PrepareForShow();
        var transparent = library.Save("No outline ink", controller.Parameters);
        controller.ApplyParameters(new ToolPresetLibrary(Path.Combine(ApplicationPaths.ProfileDirectory, "style-audit-presets.json")).Apply(transparent.Id));
        panel.PrepareForShow(); selector.SelectedValue = ElementAppearance.TextOutline; panel.PrepareForShow();
        check("transparent black remains a deliberate choice after a dormant style is saved and reopened", controller.Style.SecondaryColor == 0);
        selector.SelectedValue = ElementAppearance.TextLabel;
        controller.Style.PrimaryColor = BuiltInColors.Blue;
        var preset = library.Save("My text label", controller.Parameters);
        var restored = new ToolPresetLibrary(Path.Combine(ApplicationPaths.ProfileDirectory, "style-audit-presets.json")).Apply(preset.Id);
        check("a personal label restores its color and appearance", restored.Style.Variant == ElementAppearance.TextLabel && restored.Style.PrimaryColor == BuiltInColors.Blue);

        using var renderer = new SceneRenderer();
        using (var edit = new InlineTextEdit(session, null, new(20, 20), restored.Style, renderer))
        {
            edit.Input.Text = "集合点"; edit.Finish(true);
        }
        var label = session.Document.Elements.Last();
        var fixedLabel = label.Clone(true);
        TextFrameLayout.Resize(fixedLabel, label, .8, .8, renderer);
        check("a resized label fits both its letters and its decoration within the fixed frame",
            fixedLabel.TextFrameFixed && renderer.MeasureText(fixedLabel.Text, fixedLabel.Style, fixedLabel.Width).Height <= fixedLabel.Height + .001);
        var other = label.Clone(true); other.Style.Variant = ElementAppearance.Default;
        session.Add(other); session.SetSelection([label.Id, other.Id]);
        var inspector = new InspectorWindow(() => session);
        using var surface = new InspectorTestSurface(inspector);
        var current = Field<SegmentedChoice>(inspector, "appearanceStyle");
        var fields = Field<Dictionary<string, FrameworkElement>>(inspector, "fieldContainers");
        check("mixed text styles expose their shared primary color and no selected option",
            current.SelectedIndex == -1 && ((TextBlock)((Grid)fields["Stroke"]).Children[0]).Text == L.Get("Color.Primary"));
        current.SelectedValue = ElementAppearance.TextOutline; inspector.CompletePendingEdit();
        var texts = session.Document.Elements.Where(e => e.Kind == ElementKind.Text).ToArray();
        check("changing both layers preserves content and font and reflows their frames", texts.All(e =>
            e.Style.Variant == ElementAppearance.TextOutline && e.Text == "集合点" && e.Style.FontSize == restored.Style.FontSize &&
            Math.Abs(e.Height - renderer.MeasureText(e.Text, e.Style, e.Width).Height) < .001));
        check("outlined text layers share the editable stroke color", fields["Fill"].Visibility == Visibility.Visible &&
            ((TextBlock)((Grid)fields["Fill"]).Children[0]).Text == L.Get("Color.Outline"));
        check("one undo restores the two original text appearances", session.Undo() &&
            session.Document.Elements.Where(e => e.Kind == ElementKind.Text).Select(e => e.Style.Variant)
                .SequenceEqual([ElementAppearance.TextLabel, ElementAppearance.Default]));
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
