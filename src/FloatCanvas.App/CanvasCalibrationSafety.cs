using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed record CanvasCalibrationCheck(int AffectedCount, string? Error = null, bool TargetInvalid = false)
{
    internal bool IsSafe => !TargetInvalid && Error is null && AffectedCount == 0;
    internal IReadOnlyList<CanvasCalibrationIssue> Issues { get; init; } = [];
}
internal sealed record CanvasCalibrationIssue(Guid Id, string Name, string Path, RectD? OriginalBounds);

/// <summary>One detached content snapshot for an entire sampling operation, including resampling.</summary>
internal sealed class CanvasCalibrationSafety
{
    private readonly Task<EvaluatedScene> scene;
    private readonly SceneDocument snapshot;
    private readonly CanvasViewTransform viewing;
    internal CanvasCalibrationSafety(SceneDocument snapshot, CanvasViewTransform? viewing = null)
    {
        this.snapshot = snapshot; this.viewing = viewing ?? CanvasViewTransform.Identity;
        scene = Task.Run(() => SceneEvaluation.Build(snapshot));
    }
    internal async Task<CanvasCalibrationCheck> Check(CanvasSettings settings, CancellationToken cancellationToken = default)
    {
        var evaluated = await scene.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var display = SceneEvaluation.WithViewport(evaluated.RenderDocument, evaluated.RenderDocument.Viewport, settings);
            var mapping = CanvasMapping.Create(display);
            var affected = new Dictionary<Guid, RectD?>();
            var original = CanvasMapping.CreateViewing(evaluated.RenderDocument, viewing);
            foreach (var leaf in evaluated.Leaves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (leaf.RootCanvas == RootCanvasKind.Main && !mapping.IsElementSafe(leaf.RenderElement))
                {
                    var id = evaluated.ResolveSelectableNode(leaf.RuntimeElementId) ?? leaf.HostNodeId;
                    RectD? bounds = original.IsElementSafe(leaf.RenderElement) ? original.ProjectedBounds(leaf.RenderElement) : null;
                    if (bounds is { } b && (!double.IsFinite(b.Left) || !double.IsFinite(b.Top) || !double.IsFinite(b.Right) || !double.IsFinite(b.Bottom))) bounds = null;
                    if (affected.TryGetValue(id, out var prior) && prior is { } previous && bounds is { } next) bounds = RectD.Union(previous, next);
                    affected[id] = bounds ?? prior;
                }
            }
            if (affected.Count == 0) return new(0);
            var graph = new SceneGraph(snapshot);
            var definitionNames = snapshot.Definitions.ToDictionary(d => d.Id, d => d.Name);
            string Name(Guid id)
            {
                string? name = graph.FindGroup(id)?.Name ?? graph.FindInstance(id)?.Name ?? graph.FindElement(id)?.Name;
                if (!string.IsNullOrWhiteSpace(name)) return name;
                if (graph.FindInstance(id) is { } instance && definitionNames.TryGetValue(instance.DefinitionId, out var definitionName) &&
                    !string.IsNullOrWhiteSpace(definitionName)) return definitionName;
                return graph.FindElement(id) is { } element ? InspectorWindow.ElementName(element) : L.Get("Layers.UntitledGroup");
            }
            var issues = affected.Select(pair => new CanvasCalibrationIssue(pair.Key, Name(pair.Key),
                string.Join(" › ", graph.AncestorIds(pair.Key).Reverse().Append(pair.Key).Select(Name)), pair.Value)).ToArray();
            return new CanvasCalibrationCheck(affected.Count) { Issues = issues };
        }, cancellationToken).ConfigureAwait(false);
    }
}
