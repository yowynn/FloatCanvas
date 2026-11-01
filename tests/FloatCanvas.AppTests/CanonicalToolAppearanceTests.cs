using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;
using Controls = System.Windows.Controls;
using Wpf = System.Windows;

internal static class CanonicalToolAppearanceTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        string folder = Path.Combine(ApplicationPaths.ProfileDirectory, "canonical-appearance"); Directory.CreateDirectory(folder);
        Official(folder, check); Configuration(folder, check); Unknown(folder, check); Selectors(folder, check); LegacyPixels(check); Render(check);
        if (output is not null) Visuals(output, folder, check);
    }

    private static void Official(string folder, Action<string, bool> check)
    {
        var assembly = typeof(CanonicalToolAppearanceTests).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name => name.EndsWith("Appearance.official-v1.json", StringComparison.Ordinal)))!;
        var baseline = JsonNode.Parse(stream)!.AsArray();
        var current = new ToolPresetLibrary(Path.Combine(folder, "official-unused.json")).Official;
        var baselineIds = baseline.Select(node => node!["Id"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        var original = current.Where(preset => baselineIds.Contains(preset.Id)).ToArray();
        check("official baseline keeps all 57 actual M4 IDs in order", baseline.Select(node => node!["Id"]!.ToString()).SequenceEqual(original.Select(preset => preset.Id)));
        check("presentation adds three keyboard and three mouse recipes after the original 57 without rewriting their identities", current.Count == 63 && current.Where(preset => !baselineIds.Contains(preset.Id)).Select(preset => preset.Id).SequenceEqual(new[] { "official.presentation-keys", "official.presentation-keycaps", "official.presentation-keyboard", "official.presentation-pointer-halo", "official.presentation-pointer-clicks", "official.presentation-pointer-trail" }));
        foreach (var preset in original)
        {
            var expected = baseline.Single(node => node!["Id"]!.ToString() == preset.Id)!["Parameters"]!.DeepClone();
            var style = expected["Style"]!.AsObject();
            bool linked = style["LinkColors"]?.GetValue<bool>() == true;
            var kind = preset.Parameters.AppearanceKind;
            style["Foreground"] = style["Stroke"]!.DeepClone(); style["Background"] = style["Fill"]!.DeepClone(); style["AdditionalColors"] = new JsonObject();
            style["Variant"] = kind is { } shape && ElementGeometry.IsBoxShape(shape) ? linked ? ElementAppearance.ShapeUniform : ElementAppearance.ShapeIndependent :
                kind == ElementKind.Number ? ElementAppearance.NumberFilled : ElementAppearance.Default;
            foreach (string key in new[] { "Stroke", "Fill", "LinkColors" }) style.Remove(key);
            expected.AsObject().Remove("NumberColorRolesVersion"); expected["NumberTextSizeMode"] = (int)NumberTextSizeMode.FitBadge;
            check("official complete snapshot remains equivalent: " + preset.Id, JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(preset.Parameters)));
        }
    }

    private static JsonObject LegacyParameters(uint stroke, uint fill, int? roles, string? variant)
    {
        var parameters = JsonSerializer.SerializeToNode(new ToolParameters { Tool = EditorTool.Number })!.AsObject();
        var style = parameters["Style"]!.AsObject();
        foreach (string key in new[] { "Foreground", "Background", "AdditionalColors", "Variant" }) style.Remove(key);
        style["Stroke"] = stroke; style["Fill"] = fill; if (variant is not null) style["Variant"] = variant;
        parameters.Remove("NumberTextSizeMode"); if (roles is { } version) parameters["NumberColorRolesVersion"] = version;
        return parameters;
    }

    private static void Configuration(string folder, Action<string, bool> check)
    {
        var previousPrompt = CompatibleConfiguration.ConfirmRepair; int prompts = 0;
        CompatibleConfiguration.ConfirmRepair = (_, _) => { prompts++; return false; };
        try
        {
            foreach (int? roles in new int?[] { null, 0, 1 }) foreach (string? variant in new[] { null, ElementAppearance.NumberFilled, ElementAppearance.NumberOutline })
            foreach (uint fill in new uint[] { 0x00112233, 0xff2288cc })
            {
                uint stroke = 0x80443322; string path = Path.Combine(folder, "state-matrix.json");
                var parameters = LegacyParameters(stroke, fill, roles, variant);
                var raw = new JsonObject { ["Version"] = 1, ["Tools"] = new JsonObject { ["Number"] = parameters.DeepClone() },
                    ["PreviewSources"] = new JsonObject { ["Number"] = parameters.DeepClone() },
                    ["OfficialBaselines"] = new JsonObject { ["official.number"] = parameters.DeepClone() },
                    ["Buttons"] = new JsonObject(), ["Selections"] = new JsonObject(), ["Future"] = new JsonObject { ["Stroke"] = 17, ["Fill"] = "unrelated" } };
                File.WriteAllText(path, raw.ToJsonString()); string original = File.ReadAllText(path);
                var state = new ToolStateStore(path); state.Save();
                bool legacy = roles != 1 && variant is null;
                var snapshots = new[] { state.Tools[EditorTool.Number], state.PreviewSources[EditorTool.Number], state.OfficialBaselines["official.number"] };
                check($"v1 role migration preserves all snapshots roles={roles} style={variant} fill={fill:x8}", snapshots.All(value =>
                    value.Style.Foreground == (legacy ? (fill >> 24) == 0 ? stroke : fill : stroke) && value.Style.Background == (legacy ? 0xffffffff : fill) &&
                    value.NumberTextSizeMode == NumberTextSizeMode.FitBadge && (!legacy || value.Style.AdditionalColors["legacy.fill"] == fill && value.Style.AdditionalColors["legacy.stroke"] == stroke)));
                check("reading and unchanged Save leave v1 bytes untouched", File.ReadAllText(path) == original);
                state.Buttons[EditorTool.Number] = EditorTool.Number; state.Save(); var saved = JsonNode.Parse(File.ReadAllText(path))!;
                check("unrelated real save rewrites each owned style to v2 without legacy aliases", saved["Version"]!.GetValue<int>() == 2 &&
                    new[] { saved["Tools"]!["Number"]!, saved["PreviewSources"]!["Number"]!, saved["OfficialBaselines"]!["official.number"]! }.All(Canonical) &&
                    saved["Future"]!["Stroke"]!.GetValue<int>() == 17 && saved["Future"]!["Fill"]!.GetValue<string>() == "unrelated");
                check("canonical reload does not repeat the number role migration", JsonSerializer.Serialize(new ToolStateStore(path).Tools[EditorTool.Number]) == JsonSerializer.Serialize(state.Tools[EditorTool.Number]));
            }
            check("normal v1 appearance migration never requests a corruption repair", prompts == 0);
        }
        finally { CompatibleConfiguration.ConfirmRepair = previousPrompt; }
    }
    private static bool Canonical(JsonNode parameters)
    {
        var style = parameters["Style"]!.AsObject();
        return !parameters.AsObject().ContainsKey("NumberColorRolesVersion") && parameters["NumberTextSizeMode"] is not null &&
            new[] { "Foreground", "Background", "AdditionalColors", "Variant" }.All(style.ContainsKey) && !new[] { "Stroke", "Fill", "LinkColors" }.Any(style.ContainsKey);
    }

    private static void Unknown(string folder, Action<string, bool> check)
    {
        string path = Path.Combine(folder, "future-presets.json");
        var unknown = LegacyParameters(0xff123456, 0x00778899, 1, "number.future-style");
        var known = LegacyParameters(0xff223344, 0xffccddff, 1, ElementAppearance.NumberFilled);
        var raw = new JsonObject { ["Version"] = 1, ["FutureRoot"] = true, ["Presets"] = new JsonArray(
            new JsonObject { ["Id"] = "user-future", ["Name"] = "Future", ["Parameters"] = unknown, ["FutureRecord"] = 81 },
            new JsonObject { ["Id"] = "user-known", ["Name"] = "Known", ["Parameters"] = known }) };
        File.WriteAllText(path, raw.ToJsonString()); string original = File.ReadAllText(path);
        var library = new ToolPresetLibrary(path);
        check("future variant retains its record without preventing known presets from loading", library.User.Count == 1 && library.User[0].Id == "user-known" && File.ReadAllText(path) == original);
        bool rejected = false; try { library.Apply("user-future"); } catch (KeyNotFoundException) { rejected = true; }
        library.Rename("user-known", "Renamed"); var saved = JsonNode.Parse(File.ReadAllText(path))!; var future = saved["Presets"]![0]!;
        check("unsupported variant cannot execute and survives a peer edit with unknown metadata", rejected && future["Parameters"]!["Style"]!["Variant"]!.ToString() == "number.future-style" &&
            future["Parameters"]!["Style"]!["Foreground"]!.GetValue<uint>() == 0xff123456 && future["Parameters"]!["Style"]!["Background"]!.GetValue<uint>() == 0x00778899 &&
            future["FutureRecord"]!.GetValue<int>() == 81 && saved["FutureRoot"]!.GetValue<bool>());
    }

    private static void Selectors(string folder, Action<string, bool> check)
    {
        _ = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        var controller = new EditorController(new EditorSession());
        var parameters = new ToolParameters { Tool = EditorTool.Number, Style = new() { Foreground = 0xff2266aa, Background = 0x40778899, Variant = ElementAppearance.NumberFilled } };
        controller.ApplyParameters(parameters); var panel = new ToolOptionsPanel(controller, new(Path.Combine(folder, "selector-presets.json")), new(Path.Combine(folder, "selector-colors.json")));
        panel.PrepareForShow(); var pickers = Field<Dictionary<string, SegmentedChoice>>(panel, "pickers"); var rows = Field<Dictionary<string, Wpf.FrameworkElement>>(panel, "rows");
        var selector = pickers["StyleVariant"];
        check("number options expose all three registry variants with string IDs", selector.Items.Count == 3 && selector.Items.Select(item => item.Tag).SequenceEqual(AppearanceRegistry.VariantsFor(ElementKind.Number).Select(variant => variant.Id)));
        foreach (var variant in AppearanceRegistry.VariantsFor(ElementKind.Number))
        {
            selector.SelectedValue = variant.Id; panel.PrepareForShow();
            check("number variant changes preserve hidden secondary color and font mode: " + variant.Id, controller.Style.Background == 0x40778899 &&
                controller.Parameters.NumberTextSizeMode == NumberTextSizeMode.FitBadge && (rows["Background"].Visibility == Wpf.Visibility.Visible) == variant.BackgroundActive);
        }
        var capped = controller.Parameters; capped.NumberTextSizeMode = NumberTextSizeMode.CapFontSize; capped.Style.FontSize = 8;
        controller.ApplyParameters(capped); panel.PrepareForShow();
        foreach (var variant in AppearanceRegistry.VariantsFor(ElementKind.Number))
        {
            selector.SelectedValue = variant.Id; panel.PrepareForShow();
            check("style selection preserves explicit capped font mode: " + variant.Id, controller.Parameters.NumberTextSizeMode == NumberTextSizeMode.CapFontSize && controller.Style.FontSize == 8);
        }
        var cappedLibrary = new ToolPresetLibrary(Path.Combine(folder, "capped-font-preset.json")); var cappedPreset = cappedLibrary.Save("Capped font", controller.Parameters);
        var cappedReload = new ToolPresetLibrary(Path.Combine(folder, "capped-font-preset.json")).Apply(cappedPreset.Id);
        check("personal recipes round-trip explicit capped number font mode independently of style", cappedReload.NumberTextSizeMode == NumberTextSizeMode.CapFontSize && cappedReload.Style.FontSize == 8 && cappedReload.Style.Variant == ElementAppearance.NumberOutlineIndependent);
        check("tool appearance selection creates no canvas undo entries", controller.Session.UndoCount == 0 && controller.Session.Document.Elements.Count == 0);
        var first = new CanvasElement { Kind = ElementKind.Rectangle, Width = 50, Height = 40, Style = new() { Foreground = 0xff334455, Background = 0xffbbccdd, Variant = ElementAppearance.ShapeIndependent } };
        var number = new CanvasElement { Kind = ElementKind.Number, Width = 40, Height = 40, Style = new() { Foreground = 0xff556677, Background = 0xffbbccdd, Variant = ElementAppearance.NumberOutlineIndependent } };
        var owner = new EditorSession(new() { Elements = [first, number] }); owner.SetSelection([first.Id, number.Id]);
        var inspector = new InspectorWindow(() => owner); inspector.PrepareForShow(); var colors = Field<Dictionary<string, ColorField>>(inspector, "colorFields");
        check("mixed shape and two-color number share canonical mixed primary and secondary", colors["Stroke"].IsMixed && !colors["Fill"].IsMixed);
        colors["Stroke"].SetUserValue(0x80112233); Invoke(inspector, "Apply");
        check("mixed canonical primary edits both real source leaves in one undo", owner.Document.Elements.All(element => element.Style.Foreground == 0x80112233) && owner.UndoCount == 1);
        owner.Undo(); check("mixed undo restores both distinct original primary colors", owner.Document.Elements[0].Style.Foreground == 0xff334455 && owner.Document.Elements[1].Style.Foreground == 0xff556677);
        SessionWindowCoordinator.Close(inspector);
    }

    private static void Render(Action<string, bool> check)
    {
        var element = new CanvasElement { Kind = ElementKind.Number, Width = 80, Height = 80, Number = 1, NumberTextSizeMode = NumberTextSizeMode.FitBadge,
            Transform = Affine2D.Translation(10, 10), Style = new() { Foreground = 0xffff0000, Background = 0xff0000ff, Variant = ElementAppearance.NumberOutlineIndependent, StrokeWidth = 6 } };
        using var renderer = new SceneRenderer();
        using var bitmap = SKBitmap.Decode(renderer.ExportPng(new() { Viewport = new(0, 0, 100, 100), Elements = [element] }, new(), true));
        int red = 0, blue = 0; foreach (var color in bitmap.Pixels) { if (color.Red > 200 && color.Blue < 30) red++; if (color.Blue > 200 && color.Red < 30) blue++; }
        check("third number style renders separate red ring and blue text with an empty body", red > 100 && blue > 30 && bitmap.GetPixel(35, 35).Alpha == 0);
        element.Style.StrokeWidth = 0;
        using var zero = SKBitmap.Decode(renderer.ExportPng(new() { Viewport = new(0, 0, 100, 100), Elements = [element] }, new(), true));
        check("zero-width third number style hides the ring while keeping its text", !zero.Pixels.Any(color => color.Red > 200 && color.Blue < 30) && zero.Pixels.Any(color => color.Blue > 200 && color.Red < 30));
        element.Style.AdditionalColors["shadow"] = 0x00123456; var clone = element.Clone(); clone.Style.AdditionalColors["shadow"] = 0xffabcdef;
        check("additional color snapshots are isolated and preserve transparent RGB", element.Style.AdditionalColors["shadow"] == 0x00123456);
    }
    private static void LegacyPixels(Action<string, bool> check)
    {
        var assembly = typeof(CanonicalToolAppearanceTests).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name => name.EndsWith("Appearance.legacy-render-v11.json", StringComparison.Ordinal)))!;
        var cases = JsonNode.Parse(stream)!.AsArray(); using var renderer = new SceneRenderer();
        foreach (var baseline in cases)
        {
            var element = baseline!["Element"]!.Deserialize<CanvasElement>()!;
            using var bitmap = SKBitmap.Decode(renderer.ExportPng(new() { Viewport = new(0, 0, 100, 100), Elements = [element] }, new(), true));
            check("M4 wire migration preserves exact pixels: " + baseline["Name"], Convert.ToHexString(SHA256.HashData(bitmap.Bytes)) == baseline["PixelSha256"]!.ToString());
        }
    }
    private static void Visuals(string output, string folder, Action<string, bool> check)
    {
        Directory.CreateDirectory(output); string language = FloatCanvas.Localization.AppText.Preference;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" }) foreach (string theme in new[] { "light", "dark" })
            {
                FloatCanvas.Localization.AppText.Initialize(locale); AppTheme.Apply(theme);
                var controller = new EditorController(new EditorSession());
                controller.ApplyParameters(new() { Tool = EditorTool.Number, Style = new() { Foreground = 0xff2266aa, Background = 0xffdd5533, Variant = ElementAppearance.NumberOutlineIndependent } });
                var panel = new ToolOptionsPanel(controller, new(Path.Combine(folder, "visual-presets.json")), new(Path.Combine(folder, "visual-colors.json")));
                panel.PrepareForShow(); panel.Measure(new(ToolOptionsPanel.PreferredWidth, 620)); panel.Arrange(new(0, 0, ToolOptionsPanel.PreferredWidth, 620)); panel.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), 620, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(panel);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"number-variants-{locale}-{theme}.png")); encoder.Save(stream);
                var selector = Field<Dictionary<string, SegmentedChoice>>(panel, "pickers")["StyleVariant"];
                check(locale + " " + theme + " all three style controls fit their bounded row", selector.Items.Count == 3 && selector.Items.All(item => item.ActualWidth > 0 && item.ActualHeight > 0) && selector.ActualWidth <= panel.ActualWidth);
                var number = new CanvasElement { Kind = ElementKind.Number, Width = 72, Height = 72, Style = controller.Style.Clone(), NumberTextSizeMode = NumberTextSizeMode.FitBadge };
                var owner = new EditorSession(new() { Elements = [number] }); owner.Document.Canvas.Projection.Enabled = true; owner.SetSelection([number.Id]);
                var inspector = new InspectorWindow(() => owner); inspector.PrepareForShow();
                var content = (Wpf.FrameworkElement)inspector.Content; content.Measure(new(300, 700)); content.Arrange(new(0, 0, 300, 700)); content.UpdateLayout();
                var background = new System.Windows.Media.DrawingVisual();
                using (var drawing = background.RenderOpen()) drawing.DrawRectangle(inspector.Background, null, new(0, 0, 300, 700));
                var inspectorImage = new System.Windows.Media.Imaging.RenderTargetBitmap(300, 700, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); inspectorImage.Render(background); inspectorImage.Render(content);
                var inspectorEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); inspectorEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(inspectorImage));
                using (var inspectorStream = File.Create(Path.Combine(output, $"number-inspector-{locale}-{theme}.png"))) inspectorEncoder.Save(inspectorStream);
                var numberStyle = Field<SegmentedChoice>(inspector, "numberStyle"); var preserve = Field<Controls.CheckBox>(inspector, "preservePlacementAppearance");
                check(locale + " " + theme + " narrow inspector keeps three styles and placement option within the property width", numberStyle.ActualWidth <= 300 &&
                    numberStyle.Items.All(item => item.ActualWidth > 0) && preserve.ActualWidth > 0 && preserve.ActualWidth <= 300 && new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0);
                Descendants(content).OfType<Controls.ScrollViewer>().First().ScrollToEnd(); content.UpdateLayout();
                var placementImage = new System.Windows.Media.Imaging.RenderTargetBitmap(300, 700, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); placementImage.Render(background); placementImage.Render(content);
                var placementEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); placementEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(placementImage));
                using (var placementStream = File.Create(Path.Combine(output, $"number-placement-{locale}-{theme}.png"))) placementEncoder.Save(placementStream);
                var position = preserve.TranslatePoint(default, content);
                check(locale + " " + theme + " placement appearance checkbox remains reachable without horizontal overflow", position.X >= 0 && position.X + preserve.ActualWidth <= 300 && position.Y >= 0 && position.Y + preserve.ActualHeight <= 700);
                SessionWindowCoordinator.Close(inspector);
            }
        }
        finally { FloatCanvas.Localization.AppText.Initialize(language); }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return item;
    }
}
