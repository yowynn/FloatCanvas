using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    internal static bool HasAuxiliaryRoots(SceneDocument document) => document.RootCanvasAssignments.Count > 0;

    private static RootCanvasKind RootOf(SceneDocument document, CanvasElement element) =>
        SceneCanvasRoots.Resolve(document, element.Id);

    private static CanvasMapping ElementMapping(SceneDocument document, CanvasElement element) =>
        CanvasMapping.Create(document, RootOf(document, element));

    private void DrawRootScene(SKCanvas canvas, SceneDocument document, AssetLibrary assets,
        bool includeBackground, bool includeCanvasBackground, bool includeGrid)
    {
        double opacity = Math.Clamp(document.LayerOpacity, 0, 1);
        bool photograph = includeBackground && document.Background is { Visible: true };
        // Frozen captures retain their original photographed base. New screen roots
        // are annotations above that base, while the ordinary main-plane color stays
        // between the background and foreground authored roots.
        DrawBackground(canvas, document, assets, includeBackground, includeCanvasBackground && photograph, canvasOpacity: opacity);
        if (!(opacity > 0)) return;
        int saved = canvas.Save();
        try
        {
            if (opacity < 1)
            {
                using var alpha = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) };
                canvas.SaveLayer(ToRect(document.Viewport), alpha);
            }
            DrawRootElements(canvas, document, assets, RootCanvasKind.Background, document.Elements.Count);
            DrawMainRoot(canvas, document, assets, includeBackground, includeCanvasBackground && !photograph, includeGrid);
            DrawRootElements(canvas, document, assets, RootCanvasKind.Foreground, document.Elements.Count);
        }
        finally { canvas.RestoreToCount(saved); }
    }

    private void DrawMainRoot(SKCanvas canvas, SceneDocument document, AssetLibrary assets,
        bool includeBackground, bool includeCanvasBackground, bool includeGrid)
    {
        if (UsesDepthComposition(document))
            DrawDepthScene(canvas, document, assets, includeBackground, includeCanvasBackground, includeGrid, annotationsOnly: true);
        else
        {
            DrawPlaneColor(canvas, document, includeCanvasBackground);
            if (includeGrid) DrawGrid(canvas, document);
            DrawRootElements(canvas, document, assets, RootCanvasKind.Main, document.Elements.Count);
        }
    }

    private void DrawRootElements(SKCanvas canvas, SceneDocument document, AssetLibrary assets, RootCanvasKind root, int end)
    {
        for (int index = 0; index < Math.Min(end, document.Elements.Count); index++)
        {
            var element = document.Elements[index];
            if (!element.Visible || RootOf(document, element) != root) continue;
            if (ElementGeometry.IsLiveEffect(element.Kind)) DrawLivePixelate(canvas, document.Elements, index, assets);
            else DrawElement(canvas, element, assets, TransformFor(document), document);
        }
    }

    /// <summary>Raw lower authored ink for a screen-space filter, without final annotation opacity.</summary>
    private void DrawRootPrefix(SKCanvas canvas, SceneDocument document, AssetLibrary assets, int end)
    {
        DrawBackground(canvas, document, assets, includeCanvasBackground: false, sampling: true);
        DrawRootElements(canvas, document, assets, RootCanvasKind.Background, end);
        var root = end < document.Elements.Count ? RootOf(document, document.Elements[end]) : RootCanvasKind.Foreground;
        if (root == RootCanvasKind.Background) return;
        if (root == RootCanvasKind.Foreground)
            DrawMainRoot(canvas, document, assets, includeBackground: true,
                includeCanvasBackground: document.Background is not { Visible: true }, includeGrid: true);
        else
        {
            DrawPlaneColor(canvas, document, document.Background is not { Visible: true });
            DrawGrid(canvas, document);
            DrawRootElements(canvas, document, assets, RootCanvasKind.Main, end);
        }
        if (root == RootCanvasKind.Foreground)
            DrawRootElements(canvas, document, assets, RootCanvasKind.Foreground, end);
    }
}
