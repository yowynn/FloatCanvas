using FloatCanvas.Core;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private sealed record WindowSourceHandoff(object FromOwner, EditorSession DraftSession, EditorSession Owner,
        object TargetOwner, Guid DefinitionId, string? SourcePath, Guid SourceResourceId, string? SourceSha, IReadOnlySet<Guid> Intents);
    private readonly List<WindowSourceHandoff> _windowSourceHandoffs = [];

    private WindowSourceHandoff? CaptureWindowSourceHandoff(PrefabEditContext context)
    {
        if (_windowRuntime is null) return null;
        object from = WindowOwnerToken(context.Session);
        var intents = new[] { (SceneNodeGraph)context.Session.Document }.Concat(context.Session.Document.Definitions.Select(definition => definition.Graph))
            .SelectMany(graph => graph.Elements).Where(element => element.WindowSource is not null)
            .Select(element => element.WindowSource!.BindingIntent).Distinct()
            .Where(intent => _windowRuntime.State(from, intent).Connected).ToHashSet();
        if (intents.Count == 0) return null;
        return new(from, context.Session, context.Owner, WindowOwnerToken(context.Owner), context.DefinitionId,
            context.IsSource ? context.SourcePath : null, context.Session.Document.ResourceId,
            context.IsSource ? FileState(context.Session).DiskVersion?.Sha256 : null, intents);
    }
    private void QueueWindowSourceHandoff(WindowSourceHandoff? handoff)
    {
        if (handoff is null) return;
        foreach (var previous in _windowSourceHandoffs.Where(item => ReferenceEquals(item.Owner, handoff.Owner) && item.DefinitionId == handoff.DefinitionId).ToArray())
        {
            _windowSourceHandoffs.Remove(previous); _windowRuntime?.Forget(previous.FromOwner);
            _windowOwners.Remove(previous.DraftSession);
        }
        _windowSourceHandoffs.Add(handoff);
    }
    private void CompleteWindowSourceHandoffs(EditorSession owner)
    {
        if (_windowRuntime is null) return;
        foreach (var handoff in _windowSourceHandoffs.Where(item => ReferenceEquals(item.Owner, owner)).ToArray())
        {
            if (!_windowOwners.TryGetValue(owner, out var current) || !ReferenceEquals(current.Token, handoff.TargetOwner))
            { DiscardWindowSourceHandoff(handoff); continue; }
            var definition = owner.Document.Definitions.FirstOrDefault(item => item.Id == handoff.DefinitionId);
            if (definition is null) { DiscardWindowSourceHandoff(handoff); continue; }
            if (handoff.SourcePath is not null)
            {
                var source = definition.Source;
                if (source?.IsLinked != true || source.SourceResourceId != handoff.SourceResourceId ||
                    !string.Equals(source.LastAcceptedFileSha256, handoff.SourceSha, StringComparison.Ordinal)) continue;
            }
            var addresses = PublishedWindowSourceAddresses(owner.Document, handoff.DefinitionId, handoff.Intents);
            if (addresses.Count == 0) continue;
            try { _windowRuntime.Transfer(handoff.FromOwner, handoff.TargetOwner, owner.Document.ResourceId, addresses); }
            catch (Exception error) { _diagnostics.Write("WindowSource.PublicationBindingFailed", exception: error); }
            // Apply has already committed author content. A capture failure must not
            // leave that successful publication looking like a failed transaction.
            DiscardWindowSourceHandoff(handoff);
        }
    }
    private static IReadOnlyDictionary<Guid, SourceNodeAddress> PublishedWindowSourceAddresses(SceneDocument document, Guid entry, IReadOnlySet<Guid> intents)
    {
        var definitions = document.Definitions.ToDictionary(definition => definition.Id);
        var queue = new Queue<Guid>(); var visited = new HashSet<Guid>(); queue.Enqueue(entry);
        var result = new Dictionary<Guid, SourceNodeAddress>();
        while (queue.TryDequeue(out var id))
        {
            if (!visited.Add(id) || !definitions.TryGetValue(id, out var definition)) continue;
            foreach (var element in definition.Graph.Elements)
                if (element.WindowSource is { } source && intents.Contains(source.BindingIntent))
                    result.TryAdd(source.BindingIntent, new(id, element.Id));
            foreach (var instance in definition.Graph.Instances) queue.Enqueue(instance.DefinitionId);
        }
        return result;
    }
    private void DiscardWindowSourceHandoff(WindowSourceHandoff handoff)
    {
        _windowSourceHandoffs.Remove(handoff); _windowRuntime?.Forget(handoff.FromOwner); _windowOwners.Remove(handoff.DraftSession);
    }
    private void ForgetPrefabWindowSources(PrefabEditContext context)
    {
        if (_windowRuntime is null) return;
        if (_windowSourceHandoffs.Any(handoff => ReferenceEquals(handoff.DraftSession, context.Session))) return;
        ForgetWindowSourceBindings(context.Session);
    }
}
