using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

/// <summary>Definition dependencies, detached transfers and bounded expansion. Never performs external IO.</summary>
public static class PrefabClosure
{
    public static IReadOnlyList<PrefabDefinition> ReachableDefinitions(SceneNodeGraph graph, IReadOnlyList<PrefabDefinition> definitions)
    {
        var map = Index(definitions); var order = DependencyOrder(map);
        var needed = new HashSet<Guid>(); var pending = new Stack<Guid>(graph.Instances.Select(instance => instance.DefinitionId));
        while (pending.TryPop(out var id))
        {
            if (!map.TryGetValue(id, out var definition)) throw Invalid("Missing prefab definition.");
            if (!needed.Add(id)) continue;
            foreach (var instance in definition.Graph.Instances) pending.Push(instance.DefinitionId);
        }
        return order.Where(needed.Contains).Select(id => map[id]).ToArray();
    }

    public static PrefabImportPlan PrepareImport(SceneDocument destination, SceneNodeGraph graph,
        IReadOnlyList<PrefabDefinition> sourceDefinitions, PrefabImportIdentity identity = PrefabImportIdentity.CrossDocument)
    {
        if (!Enum.IsDefined(identity)) throw new ArgumentOutOfRangeException(nameof(identity));
        RequireSourceGraph(graph); foreach (var definition in sourceDefinitions) RequireSourceGraph(definition.Graph);
        _ = ValidateCore(graph, sourceDefinitions);
        var closure = ReachableDefinitions(graph, sourceDefinitions);
        var map = new Dictionary<Guid, Guid>(); var additions = new List<PrefabDefinition>();
        var occupied = destination.Definitions.Select(definition => definition.Id).ToHashSet();
        foreach (var definition in closure)
        {
            if (identity == PrefabImportIdentity.WithinOwner)
            {
                if (!occupied.Contains(definition.Id)) throw Invalid("The owner no longer contains the referenced definition.");
                map.Add(definition.Id, definition.Id);
            }
            else
            {
                Guid id; do { id = Guid.NewGuid(); } while (!occupied.Add(id)); map.Add(definition.Id, id);
            }
        }
        if (identity == PrefabImportIdentity.CrossDocument)
            foreach (var definition in closure)
            {
                var copy = definition.Clone(); copy.Id = map[definition.Id];
                // Definition graphs have independent namespaces: preserve source IDs so
                // a later accepted refresh retains the same occurrence identities.
                copy.Graph = SceneGraphClone.CloneGraph(definition.Graph, GraphCloneIdentity.Preserve, map).Graph;
                // A copied snapshot retains origin identity, not a machine-relative update binding.
                if (copy.Source is { } source) { source.Locator = null; source.IsRelative = false; source.ImportedDefinitionIds.Clear(); }
                additions.Add(copy);
            }
        var clone = SceneGraphClone.CloneGraph(graph, GraphCloneIdentity.NewNodes, map);
        var retained = identity == PrefabImportIdentity.WithinOwner ? ReachableDefinitions(clone.Graph, destination.Definitions) : additions;
        var assets = clone.Graph.ReferencedAssetIds.Concat(retained.SelectMany(definition => definition.Graph.ReferencedAssetIds)).ToHashSet(StringComparer.Ordinal);
        return new(clone.Graph, additions.AsReadOnly(), clone.NodeIdMap, map, assets);
    }

    public static PrefabExpansionBudget Validate(SceneDocument document)
    {
        RequireSourceGraphs(document);
        if (document.ResourceId == Guid.Empty) throw Invalid("The resource identity is empty.");
        return ValidateCore(document, document.Definitions);
    }
    public static PrefabExpansionBudget Validate(PrefabPackage package)
    {
        RequireSourceGraphs(package);
        if (package.ResourceId == Guid.Empty || package.EntryDefinitionId == Guid.Empty) throw Invalid("The prefab entry identity is empty.");
        var map = Index(package.Definitions);
        if (!map.ContainsKey(package.EntryDefinitionId)) throw Invalid("Missing prefab entry definition.");
        return ValidateCore(null, package.Definitions, package.EntryDefinitionId);
    }
    public static void RequireSourceGraphs(SceneDocument document)
    {
        RequireSourceGraph(document);
        if (document.Definitions is null) throw Invalid("Missing definition collection.");
        foreach (var definition in document.Definitions)
        {
            if (definition is null || definition.Graph is SceneDocument) throw Invalid("A definition must contain a pure content graph."); RequireSourceGraph(definition.Graph);
        }
    }
    public static void RequireSourceGraphs(PrefabPackage package)
    {
        if (package.Definitions is null) throw Invalid("Missing definition collection.");
        foreach (var definition in package.Definitions)
        {
            if (definition is null || definition.Graph is SceneDocument) throw Invalid("A definition must contain a pure content graph."); RequireSourceGraph(definition.Graph);
        }
    }
    private static void RequireSourceGraph(SceneNodeGraph graph)
    {
        if (graph is null || graph.IsRuntimeEvaluated) throw Invalid("An evaluated render graph is not an editable source.");
        if (graph.Elements is null || graph.Groups is null || graph.Instances is null ||
            graph.Elements.Any(element => element is null) || graph.Groups.Any(group => group is null) || graph.Instances.Any(instance => instance is null))
            throw Invalid("Missing authored graph collections.");
    }

    private readonly record struct Measure(long Leaves, long Nodes, long Points, long Effects, int Depth);
    private static PrefabExpansionBudget ValidateCore(SceneNodeGraph? root, IReadOnlyList<PrefabDefinition> definitions, Guid? entry = null)
    {
        var map = Index(definitions); var order = DependencyOrder(map); var measured = new Dictionary<Guid, Measure>();
        long sourceElements = root?.Elements.Count ?? 0, sourceGroups = root?.Groups.Count ?? 0, sourceInstances = root?.Instances.Count ?? 0;
        foreach (var definition in definitions)
        {
            sourceElements += definition.Graph.Elements.Count; sourceGroups += definition.Graph.Groups.Count; sourceInstances += definition.Graph.Instances.Count;
        }
        if (sourceElements > SceneLimits.MaximumElements || sourceGroups > SceneLimits.MaximumElements || sourceInstances > SceneLimits.MaximumElements)
            throw Invalid(L.Get("Scene.ElementCountExceeded"));
        foreach (var id in order)
        {
            var measure = MeasureGraph(map[id].Graph, measured); Check(measure); measured.Add(id, measure);
        }
        Measure total;
        if (root is not null) total = MeasureGraph(root, measured);
        else
        {
            var content = measured[entry!.Value]; total = content with { Nodes = Add(content.Nodes, 1, SceneLimits.MaximumExpandedNodes), Depth = content.Depth + 1 };
        }
        Check(total);
        return new(sourceElements, sourceGroups, sourceInstances, definitions.Count, total.Leaves, total.Nodes, total.Points, total.Effects, total.Depth);
    }
    private static Measure MeasureGraph(SceneNodeGraph source, IReadOnlyDictionary<Guid, Measure> definitions)
    {
        RequireSourceGraph(source); var graph = new SceneGraph(source);
        long leaves = source.Elements.Count, nodes = (long)source.Elements.Count + source.Groups.Count + source.Instances.Count, points = 0, effects = 0;
        int depth = 0;
        foreach (var group in source.Groups) depth = Math.Max(depth, graph.AncestorIds(group.Id).Count() + 1);
        foreach (var element in source.Elements)
        {
            if (element.Points is null || element.Points.Count > SceneLimits.MaximumPathPoints || element.ImageFrame?.Count > SceneLimits.MaximumPathPoints || element.Effects is null)
                throw Invalid(L.Get("Scene.PathCapacityExceeded"));
            points = Add(points, (long)element.Points.Count + (element.ImageFrame?.Count ?? 0), SceneLimits.MaximumExpandedPathPoints);
            effects = Add(effects, element.Effects.Count, SceneLimits.MaximumExpandedEffects);
            depth = Math.Max(depth, graph.AncestorIds(element.Id).Count());
        }
        foreach (var instance in source.Instances)
        {
            if (!definitions.TryGetValue(instance.DefinitionId, out var definition)) throw Invalid("Missing prefab definition.");
            leaves = Add(leaves, definition.Leaves, SceneLimits.MaximumExpandedLeaves);
            nodes = Add(nodes, definition.Nodes, SceneLimits.MaximumExpandedNodes);
            points = Add(points, definition.Points, SceneLimits.MaximumExpandedPathPoints);
            effects = Add(effects, definition.Effects, SceneLimits.MaximumExpandedEffects);
            depth = Math.Max(depth, graph.AncestorIds(instance.Id).Count() + 1 + definition.Depth);
        }
        return new(leaves, nodes, points, effects, depth);
    }
    private static Dictionary<Guid, PrefabDefinition> Index(IReadOnlyList<PrefabDefinition> definitions)
    {
        if (definitions is null || definitions.Count > SceneLimits.MaximumDefinitions) throw Invalid("Prefab definition capacity exceeded.");
        var map = new Dictionary<Guid, PrefabDefinition>();
        foreach (var definition in definitions)
        {
            if (definition is null || definition.Id == Guid.Empty || definition.Revision == Guid.Empty || !map.TryAdd(definition.Id, definition) ||
                definition.Name is null || definition.Name.Length > 1024 || !Finite(definition.Pivot) || definition.Graph is null || definition.Graph is SceneDocument)
                throw Invalid("Invalid prefab definition.");
            RequireSourceGraph(definition.Graph); SceneGraph.Validate(definition.Graph); ValidateSource(definition.Source);
        }
        return map;
    }
    private static IReadOnlyList<Guid> DependencyOrder(Dictionary<Guid, PrefabDefinition> definitions)
    {
        var counts = new Dictionary<Guid, int>(); var dependents = definitions.Keys.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var pair in definitions)
        {
            var dependencies = pair.Value.Graph.Instances.Select(instance => instance.DefinitionId).Distinct().ToArray();
            counts.Add(pair.Key, dependencies.Length);
            foreach (var dependency in dependencies)
            {
                if (!dependents.TryGetValue(dependency, out var parents)) throw Invalid("Missing nested prefab definition."); parents.Add(pair.Key);
            }
        }
        var ready = new Queue<Guid>(counts.Where(pair => pair.Value == 0).Select(pair => pair.Key)); var order = new List<Guid>();
        while (ready.TryDequeue(out var id))
        {
            order.Add(id); foreach (var parent in dependents[id]) if (--counts[parent] == 0) ready.Enqueue(parent);
        }
        if (order.Count != definitions.Count) throw Invalid("Prefab definitions contain a dependency cycle.");
        return order;
    }
    private static void ValidateSource(ExternalPrefabSource? source)
    {
        if (source is null) return;
        if (source.SourceResourceId == Guid.Empty || !ValidEntry(source.EntryKind, source.EntryDefinitionId) || !Finite(source.ImportOrigin) ||
            source.Locator is not null && (string.IsNullOrWhiteSpace(source.Locator) || source.Locator.Length > 32767 || source.Locator.Any(char.IsControl)) ||
            source.LastAcceptedFileSha256 is { } hash && !ValidHash(hash) || source.IsLinked && source.LastAcceptedFileSha256 is null ||
            source.LastAcceptedContentRevision == Guid.Empty || source.OriginClosure is null || source.OriginClosure.Count > SceneLimits.MaximumDefinitions ||
            source.ImportedDefinitionIds is null || source.ImportedDefinitionIds.Count > SceneLimits.MaximumDefinitions ||
            source.ImportedDefinitionIds.Any(pair => pair.Key == Guid.Empty || pair.Value == Guid.Empty) ||
            source.ImportedDefinitionIds.Values.Distinct().Count() != source.ImportedDefinitionIds.Count)
            throw Invalid("Invalid prefab source metadata.");
        foreach (var origin in source.OriginClosure)
            if (origin is null || origin.ResourceId == Guid.Empty || !ValidEntry(origin.EntryKind, origin.EntryDefinitionId)) throw Invalid("Invalid prefab origin closure.");
    }
    private static bool ValidEntry(PrefabSourceKind kind, Guid? entry) => Enum.IsDefined(kind) &&
        (kind == PrefabSourceKind.ProjectContent ? entry is null : entry is { } id && id != Guid.Empty);
    private static bool ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool Finite(PointD point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static long Add(long first, long second, long limit) => first > limit - second ? limit + 1 : first + second;
    private static void Check(Measure measure)
    {
        if (measure.Leaves > SceneLimits.MaximumExpandedLeaves || measure.Nodes > SceneLimits.MaximumExpandedNodes ||
            measure.Points > SceneLimits.MaximumExpandedPathPoints || measure.Effects > SceneLimits.MaximumExpandedEffects || measure.Depth > SceneLimits.MaximumGroupDepth)
            throw Invalid("Prefab expansion exceeds the document capacity.");
    }
    private static InvalidDataException Invalid(string message) => new(message);
}
