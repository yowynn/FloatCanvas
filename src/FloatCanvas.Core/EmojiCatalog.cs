using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;

namespace FloatCanvas.Core;

public enum EmojiGroup { Smileys, People, Nature, Food, Travel, Activities, Objects, Symbols, Flags }

public sealed class EmojiEntry
{
    public string Glyph { get; }
    public EmojiGroup Group { get; }
    public string ChineseName { get; }
    public string EnglishName { get; }
    internal string SearchText { get; }
    internal string Family { get; }
    internal int Tones { get; }
    internal EmojiEntry(string[] fields)
    {
        Glyph = fields[0]; Group = (EmojiGroup)int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
        ChineseName = fields[2]; EnglishName = fields[3]; SearchText = string.Join(' ', fields, 2, 4);
        var family = new StringBuilder();
        foreach (var rune in Glyph.EnumerateRunes())
        {
            if (rune.Value is >= 0x1f3fb and <= 0x1f3ff) Tones |= 1 << (rune.Value - 0x1f3fb);
            else if (rune.Value != 0xfe0f) family.Append(rune.ToString());
        }
        Family = family.ToString();
    }
}

/// <summary>Qualified emoji are displayed once. The official alternate input spellings resolve to the same entry.</summary>
public static class EmojiCatalog
{
    private sealed class Catalog
    {
        public readonly List<EmojiEntry> Entries = [];
        public readonly Dictionary<string, EmojiEntry> Lookup = new(StringComparer.Ordinal);
        public readonly HashSet<string> ToneFamilies = new(StringComparer.Ordinal);
        public Catalog()
        {
            using var resource = typeof(EmojiCatalog).Assembly.GetManifestResourceStream("FloatCanvas.EmojiCatalog")!;
            using var gzip = new GZipStream(resource, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                var fields = line.Split('\t');
                var entry = new EmojiEntry(fields);
                Entries.Add(entry); Lookup.Add(entry.Glyph, entry);
                if (entry.Tones != 0) ToneFamilies.Add(entry.Family);
                foreach (var alias in fields[6].Split(' ', StringSplitOptions.RemoveEmptyEntries)) Lookup.Add(alias, entry);
            }
        }
    }
    private static readonly Lazy<Catalog> Data = new(() => new());
    public static IReadOnlyList<EmojiEntry> Entries => Data.Value.Entries.AsReadOnly();
    public static bool TryFind(string? input, [NotNullWhen(true)] out EmojiEntry? entry)
    {
        entry = null;
        return input is { Length: > 0 and <= 32 } && Data.Value.Lookup.TryGetValue(input, out entry);
    }

    // Tone 0 shows unmodified emoji, 1..5 select a skin tone, and -1 includes every variation.
    public static IReadOnlyList<EmojiEntry> Search(string query, EmojiGroup group, int tone)
    {
        query = query.Trim();
        if (TryFind(query, out var exact)) return [exact];
        var data = Data.Value;
        var terms = query.Replace('_', ' ').Trim(':').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (query.Length != 0 && terms.Length == 0) return [];
        IEnumerable<EmojiEntry> entries = data.Entries;
        if (terms.Length == 0) entries = entries.Where(e => e.Group == group);
        if (tone == 0 && terms.Length == 0) entries = entries.Where(e => e.Tones == 0);
        if (tone is >= 1 and <= 5) entries = entries.Where(e => (e.Tones & (1 << (tone - 1))) != 0 || !data.ToneFamilies.Contains(e.Family));
        if (terms.Length > 0)
        {
            string phrase = string.Join(' ', terms);
            entries = entries.Where(e => terms.All(t => e.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(e => MatchRank(e, phrase)).ThenBy(e => e.Tones != 0);
        }
        return entries.ToArray();
    }

    private static int MatchRank(EmojiEntry entry, string phrase)
    {
        if (entry.ChineseName.Equals(phrase, StringComparison.OrdinalIgnoreCase) || entry.EnglishName.Equals(phrase, StringComparison.OrdinalIgnoreCase)) return 0;
        if (entry.ChineseName.StartsWith(phrase, StringComparison.OrdinalIgnoreCase) || entry.EnglishName.StartsWith(phrase, StringComparison.OrdinalIgnoreCase)) return 1;
        return entry.ChineseName.Contains(phrase, StringComparison.OrdinalIgnoreCase) || entry.EnglishName.Contains(phrase, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }
}
