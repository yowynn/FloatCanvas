using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>Bounded, pixels-only information. It contains no source nodes or editable input history.</summary>
public sealed record PresentationRenderInfo(int PrimitiveCount, bool BudgetExceeded, int DynamicFirst, int DynamicLast,
    IReadOnlyList<RectD> DirtyBounds);

public sealed partial class SceneRenderer
{
    public PresentationFrame? PresentationFrame { get; set; }
    /// <summary>The captured global physical origin. Input points subtract this exactly once.</summary>
    public PointD PresentationOrigin { get; set; }
    private PresentationPlan? presentationPlan;
    private PresentationState? presentationState;
    public long PresentationPlanBuildCount { get; private set; }
    public long PresentationFrameBuildCount { get; private set; }

    public PresentationRenderInfo PreparePresentation(SceneDocument document, SceneRenderSpace space = SceneRenderSpace.ScreenComposite)
    {
        document = _frameLiveEffectContext ?? WorldDocument(LiveEffectContext ?? document);
        bool sameWorld = presentationPlan is { } prior && (ReferenceEquals(prior.Document, document) ||
            document.IsRuntimeEvaluated && SceneEvaluation.RuntimeView(document) is { } runtime &&
            ReferenceEquals(runtime.Leaves, SceneEvaluation.RuntimeView(prior.Document)?.Leaves) &&
            ReferenceEquals(document.Canvas, prior.Document.Canvas) && document.Viewport == prior.Document.Viewport && document.LayerOpacity == prior.Document.LayerOpacity);
        if (!document.IsRuntimeEvaluated || !sameWorld || presentationPlan!.Space != space)
        {
            presentationPlan?.Dispose(); presentationPlan = CreatePresentationPlan(document, space);
            presentationState = null; PresentationPlanBuildCount++;
        }
        if (presentationState is { } cached && ReferenceEquals(cached.Frame, PresentationFrame) && cached.Origin == PresentationOrigin)
            return cached.Info;
        if (!double.IsFinite(PresentationOrigin.X) || !double.IsFinite(PresentationOrigin.Y)) throw new ArgumentOutOfRangeException(nameof(PresentationOrigin));
        var drawings = new Dictionary<Guid, PresentationDrawing>(); var damage = new List<RectD>(); int primitives = 0;
        bool exceeded = false;
        if (space == SceneRenderSpace.ScreenComposite)
            foreach (var region in presentationPlan.Regions)
            {
                if (PresentationFrame is null && region.Element.Presentation!.DisplayStyle != PresentationDisplayStyle.Keyboard) continue;
                var frame = PresentationFrame ?? PresentationFrame.Empty;
                var drawing = CreatePresentationDrawing(region, frame, ref primitives, out exceeded);
                if (exceeded) { drawings.Clear(); damage.Clear(); break; }
                if (drawing.Rows.Count == 0 && drawing.Cells.Count == 0) continue;
                drawings.Add(region.Element.Id, drawing); damage.AddRange(drawing.Damage);
            }
        var info = new PresentationRenderInfo(primitives, exceeded, presentationPlan.First, presentationPlan.Last, Array.AsReadOnly(damage.ToArray()));
        presentationState = new(PresentationFrame, PresentationOrigin, drawings, info); PresentationFrameBuildCount++; return info;
    }

    private PresentationPlan CreatePresentationPlan(SceneDocument document, SceneRenderSpace space)
    {
        var regions = new List<PresentationRegion>(); int first = -1, last = -1;
        var mapping = CanvasMapping.Create(document);
        if (document.LayerOpacity > 0)
            for (int index = 0; index < document.Elements.Count; index++)
            {
                var element = document.Elements[index];
                if (SceneEvaluation.RuntimeView(document) is { } runtime && runtime.FindElement(element.Id) is null) continue;
                if (element.Kind != ElementKind.Presentation || element.Presentation is null ||
                    !element.Visible || element.Style.Opacity <= 0) continue;
                var local = ElementGeometry.LocalBounds(element); if (local.IsEmpty) continue;
                var transform = space == SceneRenderSpace.PlaneContent ? ElementGeometry.LocalToPlane(element) : mapping.ElementToScreen(element);
                var polygon = PresentationLocalPolygon(document, element, mapping, transform, space);
                if (polygon.Count < 3) continue;
                var screen = polygon.Select(transform.TransformPoint).ToArray();
                var bounds = PointBounds(screen).Intersect(document.Viewport); if (bounds.IsEmpty) continue;
                using var builder = new SKPathBuilder(); builder.MoveTo(ToPoint(screen[0]));
                foreach (var point in screen.Skip(1)) builder.LineTo(ToPoint(point)); builder.Close();
                var style = element.Style.Clone(); style.TextAlignment = TextAlignment.Left;
                var layout = element.Presentation.DisplayStyle == PresentationDisplayStyle.Keyboard
                    ? element.Presentation.KeyboardLayout?.Clone() ?? PresentationKeyboardLayout.CreateDefault() : null;
                regions.Add(new(element, local, transform, polygon, bounds, builder.Detach(), style, layout));
                if (first < 0) first = index; last = index;
            }
        return new(document, space, regions, first, last);
    }

    private static IReadOnlyList<PointD> PresentationLocalPolygon(SceneDocument document, CanvasElement element,
        CanvasMapping mapping, Projective2D transform, SceneRenderSpace space)
    {
        if (space != SceneRenderSpace.ScreenComposite || !transform.TryInverse(out var inverse) ||
            !PresentationGeometry.TryScreenRegion(element, mapping, document.Viewport, out var screen)) return [];
        var polygon = new List<PointD>(screen.Count);
        foreach (var point in screen)
        {
            if (!inverse.TryTransformPoint(point, out var local)) return [];
            polygon.Add(local);
        }
        return polygon;
    }

    private PresentationDrawing CreatePresentationDrawing(PresentationRegion region, PresentationFrame frame, ref int count, out bool exceeded)
    {
        if (region.Element.Presentation!.DisplayStyle == PresentationDisplayStyle.Keyboard)
            return CreateKeyboardDrawing(region, frame, ref count, out exceeded);
        if (region.Element.Presentation.DisplayStyle == PresentationDisplayStyle.Keycaps)
            return CreateKeycapLogDrawing(region, frame, ref count, out exceeded);
        var rows = new List<PresentationRow>(); var damage = new List<RectD>();
        var settings = region.Element.Presentation!; exceeded = false;
        if (!frame.KeyboardSuppressed)
        {
            var chords = frame.RecentChords.Where(chord => VisibleChord(chord.Keys, settings.KeyDisplay) &&
                chord.Timestamp <= frame.Time && (chord.IsHeld || frame.Time - chord.Timestamp < TimeSpan.FromMilliseconds(settings.KeyHoldMilliseconds)))
                .OrderBy(chord => chord.Timestamp).TakeLast(settings.RecentChordLimit).ToList();
            double pad = Math.Min(region.Style.FontSize * .35, region.Local.Width / 8), gap = Math.Max(2, pad * .5);
            double maximum = Math.Max(.01, Math.Min(settings.KeyPanelMaximumWidth, region.Local.Width - pad * 2));
            foreach (var chord in chords.TakeLast(settings.RecentChordLimit))
            {
                string label = string.Join(" + ", chord.Keys.Select(key => key.Label));
                // Account for glyph work before shaping, not merely one draw call for a huge string.
                if (count + label.Length + 1 > PresentationLimits.MaximumPrimitives)
                { count = PresentationLimits.MaximumPrimitives + 1; exceeded = true; return new(rows, [], damage); }
                var metrics = _text.Measure(label, region.Style, Math.Max(.01, maximum - pad * 2));
                count += Math.Max(label.Length, metrics.GlyphCount) + 1;
                if (count > PresentationLimits.MaximumPrimitives) { exceeded = true; return new(rows, [], damage); }
                double fade = chord.IsHeld ? 1 : Math.Clamp(1 - (frame.Time - chord.Timestamp).TotalMilliseconds / settings.KeyHoldMilliseconds, 0, 1);
                rows.Add(new(label, new(0, 0, Math.Min(maximum, metrics.Width + pad * 2), metrics.Height + pad * 2), pad, fade));
            }
            double height = rows.Sum(row => row.Bounds.Height) + Math.Max(0, rows.Count - 1) * gap;
            bool top = settings.KeyPanelAlignment is PresentationPanelAlignment.TopLeft or PresentationPanelAlignment.TopCenter or PresentationPanelAlignment.TopRight;
            double y = top ? region.Local.Top + pad : region.Local.Bottom - pad - height;
            for (int index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                double x = settings.KeyPanelAlignment switch
                {
                    PresentationPanelAlignment.BottomCenter or PresentationPanelAlignment.TopCenter => region.Local.Center.X - row.Bounds.Width / 2,
                    PresentationPanelAlignment.BottomRight or PresentationPanelAlignment.TopRight => region.Local.Right - pad - row.Bounds.Width,
                    _ => region.Local.Left + pad
                };
                rows[index] = row with { Bounds = row.Bounds with { X = x, Y = y } }; y += row.Bounds.Height + gap;
                damage.Add(region.Transform.TryTransformBounds(rows[index].Bounds, out var projected) ? projected.Intersect(region.Bounds) : region.Bounds);
            }
        }
        exceeded = count > PresentationLimits.MaximumPrimitives;
        return new(rows, [], damage);
    }

    private PresentationDrawing CreateKeycapLogDrawing(PresentationRegion region, PresentationFrame frame, ref int count, out bool exceeded)
    {
        var cells = new List<PresentationCell>(); var damage = new List<RectD>(); var settings = region.Element.Presentation!;
        exceeded = false; if (frame.KeyboardSuppressed) return new([], cells, damage);
        var chords = frame.RecentChords.Where(chord => VisibleChord(chord.Keys, settings.KeyDisplay) &&
            chord.Timestamp <= frame.Time && (chord.IsHeld || frame.Time - chord.Timestamp < TimeSpan.FromMilliseconds(settings.KeyHoldMilliseconds)))
            .OrderBy(chord => chord.Timestamp).TakeLast(settings.RecentChordLimit);
        var lines = new List<(List<PresentationCell> Cells, double Width, double Height)>();
        double pad = Math.Min(region.Style.FontSize * .35, region.Local.Width / 8), gap = Math.Max(2, pad * .6);
        double maximum = Math.Max(.01, Math.Min(settings.KeyPanelMaximumWidth, region.Local.Width - pad * 2));
        foreach (var chord in chords)
        {
            var line = new List<PresentationCell>(); double x = 0, height = 0;
            double fade = chord.IsHeld ? 1 : Math.Clamp(1 - (frame.Time - chord.Timestamp).TotalMilliseconds / settings.KeyHoldMilliseconds, 0, 1);
            foreach (var cap in chord.Keys)
            {
                if (count + cap.Label.Length + 3 > PresentationLimits.MaximumPrimitives)
                { count = PresentationLimits.MaximumPrimitives + 1; exceeded = true; return new([], [], []); }
                var metrics = _text.Measure(cap.Label, region.Style, maximum);
                count += Math.Max(cap.Label.Length, metrics.GlyphCount) + 3;
                double width = Math.Max(region.Style.FontSize, metrics.Width) + pad * 2, cellHeight = metrics.Height + pad * 2 + 3;
                line.Add(new(cap.Label, new(x, 0, width, cellHeight), fade, false, region.Style.FontSize));
                x += width + gap; height = Math.Max(height, cellHeight);
            }
            double widthSum = Math.Max(.01, x - gap), scale = Math.Min(1, maximum / widthSum);
            line = line.Select(cell => cell with { Bounds = new(cell.Bounds.X * scale, 0, cell.Bounds.Width * scale, height * scale), FontSize = cell.FontSize * scale }).ToList();
            lines.Add((line, widthSum * scale, height * scale));
        }
        double totalHeight = lines.Sum(line => line.Height) + Math.Max(0, lines.Count - 1) * gap;
        double y = PanelTop(settings.KeyPanelAlignment) ? region.Local.Top + pad : region.Local.Bottom - pad - totalHeight;
        foreach (var line in lines)
        {
            double x = PanelX(settings.KeyPanelAlignment, region.Local, pad, line.Width);
            foreach (var cell in line.Cells)
            {
                var placed = cell with { Bounds = cell.Bounds with { X = cell.Bounds.X + x, Y = y } };
                cells.Add(placed); AddCellDamage(region, placed.Bounds, damage);
            }
            y += line.Height + gap;
        }
        exceeded = count > PresentationLimits.MaximumPrimitives; return new([], cells, damage);
    }

    private PresentationDrawing CreateKeyboardDrawing(PresentationRegion region, PresentationFrame frame, ref int count, out bool exceeded)
    {
        var cells = new List<PresentationCell>(); var damage = new List<RectD>(); var settings = region.Element.Presentation!;
        var keys = region.Layout!.Keys.Where(key => key.Enabled).ToArray(); exceeded = false;
        if (keys.Length == 0) return new([], cells, damage);
        double left = keys.Min(key => key.X), top = keys.Min(key => key.Y), right = keys.Max(key => key.X + key.Width), bottom = keys.Max(key => key.Y + key.Height);
        double pad = Math.Min(region.Style.FontSize * .35, Math.Min(region.Local.Width, region.Local.Height) / 8);
        double unit = Math.Min((region.Local.Width - pad * 2) / (right - left), (region.Local.Height - pad * 2) / (bottom - top));
        double x = PanelX(settings.KeyPanelAlignment, region.Local, pad, (right - left) * unit);
        double y = PanelTop(settings.KeyPanelAlignment) ? region.Local.Top + pad : region.Local.Bottom - pad - (bottom - top) * unit;
        foreach (var key in keys)
        {
            if (count + key.Label.Length + 3 > PresentationLimits.MaximumPrimitives)
            { count = PresentationLimits.MaximumPrimitives + 1; exceeded = true; return new([], [], []); }
            var bounds = new RectD(x + (key.X - left + .045) * unit, y + (key.Y - top + .045) * unit,
                (key.Width - .09) * unit, (key.Height - .09) * unit);
            double font = Math.Min(region.Style.FontSize, unit * .36);
            var textStyle = region.Style.Clone(); textStyle.FontSize = font;
            var metrics = _text.Measure(key.Label, textStyle, 1_000_000);
            font *= Math.Min(1, bounds.Width * .76 / Math.Max(.01, metrics.Width));
            count += Math.Max(key.Label.Length, metrics.GlyphCount) + 3;
            bool pressed = !frame.KeyboardSuppressed && frame.PressedKeys.Any(cap => PhysicalKeyMatches(key, cap));
            var cell = new PresentationCell(key.Label, bounds, 1, pressed, font);
            cells.Add(cell); AddCellDamage(region, bounds, damage);
        }
        exceeded = count > PresentationLimits.MaximumPrimitives; return new([], cells, damage);
    }
    private static bool PanelTop(PresentationPanelAlignment alignment) => alignment is PresentationPanelAlignment.TopLeft or PresentationPanelAlignment.TopCenter or PresentationPanelAlignment.TopRight;
    private static double PanelX(PresentationPanelAlignment alignment, RectD local, double pad, double width) => alignment switch
    {
        PresentationPanelAlignment.TopCenter or PresentationPanelAlignment.BottomCenter => local.Center.X - width / 2,
        PresentationPanelAlignment.TopRight or PresentationPanelAlignment.BottomRight => local.Right - pad - width,
        _ => local.Left + pad
    };
    private static void AddCellDamage(PresentationRegion region, RectD bounds, List<RectD> damage) =>
        damage.Add(region.Transform.TryTransformBounds(bounds, out var projected) ? projected.Intersect(region.Bounds) : region.Bounds);
    private static bool PhysicalKeyMatches(PresentationKeyboardKey key, PresentationKeyCap cap)
    {
        if (cap.ScanCode == 0) return cap.VirtualKey == key.VirtualKey;
        if (cap.ScanCode != key.ScanCode || cap.IsExtended != key.IsExtended) return false;
        // Physical positions survive language layouts and NumLock navigation VKs.
        // Pause and NumLock can share the low scan byte and need their VK as well.
        return key.ScanCode != 0x45 || cap.VirtualKey == key.VirtualKey;
    }

    private static bool VisibleChord(IReadOnlyList<PresentationKeyCap> keys, PresentationKeyDisplayMode mode) => keys.Count > 0 &&
        keys.Any(key => !key.IsModifier) && (mode == PresentationKeyDisplayMode.AllPhysicalKeys ||
         keys.Any(key => key.VirtualKey is 0x11 or 0xA2 or 0xA3 or 0x12 or 0xA4 or 0xA5 or 0x5B or 0x5C) ||
         keys.Any(key => key.VirtualKey is >= 0x70 and <= 0x87 or 0x08 or 0x09 or 0x0D or 0x1B or >= 0x21 and <= 0x2E));

    private bool HasPresentationInk(CanvasElement element) => element.Kind != ElementKind.Presentation ||
        presentationState?.Drawings.ContainsKey(element.Id) == true;

    private void DrawPresentation(SKCanvas canvas, CanvasElement element)
    {
        if (presentationState is not { } state || !state.Drawings.TryGetValue(element.Id, out var drawing) || presentationPlan is null) return;
        var region = presentationPlan.ById[element.Id]; int saved = canvas.Save();
        try
        {
            canvas.ClipPath(region.Clip, antialias: true);
            if (element.Style.Opacity < 1)
            {
                using var alpha = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(element.Style.Opacity)) };
                canvas.SaveLayer(ToRect(region.Bounds), alpha);
            }
            if (drawing.Rows.Count > 0 || drawing.Cells.Count > 0)
            {
                if (region.Transform.TryTransformBounds(region.Local, out _))
                {
                    int localSaved = canvas.Save();
                    try { var matrix = ToMatrix(region.Transform); canvas.Concat(in matrix); DrawPresentationKeys(canvas, region, drawing); }
                    finally { canvas.RestoreToCount(localSaved); }
                }
                else DrawClippedPresentationKeys(canvas, region, drawing);
            }

        }
        finally { canvas.RestoreToCount(saved); }
    }

    private void DrawPresentationKeys(SKCanvas canvas, PresentationRegion region, PresentationDrawing drawing)
    {
        using var paint = new SKPaint { IsAntialias = true };
        foreach (var row in drawing.Rows)
        {
            paint.Color = WithElementOpacity(new SKColor(region.Style.Background), row.Opacity);
            canvas.DrawRoundRect(ToRect(row.Bounds), (float)row.Padding, (float)row.Padding, paint);
            paint.Color = WithElementOpacity(new SKColor(region.Style.Foreground), row.Opacity);
            int saved = canvas.Save();
            try
            {
                canvas.Translate((float)(row.Bounds.Left + row.Padding), (float)(row.Bounds.Top + row.Padding));
                _text.Draw(canvas, row.Label, region.Style, Math.Max(.01, row.Bounds.Width - row.Padding * 2), paint);
            }
            finally { canvas.RestoreToCount(saved); }
        }
        foreach (var cell in drawing.Cells)
        {
            var bounds = cell.Bounds; double radius = Math.Min(bounds.Width, bounds.Height) * .13;
            uint foreground = cell.Pressed ? region.Style.Background : region.Style.Foreground;
            uint background = cell.Pressed ? region.Style.Foreground : region.Style.Background;
            paint.Style = SKPaintStyle.Fill; paint.Color = WithElementOpacity(new SKColor(background), cell.Opacity);
            canvas.DrawRoundRect(ToRect(bounds), (float)radius, (float)radius, paint);
            paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = (float)Math.Max(.5, Math.Min(bounds.Width, bounds.Height) * .025);
            paint.Color = WithElementOpacity(new SKColor(foreground), cell.Opacity * .65);
            canvas.DrawRoundRect(ToRect(bounds), (float)radius, (float)radius, paint);
            var face = bounds.Inflate(-Math.Min(bounds.Width, bounds.Height) * .07);
            canvas.DrawRoundRect(ToRect(face), (float)(radius * .7), (float)(radius * .7), paint);
            paint.Style = SKPaintStyle.Fill; paint.Color = WithElementOpacity(new SKColor(foreground), cell.Opacity);
            var style = region.Style.Clone(); style.FontSize = cell.FontSize;
            var metrics = _text.Measure(cell.Label, style, Math.Max(.01, face.Width));
            int saved = canvas.Save();
            try
            {
                canvas.ClipRect(ToRect(bounds));
                canvas.Translate((float)(bounds.Center.X - metrics.Width / 2), (float)(bounds.Center.Y - metrics.Height / 2 + (cell.Pressed ? radius * .3 : -radius * .15)));
                _text.Draw(canvas, cell.Label, style, Math.Max(.01, face.Width), paint);
            }
            finally { canvas.RestoreToCount(saved); }
        }
    }

    private void DrawClippedPresentationKeys(SKCanvas canvas, PresentationRegion region, PresentationDrawing drawing)
    {
        var bounds = PointBounds(region.LocalPolygon); if (bounds.IsEmpty) return;
        double scale = Math.Min(1, Math.Min(2048 / Math.Max(bounds.Width, bounds.Height), Math.Sqrt(4_194_304 / (bounds.Width * bounds.Height))));
        if (!double.IsFinite(scale) || scale <= 0) return;
        int width = Math.Clamp((int)Math.Ceiling(bounds.Width * scale), 1, 2048), height = Math.Clamp((int)Math.Ceiling(bounds.Height * scale), 1, 2048);
        using var surface = CreateIntermediateSurface(canvas, new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), "Cannot allocate clipped presentation keys.");
        surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Scale((float)(width / bounds.Width), (float)(height / bounds.Height));
        surface.Canvas.Translate(-(float)bounds.Left, -(float)bounds.Top); DrawPresentationKeys(surface.Canvas, region, drawing);
        using var image = surface.Snapshot();
        var imageToLocal = ToMatrix(Projective2D.FromAffine(Affine2D.Translation(bounds.Left, bounds.Top).Compose(Affine2D.Scale(bounds.Width / width, bounds.Height / height))));
        using var shader = image.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, Linear, imageToLocal);
        using var paint = new SKPaint { IsAntialias = true, Shader = shader };
        using var builder = new SKPathBuilder(); builder.MoveTo(ToPoint(region.LocalPolygon[0]));
        foreach (var point in region.LocalPolygon.Skip(1)) builder.LineTo(ToPoint(point)); builder.Close(); using var path = builder.Detach();
        int saved = canvas.Save();
        try { var matrix = ToMatrix(region.Transform); canvas.Concat(in matrix); canvas.DrawPath(path, paint); }
        finally { canvas.RestoreToCount(saved); }
    }

    private static bool PresentationIntersects(SceneDocument document, CanvasElement element, RectD rectangle)
    {
        var mapping = CanvasMapping.Create(document); var transform = mapping.ElementToScreen(element);
        var local = PresentationLocalPolygon(document, element, mapping, transform, SceneRenderSpace.ScreenComposite);
        if (local.Count < 3) return false;
        var screen = local.Select(transform.TransformPoint).ToArray();
        return ClipPolygon(screen, [new(rectangle.Left, rectangle.Top), new(rectangle.Right, rectangle.Top),
            new(rectangle.Right, rectangle.Bottom), new(rectangle.Left, rectangle.Bottom)]).Count >= 3;
    }

    private sealed record PresentationRegion(CanvasElement Element, RectD Local, Projective2D Transform,
        IReadOnlyList<PointD> LocalPolygon, RectD Bounds, SKPath Clip, ElementStyle Style, PresentationKeyboardLayout? Layout);
    private sealed class PresentationPlan(SceneDocument document, SceneRenderSpace space, List<PresentationRegion> regions, int first, int last) : IDisposable
    {
        internal SceneDocument Document { get; } = document;
        internal SceneRenderSpace Space { get; } = space;
        internal IReadOnlyList<PresentationRegion> Regions { get; } = regions;
        internal Dictionary<Guid, PresentationRegion> ById { get; } = regions.ToDictionary(region => region.Element.Id);
        internal int First { get; } = first;
        internal int Last { get; } = last;
        public void Dispose() { foreach (var region in Regions) region.Clip.Dispose(); }
    }
    private sealed record PresentationState(PresentationFrame? Frame, PointD Origin, Dictionary<Guid, PresentationDrawing> Drawings, PresentationRenderInfo Info);
    private sealed record PresentationDrawing(List<PresentationRow> Rows, List<PresentationCell> Cells, List<RectD> Damage);
    private sealed record PresentationCell(string Label, RectD Bounds, double Opacity, bool Pressed, double FontSize);
    private sealed record PresentationRow(string Label, RectD Bounds, double Padding, double Opacity);
}
