using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class InspectorNumericRailTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var shape = new CanvasElement { Kind = ElementKind.Rectangle, Width = 2048, Height = 1536,
            CornerRadius = 100, Style = new() { StrokeWidth = 48 } };
        var image = new CanvasElement { Kind = ElementKind.Image, Width = 320, Height = 240, ImageBorderWidth = 24,
            Effects = [new() { Kind = ImageEffectKind.Blur, Amount = 96.125 }, new() { Kind = ImageEffectKind.Pixelate, Amount = .5 }] };
        var line = new CanvasElement { Kind = ElementKind.Line, Points = [new(0, 0), new(200, 100)],
            Style = new() { ArrowHeadSize = 96, StrokeWidth = 40 } };
        var text = new CanvasElement { Kind = ElementKind.Text, Text = "Range", Width = 400, Height = 300, Style = new() { FontSize = 256 } };
        var blur = new CanvasElement { Kind = ElementKind.LiveBlur, Width = 100, Height = 60,
            Effects = [new() { Kind = ImageEffectKind.Blur, Amount = .05 }] };
        var pixelate = new CanvasElement { Kind = ElementKind.LivePixelate, Width = 100, Height = 60,
            Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = .5 }] };
        var highlighter = new CanvasElement { Kind = ElementKind.Highlighter, Points = [new(0, 0), new(200, 100)], Style = new() { StrokeWidth = 128 } };
        var session = new EditorSession(new SceneDocument { Elements = [shape, image, line, text, blur, pixelate, highlighter] });
        var inspector = new InspectorWindow(() => session);
        var fields = Field<Dictionary<string, NumericValueField>>(inspector, "numericFields");
        var effects = Field<ComboBox>(inspector, "effectChoice");
        void Select(params CanvasElement[] selected)
        {
            inspector.CompletePendingEdit(); session.SetSelection(selected.Select(element => element.Id)); inspector.PrepareForShow();
        }
        bool Rail(string key, double minimum, double maximum) => fields[key].Slider.Minimum == minimum && fields[key].Slider.Maximum == maximum;
        try
        {
            string original = JsonSerializer.Serialize(session.Document);
            Select(shape);
            check("inspector geometry rails retain useful positions and avoid zero dimensions", Rail("X", -8192, 8192) && Rail("Y", -8192, 8192) && Rail("W", 1, 1000) && Rail("H", 1, 1000) && Rail("Angle", -180, 180));
            check("inspector shape rails use common stroke and corner ranges", Rail("StrokeWidth", 0, 32) && Rail("CornerRadius", 0, 64));
            check("inspector refresh preserves existing dimensions and style values beyond common rails", fields["W"].Value == 2048 && fields["H"].Value == 1536 && fields["StrokeWidth"].Value == 48 && fields["CornerRadius"].Value == 100 && fields["W"].Slider.Value == 1000);
            Select(highlighter);
            check("existing highlighter strokes use their broad common rail without clamping saved widths", Rail("StrokeWidth", 16, 64) && fields["StrokeWidth"].Value == 128 && fields["StrokeWidth"].Slider.Value == 64);
            Select(shape);
            check("leaving a highlighter restores the ordinary shape stroke rail", Rail("StrokeWidth", 0, 32) && fields["StrokeWidth"].Value == 48);
            Select(line);
            check("line selection uses the common arrow range without clamping its stored head size", Rail("ArrowHeadSize", 0, 64) && fields["ArrowHeadSize"].Value == 96 && fields["ArrowHeadSize"].Slider.Value == 64);
            Select(text);
            check("inspector font range preserves existing oversized text", Rail("FontSize", 8, 144) && fields["FontSize"].Value == 256);
            Select(image);
            check("image selection changes the stroke field to the narrower border rail", Rail("StrokeWidth", 0, 16) && fields["StrokeWidth"].Value == 24 && image.ImageBorderWidth == 24);
            check("image blur effects use the blur rail without clamping stored strength", Rail("EffectStrength", .1, 64) && fields["EffectStrength"].Value == 96.125 && fields["EffectStrength"].Slider.Value == 64);
            effects.SelectedValue = 1; inspector.PrepareForShow();
            check("choosing an image pixelate effect changes its rail without clamping sub-rail strength", Rail("EffectStrength", 1, 64) && fields["EffectStrength"].Value == .5 && fields["EffectStrength"].Slider.Value == 1);
            effects.SelectedValue = 0; inspector.PrepareForShow();
            check("choosing blur again restores its independent common rail and strength", Rail("EffectStrength", .1, 64) && fields["EffectStrength"].Value == 96.125);
            Select(blur);
            check("live blur preserves a valid saved strength below its new common rail", Rail("EffectStrength", .1, 64) && fields["EffectStrength"].Value == .05);
            Select(pixelate);
            check("live pixelate preserves a valid saved strength below its new common rail", Rail("EffectStrength", 1, 64) && fields["EffectStrength"].Value == .5);
            Select(shape, image);
            check("mixed image and shape selections use their intersected common border range", Rail("StrokeWidth", 0, 16));
            Select(shape);
            check("returning to ordinary shapes restores the wider stroke rail", Rail("StrokeWidth", 0, 32));
            check("all selection and effect rail changes leave the document and history untouched", JsonSerializer.Serialize(session.Document) == original && session.UndoCount == 0 && !session.IsTransactionActive);

            fields["StrokeWidth"].Input.Text = "48.125"; inspector.CompletePendingEdit(); inspector.PrepareForShow();
            check("typed ordinary stroke widths beyond the rail retain the original hard domain", session.Document.Elements.Single(element => element.Id == shape.Id).Style.StrokeWidth == 48.125 && fields["StrokeWidth"].Value == 48.125 && fields["StrokeWidth"].Slider.Value == 32 && session.UndoCount == 1);
            Select(image);
            fields["StrokeWidth"].Input.Text = "48.125"; inspector.CompletePendingEdit(); inspector.PrepareForShow();
            check("typed image borders beyond the rail retain the original hard domain", session.Document.Elements.Single(element => element.Id == image.Id).ImageBorderWidth == 48.125 && fields["StrokeWidth"].Value == 48.125 && fields["StrokeWidth"].Slider.Value == 16 && session.UndoCount == 2);
            effects.SelectedValue = 1; inspector.PrepareForShow();
            fields["EffectStrength"].Input.Text = "0.25"; inspector.CompletePendingEdit(); inspector.PrepareForShow();
            check("typed pixelate strength below its rail retains the original hard domain", session.Document.Elements.Single(element => element.Id == image.Id).Effects[1].Amount == .25 && fields["EffectStrength"].Value == .25 && fields["EffectStrength"].Slider.Value == 1 && session.UndoCount == 3);
            Select(shape);
            fields["W"].Input.Text = "0.5"; inspector.CompletePendingEdit(); inspector.PrepareForShow();
            check("typed dimensions below the new rail retain the original fractional domain", fields["W"].Value == .5 && fields["W"].Slider.Value == 1 && session.Document.Elements.Single(element => element.Id == shape.Id).Width == .5 && session.UndoCount == 4);

            check("inspector elevation and fixed semantic rails use their intended common ranges", Rail("BillboardHeight", -1000, 1000) && Rail("Opacity", 0, 100) && Rail("Number", 1, 100));
            check("inspector rail integration checks create no native window", !inspector.IsVisible && new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0);
        }
        finally { inspector.CompletePendingEdit(); inspector.Close(); }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
}
