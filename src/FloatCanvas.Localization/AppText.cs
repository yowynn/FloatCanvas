using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace FloatCanvas.Localization;

/// <summary>Application-owned text only. Never changes user content, numeric culture, or input methods.</summary>
public static class AppText
{
    private sealed record Translation(string Zh, string En);
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;
    private static readonly Lazy<FrozenDictionary<string, Translation>> Catalog = new(LoadCatalog);
    public static string Preference { get; private set; } = "auto";
    public static string CurrentLanguage { get; private set; } = ResolveLanguage("auto", SystemUiCulture);
    public static IEnumerable<string> Keys => Catalog.Value.Keys;

    public static string NormalizePreference(string? preference) => preference?.ToLowerInvariant() switch
    {
        "zh-cn" => "zh-CN",
        "en" => "en",
        _ => "auto"
    };

    public static string ResolveLanguage(string? preference, CultureInfo systemUiCulture) => NormalizePreference(preference) switch
    {
        "zh-CN" => "zh-CN",
        "en" => "en",
        _ => systemUiCulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en"
    };

    public static void Initialize(string? preference, CultureInfo? systemUiCulture = null)
    {
        Preference = NormalizePreference(preference);
        CurrentLanguage = ResolveLanguage(Preference, systemUiCulture ?? SystemUiCulture);
    }

    public static string Get(string key) => Get(key, CurrentLanguage);

    public static string Get(string key, string language)
    {
        if (!Catalog.Value.TryGetValue(key, out var value)) throw new KeyNotFoundException($"Unknown localized string: {key}");
        return language == "zh-CN" ? value.Zh : value.En;
    }

    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    private static FrozenDictionary<string, Translation> LoadCatalog()
    {
        var assembly = typeof(AppText).Assembly;
        var entries = new Dictionary<string, Translation>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var document = JsonDocument.Parse(stream);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var zh = property.Value.GetProperty("zh").GetString();
                var en = property.Value.GetProperty("en").GetString();
                if (string.IsNullOrWhiteSpace(zh) || string.IsNullOrWhiteSpace(en))
                    throw new InvalidDataException($"Both translations are required: {property.Name}");
                if (!entries.TryAdd(property.Name, new(zh, en)))
                    throw new InvalidDataException($"Duplicate localized string: {property.Name}");
            }
        }
        if (entries.Count == 0) throw new InvalidDataException("The application text catalog is empty.");
        return entries.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
