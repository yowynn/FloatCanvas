using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

/// <summary>An authored canvas used only when a graph is opened independently.</summary>
public sealed class SubprojectCanvasSettings
{
    public PointD Origin { get; set; }
    public RectD Viewport { get; set; } = new(0, 0, 1920, 1080);
    public CanvasSettings Canvas { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CanvasElement? Background { get; set; }
    public double LayerOpacity { get; set; } = 1;
    [JsonIgnore] public IEnumerable<string> ReferencedAssetIds => Background?.AssetId is { } id ? [id] : [];

    public SubprojectCanvasSettings Clone(IReadOnlyDictionary<string, string>? assetIds = null)
    {
        var background = Background?.Clone();
        if (background?.AssetId is { } id && assetIds is not null) background.AssetId = assetIds[id];
        return new() { Origin = Origin, Viewport = Viewport, Canvas = Canvas.Clone(), Background = background, LayerOpacity = LayerOpacity };
    }

    public static SubprojectCanvasSettings FromDocument(SceneDocument document, PointD origin = default) => new()
    {
        Origin = origin, Viewport = document.Viewport, Canvas = document.Canvas.Clone(),
        Background = document.Background?.Clone(), LayerOpacity = document.LayerOpacity
    };

    /// <summary>Copies the independent canvas header, never content or monitor identities.</summary>
    public void ApplyTo(SceneDocument document)
    {
        document.Viewport = Viewport; document.Canvas = Canvas.Clone();
        document.Background = Background?.Clone(); document.LayerOpacity = LayerOpacity;
    }

    /// <summary>Changes the content coordinate basis while retaining the same screen-space preview.</summary>
    public SubprojectCanvasSettings Rebase(LayerPlacement newToPreview, RectD contentBounds)
    {
        var result = Clone(); result.Origin = default;
        var header = new SceneDocument(); ApplyTo(header);
        var matrix = CanvasMapping.Create(header).PlaneToScreen.Compose(Projective2D.FromAffine(newToPreview.PlaneTransform));
        var projection = result.Canvas.Projection;
        // A translated flat canvas stays flat. General affine and perspective
        // transforms share the existing calibrated four-corner representation.
        bool translation = Math.Abs(matrix.M11 - 1) < 1e-10 && Math.Abs(matrix.M22 - 1) < 1e-10 &&
            Math.Abs(matrix.M12) < 1e-10 && Math.Abs(matrix.M21) < 1e-10 && Math.Abs(matrix.M31) < 1e-10 && Math.Abs(matrix.M32) < 1e-10 && Math.Abs(matrix.M33 - 1) < 1e-10;
        if (!projection.Enabled && translation)
            result.Canvas.Grid.FlatTranslation = new(matrix.M13, matrix.M23);
        else
        {
            // The source calibration is a view reference, not the selected ink's
            // extent. Tiny shapes must never collapse its four-corner geometry.
            var reference = Canvas.Projection.Quad.Count == 4 ? Canvas.Projection : CanvasCalibration.CreateInitial(Viewport, projection.Columns, projection.Rows);
            var sourceBounds = CanvasProjectionGeometry.ResolvePlaneBounds(reference);
            var inverse = newToPreview.PlaneTransform.Inverse();
            var bounds = inverse.TransformBounds(sourceBounds);
            // Rotating a rectangle around a perspective horizon can put the
            // enclosing axis-aligned box beyond the finite source domain. Keep
            // the reference inside that domain without changing the mapping.
            var center = inverse.TransformPoint(sourceBounds.Center);
            for (int attempt = 0; attempt < 32 && !Positive(bounds); attempt++)
                bounds = new(center.X - bounds.Width * .25, center.Y - bounds.Height * .25, bounds.Width * .5, bounds.Height * .5);
            PointD[] corners = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];
            projection.Enabled = true; projection.Matrix = matrix; projection.PlaneBounds = bounds;
            projection.Origin = new(bounds.X, bounds.Y); projection.ReferenceBounds = bounds;
            projection.PinPlanePoint = CanvasProjectionGeometry.PinPoint(bounds, projection.PinLocation);
            projection.Quad = corners.Select(matrix.TransformPoint).ToList();
            if (result.Canvas.Grid.PlaneOrigin is { } gridOrigin) result.Canvas.Grid.PlaneOrigin = inverse.TransformPoint(gridOrigin);

            bool Positive(RectD candidate) => new[] { new PointD(candidate.Left, candidate.Top), new PointD(candidate.Right, candidate.Top),
                new PointD(candidate.Right, candidate.Bottom), new PointD(candidate.Left, candidate.Bottom) }.All(point => matrix.Denominator(point) > 0);
        }
        return result;
    }
}
