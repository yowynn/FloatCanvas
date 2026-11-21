using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class TextStyleRenderingTests
{
    internal static void Run(SceneRenderer renderer, Action<string, bool> check, string? output)
    {
        CheckOutlineWidths(renderer, check);
        var variants = AppearanceRegistry.VariantsFor(ElementKind.Text);
        var sample = new CanvasElement { Kind = ElementKind.Text, Text = "集合点", Style = new() { FontSize = 40, PrimaryColor = BuiltInColors.Blue } };
        var scene = new SceneDocument { Viewport = new(0, 0, 230, 100), Elements = [sample] };
        using var sheet = new SKBitmap(690, 200);
        using var canvas = new SKCanvas(sheet);
        using var background = new SKPaint();
        int column = 0;
        foreach (var variant in variants)
        {
            ElementAppearance.SetVariant(sample, variant.Id);
            var metrics = renderer.MeasureText(sample.Text, sample.Style);
            sample.Width = Math.Ceiling(metrics.Width) + 1; sample.Height = Math.Ceiling(metrics.Height);
            sample.Transform = Affine2D.Translation(Math.Round((230 - sample.Width) / 2), Math.Round((100 - sample.Height) / 2));
            using var pixels = Render();
            bool Has(uint color) => pixels.Pixels.Contains(new SKColor(color));
            if (variant.Id == ElementAppearance.TextLabel)
                check("blue label uses the example's dark letters, pale fill and blue border",
                    Has(0xFF193F64) && Has(0xFFDCEEFF) && Has(BuiltInColors.Blue));
            else check("text ink and outline match " + variant.Id, Has(BuiltInColors.Blue) && Has(0xFFFFFFFF) == (variant.Id == ElementAppearance.TextOutline));
            for (int row = 0; row < 2; row++)
            {
                var cell = SKRect.Create(column * 230, row * 100, 230, 100);
                background.Color = row == 0 ? new(0xFFF8FAFC) : new(0xFF282E37);
                canvas.DrawRect(cell, background);
                using var image = SKImage.FromBitmap(pixels);
                canvas.DrawImage(image, cell.Left, cell.Top, SKSamplingOptions.Default);
            }
            if (variant.Id == ElementAppearance.TextOutline)
            {
                sample.Style.SecondaryColor = 0xFFFF0000;
                sample.Style.PrimaryColor = 0x002383E2;
                using var hollow = Render();
                check("transparent glyph ink keeps the separately chosen red outline",
                    hollow.Pixels.Any(c => c.Alpha > 240 && c.Red > 240 && c.Blue < 10) &&
                    hollow.Pixels.Zip(pixels.Pixels).Any(pair => pair.First.Alpha == 0 && pair.Second == new SKColor(BuiltInColors.Blue)));
                sample.Style.PrimaryColor = BuiltInColors.Blue;
                sample.Style.SecondaryColor = 0;
                using var inkOnly = Render();
                check("transparent outline keeps the glyph alone", inkOnly.Pixels.Any(c => c == new SKColor(BuiltInColors.Blue)) &&
                    !inkOnly.Pixels.Any(c => c.Alpha > 0 && c.Red > c.Blue));
                sample.Style.SecondaryColor = 0x80FFFFFF;
            }
            sample.Style.PrimaryColor = 0x802383E2;
            using var faded = Render();
            check(variant.Id + " fades the whole appearance including shared edges", faded.Pixels.Max(color => color.Alpha) == 128);
            sample.Style.PrimaryColor = 0x002383E2;
            sample.Style.SecondaryColor = 0;
            using var clear = Render();
            check(variant.Id + " supports fully transparent ink", clear.Pixels.All(color => color.Alpha == 0));
            sample.Style.PrimaryColor = BuiltInColors.Blue;
            var editing = renderer.TextEditingGeometry(sample.Text, sample.Style, sample.Width);
            var caret = editing.Caret(0);
            check(variant.Id + " renders and edits the same padded content box", editing.HitTestPosition(new(caret.X, caret.Y + 1)).Index == 0 &&
                (caret.X > 0 && caret.Y > 0) == (variant.Id != ElementAppearance.Default) && caret.Bottom <= sample.Height);
            column++;
        }
        static int Delta(uint a, uint b) => Math.Max(Math.Abs((byte)(a >> 16) - (byte)(b >> 16)),
            Math.Max(Math.Abs((byte)(a >> 8) - (byte)(b >> 8)), Math.Abs((byte)a - (byte)b)));
        static int PaletteDelta((uint Tint, uint Letter, uint Border) a, (uint Tint, uint Letter, uint Border) b) =>
            Math.Max(Delta(a.Tint, b.Tint), Math.Max(Delta(a.Letter, b.Letter), Delta(a.Border, b.Border)));
        foreach (var family in BuiltInColors.Families)
        {
            var exact = BuiltInColors.LabelColors(family.Ink);
            check(family.Name + " keeps the curated swatch exactly", exact == (family.Tint, family.Letter, family.Border));
            foreach (int shift in new[] { 0, 8, 16 })
            foreach (int step in new[] { -1, 1 })
            {
                int channel = (byte)(family.Ink >> shift) + step;
                if (channel is < 0 or > 255) continue;
                uint nearby = (family.Ink & ~(255u << shift)) | ((uint)channel << shift);
                check("one RGB step near " + family.Name + " changes the label continuously", PaletteDelta(exact, BuiltInColors.LabelColors(nearby)) <= 2);
            }
        }
        var last = BuiltInColors.LabelColors(0);
        int largestStep = 0;
        for (uint gray = 1; gray <= 255; gray++)
        {
            var next = BuiltInColors.LabelColors(gray * 0x010101);
            largestStep = Math.Max(largestStep, PaletteDelta(last, next)); last = next;
        }
        check("all 256 grayscale levels transition smoothly, including the old near-white border cutoff and pure white", largestStep <= 5);
        sample.Style.Variant = ElementAppearance.TextLabel;
        foreach (uint ink in new[] { 0xFFE0E0E0u, 0xFFE1E1E1u, 0xFFFEFEFEu, 0xFFFFFFFFu })
        {
            sample.Style.PrimaryColor = ink;
            using var swatch = Render();
            var colors = BuiltInColors.LabelColors(ink);
            check("rendered label uses the continuous letter color at " + ink.ToString("X8"), swatch.Pixels.Contains(new SKColor(colors.Letter)));
        }
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            using var image = SKImage.FromBitmap(sheet);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(output, "text-styles.png"), png.ToArray());
        }
        SKBitmap Render() => SKBitmap.Decode(renderer.ExportPng(scene, new(), transparent: true));
    }

    private static void CheckOutlineWidths(SceneRenderer renderer, Action<string, bool> check)
    {
        var text = new CanvasElement { Kind = ElementKind.Text, Text = "O", Width = 110, Height = 90,
            Style = new() { FontSize = 36, Variant = ElementAppearance.TextOutline, PrimaryColor = 0, SecondaryColor = 0x80FF0000 } };
        var scene = new SceneDocument { Viewport = new(0, 0, 110, 90), Elements = [text] };
        var style = text.Style;
        double baseWidth = renderer.MeasureText("O", new ElementStyle { FontSize = 36 }).Width;
        int previous = -1;
        foreach (double width in new[] { 0d, 1d, 5d })
        {
            style.TextOutlineWidth = width;
            var metrics = renderer.MeasureText("O", style);
            var caret = renderer.TextEditingGeometry("O", style, text.Width).Caret(0);
            using var bitmap = SKBitmap.Decode(renderer.ExportPng(scene, new(), transparent: true));
            int ink = bitmap.Pixels.Count(p => p.Alpha > 0);
            check($"{width}px text outline updates metrics, caret and actual translucent ink together",
                Math.Abs(metrics.Width - baseWidth - 2 * width) < .001 && caret.X == width && caret.Top == width &&
                caret.Bottom <= metrics.Height && ink > previous && (width == 0 ? ink == 0 : bitmap.Pixels.Max(p => p.Alpha) == 128));
            previous = ink;
        }
        style.TextOutlineWidth = null;
        check("the default text outline follows the font size", renderer.TextEditingGeometry("O", style, text.Width).Caret(0).Top is > 1.979 and < 1.981);
        style.TextOutlineWidth = 1;
        check("returning to a previous outline width also restores its cached glyph placement", renderer.TextEditingGeometry("O", style, text.Width).Caret(0).Top == 1);
    }
}
