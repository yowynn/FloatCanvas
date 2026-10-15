using System.Globalization;
using System.Text;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class LocalizationTests
{
    public static void Run(Action<string, bool> check)
    {
        var preference = L.Preference;
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            check("New settings default to automatic interface language", new UserSettings().Language == "auto");
            check("Older settings without language use automatic", JsonSerializer.Deserialize<UserSettings>("{}")!.Language == "auto");
            var settings = new UserSettings { Language = "en" };
            check("English preference round trips", JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!.Language == "en");
            check("Automatic Chinese uses Simplified Chinese", L.ResolveLanguage("auto", CultureInfo.GetCultureInfo("zh-CN")) == "zh-CN");
            check("Other Chinese display locales use Simplified Chinese", L.ResolveLanguage("auto", CultureInfo.GetCultureInfo("zh-TW")) == "zh-CN");
            check("Automatic English uses English", L.ResolveLanguage("auto", CultureInfo.GetCultureInfo("en-GB")) == "en");
            check("Unsupported display language falls back to English", L.ResolveLanguage("auto", CultureInfo.GetCultureInfo("ja-JP")) == "en");
            check("Explicit Chinese overrides Windows language", L.ResolveLanguage("zh-CN", CultureInfo.GetCultureInfo("en-US")) == "zh-CN");
            check("Explicit English overrides Windows language", L.ResolveLanguage("en", CultureInfo.GetCultureInfo("zh-CN")) == "en");
            check("Unknown preference follows automatic fallback", L.ResolveLanguage("unknown", CultureInfo.GetCultureInfo("de-DE")) == "en");

            var keys = L.Keys.ToArray();
            bool formatsValid = true;
            foreach (var key in keys)
            {
                var zh = CompositeFormat.Parse(L.Get(key, "zh-CN"));
                var en = CompositeFormat.Parse(L.Get(key, "en"));
                formatsValid &= zh.MinimumArgumentCount == en.MinimumArgumentCount;
            }
            check("Every embedded bilingual entry has matching valid format arguments", keys.Length > 0 && formatsValid);
            L.Initialize("en");
            check("English welcome is resolved at runtime", L.Get("Welcome.Title") == "FloatCanvas · Welcome");
            check("English formatted status substitutes actual values", L.Format("Image.Imported", 3) == "Imported 3 image(s).");
            var session = new EditorSession(new SceneDocument(), new AssetLibrary());
            var element = new CanvasElement { Kind = ElementKind.Text, Text = "Hello, 世界 · user content", Width = 100, Height = 40 };
            session.Add(element);
            check("Core default undo label uses the selected language", session.UndoLabel == L.Get("History.AddElement"));
            session.UpdateSelection(e => e.Width = 120, "用户自定 · custom label");
            check("Caller-provided history labels remain unchanged", session.UndoLabel == "用户自定 · custom label");
            // Language changes must not reinterpret or rewrite any document text.
            var before = JsonSerializer.Serialize(session.Document);
            L.Initialize("zh-CN");
            check("Chinese welcome is resolved at runtime", L.Get("Welcome.Title") == "浮绘 · 欢迎");
            check("Interface language does not modify canvas content", JsonSerializer.Serialize(session.Document) == before && session.Document.Elements.Single().Text == element.Text);
            check("Interface language does not alter numeric or Windows UI cultures", CultureInfo.CurrentCulture.Equals(culture) && CultureInfo.CurrentUICulture.Equals(uiCulture));
        }
        finally { L.Initialize(preference, uiCulture); }
    }
}
