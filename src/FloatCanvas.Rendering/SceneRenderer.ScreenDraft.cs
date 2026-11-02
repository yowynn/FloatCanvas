using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>Detached screen content and its world candidate, if its ground has become safe.</summary>
public sealed record TransientScreenElement(CanvasElement ScreenElement, int InsertionIndex, CanvasElement? ResolvedWorldElement = null,
    RootCanvasKind RootCanvas = RootCanvasKind.Main);

public sealed partial class SceneRenderer
{
    private SceneDocument? transientScreenDocument;
    private Guid transientScreenId;

    private bool IsTransientScreenElement(SceneDocument? document, CanvasElement element) =>
        ReferenceEquals(document, transientScreenDocument) && element.Id == transientScreenId;

    /// <summary>
    /// Draws a private creation draft without publishing a source node or occurrence. A safe world
    /// candidate uses normal depth composition. Pending ink is half-opacity: in painter order at
    /// its insertion slot, or an explicit screen guide above an unchanged depth-composed base.
    /// </summary>
    public void DrawWithScreenDraft(SKCanvas canvas, SceneDocument runtimeWorld, AssetLibrary assets, TransientScreenElement draft)
    {
        if (draft.ResolvedWorldElement is { } resolved)
        {
            var world = SceneEvaluation.WithTransientElement(runtimeWorld, resolved, draft.InsertionIndex, draft.RootCanvas);
            Draw(canvas, world, assets); return;
        }
        var pending = draft.ScreenElement.Clone(); pending.IsBillboard = false;
        pending.Style.Opacity *= .5;
        var document = SceneEvaluation.WithTransientElement(runtimeWorld, pending, draft.InsertionIndex, draft.RootCanvas);
        transientScreenDocument = document; transientScreenId = pending.Id;
        try
        {
            if (!UsesDepthComposition(runtimeWorld)) { Draw(canvas, document, assets); return; }
            // Pending ground has no physical depth. Do not invent an anchor, clip its sky ink,
            // or change any base depth relationships to give it a misleading final appearance.
            Draw(canvas, runtimeWorld, assets);
            if (!(document.LayerOpacity > 0)) return;
            BeginFrame(document);
            int saved = canvas.Save();
            try
            {
                canvas.ClipRect(ToRect(document.Viewport));
                using var opacity = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(document.LayerOpacity)) };
                canvas.SaveLayer(ToRect(document.Viewport), opacity);
                int pendingIndex = document.Elements.FindIndex(element => element.Id == pending.Id);
                DrawElements(canvas, document.Elements, assets, pendingIndex, pendingIndex + 1);
            }
            finally { canvas.RestoreToCount(saved); EndFrame(); }
        }
        finally { transientScreenDocument = null; transientScreenId = default; }
    }
}
