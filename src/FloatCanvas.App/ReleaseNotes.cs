using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed record ReleaseNote(string Version, string Body);
internal sealed record ReleaseNoteEntry(string Category, string Text, string Version);
internal sealed record ParsedReleaseNotes(IReadOnlyList<ReleaseNoteEntry> Entries, bool Unavailable);

/// <summary>Optional presentation metadata. Invalid notes must never invalidate an update package.</summary>
internal static class ReleaseNotes
{
    internal const string FormatMarker = "<!-- floatcanvas-notes:1 -->";
    internal static readonly string[] Categories = ["feature", "improvement", "fix", "compatibility", "legacy"];
    private const int MaxBodyLength = 200_000, MaxEntries = 1000;

    internal static ParsedReleaseNotes Parse(ReleaseNote release, string language)
    {
        if (string.IsNullOrWhiteSpace(release.Body) || release.Body.Length > MaxBodyLength) return new([], true);
        string body = release.Body.Replace("\r\n", "\n");
        if (!body.Contains("<!-- floatcanvas-notes:", StringComparison.Ordinal))
            return Legacy(release.Version, body, language);
        if (!body.Contains(FormatMarker, StringComparison.Ordinal)) return new([], true);
        string marker = $"<!-- lang:{(language == "zh-CN" ? "zh-CN" : "en")} -->";
        int start = body.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0 || body.IndexOf(marker, start + marker.Length, StringComparison.Ordinal) >= 0) return new([], true);
        start += marker.Length;
        int end = body.IndexOf("<!-- /lang -->", start, StringComparison.Ordinal);
        if (end < 0) return new([], true);
        var entries = new List<ReleaseNoteEntry>();
        string? category = null;
        foreach (string raw in body[start..end].Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("<!-- category:", StringComparison.Ordinal) && line.EndsWith(" -->", StringComparison.Ordinal))
            {
                category = line[14..^4];
                if (!Categories.Take(4).Contains(category, StringComparer.Ordinal)) return new([], true);
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) && category is not null && line.Length > 2)
            {
                entries.Add(new(category, line[2..], release.Version));
                if (entries.Count > MaxEntries) return new([], true);
            }
            else return new([], true);
        }
        return new(entries, entries.Count == 0);
    }

    private static ParsedReleaseNotes Legacy(string version, string body, string language)
    {
        // Historical FloatCanvas notes used paired Chinese/English paragraphs.
        // No inferred categories or machine translations: published wording stays intact.
        var lines = body.Split('\n').Select(p => p.Trim())
            .Where(p => p.Length > 0 && !p.StartsWith('#')).ToArray();
        var selected = lines.Where(p => HasChinese(p) == (language == "zh-CN"))
            .Where(p => !p.StartsWith("<!--", StringComparison.Ordinal))
            .Take(MaxEntries + 1).Select(p => new ReleaseNoteEntry("legacy", p.StartsWith("- ", StringComparison.Ordinal) ? p[2..] : p, version)).ToArray();
        if (selected.Length > MaxEntries) return new([], true);
        return new(selected, selected.Length == 0);
    }

    private static bool HasChinese(string text) => text.Any(c => c is >= '\u3400' and <= '\u9fff');

    internal static string Format(IReadOnlyList<ReleaseNote> releases, string currentVersion, string targetVersion, bool incomplete)
    {
        var entries = new List<ReleaseNoteEntry>();
        var missing = new List<string>();
        int characters = 0;
        bool limited = false;
        foreach (var release in releases)
        {
            var parsed = Parse(release, L.CurrentLanguage);
            if (parsed.Unavailable) missing.Add("v" + release.Version);
            foreach (var entry in parsed.Entries)
            {
                // A compromised or accidentally enormous body must not freeze the WPF document.
                if (entries.Count >= 2000 || characters + entry.Text.Length > 500_000) { limited = true; break; }
                entries.Add(entry); characters += entry.Text.Length;
            }
            if (limited) break;
        }
        var text = new StringBuilder();
        text.AppendLine(currentVersion == targetVersion
            ? L.Format("Updates.NotesCurrent", targetVersion)
            : L.Format("Updates.NotesRange", currentVersion, targetVersion, releases.Count));
        if (incomplete) text.AppendLine("\n" + L.Get("Updates.NotesIncomplete"));
        if (limited) text.AppendLine("\n" + L.Get("Updates.NotesDisplayLimited"));
        if (missing.Count > 0) text.AppendLine("\n" + L.Format("Updates.NotesUnavailable", string.Join(", ", missing)));
        if (entries.Count == 0) text.AppendLine("\n" + L.Get("Updates.NoNotes"));
        foreach (string category in Categories)
        {
            var group = entries.Where(e => e.Category == category).ToArray();
            if (group.Length == 0) continue;
            text.AppendLine($"\n## {L.Get("Updates.Category." + category)} · {group.Length}\n");
            foreach (var entry in group)
                text.AppendLine($"- {entry.Text.Replace("\n", " ", StringComparison.Ordinal)}  **v{entry.Version}**");
        }
        return text.ToString().Trim();
    }
}
