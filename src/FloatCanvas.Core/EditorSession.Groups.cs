using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

public sealed partial class EditorSession
{
    /// <summary>The outer group is selected as a unit; individual=true keeps direct member editing available.</summary>
    public IEnumerable<Guid> SelectionUnitIds(Guid elementId, bool individual = false)
    {
        var graph = new SceneGraph(Document);
        if (graph.FindElement(elementId) is null && graph.FindGroup(elementId) is null && graph.FindInstance(elementId) is null || graph.IsLocked(elementId)) return [];
        var node = individual ? elementId : graph.ResolveSelectableNode(elementId, EditScopeId);
        return node is { } id && !graph.IsLocked(id) ? [id] : [];
    }

    public Guid? GroupSelection(string? name = null)
    {
        var graph = new SceneGraph(Document);
        var selected = graph.NormalizeSelection(Selection).Where(id => !graph.IsLocked(id)).ToArray();
        if (selected.Length == 0 || selected.Length == 1 && graph.FindGroup(selected[0]) is null && graph.FindInstance(selected[0]) is null) return null;
        EnsureWritableParent(EditScopeId);
        name = NormalizeInsertionName(name) ?? NextGroupName();
        Guid id = Guid.NewGuid();
        Execute(L.Get("History.GroupElements"), document =>
        {
            var current = new SceneGraph(document); current.MaterializeOrder();
            var context = ScopePaintContext(current, EditScopeId);
            var order = current.PaintOrder(context);
            var moving = selected.ToHashSet();
            int index = Enumerable.Range(0, order.Count).Where(i => moving.Contains(order[i]) || current.AncestorIds(order[i]).Any(moving.Contains)).DefaultIfEmpty(order.Count - 1).Max() + 1;
            current.InsertGroup(new() { Id = id, Name = name, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] }, EditScopeId, index);
            current.ReparentPreservingWorld(selected, id); SynchronizeElementOrder(document); SetSelectionCore([id]);
        });
        return id;
    }

    /// <summary>Removes one outer grouping level and preserves any nested groups and all element transforms.</summary>
    public void UngroupSelection()
    {
        var graph = new SceneGraph(Document);
        var roots = graph.NormalizeSelection(Selection).Select(graph.FindGroup).Where(group => group is not null && !graph.IsLocked(group.Id)).Cast<CanvasGroup>().ToArray();
        if (roots.Length == 0) return;
        Execute(L.Get("History.UngroupElements"), document =>
        {
            var selected = new List<Guid>();
            foreach (var saved in roots)
            {
                var current = new SceneGraph(document); var root = current.FindGroup(saved.Id)!;
                var placement = new LayerPlacement(root.Transform, root.ElevationScale);
                foreach (var child in current.EnumerateChildren(root.Id))
                {
                    selected.Add(child);
                    if (current.FindGroup(child) is { } nested)
                    {
                        var combined = placement.Compose(new(nested.Transform, nested.ElevationScale));
                        nested.Transform = combined.PlaneTransform; nested.ElevationScale = combined.ElevationScale; nested.ParentId = root.ParentId;
                        nested.Visible &= root.Visible; nested.Locked |= root.Locked;
                    }
                    else if (current.FindInstance(child) is { } instance)
                    {
                        var combined = placement.Compose(new(instance.Transform, instance.ElevationScale));
                        instance.Transform = combined.PlaneTransform; instance.ElevationScale = combined.ElevationScale; instance.ParentId = root.ParentId;
                        instance.Visible &= root.Visible; instance.Locked |= root.Locked;
                    }
                    else { var element = current.FindElement(child)!; SceneGraph.ApplyPlacement(element, placement); element.GroupId = root.ParentId; element.Visible &= root.Visible; element.Locked |= root.Locked; }
                }
                if (root.OrderMode == GroupOrderMode.AtomicBlock)
                {
                    var context = current.PaintContext(root.Id);
                    var order = context is { } parent ? current.FindGroup(parent)!.ChildOrder! : document.RootOrder!;
                    int index = order.IndexOf(root.Id); order.RemoveAt(index); order.InsertRange(index, root.ChildOrder!);
                }
                document.Groups.Remove(root);
            }
            SynchronizeElementOrder(document); SetSelectionCore(selected);
        });
    }

    /// <summary>Vector preset/clipboard snapshot, with exact selected members and independent group metadata.</summary>
    public SceneDocument CreateSelectionSnapshot(bool normalizeOrigin = true)
    {
        var snapshot = Document.CloneElements(Selection);
        if (snapshot.Elements.Count == 0 && snapshot.Groups.Count == 0 && snapshot.Instances.Count == 0) return snapshot;
        // Clipboard/stamps own the chosen subtrees, rather than duplicate their unselected editing context.
        var graph = new SceneGraph(snapshot); var roots = graph.NormalizeSelection(Selection);
        var retainedGroups = snapshot.Groups.Where(group => roots.Contains(group.Id) || graph.AncestorIds(group.Id).Any(roots.Contains)).Select(group => group.Id).ToHashSet();
        graph.ReparentPreservingWorld(roots, null);
        graph.RemoveNodes(snapshot.Groups.Where(group => !retainedGroups.Contains(group.Id)).Select(group => group.Id).ToArray());
        var world = SceneEvaluation.Build(snapshot).RenderDocument.Elements;
        var bounds = world.Count > 0 ? world.Select(WorldBounds).Aggregate(RectD.Union) :
            roots.Select(id => new SceneGraph(snapshot).NodeWorldPlacement(id).PlaneTransform.TransformPoint(default))
                .Select(point => new RectD(point.X, point.Y, 0, 0)).Aggregate(RectD.Union);
        snapshot.Viewport = new(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        if (normalizeOrigin)
        {
            var translation = Affine2D.Translation(-bounds.Left, -bounds.Top);
            TransformSnapshotRoots(snapshot, new(translation, 1));
            snapshot.Viewport = new(0, 0, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        }
        // The preset carries object opacity. The receiving document owns its overall layer opacity.
        snapshot.LayerOpacity = 1;
        return snapshot;
    }

    /// <summary>Imports editable entities, fresh element/effect/group IDs and immutable referenced resources atomically.</summary>
    public void InsertDocument(SceneDocument source, AssetLibrary sourceAssets, Affine2D transform, bool group = false, string? label = null, bool preserveDefinitions = false, string? groupName = null)
    {
        EnsureWritableParent(EditScopeId);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceAssets);
        groupName = NormalizeInsertionName(groupName);
        if (!double.IsFinite(transform.M11) || !double.IsFinite(transform.M12) || !double.IsFinite(transform.M21) ||
            !double.IsFinite(transform.M22) || !double.IsFinite(transform.OffsetX) || !double.IsFinite(transform.OffsetY) || !transform.TryInverse(out _))
            throw new ArgumentException("The insertion transform must be finite and invertible.", nameof(transform));
        var selected = source.CloneElements(source.Elements.Select(e => e.Id).Concat(source.Groups.Select(g => g.Id)).Concat(source.Instances.Select(instance => instance.Id)));
        var imported = PrefabClosure.PrepareImport(Document, selected, selected.Definitions,
            preserveDefinitions ? PrefabImportIdentity.WithinOwner : PrefabImportIdentity.CrossDocument);
        var snapshot = new SceneDocument { Elements = imported.Graph.Elements, Groups = imported.Graph.Groups, Instances = imported.Graph.Instances,
            RootOrder = imported.Graph.RootOrder, IsRuntimeEvaluated = imported.Graph.IsRuntimeEvaluated,
            Definitions = (preserveDefinitions ? PrefabClosure.ReachableDefinitions(imported.Graph, Document.Definitions) : imported.DefinitionsToAdd).Select(definition => definition.Clone()).ToList() };
        // Resolve every resource before adding any entities to the receiving document.
        var resources = snapshot.ReferencedAssets.Select(id => preserveDefinitions && Assets.TryGet(id, out var retained) ? retained : sourceAssets.Get(id)).ToArray();
        if (snapshot.Elements.Count == 0 && snapshot.Groups.Count == 0 && snapshot.Instances.Count == 0) return;
        TransformSnapshotRoots(snapshot, new(transform, 1));
        EnsureSafeInsertion(SceneEvaluation.Build(snapshot).RenderDocument.Elements);
        if (group && new SceneGraph(snapshot).EnumerateChildren(null).Count > 1)
        {
            var graph = new SceneGraph(snapshot); var roots = graph.EnumerateChildren(null).ToArray();
            var outer = graph.InsertGroup(new() { Name = groupName ?? NextGroupName(), OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
            graph.ReparentPreservingWorld(roots, outer.Id);
        }
        else if (group && groupName is not null)
        {
            var graph = new SceneGraph(snapshot); var root = graph.EnumerateChildren(null).Single();
            if (graph.FindGroup(root) is { } singleGroup) singleGroup.Name = groupName;
            else if (graph.FindInstance(root) is { } singleInstance) singleInstance.Name = groupName;
            else graph.FindElement(root)!.Name = groupName;
        }
        var candidate = Document.Clone();
        AppendSnapshot(candidate, snapshot.Clone(), EditScopeId);
        SceneLimits.ValidateCapacity(candidate);
        // Publish resources only after the complete target hierarchy and capacity have been accepted.
        Execute(label ?? L.Get("History.InsertPreset"), document =>
        {
            foreach (var resource in resources) Assets.Add(resource);
            SetSelectionCore(AppendSnapshot(document, snapshot, EditScopeId));
        });
    }

    private static Guid[] AppendSnapshot(SceneDocument document, SceneDocument snapshot, Guid? scope)
    {
        var graph = new SceneGraph(document); graph.MaterializeOrder();
        var incoming = new SceneGraph(snapshot); incoming.MaterializeOrder();
        var roots = incoming.EnumerateChildren(null).ToArray();
        TransformSnapshotRoots(snapshot, graph.ScopeWorldPlacement(scope).Inverse());
        foreach (var nested in snapshot.Groups.Where(group => group.ParentId is null)) nested.ParentId = scope;
        foreach (var element in snapshot.Elements.Where(element => element.GroupId is null)) element.GroupId = scope;
        foreach (var instance in snapshot.Instances.Where(instance => instance.ParentId is null)) instance.ParentId = scope;
        var context = ScopePaintContext(graph, scope);
        var order = context is { } parent ? graph.FindGroup(parent)!.ChildOrder! : document.RootOrder!;
        order.AddRange(snapshot.RootOrder!);
        document.Groups.AddRange(snapshot.Groups); document.Elements.AddRange(snapshot.Elements); document.Instances.AddRange(snapshot.Instances);
        var retainedDefinitions = document.Definitions.Select(definition => definition.Id).ToHashSet();
        foreach (var definition in snapshot.Definitions) if (retainedDefinitions.Add(definition.Id)) document.Definitions.Add(definition.Clone());
        foreach (var element in snapshot.Elements)
            if (element.Kind == ElementKind.Number) document.NextNumber = Math.Max(document.NextNumber, (int)Math.Min(int.MaxValue, (long)element.Number + 1));
        SynchronizeElementOrder(document); return roots;
    }

    private static void TransformSnapshotRoots(SceneDocument snapshot, LayerPlacement placement)
    {
        TransformGraphRoots(snapshot, placement);
    }
}
