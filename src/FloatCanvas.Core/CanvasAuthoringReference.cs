using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

/// <summary>Stable authored pixels, independent of the current editor viewport and monitor synchronization.</summary>
public sealed record CanvasAuthoringReference(RectD Frame, RectD ReferenceScreenBounds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RectD? ContentReferenceBounds = null)
{
    [JsonIgnore] public RectD EffectiveReferenceBounds => ContentReferenceBounds ?? ReferenceScreenBounds;
    /// <summary>Legacy fallback uses saved evidence only. It never consults the machine opening the project.</summary>
    public static CanvasAuthoringReference FromSavedDocument(SceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.AuthoringReference ?? new(document.Viewport, document.DisplayLayout.FirstOrDefault()?.Bounds ?? document.Viewport);
    }
}
