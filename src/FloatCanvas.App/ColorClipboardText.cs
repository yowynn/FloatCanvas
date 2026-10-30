using System.Globalization;

namespace FloatCanvas.App;

internal enum ColorClipboardFormat { HexRgb, HexArgb, Rgb, Rgba, Hsl, Hsla }

/// <summary>Explicit color syntaxes: eight-digit hex is always alpha-first ARGB.</summary>
internal static class ColorClipboardText
{
    internal static IReadOnlyList<ColorClipboardFormat> Formats { get; } = Array.AsReadOnly(Enum.GetValues<ColorClipboardFormat>());

    internal static string Format(uint color, ColorClipboardFormat format)
    {
        int red = (int)((color >> 16) & 255), green = (int)((color >> 8) & 255), blue = (int)(color & 255);
        string alpha = Number((color >> 24) / 255d, "0.########");
        switch (format)
        {
            case ColorClipboardFormat.HexRgb: return "#" + (color & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
            case ColorClipboardFormat.HexArgb: return "#" + color.ToString("X8", CultureInfo.InvariantCulture);
            case ColorClipboardFormat.Rgb: return FormattableString.Invariant($"rgb({red}, {green}, {blue})");
            case ColorClipboardFormat.Rgba: return FormattableString.Invariant($"rgba({red}, {green}, {blue}, {alpha})");
            case ColorClipboardFormat.Hsl:
            case ColorClipboardFormat.Hsla:
                var (hue, saturation, lightness) = ToHsl(red, green, blue);
                string channels = $"{Number(hue)}, {Number(saturation)}%, {Number(lightness)}%";
                return format == ColorClipboardFormat.Hsl ? $"hsl({channels})" : $"hsla({channels}, {alpha})";
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    internal static bool TryParse(string? text, uint currentColor, out uint color)
    {
        color = currentColor;
        if (text is null || text.Length > 256) return false;
        string input = text.Trim();
        if (input.StartsWith('#'))
        {
            string digits = input[1..];
            if (digits.Length == 3) digits = string.Concat(digits.Select(character => new string(character, 2)));
            if (digits.Length is not (6 or 8) || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint parsed)) return false;
            color = digits.Length == 8 ? parsed : (currentColor & 0xFF000000) | parsed;
            return true;
        }

        int open = input.IndexOf('(');
        if (open <= 0 || !input.EndsWith(')')) return false;
        string name = input[..open].Trim().ToLowerInvariant();
        bool rgb = name is "rgb" or "rgba", hsl = name is "hsl" or "hsla";
        if (!rgb && !hsl) return false;
        bool hasAlpha = name is "rgba" or "hsla";
        string[] channels = input[(open + 1)..^1].Split(',');
        if (channels.Length != (hasAlpha ? 4 : 3)) return false;
        uint alpha = currentColor >> 24;
        if (hasAlpha)
        {
            if (!TryNumber(channels[3], 1, out double parsedAlpha)) return false;
            alpha = (uint)Math.Round(parsedAlpha * 255);
        }
        uint red, green, blue;
        if (rgb)
        {
            if (!TryByte(channels[0], out red) || !TryByte(channels[1], out green) || !TryByte(channels[2], out blue)) return false;
        }
        else
        {
            if (!TryNumber(channels[0], 360, out double hue) || !TryPercent(channels[1], out double saturation) || !TryPercent(channels[2], out double lightness)) return false;
            (red, green, blue) = FromHsl(hue, saturation / 100, lightness / 100);
        }
        color = (alpha << 24) | (red << 16) | (green << 8) | blue;
        return true;
    }

    private static bool TryByte(string text, out uint value) => uint.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= 255;

    private static bool TryNumber(string text, double maximum, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value >= 0 && value <= maximum;

    private static bool TryPercent(string text, out double value)
    {
        string input = text.Trim(); value = 0;
        return input.EndsWith('%') && TryNumber(input[..^1], 100, out value);
    }

    private static string Number(double value, string format = "0.######") => value.ToString(format, CultureInfo.InvariantCulture);

    private static (double Hue, double Saturation, double Lightness) ToHsl(int red, int green, int blue)
    {
        double r = red / 255d, g = green / 255d, b = blue / 255d;
        double maximum = Math.Max(r, Math.Max(g, b)), minimum = Math.Min(r, Math.Min(g, b));
        double delta = maximum - minimum, lightness = (maximum + minimum) / 2;
        if (delta == 0) return (0, 0, lightness * 100);
        double hue = maximum == r ? (g - b) / delta : maximum == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        hue *= 60; if (hue < 0) hue += 360;
        return (hue, delta / (1 - Math.Abs(2 * lightness - 1)) * 100, lightness * 100);
    }

    private static (uint Red, uint Green, uint Blue) FromHsl(double hue, double saturation, double lightness)
    {
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double sector = hue / 60, secondary = chroma * (1 - Math.Abs(sector % 2 - 1)), offset = lightness - chroma / 2;
        var (red, green, blue) = sector switch
        {
            < 1 => (chroma, secondary, 0d), < 2 => (secondary, chroma, 0d),
            < 3 => (0d, chroma, secondary), < 4 => (0d, secondary, chroma),
            < 5 => (secondary, 0d, chroma), _ => (chroma, 0d, secondary)
        };
        uint Byte(double channel) => (uint)Math.Clamp(Math.Round((channel + offset) * 255), 0, 255);
        return (Byte(red), Byte(green), Byte(blue));
    }
}
