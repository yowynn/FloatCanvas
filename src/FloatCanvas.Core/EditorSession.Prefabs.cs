using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

public sealed record PrefabCreation(Guid DefinitionId, Guid InstanceId);
public sealed record PrefabImportResult(Guid DefinitionId, Guid InstanceId);

/// <summary>An explicit, independently undoable editing context; publishing remains owned by the original session.</summary>
public sealed class PrefabEditDraft
{
    internal PrefabEditDraft(EditorSession owner, PrefabDefinition definition, EditorSession session)
    {
        Owner = owner; OwnerDocument = owner.Document; OwnerContentVersion = owner.ContentVersion; OwnerScopeVersion = owner.EditScopeVersion;
        DefinitionId = definition.Id; ExpectedRevision = definition.Revision; Session = session;
        Name = definition.Name; Pivot = definition.Pivot; Source = definition.Source?.Clone();
    }
    internal EditorSession Owner { get; }
    internal SceneDocument OwnerDocument { get; }
    internal long OwnerContentVersion { get; }
    internal long OwnerScopeVersion { get; }
    public Guid DefinitionId { get; }
    public Guid ExpectedRevision { get; }
    public EditorSession Session { get; }
    public string Name { get; }
    public PointD Pivot { get; }
    internal ExternalPrefabSource? Source { get; }
}

/// <summary>A complete candidate and immutable resource references. Preparation never changes the owning session.</summary>
public sealed class PrefabEditPlan
{
    internal PrefabEditPlan(EditorSession owner, SceneDocument candidate, IReadOnlyList<AssetResource> resources)
    {
        Owner = owner; OwnerDocument = owner.Document; OwnerContentVersion = owner.ContentVersion; OwnerScopeVersion = owner.EditScopeVersion;
        Candidate = candidate; Resources = resources;
        var graph = new SceneGraph(candidate);
        var mappings = Enum.GetValues<RootCanvasKind>().ToDictionary(root => root, root => CanvasMapping.Create(candidate, root));
        UnsafeRootIds = SceneEvaluation.Build(candidate).Leaves.Where(leaf => !mappings[leaf.RootCanvas].IsElementSafe(leaf.RenderElement))
            .Select(leaf => graph.AncestorIds(leaf.HostNodeId).LastOrDefault() is var root && root != Guid.Empty ? root : leaf.HostNodeId)
            .Distinct().ToArray();
    }
    internal EditorSession Owner { get; }
    internal SceneDocument OwnerDocument { get; }
    internal long OwnerContentVersion { get; }
    internal long OwnerScopeVersion { get; }
    internal IReadOnlyList<AssetResource> Resources { get; }
    internal bool Consumed { get; set; }
    public SceneDocument Candidate { get; }
    public IReadOnlyList<Guid> UnsafeRootIds { get; }
}

public sealed partial class EditorSession
{
    public PrefabCreation CreatePrefabFromSelection(string name)
    {
        RequireWritableScope();
        var graph = new SceneGraph(Document);
        var roots = graph.NormalizeSelection(Selection).Where(id => !graph.IsLocked(id)).ToArray();
        if (roots.Any(id => !InActiveRoot(graph, id))) throw new InvalidOperationException("A shared definition must come from the active root canvas.");
        if (roots.Length == 0) throw new InvalidOperationException(L.Get("Stamp.NoSelection"));
        // Build the selected forest without temporarily changing the user's selection.
        var selected = Selection.ToArray(); SceneDocument snapshot;
        try { SetSelectionCore(roots); snapshot = CreateSelectionSnapshot(normalizeOrigin: true); }
        finally { SetSelectionCore(selected); }
        var evaluated = Document.Instances.Count > 0 ? SceneEvaluation.Build(Document) : null;
        var bounds = roots.Select(id => NodeBounds(graph, id, evaluated)).OfType<RectD>().Aggregate(RectD.Union);
        var definition = new PrefabDefinition { Name = name.Trim(), Graph = SceneGraphClone.CloneGraph(snapshot).Graph, Pivot = default,
            PreviewCanvas = BuildSelectionPreviewSettings(snapshot) };
        var instance = new PrefabInstance { DefinitionId = definition.Id, Name = definition.Name,
            Transform = Affine2D.Translation(bounds.Left, bounds.Top) };
        var candidate = Document.Clone(); var target = new SceneGraph(candidate); target.MaterializeOrder();
        var moving = roots.ToHashSet(); var order = target.PaintOrder(ScopePaintContext(target, EditScopeId));
        int index = Enumerable.Range(0, order.Count).Where(i => moving.Contains(order[i]) || target.AncestorIds(order[i]).Any(moving.Contains))
            .DefaultIfEmpty(order.Count - 1).Max() + 1;
        candidate.Definitions.Add(definition); target.InsertInstance(instance, EditScopeId, index, rootCanvas: ActiveRootCanvas);
        target.RemoveNodes(roots); SceneLimits.ValidateCapacity(candidate);
        Execute(L.Get("Prefab.History.Create"), _ => { Document = candidate; SetSelectionCore([instance.Id]); });
        return new(definition.Id, instance.Id);
    }

    public Guid PlacePrefab(Guid definitionId, LayerPlacement worldPlacement, string? name = null, bool select = true, ContainerBillboardInsertion? billboard = null)
    {
        RequireWritableScope();
        name = NormalizeInsertionName(name);
        var definition = Document.Definitions.Single(definition => definition.Id == definitionId);
        if (!worldPlacement.IsValid) throw new ArgumentException(nameof(worldPlacement));
        var candidate = Document.Clone(); var instance = new PrefabInstance { DefinitionId = definitionId, Name = name ?? definition.Name,
            Transform = worldPlacement.PlaneTransform, ElevationScale = worldPlacement.ElevationScale };
        new SceneGraph(candidate).InsertInstance(instance, EditScopeId, rootCanvas: ActiveRootCanvas);
        billboard?.Apply(candidate, instance.Id);
        SceneLimits.ValidateCapacity(candidate); EnsureSafeInsertion(SceneEvaluation.Build(candidate).OccurrencesOfHostNode(instance.Id).Select(leaf => leaf.RenderElement));
        Execute(L.Get("Prefab.History.Place"), _ => { Document = candidate; if (select) SetSelectionCore([instance.Id]); });
        return instance.Id;
    }

    public PrefabEditDraft BeginPrefabEdit(Guid definitionId, bool readOnly = false)
    {
        if (IsTransactionActive) throw new InvalidOperationException(L.Get("History.TransactionAlreadyActive"));
        PrefabClosure.Validate(Document);
        var definition = Document.Definitions.Single(definition => definition.Id == definitionId);
        var content = SceneGraphClone.CloneGraph(definition.Graph).Graph;
        // A definition cannot contain itself or any definition which already depends
        // on it. Omit those resources from the detached editing graph altogether.
        var dependents = new Dictionary<Guid, List<Guid>>();
        foreach (var item in Document.Definitions)
            foreach (var dependency in item.Graph.Instances.Select(instance => instance.DefinitionId).Distinct())
            {
                if (!dependents.TryGetValue(dependency, out var parents)) dependents.Add(dependency, parents = []);
                parents.Add(item.Id);
            }
        var forbidden = new HashSet<Guid> { definitionId }; var pending = new Queue<Guid>(); pending.Enqueue(definitionId);
        while (pending.TryDequeue(out var dependency))
            if (dependents.TryGetValue(dependency, out var parents))
                foreach (var parent in parents) if (forbidden.Add(parent)) pending.Enqueue(parent);
        var document = new SceneDocument
        {
            Elements = content.Elements, Groups = content.Groups, Instances = content.Instances, RootOrder = content.RootOrder,
            Definitions = Document.Definitions.Where(definition => !forbidden.Contains(definition.Id)).Select(definition => definition.Clone()).ToList(),
            ResourceId = Document.ResourceId, Kind = Document.Kind
        };
        var preview = definition.PreviewCanvas?.Clone() ?? new SubprojectCanvasSettings { Origin = definition.Pivot };
        preview.ApplyTo(document);
        return new(this, definition, new EditorSession(document, Assets, readOnly: IsReadOnly || readOnly)
            { IndependentCanvasOrigin = preview.Origin });
    }

    public PrefabEditPlan PreparePrefabEdit(PrefabEditDraft draft)
    {
        if (draft.Session.IsTransactionActive) throw new InvalidOperationException(L.Get("History.TransactionAlreadyActive"));
        var candidate = BuildPrefabCandidate(draft, draft.Session.Document, publishRevision: true);
        return new(this, candidate, ResolvePrefabResources(candidate, draft.Session.Assets));
    }

    /// <summary>A detached authored preview of all owner instances, including the accepted current gesture sample.</summary>
    public SceneDocument CreatePrefabPreview(PrefabEditDraft draft, SceneDocument? documentOverride = null)
        => BuildPrefabCandidate(draft, documentOverride ?? draft.Session.Document, publishRevision: false);

    private SceneDocument BuildPrefabCandidate(PrefabEditDraft draft, SceneDocument edited, bool publishRevision)
    {
        RequireDraftOwner(draft); PrefabClosure.RequireSourceGraphs(edited);
        var candidate = Document.Clone();
        foreach (var definition in edited.Definitions)
        {
            int index = candidate.Definitions.FindIndex(existing => existing.Id == definition.Id);
            if (index < 0) candidate.Definitions.Add(definition.Clone()); else candidate.Definitions[index] = definition.Clone();
        }
        var existing = Document.Definitions.Single(definition => definition.Id == draft.DefinitionId);
        var replacement = candidate.Definitions.Single(definition => definition.Id == draft.DefinitionId);
        replacement.Graph = SceneGraphClone.CloneGraph(edited).Graph;
        var preview = SubprojectCanvasSettings.FromDocument(edited, draft.Session.IndependentCanvasOrigin);
        replacement.PreviewCanvas = existing.PreviewCanvas is null && SceneContent.Equal(preview, new SubprojectCanvasSettings { Origin = existing.Pivot }) ? null : preview;
        if (publishRevision && (!SceneContent.Equal(existing.Graph, replacement.Graph) || !SceneContent.Equal(Document.Definitions, candidate.Definitions))) replacement.Revision = Guid.NewGuid();
        SceneLimits.ValidateCapacity(candidate);
        return candidate;
    }

    public void ApplyPrefabEdit(PrefabEditPlan plan, bool allowUnsafe = false)
    {
        if (!ReferenceEquals(plan.Owner, this) || plan.Consumed || !ReferenceEquals(plan.OwnerDocument, Document) ||
            plan.OwnerContentVersion != ContentVersion || plan.OwnerScopeVersion != EditScopeVersion || IsTransactionActive) throw new InvalidOperationException(L.Get("Prefab.EditConflict"));
        // UI safety choices may edit the candidate. Materialize the complete owned
        // document before publishing any resources, including unused definitions.
        var candidate = plan.Candidate.Clone(); SceneLimits.ValidateCapacity(candidate);
        var mappings = Enum.GetValues<RootCanvasKind>().ToDictionary(root => root, root => CanvasMapping.Create(candidate, root));
        if (!allowUnsafe && SceneEvaluation.Build(candidate).Leaves.Any(leaf => !mappings[leaf.RootCanvas].IsElementSafe(leaf.RenderElement)))
            throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
        // Resolve every byte before publishing a resource or starting the owner transaction.
        var resources = plan.Resources.ToDictionary(resource => resource.Id, StringComparer.Ordinal);
        foreach (var id in candidate.ReferencedAssets) if (!Assets.TryGet(id, out _) && !resources.ContainsKey(id)) throw new KeyNotFoundException(id);
        Execute(L.Get("Prefab.History.Edit"), _ =>
        {
            foreach (var resource in resources.Values) Assets.Add(resource);
            Document = candidate;
        }); plan.Consumed = true;
    }

    private void RequireDraftOwner(PrefabEditDraft draft)
    {
        if (!IsPrefabEditCurrent(draft))
            throw new InvalidOperationException(L.Get("Prefab.EditConflict"));
    }
    public bool IsPrefabEditCurrent(PrefabEditDraft draft) => ReferenceEquals(draft.Owner, this) &&
        ReferenceEquals(draft.OwnerDocument, Document) && draft.OwnerContentVersion == ContentVersion &&
        draft.OwnerScopeVersion == EditScopeVersion && !IsTransactionActive &&
        Document.Definitions.SingleOrDefault(definition => definition.Id == draft.DefinitionId)?.Revision == draft.ExpectedRevision;
    private static AssetResource[] ResolvePrefabResources(SceneDocument document, AssetLibrary assets) => document.ReferencedAssets.Select(assets.Get).ToArray();

    public PrefabPackage ExportPrefab(Guid definitionId)
    {
        var graph = new SceneNodeGraph { Instances = [new() { DefinitionId = definitionId }] }; graph.RootOrder = [graph.Instances[0].Id];
        var package = new PrefabPackage { EntryDefinitionId = definitionId,
            Definitions = PrefabClosure.ReachableDefinitions(graph, Document.Definitions).Select(definition => definition.Clone()).ToList() };
        foreach (var definition in package.Definitions)
            if (definition.Source is { } source) { source.Locator = null; source.IsRelative = false; source.ImportedDefinitionIds.Clear(); }
        return package;
    }

    /// <summary>Exports a group's local contents without converting the group or changing the editing session.</summary>
    public PrefabPackage ExportGroupPrefab(Guid groupId)
    {
        var source = new SceneGraph(Document);
        var group = source.FindGroup(groupId) ?? throw new InvalidOperationException(L.Get("Inspector.LayerMissing"));
        var retained = source.DescendantNodeIds(groupId).Where(id => id != groupId).ToHashSet();
        var content = new SceneNodeGraph
        {
            Elements = Document.Elements.Where(element => retained.Contains(element.Id)).Select(element => element.Clone()).ToList(),
            Groups = Document.Groups.Where(child => retained.Contains(child.Id)).Select(child => child.Clone()).ToList(),
            Instances = Document.Instances.Where(instance => retained.Contains(instance.Id)).Select(instance => instance.Clone()).ToList(),
            RootOrder = source.PaintOrder(group.OrderMode == GroupOrderMode.AtomicBlock ? groupId : source.PaintContext(groupId))
                .Where(retained.Contains).ToList()
        };
        foreach (var element in content.Elements.Where(element => element.GroupId == groupId)) element.GroupId = null;
        foreach (var child in content.Groups.Where(child => child.ParentId == groupId)) child.ParentId = null;
        foreach (var instance in content.Instances.Where(instance => instance.ParentId == groupId)) instance.ParentId = null;
        var entry = new PrefabDefinition { Name = group.Name, Graph = content, PreviewCanvas = group.PreviewCanvas?.Clone() ?? new() };
        var definitions = PrefabClosure.ReachableDefinitions(content, Document.Definitions).Select(definition => definition.Clone()).ToList();
        definitions.Add(entry);
        var package = new PrefabPackage { EntryDefinitionId = entry.Id, Definitions = definitions };
        foreach (var definition in definitions)
            if (definition.Source is { } link) { link.Locator = null; link.IsRelative = false; link.ImportedDefinitionIds.Clear(); }
        PrefabClosure.Validate(package); return package;
    }

    /// <summary>Retains an editable draft as a new portable resource even after its publication owner has changed.</summary>
    public PrefabPackage ExportPrefabDraft(PrefabEditDraft draft)
    {
        if (!ReferenceEquals(draft.Owner, this)) throw new InvalidOperationException(L.Get("Prefab.EditConflict"));
        PrefabClosure.RequireSourceGraphs(draft.Session.Document);
        var entry = new PrefabDefinition { Id = draft.DefinitionId, Name = draft.Name, Pivot = draft.Pivot,
            Graph = SceneGraphClone.CloneGraph(draft.Session.Document).Graph, Source = draft.Source?.Clone(),
            PreviewCanvas = SubprojectCanvasSettings.FromDocument(draft.Session.Document, draft.Session.IndependentCanvasOrigin) };
        var definitions = draft.Session.Document.Definitions.Where(definition => definition.Id != entry.Id).Select(definition => definition.Clone()).Append(entry).ToList();
        var instance = new PrefabInstance { DefinitionId = entry.Id };
        var graph = new SceneNodeGraph { Instances = [instance], RootOrder = [instance.Id] };
        var package = new PrefabPackage { EntryDefinitionId = entry.Id,
            Definitions = PrefabClosure.ReachableDefinitions(graph, definitions).Select(definition => definition.Clone()).ToList() };
        foreach (var definition in package.Definitions)
            if (definition.Source is { } source) { source.Locator = null; source.IsRelative = false; source.ImportedDefinitionIds.Clear(); }
        PrefabClosure.Validate(package); return package;
    }

    public PrefabImportResult ImportPrefab(PrefabPackage package, AssetLibrary sourceAssets, LayerPlacement worldPlacement,
        ExternalPrefabSource? source = null, bool select = true, string? name = null, ContainerBillboardInsertion? billboard = null)
    {
        RequireWritableScope();
        PrefabClosure.Validate(package); RequireExternalSource(source);
        name = NormalizeInsertionName(name);
        if (!worldPlacement.IsValid) throw new ArgumentException(nameof(worldPlacement));
        var root = new PrefabInstance { DefinitionId = package.EntryDefinitionId };
        var graph = new SceneNodeGraph { Instances = [root], RootOrder = [root.Id] };
        var imported = PrefabClosure.PrepareImport(Document, graph, package.Definitions, PrefabImportIdentity.CrossDocument);
        var candidate = Document.Clone(); candidate.Definitions.AddRange(imported.DefinitionsToAdd.Select(definition => definition.Clone()));
        var definitionId = imported.DefinitionIdMap[package.EntryDefinitionId];
        var definition = candidate.Definitions.Single(definition => definition.Id == definitionId);
        definition.Source = source?.Clone() ?? new ExternalPrefabSource
        {
            SourceResourceId = package.ResourceId, EntryKind = PrefabSourceKind.PrefabDefinition,
            EntryDefinitionId = package.EntryDefinitionId, LastAcceptedContentRevision = definition.Revision,
            OriginClosure = package.Definitions.Where(item => item.Source is not null).SelectMany(item => item.Source!.OriginClosure
                .Append(new(item.Source.SourceResourceId, item.Source.EntryKind, item.Source.EntryDefinitionId))).Distinct().ToList()
        };
        definition.Source.ImportedDefinitionIds = imported.DefinitionIdMap.ToDictionary(pair => pair.Key, pair => pair.Value);
        var instance = imported.Graph.Instances.Single(); instance.Transform = worldPlacement.PlaneTransform;
        instance.ElevationScale = worldPlacement.ElevationScale; instance.Name = name ?? definition.Name;
        new SceneGraph(candidate).InsertInstance(instance, EditScopeId, rootCanvas: ActiveRootCanvas);
        billboard?.Apply(candidate, instance.Id);
        SceneLimits.ValidateCapacity(candidate); EnsureSafeInsertion(SceneEvaluation.Build(candidate).OccurrencesOfHostNode(instance.Id).Select(leaf => leaf.RenderElement));
        var resources = imported.ReferencedAssetIds.Select(sourceAssets.Get).ToArray();
        Execute(L.Get("Prefab.History.Import"), _ =>
        {
            foreach (var resource in resources) Assets.Add(resource);
            Document = candidate; if (select) SetSelectionCore([instance.Id]);
        });
        return new(definitionId, instance.Id);
    }

    public PrefabEditPlan PreparePrefabRefresh(Guid definitionId, PrefabPackage incoming, AssetLibrary sourceAssets, ExternalPrefabSource source)
    {
        if (IsTransactionActive) throw new InvalidOperationException(L.Get("History.TransactionAlreadyActive"));
        var current = Document.Definitions.Single(definition => definition.Id == definitionId);
        PrefabClosure.Validate(incoming); RequireExternalSource(source);
        var root = new PrefabInstance { DefinitionId = incoming.EntryDefinitionId };
        var incomingRoot = new SceneNodeGraph { Instances = [root], RootOrder = [root.Id] };
        var closure = PrefabClosure.ReachableDefinitions(incomingRoot, incoming.Definitions);
        var previousRoot = new PrefabInstance { DefinitionId = definitionId };
        var previous = PrefabClosure.ReachableDefinitions(new() { Instances = [previousRoot], RootOrder = [previousRoot.Id] }, Document.Definitions)
            .Select(definition => definition.Id).ToHashSet();
        var map = new Dictionary<Guid, Guid> { [incoming.EntryDefinitionId] = definitionId };
        var used = new HashSet<Guid> { definitionId }; var occupied = Document.Definitions.Select(definition => definition.Id).ToHashSet();
        foreach (var definition in closure.Where(definition => definition.Id != incoming.EntryDefinitionId))
        {
            if (current.Source?.ImportedDefinitionIds.TryGetValue(definition.Id, out var retained) == true && previous.Contains(retained) && used.Add(retained))
                map.Add(definition.Id, retained);
            else
            {
                Guid id; do { id = Guid.NewGuid(); } while (!occupied.Add(id) || !used.Add(id)); map.Add(definition.Id, id);
            }
        }
        var candidate = Document.Clone();
        foreach (var definition in closure)
        {
            var copy = definition.Clone(); copy.Id = map[definition.Id];
            copy.Graph = SceneGraphClone.CloneGraph(definition.Graph, definitionIds: map).Graph;
            if (copy.Source is { } portable) { portable.Locator = null; portable.IsRelative = false; portable.ImportedDefinitionIds.Clear(); }
            candidate.Definitions.RemoveAll(existing => existing.Id == copy.Id); candidate.Definitions.Add(copy);
        }
        var replacement = candidate.Definitions.Single(definition => definition.Id == definitionId);
        replacement.Name = current.Name; replacement.Pivot = current.Pivot;
        replacement.Source = source.Clone(); replacement.Source.ImportedDefinitionIds = map; replacement.Revision = Guid.NewGuid();
        bool sameSource = current.Source is { } accepted && accepted.SourceResourceId == source.SourceResourceId && accepted.EntryKind == source.EntryKind &&
            (source.EntryKind != PrefabSourceKind.PrefabDefinition || accepted.EntryDefinitionId == source.EntryDefinitionId);
        var offset = sameSource ? source.ImportOrigin - current.Source!.ImportOrigin : default;
        if (sameSource) replacement.Source.ImportOrigin = current.Source!.ImportOrigin;
        if (offset != default)
        {
            TransformGraphRoots(replacement.Graph, new(Affine2D.Translation(offset.X, offset.Y), 1));
            if (replacement.PreviewCanvas is { } preview) preview.Origin += offset;
        }
        SceneLimits.ValidateCapacity(candidate);
        var resources = candidate.ReferencedAssets.Select(id => Assets.TryGet(id, out var retained) ? retained : sourceAssets.Get(id)).ToArray();
        return new(this, candidate, resources);
    }

    private void RequireExternalSource(ExternalPrefabSource? source)
    {
        if (source is null) return;
        if (source.SourceResourceId == Document.ResourceId && source.EntryKind == PrefabSourceKind.ProjectContent ||
            source.OriginClosure.Any(origin => origin.ResourceId == Document.ResourceId && origin.EntryKind == PrefabSourceKind.ProjectContent))
            throw new InvalidDataException(L.Get("Prefab.SourceCycle"));
    }

    public Guid DetachPrefab(Guid instanceId)
    {
        var graph = new SceneGraph(Document);
        if (graph.FindInstance(instanceId) is null || graph.IsLocked(instanceId)) throw new InvalidOperationException(L.Get("Inspector.LockedSuffix"));
        var candidate = Document.Clone(); ExpandPrefabInstance(candidate, instanceId); SceneLimits.ValidateCapacity(candidate);
        Execute(L.Get("Prefab.History.Detach"), _ => { Document = candidate; SetSelectionCore([instanceId]); });
        return instanceId;
    }

    /// <summary>Detaches one occurrence from a prepared source view without
    /// publishing that source generation to the other linked occurrences.</summary>
    public PrefabEditPlan PreparePrefabDetach(Guid instanceId, PrefabEditPlan viewedSource)
    {
        RequireWritableScope();
        if (!ReferenceEquals(viewedSource.Owner, this) || viewedSource.Consumed ||
            !ReferenceEquals(viewedSource.OwnerDocument, Document) || viewedSource.OwnerContentVersion != ContentVersion ||
            viewedSource.OwnerScopeVersion != EditScopeVersion || IsTransactionActive)
            throw new InvalidOperationException(L.Get("Prefab.EditConflict"));
        var graph = new SceneGraph(Document);
        if (graph.FindInstance(instanceId) is null || graph.IsLocked(instanceId))
            throw new InvalidOperationException(L.Get("Inspector.LockedSuffix"));
        var expanded = viewedSource.Candidate.Clone();
        ExpandPrefabInstance(expanded, instanceId);
        var retained = new SceneGraph(expanded).DescendantNodeIds(instanceId).ToHashSet();
        var candidate = Document.Clone();
        candidate.Instances.RemoveAll(instance => instance.Id == instanceId);
        candidate.Groups.AddRange(expanded.Groups.Where(group => retained.Contains(group.Id)));
        candidate.Elements.AddRange(expanded.Elements.Where(element => retained.Contains(element.Id)));
        SceneLimits.ValidateCapacity(candidate);
        var resources = viewedSource.Resources.ToDictionary(resource => resource.Id, StringComparer.Ordinal);
        return new(this, candidate, candidate.ReferencedAssets.Select(id =>
            Assets.TryGet(id, out var asset) ? asset : resources[id]).ToArray());
    }

    private static void ExpandPrefabInstance(SceneDocument document, Guid instanceId)
    {
        var instance = document.Instances.Single(instance => instance.Id == instanceId);
        var definition = document.Definitions.Single(definition => definition.Id == instance.DefinitionId);
        var copy = SceneGraphClone.CloneGraph(definition.Graph, GraphCloneIdentity.NewNodes).Graph;
        new SceneGraph(copy).MaterializeOrder();
        var root = new CanvasGroup { Id = instance.Id, ParentId = instance.ParentId, Name = instance.Name,
            Billboard = instance.Billboard,
            Transform = instance.Transform, ElevationScale = instance.ElevationScale, Visible = instance.Visible, Locked = instance.Locked, Opacity = instance.Opacity,
            PreviewCanvas = definition.PreviewCanvas?.Clone() ?? new SubprojectCanvasSettings { Origin = definition.Pivot },
            OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = copy.RootOrder!.ToList() };
        foreach (var group in copy.Groups.Where(group => group.ParentId is null)) group.ParentId = root.Id;
        foreach (var element in copy.Elements.Where(element => element.GroupId is null)) element.GroupId = root.Id;
        foreach (var nested in copy.Instances.Where(nested => nested.ParentId is null)) nested.ParentId = root.Id;
        document.Instances.Remove(instance); document.Groups.Add(root);
        document.Groups.AddRange(copy.Groups); document.Elements.AddRange(copy.Elements); document.Instances.AddRange(copy.Instances);
        foreach (var nested in copy.Instances.ToArray()) ExpandPrefabInstance(document, nested.Id);
    }

    private static void TransformGraphRoots(SceneNodeGraph document, LayerPlacement placement)
    {
        foreach (var group in document.Groups.Where(group => group.ParentId is null))
        {
            var combined = placement.Compose(new(group.Transform, group.ElevationScale));
            group.Transform = combined.PlaneTransform; group.ElevationScale = combined.ElevationScale;
        }
        foreach (var instance in document.Instances.Where(instance => instance.ParentId is null))
        {
            var combined = placement.Compose(new(instance.Transform, instance.ElevationScale));
            instance.Transform = combined.PlaneTransform; instance.ElevationScale = combined.ElevationScale;
        }
        foreach (var element in document.Elements.Where(element => element.GroupId is null)) SceneGraph.ApplyPlacement(element, placement);
    }

    public void RenamePrefabDefinition(Guid definitionId, string name) => Execute(L.Get("Prefab.RenameDefinition"), document =>
    {
        document.Definitions.Single(definition => definition.Id == definitionId).Name = name.Trim();
    });
    public void ConvertPrefabToEmbedded(Guid definitionId) => Execute(L.Get("Prefab.History.Embed"), document =>
    {
        var definition = document.Definitions.Single(definition => definition.Id == definitionId);
        if (definition.Source is { } source) { source.Locator = null; source.IsRelative = false; }
    });
    public void RemoveUnusedPrefabDefinitions() => Execute(L.Get("Prefab.History.Clean"), document =>
    {
        var retained = PrefabClosure.ReachableDefinitions(document, document.Definitions).Select(definition => definition.Id).ToHashSet();
        document.Definitions.RemoveAll(definition => !retained.Contains(definition.Id));
    });
}
