using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public enum SceneRenderSpace { ScreenComposite, PlaneContent }

/// <summary>Single-threaded renderer shared by the desktop overlay and offscreen exports.</summary>
public sealed partial class SceneRenderer : IDisposable
{
    private readonly Dictionary<Guid, PathEntry> _paths = [];
    private readonly Dictionary<string, ImageEntry> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, EffectEntry> _effects = [];
    private readonly TextRenderer _text = new();
    private readonly SKPaint _paint = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private long _clock;
    private bool _disposed;
    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear);
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest);
    public long CacheByteLimit { get; set; } = 256L * 1024 * 1024;
    public long ImageDecodeCount { get; private set; }
    public long AnimationFrameDecodeCount { get; private set; }
    /// <summary>Caller-owned visible playback time. A fixed value freezes all images for an export.</summary>
    public TimeSpan AnimationTime { get; set; }
    public TimeSpan? NextAnimationDelay { get; private set; }
    public long PathBuildCount { get; private set; }
    public long CachedImageBytes => _images.Values.Sum(e => e.CachedBytes) + _effects.Values.Sum(e => e.CachedBytes);

    /// <summary>Preserves the caller's matrix and clip. Viewport is in document coordinates.</summary>
    public void Draw(SKCanvas canvas, SceneDocument document, AssetLibrary assets, bool includeBackground = true,
        SceneRenderSpace space = SceneRenderSpace.ScreenComposite, bool includeCanvasBackground = true, bool calibrationReference = false, bool includeGrid = true)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        BeginFrame(document, space);
        int saved = canvas.Save();
        try
        {
            canvas.ClipRect(ToRect(document.Viewport));
            if (space == SceneRenderSpace.ScreenComposite && UsesDepthComposition(document))
            {
                DrawDepthScene(canvas, document, assets, includeBackground, includeCanvasBackground, includeGrid);
                if (includeGrid && calibrationReference) DrawGridCore(canvas, document, true);
                return;
            }
            DrawBackground(canvas, document, assets, includeBackground, includeCanvasBackground);
            double opacity = Math.Clamp(document.LayerOpacity, 0, 1);
            if (opacity > 0)
            {
                // Apply layer opacity once after compositing, so overlapping strokes do not become darker.
                if (opacity < 1)
                {
                    using var opacityPaint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) };
                    canvas.SaveLayer(ToRect(document.Viewport), opacityPaint);
                }
                if (includeGrid && !calibrationReference) DrawGrid(canvas, document);
                DrawElements(canvas, document.Elements, assets, 0, document.Elements.Count);
                if (opacity < 1) canvas.Restore();
            }
            if (includeGrid && calibrationReference && space == SceneRenderSpace.ScreenComposite)
            {
                // Editing guides stay visible over photographs and independent of annotation opacity.
                DrawGridCore(canvas, document, true);
                if (opacity > 0)
                {
                    if (opacity < 1)
                    {
                        using var gridOpacity = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) };
                        canvas.SaveLayer(ToRect(document.Viewport), gridOpacity);
                    }
                    DrawGrid(canvas, document);
                    if (opacity < 1) canvas.Restore();
                }
            }
        }
        finally { canvas.RestoreToCount(saved); EndFrame(); }
    }

    internal void BeginFrame(SceneDocument document, SceneRenderSpace space = SceneRenderSpace.ScreenComposite)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ClearLiveEffectImages(); NextAnimationDelay = null; _clock++;
        if (space == SceneRenderSpace.PlaneContent && document.Elements.Any(e => e.Visible && ElementGeometry.IsLiveEffect(e.Kind)))
            throw new InvalidOperationException("Live desktop filters require screen composition before being placed back on the plane.");
        _drawingDocument = document; _renderSpace = space; _mapping = CanvasMapping.Create(document);
    }
    internal void EndFrame()
    {
        ClearGpuEffectImages(); TrimCaches(); ClearLiveEffectImages(); _drawingDocument = null; _mapping = null;
    }
    internal void DrawBackground(SKCanvas canvas, SceneDocument document, AssetLibrary assets,
        bool includeBackground = true, bool includeCanvasBackground = true, bool sampling = false)
    {
        if (_renderSpace == SceneRenderSpace.ScreenComposite)
        {
            if (includeBackground) (ScreenBackdrop ?? (sampling && document.Background is null ? LiveBackdrop : null))?.Draw(canvas);
            if (includeCanvasBackground && (document.Canvas.BackgroundColor >> 24) != 0)
                DrawPlaneColor(canvas, document, true);
        }
        if (includeBackground && document.Background is { Visible: true } background)
            DrawElement(canvas, background, assets, TransformFor(document), document);
    }
    /// <summary>Draws raw annotation alpha in original z order; the caller owns viewport and global opacity.</summary>
    internal void DrawElements(SKCanvas canvas, IReadOnlyList<CanvasElement> elements, AssetLibrary assets, int first, int endExclusive,
        SceneDocument? sourceDocument = null)
    {
        var document = sourceDocument ?? _drawingDocument;
        var transform = TransformFor(document);
        for (int i = first; i < endExclusive; i++)
            if (elements[i].Visible)
            {
                if (ElementGeometry.IsLiveEffect(elements[i].Kind)) DrawLivePixelate(canvas, elements, i, assets);
                else DrawElement(canvas, elements[i], assets, transform, document);
            }
    }

    public byte[] ExportPng(SceneDocument document, AssetLibrary assets, bool transparent = false,
        SceneRenderSpace space = SceneRenderSpace.ScreenComposite) =>
        Export(document, assets, SKEncodedImageFormat.Png, 100, !transparent, space);

    public byte[] ExportJpeg(SceneDocument document, AssetLibrary assets, int quality = 92,
        SceneRenderSpace space = SceneRenderSpace.ScreenComposite) =>
        Export(document, assets, SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100), true, space);

    public TextMetrics MeasureText(string text, ElementStyle style, double maxWidth = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (EmojiGlyphs.IsEmoji(text, style)) return EmojiGlyphs.Measure(text, style.FontSize);
        return _text.Measure(text, style, maxWidth);
    }

    public TextEditingGeometry TextEditingGeometry(string text,ElementStyle style,double width)
    {
        if(!EmojiGlyphs.IsEmoji(text,style))return _text.EditingGeometry(text,style,width);
        var metrics=EmojiGlyphs.Measure(text,style.FontSize);
        return new([new(0,metrics.Height,[new(0,0),new(text.Length,metrics.Width)])]);
    }

    private byte[] Export(SceneDocument document, AssetLibrary assets, SKEncodedImageFormat format, int quality, bool background, SceneRenderSpace space)
    {
        var viewport = document.Viewport.Normalize();
        int width = checked((int)Math.Ceiling(viewport.Width)), height = checked((int)Math.Ceiling(viewport.Height));
        if (width < 1 || height < 1) throw new ArgumentException(L.Get("Export.EmptyRegion"), nameof(document));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException(L.Get("Export.BufferTooLarge"));
        surface.Canvas.Clear(format == SKEncodedImageFormat.Jpeg ? SKColors.White : SKColors.Transparent);
        surface.Canvas.Translate(-(float)viewport.X, -(float)viewport.Y);
        Draw(surface.Canvas, document, assets, background, space, includeCanvasBackground: background);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, quality) ?? throw new InvalidOperationException(L.Get("Images.EncodingFailed"));
        return data.ToArray();
    }

    private void DrawElement(SKCanvas canvas, CanvasElement element, AssetLibrary assets, Projective2D planeToTarget, SceneDocument? document)
    {
        if (element.Style.Opacity <= 0) return;
        int saved = canvas.Save();
        try
        {
            var transform = BillboardGeometry.ElementToTarget(element, planeToTarget);
            if (_renderSpace == SceneRenderSpace.ScreenComposite && document is not null)
            {
                var mapping = MappingFor(document);
                if (element.IsBillboard)
                {
                    if (!mapping.IsElementSafe(element)) return;
                }
                else
                {
                    if (!ClipSafePlane(canvas, document)) return;
                    var localBounds = ElementGeometry.StrokedLocalBounds(element);
                    if (!transform.TryTransformBounds(localBounds, out _))
                    {
                        DrawClippedSource(canvas, element, assets, transform, mapping);
                        return;
                    }
                }
            }
            var matrix = ToMatrix(transform);
            canvas.Concat(in matrix);
            DrawLocalElement(canvas, element, assets);
        }
        finally { canvas.RestoreToCount(saved); }
    }

    private void DrawLocalElement(SKCanvas canvas, CanvasElement element, AssetLibrary assets)
    {
        int saved = canvas.Save();
        try
        {
            double opacity = Math.Clamp(element.Style.Opacity, 0, 1);
            // One element is a compositing unit, including arrow heads, fill+outline and number labels.
            if (opacity < 1)
            {
                using var alpha = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) };
                canvas.SaveLayer(alpha);
            }
            if (element.Kind == ElementKind.Image) DrawImage(canvas, element, assets);
            else if (element.Kind == ElementKind.Text)
            {
                if (element.TextFrameFixed) canvas.ClipRect(new(0, 0, (float)element.Width, (float)element.Height));
                SetPaint(element.Style.Stroke, SKPaintStyle.Fill);
                if (EmojiGlyphs.IsEmoji(element.Text, element.Style)) EmojiGlyphs.Draw(canvas, element.Text, element.Style.FontSize, _paint);
                else _text.Draw(canvas, element.Text, element.Style, element.Width, _paint);
            }
            else if (element.Kind == ElementKind.Number) DrawNumber(canvas, element);
            else
            {
                var geometry = GetPath(element);
                bool shape = element.Kind is ElementKind.Rectangle or ElementKind.RoundedRectangle or ElementKind.Ellipse or ElementKind.Triangle or ElementKind.Symbol;
                if (shape && (element.Style.Fill >> 24) != 0)
                {
                    SetPaint(element.Style.Fill, SKPaintStyle.Fill);
                    canvas.DrawPath(geometry.Path, _paint);
                }
                if (element.Style.StrokeWidth > 0)
                {
                    SetPaint(element.Style.Stroke, SKPaintStyle.Stroke, element.Style.StrokeWidth);
                    using var dash = CreateDash(element.Style);
                    _paint.PathEffect = dash;
                    if (element.Kind == ElementKind.Highlighter) _paint.Color = _paint.Color.WithAlpha((byte)Math.Round(_paint.Color.Alpha * .35));
                    if (element.Points.Count == 1 && element.Kind is ElementKind.Freehand or ElementKind.Highlighter)
                        canvas.DrawPoint((float)element.Points[0].X, (float)element.Points[0].Y, _paint);
                    else canvas.DrawPath(geometry.StrokePath, _paint);
                    _paint.PathEffect = null;
                    if (ElementGeometry.IsLine(element.Kind))
                    {
                        _paint.Style = SKPaintStyle.Fill;
                        canvas.DrawPath(geometry.FilledHeads, _paint);
                        _paint.Style = SKPaintStyle.Stroke;
                        canvas.DrawPath(geometry.OpenHeads, _paint);
                    }
                }
            }
        }
        finally { canvas.RestoreToCount(saved); }
    }

    private void SetPaint(uint color, SKPaintStyle style, double width = 1)
    {
        _paint.Color = new SKColor(color);
        _paint.Style = style;
        _paint.StrokeWidth = (float)width;
        _paint.PathEffect = null;
    }

    private static SKPathEffect? CreateDash(ElementStyle style)
    {
        float width = (float)Math.Max(.1, style.StrokeWidth);
        return style.StrokePattern switch
        {
            StrokePattern.Dashed => SKPathEffect.CreateDash([width * 4, width * 3], 0),
            StrokePattern.Dotted => SKPathEffect.CreateDash([width * .001f, width * 2.5f], 0),
            _ => null
        };
    }

    private PathEntry GetPath(CanvasElement element)
    {
        if (_paths.TryGetValue(element.Id, out var old) && old.Matches(element)) { old.Used = _clock; return old; }
        if (old is not null) { _paths.Remove(element.Id); old.Dispose(); }
        using var builder = new SKPathBuilder();
        using var filledHeads = new SKPathBuilder();
        using var openHeads = new SKPathBuilder();
        float width = (float)element.Width, height = (float)element.Height;
        switch (element.Kind)
        {
            case ElementKind.Rectangle:
            case ElementKind.RoundedRectangle:
                float radius = (float)ElementGeometry.CornerRadius(element);
                builder.AddRoundRect(new(0, 0, width, height), radius, radius); break;
            case ElementKind.Ellipse: builder.AddOval(new(0, 0, width, height)); break;
            case ElementKind.Triangle:
                builder.MoveTo(width / 2, 0); builder.LineTo(width, height); builder.LineTo(0, height); builder.Close(); break;
            case ElementKind.Symbol:
                AddHead(builder, ElementGeometry.Outline(element), true); break;
            default:
                var points = ElementGeometry.IsLine(element.Kind) ? ElementGeometry.LineShaftPoints(element) : element.Points;
                if (points.Count > 0)
                {
                    builder.MoveTo(ToPoint(points[0]));
                    foreach (var point in points.Skip(1)) builder.LineTo(ToPoint(point));
                }
                if (ElementGeometry.IsLine(element.Kind) && element.Points.Count >= 2)
                    foreach (var head in ElementGeometry.GetStyledArrowHeads(element))
                        AddHead(head.Style == ArrowHeadStyle.Open ? openHeads : filledHeads, head.Points, head.Style != ArrowHeadStyle.Open);
                break;
        }
        SKPath? patternedStroke = null;
        if (element.Style.StrokePattern != StrokePattern.Solid && element.Kind is ElementKind.Rectangle or ElementKind.RoundedRectangle or ElementKind.Ellipse)
        {
            // Native curved paths have different contour origins and curve lengths.
            // Patterned strokes share hit geometry; fills retain the exact native curve.
            using var strokeBuilder = new SKPathBuilder();
            AddHead(strokeBuilder, ElementGeometry.Outline(element), true);
            patternedStroke = strokeBuilder.Detach();
        }
        var entry = new PathEntry(element, builder.Detach(), patternedStroke, filledHeads.Detach(), openHeads.Detach(), _clock);
        _paths[element.Id] = entry;
        PathBuildCount++;
        return entry;
    }

    private static void AddHead(SKPathBuilder builder, IReadOnlyList<PointD> points, bool closed)
    {
        if (points.Count < 2) return;
        builder.MoveTo(ToPoint(points[0]));
        foreach (var point in points.Skip(1)) builder.LineTo(ToPoint(point));
        if (closed) builder.Close();
    }

    private void DrawNumber(SKCanvas canvas, CanvasElement element)
    {
        float width = (float)element.Width, height = (float)element.Height;
        if (width <= 0 || height <= 0) return;
        SetPaint((element.Style.Fill >> 24) == 0 ? element.Style.Stroke : element.Style.Fill, SKPaintStyle.Fill);
        canvas.DrawOval(new SKRect(0, 0, width, height), _paint);
        string label = element.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var style = element.Style.Clone();
        style.FontSize = Math.Clamp(Math.Min(style.FontSize, Math.Min(width, height) * .58), 1, 2048);
        style.TextAlignment = TextAlignment.Left;
        // Measure the entire label without wrapping before fitting it. Canvas scaling
        // also keeps very small badges within bounds below the font engine's minimum size.
        var metrics = _text.Measure(label, style, 0);
        float scale = (float)Math.Min(1, Math.Min(width * .8 / metrics.Width, height * .8 / metrics.Height));
        canvas.Translate((width - (float)metrics.Width * scale) / 2, (height - (float)metrics.Height * scale) / 2);
        canvas.Scale(scale, scale);
        SetPaint(0xFFFFFFFF, SKPaintStyle.Fill);
        _text.Draw(canvas, label, style, 0, _paint);
    }

    private void DrawImage(SKCanvas canvas, CanvasElement element, AssetLibrary assets)
    {
        if (element.AssetId is null || element.Width <= 0 || element.Height <= 0) return;
        var original = GetImage(element.AssetId, assets);
        var image = element.Effects.Count == 0 ? original : GetEffectImage(canvas, element, original, _images[element.AssetId].FrameIndex);
        RectD crop = (element.ImageCrop ?? new RectD(0, 0, 1, 1)).Normalize().Intersect(new(0, 0, 1, 1));
        if (crop.IsEmpty) return;
        var source = new SKRect((float)(crop.Left * image.Width), (float)(crop.Top * image.Height), (float)(crop.Right * image.Width), (float)(crop.Bottom * image.Height));
        SetPaint(0xFFFFFFFF, SKPaintStyle.Fill);
        canvas.DrawImage(image, source, new(0, 0, (float)element.Width, (float)element.Height), Linear, _paint);
    }

    private SKImage GetImage(string assetId, AssetLibrary assets)
    {
        var entry = GetImageEntry(assetId, assets); entry.Used = _clock;
        if (entry.Playback is { } playback)
        {
            if (playback.Select(AnimationTime)) AnimationFrameDecodeCount++;
            if (playback.NextDelay is { } delay && (NextAnimationDelay is null || delay < NextAnimationDelay)) NextAnimationDelay = delay;
        }
        return entry.Image;
    }

    private ImageEntry GetImageEntry(string id, AssetLibrary assets)
    {
        if (_images.TryGetValue(id, out var entry)) return entry;
        var bytes = assets.Get(id).Data.Span;
        var playback = AnimatedImage.TryCreate(bytes);
        entry = playback is null ? new(AssetImport.Decode(bytes), _clock) : new(playback, _clock);
        _images.Add(id, entry); ImageDecodeCount++; return entry;
    }

    /// <summary>Examines animation metadata, without decoding every frame or changing document state.</summary>
    public bool HasAnimatedImages(SceneDocument document, AssetLibrary assets)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return VisibleAnimations(document, assets).Any();
    }

    /// <summary>Whole-scene metadata scheduling, independent of monitor draw order and without decoding pixels.</summary>
    public TimeSpan? GetNextAnimationDelay(SceneDocument document, AssetLibrary assets, TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TimeSpan? next = null;
        foreach (var playback in VisibleAnimations(document, assets))
            if (playback.NextDelayAt(time) is { } delay && (next is null || delay < next)) next = delay;
        return next;
    }

    private IEnumerable<AnimatedImage> VisibleAnimations(SceneDocument document, AssetLibrary assets)
    {
        IEnumerable<CanvasElement> images = document.LayerOpacity > 0 ? document.Elements : [];
        if (document.Background is { } background) images = images.Prepend(background);
        foreach (var element in images)
            if (VisibleAnimation(element, document, assets) is { } playback)
                yield return playback;
    }

    internal (int First, int Last) AnimatedElementRange(SceneDocument document, AssetLibrary assets)
    {
        int first = -1, last = -1;
        if (document.LayerOpacity > 0)
            for (int i = 0; i < document.Elements.Count; i++)
                if (VisibleAnimation(document.Elements[i], document, assets) is not null)
                { if (first < 0) first = i; last = i; }
        return (first, last);
    }

    private AnimatedImage? VisibleAnimation(CanvasElement element, SceneDocument document, AssetLibrary assets)
    {
        if (element.Kind != ElementKind.Image || !element.Visible || !(element.Style.Opacity > 0) || element.AssetId is not { } id) return null;
        var mapping = CanvasMapping.Create(document);
        // Retained unsafe billboards are hidden. Their empty projected bounds can still
        // intersect an origin-containing viewport, so apply the drawing guard first.
        if (element.IsBillboard && !mapping.IsElementSafe(element)) return null;
        return mapping.ProjectedBounds(element, includeStroke: false).Intersects(document.Viewport) &&
            AnimatedImage.MayAnimate(assets.Get(id).Data.Span) ? GetImageEntry(id, assets).Playback : null;
    }

    private SKImage GetEffectImage(SKCanvas destination, CanvasElement element, SKImage original, int sourceFrame)
    {
        bool gpu = destination.Context is not null;
        var snapshots = element.Effects.Select(EffectSnapshot.From).ToArray();
        if (_effects.TryGetValue(element.Id, out var cached) && cached.IsGpu == gpu && cached.AssetId == element.AssetId && cached.SourceFrame == sourceFrame && cached.Effects.SequenceEqual(snapshots))
        { cached.Used = _clock; return cached.Image; }
        if (cached is not null) { _effects.Remove(element.Id); cached.Dispose(); }
        using var surface = CreateIntermediateSurface(destination,
            new SKImageInfo(original.Width, original.Height, SKColorType.Bgra8888, SKAlphaType.Premul), L.Get("Images.EffectBufferFailed"));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(original, 0, 0, Nearest);
        foreach (var effect in element.Effects)
        {
            var normalized = effect.Bounds.Normalize().Intersect(new(0, 0, 1, 1));
            if (normalized.IsEmpty) continue;
            var region = new SKRect((float)(normalized.Left * original.Width), (float)(normalized.Top * original.Height),
                (float)(normalized.Right * original.Width), (float)(normalized.Bottom * original.Height));
            int saved = canvas.Save();
            try
            {
                canvas.ClipRect(region, antialias: false);
                using var paint = new SKPaint { Color = SKColors.White, BlendMode = SKBlendMode.Src };
                if (effect.Kind == ImageEffectKind.Redact)
                {
                    paint.Color = new SKColor(effect.Color).WithAlpha(255);
                    canvas.DrawRect(region, paint);
                    continue;
                }
                using var before = surface.Snapshot();
                if (effect.Kind == ImageEffectKind.Blur)
                {
                    using var filter = SKImageFilter.CreateBlur((float)Math.Clamp(effect.Amount, .1, 256), (float)Math.Clamp(effect.Amount, .1, 256), SKShaderTileMode.Clamp);
                    paint.ImageFilter = filter;
                    canvas.DrawImage(before, 0, 0, Nearest, paint);
                }
                else if (effect.Kind == ImageEffectKind.Pixelate)
                {
                    double amount = Math.Clamp(effect.Amount, 1, 1024);
                    int width = Math.Max(1, (int)Math.Ceiling(region.Width / amount));
                    int height = Math.Max(1, (int)Math.Ceiling(region.Height / amount));
                    using var small = CreateIntermediateSurface(canvas,
                        new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), L.Get("Images.EffectBufferFailed"));
                    small.Canvas.Clear(SKColors.Transparent);
                    small.Canvas.DrawImage(before, region, new SKRect(0, 0, width, height), Linear);
                    using var pixels = small.Snapshot();
                    canvas.DrawImage(pixels, new SKRect(0, 0, width, height), region, Nearest, paint);
                }
            }
            finally { canvas.RestoreToCount(saved); }
        }
        var entry = new EffectEntry(element.AssetId!, sourceFrame, snapshots, surface.Snapshot(), _clock, gpu);
        _effects[element.Id] = entry;
        return entry.Image;
    }

    private void TrimCaches()
    {
        foreach (var id in _paths.Where(p => p.Value.Used < _clock - 2).Select(p => p.Key).ToArray())
        { _paths[id].Dispose(); _paths.Remove(id); }
        foreach (var id in _effects.Where(p => p.Value.Used < _clock - 2).Select(p => p.Key).ToArray())
        { _effects[id].Dispose(); _effects.Remove(id); }
        while (CachedImageBytes > Math.Max(0, CacheByteLimit) && _images.Count + _effects.Count > 0)
        {
            var image = _images.Count == 0 ? default : _images.MinBy(e => e.Value.Used);
            var effect = _effects.Count == 0 ? default : _effects.MinBy(e => e.Value.Used);
            if (image.Value is not null && (effect.Value is null || image.Value.Used <= effect.Value.Used))
            { image.Value.Dispose(); _images.Remove(image.Key); }
            else if (effect.Value is not null) { effect.Value.Dispose(); _effects.Remove(effect.Key); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearLiveEffectImages();
        DisposeDepthBuffers();
        foreach (var entry in _paths.Values) entry.Dispose();
        foreach (var entry in _images.Values) entry.Dispose();
        foreach (var entry in _effects.Values) entry.Dispose();
        _paths.Clear(); _images.Clear(); _effects.Clear();
        _paint.Dispose(); _text.Dispose();
    }

    private static long Bytes(SKImage image) => (long)image.Width * image.Height * 4;
    private static byte Alpha(double opacity) => (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
    private static SKPoint ToPoint(PointD p) => new((float)p.X, (float)p.Y);
    private static SKRect ToRect(RectD r) => new((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom);
    private static SKMatrix ToMatrix(Projective2D t) => new((float)t.M11, (float)t.M12, (float)t.M13,
        (float)t.M21, (float)t.M22, (float)t.M23, (float)t.M31, (float)t.M32, (float)t.M33);

    private sealed class PathEntry(CanvasElement element, SKPath path, SKPath? patternedStroke, SKPath filledHeads, SKPath openHeads, long used) : IDisposable
    {
        private readonly ElementKind _kind = element.Kind;
        private readonly double _width = element.Width, _height = element.Height, _strokeWidth = element.Style.StrokeWidth;
        private readonly ArrowHeadStyle _arrowHeadStyle = element.Style.ArrowHeadStyle;
        private readonly ArrowHeadStyle? _startArrowHead = element.Style.StartArrowHead, _endArrowHead = element.Style.EndArrowHead;
        private readonly double? _cornerRadius = element.CornerRadius;
        private readonly SymbolKind _symbol = element.Symbol;
        private readonly double _arrowHeadSize = element.Style.ArrowHeadSize;
        private readonly bool _patterned = element.Style.StrokePattern != StrokePattern.Solid;
        private readonly PointD[] _points = element.Points.ToArray();
        public SKPath Path { get; } = path;
        public SKPath StrokePath => patternedStroke ?? Path;
        public SKPath FilledHeads { get; } = filledHeads;
        public SKPath OpenHeads { get; } = openHeads;
        public long Used { get; set; } = used;
        public bool Matches(CanvasElement e) => _kind == e.Kind && _width == e.Width && _height == e.Height && _strokeWidth == e.Style.StrokeWidth &&
            _arrowHeadStyle == e.Style.ArrowHeadStyle && _arrowHeadSize == e.Style.ArrowHeadSize &&
            _startArrowHead == e.Style.StartArrowHead && _endArrowHead == e.Style.EndArrowHead && _cornerRadius == e.CornerRadius && _symbol == e.Symbol &&
            _patterned == (e.Style.StrokePattern != StrokePattern.Solid) &&
            _points.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(e.Points));
        public void Dispose() { Path.Dispose(); patternedStroke?.Dispose(); FilledHeads.Dispose(); OpenHeads.Dispose(); }
    }
    private class ImageEntry : IDisposable
    {
        private readonly SKImage? image;
        public AnimatedImage? Playback { get; }
        public SKImage Image => Playback?.Image ?? image!;
        public int FrameIndex => Playback?.FrameIndex ?? -1;
        public long CachedBytes => Playback?.CachedBytes ?? Bytes(image!);
        public long Used { get; set; }
        public ImageEntry(SKImage image, long used) { this.image = image; Used = used; }
        public ImageEntry(AnimatedImage playback, long used) { Playback = playback; Used = used; }
        public void Dispose() { image?.Dispose(); Playback?.Dispose(); }
    }
    private sealed class EffectEntry(string assetId, int sourceFrame, EffectSnapshot[] effects, SKImage image, long used, bool isGpu) : ImageEntry(image, used)
    {
        public bool IsGpu { get; } = isGpu;
        public string AssetId { get; } = assetId;
        public int SourceFrame { get; } = sourceFrame;
        public EffectSnapshot[] Effects { get; } = effects;
    }
    private readonly record struct EffectSnapshot(ImageEffectKind Kind, RectD Bounds, double Amount, uint Color)
    {
        public static EffectSnapshot From(ImageEffect effect) => new(effect.Kind, effect.Bounds, effect.Amount, effect.Color);
    }
}
