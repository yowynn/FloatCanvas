using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

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
    public long PathBuildCount { get; private set; }
    public long CachedImageBytes => _images.Values.Sum(e => Bytes(e.Image)) + _effects.Values.Sum(e => Bytes(e.Image));

    /// <summary>Preserves the caller's matrix and clip. Viewport is in document coordinates.</summary>
    public void Draw(SKCanvas canvas, SceneDocument document, AssetLibrary assets, bool includeBackground = true)
    {
        BeginFrame();
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        int saved = canvas.Save();
        var previousDocument = _drawingDocument;
        _drawingDocument = document;
        try
        {
            canvas.ClipRect(ToRect(document.Viewport));
            if (includeBackground) DrawBackground(canvas, document, assets);
            double opacity = Math.Clamp(document.LayerOpacity, 0, 1);
            if (opacity > 0)
            {
                // Apply layer opacity once after compositing, so overlapping strokes do not become darker.
                if (opacity < 1)
                {
                    using var opacityPaint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) };
                    canvas.SaveLayer(ToRect(document.Viewport), opacityPaint);
                }
                DrawElements(canvas, document.Elements, assets, 0, document.Elements.Count);
                if (opacity < 1) canvas.Restore();
            }
        }
        finally { _drawingDocument = previousDocument; canvas.RestoreToCount(saved); EndFrame(); }
    }

    internal void BeginFrame() { ObjectDisposedException.ThrowIf(_disposed, this); ClearLiveEffectImages(); _clock++; }
    internal void EndFrame() { TrimCaches(); ClearLiveEffectImages(); }
    internal void DrawBackground(SKCanvas canvas, SceneDocument document, AssetLibrary assets)
    {
        if (document.Background is { Visible: true } background) DrawElement(canvas, background, assets);
    }
    /// <summary>Draws raw annotation alpha in original z order; the caller owns viewport and global opacity.</summary>
    internal void DrawElements(SKCanvas canvas, IReadOnlyList<CanvasElement> elements, AssetLibrary assets, int first, int endExclusive)
    {
        for (int i = first; i < endExclusive; i++)
            if (elements[i].Visible)
            {
                if (elements[i].Kind == ElementKind.LivePixelate) DrawLivePixelate(canvas, elements, i, assets);
                else DrawElement(canvas, elements[i], assets);
            }
    }

    public byte[] ExportPng(SceneDocument document, AssetLibrary assets, bool transparent = false) =>
        Export(document, assets, SKEncodedImageFormat.Png, 100, !transparent);

    public byte[] ExportJpeg(SceneDocument document, AssetLibrary assets, int quality = 92) =>
        Export(document, assets, SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100), true);

    public TextMetrics MeasureText(string text, ElementStyle style, double maxWidth = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (EmojiGlyphs.IsEmoji(text, style)) return EmojiGlyphs.Measure(text, style.FontSize);
        return _text.Measure(text, style, maxWidth);
    }

    private byte[] Export(SceneDocument document, AssetLibrary assets, SKEncodedImageFormat format, int quality, bool background)
    {
        var viewport = document.Viewport.Normalize();
        int width = checked((int)Math.Ceiling(viewport.Width)), height = checked((int)Math.Ceiling(viewport.Height));
        if (width < 1 || height < 1) throw new ArgumentException(L.Get("Export.EmptyRegion"), nameof(document));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException(L.Get("Export.BufferTooLarge"));
        surface.Canvas.Clear(format == SKEncodedImageFormat.Jpeg ? SKColors.White : SKColors.Transparent);
        surface.Canvas.Translate(-(float)viewport.X, -(float)viewport.Y);
        Draw(surface.Canvas, document, assets, background);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, quality) ?? throw new InvalidOperationException(L.Get("Images.EncodingFailed"));
        return data.ToArray();
    }

    private void DrawElement(SKCanvas canvas, CanvasElement element, AssetLibrary assets)
    {
        if (element.Style.Opacity <= 0) return;
        int saved = canvas.Save();
        try
        {
            var matrix = ToMatrix(element.Transform);
            canvas.Concat(in matrix);
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
        SetPaint((element.Style.Fill >> 24) == 0 ? element.Style.Stroke : element.Style.Fill, SKPaintStyle.Fill);
        canvas.DrawOval(new SKRect(0, 0, width, height), _paint);
        string label = element.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var style = element.Style.Clone();
        style.FontSize = Math.Min(style.FontSize, Math.Min(width, height) * .58);
        style.TextAlignment = TextAlignment.Center;
        var metrics = _text.Measure(label, style, width);
        while (metrics.Width > width * .8 && style.FontSize > 4)
        {
            style.FontSize *= .85;
            metrics = _text.Measure(label, style, width);
        }
        canvas.Translate(0, (height - (float)metrics.Height) / 2);
        SetPaint(0xFFFFFFFF, SKPaintStyle.Fill);
        _text.Draw(canvas, label, style, width, _paint);
    }

    private void DrawImage(SKCanvas canvas, CanvasElement element, AssetLibrary assets)
    {
        if (element.AssetId is null || element.Width <= 0 || element.Height <= 0) return;
        var original = GetImage(element.AssetId, assets);
        var image = element.Effects.Count == 0 ? original : GetEffectImage(element, original);
        RectD crop = (element.ImageCrop ?? new RectD(0, 0, 1, 1)).Normalize().Intersect(new(0, 0, 1, 1));
        if (crop.IsEmpty) return;
        var source = new SKRect((float)(crop.Left * image.Width), (float)(crop.Top * image.Height), (float)(crop.Right * image.Width), (float)(crop.Bottom * image.Height));
        SetPaint(0xFFFFFFFF, SKPaintStyle.Fill);
        canvas.DrawImage(image, source, new(0, 0, (float)element.Width, (float)element.Height), Linear, _paint);
    }

    private SKImage GetImage(string assetId, AssetLibrary assets)
    {
        if (_images.TryGetValue(assetId, out var cached)) { cached.Used = _clock; return cached.Image; }
        var decoded = AssetImport.Decode(assets.Get(assetId).Data.Span);
        _images.Add(assetId, new(decoded, _clock));
        ImageDecodeCount++;
        return decoded;
    }

    private SKImage GetEffectImage(CanvasElement element, SKImage original)
    {
        var snapshots = element.Effects.Select(EffectSnapshot.From).ToArray();
        if (_effects.TryGetValue(element.Id, out var cached) && cached.AssetId == element.AssetId && cached.Effects.SequenceEqual(snapshots))
        { cached.Used = _clock; return cached.Image; }
        if (cached is not null) { _effects.Remove(element.Id); cached.Dispose(); }
        using var surface = SKSurface.Create(new SKImageInfo(original.Width, original.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException(L.Get("Images.EffectBufferFailed"));
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
                    using var small = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))!;
                    small.Canvas.Clear(SKColors.Transparent);
                    small.Canvas.DrawImage(before, region, new SKRect(0, 0, width, height), Linear);
                    using var pixels = small.Snapshot();
                    canvas.DrawImage(pixels, new SKRect(0, 0, width, height), region, Nearest, paint);
                }
            }
            finally { canvas.RestoreToCount(saved); }
        }
        var entry = new EffectEntry(element.AssetId!, snapshots, surface.Snapshot(), _clock);
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
    private static SKMatrix ToMatrix(Affine2D t) => new((float)t.M11, (float)t.M12, (float)t.OffsetX, (float)t.M21, (float)t.M22, (float)t.OffsetY, 0, 0, 1);

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
    private class ImageEntry(SKImage image, long used) : IDisposable
    {
        public SKImage Image { get; } = image;
        public long Used { get; set; } = used;
        public void Dispose() => Image.Dispose();
    }
    private sealed class EffectEntry(string assetId, EffectSnapshot[] effects, SKImage image, long used) : ImageEntry(image, used)
    {
        public string AssetId { get; } = assetId;
        public EffectSnapshot[] Effects { get; } = effects;
    }
    private readonly record struct EffectSnapshot(ImageEffectKind Kind, RectD Bounds, double Amount, uint Color)
    {
        public static EffectSnapshot From(ImageEffect effect) => new(effect.Kind, effect.Bounds, effect.Amount, effect.Color);
    }
}
