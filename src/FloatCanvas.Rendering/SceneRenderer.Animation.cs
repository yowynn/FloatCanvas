using FloatCanvas.Core;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    private readonly Dictionary<string, AnimationTimeline?> _animationMetadata = new(StringComparer.Ordinal);
    public long AnimationMetadataReadCount { get; private set; }
    /// <summary>Owned immutable timing payload; excludes decoded image and playback buffers.</summary>
    public long CachedAnimationMetadataBytes => _animationMetadata.Values.Sum(timeline => timeline?.CachedBytes ?? 0);

    /// <summary>Metadata-only query. Desktop UI scheduling consumes CaptureAnimationSchedule results instead.</summary>
    public bool HasAnimatedImages(SceneDocument document, AssetLibrary assets)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return VisibleAnimations(document, assets).Any();
    }

    public IReadOnlyList<RectD> AnimatedImageBounds(SceneDocument document, AssetLibrary assets)
        => CaptureAnimationSchedule(document, assets, AnimationTime).DirtyBounds;

    /// <summary>Whole-scene timing lookup without pixel decoding. Called on the renderer's owning thread.</summary>
    public TimeSpan? GetNextAnimationDelay(SceneDocument document, AssetLibrary assets, TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TimeSpan? next = null;
        foreach (var (_, timeline) in VisibleAnimations(document, assets))
            if (timeline.Locate(time).Delay is { } delay && (next is null || delay < next)) next = delay;
        return next;
    }

    /// <summary>Capture on the preview worker; the returned value retains no live scene, assets or codec.</summary>
    public SceneAnimationSchedule CaptureAnimationSchedule(SceneDocument document, AssetLibrary assets, TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (string id in _animationMetadata.Keys.Where(id => !assets.TryGet(id, out _)).ToArray())
            _animationMetadata.Remove(id);
        var mapping = CanvasMapping.Create(document);
        var bounds = new List<RectD>();
        TimeSpan? next = null;
        bool hasAnimations = false;
        foreach (var (element, timeline) in VisibleAnimations(document, assets, mapping))
        {
            hasAnimations = true;
            if (timeline.Locate(time).Delay is { } delay && (next is null || delay < next)) next = delay;
            var projected = mapping.ProjectedBounds(element, includeStroke: false).Intersect(document.Viewport);
            if (!projected.IsEmpty) bounds.Add(projected);
        }
        // The animation can alter a filter whose sampling halo reaches another output.
        foreach (var effect in document.Elements.Where(element => element.Visible && element.Style.Opacity > 0 && ElementGeometry.IsLiveEffect(element.Kind)))
        {
            var sampling = LiveEffectSamplingBounds(document, effect);
            if (bounds.Any(bound => bound.Intersects(sampling))) bounds.Add(sampling);
        }
        return new(time, hasAnimations, next, bounds);
    }

    private IEnumerable<(CanvasElement Element, AnimationTimeline Timeline)> VisibleAnimations(
        SceneDocument document, AssetLibrary assets, CanvasMapping? mapping = null)
    {
        mapping ??= CanvasMapping.Create(document);
        IEnumerable<CanvasElement> elements = document.LayerOpacity > 0 ? document.Elements : [];
        if (document.Background is { } background) elements = elements.Prepend(background);
        foreach (var element in elements)
            if (VisibleAnimation(element, document, assets, mapping) is { } timeline)
                yield return (element, timeline);
    }

    internal (int First, int Last) AnimatedElementRange(SceneDocument document, AssetLibrary assets)
    {
        int first = -1, last = -1;
        var mapping = CanvasMapping.Create(document);
        if (document.LayerOpacity > 0)
            for (int i = 0; i < document.Elements.Count; i++)
                if (VisibleAnimation(document.Elements[i], document, assets, mapping) is not null)
                { if (first < 0) first = i; last = i; }
        return (first, last);
    }

    private AnimationTimeline? VisibleAnimation(CanvasElement element, SceneDocument document, AssetLibrary assets, CanvasMapping mapping)
    {
        if (element.Kind != ElementKind.Image || !element.Visible || !(element.Style.Opacity > 0) || element.AssetId is not { } id) return null;
        if (element.IsBillboard && !mapping.IsElementSafe(element)) return null;
        if (!mapping.ProjectedBounds(element, includeStroke: false).Intersects(document.Viewport)) return null;
        if (_animationMetadata.TryGetValue(id, out var cached)) return cached;
        var bytes = assets.Get(id).Data.Span;
        // Header/timeline parsing is separated from GetImageEntry: static WebP must never
        // allocate decoded pixels merely to answer whether another frame is needed.
        var timeline = AnimationTimeline.Read(bytes);
        _animationMetadata.Add(id, timeline);
        AnimationMetadataReadCount++;
        return timeline;
    }
}
