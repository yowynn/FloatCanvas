using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Core;
using System.Diagnostics.CodeAnalysis;

namespace FloatCanvas.Storage;

public static class SceneValidation
{
    public static void Validate(SceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version is not (1 or 2 or 3 or 4 or 5 or 6 or SceneDocument.CurrentVersion)) throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", document.Version));
        Require(Enum.IsDefined(document.Kind), L.Get("Scene.InvalidDocumentKind"));
        Require(document.Elements is not null && document.DisplayLayout is not null && document.Groups is not null, L.Get("Scene.MissingCollections"));
        SceneLimits.ValidateCapacity(document);
        Require(document.Groups!.Count <= SceneLimits.MaximumElements, L.Get("Scene.ElementCountExceeded"));
        Require(Unit(document.LayerOpacity), L.Get("Scene.InvalidLayerOpacity"));
        Require(document.NextNumber >= 1, L.Get("Scene.InvalidNumberCounter"));
        Require(Rect(document.Viewport) && document.Viewport.Width > 0 && document.Viewport.Height > 0, L.Get("Scene.InvalidViewport"));
        ValidateCanvas(document);
        var ids = new HashSet<Guid>();
        foreach (var element in document.Elements) ValidateElement(element, ids);
        if (document.Canvas.Projection.Enabled)
        {
            var mapping = CanvasMapping.Create(document);
            foreach (var element in document.Elements.Where(e => e.IsBillboard))
            {
                // A retained layer can lie outside the current drawing domain. Its local
                // geometry is still validated above; rendering hides unsafe anchors.
                if (mapping.TryGetDepthScale(element.BillboardAnchor, out _))
                    Require(mapping.ElementToScreen(element).TryTransformBounds(ElementGeometry.StrokedLocalBounds(element), out var projected) && Rect(projected),
                        L.Get("Scene.InvalidElementTransform"));
            }
        }
        ValidateGroups(document, ids);
        if (document.Background is not null)
        {
            Require(document.Background.Kind == ElementKind.Image, L.Get("Scene.BackgroundMustBeImage"));
            ValidateElement(document.Background, ids);
            Require(document.Background.GroupId is null, L.Get("Scene.BackgroundMustBeImage"));
        }
        foreach (var display in document.DisplayLayout!)
            Require(display is not null && display.DeviceName is not null && Rect(display.Bounds) && display.Bounds.Width > 0 && display.Bounds.Height > 0 && Positive(display.DpiX) && Positive(display.DpiY), L.Get("Scene.InvalidDisplayLayout"));
    }
    public static void Validate(WorkspaceState workspace)
    {
        Require(workspace is not null && workspace.LiveDocument is not null, L.Get("Recovery.MissingLiveCanvas"));
        Validate(workspace!.LiveDocument!);
        Require(workspace.LiveDocument.Kind == DocumentKind.LiveOverlay, L.Get("Recovery.InvalidLiveDocumentKind"));
        if (workspace.CaptureDocument is not null)
        {
            Validate(workspace.CaptureDocument);
            Require(workspace.CaptureDocument.Kind == DocumentKind.FrozenCapture, L.Get("Recovery.InvalidCaptureDocumentKind"));
        }
        Require(Point(workspace.CaptureOrigin), L.Get("Recovery.InvalidCaptureOrigin"));
    }
    private static void ValidateElement(CanvasElement element, HashSet<Guid> ids)
    {
        Require(element is not null, L.Get("Scene.NullElement"));
        Require(element!.Id != Guid.Empty && ids.Add(element.Id), L.Get("Scene.InvalidElementId"));
        Require(Enum.IsDefined(element.Kind), L.Get("Scene.InvalidElementKind"));
        Require(element.Name is null || element.Name.Length <= EditorSession.MaximumLayerNameLength, L.Get("Layers.NameTooLong"));
        Require(Nonnegative(element.Width) && Nonnegative(element.Height), L.Get("Scene.InvalidElementSize"));
        Require(element.CornerRadius is null || Nonnegative(element.CornerRadius.Value), L.Get("Scene.InvalidElementSize"));
        Require(Enum.IsDefined(element.Symbol), L.Get("Scene.InvalidElementKind"));
        var t = element.Transform;
        Require(Point(element.BillboardAnchor) && double.IsFinite(element.BillboardHeight), L.Get("Scene.InvalidElementTransform"));
        Require(Finite(t.M11, t.M12, t.M21, t.M22, t.OffsetX, t.OffsetY) && t.TryInverse(out _), L.Get("Scene.InvalidElementTransform"));
        Require(element.Points is not null && element.Points.Count <= SceneLimits.MaximumPathPoints && element.Points.All(Point), L.Get("Scene.InvalidElementPath"));
        Require(element.Style is not null && element.Text is not null && element.Text.Length <= 2_000_000, L.Get("Scene.InvalidElementContent"));
        var s = element.Style!;
        Require(Nonnegative(s.StrokeWidth) && Unit(s.Opacity) && Positive(s.FontSize) && s.FontFamily is not null && s.FontFamily.Length <= 1024 && Enum.IsDefined(s.TextAlignment), L.Get("Scene.InvalidElementStyle"));
        Require(Enum.IsDefined(s.ArrowHeadStyle) && Nonnegative(s.ArrowHeadSize) && s.ArrowHeadSize <= 512 &&
            (s.StartArrowHead is null || Enum.IsDefined(s.StartArrowHead.Value)) &&
            (s.EndArrowHead is null || Enum.IsDefined(s.EndArrowHead.Value)), L.Get("Scene.InvalidArrowHead"));
        Require(Enum.IsDefined(s.StrokePattern), L.Get("Scene.InvalidElementStyle"));
        Require(element.Number >= 1, L.Get("Scene.InvalidElementNumber"));
        Require(element.Kind != ElementKind.Image || PackageCodec.IsAssetId(element.AssetId), L.Get("Scene.InvalidImageReference"));
        Require(element.AssetId is null || PackageCodec.IsAssetId(element.AssetId), L.Get("Scene.InvalidAssetReference"));
        Require(element.ImageCrop is null || NormalizedRect(element.ImageCrop.Value, positive: true), L.Get("Scene.InvalidImageCrop"));
        Require(element.Effects is not null && element.Effects.Count <= 10_000, L.Get("Scene.InvalidEffects"));
        Require(element.Effects!.Count == 0 || element.Kind == ElementKind.Image || ElementGeometry.IsLiveEffect(element.Kind), L.Get("Scene.EffectsRequireImage"));
        Require(!ElementGeometry.IsLiveEffect(element.Kind) || element.Effects!.Count == 1 &&
            element.Effects[0] is { } liveEffect && liveEffect.Kind == (element.Kind == ElementKind.LiveBlur ? ImageEffectKind.Blur : ImageEffectKind.Pixelate) &&
            element.Width > 0 && element.Height > 0,
            L.Get("Scene.InvalidImageEffect"));
        var effectIds = new HashSet<Guid>();
        foreach (var effect in element.Effects)
            Require(effect is not null && effect.Id != Guid.Empty && effectIds.Add(effect.Id) && Enum.IsDefined(effect.Kind) && NormalizedRect(effect.Bounds, positive: false) && Positive(effect.Amount), L.Get("Scene.InvalidImageEffect"));
        if (element.ProjectivePlacement is { } placement)
        {
            var local = ElementGeometry.StrokedLocalBounds(element);
            Require(placement.IsFinite && placement.TryInverse(out _) && placement.Denominator(local.Center) > 0 &&
                placement.TryTransformBounds(local, out _), L.Get("Scene.InvalidElementTransform"));
        }
        Require(ElementGeometry.LocalToPlane(element).TryTransformBounds(ElementGeometry.StrokedLocalBounds(element), out var rendered) && Rect(rendered), L.Get("Scene.InvalidRenderedBounds"));
    }
    private static void ValidateCanvas(SceneDocument document)
    {
        var canvas = document.Canvas;
        Require(canvas is not null && canvas.Projection is not null && canvas.Grid is not null, L.Get("Scene.InvalidCanvas"));
        var projection = canvas.Projection;
        Require(projection.Columns is >= 1 and <= CanvasCalibration.MaximumGridCount && projection.Rows is >= 1 and <= CanvasCalibration.MaximumGridCount,
            L.Get("Scene.InvalidCanvas"));
        Require(Point(projection.Origin) && projection.CellSize is >= .01 and <= 1_000_000 && double.IsFinite(projection.CellSize) &&
            projection.AlgorithmVersion == CanvasCalibration.AlgorithmVersion && projection.Matrix.IsFinite && projection.Matrix.TryInverse(out _), L.Get("Scene.InvalidCanvas"));
        Require(projection.LockedGridCellSize is null || projection.LockedGridCellSize is >= .01 and <= 1_000_000 && double.IsFinite(projection.LockedGridCellSize.Value), L.Get("Scene.InvalidCanvas"));
        Require(projection.Quad is not null && projection.Quad.Count is 0 or 4 && projection.Quad.All(Point), L.Get("Scene.InvalidCanvas"));
        Require(canvas.Grid.Width is > 0 and <= 64 && double.IsFinite(canvas.Grid.Width) && Enum.IsDefined(canvas.Grid.Pattern), L.Get("Scene.InvalidCanvas"));
        Require(canvas.Grid.FlatCellSize is null || canvas.Grid.FlatCellSize is >= CanvasGridGeometry.MinimumCellSize and <= CanvasGridGeometry.MaximumCellSize &&
            double.IsFinite(canvas.Grid.FlatCellSize.Value), L.Get("Scene.InvalidCanvas"));
        Require(canvas.Grid.FlatOrigin is null || Point(canvas.Grid.FlatOrigin.Value), L.Get("Scene.InvalidCanvas"));
        if (projection.Quad.Count == 0)
        {
            Require(!projection.Enabled && projection.Matrix == Projective2D.Identity, L.Get("Scene.InvalidCanvas"));
            return;
        }
        Require(CanvasCalibration.TryGridToScreen(projection.Columns, projection.Rows, projection.Quad, out _), L.Get("Scene.InvalidCanvas"));
        PointD[] corners = [projection.Origin, projection.Origin + new PointD(projection.Columns * projection.CellSize, 0),
            projection.Origin + new PointD(projection.Columns * projection.CellSize, projection.Rows * projection.CellSize),
            projection.Origin + new PointD(0, projection.Rows * projection.CellSize)];
        for (int i = 0; i < corners.Length; i++)
            Require(projection.Matrix.Denominator(corners[i]) > 0 && projection.Matrix.TryTransformPoint(corners[i], out var actual) &&
                actual.DistanceTo(projection.Quad[i]) <= 1e-5, L.Get("Scene.InvalidCanvas"));
        // The screen may contain the horizon; drawing and editing clip against the finite ground domain.
        Require(CanvasCalibration.IsValidProjection(projection.Matrix), L.Get("Scene.InvalidCanvas"));
    }
    private static void ValidateGroups(SceneDocument document, HashSet<Guid> ids)
    {
        var groups = new Dictionary<Guid, CanvasGroup>();
        foreach (var group in document.Groups)
        {
            Require(group is not null && group.Id != Guid.Empty && ids.Add(group.Id) && group.Name is not null && group.Name.Length <= 1024,
                L.Get("Scene.InvalidElementId"));
            groups.Add(group.Id, group);
        }
        foreach (var group in document.Groups)
        {
            var visited = new HashSet<Guid> { group.Id };
            var parent = group.ParentId;
            while (parent is { } value)
            {
                Require(visited.Count < 64 && visited.Add(value) && groups.ContainsKey(value), L.Get("Scene.InvalidElementId"));
                parent = groups[value].ParentId;
            }
        }
        var used = new HashSet<Guid>();
        foreach (var element in document.Elements)
        {
            var parent = element.GroupId;
            while (parent is { } value)
            {
                Require(groups.ContainsKey(value), L.Get("Scene.InvalidElementId"));
                if (!used.Add(value)) break;
                parent = groups[value].ParentId;
            }
        }
        Require(document.Groups.All(g => used.Contains(g.Id)), L.Get("Scene.InvalidElementId"));
    }
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private static bool Point(PointD p) => Finite(p.X, p.Y);
    private static bool Rect(RectD r) => Finite(r.X, r.Y, r.Width, r.Height) && r.Width >= 0 && r.Height >= 0 && Finite(r.Right, r.Bottom);
    private static bool Unit(double value) => double.IsFinite(value) && value >= 0 && value <= 1;
    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool Nonnegative(double value) => double.IsFinite(value) && value >= 0;
    private static bool NormalizedRect(RectD r, bool positive) => Rect(r) && r.X >= 0 && r.Y >= 0 && r.Right <= 1.000000001 && r.Bottom <= 1.000000001 && (!positive || r.Width > 0 && r.Height > 0);
    private static void Require([DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
