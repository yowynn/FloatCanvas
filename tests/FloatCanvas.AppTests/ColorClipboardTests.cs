using System.Globalization;
using FloatCanvas.App;

internal static class ColorClipboardTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("color clipboard offers six distinct tagged formats", ColorClipboardText.Formats.Count == 6 &&
            ColorClipboardText.Formats.Distinct().Count() == 6 &&
            ColorClipboardText.Formats.Order().SequenceEqual(Enum.GetValues<ColorClipboardFormat>().Order()));
        check("color clipboard hex formats place alpha first only in the eight-digit form",
            ColorClipboardText.Format(0x80402010, ColorClipboardFormat.HexRgb) == "#402010" &&
            ColorClipboardText.Format(0x80402010, ColorClipboardFormat.HexArgb) == "#80402010");
        check("color clipboard RGB format identifies channel order explicitly",
            ColorClipboardText.Format(0x80402010, ColorClipboardFormat.Rgb) == "rgb(64, 32, 16)");
        var examples = new (string Text, uint Expected)[]
        {
            ("#112233", 0x5A112233), ("#0F8", 0x5A00FF88), ("#7f112233", 0x7F112233),
            ("#11223344", 0x11223344), ("  RGB(255, 0, 127)  ", 0x5AFF007F),
            ("rgb(1,2,3)", 0x5A010203), ("rgba(255, 128, 0, 0)", 0x00FF8000),
            ("rgba(1, 2, 3, 1)", 0xFF010203), ("rgba(1, 2, 3, 0.5)", 0x80010203),
            ("hsl(0, 100%, 50%)", 0x5AFF0000), ("hsl(60, 100%, 50%)", 0x5AFFFF00),
            ("hsl(120, 100%, 50%)", 0x5A00FF00), ("hsl(180, 100%, 50%)", 0x5A00FFFF),
            ("hsl(240, 100%, 50%)", 0x5A0000FF), ("hsl(300, 100%, 50%)", 0x5AFF00FF),
            ("hsl(360, 100%, 50%)", 0x5AFF0000), ("HSL(270, 50%, 40%)", 0x5A663399),
            ("hsl(0, 0%, 50%)", 0x5A808080), ("hsla(0, 100%, 50%, 0.5)", 0x80FF0000),
            ("hsla(120, 100%, 50%, 0)", 0x0000FF00), ("hsla(240, 100%, 50%, 1)", 0xFF0000FF)
        };
        foreach (var (text, expected) in examples)
            check("color clipboard parses an unambiguous value: " + text,
                ColorClipboardText.TryParse(text, 0x5AAABBCC, out uint parsed) && parsed == expected);

        foreach (string? text in new string?[]
        {
            null, "", " ", "red", "112233", "12345678", "0x11223344", "1, 2, 3", "(1, 2, 3)",
            "#1234", "#12345", "#1234567", "#123456789", "#GG2233", "##112233", "#11 2233",
            "rgb()", "rgb(1,2)", "rgb(1,2,3,4)", "rgb(256,2,3)", "rgb(-1,2,3)",
            "rgb(1.5,2,3)", "rgb(10%,20%,30%)", "rgb(1 2 3)", "rgb(1,,2,3)",
            "rgba(1,2,3)", "rgba(1,2,3,4,5)", "rgba(1,2,3,-0.01)", "rgba(1,2,3,1.01)",
            "rgba(1,2,3,50%)", "rgba(1,2,3,NaN)", "rgba(1,2,3,Infinity)",
            "hsl(-1,100%,50%)", "hsl(361,100%,50%)", "hsl(0,101%,50%)", "hsl(0,100%,-1%)",
            "hsl(0,100%,101%)", "hsl(0,100,50)", "hsl(NaN,100%,50%)", "hsl(0,Infinity%,50%)",
            "hsla(0,100%,50%)", "hsla(0,100%,50%,2)", "hsla(0,100%,50%,NaN)",
            "rgb(1,2,3) trailing", "prefix rgb(1,2,3)", "#112233 trailing", "rgb(1,2,3);",
            "rgb(1,2,3) rgb(4,5,6)", "rgba(1,2,3,0,5)", new string(' ', 257) + "#112233"
        })
            check("color clipboard rejects malformed or ambiguous text: " + (text is null ? "<null>" : text.Length > 80 ? "<over limit>" : text),
                !ColorClipboardText.TryParse(text, 0x5AAABBCC, out _));

        uint[] colors = [0, 0xFFFFFFFF, 0x00010203, 0xFF000000, 0x00FF0000, 0xFF00FF00,
            0xFF0000FF, 0x7FFFFFFF, 0x80112233, 0xFE0100FF, 0x01333333, 0x01000100];
        var random = new Random(20261030);
        var samples = colors.Concat(Enumerable.Range(0, 8192).Select(_ => (uint)random.NextInt64(0x100000000L)))
            .Concat(Enumerable.Range(0, 256).SelectMany(channel => new uint[]
            {
                (uint)channel << 24 | 0x00553B97,
                0xA5000000 | (uint)channel << 16 | (uint)channel << 8 | (uint)channel,
                0x5A000000 | (uint)channel << 16 | 0x000100,
                0x5AFF0000 | (uint)channel << 8 | 0xFF,
                0x5A010100 | (uint)channel
            })).ToArray();
        foreach (var format in ColorClipboardText.Formats)
        {
            uint? failure = null;
            bool carriesAlpha = format is ColorClipboardFormat.HexArgb or ColorClipboardFormat.Rgba or ColorClipboardFormat.Hsla;
            foreach (uint color in samples)
            {
                uint expected = carriesAlpha ? color : (color & 0xFFFFFF) | 0x3C000000;
                if (ColorClipboardText.TryParse(ColorClipboardText.Format(color, format), 0x3CABCDEF, out uint parsed) && parsed == expected) continue;
                failure = color; break;
            }
            check("color clipboard " + format + " roundtrips byte channels and " + (carriesAlpha ? "alpha" : "retains destination alpha") +
                " across " + samples.Length + " boundary and sampled colors" + (failure is uint failed ? " (failed #" + failed.ToString("X8") + ")" : ""), failure is null);
        }

        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                check(name + " color clipboard uses invariant numeric formatting and parsing", ColorClipboardText.Formats.All(format =>
                    ColorClipboardText.TryParse(ColorClipboardText.Format(0x80402010, format), 0x80ABCDEF, out uint color) && color == 0x80402010) &&
                    ColorClipboardText.TryParse("RGBA(64,32,16,0.5)", 0, out uint rgba) && rgba == 0x80402010);
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
}
