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
    private bool _referenceOpacityAtHorizon;
    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear);
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest);
    public long CacheByteLimit { get; set; } = 256L * 1024 * 1024;
    public long ImageDecodeCount { get; private set; }
    public long AnimationFrameDecodeCount { get; private set; }
    /// <summary>Caller-owned visible playback time. A fixed value freezes all images for an export.</summary>
    public TimeSpan AnimationTime { get; set; }
    public TimeSpan? NextAnimationDelay { get; private set; }
    public long PathBuildCount { get; private set; }
    internal bool OptimizeElementOpacity { get; set; } = true;
    /// <summary>Grid candidates and retained lines in the most recent frame, including calibration guides.</summary>
    public int GridCandidateCount { get; private set; }
    public int GridLineCount { get; private set; }
    /// <summary>Estimated plane-space dash periods after and before screen-resolution trimming.</summary>
    public double GridDashIntervalCount { get; private set; }
    public double UnresolvedGridDashIntervalCount { get; private set; }
    public long CachedImageBytes => _images.Values.Sum(e => e.CachedBytes) + _effects.Values.Sum(e => e.CachedBytes) + _imageBorders.Values.Sum(e => e.CachedBytes);

    /// <summary>Preserves the caller's matrix and clip. Viewport is in document coordinates.</summary>
    public void Draw(SKCanvas canvas, SceneDocument document, AssetLibrary assets, bool includeBackground = true,
        SceneRenderSpace space = SceneRenderSpace.ScreenComposite, bool includeCanvasBackground = true, bool calibrationReference = false, bool includeGrid = true)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        document = BeginFrame(document, space);
        int saved = canvas.Save();
        try
        {
            canvas.ClipRect(ToRect(document.Viewport));
            if (space == SceneRenderSpace.ScreenComposite && HasAuxiliaryRoots(document))
            {
                DrawRootScene(canvas, document, assets, includeBackground, includeCanvasBackground, includeGrid);
                if (includeGrid && calibrationReference) DrawGridCore(canvas, document, true);
                return;
            }
            if (space == SceneRenderSpace.ScreenComposite && UsesDepthComposition(document))
            {
                DrawDepthScene(canvas, document, assets, includeBackground, includeCanvasBackground, includeGrid);
                if (includeGrid && calibrationReference) DrawGridCore(canvas, document, true);
                return;
            }
            double opacity = Math.Clamp(document.LayerOpacity, 0, 1);
            // A captured photograph keeps its existing background semantics. Otherwise
            // canvas color, grid and objects form one lowest-to-highest opacity group.
            bool photograph = includeBackground && document.Background is { Visible: true };
            DrawBackground(canvas, document, assets, includeBackground, includeCanvasBackground && photograph, canvasOpacity: opacity);
            if (opacity > 0)
            {
                // Apply layer opacity once after compositing, so overlapping strokes do not become darker.
                if (opacity < 1)
                {
                    using var opacityPaint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) };
                    canvas.SaveLayer(ToRect(document.Viewport), opacityPaint);
                }
                if (!photograph) DrawPlaneColor(canvas, document, includeCanvasBackground);
                if (includeGrid) DrawGrid(canvas, document);
                DrawElements(canvas, document.Elements, assets, 0, document.Elements.Count);
                if (opacity < 1) canvas.Restore();
            }
            if (includeGrid && calibrationReference && space == SceneRenderSpace.ScreenComposite)
            {
                // Only thin editing guides appear above content; the configured grid
                // has already been composed beneath every object in the ordinary path.
                DrawGridCore(canvas, document, true);
            }
        }
        finally { canvas.RestoreToCount(saved); EndFrame(); }
    }

    internal SceneDocument BeginFrame(SceneDocument document, SceneRenderSpace space = SceneRenderSpace.ScreenComposite)
    {
        var source = document;
        document = WorldDocument(document);
        ObjectDisposedException.ThrowIf(_disposed, this); ClearLiveEffectImages(); NextAnimationDelay = null; _clock++;
        GridCandidateCount = GridLineCount = 0;
        GridDashIntervalCount = 0;
        UnresolvedGridDashIntervalCount = 0;
        if (space == SceneRenderSpace.PlaneContent && document.Elements.Any(e => e.Visible && RootOf(document, e) == RootCanvasKind.Main && ElementGeometry.IsLiveEffect(e.Kind)))
            throw new InvalidOperationException("Live desktop filters require screen composition before being placed back on the plane.");
        _drawingDocument = document; _renderSpace = space; _mapping = CanvasMapping.Create(document); _screenRootMapping = null;
        _frameLiveEffectContext = LiveEffectContext is { } context
            ? ReferenceEquals(context, source) ? document : WorldDocument(context) : null;
        PreparePresentation(document, space);
        PrepareWindowFrames(document);
        var viewport = document.Viewport;
        _referenceOpacityAtHorizon = space == SceneRenderSpace.ScreenComposite &&
            (!_mapping.IsScreenSafe(new(viewport.Left, viewport.Top)) || !_mapping.IsScreenSafe(new(viewport.Right, viewport.Top)) ||
             !_mapping.IsScreenSafe(new(viewport.Right, viewport.Bottom)) || !_mapping.IsScreenSafe(new(viewport.Left, viewport.Bottom)));
        return document;
    }
    internal void EndFrame()
    {
        ClearGpuEffectImages(); TrimCaches(); ClearLiveEffectImages(); _drawingDocument = null; _frameLiveEffectContext = null; _mapping = null; _screenRootMapping = null; _referenceOpacityAtHorizon = false;
    }
    internal void DrawBackground(SKCanvas canvas, SceneDocument document, AssetLibrary assets,
        bool includeBackground = true, bool includeCanvasBackground = true, bool sampling = false, double canvasOpacity = 1)
    {
        if (_renderSpace == SceneRenderSpace.ScreenComposite)
        {
            if (includeBackground) (ScreenBackdrop ?? (sampling ? _captureViewingBackdrop ?? (document.Background is null ? LiveBackdrop : null) : null))?.Draw(canvas);
            if (includeCanvasBackground && (document.Canvas.BackgroundColor >> 24) != 0)
                DrawPlaneColor(canvas, document, true, canvasOpacity);
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
            if (elements[i].Visible && (_renderSpace != SceneRenderSpace.PlaneContent || document is null || RootOf(document, elements[i]) == RootCanvasKind.Main))
            {
                if (ElementGeometry.IsLiveEffect(elements[i].Kind)) DrawLivePixelate(canvas, elements, i, assets);
                else DrawElement(canvas, elements[i], assets, transform, document);
            }
    }

    public byte[] ExportPng(SceneDocument document, AssetLibrary assets, bool transparent = false,
        SceneRenderSpace space = SceneRenderSpace.ScreenComposite, bool useAuthoredFrame = true) =>
        Export(document, assets, SKEncodedImageFormat.Png, 100, !transparent, space, useAuthoredFrame);

    public byte[] ExportJpeg(SceneDocument document, AssetLibrary assets, int quality = 92,
        SceneRenderSpace space = SceneRenderSpace.ScreenComposite, bool useAuthoredFrame = true) =>
        Export(document, assets, SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100), true, space, useAuthoredFrame);

    public TextMetrics MeasureText(string text, ElementStyle style, double maxWidth = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (EmojiGlyphs.IsEmoji(text, style)) return EmojiGlyphs.Measure(text, style.FontSize);
        return _text.Measure(text, style, maxWidth);
    }

    internal double FittedTextSize(string text, ElementStyle style, double width, double height, double requestedSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        double maximum = Math.Clamp(requestedSize, 1, ElementStyle.MaximumFontSize);
        if (!EmojiGlyphs.IsEmoji(text, style)) return _text.FitSize(text, style, width, height, maximum);
        var glyph = EmojiGlyphs.Measure(text, maximum);
        return Math.Clamp(maximum * Math.Min((width + .001) / glyph.Width, (height + .001) / glyph.Height), 1, maximum);
    }

    public TextEditingGeometry TextEditingGeometry(string text,ElementStyle style,double width)
    {
        if(!EmojiGlyphs.IsEmoji(text,style))return _text.EditingGeometry(text,style,width);
        var metrics=EmojiGlyphs.Measure(text,style.FontSize);
        return new([new(0,metrics.Height,[new(0,0),new(text.Length,metrics.Width)])]);
    }

    private byte[] Export(SceneDocument document, AssetLibrary assets, SKEncodedImageFormat format, int quality, bool background, SceneRenderSpace space, bool useAuthoredFrame)
    {
        var viewport = (useAuthoredFrame && space == SceneRenderSpace.ScreenComposite ? document.AuthoringReference?.Frame ?? document.Viewport : document.Viewport).Normalize();
        if (document.Viewport != viewport)
            document = SceneEvaluation.WithViewport(SceneEvaluation.Build(document).RenderDocument, viewport, document.Canvas.ResolveReference(document.Viewport));
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

    private void DrawElement(SKCanvas canvas, CanvasElement element, AssetLibrary assets, Projective2D planeToTarget, SceneDocument? document, bool knownHardClip = false)
    {
        if (element.Style.Opacity <= 0) return;
        var root = document is null ? RootCanvasKind.Main : RootOf(document, element);
        if (_renderSpace == SceneRenderSpace.PlaneContent && root != RootCanvasKind.Main) return;
        if (root != RootCanvasKind.Main) planeToTarget = Projective2D.Identity;
        if (element.Kind == ElementKind.Presentation) { DrawPresentation(canvas, element); return; }
        int saved = canvas.Save();
        try
        {
            // Our safe-domain clips are hard polygons. Keep that provenance rather than
            // treating an arbitrary caller-supplied path/AA clip as interchangeable.
            bool opacityClipSafe = canvas.IsClipRect || knownHardClip;
            bool screenDraft = IsTransientScreenElement(document, element);
            var transform = BillboardGeometry.ElementToTarget(element, screenDraft ? Projective2D.Identity : planeToTarget);
            if (!screenDraft && _renderSpace == SceneRenderSpace.ScreenComposite && document is not null)
            {
                var mapping = MappingFor(document, root);
                if (element.IsBillboard)
                {
                    if (!mapping.IsElementSafe(element)) return;
                }
                else
                {
                    if (!ClipSafePlane(canvas, document, root)) return;
                    var localBounds = DrawLocalBounds(element);
                    if (!transform.TryTransformBounds(localBounds, out _))
                    {
                        DrawClippedSource(canvas, element, assets, transform, mapping);
                        return;
                    }
                }
            }
            var matrix = ToMatrix(transform);
            canvas.Concat(in matrix);
            DrawLocalElement(canvas, element, assets, opacityClipSafe);
        }
        finally { canvas.RestoreToCount(saved); }
    }

    private void DrawLocalElement(SKCanvas canvas, CanvasElement element, AssetLibrary assets, bool knownHardClip = false)
    {
        double opacity = Math.Clamp(element.Style.Opacity, 0, 1);
        // Folding opacity into one primitive keeps self-intersections as one draw. Compound
        // objects still need a compositing unit so fill, outline and heads do not darken each other.
        var matrix = canvas.TotalMatrix;
        bool affine = matrix.Persp0 == 0 && matrix.Persp1 == 0 && matrix.Persp2 == 1;
        bool clipSafe = canvas.IsClipRect || knownHardClip;
        // At a visible safety boundary, repeated small coverage-rounding differences
        // can accumulate across many strokes. Keep single primitives on the exact
        // reference path there; compound objects retain their exact bounded layer.
        bool optimizeOpacity = OptimizeElementOpacity && !(_referenceOpacityAtHorizon && IsSingleDrawElement(element));
        bool singleDrawOpacity = optimizeOpacity && opacity < 1 && canvas.Context is null && clipSafe &&
            IsSingleDrawElement(element) && (affine || element.Kind is not (ElementKind.Freehand or ElementKind.Highlighter) && SupportsProjectiveOpacity(canvas, element));
        if (optimizeOpacity && opacity < 1 && !singleDrawOpacity && TryDrawRasterOpacity(canvas, element, assets, opacity, clipSafe)) return;
        DrawLocalElementCore(canvas, element, assets, !singleDrawOpacity, singleDrawOpacity ? opacity : 1);
    }

    private void DrawLocalElementCore(SKCanvas canvas, CanvasElement element, AssetLibrary assets, bool includeOpacity, double directOpacity = 1)
    {
        int saved = canvas.Save();
        try
        {
            double opacity = Math.Clamp(element.Style.Opacity, 0, 1);
            // One element is a compositing unit, including arrow heads, fill+outline and number labels.
            if (opacity < 1 && includeOpacity)
            {
                using var alpha = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) };
                canvas.SaveLayer(alpha);
            }
            if (element.Kind == ElementKind.Highlighter && TryDrawHighlighterDeposits(canvas, element, directOpacity)) return;
            if (element.Kind == ElementKind.Image) DrawImage(canvas, element, assets);
            else if (element.Kind == ElementKind.Text)
            {
                if (element.TextFrameFixed) canvas.ClipRect(new(0, 0, (float)element.Width, (float)element.Height));
                SetPaint(element.Style.Foreground, SKPaintStyle.Fill);
                if (EmojiGlyphs.IsEmoji(element.Text, element.Style))
                {
                    var glyph = EmojiGlyphs.Measure(element.Text, element.Style.FontSize);
                    canvas.Scale((float)(element.Width / glyph.Width), (float)(element.Height / glyph.Height));
                    EmojiGlyphs.Draw(canvas, element.Text, element.Style.FontSize, _paint);
                }
                else _text.DrawElement(canvas, element, _paint);
            }
            else if (element.Kind == ElementKind.Number) DrawNumber(canvas, element);
            else if (ElementGeometry.HasFlatHighlighterTip(element))
            {
                SetPaint(element.Style.Foreground, SKPaintStyle.Fill);
                var ink = HighlighterInk(_paint.Color);
                _paint.Color = directOpacity < 1 ? WithElementOpacity(ink, directOpacity) : ink;
                canvas.DrawPath(GetPath(element).Path, _paint);
            }
            else
            {
                var geometry = GetPath(element);
                bool shape = element.Kind is ElementKind.Rectangle or ElementKind.RoundedRectangle or ElementKind.Ellipse or ElementKind.Triangle or ElementKind.Symbol;
                uint fill = ElementAppearance.ShapeFill(element.Style);
                if (shape) DrawInsideShape(canvas, element, geometry, fill, directOpacity);
                else if (element.Style.StrokeWidth > 0 && geometry.HasArrowHeads)
                {
                    SetPaint(element.Style.Foreground, SKPaintStyle.Fill);
                    if (directOpacity < 1) _paint.Color = WithElementOpacity(_paint.Color, directOpacity);
                    canvas.DrawPath(geometry.ArrowInk, _paint);
                }
                else if (element.Style.StrokeWidth > 0)
                {
                    SetPaint(element.Style.Foreground, SKPaintStyle.Stroke, element.Style.StrokeWidth);
                    if (element.Kind == ElementKind.Highlighter) _paint.Color = HighlighterInk(_paint.Color);
                    var strokeMatrix = canvas.TotalMatrix;
                    var opacityStroke = !includeOpacity && directOpacity == 1 && canvas.Context is null &&
                        element.Kind is ElementKind.Freehand or ElementKind.Highlighter && element.Style.StrokePattern == StrokePattern.Solid &&
                        (strokeMatrix.Persp0 != 0 || strokeMatrix.Persp1 != 0) && element.Points.Count > 1
                        ? geometry.OpacityStroke(_paint, strokeMatrix) : null;
                    if (directOpacity < 1) _paint.Color = WithElementOpacity(_paint.Color, directOpacity);
                    if (element.Points.Count == 1 && element.Kind is ElementKind.Freehand or ElementKind.Highlighter)
                        canvas.DrawPoint((float)element.Points[0].X, (float)element.Points[0].Y, _paint);
                    else if (opacityStroke is not null)
                    {
                        _paint.Style = SKPaintStyle.Fill; canvas.DrawPath(opacityStroke, _paint); _paint.Style = SKPaintStyle.Stroke;
                    }
                    else if (element.Style.StrokePattern != StrokePattern.Solid)
                    {
                        _paint.Style = SKPaintStyle.Fill;
                        if (element.Style.StrokePattern is StrokePattern.Dashed or StrokePattern.DashDot &&
                            element.Kind is ElementKind.Freehand or ElementKind.Highlighter)
                        {
                            // Apply the shared dash mask during drawing. Intersecting the
                            // complete stroke and every mask polygon rebuilds a costly
                            // global boolean path on each pointer sample.
                            int clip = canvas.Save();
                            try
                            {
                                canvas.ClipPath(geometry.PatternMask, SKClipOperation.Intersect, true);
                                canvas.DrawPath(geometry.SolidStroke, _paint);
                            }
                            finally { canvas.RestoreToCount(clip); }
                        }
                        else canvas.DrawPath(geometry.PatternStroke, _paint);
                        _paint.Style = SKPaintStyle.Stroke;
                    }
                    else canvas.DrawPath(geometry.Path, _paint);
                    _paint.PathEffect = null;
                }
            }
        }
        finally { canvas.RestoreToCount(saved); }
    }

    private void DrawInsideShape(SKCanvas canvas, CanvasElement element, PathEntry geometry, uint fill, double directOpacity)
    {
        bool filled = (fill >> 24) != 0;
        bool stroked = ElementAppearance.HasShapeStroke(element.Style) && element.Style.StrokeWidth > 0 && (element.Style.PrimaryColor >> 24) != 0;
        // Replace fill with stroke on an isolated element layer, including the
        // antialiased transition. SourceOver would stack translucent colors there.
        int layer = filled && stroked ? canvas.SaveLayer(ToRect(geometry.StrokedBounds), null) : -1;
        try
        {
            if (filled)
            {
                SetPaint(fill, SKPaintStyle.Fill);
                if (directOpacity < 1) _paint.Color = WithElementOpacity(_paint.Color, directOpacity);
                canvas.DrawPath(geometry.Path, _paint);
            }
            if (stroked)
            {
                if (layer >= 0) _paint.BlendMode = SKBlendMode.Src;
                SetPaint(element.Style.Foreground, SKPaintStyle.Fill);
                if (directOpacity < 1) _paint.Color = WithElementOpacity(_paint.Color, directOpacity);
                canvas.DrawPath(geometry.InnerStroke, _paint);
            }
        }
        finally
        {
            _paint.BlendMode = SKBlendMode.SrcOver;
            if (layer >= 0) canvas.RestoreToCount(layer);
        }
    }

    private static bool IsSingleDrawElement(CanvasElement element)
    {
        if (element.Kind is ElementKind.Freehand or ElementKind.Highlighter) return true;
        if (!ElementGeometry.IsBoxShape(element.Kind)) return false;
        uint fill = ElementAppearance.ShapeFill(element.Style);
        return (fill >> 24) == 0 || !ElementAppearance.HasShapeStroke(element.Style) || element.Style.StrokeWidth <= 0;
    }

    private static SKColor WithElementOpacity(SKColor color, double opacity) =>
        color.WithAlpha((byte)(color.Alpha * (Alpha(opacity) + 1) >> 8));

    private void SetPaint(uint color, SKPaintStyle style, double width = 1)
    {
        _paint.Color = new SKColor(color);
        _paint.Style = style;
        _paint.StrokeWidth = (float)width;
        _paint.PathEffect = null;
    }

    // Grid families retain their native path effects; element patterns use the
    // shared solid-band masks below so drawing and picking agree.
    private static SKPathEffect? CreateDash(ElementStyle style)
    {
        float width = (float)Math.Max(.1, style.StrokeWidth);
        return style.StrokePattern switch
        {
            StrokePattern.Dashed => SKPathEffect.CreateDash([width * 4, width * 3], 0),
            StrokePattern.Dotted => SKPathEffect.CreateDash([width * .001f, width * 2.5f], 0),
            StrokePattern.DashDot => SKPathEffect.CreateDash([width * 3, width, width, width], 0),
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
            case ElementKind.Highlighter when ElementGeometry.HasFlatHighlighterTip(element):
                foreach (var polygon in ElementGeometry.FlatHighlighterPolygons(element.Points, element.Style))
                    AddHead(builder, polygon, true);
                break;
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
                    {
                        if (head.Style == ArrowHeadStyle.Open && element.Style.StrokePattern != StrokePattern.Solid)
                            AddRoundHeadInk(filledHeads, head.Points, element.Style.StrokeWidth / 2);
                        else
                            AddHead(head.Style == ArrowHeadStyle.Open ? openHeads : filledHeads, head.Points, head.Style != ArrowHeadStyle.Open);
                    }
                break;
        }
        var entry = new PathEntry(element, builder.Detach(), filledHeads.Detach(), openHeads.Detach(), _clock);
        _paths[element.Id] = entry;
        PathBuildCount++;
        return entry;
    }

    private RectD DrawLocalBounds(CanvasElement element) => element.Kind == ElementKind.Freehand
        ? GetPath(element).StrokedBounds : ElementGeometry.StrokedLocalBounds(element);

    private static void AddHead(SKPathBuilder builder, IReadOnlyList<PointD> points, bool closed)
    {
        if (points.Count < 2) return;
        builder.MoveTo(ToPoint(points[0]));
        foreach (var point in points.Skip(1)) builder.LineTo(ToPoint(point));
        if (closed) builder.Close();
    }

    private static SKPath BuildPatternMask(StrokePatternGeometry geometry)
    {
        using var builder = new SKPathBuilder();
        foreach (var polygon in geometry.DashPolygons)
        {
            double area = 0;
            for (int i = 0; i < polygon.Count; i++) { var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count]; area += a.X*b.Y-b.X*a.Y; }
            AddHead(builder, area >= 0 ? polygon : polygon.Reverse().ToArray(), true);
        }
        foreach (var point in geometry.Dots)
            builder.AddCircle((float)point.X, (float)point.Y, (float)geometry.Radius);
        return builder.Detach();
    }

    private static void AddRoundHeadInk(SKPathBuilder builder, IReadOnlyList<PointD> points, double radius)
    {
        foreach (var point in points)
            builder.AddCircle((float)point.X, (float)point.Y, (float)radius);
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i];
            double length = a.DistanceTo(b);
            if (length <= 1e-9) continue;
            var normal = new PointD(a.Y - b.Y, b.X - a.X) * (radius / length);
            AddHead(builder, [a + normal, a - normal, b - normal, b + normal], true);
        }
    }

    private void DrawNumber(SKCanvas canvas, CanvasElement element)
    {
        float width = (float)element.Width, height = (float)element.Height;
        if (width <= 0 || height <= 0) return;
        bool outline = ElementAppearance.IsOutlineNumber(element);
        double border = Math.Min(element.Style.StrokeWidth, Math.Min(width, height) / 2);
        uint fill = outline ? element.Style.SecondaryColor : element.Style.PrimaryColor;
        bool hasFill = (fill >> 24) != 0, hasBorder = outline && border > 0 && (element.Style.PrimaryColor >> 24) != 0;
        int layer = hasFill && hasBorder ? canvas.SaveLayer(new SKRect(0, 0, width, height), null) : -1;
        try
        {
            if (hasFill)
            {
                SetPaint(fill, SKPaintStyle.Fill);
                canvas.DrawOval(new SKRect(0, 0, width, height), _paint);
            }
            if (hasBorder)
            {
                SetPaint(element.Style.PrimaryColor, SKPaintStyle.Stroke, border);
                if (layer >= 0) _paint.BlendMode = SKBlendMode.Src;
                float inset = (float)(border / 2);
                canvas.DrawOval(new SKRect(inset, inset, width - inset, height - inset), _paint);
            }
        }
        finally
        {
            _paint.BlendMode = SKBlendMode.SrcOver;
            if (layer >= 0) canvas.RestoreToCount(layer);
        }
        string label = element.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var style = element.Style.Clone();
        style.FontSize = Math.Clamp(element.NumberTextSizeMode == NumberTextSizeMode.FitBadge ? Math.Min(width, height) * .58 : Math.Min(style.FontSize, Math.Min(width, height) * .58), 1, 2048);
        style.TextAlignment = TextAlignment.Left;
        // Measure the entire label without wrapping before fitting it. Canvas scaling
        // also keeps very small badges within bounds below the font engine's minimum size.
        var metrics = _text.Measure(label, style, 0);
        float scale = (float)Math.Min(1, Math.Min(width * .8 / metrics.Width, height * .8 / metrics.Height));
        canvas.Translate((width - (float)metrics.Width * scale) / 2, (height - (float)metrics.Height * scale) / 2);
        canvas.Scale(scale, scale);
        SetPaint(ElementAppearance.NumberTextColor(element), SKPaintStyle.Fill);
        _text.Draw(canvas, label, style, 0, _paint);
    }

    private void DrawImage(SKCanvas canvas, CanvasElement element, AssetLibrary assets)
    {
        if (element.Width <= 0 || element.Height <= 0) return;
        if (!ResolveImageFrame(element, assets, out var original, out var frameKey, out var layout))
        {
            if (element.WindowSource is not null && original is null)
            { SetPaint(0xFF50545B, SKPaintStyle.Fill); canvas.DrawRect(new SKRect(0, 0, (float)element.Width, (float)element.Height), _paint); }
            return;
        }
        var image = element.Effects.Count == 0 ? original : GetEffectImage(canvas, element, original, frameKey);
        var source = ToRect(layout.SourcePixels);
        DrawImageBorder(canvas, element, image, new(source.Left, source.Top, source.Width, source.Height), frameKey, layout.Destination);
        SetPaint(0xFFFFFFFF, SKPaintStyle.Fill);
        canvas.DrawImage(image, source, ToRect(layout.Destination), Linear, _paint);
    }

    private SKImage GetImage(string assetId, AssetLibrary assets)
    {
        if (PresentedImages?.TryGet(assetId, out var presented, out _) == true) return presented;
        var entry = GetImageEntry(assetId, assets); entry.Used = _clock;
        if (entry.Playback is { } playback)
        {
            if (playback.Select(AnimationTime, pickingCancellation)) AnimationFrameDecodeCount++;
            if (playback.NextDelay is { } delay && (NextAnimationDelay is null || delay < NextAnimationDelay)) NextAnimationDelay = delay;
        }
        return entry.Image;
    }

    private ImageEntry GetImageEntry(string id, AssetLibrary assets)
    {
        if (_images.TryGetValue(id, out var entry)) return entry;
        var bytes = assets.Get(id).Data.Span;
        // Evict before decoding, not after the entire scene has accumulated decoded images.
        long pixels = ImageDecodeBudget.ReadPixelCount(bytes);
        long workingBytes = pixels * 4 * (AnimatedImage.MayAnimate(bytes) ? 3 : 2);
        TrimImageCaches(Math.Max(0, CacheByteLimit - workingBytes));
        var playback = AnimatedImage.TryCreate(bytes);
        entry = playback is null ? new(AssetImport.Decode(bytes), _clock) : new(playback, _clock);
        _images.Add(id, entry); ImageDecodeCount++; return entry;
    }

    private SKImage GetEffectImage(SKCanvas destination, CanvasElement element, SKImage original, RasterFrameKey sourceFrame)
    {
        bool gpu = destination.Context is not null;
        var snapshots = element.Effects.Select(EffectSnapshot.From).ToArray();
        if (_effects.TryGetValue(element.Id, out var cached) && cached.IsGpu == gpu && cached.SourceFrame == sourceFrame && cached.Effects.SequenceEqual(snapshots))
        { cached.Used = _clock; return cached.Image; }
        if (cached is not null) { _effects.Remove(element.Id); cached.Dispose(); }
        TrimImageCaches(Math.Max(0, CacheByteLimit - Bytes(original) * 2), element.AssetId);
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
        var entry = new EffectEntry(sourceFrame, snapshots, surface.Snapshot(), _clock, gpu);
        _effects[element.Id] = entry;
        return entry.Image;
    }

    private void TrimCaches()
    {
        TrimHighlighterRasters();
        if (CachedElementOpacityBytes > Math.Max(0, Math.Min(CacheByteLimit, 64L * 1024 * 1024))) ReleaseElementOpacitySurface();
        foreach (var id in _paths.Where(p => p.Value.Used < _clock - 2).Select(p => p.Key).ToArray())
        { _paths[id].Dispose(); _paths.Remove(id); }
        foreach (var id in _effects.Where(p => p.Value.Used < _clock - 2).Select(p => p.Key).ToArray())
        { _effects[id].Dispose(); _effects.Remove(id); }
        // Animation frames retain their border masks until the byte-budget LRU evicts
        // them, so a loop can reuse frames even after several intervening draws.
        TrimImageBorderCaches();
        TrimImageCaches(Math.Max(0, CacheByteLimit));
    }

    private void TrimImageCaches(long limit, string? protectedImage = null)
    {
        while (CachedImageBytes > limit && _images.Count + _effects.Count + _imageBorders.Count > 0)
        {
            var image = _images.Where(entry => entry.Key != protectedImage).OrderBy(e => e.Value.Used).FirstOrDefault();
            var effect = _effects.Count == 0 ? default : _effects.MinBy(e => e.Value.Used);
            var border = _imageBorders.Count == 0 ? default : _imageBorders.MinBy(e => e.Value.Used);
            if (border.Value is not null && (image.Value is null || border.Value.Used <= image.Value.Used) &&
                (effect.Value is null || border.Value.Used <= effect.Value.Used))
            { border.Value.Dispose(); _imageBorders.Remove(border.Key); continue; }
            if (image.Value is null && effect.Value is null) break;
            if (image.Value is not null && (effect.Value is null || image.Value.Used <= effect.Value.Used))
            { image.Value.Dispose(); _images.Remove(image.Key); }
            else if (effect.Value is not null) { effect.Value.Dispose(); _effects.Remove(effect.Key); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var stroke in _highlighterStrokes.Values) stroke.Dispose();
        _highlighterStrokes.Clear(); _highlighterWorkingId = null;
        ClearLiveEffectImages();
        DisposeDepthBuffers();
        foreach (var entry in _paths.Values) entry.Dispose();
        foreach (var entry in _images.Values) entry.Dispose();
        foreach (var entry in _effects.Values) entry.Dispose();
        foreach (var entry in _imageBorders.Values) entry.Dispose();
        _paths.Clear(); _images.Clear(); _effects.Clear(); _imageBorders.Clear(); _animationMetadata.Clear();
        ReleaseElementOpacitySurface();
        presentationPlan?.Dispose(); presentationPlan = null; presentationState = null;
        windowPlan = null; windowState = null;
        _paint.Dispose(); _text.Dispose();
    }

    private static long Bytes(SKImage image) => (long)image.Width * image.Height * 4;
    private static byte Alpha(double opacity) => (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
    private static SKPoint ToPoint(PointD p) => new((float)p.X, (float)p.Y);
    private static SKRect ToRect(RectD r) => new((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom);
    private static SKMatrix ToMatrix(Projective2D t) => new((float)t.M11, (float)t.M12, (float)t.M13,
        (float)t.M21, (float)t.M22, (float)t.M23, (float)t.M31, (float)t.M32, (float)t.M33);

    private sealed class PathEntry(CanvasElement element, SKPath path, SKPath filledHeads, SKPath openHeads, long used) : IDisposable
    {
        private readonly ElementKind _kind = element.Kind;
        private readonly double _width = element.Width, _height = element.Height, _strokeWidth = element.Style.StrokeWidth;
        private readonly ArrowHeadStyle _arrowHeadStyle = element.Style.ArrowHeadStyle;
        private readonly ArrowHeadStyle? _startArrowHead = element.Style.StartArrowHead, _endArrowHead = element.Style.EndArrowHead;
        private readonly double? _cornerRadius = element.CornerRadius;
        private readonly double? _lineArcBulge = element.Style.LineArcBulge;
        private readonly double _lineArcOffset = element.Style.LineArcOffset;
        private readonly SymbolKind _symbol = element.Symbol;
        private readonly double _arrowHeadSize = element.Style.ArrowHeadSize;
        private readonly StrokePattern _strokePattern = element.Style.StrokePattern;
        private readonly HighlighterTip _highlighterTip = element.Style.HighlighterTip;
        private readonly double _highlighterTipAngle = element.Style.HighlighterTipAngle;
        private readonly PointCollection _points = element.Points.Snapshot();
        public SKPath Path { get; } = path;
        public RectD StrokedBounds { get; } = ElementGeometry.StrokedLocalBounds(element);
        public bool HasArrowHeads => !filledHeads.IsEmpty || !openHeads.IsEmpty;
        public SKPath ArrowInk => _arrowInk ??= BuildArrowInk();
        public long Used { get; set; } = used;
        private SKPath? _opacityStroke;
        private SKPath? _innerStroke;
        private SKPath? _patternStroke;
        private SKPath? _patternMask;
        private SKPath? _solidStroke;
        private SKPath? _arrowInk;
        private readonly bool _shapeStroke = ElementAppearance.HasShapeStroke(element.Style);
        private readonly StrokePatternGeometry? _patternGeometry = element.Style.StrokePattern == StrokePattern.Solid || ElementGeometry.HasFlatHighlighterTip(element) ||
            ElementGeometry.IsBoxShape(element.Kind) && !ElementAppearance.HasShapeStroke(element.Style)
            ? null : ElementGeometry.StrokePatternGeometry(element);
        public SKPath PatternStroke => _patternStroke ??= ElementGeometry.IsLine(_kind)
            ? BuildPatternMask(_patternGeometry!) : BuildStroke(false);
        public SKPath PatternMask => _patternMask ??= BuildPatternMask(_patternGeometry!);
        public SKPath SolidStroke => _solidStroke ??= BuildStroke(false, patterned: false);
        public SKPath InnerStroke
        {
            get
            {
                if (_innerStroke is not null) return _innerStroke;
                return _innerStroke = BuildStroke(true);
            }
        }
        private SKPath BuildArrowInk()
        {
            if (_patternGeometry is not null && ElementGeometry.IsLine(_kind))
                return BuildPatternedArrowInk();

            // The shaft and heads share a color. Union their coverage before applying it.
            var shaft = _strokePattern == StrokePattern.Solid ? SolidStroke : PatternStroke;
            if (openHeads.IsEmpty) return Union(shaft, filledHeads);
            using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)_strokeWidth,
                StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
            using var openInk = stroke.GetFillPath(openHeads);
            if (filledHeads.IsEmpty) return Union(shaft, openInk);
            using var heads = Union(filledHeads, openInk);
            return Union(shaft, heads);

            static SKPath Union(SKPath a, SKPath b) => a.IsEmpty ? new(b) : b.IsEmpty ? new(a) :
                a.Op(b, SKPathOp.Union) ?? throw new InvalidOperationException("Cannot combine the line and arrowheads.");
        }

        private SKPath BuildPatternedArrowInk()
        {
            // All contours wind in the same direction, including the heads. Draw once so
            // crossings and head joins share coverage without a global boolean operation.
            using var builder = new SKPathBuilder();
            builder.AddPath(PatternStroke);
            builder.AddPath(filledHeads);
            return builder.Detach();
        }
        private SKPath BuildStroke(bool inside, bool patterned = true)
        {
            if (!patterned || _patternGeometry is null) return BuildSolidBand(inside);
            // Fitted dots already are the finished ink.
            if (_strokePattern == StrokePattern.Dotted) return BuildPatternMask(_patternGeometry);
            using var solid = BuildSolidBand(inside);
            using var mask = BuildPatternMask(_patternGeometry);
            return solid.Op(mask, SKPathOp.Intersect) ??
                throw new InvalidOperationException("Cannot cut dash gaps from a solid stroke band.");
        }

        private SKPath BuildSolidBand(bool inside)
        {
            // Once the inner edge reaches the medial axis, no hole remains. A
            // doubled Skia stroke can fold back on itself and leave a spurious hole.
            if (inside && _kind == ElementKind.Ellipse && _strokeWidth >= Math.Min(_width, _height) / 2)
                return new(Path);
            using var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)_strokeWidth * (inside ? 2 : 1),
                StrokeJoin = inside ? SKStrokeJoin.Miter : SKStrokeJoin.Round, StrokeMiter = 4, StrokeCap = SKStrokeCap.Round };
            if (!inside) return paint.GetFillPath(Path);
            using var expanded = paint.GetFillPath(Path, 16);
            return Path.Op(expanded, SKPathOp.Intersect) ?? throw new InvalidOperationException("Cannot construct a solid stroke band.");
        }
        private float _opacityStrokeScale;
        public SKPath? OpacityStroke(SKPaint paint, SKMatrix matrix)
        {
            // Cache only when Skia's stroke-expansion resolution (the maximum
            // linear-column length) is unchanged. PathEntry owns geometry/width invalidation.
            float scale = (float)Math.Max(Math.Sqrt((double)matrix.ScaleX * matrix.ScaleX + (double)matrix.SkewY * matrix.SkewY),
                Math.Sqrt((double)matrix.SkewX * matrix.SkewX + (double)matrix.ScaleY * matrix.ScaleY));
            if (!float.IsFinite(scale) || scale <= 0) return null;
            if (_opacityStroke is null || scale != _opacityStrokeScale)
            {
                _opacityStroke?.Dispose(); _opacityStroke = paint.GetFillPath(Path, matrix); _opacityStrokeScale = scale;
            }
            return _opacityStroke;
        }
        public bool Matches(CanvasElement e) => _kind == e.Kind && _width == e.Width && _height == e.Height && _strokeWidth == e.Style.StrokeWidth &&
            _shapeStroke == ElementAppearance.HasShapeStroke(e.Style) &&
            _arrowHeadStyle == e.Style.ArrowHeadStyle && _arrowHeadSize == e.Style.ArrowHeadSize &&
            _startArrowHead == e.Style.StartArrowHead && _endArrowHead == e.Style.EndArrowHead && _cornerRadius == e.CornerRadius && _symbol == e.Symbol &&
            _strokePattern == e.Style.StrokePattern && _lineArcBulge == e.Style.LineArcBulge && _lineArcOffset == e.Style.LineArcOffset &&
            _highlighterTip == e.Style.HighlighterTip && _highlighterTipAngle == e.Style.HighlighterTipAngle &&
            e.Points.ContentEquals(_points);
        public void Dispose()
        {
            _opacityStroke?.Dispose(); _innerStroke?.Dispose(); _patternStroke?.Dispose(); _patternMask?.Dispose(); _solidStroke?.Dispose();
            _arrowInk?.Dispose();
            Path.Dispose(); filledHeads.Dispose(); openHeads.Dispose();
        }
    }
    private class ImageEntry : IDisposable
    {
        private readonly SharedSceneImage? image;
        public AnimatedImage? Playback { get; }
        public SharedSceneImage SharedImage => Playback?.SharedImage ?? image!;
        public bool HasImage => Playback?.HasImage ?? image is not null;
        public SKImage Image => SharedImage.Image;
        public int FrameIndex => Playback?.FrameIndex ?? -1;
        public long CachedBytes => Playback?.CachedBytes ?? Bytes(image!.Image);
        public long Used { get; set; }
        public ImageEntry(SKImage image, long used) { this.image = new(image); Used = used; }
        public ImageEntry(AnimatedImage playback, long used) { Playback = playback; Used = used; }
        public void Dispose() { image?.Release(); Playback?.Dispose(); }
    }
    private sealed class EffectEntry(RasterFrameKey sourceFrame, EffectSnapshot[] effects, SKImage image, long used, bool isGpu) : ImageEntry(image, used)
    {
        public bool IsGpu { get; } = isGpu;
        public RasterFrameKey SourceFrame { get; } = sourceFrame;
        public EffectSnapshot[] Effects { get; } = effects;
    }
    private readonly record struct EffectSnapshot(ImageEffectKind Kind, RectD Bounds, double Amount)
    {
        public static EffectSnapshot From(ImageEffect effect) => new(effect.Kind, effect.Bounds, effect.Amount);
    }
}
