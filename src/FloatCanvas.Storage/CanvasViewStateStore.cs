using System.Text.Json;
using FloatCanvas.Core;

namespace FloatCanvas.Storage;

/// <summary>Local UI state, deliberately separate from project and recovery content fingerprints.</summary>
public sealed class CanvasViewStateStore
{
    private sealed record Entry(string Key, PointD Center, double Zoom, DateTimeOffset AccessedAt, PointD PlaneOffset = default);
    private sealed record Binding(string Document, string Namespace, DateTimeOffset AccessedAt);
    private sealed record State(int Version, List<Entry> Entries, List<Binding>? Bindings = null);
    private readonly string path;
    private readonly Func<DateTimeOffset> now;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Binding> bindings = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    public Exception? ReadError { get; private set; }
    public const int MaximumEntries = 1000;
    public static TimeSpan Retention => TimeSpan.FromDays(30);

    public CanvasViewStateStore(string directory, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        path = Path.Combine(Path.GetFullPath(directory), "canvas-view-state.json");
        now = clock ?? (() => DateTimeOffset.UtcNow);
        try
        {
            if (!File.Exists(path)) return;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
            if (state is not { Version: 1, Entries: not null }) return;
            foreach (var entry in state.Entries.Where(Valid).OrderByDescending(entry => entry.AccessedAt).Take(MaximumEntries))
                entries.TryAdd(entry.Key, entry);
            foreach (var binding in (state.Bindings ?? []).Where(Valid).OrderByDescending(item => item.AccessedAt).Take(MaximumEntries))
                bindings.TryAdd(binding.Document, binding);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { ReadError = error; }
    }

    public bool TryRestore(string key, RectD viewport, out CanvasViewTransform view)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (gate)
        {
            if (entries.TryGetValue(key, out var entry) && Valid(entry))
            {
                view = CanvasViewTransform.Restore(entry.Center, entry.Zoom, viewport) with { PlaneOffset = entry.PlaneOffset };
                entries[key] = entry with { AccessedAt = now() };
                return true;
            }
            entries.Remove(key); view = CanvasViewTransform.Identity; return false;
        }
    }

    public void Remember(string key, CanvasViewTransform view, RectD viewport)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        // Restore validates the viewport independently from the view's own validity.
        var center = view.ToCanvas(viewport.Center);
        _ = CanvasViewTransform.Restore(center, view.Zoom, viewport);
        var entry = new Entry(key, center, view.Zoom, now(), view.PlaneOffset);
        if (!Valid(entry)) throw new ArgumentOutOfRangeException(nameof(view));
        lock (gate)
        {
            entries[key] = entry;
            Trim();
        }
    }

    public string? WorkspaceNamespace(string document)
    {
        lock (gate)
        {
            if (!bindings.TryGetValue(document, out var binding) || !Valid(binding)) return null;
            bindings[document] = binding with { AccessedAt = now() }; return binding.Namespace;
        }
    }

    public void BindWorkspace(string document, string canvasNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document); ArgumentException.ThrowIfNullOrWhiteSpace(canvasNamespace);
        lock (gate) { bindings[document] = new(document, canvasNamespace, now()); Trim(); }
    }

    /// <summary>Forks all locally remembered scopes without modifying the source namespace.</summary>
    public void CopyNamespace(string source, string destination, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source); ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (source == destination) return;
        lock (gate)
        {
            string prefix = source + "/";
            foreach (var entry in entries.Values.Where(entry => Valid(entry) && entry.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                string key = destination + entry.Key[source.Length..];
                if (overwrite || !entries.TryGetValue(key, out var prior) || !Valid(prior))
                    entries[key] = entry with { Key = key, AccessedAt = now() };
            }
            Trim();
        }
    }

    private void Trim()
    {
        foreach (var stale in entries.Values.Reverse().OrderByDescending(item => item.AccessedAt)
            .Where((item, index) => !Valid(item) || index >= MaximumEntries).Select(item => item.Key).ToArray()) entries.Remove(stale);
        foreach (var stale in bindings.Values.Reverse().OrderByDescending(item => item.AccessedAt)
            .Where((item, index) => !Valid(item) || index >= MaximumEntries).Select(item => item.Document).ToArray()) bindings.Remove(stale);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            State snapshot;
            lock (gate) snapshot = new(1, entries.Values.Where(Valid).OrderByDescending(entry => entry.AccessedAt).Take(MaximumEntries).ToList(),
                bindings.Values.Where(Valid).OrderByDescending(entry => entry.AccessedAt).Take(MaximumEntries).ToList());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { writes.Release(); }
        }
    }

    private bool Valid(Entry entry) => entry is not null && !string.IsNullOrWhiteSpace(entry.Key) &&
        double.IsFinite(entry.Center.X) && double.IsFinite(entry.Center.Y) &&
        new CanvasViewTransform(entry.Zoom, default, entry.PlaneOffset).IsValid &&
        entry.AccessedAt <= now() + TimeSpan.FromMinutes(5) && now() - entry.AccessedAt <= Retention;
    private bool Valid(Binding entry) => entry is not null && !string.IsNullOrWhiteSpace(entry.Document) && !string.IsNullOrWhiteSpace(entry.Namespace) &&
        entry.AccessedAt <= now() + TimeSpan.FromMinutes(5) && now() - entry.AccessedAt <= Retention;
}
