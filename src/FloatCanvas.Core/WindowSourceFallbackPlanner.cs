namespace FloatCanvas.Core;

/// <summary>An owned package candidate, separate from the editing document and shared asset library.</summary>
public sealed record WindowSourceFallbackPlan(SceneDocument Document, IReadOnlyList<AssetResource> Resources);

public static class WindowSourceFallbackPlanner
{
    /// <summary>Frames must be complete, unprocessed source PNG resources, keyed by real source address.</summary>
    public static WindowSourceFallbackPlan Prepare(SceneDocument document,
        IReadOnlyDictionary<SourceNodeAddress, AssetResource> frames, IEnumerable<AssetResource> resources)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(frames); ArgumentNullException.ThrowIfNull(resources);
        PrefabClosure.RequireSourceGraphs(document);
        SceneLimits.ValidateCapacity(document);
        var candidate = document.Clone();
        var sources = candidate.Elements.Select(element => (Address: new SourceNodeAddress(null, element.Id), Element: element))
            .Concat(candidate.Definitions.SelectMany(definition => definition.Graph.Elements.Select(element =>
                (Address: new SourceNodeAddress(definition.Id, element.Id), Element: element))))
            .ToDictionary(pair => pair.Address, pair => pair.Element);
        var available = new Dictionary<string, AssetResource>(StringComparer.Ordinal);
        void Add(AssetResource resource)
        {
            ArgumentNullException.ThrowIfNull(resource);
            if (available.TryGetValue(resource.Id, out var previous))
            {
                if (previous.MediaType != resource.MediaType || !previous.Data.Span.SequenceEqual(resource.Data.Span))
                    throw new InvalidDataException("Conflicting package resources.");
                return;
            }
            available.Add(resource.Id, resource);
        }
        foreach (var resource in resources) Add(resource);
        foreach (var pair in frames)
        {
            if (!sources.TryGetValue(pair.Key, out var source) || source.Kind != ElementKind.Image || source.WindowSource is null)
                throw new InvalidDataException("A window fallback requires a real live-image source address.");
            if (pair.Value is null || pair.Value.MediaType != "image/png" || pair.Value.Data.IsEmpty)
                throw new InvalidDataException("A window fallback must be a complete PNG frame.");
            Add(pair.Value); source.AssetId = pair.Value.Id;
        }
        // Keep every authored definition, including presently unused definitions and source metadata.
        // The resulting resource set includes fallback references retained without a new frame.
        var referenced = candidate.ReferencedAssets.Order(StringComparer.Ordinal).Select(id =>
        {
            if (!available.TryGetValue(id, out var resource)) throw new InvalidDataException("A package resource is missing.");
            return new AssetResource(resource.Id, resource.MediaType, resource.Data.ToArray());
        }).ToArray();
        return new(candidate, Array.AsReadOnly(referenced));
    }
}
