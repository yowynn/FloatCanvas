using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Core;
using System.Diagnostics.CodeAnalysis;

namespace FloatCanvas.Storage;

public static class SceneValidation
{
    public static void Validate(SceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version != 1) throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", document.Version));
        Require(Enum.IsDefined(document.Kind), L.Get("Scene.InvalidDocumentKind"));
        Require(document.Elements is not null && document.DisplayLayout is not null, L.Get("Scene.MissingCollections"));
        Require(document.Elements!.Count <= 100_000, L.Get("Scene.ElementCountExceeded"));
        Require(Unit(document.LayerOpacity), L.Get("Scene.InvalidLayerOpacity"));
        Require(document.NextNumber >= 1, L.Get("Scene.InvalidNumberCounter"));
        Require(Rect(document.Viewport) && document.Viewport.Width > 0 && document.Viewport.Height > 0, L.Get("Scene.InvalidViewport"));
        var ids = new HashSet<Guid>();
        foreach (var element in document.Elements) ValidateElement(element, ids);
        if (document.Background is not null)
        {
            Require(document.Background.Kind == ElementKind.Image, L.Get("Scene.BackgroundMustBeImage"));
            ValidateElement(document.Background, ids);
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
        Require(Nonnegative(element.Width) && Nonnegative(element.Height), L.Get("Scene.InvalidElementSize"));
        var t = element.Transform;
        Require(Finite(t.M11, t.M12, t.M21, t.M22, t.OffsetX, t.OffsetY) && t.TryInverse(out _), L.Get("Scene.InvalidElementTransform"));
        Require(element.Points is not null && element.Points.Count <= 2_000_000 && element.Points.All(Point), L.Get("Scene.InvalidElementPath"));
        Require(element.Style is not null && element.Text is not null && element.Text.Length <= 2_000_000, L.Get("Scene.InvalidElementContent"));
        var s = element.Style!;
        Require(Nonnegative(s.StrokeWidth) && Unit(s.Opacity) && Positive(s.FontSize) && s.FontFamily is not null && s.FontFamily.Length <= 1024 && Enum.IsDefined(s.TextAlignment), L.Get("Scene.InvalidElementStyle"));
        Require(Enum.IsDefined(s.ArrowHeadStyle) && Nonnegative(s.ArrowHeadSize) && s.ArrowHeadSize <= 512, L.Get("Scene.InvalidArrowHead"));
        Require(element.Number >= 1, L.Get("Scene.InvalidElementNumber"));
        Require(element.Kind != ElementKind.Image || PackageCodec.IsAssetId(element.AssetId), L.Get("Scene.InvalidImageReference"));
        Require(element.AssetId is null || PackageCodec.IsAssetId(element.AssetId), L.Get("Scene.InvalidAssetReference"));
        Require(element.ImageCrop is null || NormalizedRect(element.ImageCrop.Value, positive: true), L.Get("Scene.InvalidImageCrop"));
        Require(element.Effects is not null && element.Effects.Count <= 10_000, L.Get("Scene.InvalidEffects"));
        Require(element.Effects!.Count == 0 || element.Kind == ElementKind.Image, L.Get("Scene.EffectsRequireImage"));
        var effectIds = new HashSet<Guid>();
        foreach (var effect in element.Effects)
            Require(effect is not null && effect.Id != Guid.Empty && effectIds.Add(effect.Id) && Enum.IsDefined(effect.Kind) && NormalizedRect(effect.Bounds, positive: false) && Positive(effect.Amount), L.Get("Scene.InvalidImageEffect"));
        Require(Rect(ElementGeometry.Bounds(element)), L.Get("Scene.InvalidRenderedBounds"));
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
