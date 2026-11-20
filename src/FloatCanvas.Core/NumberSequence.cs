using System.Globalization;
using System.Text;

namespace FloatCanvas.Core;

public enum NumberFormat
{
    Decimal = 0, LeadingZero = 1, LowerLatin = 2, UpperLatin = 3, LowerRoman = 4, UpperRoman = 5,
    Chinese = 6, ChineseFinancial = 7, Custom = 8
}

/// <summary>Formats authored marker labels and advances the independently stored numeric counter.</summary>
public static class NumberSequence
{
    public const int MaximumLabelLength = 1024;
    public static bool IsSingleLineLabel(string? text) => text is { Length: <= MaximumLabelLength } &&
        !text.Any(ch => char.IsControl(ch) || ch is '\u2028' or '\u2029');

    public static bool IsValidMarker(CanvasElement element) => element.NumberFormat == NumberFormat.Decimal ||
        element.Kind == ElementKind.Number && Enum.IsDefined(element.NumberFormat) &&
        (element.NumberFormat != NumberFormat.Custom || IsSingleLineLabel(element.Text));

    private static readonly (int Value, string Symbol)[] romanDigits =
        [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
         (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];

    public static string Label(CanvasElement marker) =>
        marker.NumberFormat == NumberFormat.Custom ? marker.Text : Format(marker.Number, marker.NumberFormat);

    public static string Format(int value, NumberFormat format) => format switch
    {
        NumberFormat.LeadingZero => value.ToString("D2", CultureInfo.InvariantCulture),
        NumberFormat.LowerLatin or NumberFormat.UpperLatin when value > 0 => Latin(value, format == NumberFormat.UpperLatin),
        NumberFormat.LowerRoman or NumberFormat.UpperRoman when value is >= 1 and <= 3999 => Roman(value, format == NumberFormat.UpperRoman),
        NumberFormat.Chinese or NumberFormat.ChineseFinancial => Chinese(value, format == NumberFormat.ChineseFinancial),
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    public static void ChangeFormat(CanvasElement marker, NumberFormat format)
    {
        if (marker.NumberFormat == format) return;
        marker.Text = format == NumberFormat.Custom ? Label(marker) : string.Empty;
        marker.NumberFormat = format;
    }

    private static string Latin(int value, bool upper)
    {
        Span<char> buffer = stackalloc char[7]; // seven letters cover every positive Int32
        int start = buffer.Length;
        while (value > 0)
        {
            value--;
            buffer[--start] = (char)((upper ? 'A' : 'a') + value % 26);
            value /= 26;
        }
        return new string(buffer[start..]);
    }

    private static string Roman(int value, bool upper)
    {
        var text = new StringBuilder();
        foreach (var (amount, symbol) in romanDigits)
            while (value >= amount) { text.Append(symbol); value -= amount; }
        return upper ? text.ToString() : text.ToString().ToLowerInvariant();
    }

    private static string Chinese(int value, bool financial)
    {
        if (value == 0) return "零";
        long magnitude = Math.Abs((long)value);
        string digits = financial ? "零壹贰叁肆伍陆柒捌玖" : "零一二三四五六七八九";
        string units = financial ? "拾佰仟" : "十百千";
        var text = new StringBuilder(value < 0 ? "负" : "");
        bool started = false, emptyGroup = false;
        for (int divisor = 100_000_000; divisor > 0; divisor /= 10_000)
        {
            int group = (int)(magnitude / divisor % 10_000);
            if (group == 0) { emptyGroup = started; continue; }
            if (started && (emptyGroup || group < 1000)) text.Append('零');
            bool omitOne = !financial && !started && group is >= 10 and < 20;
            bool significant = false, zero = false;
            for (int place = 3, power = 1000; place >= 0; place--, power /= 10)
            {
                int digit = group / power % 10;
                if (digit == 0) { zero = significant; continue; }
                if (zero) text.Append('零');
                if (!(omitOne && place == 1)) text.Append(digits[digit]);
                if (place > 0) text.Append(units[place - 1]);
                significant = true; zero = false;
            }
            if (divisor == 100_000_000) text.Append('亿');
            else if (divisor == 10_000) text.Append('万');
            started = true; emptyGroup = false;
        }
        return text.ToString();
    }

    public static int Advance(int current, int marker, int step)
    {
        int next = (int)Math.Clamp((long)marker + step, int.MinValue, int.MaxValue);
        return step > 0 ? Math.Max(current, next) : step < 0 ? Math.Min(current, next) : current;
    }
}
