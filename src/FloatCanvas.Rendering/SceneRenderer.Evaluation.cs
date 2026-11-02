using FloatCanvas.Core;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    // Mutable public source documents are never retained or marked as runtime views.
    // Preview callers supply an already evaluated immutable document and pay no graph work per frame.
    internal static SceneDocument WorldDocument(SceneDocument document) =>
        document.IsRuntimeEvaluated || document.Groups.Count == 0 && document.Instances.Count == 0 && document.RootOrder is null && !HasAuxiliaryRoots(document)
            ? document : SceneEvaluation.Build(document).RenderDocument;

    private static (SceneDocument Document, CanvasElement Element) WorldElement(SceneDocument document, CanvasElement element)
    {
        if (document.IsRuntimeEvaluated || document.Groups.Count == 0 && document.Instances.Count == 0 && document.RootOrder is null && !HasAuxiliaryRoots(document)) return (document, element);
        var evaluated = SceneEvaluation.Build(document);
        return (evaluated.RenderDocument, evaluated.ResolveElement(element));
    }

    private SceneDocument? _frameLiveEffectContext;
    private SceneDocument? EffectiveLiveEffectContext => _frameLiveEffectContext ??
        (LiveEffectContext is { } source ? WorldDocument(source) : null);
}
