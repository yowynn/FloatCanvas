using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed record WindowRenderInfo(int DynamicFirst, int DynamicLast, IReadOnlyList<RectD> DirtyBounds,
    IReadOnlyList<WindowSourceKey> ChangedSources, long PixelBytes, int SourceCount);
public sealed record FrozenWindowBody(byte[] PngBytes, double LogicalWidth, double LogicalHeight, int PixelWidth, int PixelHeight);

public sealed partial class SceneRenderer
{
    /// <summary>Borrowed immutable frame bundle fixed for this draw. It never starts a capture service.</summary>
    public WindowFrameSet? WindowFrames { get; set; }
    /// <summary>Explicit editor-owner lifetime; equal ResourceIds alone cannot adopt a frame bundle.</summary>
    public object? WindowOwnerContext { get; set; }
    private WindowRenderPlan? windowPlan;
    private WindowRenderState? windowState;
    public long WindowPlanBuildCount { get; private set; }
    public long WindowFrameBuildCount { get; private set; }

    public WindowRenderInfo PrepareWindowFrames(SceneDocument document)
    {
        document = _frameLiveEffectContext ?? WorldDocument(LiveEffectContext ?? document);
        bool same = windowPlan is { } previous && (ReferenceEquals(previous.Document, document) ||
            document.IsRuntimeEvaluated && SceneEvaluation.RuntimeView(document) is { } view &&
            ReferenceEquals(view.Leaves, SceneEvaluation.RuntimeView(previous.Document)?.Leaves) &&
            ReferenceEquals(document.Canvas, previous.Document.Canvas) && document.Viewport == previous.Document.Viewport && document.LayerOpacity == previous.Document.LayerOpacity);
        if (!document.IsRuntimeEvaluated || !same)
        {
            var regions = new List<WindowRegion>(); var mapping = CanvasMapping.Create(document);
            int first = -1, last = -1;
            if (document.LayerOpacity > 0)
                for (int index = 0; index < document.Elements.Count; index++)
                {
                    var element = document.Elements[index];
                    var elementMapping = RootOf(document, element) == RootCanvasKind.Main ? mapping : ElementMapping(document, element);
                    if (element.Kind != ElementKind.Image || element.WindowSource is null || !element.Visible || element.Style.Opacity <= 0 ||
                        element.IsBillboard && !elementMapping.IsElementSafe(element)) continue;
                    var transform = elementMapping.ElementToScreen(element);
                    var bounds = transform.TryTransformBounds(ElementGeometry.StrokedLocalBounds(element), out var projected) ? projected : document.Viewport;
                    bounds = bounds.Intersect(document.Viewport); if (bounds.IsEmpty) continue;
                    var address = SceneEvaluation.RuntimeView(document)?.SourceAddress(element.Id);
                    // Private screen drafts are not authored capture sources.
                    if (document.IsRuntimeEvaluated && SceneEvaluation.RuntimeView(document) is not null && address is null) continue;
                    regions.Add(new(element.Id, new(document.ResourceId, address ?? new(null, element.Id)), element.WindowSource.BindingIntent, bounds));
                    if (first < 0) first = index; last = index;
                }
            windowPlan = new(document, regions, first, last); windowState = null; WindowPlanBuildCount++;
        }
        if (windowState is { } cached && ReferenceEquals(cached.Frames, WindowFrames) && ReferenceEquals(cached.OwnerContext, WindowOwnerContext)) return cached.Info;
        bool accepted = WindowFrames is not null && WindowOwnerContext is not null && ReferenceEquals(WindowFrames.OwnerContext, WindowOwnerContext);
        var versions = new Dictionary<WindowSourceKey, WindowFrameId?>();
        foreach (var region in windowPlan!.Regions)
            versions[region.Source] = accepted && WindowFrames!.TryGet(region.Source, out var frame) && frame.BindingIntent == region.BindingIntent ? frame.Id : null;
        var changed = versions.Keys.Where(key => windowState is null || !windowState.Versions.TryGetValue(key, out var previousId) || previousId != versions[key]).ToArray();
        var changedSet = changed.ToHashSet();
        var info = new WindowRenderInfo(windowPlan.First, windowPlan.Last,
            Array.AsReadOnly(windowPlan.Regions.Where(region => changedSet.Contains(region.Source)).Select(region => region.Bounds).ToArray()),
            Array.AsReadOnly(changed), accepted ? WindowFrames!.PixelBytes : 0, accepted ? WindowFrames!.Count : 0);
        windowState = new(WindowFrames, WindowOwnerContext, versions, info); WindowFrameBuildCount++; return info;
    }

    private bool TryWindowImage(CanvasElement element, out WindowFrameLease frame)
    {
        frame = null!;
        if (element.WindowSource is not { } source || WindowFrames is null || WindowOwnerContext is null ||
            !ReferenceEquals(WindowFrames.OwnerContext, WindowOwnerContext)) return false;
        var document = EffectiveLiveEffectContext ?? _drawingDocument;
        if (document is null) return false;
        var address = SceneEvaluation.RuntimeView(document)?.SourceAddress(element.Id);
        if (document.IsRuntimeEvaluated && SceneEvaluation.RuntimeView(document) is not null && address is null) return false;
        return WindowFrames.TryGet(new(document.ResourceId, address ?? new(null, element.Id)), out frame) && frame.BindingIntent == source.BindingIntent;
    }

    private bool ResolveImageFrame(CanvasElement element, AssetLibrary assets, out SKImage image, out RasterFrameKey key, out WindowImageLayout layout)
    {
        image = null!; key = default; layout = default;
        int contentWidth = 0, contentHeight = 0;
        if (TryWindowImage(element, out var live))
        {
            image = live.Image; key = RasterFrameKey.Window(live.Source, live.Id);
            contentWidth = live.ContentWidth; contentHeight = live.ContentHeight;
        }
        else if (element.AssetId is { } assetId)
        { image = GetImage(assetId, assets); key = RasterFrameKey.Static(assetId, ImageFrameIndex(assetId)); }
        else return false;
        return WindowImageLayout.TryCreate(image.Width, image.Height, element.Width, element.Height, element.ImageCrop,
            element.WindowSource?.Fit == WindowSourceFit.Contain, out layout, contentWidth, contentHeight);
    }

    /// <summary>Explicitly materializes effect→crop→fit body pixels. Placement, opacity and border remain editable outside this PNG.</summary>
    public FrozenWindowBody PrepareFrozenWindowBody(SceneDocument document, CanvasElement element, AssetLibrary assets,
        int maximumWidth = WindowFrameBudget.MaximumWidth, int maximumHeight = WindowFrameBudget.MaximumHeight)
    {
        if (maximumWidth is < 1 or > WindowFrameBudget.MaximumWidth || maximumHeight is < 1 or > WindowFrameBudget.MaximumHeight)
            throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        (document, element) = WorldElement(document, element);
        if (element.Kind != ElementKind.Image || element.WindowSource is null || !double.IsFinite(element.Width) ||
            !double.IsFinite(element.Height) || element.Width <= 0 || element.Height <= 0) throw new ArgumentException("A valid window image body is required.", nameof(element));
        var body = element.Clone(); body.ImageBorderWidth = 0; body.Style.Opacity = 1;
        BeginFrame(document);
        try
        {
            // Preserve the source's natural sampling density, including transparent
            // contain padding. A second arbitrary 1:1 logical raster would enlarge
            // soft alpha support and then enlarge its editable border on reopening.
            double naturalWidth = element.Width, naturalHeight = element.Height;
            if (ResolveImageFrame(body, assets, out _, out _, out var layout))
            {
                naturalWidth = element.Width * layout.SourcePixels.Width / layout.Destination.Width;
                naturalHeight = element.Height * layout.SourcePixels.Height / layout.Destination.Height;
            }
            double scale = Math.Min(1, Math.Min(maximumWidth / naturalWidth, maximumHeight / naturalHeight));
            int width = Math.Clamp((int)Math.Ceiling(naturalWidth * scale), 1, maximumWidth);
            int height = Math.Clamp((int)Math.Ceiling(naturalHeight * scale), 1, maximumHeight);
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("Cannot allocate a frozen window body.");
            surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Scale((float)(width / element.Width), (float)(height / element.Height));
            DrawImage(surface.Canvas, body, assets);
            using var image = surface.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("Cannot encode a frozen window body.");
            return new(png.ToArray(), element.Width, element.Height, width, height);
        }
        finally { EndFrame(); }
    }

    private sealed record WindowRegion(Guid ElementId, WindowSourceKey Source, Guid BindingIntent, RectD Bounds);
    private sealed record WindowRenderPlan(SceneDocument Document, IReadOnlyList<WindowRegion> Regions, int First, int Last);
    private sealed record WindowRenderState(WindowFrameSet? Frames, object? OwnerContext,
        IReadOnlyDictionary<WindowSourceKey, WindowFrameId?> Versions, WindowRenderInfo Info);
}

internal readonly record struct RasterFrameKey(string? AssetId, int AnimationFrame, WindowSourceKey? Source, WindowFrameId WindowFrame)
{
    internal static RasterFrameKey Static(string assetId, int frame) => new(assetId, frame, null, default);
    internal static RasterFrameKey Window(WindowSourceKey source, WindowFrameId frame) => new(null, -1, source, frame);
    internal bool IsWindow => Source is not null;
}
