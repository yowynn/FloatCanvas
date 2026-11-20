namespace FloatCanvas.Core;

public readonly record struct ColorFamily(string Name, uint Ink, uint Tint, uint Letter, uint Outline, uint Border);

public static class BuiltInColors
{
    public const uint Red = 0xFFE03E3E, Yellow = 0xFFCB912F, Blue = 0xFF2383E2, PaleRed = 0xFFFFDCDC;
    private static ColorFamily Family(string name, uint ink, uint tint, uint letter, uint outline, uint? border = null) =>
        new(name, ink, tint, letter, outline, border ?? ink);

    public static readonly IReadOnlyList<ColorFamily> Families = Array.AsReadOnly<ColorFamily>(
    [
        Family("Black", 0xFF252525, 0xFFE4E4E4, 0xFF252525, 0xFFFFFFFF),
        Family("White", 0xFFFFFFFF, 0xFFFFFFFF, 0xFF252525, 0xFF252525, 0xFFAAB2BA),
        Family("Red", Red, PaleRed, 0xFF772625, 0xFFFFFFFF),
        Family("Brown", 0xFF9F6B53, 0xFFEADDD4, 0xFF503529, 0xFFFFFFFF),
        Family("Orange", 0xFFD9730D, 0xFFFFE5CC, 0xFF6A370D, 0xFF252525),
        Family("Yellow", Yellow, 0xFFFFF2BB, 0xFF604315, 0xFF252525),
        Family("Blue", Blue, 0xFFDCEEFF, 0xFF193F64, 0xFFFFFFFF),
        Family("Green", 0xFF448361, 0xFFDCEFE5, 0xFF244C37, 0xFFFFFFFF),
        Family("Purple", 0xFF9065B0, 0xFFEBDFF5, 0xFF513466, 0xFFFFFFFF),
        Family("Pink", 0xFFC14C8A, 0xFFF6DDEB, 0xFF6C284C, 0xFFFFFFFF)
    ]);

    // Half the distance to the nearest swatch gives each curated family a disjoint
    // neighborhood. At its edge the interpolated palette meets the custom formula.
    private static readonly double[] RadiusSquared = Families.Select(f => Families.Where(other => other.Ink != f.Ink)
        .Min(other => DistanceSquared(f.Ink, other.Ink)) / 4.0).ToArray();

    public static (uint Tint, uint Letter, uint Border) LabelColors(uint argb)
    {
        uint ink = argb | 0xFF000000;
        uint tint = Mix(ink, 0xFFFFFFFF, .84), letter = Mix(ink, 0xFF151D24, .58);
        for (int i = 0; i < Families.Count; i++)
        {
            var family = Families[i];
            int distance = DistanceSquared(ink, family.Ink);
            if (distance >= RadiusSquared[i]) continue;
            double t = 1 - Math.Sqrt(distance / RadiusSquared[i]);
            double weight = t * t * (3 - 2 * t);
            return (Mix(tint, family.Tint, weight), Mix(letter, family.Letter, weight),
                family.Border == family.Ink ? ink : Mix(ink, family.Border, weight));
        }
        return (tint, letter, ink);
    }

    // Used once when an author first selects the outline style.
    public static uint InitialTextOutline(uint argb)
    {
        uint ink = argb | 0xFF000000;
        foreach (var family in Families)
            if (family.Ink == ink) return (argb & 0xFF000000) | (family.Outline & 0x00FFFFFF);
        int luminance = 2126 * (byte)(ink >> 16) + 7152 * (byte)(ink >> 8) + 722 * (byte)ink;
        return (argb & 0xFF000000) | (luminance >= 1500000 ? 0x00252525u : 0x00FFFFFFu);
    }

    private static int DistanceSquared(uint a, uint b)
    {
        int r = (byte)(a >> 16) - (byte)(b >> 16), g = (byte)(a >> 8) - (byte)(b >> 8), blue = (byte)a - (byte)b;
        return r * r + g * g + blue * blue;
    }

    private static uint Mix(uint from, uint to, double amount)
    {
        uint Channel(int shift) => (uint)Math.Round((byte)(from >> shift) * (1 - amount) + (byte)(to >> shift) * amount);
        return 0xFF000000 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }
}
