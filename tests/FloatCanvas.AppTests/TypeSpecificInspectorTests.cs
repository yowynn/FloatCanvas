using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class TypeSpecificInspectorTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string language = L.Preference;
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-type-inspector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            AppTheme.Apply("light"); L.Initialize("zh-CN");
            foreach (var kind in new[] { ElementKind.LiveBlur, ElementKind.LivePixelate })
            {
                var effect = new ImageEffect { Kind = kind == ElementKind.LiveBlur ? ImageEffectKind.Blur : ImageEffectKind.Pixelate, Amount = 17, Bounds = new(0, 0, 1, 1) };
                var session = new EditorSession(new() { Elements = [new CanvasElement { Kind = kind, Width = 120, Height = 90, Effects = [effect] }] });
                session.Selection.Add(session.Document.Elements[0].Id);
                using var fixture = new Fixture(session, folder);
                var containers = Field<Dictionary<string, FrameworkElement>>(fixture.Window, "fieldContainers");
                check(kind + " existing layers expose their own effect strength", containers.TryGetValue("EffectStrength", out var row) && row.Visibility == Visibility.Visible);
                var fields = Field<Dictionary<string, TextBox>>(fixture.Window, "fields");
                check(kind + " inspector loads the stored effect parameter", fields["EffectStrength"].Text == "17");
                int undo = session.UndoCount;
                fields["EffectStrength"].Text = "37.5"; fixture.Complete();
                check(kind + " edits update the selected entity effect and preserve its identity and region", session.Document.Elements[0].Effects.Single() is { Amount: 37.5 } changed && changed.Id == effect.Id && changed.Bounds == effect.Bounds);
                check(kind + " parameter edit makes one undo step", session.UndoCount == undo + 1 && session.Undo() && session.Document.Elements[0].Effects.Single().Amount == 17);
                check(kind + " parameter edit can redo", session.Redo() && session.Document.Elements[0].Effects.Single().Amount == 37.5);
                fixture.Window.PrepareForShow(); fields["EffectStrength"].Text = "0"; fixture.Complete();
                check(kind + " invalid strength cannot modify the entity", session.Document.Elements[0].Effects.Single().Amount == 37.5);
            }
            MultipleEffects(folder, check);
            SymbolsAndTypeSizes(folder, check);
            ImageBorders(folder, check);
            MixedAndHidden(folder, check);
        }
        finally { L.Initialize(language); AppTheme.Apply("light"); Directory.Delete(folder, recursive: true); }
    }

    private static void MultipleEffects(string folder, Action<string, bool> check)
    {
        var image = new CanvasElement { Kind = ElementKind.Image, Width = 120, Height = 90, Effects =
            [new() { Kind = ImageEffectKind.Blur, Amount = 8, Bounds = new(.1, .2, .3, .4) }, new() { Kind = ImageEffectKind.Pixelate, Amount = 19, Bounds = new(.2, .3, .4, .5) },
                new() { Kind = ImageEffectKind.Redact, Color = 0xFF123456, Bounds = new(.25, .25, .5, .5) }] };
        var session = new EditorSession(new() { Elements = [image] }); session.Selection.Add(image.Id);
        using var fixture = new Fixture(session, folder);
        using var surface = new InspectorTestSurface(fixture.Window);
        var fields = Field<Dictionary<string, TextBox>>(fixture.Window, "fields");
        var choice = Field<ComboBox>(fixture.Window, "effectChoice");
        check("image effect selector exposes every retained effect without changing the entity", choice.Items.Count == 3 && session.UndoCount == 0);
        choice.SelectedValue = 1;
        check("choosing another effect loads its own strength", fields["EffectStrength"].Text == "19" && session.UndoCount == 0);
        SaveInspector(fixture.Window, "type-properties-effects.png");
        fields["EffectStrength"].Text = "51"; fixture.Complete();
        check("editing one retained image effect preserves the others", image.Effects[0].Amount == 8 && image.Effects[1].Amount == 51 && image.Effects[2].Color == 0xFF123456);
        check("effect parameter edits preserve the original region and layer geometry", image.Effects[1].Bounds == new RectD(.2, .3, .4, .5) && image.Transform == Affine2D.Identity && image.Width == 120);
        choice.SelectedValue = 2;
        var colors = Field<Dictionary<string, ColorField>>(fixture.Window, "colorFields");
        colors["EffectColor"].SetUserValue(0xFFBADA55); fixture.Complete();
        check("redaction exposes its own color rather than irrelevant strength", image.Effects[2].Color == 0xFFBADA55 && Field<Dictionary<string, FrameworkElement>>(fixture.Window, "fieldContainers")["EffectStrength"].Visibility == Visibility.Collapsed);
    }

    private static void SymbolsAndTypeSizes(string folder, Action<string, bool> check)
    {
        var symbol = new CanvasElement { Kind = ElementKind.Symbol, Symbol = SymbolKind.Star, Width = 70, Height = 60 };
        var session = new EditorSession(new() { Elements = [symbol] }); session.Selection.Add(symbol.Id);
        using var fixture = new Fixture(session, folder);
        var symbols = Field<ComboBox>(fixture.Window, "symbolChoice");
        check("symbol layers expose the saved shape choice", Equals(symbols.SelectedValue, SymbolKind.Star));
        symbols.SelectedValue = SymbolKind.Heart; fixture.Complete();
        check("symbol shape changes are editable and undoable without replacing the layer", symbol.Symbol == SymbolKind.Heart && session.Undo() && session.Document.Elements[0].Symbol == SymbolKind.Star);
        foreach (var element in new[] { new CanvasElement { Kind = ElementKind.Number, Number = 1, Width = 40, Height = 40 },
            new CanvasElement { Kind = ElementKind.Text, Text = "😀", Width = 40, Height = 40, Style = new() { FontFamily = "Segoe UI Emoji", FontSize = 24 } } })
        {
            var typed = new EditorSession(new() { Elements = [element] }); typed.Selection.Add(element.Id);
            using var typedFixture = new Fixture(typed, folder);
            var containers = Field<Dictionary<string, FrameworkElement>>(typedFixture.Window, "fieldContainers");
            check(element.Kind + " retains its creation size property after creation", containers["FontSize"].Visibility == Visibility.Visible);
            var fields = Field<Dictionary<string, TextBox>>(typedFixture.Window, "fields"); fields["FontSize"].Text = "64"; typedFixture.Complete();
            check(element.Kind + " size property updates its saved font size", typed.Document.Elements[0].Style.FontSize == 64);
            if (element.Kind == ElementKind.Number)
            {
                fields["Number"].Text = "-12"; typedFixture.Complete();
                check("number layer accepts signed values while its slider starts at one", typed.Document.Elements[0].Number == -12 &&
                    Field<Dictionary<string, NumericValueField>>(typedFixture.Window, "numericFields")["Number"].Slider.Minimum == 1);
                SaveInspector(typedFixture.Window, "type-properties-number.png");
            }
        }
    }

    private static void MixedAndHidden(string folder, Action<string, bool> check)
    {
        CanvasElement Make(double amount) => new() { Kind = ElementKind.LiveBlur, Width = 120, Height = 80, Effects = [new() { Kind = ImageEffectKind.Blur, Amount = amount, Bounds = new(0, 0, 1, 1) }] };
        var first = Make(10); var second = Make(25);
        var session = new EditorSession(new() { Elements = [first, second] }); session.Selection.UnionWith([first.Id, second.Id]);
        using var fixture = new Fixture(session, folder);
        var fields = Field<Dictionary<string, TextBox>>(fixture.Window, "fields");
        check("mixed effect strength presents an empty draft", fields["EffectStrength"].Text.Length == 0);
        fields["EffectStrength"].Text = "33"; fixture.Complete();
        check("mixed selection edits each compatible effect in one undo unit", first.Effects[0].Amount == 33 && second.Effects[0].Amount == 33 && session.UndoCount == 1);
        string before = JsonSerializer.Serialize(session.Document);
        fields["EffectStrength"].Text = "retained draft";
        fixture.Window.Refresh();
        check("hidden inspectors retain their field drafts without refreshing controls", fields["EffectStrength"].Text == "retained draft" && JsonSerializer.Serialize(session.Document) == before);
    }

    private static void ImageBorders(string folder, Action<string, bool> check)
    {
        var image = new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 60, ImageBorderWidth = 2048,
            Transform = Affine2D.Scale(.5, .5), Style = new() { StrokeWidth = 4, Stroke = 0xFF123456 } };
        var session = new EditorSession(new() { Elements = [image] }); session.Selection.Add(image.Id);
        using var fixture = new Fixture(session, folder);
        var fields = Field<Dictionary<string, TextBox>>(fixture.Window, "fields");
        var numeric = Field<Dictionary<string, NumericValueField>>(fixture.Window, "numericFields");
        check("image inspector displays large local capture border widths exactly without mutating the model", fields["StrokeWidth"].Text == "2048" &&
            numeric["StrokeWidth"].TryGetValue(out double actual) && actual == 2048 && image.ImageBorderWidth == 2048 && session.UndoCount == 0);
        check("image property dimensions exclude the complete outward border", fields["W"].Text == "50" && fields["H"].Text == "30");
        check("image property position uses the body rather than the outward stroke", fields["X"].Text == "0" && fields["Y"].Text == "0");
        fields["StrokeWidth"].Text = "3072"; fixture.Complete();
        check("image border edits use the image field instead of dormant legacy shape width", image.ImageBorderWidth == 3072 && image.Style.StrokeWidth == 4);
        var colors = Field<Dictionary<string, ColorField>>(fixture.Window, "colorFields");
        colors["Stroke"].SetUserValue(0xFFAA4488); fixture.Complete();
        check("image border color shares the screenshot color property", image.Style.Stroke == 0xFFAA4488);
        fixture.Window.PrepareForShow();
        check("changing image border width and color leaves its body position and size unchanged", fields["X"].Text == "0" && fields["Y"].Text == "0" &&
            fields["W"].Text == "50" && fields["H"].Text == "30");
        fields["X"].Text = "25"; fixture.Complete();
        check("image position editing writes the same body-based coordinate it displays", ElementGeometry.Bounds(image, includeStroke: false).Left == 25 && image.ImageBorderWidth == 3072);
        SaveInspector(fixture.Window, "type-properties-image-border.png");
    }

    private static void SaveInspector(InspectorWindow window, string filename)
    {
        string? output = Environment.GetEnvironmentVariable("FLOATCANVAS_TYPE_PROPERTIES_UI_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        using var surface = window.IsVisible ? null : new InspectorTestSurface(window);
        window.PrepareForShow(); window.UpdateLayout();
        var visual = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(visual.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(visual.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, filename)); encoder.Save(stream);
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private sealed class Fixture : IDisposable
    {
        internal readonly InspectorWindow Window;
        internal Fixture(EditorSession session, string folder)
        {
            Window = new(() => session, colorLibrary: new ColorLibrary(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json")));
            Window.PrepareForShow();
        }
        internal void Complete() => typeof(InspectorWindow).GetMethod("Apply", Private)!.Invoke(Window, null);
        public void Dispose() => Window.Close();
    }
}
