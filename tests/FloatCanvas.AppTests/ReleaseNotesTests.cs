using FloatCanvas.App;
using L = FloatCanvas.Localization.AppText;

internal static class ReleaseNotesTests
{
    internal const string Notes = """
        <!-- floatcanvas-notes:1 -->
        ### 简体中文
        <!-- lang:zh-CN -->
        #### 新功能
        <!-- category:feature -->
        - **独立画布。** 在自己的画布中编辑分组。
        #### 修复
        <!-- category:fix -->
        - 修复图片粘贴不可见。
        <!-- /lang -->
        ### English
        <!-- lang:en -->
        #### New features
        <!-- category:feature -->
        - **Independent canvases.** Edit groups on their own canvases.
        #### Fixes
        <!-- category:fix -->
        - Fix invisible pasted images.
        <!-- /lang -->
        """;

    internal static void Run(Action<string, bool> check)
    {
        string changelog = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "CHANGELOG.md"));
        string unreleased = changelog.Split("## [Unreleased]", StringSplitOptions.None)[1].Split("\n## [", StringSplitOptions.None)[0];
        foreach (string language in new[] { "zh-CN", "en" })
        {
            var actual = ReleaseNotes.Parse(new("0.4.0", unreleased), language);
            check(language + " shipped changelog source parses with all four categories", !actual.Unavailable &&
                actual.Entries.Select(e => e.Category).Distinct().SequenceEqual(ReleaseNotes.Categories.Take(4)) && actual.Entries.Count >= 4);
        }
        foreach (string language in new[] { "zh-CN", "en" })
        {
            var notes = ReleaseNotes.Parse(new("0.4.0", Notes), language);
            check(language + " explicitly selects one language and category", !notes.Unavailable &&
                notes.Entries.Count == 2 && notes.Entries[0].Category == "feature" && notes.Entries[1].Category == "fix" &&
                notes.Entries.All(e => e.Version == "0.4.0") &&
                (language == "en" ? !notes.Entries.Any(e => e.Text.Contains("画布")) : !notes.Entries.Any(e => e.Text.Contains("canvases"))));
            var legacy = ReleaseNotes.Parse(new("0.3.2", "- **中文。** 修复问题。\n- **English.** Fix a problem.\n\n- 中文第二条。\n- Another English entry."), language);
            check(language + " historical paired bullets remain single-language without invented categories",
                legacy.Entries.Count == 2 && legacy.Entries.All(e => e.Category == "legacy"));
        }
        foreach (string bad in new[] { "", Notes.Replace("notes:1", "notes:9"), Notes.Replace("lang:en", "lang:fr"),
            Notes.Replace("<!-- /lang -->", ""), Notes.Replace("category:feature", "category:unknown"), new string('x', 200_001) })
            check("unsupported or missing English notes are nonfatal", ReleaseNotes.Parse(new("0.4.0", bad), "en").Unavailable);
        check("missing legacy translation is explicit instead of displaying another language",
            ReleaseNotes.Parse(new("0.3.0", "- English only."), "zh-CN").Unavailable);
        var legacyHeading = ReleaseNotes.Parse(new("0.3.0", "## Changes / 改动\n- 中文条目。\n- English entry."), "zh-CN");
        check("legacy headings without blank separators cannot merge two languages",
            legacyHeading.Entries.Count == 1 && !legacyHeading.Entries[0].Text.Contains("English"));
        string oldLanguage = L.Preference;
        try
        {
            L.Initialize("en");
            string formatted = ReleaseNotes.Format([new("0.4.0", Notes), new("0.3.2", Notes), new("0.3.1", "")], "0.2.0", "0.4.0", true);
            check("groups combine versions while preserving every entry's origin", formatted.Contains("New features · 2") &&
                formatted.Contains("Fixes · 2") && formatted.Contains("**v0.4.0**") && formatted.Contains("**v0.3.2**") &&
                formatted.IndexOf("v0.4.0**", StringComparison.Ordinal) < formatted.IndexOf("v0.3.2**", StringComparison.Ordinal));
            check("history and translation gaps are both disclosed", formatted.Contains("incomplete") && formatted.Contains("v0.3.1") &&
                !formatted.Contains("独立画布"));
            check("current-version notes have their own heading", ReleaseNotes.Format([new("0.4.0", Notes)], "0.4.0", "0.4.0", false).Contains("current version"));
            string many = ReleaseNotes.Format(Enumerable.Range(0, 1001).Select(i => new ReleaseNote("0.4." + i, Notes)).ToArray(), "0.2.0", "0.4.1000", false);
            check("enormous history has a bounded document and an explicit full-history exit", many.Contains(L.Get("Updates.NotesDisplayLimited")) && many.Length < 500_000);
        }
        finally { L.Initialize(oldLanguage); }
    }
}
