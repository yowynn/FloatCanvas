using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Persistent, process-independent list of recently applied emoji.</summary>
internal sealed class EmojiHistoryStore
{
    internal const int MaximumEntries = 16;
    private sealed class State
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public List<string> Glyphs { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static EmojiHistoryStore? shared;
    private readonly string path;
    private readonly CompatibleConfiguration compatibility;
    private readonly List<string> glyphs;
    private readonly object saveGate = new();
    private Task saveTail = Task.CompletedTask;

    internal static EmojiHistoryStore Shared
    {
        get
        {
            lock (Gate)
            {
                string expected = Path.GetFullPath(Path.Combine(ApplicationPaths.ProfileDirectory, "emoji-history.json"));
                if (shared is null || !StringComparer.OrdinalIgnoreCase.Equals(shared.path, expected))
                    shared = new EmojiHistoryStore(expected);
                return shared;
            }
        }
    }

    internal EmojiHistoryStore(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? Path.Combine(ApplicationPaths.ProfileDirectory, "emoji-history.json"));
        compatibility = new(this.path);
        var state = new State();
        if (File.Exists(this.path))
        {
            try { state = compatibility.Read<State>(JsonOptions); }
            catch (JsonException ex) { throw new InvalidDataException(L.Get("Emoji.InvalidHistory"), ex); }
        }
        if (state.Version != 1) throw new InvalidDataException(L.Get("Emoji.InvalidHistory"));
        var normalized = new List<string>(MaximumEntries);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string glyph in state.Glyphs ?? [])
        {
            if (!EmojiCatalog.TryFind(glyph, out var entry) || !seen.Add(entry.Glyph)) continue;
            if (normalized.Count < MaximumEntries) normalized.Add(entry.Glyph);
        }
        glyphs = normalized;
        if (state.Glyphs is null || state.Glyphs.Count != glyphs.Count || !state.Glyphs.SequenceEqual(glyphs, StringComparer.Ordinal))
            QueueSave([.. glyphs]);
    }

    internal IReadOnlyList<EmojiEntry> Entries
    {
        get
        {
            lock (Gate)
                return glyphs.Select(glyph => EmojiCatalog.TryFind(glyph, out var entry) ? entry : throw new InvalidDataException(L.Get("Emoji.InvalidHistory"))).ToArray();
        }
    }

    internal void Remember(string glyph)
    {
        if (!EmojiCatalog.TryFind(glyph, out var entry)) throw new ArgumentException(L.Get("Emoji.Invalid"), nameof(glyph));
        string[] snapshot;
        lock (Gate)
        {
            glyphs.Remove(entry.Glyph);
            glyphs.Insert(0, entry.Glyph);
            if (glyphs.Count > MaximumEntries) glyphs.RemoveRange(MaximumEntries, glyphs.Count - MaximumEntries);
            snapshot = [.. glyphs];
        }
        QueueSave(snapshot);
    }

    internal void Flush()
    {
        Task pending;
        lock (saveGate) pending = saveTail;
        try { pending.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
    }

    private void QueueSave(string[] snapshot)
    {
        lock (saveGate)
            saveTail = saveTail.ContinueWith(_ => SaveBestEffort(snapshot), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void SaveBestEffort(string[] snapshot)
    {
        try { compatibility.Save(new State { Glyphs = [.. snapshot] }, JsonOptions); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or NotSupportedException)
        {
            try
            {
                new ApplicationDiagnostics(Path.GetDirectoryName(path)!)
                    .Write("Emoji.HistorySaveFailed", new { path }, ex);
            }
            catch { }
        }
    }
}
