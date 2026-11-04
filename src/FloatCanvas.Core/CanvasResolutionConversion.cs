namespace FloatCanvas.Core;

/// <summary>A converted working copy. Source bytes and external resources are never rewritten here.</summary>
public sealed record CanvasResolutionConversionResult(SceneDocument Document, Affine2D SourceToTarget);

public static class CanvasResolutionConversion
{
    public static bool NeedsConversion(SceneDocument source, RectD targetScreen)
    {
        RequireTarget(targetScreen);
        if (source.Kind != DocumentKind.LiveOverlay) return false;
        var reference = CanvasAuthoringReference.FromSavedDocument(source).ReferenceScreenBounds;
        return Math.Abs(reference.Width - targetScreen.Width) > 1e-6 || Math.Abs(reference.Height - targetScreen.Height) > 1e-6;
    }

    public static CanvasResolutionConversionResult Prepare(SceneDocument source, RectD targetScreen)
    {
        ArgumentNullException.ThrowIfNull(source); RequireTarget(targetScreen);
        if (source.Kind != DocumentKind.LiveOverlay) throw new ArgumentException("Captured pixel documents are not screen-resolution conversions.", nameof(source));
        var reference = CanvasAuthoringReference.FromSavedDocument(source);
        var content = reference.EffectiveReferenceBounds;
        RequireTarget(content); RequireTarget(reference.Frame);
        double scale = Math.Min(targetScreen.Width / content.Width, targetScreen.Height / content.Height);
        var offset = targetScreen.Center - content.Center * scale;
        var transform = new Affine2D(scale, 0, 0, scale, offset.X, offset.Y);
        var candidate = CanvasViewApplication.Prepare(source, transform);
        // The full physical screen suppresses a repeat compatibility prompt. Only
        // its effective authored reference participates in the NEXT conversion;
        // otherwise added aspect-ratio bars would cause cumulative shrinking.
        candidate.AuthoringReference = new(transform.TransformBounds(reference.Frame), targetScreen, transform.TransformBounds(content));
        candidate.Viewport = targetScreen;
        foreach (var display in candidate.DisplayLayout) display.Bounds = transform.TransformBounds(display.Bounds);
        // A converted project is a new working copy, not a refresh of owners of
        // the original source. Its dependency definition/link identities stay intact.
        candidate.ResourceId = Guid.NewGuid(); candidate.Version = SceneDocument.CurrentVersion;
        return new(candidate, transform);
    }

    private static void RequireTarget(RectD bounds)
    {
        if (!double.IsFinite(bounds.X + bounds.Y + bounds.Width + bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds));
    }
}
