using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal enum EditorTool
{
    Select, Freehand, Highlighter, Line, Arrow, DoubleArrow, Rectangle, RoundedRectangle, Ellipse,
    Triangle, Text, Number, Image, Eraser, Crop, Blur, Pixelate, Redact, CaptureRegion
}

/// <summary>Converts native pointer gestures into atomic document operations.</summary>
internal sealed class EditorController
{
    public EditorSession Session { get; private set; }
    private EditorTool tool = EditorTool.Freehand;
    public EditorTool Tool
    {
        get => tool;
        set { if (tool != value) { Cancel(); tool = value; Changed?.Invoke(); } }
    }
    public ElementStyle Style { get; } = new() { Stroke = 0xFF2383E2 };
    public PointD ScreenOrigin { get; set; }
    public RectD? Marquee { get; private set; }
    public IReadOnlyList<PointD>? EffectPreviewPolygon { get; private set; }
    public event Action? Changed;
    public event Action<string>? Message;
    public event Action<CanvasElement?, PointD>? EditText;
    public event Action<RectD>? CaptureRegionSelected;
    private PointD start;
    private CanvasElement? drawing;
    private CanvasElement? effectTarget;
    private string? gesture;
    private RectD selectionBounds;
    private Dictionary<Guid, Affine2D> transforms = [];
    private int endpoint;
    private RectD? imageRegion;
    private (PointD Position, OverlayModifiers Modifiers, OverlayMouseButton Button)? lastGestureMove;

    /// <summary>The contiguous z-order span whose pixels can change during this pointer gesture.</summary>
    public (int First, int Last, bool BypassCache) RasterPreview
    {
        get
        {
            if (!Session.IsTransactionActive) return (-1, -1, false);
            // Erasure changes the element list. Unknown transactions may mutate any content.
            // Both use the reference draw path rather than reuse potentially stale surfaces.
            if (gesture is not ("draw" or "endpoint" or "move" or "rotate" or "resize")) return (-1, -1, true);
            int first = int.MaxValue, last = -1;
            for (int i = 0; i < Session.Document.Elements.Count; i++)
            {
                var id = Session.Document.Elements[i].Id;
                bool active = gesture is "draw" or "endpoint" ? drawing?.Id == id : transforms.ContainsKey(id);
                if (active) { first = Math.Min(first, i); last = i; }
            }
            return last < 0 ? (-1, -1, true) : (first, last, false);
        }
    }

    public EditorController(EditorSession session) { Session = session; session.Changed += OnChanged; }
    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke();
    public void ReplaceSession(EditorSession session, PointD origin)
    {
        Cancel();
        Session.Changed -= OnChanged;
        Session = session;
        session.Changed += OnChanged;
        ScreenOrigin = origin;
        Changed?.Invoke();
    }
    public PointD ToDocument(OverlayPoint p) => new(p.X - ScreenOrigin.X, p.Y - ScreenOrigin.Y);
    public void Cancel()
    {
        gesture = null; drawing = effectTarget = null; Marquee = imageRegion = null; EffectPreviewPolygon = null;
        lastGestureMove = null;
        Session.Cancel(); Changed?.Invoke();
    }
    public void Pointer(OverlayPointerEvent e)
    {
        var p = ToDocument(e.Position);
        if (e.Kind == OverlayPointerKind.Cancel) { Cancel(); return; }
        if (e.Button == OverlayMouseButton.Right && e.Kind == OverlayPointerKind.Down) { Cancel(); Tool = EditorTool.Select; return; }
        if (e.Kind == OverlayPointerKind.DoubleClick && Tool == EditorTool.Select)
        {
            Cancel();
            var hit = Session.HitTest(p);
            if (hit?.Kind is ElementKind.Text or ElementKind.Number) { EditText?.Invoke(hit, p); return; }
        }
        if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && e.Button == OverlayMouseButton.Left)
        {
            Cancel(); start = p; lastGestureMove = (p, e.Modifiers, e.Button);
            if (Tool == EditorTool.Text) { EditText?.Invoke(null, p); return; }
            if (Tool == EditorTool.Number)
            {
                Session.Add(new CanvasElement { Kind = ElementKind.Number, Width = 40, Height = 40,
                    Transform = Affine2D.Translation(p.X - 20, p.Y - 20), Style = Style.Clone(), Number = Session.Document.NextNumber });
                return;
            }
            if (Tool == EditorTool.Image) { Message?.Invoke(L.Get("Editor.ImportImageHint")); return; }
            if (Tool == EditorTool.Crop)
            {
                if (!TryGetSelectedImageTarget(out effectTarget)) return;
                if (effectTarget is not null)
                {
                    if (!CanEditImage(effectTarget)) return;
                    gesture = "image-crop"; UpdateImageRegion(p); return;
                }
                if (Session.Document.Kind != DocumentKind.FrozenCapture) { Message?.Invoke(L.Get("Editor.CropImageHint")); return; }
                gesture = "region"; Marquee = new(p.X, p.Y, 0, 0); return;
            }
            if (Tool == EditorTool.CaptureRegion) { gesture = "region"; Marquee = new(p.X, p.Y, 0, 0); return; }
            if (Tool is EditorTool.Blur or EditorTool.Pixelate or EditorTool.Redact)
            {
                if (!TryGetSelectedImageTarget(out effectTarget)) return;
                effectTarget ??= Session.Document.Background;
                if (effectTarget == null) { Message?.Invoke(L.Get("Editor.ImageEffectHint")); return; }
                if (!CanEditImage(effectTarget)) return;
                gesture = "effect"; UpdateImageRegion(p); return;
            }
            if (Tool == EditorTool.Eraser) { gesture = "erase"; Session.BeginTransaction(L.Get("Editor.History.Erase")); Erase(p); return; }
            if (Tool == EditorTool.Select) { StartSelect(p, e.Modifiers); return; }
            gesture = "draw";
            Session.BeginTransaction(L.Format("Editor.History.Draw", L.Get("Editor.ElementKind." + Tool)));
            drawing = new CanvasElement { Kind = Enum.Parse<ElementKind>(Tool.ToString()), Style = Style.Clone(), Transform = Affine2D.Translation(p.X, p.Y), Points = [new(0, 0)] };
            if (Tool == EditorTool.Highlighter) drawing.Style.StrokeWidth = Math.Max(16, Style.StrokeWidth);
            Session.Document.Elements.Add(drawing);
            Session.Selection.Clear();
            Session.Selection.Add(drawing.Id);
            Session.NotifyChanged();
        }
        else if (e.Kind == OverlayPointerKind.Move && gesture != null)
        {
            // Layered-window presentation can generate another mouse-move at the same point.
            // A stationary gesture must not request a render which generates the next no-op move.
            var sample = (p, e.Modifiers, e.Button);
            if (lastGestureMove == sample) return;
            lastGestureMove = sample;
            Move(p, e.Modifiers);
        }
        else if (e.Kind == OverlayPointerKind.Up && gesture != null && e.Button == OverlayMouseButton.Left)
        {
            Move(p, e.Modifiers);
            var completed = gesture;
            gesture = null;
            if (completed == "marquee") Session.SelectRectangle(Marquee!.Value, (e.Modifiers & OverlayModifiers.Shift) != 0);
            else if (completed == "region")
            {
                if (Marquee is { Width: >= 2, Height: >= 2 } region)
                {
                    if (Tool == EditorTool.CaptureRegion) CaptureRegionSelected?.Invoke(region);
                    else
                    {
                        var cropped = Session.Document.Viewport.Intersect(region);
                        if (!cropped.IsEmpty) Session.Execute(L.Get("Editor.History.CropCanvas"), d => d.Viewport = cropped);
                    }
                }
            }
            else if (completed == "effect") CommitEffect();
            else if (completed == "image-crop") CommitImageCrop();
            else if (Session.IsTransactionActive) Session.Commit();
            drawing = effectTarget = null; Marquee = imageRegion = null; EffectPreviewPolygon = null; Changed?.Invoke();
        }
    }

    private void StartSelect(PointD p, OverlayModifiers modifiers)
    {
        var bounds = Session.SelectionBounds();
        var selected = Session.SelectedElements().Where(x => !x.Locked).ToArray();
        if (bounds != null && selected.Length > 0)
        {
            selectionBounds = bounds.Value;
            if (selected.Length == 1 && selected[0].Kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow && selected[0].Points.Count > 1)
            {
                var element = selected[0];
                foreach (var i in new[] { 0, element.Points.Count - 1 })
                    if (element.Transform.TransformPoint(element.Points[i]).DistanceTo(p) < 12)
                    { drawing = element; endpoint = i; gesture = "endpoint"; Session.BeginTransaction(L.Get("Editor.History.AdjustEndpoint")); return; }
            }
            if (p.DistanceTo(new(selectionBounds.Center.X, selectionBounds.Top - 28)) < 14) gesture = "rotate";
            else if (Corners(selectionBounds).Any(c => p.DistanceTo(c) < 12)) gesture = "resize";
        }
        if (gesture == null)
        {
            var hit = Session.HitTest(p, 7);
            if (hit == null) { gesture = "marquee"; Marquee = new(p.X, p.Y, 0, 0); return; }
            if ((modifiers & OverlayModifiers.Shift) != 0) { Session.SelectAt(p, true, 7); return; }
            if (!Session.Selection.Contains(hit.Id)) Session.SetSelection([hit.Id]);
            selectionBounds = Session.SelectionBounds()!.Value;
            gesture = "move";
        }
        transforms = Session.SelectedElements().Where(x => !x.Locked).ToDictionary(x => x.Id, x => x.Transform);
        Session.BeginTransaction(L.Get("Editor.History.Transform"));
    }

    private void Move(PointD p, OverlayModifiers modifiers)
    {
        if (gesture is "effect" or "image-crop") { UpdateImageRegion(p); Changed?.Invoke(); return; }
        if (gesture is "marquee" or "region") { Marquee = RectD.FromPoints(start, p); Changed?.Invoke(); return; }
        if (gesture == "erase") { Erase(p); return; }
        if (gesture == "endpoint" && drawing != null)
        {
            drawing.Points[endpoint] = drawing.Transform.Inverse().TransformPoint(p); Session.NotifyChanged(); return;
        }
        if (gesture == "draw" && drawing != null)
        {
            var local = p - start;
            if (drawing.Kind is ElementKind.Freehand or ElementKind.Highlighter)
            {
                if (drawing.Points[^1].DistanceTo(local) >= .75) drawing.Points.Add(local);
                if (drawing.Points.Count == 1) drawing.Points.Add(new(.1, .1));
            }
            else if (drawing.Kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow) drawing.Points = [new(0, 0), local];
            else
            {
                var rect = RectD.FromPoints(start, p);
                drawing.Width = Math.Max(1, rect.Width); drawing.Height = Math.Max(1, rect.Height);
                drawing.Transform = Affine2D.Translation(rect.X, rect.Y); drawing.Points.Clear();
            }
            Session.NotifyChanged(); return;
        }
        Affine2D delta;
        if (gesture == "move") delta = Affine2D.Translation(p.X - start.X, p.Y - start.Y);
        else if (gesture == "rotate")
        {
            var center = selectionBounds.Center;
            var degrees = (Math.Atan2(p.Y - center.Y, p.X - center.X) - Math.Atan2(start.Y - center.Y, start.X - center.X)) * 180 / Math.PI;
            if ((modifiers & OverlayModifiers.Shift) != 0) degrees = Math.Round(degrees / 15) * 15;
            delta = Affine2D.Around(center, Affine2D.Rotation(degrees));
        }
        else if (gesture == "resize")
        {
            var single = Session.SelectedElements().Where(e => !e.Locked).ToArray();
            if (single.Length == 1 && single[0].Kind is ElementKind.Rectangle or ElementKind.RoundedRectangle or ElementKind.Ellipse or ElementKind.Triangle)
            {
                var element = single[0]; var original = transforms[element.Id];
                if (!original.TryInverse(out var inverse)) return;
                var localCenter = ElementGeometry.LocalBounds(element).Center;
                var fromLocal = inverse.TransformPoint(start) - localCenter;
                var toLocal = inverse.TransformPoint(p) - localCenter;
                double scaleX = Math.Abs(fromLocal.X) < .001 ? 1 : Math.Max(.02, toLocal.X / fromLocal.X);
                double scaleY = Math.Abs(fromLocal.Y) < .001 ? 1 : Math.Max(.02, toLocal.Y / fromLocal.Y);
                if ((modifiers & OverlayModifiers.Shift) != 0) scaleX = scaleY = UniformScale(fromLocal, toLocal);
                element.Transform = original.Compose(Affine2D.Around(localCenter, Affine2D.Scale(scaleX, scaleY)));
                Session.NotifyChanged(); return;
            }
            var center = selectionBounds.Center;
            var from = start - center; var to = p - center;
            var sx = Math.Abs(from.X) < 1 ? 1 : Math.Max(.02, to.X / from.X);
            var sy = Math.Abs(from.Y) < 1 ? 1 : Math.Max(.02, to.Y / from.Y);
            var shapeOnly = Session.SelectedElements().All(e => e.Kind is ElementKind.Rectangle or ElementKind.RoundedRectangle or ElementKind.Ellipse or ElementKind.Triangle);
            if (!shapeOnly || Session.Selection.Count > 1 || (modifiers & OverlayModifiers.Shift) != 0) sx = sy = UniformScale(from, to);
            delta = Affine2D.Around(center, Affine2D.Scale(sx, sy));
        }
        else return;
        foreach (var element in Session.SelectedElements())
            if (transforms.TryGetValue(element.Id, out var original)) element.Transform = delta.Compose(original);
        Session.NotifyChanged();
    }

    private void Erase(PointD p)
    {
        Session.Document.Elements.RemoveAll(e => !e.Locked && ElementGeometry.HitTest(e, p, 10));
        Session.NotifyChanged();
    }
    private void CommitEffect()
    {
        if (effectTarget == null || imageRegion is not { Width: >= 1, Height: >= 1 } local) return;
        var normalized = SourceRegion(effectTarget, local);
        if (normalized.IsEmpty) return;
        var targetId = effectTarget.Id;
        var effect = new ImageEffect { Kind = Enum.Parse<ImageEffectKind>(Tool.ToString()), Bounds = normalized, Color = 0xFF111111 };
        Session.Execute(L.Get("Editor.History.AddImageEffect"), d => (d.Background?.Id == targetId ? d.Background : d.Elements.First(e => e.Id == targetId))!.Effects.Add(effect));
    }
    private bool CanEditImage(CanvasElement image)
    {
        if (image.Width > 0 && image.Height > 0 && image.Transform.TryInverse(out _)) return true;
        Message?.Invoke(L.Get("Editor.InvalidImage")); return false;
    }
    private bool TryGetSelectedImageTarget(out CanvasElement? image)
    {
        var selected = Session.SelectedElements().Where(e => e.Kind == ElementKind.Image).ToArray();
        image = selected.Length == 1 ? selected[0] : null;
        if (selected.Length > 1) { Message?.Invoke(L.Get("Editor.SelectSingleImage")); return false; }
        if (image?.Locked == true) { Message?.Invoke(L.Get("Editor.ImageTargetLocked")); return false; }
        return true;
    }
    private void UpdateImageRegion(PointD p)
    {
        if (effectTarget == null || !effectTarget.Transform.TryInverse(out var inverse)) return;
        imageRegion = RectD.FromPoints(inverse.TransformPoint(start), inverse.TransformPoint(p)).Intersect(new(0, 0, effectTarget.Width, effectTarget.Height));
        EffectPreviewPolygon = Corners(imageRegion.Value).Select(effectTarget.Transform.TransformPoint).ToArray();
        Marquee = effectTarget.Transform.TransformBounds(imageRegion.Value);
    }
    private static RectD SourceRegion(CanvasElement image, RectD local)
    {
        var crop = (image.ImageCrop ?? new RectD(0, 0, 1, 1)).Normalize().Intersect(new(0, 0, 1, 1));
        return new(crop.X + local.X / image.Width * crop.Width, crop.Y + local.Y / image.Height * crop.Height,
            local.Width / image.Width * crop.Width, local.Height / image.Height * crop.Height);
    }
    private void CommitImageCrop()
    {
        if (effectTarget == null || imageRegion is not { Width: >= 1, Height: >= 1 } local) return;
        var normalized = SourceRegion(effectTarget, local);
        if (normalized.IsEmpty) return;
        var id = effectTarget.Id;
        Session.Execute(L.Get("Editor.History.CropImage"), d =>
        {
            var image = d.Elements.First(e => e.Id == id);
            image.ImageCrop = normalized;
            image.Width = local.Width; image.Height = local.Height;
            image.Transform = image.Transform.Compose(Affine2D.Translation(local.X, local.Y));
        });
    }
    private static double UniformScale(PointD from, PointD to) => Math.Max(.02,
        (to.X * from.X + to.Y * from.Y) / Math.Max(1e-12, from.X * from.X + from.Y * from.Y));
    public static PointD[] Corners(RectD b) => [new(b.Left, b.Top), new(b.Right, b.Top), new(b.Right, b.Bottom), new(b.Left, b.Bottom)];
}
