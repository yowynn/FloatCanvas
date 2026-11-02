using FloatCanvas.Core;
using System.Diagnostics;
using System.Windows.Threading;
using System.Text.Json;

namespace FloatCanvas.App;

/// <summary>One immutable source revision, materialized on its first consumer (normally the render worker).</summary>
internal sealed partial class FrozenSceneEvaluation
{
    private readonly SceneDocument source;
    private readonly Lazy<(EvaluatedScene Scene, Dictionary<Guid, int> Indices)> value;
    internal FrozenSceneEvaluation(SceneDocument source, Action? built = null)
    {
        this.source = source;
        value = new(() =>
        {
            var scene = SceneEvaluation.Build(source);
            var indices = scene.Leaves.Select((leaf, index) => (leaf.RuntimeElementId, index)).ToDictionary(pair => pair.RuntimeElementId, pair => pair.index);
            built?.Invoke(); return (scene, indices);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }
    internal SceneDocument SourceDocument => source;
    internal EvaluatedScene Scene => value.Value.Scene;
    internal (int First, int Last) RenderRange(int first, int last)
    {
        if (first < 0 || last < first || last >= source.Elements.Count) return (-1, -1);
        int minimum = int.MaxValue, maximum = -1;
        var indices = value.Value.Indices;
        for (int index = first; index <= last; index++)
            if (indices.TryGetValue(source.Elements[index].Id, out int renderIndex))
            { minimum = Math.Min(minimum, renderIndex); maximum = Math.Max(maximum, renderIndex); }
        return maximum < 0 ? (-1, -1) : (minimum, maximum);
    }
}

/// <summary>UI-owned freezer. Published nodes are never mutated; a gesture replaces only its
/// declared dynamic span. A new/unknown edit deliberately rebuilds instead of sharing live nodes.</summary>
internal sealed class CanvasPreviewScene
{
    private SceneDocument? source, frozen;
    private long capturedRevision = -1, elementsRevision = -1, transaction = -1;
    private SceneDocument? evaluatedSnapshot;
    private CanvasSettings? evaluatedCanvasOverride;
    private FrozenSceneEvaluation? evaluated;
    private long evaluationBuildCount;
    private int evaluationBuildThreadId;
    internal long CapturedRevision => capturedRevision;
    internal long FullBuildCount { get; private set; }
    internal long ClonedElementCount { get; private set; }
    internal long EvaluationBuildCount => Interlocked.Read(ref evaluationBuildCount);
    internal int EvaluationBuildThreadId => Volatile.Read(ref evaluationBuildThreadId);
    internal bool GraphChanged { get; private set; }

    /// <summary>Animation/capture ticks reuse one immutable world view of the frozen source revision.</summary>
    internal EvaluatedScene EvaluateFrozen(SceneDocument snapshot, CanvasSettings? canvasOverride = null) => EvaluationFor(snapshot, canvasOverride).Scene;
    internal FrozenSceneEvaluation EvaluationFor(SceneDocument snapshot, CanvasSettings? canvasOverride = null)
    {
        if (ReferenceEquals(evaluatedSnapshot, snapshot) && ReferenceEquals(evaluatedCanvasOverride, canvasOverride) && evaluated is not null) return evaluated;
        var input = canvasOverride is null ? snapshot : Header(snapshot, snapshot.Elements, snapshot.Background, snapshot.Groups, snapshot.DisplayLayout);
        if (canvasOverride is not null) input.Canvas = canvasOverride.Clone();
        evaluated = new(input, () => { Volatile.Write(ref evaluationBuildThreadId, Environment.CurrentManagedThreadId); Interlocked.Increment(ref evaluationBuildCount); });
        evaluatedSnapshot = snapshot; evaluatedCanvasOverride = canvasOverride;
        return evaluated;
    }

    /// <summary>Builds unknown/initial scenes in bounded UI slices. The caller owns the
    /// revision/context predicate; an invalidated attempt never replaces the published scene.</summary>
    internal async ValueTask<bool> PrepareAsync(SceneDocument document, long revision, long elementRevision,
        long transactionId, int first, int last, bool bypass, IReadOnlySet<Guid>? changedIds,
        long transactionBaseRevision, Func<bool> isCurrent)
    {
        RequireSource(document);
        if (!isCurrent()) return false;
        bool same = ReferenceEquals(source, document) && frozen is not null;
        bool elementsChanged = !same || elementRevision != elementsRevision;
        bool range = TrustedRange(document, transactionId, first, last, bypass, transactionBaseRevision);
        bool settingsOnly = same && !elementsChanged && frozen!.Elements.Count == document.Elements.Count;
        bool elementOnly = same && (changedIds is not null || range);
        if (same && (revision == capturedRevision || settingsOnly && document.Definitions.Count == 0))
        {
            Capture(document, revision, elementRevision, transactionId, first, last, bypass, changedIds, transactionBaseRevision);
            return isCurrent();
        }

        var nodes = new List<CanvasElement>(document.Elements.Count);
        var groups = new List<CanvasGroup>(document.Groups.Count);
        var instances = new List<PrefabInstance>(document.Instances.Count);
        var definitions = new List<PrefabDefinition>(document.Definitions.Count);
        var layouts = new List<DisplayLayoutHint>(document.DisplayLayout.Count);
        var previous = frozen;
        var slice = Stopwatch.StartNew();
        for (int index = 0; index < document.Elements.Count; index++)
        {
            var item = document.Elements[index];
            bool changed = !settingsOnly && (!elementOnly || (changedIds is not null ? changedIds.Contains(item.Id) : index >= first && index <= last));
            if (!changed && index < previous!.Elements.Count && previous.Elements[index].Id == item.Id)
                nodes.Add(previous.Elements[index]);
            else { nodes.Add(item.Clone()); ClonedElementCount++; }
            if (slice.Elapsed.TotalMilliseconds < 2) continue;
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!isCurrent()) return false;
            slice.Restart();
        }
        for (int index = 0; index < document.Groups.Count; index++)
        {
            groups.Add(document.Groups[index].Clone());
            if (slice.Elapsed.TotalMilliseconds < 2) continue;
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!isCurrent()) return false;
            slice.Restart();
        }
        for (int index = 0; !elementOnly && index < document.DisplayLayout.Count; index++)
        {
            layouts.Add(document.DisplayLayout[index].Clone());
            if (slice.Elapsed.TotalMilliseconds < 2) continue;
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!isCurrent()) return false;
            slice.Restart();
        }
        if (!isCurrent()) return false;
        if (ReferenceEquals(source, document) && capturedRevision == revision) return true;
        foreach (var instance in document.Instances)
        {
            instances.Add(instance.Clone());
            if (slice.Elapsed.TotalMilliseconds < 2) continue;
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!isCurrent()) return false; slice.Restart();
        }
        foreach (var definition in document.Definitions)
        {
            // Mutable DTOs cannot be shared solely because their revision is equal.
            // Freeze a definition once, independently of its number of occurrences.
            var copy = new PrefabDefinition { Id = definition.Id, Revision = definition.Revision, Name = definition.Name,
                Pivot = definition.Pivot, Source = definition.Source?.Clone(), Graph = new() {
                    RootOrder = definition.Graph.RootOrder?.ToList() } };
            foreach (var element in definition.Graph.Elements)
            {
                copy.Graph.Elements.Add(element.Clone()); ClonedElementCount++;
                if (slice.Elapsed.TotalMilliseconds < 2) continue;
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (!isCurrent()) return false; slice.Restart();
            }
            foreach (var group in definition.Graph.Groups)
            {
                copy.Graph.Groups.Add(group.Clone());
                if (slice.Elapsed.TotalMilliseconds < 2) continue;
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (!isCurrent()) return false; slice.Restart();
            }
            foreach (var instance in definition.Graph.Instances)
            {
                copy.Graph.Instances.Add(instance.Clone());
                if (slice.Elapsed.TotalMilliseconds < 2) continue;
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (!isCurrent()) return false; slice.Restart();
            }
            definitions.Add(copy);
            if (slice.Elapsed.TotalMilliseconds < 2) continue;
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!isCurrent()) return false; slice.Restart();
        }
        if (!isCurrent()) return false;
        if (ReferenceEquals(source, document) && capturedRevision == revision) return true;
        var next = Header(document, nodes, elementOnly && BackgroundUnchanged(document, previous!, changedIds) ? previous!.Background : document.Background?.Clone(),
            groups, elementOnly ? previous!.DisplayLayout : layouts, instances, definitions);
        if (!isCurrent()) return false;
        Publish(document, next, revision, elementRevision, transactionId);
        if (!elementOnly) FullBuildCount++;
        return true;

    }

    internal SceneDocument Capture(SceneDocument document, long revision, long elementRevision,
        long transactionId, int first, int last, bool bypass, IReadOnlySet<Guid>? changedIds = null, long transactionBaseRevision = -1)
    {
        RequireSource(document);
        bool same = ReferenceEquals(source, document) && frozen is not null;
        if (same && revision == capturedRevision) return frozen!;
        bool elementsChanged = !same || elementRevision != elementsRevision;
        bool range = TrustedRange(document, transactionId, first, last, bypass, transactionBaseRevision);
        bool settingsOnly = same && !elementsChanged && frozen!.Elements.Count == document.Elements.Count;
        bool elementOnly = same && (changedIds is not null || range);
        List<CanvasElement> nodes;
        if (settingsOnly)
            nodes = frozen!.Elements;
        else if (elementOnly)
        {
            nodes = new(document.Elements.Count);
            for (int i = 0; i < document.Elements.Count; i++)
            {
                var item = document.Elements[i];
                bool changed = changedIds is not null ? changedIds.Contains(item.Id) : i >= first && i <= last;
                if (!changed && i < frozen!.Elements.Count && frozen.Elements[i].Id == item.Id)
                    nodes.Add(frozen.Elements[i]);
                else { nodes.Add(item.Clone()); ClonedElementCount++; }
            }
        }
        else
        {
            nodes = document.Elements.Select(item => item.Clone()).ToList();
            ClonedElementCount += nodes.Count; FullBuildCount++;
        }
        // Only already frozen authored leaves may be shared. Mutable definition DTOs
        // are cloned once per revision, never once per placed instance.
        bool shareMetadata = settingsOnly || elementOnly;
        var next = Header(document, nodes, shareMetadata && BackgroundUnchanged(document, frozen!, changedIds) ? frozen!.Background : document.Background?.Clone(),
            document.Groups.Select(group => group.Clone()).ToList(),
            shareMetadata ? frozen!.DisplayLayout : document.DisplayLayout.Select(layout => layout.Clone()).ToList());
        ClonedElementCount += document.Definitions.Sum(definition => definition.Graph.Elements.Count);
        Publish(document, next, revision, elementRevision, transactionId);
        return next;
    }

    private bool TrustedRange(SceneDocument document, long transactionId, int first, int last, bool bypass, long transactionBaseRevision) =>
        first >= 0 && last >= first && last < document.Elements.Count && !bypass &&
        (transaction == transactionId || capturedRevision == transactionBaseRevision);

    private static void RequireSource(SceneDocument document)
    {
        if (document.IsRuntimeEvaluated || document.Definitions.Any(definition => definition.Graph.IsRuntimeEvaluated))
            throw new InvalidOperationException("An evaluated view is already immutable and cannot be frozen as editable source.");
    }

    private static bool BackgroundUnchanged(SceneDocument document, SceneDocument previous, IReadOnlySet<Guid>? changedIds) =>
        document.Background?.Id == previous.Background?.Id &&
        (document.Background is null || changedIds?.Contains(document.Background.Id) != true);

    private static SceneDocument Header(SceneDocument document, List<CanvasElement> nodes, CanvasElement? background,
        List<CanvasGroup> groups, List<DisplayLayoutHint> layouts, List<PrefabInstance>? instances = null, List<PrefabDefinition>? definitions = null) => new()
        {
            Version = document.Version, Kind = document.Kind, Elements = nodes,
            Provenance = document.Provenance, ResourceId = document.ResourceId, RootOrder = document.RootOrder?.ToList(),
            RootCanvasAssignments = new(document.RootCanvasAssignments),
            Instances = instances ?? document.Instances.Select(instance => instance.Clone()).ToList(),
            Definitions = definitions ?? document.Definitions.Select(definition => definition.Clone()).ToList(),
            Background = background, Canvas = document.Canvas.Clone(), Groups = groups,
            Viewport = document.Viewport, LayerOpacity = document.LayerOpacity, NextNumber = document.NextNumber,
            DisplayLayout = layouts
        };

    private void Publish(SceneDocument document, SceneDocument next, long revision, long elementRevision, long transactionId)
    {
        GraphChanged = frozen is null || JsonSerializer.Serialize(frozen.Groups) != JsonSerializer.Serialize(next.Groups) ||
            JsonSerializer.Serialize(frozen.RootCanvasAssignments) != JsonSerializer.Serialize(next.RootCanvasAssignments) ||
            JsonSerializer.Serialize(frozen.Instances) != JsonSerializer.Serialize(next.Instances) ||
            JsonSerializer.Serialize(frozen.Definitions) != JsonSerializer.Serialize(next.Definitions) ||
            !(frozen.RootOrder is null && next.RootOrder is null || frozen.RootOrder is not null && next.RootOrder is not null && frozen.RootOrder.SequenceEqual(next.RootOrder));
        frozen = next;
        source = document; capturedRevision = revision; elementsRevision = elementRevision; transaction = transactionId;
    }
}
