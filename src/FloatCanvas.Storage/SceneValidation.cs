using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Core;
using System.Diagnostics.CodeAnalysis;

namespace FloatCanvas.Storage;

public static class SceneValidation
{
    public static void Validate(SceneDocument document) => ValidateDocument(document, allowMirroredCanvas: false);
    /// <summary>A detached independent editing header permits the same mirrored calibration as persisted prefab previews.</summary>
    public static void ValidateIndependentCanvas(SceneDocument document) => ValidateDocument(document, allowMirroredCanvas: true);
    private static void ValidateDocument(SceneDocument document, bool allowMirroredCanvas)
    {
        ArgumentNullException.ThrowIfNull(document);
        PrefabClosure.RequireSourceGraphs(document);
        if (!SceneFormatMigration.Supports(document.Version)) throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", document.Version));
        Require(Enum.IsDefined(document.Kind), L.Get("Scene.InvalidDocumentKind"));
        Require(document.Elements is not null && document.DisplayLayout is not null && document.Groups is not null && document.Instances is not null && document.Definitions is not null && document.Provenance is not null, L.Get("Scene.MissingCollections"));
        Require(document.RootCanvasAssignments is not null && (document.Version >= 16 || document.RootCanvasAssignments.Count == 0), "Root canvases require scene format 16.");
        Require(document.RootPreviewCanvases is not null && (document.Version >= 17 || document.RootPreviewCanvases.Count == 0), "Subproject canvases require scene format 17.");
        foreach (var pair in document.RootPreviewCanvases)
        {
            Require(pair.Key is RootCanvasKind.Foreground or RootCanvasKind.Background && pair.Value is not null, "Invalid root preview canvas.");
            ValidatePreview(pair.Value);
        }
        ValidatePreviewVersion(document, document.Version);
        if (document.Version < 11) Require(document.Instances.Count == 0 && document.Definitions.Count == 0, L.Format("Scene.UnsupportedVersion", document.Version));
        if (document.Version < 13) Require(!document.Elements.Concat(document.Definitions.SelectMany(definition => definition.Graph.Elements)).Append(document.Background)
            .OfType<CanvasElement>().Any(element => element.Kind == ElementKind.Presentation || element.Presentation is not null), "Presentation content requires scene format 13.");
        if (document.Version < 14) Require(!document.Elements.Concat(document.Definitions.SelectMany(definition => definition.Graph.Elements)).Append(document.Background)
            .OfType<CanvasElement>().Any(element => element.WindowSource is not null), "Window sources require scene format 14.");
        if (document.Version < 15) Require(!document.Elements.Concat(document.Definitions.SelectMany(definition => definition.Graph.Elements)).Append(document.Background)
            .OfType<CanvasElement>().Any(element => element.Presentation is { } settings &&
                (settings.DisplayStyle != PresentationDisplayStyle.Log || settings.KeyboardLayout is not null)), "Keyboard display styles and layouts require scene format 15.");
        SceneLimits.ValidateCapacity(document);
        Require(document.Groups!.Count <= SceneLimits.MaximumElements, L.Get("Scene.ElementCountExceeded"));
        Require(Unit(document.LayerOpacity), L.Get("Scene.InvalidLayerOpacity"));
        Require(Rect(document.Viewport) && document.Viewport.Width > 0 && document.Viewport.Height > 0, L.Get("Scene.InvalidViewport"));
        if (document.AuthoringReference is { } reference)
        {
            Require(document.Version >= 18, "Authored canvas references require scene format 18.");
            Require(Rect(reference.Frame) && !reference.Frame.IsEmpty && Rect(reference.ReferenceScreenBounds) && !reference.ReferenceScreenBounds.IsEmpty,
                "Invalid authored canvas reference.");
            Require(reference.ContentReferenceBounds is not { } content || Rect(content) && !content.IsEmpty, "Invalid authored content reference.");
        }
        ValidateCanvas(document, allowMirroredCanvas);
        var ids = new HashSet<Guid>();
        var graph = ValidateGraph(document, ids);
        foreach (var definition in document.Definitions)
        {
            if (definition.PreviewCanvas is { } preview) ValidatePreview(preview);
            ValidateGraph(definition.Graph, new());
        }
        if (document.Version < 10)
            Require(document.RootOrder is null && document.Groups.All(group => group.Transform == Affine2D.Identity && group.ElevationScale == 1 &&
                group.Visible && !group.Locked && group.OrderMode == GroupOrderMode.LegacyPassThrough && group.ChildOrder is null),
                L.Format("Scene.UnsupportedVersion", document.Version));
        var evaluation = document.Instances.Count > 0 ? SceneEvaluation.Build(document) : null;
        var rendered = evaluation is null ? document.Elements.Select(graph.ToWorld) : evaluation.RenderDocument.Elements;
        foreach (var placed in rendered)
        {
            Require(LayerPlacementFinite(placed.Transform) && Point(placed.BillboardAnchor) && double.IsFinite(placed.BillboardHeight) &&
                ElementGeometry.LocalToPlane(placed).TryTransformBounds(ElementGeometry.StrokedLocalBounds(placed), out var bounds) && Rect(bounds),
                L.Get("Scene.InvalidRenderedBounds"));
        }
        if (document.Canvas.Projection.Enabled)
        {
            var mapping = CanvasMapping.Create(document);
            foreach (var element in rendered.Where(e => e.IsBillboard))
            {
                if (SceneCanvasRoots.Resolve(evaluation?.RenderDocument ?? document, element.Id, evaluation is null ? graph : null) != RootCanvasKind.Main) continue;
                // A retained layer can lie outside the current drawing domain. Its local
                // geometry is still validated above; rendering hides unsafe anchors.
                if (mapping.TryGetDepthScale(element.BillboardAnchor, out _))
                    Require(mapping.ElementToScreen(element).TryTransformBounds(ElementGeometry.StrokedLocalBounds(element), out var projected) && Rect(projected),
                        L.Get("Scene.InvalidElementTransform"));
            }
        }
        if (document.Background is not null)
        {
            Require(document.Background.Kind == ElementKind.Image, L.Get("Scene.BackgroundMustBeImage"));
            Require(document.Background.WindowSource is null, "A canvas background cannot be a live window source.");
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
        foreach (var state in PrefabDraftState.Enumerate(workspace.PendingPrefabEdit))
        {
            Require(workspace.LiveDocument.Version >= 11 && state.OwnerResourceId != Guid.Empty && state.DefinitionId != Guid.Empty && state.ExpectedRevision != Guid.Empty,
                "Invalid prefab edit recovery identity.");
            Require(state.DraftDocument is not null && state.ReturnSelection is not null && state.DraftSelection is not null &&
                state.ReturnSelection.Count <= SceneLimits.MaximumElements && state.DraftSelection.Count <= SceneLimits.MaximumElements &&
                state.ReturnSelection.All(id => id != Guid.Empty) && state.DraftSelection.All(id => id != Guid.Empty) &&
                state.ReturnSelection.Distinct().Count() == state.ReturnSelection.Count && state.DraftSelection.Distinct().Count() == state.DraftSelection.Count &&
                state.ReturnScopeId != Guid.Empty && state.DraftScopeId != Guid.Empty, "Invalid prefab edit recovery selection.");
            Require(state.SourcePath is null || state.SourcePath.Length is > 0 and <= 32767 && !state.SourcePath.Any(char.IsControl), "Invalid prefab edit source path.");
            Require(state.SourceFileSha256 is null || state.SourceFileSha256.Length == 64 && state.SourceFileSha256.All(Uri.IsHexDigit), "Invalid prefab edit file identity.");
            Require(state.PreviewOrigin is null || state.DraftDocument.Version >= 17 && Point(state.PreviewOrigin.Value), "Invalid prefab preview recovery origin.");
            Require(!state.IsSourceProject || state.SourcePath is not null && state.SourceFileSha256 is not null, "Missing source project recovery context.");
            ValidateDocument(state.DraftDocument, allowMirroredCanvas: state.DraftDocument.Version >= 17);
        }
    }
    public static void Validate(PrefabPackage package)
    {
        Require(package is not null && SceneFormatMigration.SupportsPrefab(package.Version) && package.Provenance is not null, "Unsupported prefab package version.");
        if (package.Version < 15) Require(!package.Definitions.SelectMany(definition => definition.Graph.Elements)
            .Any(element => element.Presentation is { } settings && (settings.DisplayStyle != PresentationDisplayStyle.Log || settings.KeyboardLayout is not null)),
            "Keyboard display styles and layouts require scene format 15.");
        if (package.Version < 13) Require(!package.Definitions.SelectMany(definition => definition.Graph.Elements)
            .Any(element => element.Kind == ElementKind.Presentation || element.Presentation is not null), "Presentation content requires scene format 13.");
        if (package.Version < 14) Require(!package.Definitions.SelectMany(definition => definition.Graph.Elements)
            .Any(element => element.WindowSource is not null), "Window sources require scene format 14.");
        _ = PrefabClosure.Validate(package);
        foreach (var definition in package.Definitions)
        {
            Require(package.Version >= 17 || definition.PreviewCanvas is null, "Subproject canvases require scene format 17.");
            ValidatePreviewVersion(definition.Graph, package.Version);
            if (definition.PreviewCanvas is { } preview) ValidatePreview(preview);
            ValidateGraph(definition.Graph, new());
        }
        var entry = new PrefabInstance { DefinitionId = package.EntryDefinitionId };
        var view = new SceneDocument { Instances = [entry], RootOrder = [entry.Id], Definitions = package.Definitions };
        foreach (var element in SceneEvaluation.Build(view).RenderDocument.Elements)
            Require(ElementGeometry.LocalToPlane(element).TryTransformBounds(ElementGeometry.StrokedLocalBounds(element), out var bounds) && Rect(bounds),
                L.Get("Scene.InvalidRenderedBounds"));
        SceneLimits.ValidatePayloadJsonCapacity(package);
    }
    private static SceneGraph ValidateGraph(SceneNodeGraph source, HashSet<Guid> ids)
    {
        foreach (var element in source.Elements) ValidateElement(element, ids);
        foreach (var instance in source.Instances)
            Require(instance is not null && instance.Id != Guid.Empty && ids.Add(instance.Id) && instance.DefinitionId != Guid.Empty &&
                instance.Name is not null && instance.Name.Length <= EditorSession.MaximumLayerNameLength, L.Get("Scene.InvalidElementId"));
        var graph = ValidateGroups(source, ids);
        foreach (var element in source.Elements)
        {
            var placed = graph.ToWorld(element);
            Require(LayerPlacementFinite(placed.Transform) && Point(placed.BillboardAnchor) && double.IsFinite(placed.BillboardHeight) &&
                ElementGeometry.LocalToPlane(placed).TryTransformBounds(ElementGeometry.StrokedLocalBounds(placed), out var bounds) && Rect(bounds), L.Get("Scene.InvalidRenderedBounds"));
        }
        return graph;
    }
    private static void ValidatePreviewVersion(SceneNodeGraph graph, int version)
    {
        Require(version >= 17 || graph.Groups.All(group => group.PreviewCanvas is null), "Subproject canvases require scene format 17.");
        if (graph is SceneDocument document)
            foreach (var definition in document.Definitions)
            {
                Require(version >= 17 || definition.PreviewCanvas is null, "Subproject canvases require scene format 17.");
                ValidatePreviewVersion(definition.Graph, version);
            }
    }
    private static void ValidatePreview(SubprojectCanvasSettings preview)
    {
        Require(Point(preview.Origin) && Rect(preview.Viewport) && preview.Viewport.Width > 0 && preview.Viewport.Height > 0 && Unit(preview.LayerOpacity), "Invalid subproject canvas.");
        ValidateCanvas(new SceneDocument { Canvas = preview.Canvas }, allowMirrored: true);
        if (preview.Background is { } background)
        {
            Require(background.Kind == ElementKind.Image && background.WindowSource is null && background.GroupId is null, L.Get("Scene.BackgroundMustBeImage"));
            ValidateElement(background, new());
        }
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
        Require(ElementAppearance.IsValidVariant(element.Kind, s.Variant), L.Get("Scene.InvalidElementStyle"));
        Require(AppearanceRegistry.IsValidColorRoles(s.AdditionalColors) && Enum.IsDefined(element.NumberTextSizeMode), L.Get("Scene.InvalidElementStyle"));
        Require(PresentationLimits.IsValidElement(element), L.Get("Scene.InvalidElementStyle"));
        Require(WindowSourceLimits.IsValidElement(element), "Invalid live window source.");
        Require(Nonnegative(s.StrokeWidth) && Unit(s.Opacity) && Positive(s.FontSize) && s.FontFamily is not null && s.FontFamily.Length <= 1024 && Enum.IsDefined(s.TextAlignment), L.Get("Scene.InvalidElementStyle"));
        Require(Enum.IsDefined(s.ArrowHeadStyle) && Nonnegative(s.ArrowHeadSize) && s.ArrowHeadSize <= 512 &&
            (s.StartArrowHead is null || Enum.IsDefined(s.StartArrowHead.Value)) &&
            (s.EndArrowHead is null || Enum.IsDefined(s.EndArrowHead.Value)), L.Get("Scene.InvalidArrowHead"));
        Require(Enum.IsDefined(s.StrokePattern), L.Get("Scene.InvalidElementStyle"));
        Require(element.Kind != ElementKind.Image || element.WindowSource is not null || PackageCodec.IsAssetId(element.AssetId), L.Get("Scene.InvalidImageReference"));
        Require(element.AssetId is null || PackageCodec.IsAssetId(element.AssetId), L.Get("Scene.InvalidAssetReference"));
        Require(element.ImageCrop is null || NormalizedRect(element.ImageCrop.Value, positive: true), L.Get("Scene.InvalidImageCrop"));
        Require(element.ImageFrame is null || element.Kind == ElementKind.Image && element.ImageFrame.Count is >= 3 and <= SceneLimits.MaximumPathPoints &&
            element.ImageFrame.All(p => Point(p) && p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1), L.Get("Scene.InvalidImageCrop"));
        Require(Nonnegative(element.ImageBorderWidth), L.Get("Scene.InvalidElementStyle"));
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
    private static void ValidateCanvas(SceneDocument document, bool allowMirrored = false)
    {
        var canvas = document.Canvas;
        Require(canvas is not null && canvas.Projection is not null && canvas.Grid is not null, L.Get("Scene.InvalidCanvas"));
        var projection = canvas.Projection;
        Require(projection.Columns is >= 1 and <= CanvasCalibration.MaximumGridCount && projection.Rows is >= 1 and <= CanvasCalibration.MaximumGridCount,
            L.Get("Scene.InvalidCanvas"));
        Require(Point(projection.Origin) && projection.CellSize is >= .01 and <= 1_000_000 && double.IsFinite(projection.CellSize) &&
            projection.AlgorithmVersion is 1 or 2 or CanvasCalibration.AlgorithmVersion && projection.Matrix.IsFinite && projection.Matrix.TryInverse(out _), L.Get("Scene.InvalidCanvas"));
        Require(Enum.IsDefined(projection.PinLocation) && (projection.PinPlanePoint is null || Point(projection.PinPlanePoint.Value)) &&
            Point(canvas.Grid.FlatTranslation), L.Get("Scene.InvalidCanvas"));
        Require(projection.ReferenceBounds is null || Rect(projection.ReferenceBounds.Value) && !projection.ReferenceBounds.Value.IsEmpty,
            L.Get("Scene.InvalidCanvas"));
        Require(projection.LockedGridCellSize is null || projection.LockedGridCellSize is >= .01 and <= 1_000_000 && double.IsFinite(projection.LockedGridCellSize.Value), L.Get("Scene.InvalidCanvas"));
        Require(projection.Quad is not null && projection.Quad.Count is 0 or 4 && projection.Quad.All(Point), L.Get("Scene.InvalidCanvas"));
        Require(canvas.Grid.Width is > 0 and <= 64 && double.IsFinite(canvas.Grid.Width) && Enum.IsDefined(canvas.Grid.Pattern), L.Get("Scene.InvalidCanvas"));
        Require(canvas.Grid.FlatCellSize is null || canvas.Grid.FlatCellSize is >= CanvasGridGeometry.MinimumCellSize and <= CanvasGridGeometry.MaximumCellSize &&
            double.IsFinite(canvas.Grid.FlatCellSize.Value), L.Get("Scene.InvalidCanvas"));
        Require(canvas.Grid.FlatOrigin is null || Point(canvas.Grid.FlatOrigin.Value), L.Get("Scene.InvalidCanvas"));
        Require(projection.PlaneBounds is null || Rect(projection.PlaneBounds.Value) &&
            CanvasProjectionGeometry.IsValidSize(projection.PlaneBounds.Value.Width, projection.PlaneBounds.Value.Height), L.Get("Scene.InvalidCanvas"));
        Require(canvas.Grid.PlaneCellSize is null || double.IsFinite(canvas.Grid.PlaneCellSize.Value) &&
            canvas.Grid.PlaneCellSize is >= CanvasGridGeometry.MinimumCellSize and <= CanvasGridGeometry.MaximumCellSize, L.Get("Scene.InvalidCanvas"));
        Require(canvas.Grid.PlaneOrigin is null || Point(canvas.Grid.PlaneOrigin.Value), L.Get("Scene.InvalidCanvas"));
        if (projection.Quad.Count == 0)
        {
            Require(!projection.Enabled && projection.Matrix == Projective2D.Identity, L.Get("Scene.InvalidCanvas"));
            return;
        }
        Require(CanvasCalibration.TryGridToScreen(projection.Columns, projection.Rows, projection.Quad, out _, allowMirrored: allowMirrored), L.Get("Scene.InvalidCanvas"));
        var plane = CanvasProjectionGeometry.ResolvePlaneBounds(projection);
        PointD[] corners = [new(plane.Left, plane.Top), new(plane.Right, plane.Top), new(plane.Right, plane.Bottom), new(plane.Left, plane.Bottom)];
        for (int i = 0; i < corners.Length; i++)
            Require(projection.Matrix.Denominator(corners[i]) > 0 && projection.Matrix.TryTransformPoint(corners[i], out var actual) &&
                actual.DistanceTo(projection.Quad[i]) <= 1e-5, L.Get("Scene.InvalidCanvas"));
        // The screen may contain the horizon; drawing and editing clip against the finite ground domain.
        Require(CanvasCalibration.IsValidProjection(projection.Matrix), L.Get("Scene.InvalidCanvas"));
    }
    private static SceneGraph ValidateGroups(SceneNodeGraph document, HashSet<Guid> ids)
    {
        var groups = new Dictionary<Guid, CanvasGroup>();
        foreach (var group in document.Groups)
        {
            Require(group is not null && group.Id != Guid.Empty && ids.Add(group.Id) && group.Name is not null && group.Name.Length <= 1024,
                L.Get("Scene.InvalidElementId"));
            groups.Add(group.Id, group);
            if (group.PreviewCanvas is { } preview) ValidatePreview(preview);
        }
        return new SceneGraph(document);
    }
    private static bool LayerPlacementFinite(Affine2D value) => Finite(value.M11, value.M12, value.M21, value.M22, value.OffsetX, value.OffsetY);
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private static bool Point(PointD p) => Finite(p.X, p.Y);
    private static bool Rect(RectD r) => Finite(r.X, r.Y, r.Width, r.Height) && r.Width >= 0 && r.Height >= 0 && Finite(r.Right, r.Bottom);
    private static bool Unit(double value) => double.IsFinite(value) && value >= 0 && value <= 1;
    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool Nonnegative(double value) => double.IsFinite(value) && value >= 0;
    private static bool NormalizedRect(RectD r, bool positive) => Rect(r) && r.X >= 0 && r.Y >= 0 && r.Right <= 1.000000001 && r.Bottom <= 1.000000001 && (!positive || r.Width > 0 && r.Height > 0);
    private static void Require([DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
