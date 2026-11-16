using FloatCanvas.Core;

namespace FloatCanvas.Rendering;

/// <summary>Natural keyboard presentation dimensions before optional horizontal stretching.</summary>
public static class PresentationLayout
{
    public const double LogWidthPerFontSize = 24;
    private static readonly Dictionary<int, double> KeyWidths = PresentationKeyboardLayout.CreateDefault().Keys
        .GroupBy(key => key.VirtualKey).ToDictionary(group => group.Key, group => Math.Max(1, group.First().Width));

    public static double KeycapWidthUnits(int virtualKey) => virtualKey switch
    {
        0x20 => 2.25, // Space is deliberately shorter than a physical spacebar.
        0x10 or 0xA0 or 0xA1 => 2.25,
        0x11 or 0xA2 or 0xA3 or 0x12 or 0xA4 or 0xA5 or 0x5B or 0x5C => 1.25,
        _ => KeyWidths.GetValueOrDefault(virtualKey, 1)
    };
    public static PointD NaturalSize(PresentationSettings settings, ElementStyle style, SceneRenderer renderer)
    {
        double font = style.FontSize;
        double pad = font * .35;
        if (settings.DisplayStyle == PresentationDisplayStyle.Keyboard)
        {
            var keys = (settings.KeyboardLayout ?? PresentationKeyboardLayout.CreateDefault()).Keys.Where(key => key.Enabled).ToArray();
            if (keys.Length == 0) return new(Math.Max(1, font * 3), Math.Max(1, font));
            double unit = font / .36;
            return new((keys.Max(key => key.X + key.Width) - keys.Min(key => key.X)) * unit + 2 * pad,
                (keys.Max(key => key.Y + key.Height) - keys.Min(key => key.Y)) * unit + 2 * pad);
        }
        var referenceStyle = style.Clone(); referenceStyle.FontSize = 22;
        double line = renderer.MeasureText("Ag", referenceStyle, 1_000_000).Height * font / 22 + 2 * pad;
        if (settings.DisplayStyle == PresentationDisplayStyle.Keycaps) line += font * 3 / 22;
        double gap = pad * (settings.DisplayStyle == PresentationDisplayStyle.Keycaps ? .6 : .5);
        double height = 2 * pad + settings.RecentChordLimit * line + (settings.RecentChordLimit - 1) * gap;
        return new(font * LogWidthPerFontSize, height);
    }
}
