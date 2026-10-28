using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

namespace FloatCanvas.App;

/// <summary>A bounded snapshot of the annotation layer, without reading the desktop.</summary>
internal static class WorkspacePreview
{
    internal static byte[]? TryRender(SceneDocument document, AssetLibrary assets, TimeSpan animationTime = default)
    {
        try { return Render(document, assets, animationTime); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or System.IO.IOException or OverflowException)
        { return null; }
    }
    internal static byte[]? Render(SceneDocument document, AssetLibrary assets, TimeSpan animationTime = default)
    {
        var viewport = document.Viewport.Normalize();
        if (viewport.IsEmpty || !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height)) return null;
        double scale = Math.Min(1, 512 / Math.Max(viewport.Width, viewport.Height));
        int width = Math.Max(1, (int)Math.Ceiling(viewport.Width * scale));
        int height = Math.Max(1, (int)Math.Ceiling(viewport.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface is null) return null;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale((float)scale);
        surface.Canvas.Translate(-(float)viewport.X, -(float)viewport.Y);
        using var renderer = new SceneRenderer { AnimationTime = animationTime };
        renderer.Draw(surface.Canvas, document, assets, includeBackground: false);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}
