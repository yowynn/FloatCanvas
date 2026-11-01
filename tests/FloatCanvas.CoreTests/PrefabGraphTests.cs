using FloatCanvas.Core;
using System.Diagnostics;
using System.Text.Json;

internal static class PrefabGraphTests
{
    internal static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string message)
        {
            bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; }
            check(rejected, message);
        }
        var source = Box(); var child = new PrefabDefinition { Name = "child", Graph = new() { Elements = [source] } };
        var nested = new PrefabInstance { DefinitionId = child.Id, Transform = Affine2D.Translation(17, 23), ElevationScale = 2 };
        var entry = new PrefabDefinition { Name = "entry", Graph = new() { Instances = [nested], RootOrder = [nested.Id] } };
        var host = new SceneDocument { Definitions = [entry, child] };
        var graph = new SceneGraph(host); var first = graph.InsertInstance(new() { DefinitionId = entry.Id, Transform = Affine2D.Translation(100, 0) });
        var second = new SceneGraph(host).InsertInstance(new() { DefinitionId = entry.Id, Transform = Affine2D.Translation(300, 0) });
        var budget = PrefabClosure.Validate(host);
        check(host.Elements.Count == 0 && host.Instances.Count == 2 && host.Definitions.Count == 2 && budget.SourceElements == 1 && budget.ExpandedLeaves == 2,
            "real instances retain one source graph and expand nested appearances without host leaf duplication");
        check(new SceneGraph(host).OrderedContentNodeIds.SequenceEqual([first.Id, second.Id]) && !new SceneGraph(host).DescendantElementIds(first.Id).Any(),
            "instance slots are source terminals and never masquerade as writable definition leaves");
        var evaluation = SceneEvaluation.Build(host);
        check(evaluation.Leaves.Count == 2 && evaluation.Leaves.Select(leaf => leaf.RuntimeElementId).Distinct().Count() == 2 &&
            evaluation.Leaves.All(leaf => leaf.Source == new SourceNodeAddress(child.Id, source.Id)), "nested occurrences have distinct identities and an explicit definition namespace");
        var beforeIds = evaluation.Leaves.Select(leaf => leaf.RuntimeElementId).ToArray(); source.Style.Fill = 0xFF123456; child.Revision = Guid.NewGuid();
        check(SceneEvaluation.Build(host).Leaves.Select(leaf => leaf.RuntimeElementId).SequenceEqual(beforeIds) &&
            SceneEvaluation.Build(host).Leaves.All(leaf => leaf.RenderElement.Style.Fill == source.Style.Fill), "shared edits affect all instances while revision and color preserve occurrence identity");
        var duplicate = host.Clone(true);
        check(duplicate.Instances.All(instance => host.Instances.All(original => original.Id != instance.Id)) && duplicate.Instances.All(instance => instance.DefinitionId == entry.Id) &&
            duplicate.Definitions[1].Graph.Elements[0].Id == source.Id, "host duplication remaps real nodes while preserving definition IDs and independent source namespaces");
        var selection = host.CloneElements([first.Id]);
        check(selection.Instances.Count == 1 && selection.Definitions.Count == 2 && selection.Definitions.Single(definition => definition.Id == child.Id).Graph.Elements.Single().Id == source.Id,
            "instance selection snapshot retains its full dependency closure");
        var destination = new SceneDocument { Definitions = [child.Clone()] };
        string unchanged = JsonSerializer.Serialize(destination);
        var transfer = PrefabClosure.PrepareImport(destination, selection, selection.Definitions);
        check(transfer.DefinitionsToAdd.Count == 2 && transfer.DefinitionIdMap.All(pair => pair.Key != pair.Value) &&
            transfer.Graph.Instances.Single().DefinitionId == transfer.DefinitionIdMap[entry.Id] &&
            transfer.DefinitionsToAdd.Single(definition => definition.Name == "child").Graph.Elements.Single().Id == source.Id && JsonSerializer.Serialize(destination) == unchanged,
            "cross-document transfer remaps conflicting definition IDs but preserves source node identity and does not mutate destination");
        var sameOwner = PrefabClosure.PrepareImport(host, selection, selection.Definitions, PrefabImportIdentity.WithinOwner);
        check(sameOwner.DefinitionsToAdd.Count == 0 && sameOwner.Graph.Instances.Single().DefinitionId == entry.Id, "same-owner duplicate shares existing definitions");
        var reparented = host.Clone(); var oldWorld = SceneEvaluation.Build(reparented).Leaves[0].RenderElement.Transform;
        var parent = new SceneGraph(reparented).InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, Transform = Affine2D.Translation(-50, 30).Compose(Affine2D.Rotation(41)), ElevationScale = 3 });
        new SceneGraph(reparented).ReparentPreservingWorld([first.Id], parent.Id);
        var newWorld = SceneEvaluation.Build(reparented).OccurrencesOfHostNode(first.Id).Single().RenderElement.Transform;
        check(newWorld.TransformPoint(default).DistanceTo(oldWorld.TransformPoint(default)) < 1e-7 && newWorld.TransformPoint(new(1, 1)).DistanceTo(oldWorld.TransformPoint(new(1, 1))) < 1e-7 &&
            reparented.Definitions.Single(definition => definition.Id == child.Id).Graph.Elements.Single().Transform == source.Transform,
            "reparenting a real instance retains occurrence world placement and never bakes into a shared source leaf");
        var collide = new PrefabDefinition { Graph = new() { Elements = [source.Clone()] } };
        var namespaces = new SceneDocument { Elements = [source.Clone()], Definitions = [child.Clone(), collide] };
        PrefabClosure.Validate(namespaces); check(true, "equal node GUIDs in distinct graph namespaces are legal");
        var malformed = host.Clone(); malformed.RootOrder!.Remove(first.Id); Reject(() => PrefabClosure.Validate(malformed), "instance without a draw slot is rejected");
        malformed = host.Clone(); malformed.Instances[0].DefinitionId = Guid.NewGuid(); Reject(() => PrefabClosure.Validate(malformed), "missing instance definition is rejected");
        malformed = host.Clone(); malformed.Definitions[1].Graph = malformed;
        Reject(() => PrefabClosure.Validate(malformed), "a document camera cannot hide inside a definition graph");
        var runtimeGraph = SceneGraphClone.CloneGraph(evaluation.RenderDocument).Graph;
        check(runtimeGraph.IsRuntimeEvaluated && SceneGraphClone.CloneGraph(runtimeGraph).Graph.IsRuntimeEvaluated, "runtime marker survives typed base graph cloning");
        malformed = host.Clone(); malformed.Definitions[0].Graph = runtimeGraph;
        Reject(() => PrefabClosure.RequireSourceGraphs(malformed), "runtime marker inside a retained definition is rejected before saving");
        var cycle = new PrefabDefinition(); var cycleInstance = new PrefabInstance { DefinitionId = cycle.Id };
        cycle.Graph.Instances.Add(cycleInstance); cycle.Graph.RootOrder = [cycleInstance.Id];
        Reject(() => PrefabClosure.Validate(new PrefabPackage { EntryDefinitionId = cycle.Id, Definitions = [cycle] }), "definition self cycle is rejected without recursive expansion");
        var other = new PrefabDefinition(); var back = new PrefabInstance { DefinitionId = other.Id };
        cycle.Graph.Instances[0].DefinitionId = other.Id; other.Graph.Instances.Add(new() { DefinitionId = cycle.Id }); other.Graph.RootOrder = [other.Graph.Instances[0].Id];
        Reject(() => PrefabClosure.Validate(new PrefabPackage { EntryDefinitionId = cycle.Id, Definitions = [cycle, other] }), "mutual definition cycle is rejected");
        var deep = Chain(64); PrefabClosure.Validate(deep); check(true, "cumulative instance depth64 is accepted");
        Reject(() => PrefabClosure.Validate(Chain(65)), "cumulative instance depth65 is rejected before runtime allocation");
        var empty = new PrefabDefinition(); var doubled = new List<PrefabDefinition> { empty };
        for (int i = 0; i < 20; i++)
        {
            var a = new PrefabInstance { DefinitionId = doubled[^1].Id }; var b = a.Clone(); b.Id = Guid.NewGuid();
            doubled.Add(new() { Graph = new() { Instances = [a, b], RootOrder = [a.Id, b.Id] } });
        }
        var timer = Stopwatch.StartNew(); Reject(() => PrefabClosure.Validate(new PrefabPackage { EntryDefinitionId = doubled[^1].Id, Definitions = doubled }),
            "exponential empty definition DAG is rejected by expanded work, even with zero leaves");
        Console.WriteLine($"PREFAB_BUDGET empty_dag_defs={doubled.Count} elapsed_ms={timer.Elapsed.TotalMilliseconds:F3}");
        var path = Box(); path.Kind = ElementKind.Freehand; path.Points = new PointCollection(Enumerable.Repeat(new PointD(1, 2), SceneLimits.MaximumPathPoints));
        var pathDefinition = new PrefabDefinition { Graph = new() { Elements = [path] } }; var pathHost = new SceneDocument { Definitions = [pathDefinition] };
        for (int i = 0; i < 8; i++) new SceneGraph(pathHost).InsertInstance(new() { DefinitionId = pathDefinition.Id });
        check(PrefabClosure.Validate(pathHost).ExpandedPathPoints == SceneLimits.MaximumExpandedPathPoints, "expanded path work is accepted at16m points while retaining one source buffer");
        var pathCopy = pathHost.Clone(); pathCopy.Definitions[0].Graph.Elements[0].Points[0] = new(8, 9);
        check(path.Points[0] == new PointD(1, 2), "definition history clones retain path snapshot isolation through copy-on-write");
        new SceneGraph(pathHost).InsertInstance(new() { DefinitionId = pathDefinition.Id });
        Reject(() => PrefabClosure.Validate(pathHost), "small source graph repeated beyond16m expanded points is rejected before cloning buffers");
        var workDefinition = new PrefabDefinition { Graph = new() { Elements = [new() { Kind = ElementKind.Image, ImageFrame = Enumerable.Repeat(new PointD(.5, .5), 32).ToList(), Effects = Enumerable.Range(0, 20).Select(_ => new ImageEffect()).ToList() }] } };
        var workHost = new SceneDocument { Definitions = [workDefinition] };
        workHost.Instances = Enumerable.Range(0, SceneLimits.MaximumExpandedLeaves).Select(_ => new PrefabInstance { DefinitionId = workDefinition.Id }).ToList();
        workHost.RootOrder = workHost.Instances.Select(instance => instance.Id).ToList();
        timer.Restart(); var workBudget = PrefabClosure.Validate(workHost);
        check(workBudget.ExpandedLeaves == 100_000 && workBudget.ExpandedEffects == SceneLimits.MaximumExpandedEffects && workBudget.ExpandedPathPoints == 3_200_000,
            "100k real instances count image-frame and per-occurrence effect work without expanding source buffers");
        Console.WriteLine($"PREFAB_BUDGET instances=100000 leaves={workBudget.ExpandedLeaves} frame_points={workBudget.ExpandedPathPoints} effects={workBudget.ExpandedEffects} elapsed_ms={timer.Elapsed.TotalMilliseconds:F3}");
        workDefinition.Graph.Elements[0].Effects.Add(new());
        Reject(() => PrefabClosure.Validate(workHost), "repeated effects beyond2m expanded work are rejected even with legal source counts");
        workDefinition.Graph.Elements[0].Effects.RemoveAt(20); workDefinition.Graph.Elements.Add(Box());
        Reject(() => PrefabClosure.Validate(workHost), "100k instances with two leaves exceed the expanded leaf budget without allocating virtual leaves");
        var emptyHost = new SceneDocument { Definitions = [empty] }; var emptyInstance = new SceneGraph(emptyHost).InsertInstance(new() { DefinitionId = empty.Id });
        check(emptyHost.CloneElements([emptyInstance.Id]).Instances.Count == 1 && PrefabClosure.Validate(emptyHost).ExpandedLeaves == 0,
            "empty prefab instance is a stable selectable persistent real node");
        var capture = host.CreateCapture(new(90, -10, 100, 100), new string('a', 64));
        check(capture.Instances.Count == 1 && capture.Definitions.Count == 2 && capture.ResourceId != host.ResourceId &&
            SceneEvaluation.Build(capture).Leaves.Single().RenderElement.Transform.OffsetX == 27,
            "capture keeps an intersecting instance source subtree and translates its host placement once");
        var jsonBudget = new SceneDocument { Definitions = [new() { Graph = new() { Elements = [new() { Kind = ElementKind.Text, Text = new string('x', 16384) }] } }] };
        Reject(() => SceneLimits.ValidatePayloadJsonCapacity(jsonBudget, 8192), "the one source JSON budget includes retained definitions even when the host owns no leaves");
    }
    private static CanvasElement Box() => new() { Kind = ElementKind.Rectangle, Width = 30, Height = 20 };
    private static PrefabPackage Chain(int count)
    {
        var definitions = new List<PrefabDefinition> { new() { Graph = new() { Elements = [Box()] } } };
        for (int i = 1; i < count; i++)
        {
            var instance = new PrefabInstance { DefinitionId = definitions[^1].Id };
            definitions.Add(new() { Graph = new() { Instances = [instance], RootOrder = [instance.Id] } });
        }
        return new() { EntryDefinitionId = definitions[^1].Id, Definitions = definitions };
    }
}
