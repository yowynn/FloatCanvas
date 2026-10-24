using System.IO;
using System.Text;
using System.Text.Json;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Persist one resource edit and replay only the records touched by that edit.</summary>
internal sealed class PresetHistory<T>(string path, Func<T, string> id, Func<T, T> clone, JsonSerializerOptions options) where T : class
{
    private string? expectedFile = File.Exists(path) ? File.ReadAllText(path) : null;

    internal void Execute(string label, List<T> before, List<T> after, Func<EditorSession>? session,
        Func<List<T>> current, Action<List<T>> write, string? movedId = null)
    {
        var beforeById = before.ToDictionary(id, StringComparer.Ordinal); var afterById = after.ToDictionary(id, StringComparer.Ordinal);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in beforeById.Keys.Concat(afterById.Keys).Distinct(StringComparer.Ordinal))
            if (!Equal(beforeById.GetValueOrDefault(key), afterById.GetValueOrDefault(key))) changed.Add(key);
        if (movedId is not null && !before.Select(id).SequenceEqual(after.Select(id))) changed.Add(movedId);
        if (changed.Count == 0) return;
        // Keep only changed records and ordering anchors, rather than the whole library in history.
        var oldItems = before.Where(item => changed.Contains(id(item))).Select(clone).ToList();
        var newItems = after.Where(item => changed.Contains(id(item))).Select(clone).ToList();
        string[] oldOrder = before.Select(id).ToArray(), newOrder = after.Select(id).ToArray();
        void Replay(List<T> expected, List<T> replacement, string[] order)
        {
            var next = current().Select(clone).ToList();
            foreach (var key in changed)
                if (!Equal(next.FirstOrDefault(item => id(item) == key), expected.FirstOrDefault(item => id(item) == key)))
                    throw new IOException(L.Get("PresetHistory.Conflict"));
            foreach (var key in changed.OrderBy(key => Array.IndexOf(order, key)))
            {
                int index = next.FindIndex(item => id(item) == key);
                var item = replacement.FirstOrDefault(item => id(item) == key);
                if (item is null) { if (index >= 0) next.RemoveAt(index); continue; }
                if (index >= 0 && key != movedId) { next[index] = clone(item); continue; }
                if (index >= 0) next.RemoveAt(index);
                int target = Array.IndexOf(order, key);
                string? previous = order.Take(target).LastOrDefault(anchor => next.Any(value => id(value) == anchor));
                string? following = order.Skip(target + 1).FirstOrDefault(anchor => next.Any(value => id(value) == anchor));
                int insertion = previous is not null ? next.FindIndex(value => id(value) == previous) + 1 :
                    following is not null ? next.FindIndex(value => id(value) == following) : Math.Min(target, next.Count);
                next.Insert(insertion, clone(item));
            }
            Write(() => write(next));
        }
        var owner = session?.Invoke();
        if (owner is null) { CreateApply(after, write)(); return; }
        long bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(oldItems, options)) +
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(newItems, options)) +
            oldOrder.Concat(newOrder).Sum(key => (long)key.Length * 2);
        owner.ExecuteExternal(label, CreateApply(after, write), () => Replay(newItems, oldItems, oldOrder), () => Replay(oldItems, newItems, newOrder), bytes);
    }

    // A separate closure keeps the initial whole-library write out of retained undo/redo delegates.
    private Action CreateApply(List<T> after, Action<List<T>> write) => () => Write(() => write(after.Select(clone).ToList()));

    internal void Write(Action save)
    {
        if (!StringComparer.Ordinal.Equals(expectedFile, File.Exists(path) ? File.ReadAllText(path) : null))
            throw new IOException(L.Get("PresetHistory.ExternalChange"));
        save();
        expectedFile = File.ReadAllText(path);
    }

    private bool Equal(T? first, T? second) => ReferenceEquals(first, second) || JsonSerializer.Serialize(first, options) == JsonSerializer.Serialize(second, options);
}
