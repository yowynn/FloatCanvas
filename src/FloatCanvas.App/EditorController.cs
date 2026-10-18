using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal enum EditorTool
{
    Select, Freehand, Highlighter, Line, Arrow, DoubleArrow, Rectangle, RoundedRectangle, Ellipse,
    Triangle, Text, Number, Image, Eraser, Crop, Blur, Pixelate, Redact, CaptureRegion, Symbol, Emoji, Stamp
}

/// <summary>Converts native pointer gestures into atomic document operations.</summary>
internal sealed partial class EditorController
{
    public EditorSession Session { get; private set; }
    private EditorTool tool = EditorTool.Freehand;
    public EditorTool Tool
    {
        get => tool;
        set
        {
            if (value == EditorTool.Crop && !CanCropSelection) value = EditorTool.Select;
            if (value == EditorTool.Triangle)
            {
                var parameters = Parameters; parameters.Tool = value; ApplyParameters(parameters); return;
            }
            if (tool != value) { Cancel(); tool = value; Changed?.Invoke(); }
        }
    }
    public ElementStyle Style { get; private set; } = new() { Stroke = ColorLibrary.ForegroundRed };
    public double? CornerRadius { get; set; }
    public SymbolKind Symbol { get; set; }
    public bool ConstrainAspect { get; set; }
    public double EffectStrength { get; set; } = 12;
    public string Emoji { get; set; } = "😀";
    public double DefaultWidth { get; set; } = 160;
    public double DefaultHeight { get; set; } = 100;
    public int NumberStart { get; set; } = 1;
    public int NumberStep { get; set; } = 1;
    public CaptureShape CaptureShape { get; set; } = CaptureShape.Rectangle;
    public CaptureSource CaptureSource { get; set; } = CaptureSource.Background;
    public double CaptureBorderWidth { get; set; }
    public uint CaptureBorderColor { get; set; } = ColorLibrary.ForegroundRed;
    public ToolParameters Parameters => new() { Tool = Tool, Style = Style.Clone(), CornerRadius = CornerRadius,
        Symbol = Symbol, ConstrainAspect = ConstrainAspect, EffectStrength = EffectStrength, Emoji = Emoji, DefaultWidth = DefaultWidth, DefaultHeight = DefaultHeight,
        NumberStart = NumberStart, NumberStep = NumberStep, CaptureShape = CaptureShape, CaptureSource = CaptureSource,
        CaptureBorderWidth = CaptureBorderWidth, CaptureBorderColor = CaptureBorderColor };
    public void ApplyParameters(ToolParameters parameters)
    {
        if (!parameters.IsValid()) throw new ArgumentException(nameof(parameters));
        // Old presets retain their serialized enum; only future drawing uses the unified symbol tool.
        Cancel(); tool = parameters.Tool == EditorTool.Triangle ? EditorTool.Symbol :
            parameters.Tool == EditorTool.Crop && !CanCropSelection ? EditorTool.Select : parameters.Tool;
        Style = parameters.Style.Clone(); CornerRadius = parameters.CornerRadius;
        Symbol = parameters.Tool == EditorTool.Triangle ? SymbolKind.Triangle : parameters.Symbol;
        ConstrainAspect = parameters.ConstrainAspect; EffectStrength = parameters.EffectStrength;
        Emoji = parameters.Emoji; DefaultWidth = parameters.DefaultWidth; DefaultHeight = parameters.DefaultHeight;
        CaptureShape = parameters.CaptureShape; CaptureSource = parameters.CaptureSource;
        CaptureBorderWidth = parameters.CaptureBorderWidth; CaptureBorderColor = parameters.CaptureBorderColor;
        if (parameters.Tool == EditorTool.Number) { NumberStart = parameters.NumberStart; NumberStep = parameters.NumberStep; }
        Changed?.Invoke();
    }
    public PointD ScreenOrigin { get; set; }
    public RectD? Marquee { get; private set; }
    public IReadOnlyList<PointD>? EffectPreviewPolygon { get; private set; }
    public CaptureSelection? CapturePreview { get; private set; }
    public bool HasCaptureGesture => gesture is "capture" or "capture-polygon";
    public event Action? Changed;
    public event Action<string>? Message;
    public event Action<CanvasElement?, PointD>? EditText;
    public event Action<CaptureSelection>? CaptureSelectionCompleted;
    public event Action<RectD, ImageEffectKind, double, uint>? DesktopEffectRequested;
    private PointD start;
    private PointD startScreen, pointerScreen;
    private CanvasElement? drawing;
    private CanvasElement? effectTarget;
    private long cropRevision;
    private string? gesture;
    private RectD selectionBounds;
    private Dictionary<Guid, Affine2D> transforms = [];
    private CanvasElement? resizeSource;
    private int endpoint;
    private RectD? imageRegion;
    private (PointD Position, OverlayModifiers Modifiers, OverlayMouseButton Button)? lastGestureMove;
    private bool deselectOnClick, gestureMoved;
    private readonly List<PointD> capturePoints = [];
    private const int MaximumCapturePoints = 65536;

    internal static bool IsCreationTool(EditorTool value) => value is EditorTool.Freehand or EditorTool.Highlighter or EditorTool.Line or
        EditorTool.Arrow or EditorTool.DoubleArrow or EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse or
        EditorTool.Triangle or EditorTool.Symbol or EditorTool.Text or EditorTool.Number or EditorTool.Emoji or EditorTool.Image or EditorTool.CaptureRegion;
    internal static bool UsesDefaultSize(EditorTool value) => value is
        EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse or EditorTool.Triangle or EditorTool.Symbol;
    internal bool ShouldShowSelection(OverlayModifiers modifiers) =>
        (Tool == EditorTool.Select || IsCreationTool(Tool)) && (modifiers & OverlayModifiers.Control) == 0 && Session.Selection.Count > 0;

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
    public bool CanCropSelection => ImageToolEligibility.TryGetCropTarget(Session, out _);
    private void OnChanged(object? sender, EventArgs e)
    {
        if (gesture == "image-crop" && !IsCurrentCropTarget()) Cancel();
        if (tool == EditorTool.Crop && !CanCropSelection) tool = EditorTool.Select;
        Changed?.Invoke();
    }
    public void ReplaceSession(EditorSession session, PointD origin)
    {
        Cancel();
        Session.Changed -= OnChanged;
        Session = session;
        session.Changed += OnChanged;
        ScreenOrigin = origin;
        if (tool == EditorTool.Crop && !CanCropSelection) tool = EditorTool.Select;
        Changed?.Invoke();
    }
    public bool TryToDocument(OverlayPoint p, out PointD point) =>
        CanvasMapping.Create(Session.Document).TryToPlane(new(p.X - ScreenOrigin.X, p.Y - ScreenOrigin.Y), out point);
    public PointD ToDocument(OverlayPoint p) => TryToDocument(p, out var point) ? point :
        throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
    public void Cancel()
    {
        gesture = null; drawing = effectTarget = resizeSource = null; Marquee = imageRegion = null; EffectPreviewPolygon = null;
        lastGestureMove = null;
        CapturePreview = null; capturePoints.Clear();
        deselectOnClick = gestureMoved = false;
        Session.Cancel(); Changed?.Invoke();
    }
    public void Pointer(OverlayPointerEvent e)
    {
        if (e.Kind == OverlayPointerKind.Cancel) { Cancel(); return; }
        pointerScreen = new(e.Position.X - ScreenOrigin.X, e.Position.Y - ScreenOrigin.Y);
        bool screenGesture = HasCaptureGesture || Tool == EditorTool.CaptureRegion && gesture is null;
        PointD p;
        if (screenGesture) p = pointerScreen;
        else if (!CanvasMapping.Create(Session.Document).TryToPlane(pointerScreen, out p))
        {
            if (e.Kind == OverlayPointerKind.Up) Cancel();
            return;
        }
        if (gesture == "image-crop" && !IsCurrentCropTarget()) { Cancel(); return; }
        if (e.Button == OverlayMouseButton.Right && e.Kind == OverlayPointerKind.Down) { Cancel(); Tool = EditorTool.Select; return; }
        if (gesture == "capture-polygon" && e.Button == OverlayMouseButton.Left &&
            e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick)
        {
            if (e.Kind == OverlayPointerKind.DoubleClick ||
                capturePoints.Count >= 3 && p.DistanceTo(capturePoints[0]) <= 8) CompleteCaptureSelection();
            else
            {
                AddCapturePoint(p);
                UpdateCapturePreview(p, e.Modifiers, includeHover: false);
            }
            return;
        }
        if (e.Kind == OverlayPointerKind.DoubleClick && Tool == EditorTool.Select)
        {
            Cancel();
            var hit = HitTestScreen(pointerScreen, 4);
            if (hit?.Kind is ElementKind.Text or ElementKind.Number) { EditText?.Invoke(hit, p); return; }
        }
        if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && e.Button == OverlayMouseButton.Left)
        {
            Cancel(); start = p; startScreen = pointerScreen; lastGestureMove = (p, e.Modifiers, e.Button);
            bool creating = IsCreationTool(Tool);
            bool forceCreate = (e.Modifiers & OverlayModifiers.Control) != 0;
            deselectOnClick = creating && !forceCreate && Session.Selection.Count > 0;
            var selectionPoint = p;
            bool canSelect = !screenGesture || CanvasMapping.Create(Session.Document).TryToPlane(pointerScreen, out selectionPoint);
            if (creating && !forceCreate && canSelect && IsWithinCurrentSelection(selectionPoint))
            { start = selectionPoint; StartSelect(selectionPoint, e.Modifiers, selectedOnly: true); deselectOnClick = false; return; }
            if (deselectOnClick && Tool == EditorTool.Image)
            { Session.SetSelection([]); return; }
            if (Tool == EditorTool.Text) { EditText?.Invoke(null, p); return; }
            if (Tool is EditorTool.Number or EditorTool.Emoji) { BeginSizedPlacement(p); return; }
            if (Tool == EditorTool.Image) { Message?.Invoke(L.Get("Editor.ImportImageHint")); return; }
            if (Tool == EditorTool.Stamp) return;
            if (Tool == EditorTool.Crop)
            {
                if (!ImageToolEligibility.TryGetCropTarget(Session, out effectTarget))
                { Message?.Invoke(L.Get("Editor.CropImageHint")); return; }
                cropRevision = Session.SceneRevision;
                gesture = "image-crop"; UpdateImageRegion(p); return;
            }
            if (Tool == EditorTool.CaptureRegion)
            {
                gesture = CaptureShape == CaptureShape.PolygonLasso ? "capture-polygon" : "capture";
                if (CaptureShape is CaptureShape.FreehandLasso or CaptureShape.PolygonLasso) capturePoints.Add(p);
                UpdateCapturePreview(p, e.Modifiers, includeHover: false); return;
            }
            if (Tool is EditorTool.Blur or EditorTool.Pixelate or EditorTool.Redact)
            {
                if (Tool is EditorTool.Blur or EditorTool.Pixelate && Session.Document.Kind == DocumentKind.LiveOverlay)
                { gesture = "desktop-effect"; Marquee = new(p.X,p.Y,0,0); return; }
                if (!TryGetSelectedImageTarget(out effectTarget, canvasFallback: true)) return;
                effectTarget ??= Session.Document.Background;
                if (effectTarget == null)
                { gesture = "desktop-effect"; Marquee = new(p.X, p.Y, 0, 0); return; }
                if (!CanEditImage(effectTarget)) return;
                gesture = "effect"; UpdateImageRegion(p); return;
            }
            if (Tool == EditorTool.Eraser) { gesture = "erase"; Session.BeginTransaction(L.Get("Editor.History.Erase")); Erase(p); return; }
            if (Tool == EditorTool.Select) { StartSelect(p, e.Modifiers); return; }
            gesture = "draw";
            Session.BeginTransaction(L.Format("Editor.History.Draw", L.Get("Editor.ElementKind." + Tool)));
            drawing = new CanvasElement { Kind = Enum.Parse<ElementKind>(Tool.ToString()), Style = Style.Clone(), CornerRadius = CornerRadius,
                Symbol = Symbol, Transform = Affine2D.Translation(p.X, p.Y), Points = [new(0, 0)] };
            if (Tool == EditorTool.Highlighter) drawing.Style.StrokeWidth = Math.Max(16, Style.StrokeWidth);
            Session.Document.Elements.Add(drawing);
            Session.Selection.Clear();
            if (Tool is not (EditorTool.Freehand or EditorTool.Highlighter)) Session.Selection.Add(drawing.Id);
            Session.NotifyChanged();
        }
        else if (e.Kind == OverlayPointerKind.Move && gesture != null)
        {
            // Layered-window presentation can generate another mouse-move at the same point.
            // A stationary gesture must not request a render which generates the next no-op move.
            var sample = (p, e.Modifiers, e.Button);
            if (lastGestureMove == sample) return;
            lastGestureMove = sample;
            gestureMoved |= pointerScreen.DistanceTo(startScreen) >= 2;
            Move(p, e.Modifiers);
        }
        else if (e.Kind == OverlayPointerKind.Up && gesture != null && e.Button == OverlayMouseButton.Left)
        {
            if (gesture == "capture-polygon") return;
            if (gesture == "capture")
            {
                UpdateCapturePreview(p, e.Modifiers, includeHover: false);
                CompleteCaptureSelection(); return;
            }
            gestureMoved |= pointerScreen.DistanceTo(startScreen) >= 2;
            Move(p, e.Modifiers);
            var completed = gesture;
            gesture = null;
            if (completed == "marquee") SelectScreenRectangle(Marquee!.Value, (e.Modifiers & OverlayModifiers.Shift) != 0);
            else if (completed == "effect") CommitEffect();
            else if (completed == "desktop-effect" && Marquee is { Width: >= 2, Height: >= 2 } region)
                DesktopEffectRequested?.Invoke(region, Enum.Parse<ImageEffectKind>(Tool.ToString()), EffectStrength, Style.Stroke);
            else if (completed == "image-crop") CommitImageCrop();
            else if (Session.IsTransactionActive)
            {
                if (completed == "draw" && Tool is EditorTool.Number or EditorTool.Emoji)
                {
                    if (drawing?.Kind == ElementKind.Number)
                        Session.Document.NextNumber = Math.Max(Session.Document.NextNumber,
                            (int)Math.Min(int.MaxValue, (long)drawing.Number + NumberStep));
                    Session.Commit();
                }
                else if (completed == "draw" && !gestureMoved && drawing is not null && ElementGeometry.IsLine(drawing.Kind))
                { Session.Cancel(); if (deselectOnClick) Session.SetSelection([]); }
                else if (completed == "draw" && !gestureMoved && drawing is not null && UsesDefaultSize(Tool))
                { ApplyDefaultSize(drawing, start, e.Modifiers); Session.Commit(); }
                else if (completed == "draw" && deselectOnClick && !gestureMoved)
                { Session.Cancel(); Session.SetSelection([]); }
                else Session.Commit();
            }
            drawing = effectTarget = null; Marquee = imageRegion = null; EffectPreviewPolygon = null; Changed?.Invoke();
        }
    }

    private bool IsWithinCurrentSelection(PointD point)
    {
        if (Session.SelectionBounds() is not { } bounds || !Session.SelectedElements().Any(e => e.Visible && !e.Locked)) return false;
        return bounds.Contains(point) || SelectionScreenCorners.Any(c => c.DistanceTo(pointerScreen) < 12) ||
            SelectionScreenRotationHandle is { } rotation && pointerScreen.DistanceTo(rotation) < 14;
    }

    private void ApplyDefaultSize(CanvasElement element, PointD center, OverlayModifiers modifiers)
    {
        double width = DefaultWidth, height = DefaultHeight;
        if (ConstrainAspect || (modifiers & OverlayModifiers.Shift) != 0) height = width;
        element.Width = width; element.Height = height; element.Points.Clear();
        element.Transform = Affine2D.Translation(center.X - width / 2, center.Y - height / 2);
    }

    private void BeginSizedPlacement(PointD center)
    {
        var style = Style.Clone();
        if (Tool == EditorTool.Number)
        {
            if (NumberStep < 1) throw new ArgumentOutOfRangeException(nameof(NumberStep));
            double diameter = Math.Max(12, style.FontSize / .58);
            drawing = new() { Kind = ElementKind.Number, Width = diameter, Height = diameter,
                Style = style, Number = Session.Document.NextNumber };
        }
        else
        {
            style.FontFamily = EmojiGlyphs.FontFamily;
            var size = EmojiGlyphs.Measure(Emoji, style.FontSize);
            drawing = new() { Kind = ElementKind.Text, Text = Emoji, Width = size.Width, Height = size.Height, Style = style };
        }
        drawing.Transform = Affine2D.Translation(center.X - drawing.Width / 2, center.Y - drawing.Height / 2);
        Session.BeginTransaction(L.Get("History.AddElement"));
        gesture = "draw";
        // These tools keep their continuous click-to-insert behavior even when another object is selected.
        deselectOnClick = false;
        Session.Document.Elements.Add(drawing);
        Session.Selection.Clear(); Session.Selection.Add(drawing.Id);
        Session.NotifyChanged();
    }

    private void ResizePlacement(PointD point)
    {
        if (drawing is null || !gestureMoved) return;
        var delta = point - start;
        double scale = Math.Max(1 / Math.Min(drawing.Width, drawing.Height),
            Math.Max(Math.Abs(delta.X) / drawing.Width, Math.Abs(delta.Y) / drawing.Height));
        double x = delta.X < 0 ? start.X - drawing.Width * scale : start.X;
        double y = delta.Y < 0 ? start.Y - drawing.Height * scale : start.Y;
        // Scale the complete instance so the glyph and badge grow together; tool defaults stay unchanged.
        drawing.Transform = Affine2D.Translation(x, y).Compose(Affine2D.Scale(scale, scale));
    }

    /// <summary>Handles keys belonging to an unfinished capture before ordinary editor commands.</summary>
    public bool HandleCaptureKey(int virtualKey)
    {
        if (!HasCaptureGesture) return false;
        if (virtualKey == 0x1B) { Cancel(); return true; }
        if (gesture != "capture-polygon") return false;
        if (virtualKey == 0x0D) { CompleteCaptureSelection(); return true; }
        if (virtualKey != 0x08) return false;
        if (capturePoints.Count > 0) capturePoints.RemoveAt(capturePoints.Count - 1);
        if (capturePoints.Count == 0) Cancel();
        else UpdateCapturePreview(capturePoints[^1], OverlayModifiers.None, includeHover: false);
        return true;
    }

    private void AddCapturePoint(PointD point)
    {
        if (capturePoints.Count < MaximumCapturePoints &&
            (capturePoints.Count == 0 || capturePoints[^1].DistanceTo(point) >= .75)) capturePoints.Add(point);
    }

    private void UpdateCapturePreview(PointD point, OverlayModifiers modifiers, bool includeHover = true)
    {
        IReadOnlyList<PointD> contour = Array.Empty<PointD>();
        RectD bounds;
        if (CaptureShape is CaptureShape.Rectangle or CaptureShape.Ellipse)
        {
            if ((modifiers & OverlayModifiers.Shift) != 0)
            {
                var delta = point - start; double side = Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
                point = start + new PointD(Math.CopySign(side, delta.X), Math.CopySign(side, delta.Y));
            }
            bounds = RectD.FromPoints(start, point);
        }
        else
        {
            if (CaptureShape == CaptureShape.FreehandLasso) AddCapturePoint(point);
            var points = capturePoints.ToList();
            if (CaptureShape == CaptureShape.PolygonLasso && includeHover &&
                (points.Count == 0 || points[^1] != point)) points.Add(point);
            contour = Array.AsReadOnly(points.ToArray());
            bounds = CaptureBounds(points);
        }
        CapturePreview = new(CaptureShape, CaptureSource, bounds, contour, CaptureBorderWidth, CaptureBorderColor);
        Changed?.Invoke();
    }

    private void CompleteCaptureSelection()
    {
        if (gesture == "capture-polygon")
            UpdateCapturePreview(capturePoints.LastOrDefault(), OverlayModifiers.None, includeHover: false);
        var selection = CapturePreview;
        bool valid = selection is { Bounds.Width: >= 2, Bounds.Height: >= 2 } &&
            (selection.Shape is CaptureShape.Rectangle or CaptureShape.Ellipse || HasCaptureArea(selection.Points));
        // Finishing and cancellation both discard only transient geometry, without a document transaction.
        gesture = null; CapturePreview = null; capturePoints.Clear(); lastGestureMove = null;
        deselectOnClick = gestureMoved = false;
        Changed?.Invoke();
        if (valid) CaptureSelectionCompleted?.Invoke(selection!);
    }

    private static RectD CaptureBounds(IReadOnlyList<PointD> points) => points.Count == 0 ? default :
        new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));

    private static bool HasCaptureArea(IReadOnlyList<PointD> points)
    {
        if (points.Count < 3) return false;
        var first = points[0];
        var edge = points.Skip(1).FirstOrDefault(point => point.DistanceTo(first) >= .75) - first;
        // Signed polygon area would reject valid self-intersecting contours. Any non-collinear
        // vertex is enough here; the renderer applies the even-odd mask and checks visible pixels.
        return points.Any(point => Math.Abs(edge.X * (point.Y - first.Y) - edge.Y * (point.X - first.X)) >= 1);
    }

    private void StartSelect(PointD p, OverlayModifiers modifiers, bool selectedOnly = false)
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
                    if (CanvasMapping.Create(Session.Document).ToScreen(element.Transform.TransformPoint(element.Points[i])).DistanceTo(pointerScreen) < 12)
                    { drawing = element; endpoint = i; gesture = "endpoint"; Session.BeginTransaction(L.Get("Editor.History.AdjustEndpoint")); return; }
            }
            if (SelectionScreenRotationHandle is { } rotation && pointerScreen.DistanceTo(rotation) < 14) gesture = "rotate";
            else if (SelectionScreenCorners.Any(c => pointerScreen.DistanceTo(c) < 12)) gesture = "resize";
        }
        if (gesture == null && selectedOnly && bounds is not null)
        { selectionBounds = bounds.Value; gesture = "move"; }
        if (gesture == null)
        {
            var hit = HitTestScreen(pointerScreen, 7);
            if (hit == null) { gesture = "marquee"; Marquee = new(pointerScreen.X, pointerScreen.Y, 0, 0); return; }
            bool individual = (modifiers & OverlayModifiers.Alt) != 0;
            if ((modifiers & OverlayModifiers.Shift) != 0)
            {
                var ids = Session.SelectionUnitIds(hit.Id, individual).ToArray();
                Session.SetSelection(ids.All(Session.Selection.Contains) ? Session.Selection.Except(ids) : Session.Selection.Union(ids));
                return;
            }
            var unit = Session.SelectionUnitIds(hit.Id, individual);
            if (individual || !unit.All(Session.Selection.Contains)) Session.SetSelection(unit);
            selectionBounds = Session.SelectionBounds()!.Value;
            gesture = "move";
        }
        transforms = Session.SelectedElements().Where(x => !x.Locked).ToDictionary(x => x.Id, x => x.Transform);
        resizeSource = gesture == "resize" && selected.Length == 1 && ElementGeometry.IsBoxShape(selected[0].Kind) ? selected[0].Clone() : null;
        Session.BeginTransaction(L.Get("Editor.History.Transform"));
    }

    private void Move(PointD p, OverlayModifiers modifiers)
    {
        if (HasCaptureGesture) { UpdateCapturePreview(p, modifiers); return; }
        if (gesture is "effect" or "image-crop") { UpdateImageRegion(p); Changed?.Invoke(); return; }
        if (gesture == "marquee") { Marquee = RectD.FromPoints(startScreen, pointerScreen); Changed?.Invoke(); return; }
        if (gesture == "desktop-effect") { Marquee = RectD.FromPoints(start, p); Changed?.Invoke(); return; }
        if (gesture == "erase") { Erase(p); return; }
        if (gesture == "endpoint" && drawing != null)
        {
            drawing.Points[endpoint] = drawing.Transform.Inverse().TransformPoint(p); Session.NotifyChanged(); return;
        }
        if (gesture == "draw" && drawing != null)
        {
            if (Tool is EditorTool.Number or EditorTool.Emoji)
            {
                ResizePlacement(p); Session.NotifyChanged(); return;
            }
            var local = p - start;
            if (drawing.Kind is ElementKind.Freehand or ElementKind.Highlighter)
            {
                if (CanvasMapping.Create(Session.Document).ToScreen(drawing.Transform.TransformPoint(drawing.Points[^1])).DistanceTo(pointerScreen) >= .75) drawing.Points.Add(local);
                if (drawing.Points.Count == 1) drawing.Points.Add(new(.1, .1));
            }
            else if (drawing.Kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow) drawing.Points = [new(0, 0), local];
            else
            {
                if (ConstrainAspect || (modifiers & OverlayModifiers.Shift) != 0)
                { var side = Math.Max(Math.Abs(local.X), Math.Abs(local.Y)); p = start + new PointD(Math.CopySign(side, local.X), Math.CopySign(side, local.Y)); }
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
            if (single.Length == 1 && resizeSource is { } source && ElementGeometry.IsBoxShape(single[0].Kind))
            {
                var element = single[0]; var original = transforms[element.Id];
                if (!original.TryInverse(out var inverse)) return;
                var localCenter = ElementGeometry.LocalBounds(source).Center;
                var fromLocal = inverse.TransformPoint(start) - localCenter;
                var toLocal = inverse.TransformPoint(p) - localCenter;
                double scaleX = Math.Abs(fromLocal.X) < .001 ? 1 : Math.Max(.02, toLocal.X / fromLocal.X);
                double scaleY = Math.Abs(fromLocal.Y) < .001 ? 1 : Math.Max(.02, toLocal.Y / fromLocal.Y);
                if ((modifiers & OverlayModifiers.Shift) != 0) scaleX = scaleY = UniformScale(fromLocal, toLocal);
                element.Width = source.Width; element.Height = source.Height; element.CornerRadius = source.CornerRadius; element.Transform = original;
                ElementGeometry.ResizeShapeGeometry(element, scaleX, scaleY);
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
        Session.Document.Elements.RemoveAll(e => !e.Locked && ElementHitTestScreen(e, pointerScreen, 10));
        Session.NotifyChanged();
    }
    private void CommitEffect()
    {
        if (effectTarget == null || imageRegion is not { Width: >= 1, Height: >= 1 } local) return;
        var normalized = SourceRegion(effectTarget, local);
        if (normalized.IsEmpty) return;
        var targetId = effectTarget.Id;
        var effect = new ImageEffect { Kind = Enum.Parse<ImageEffectKind>(Tool.ToString()), Bounds = normalized, Color = Style.Stroke, Amount = EffectStrength };
        Session.Execute(L.Get("Editor.History.AddImageEffect"), d => (d.Background?.Id == targetId ? d.Background : d.Elements.First(e => e.Id == targetId))!.Effects.Add(effect));
    }
    private bool CanEditImage(CanvasElement image)
    {
        if (image.Width > 0 && image.Height > 0 && ElementGeometry.LocalToPlane(image).TryInverse(out _)) return true;
        Message?.Invoke(L.Get("Editor.InvalidImage")); return false;
    }
    private bool TryGetSelectedImageTarget(out CanvasElement? image, bool canvasFallback = false)
    {
        var selected = Session.SelectedElements().Where(e => e.Kind == ElementKind.Image).ToArray();
        image = selected.Length == 1 ? selected[0] : null;
        if (selected.Length > 1 && !canvasFallback) { Message?.Invoke(L.Get("Editor.SelectSingleImage")); return false; }
        if (image?.Locked == true) { Message?.Invoke(L.Get("Editor.ImageTargetLocked")); return false; }
        return true;
    }
    private void UpdateImageRegion(PointD p)
    {
        if (effectTarget == null || !ElementGeometry.LocalToPlane(effectTarget).TryInverse(out var inverse)) return;
        imageRegion = RectD.FromPoints(inverse.TransformPoint(start), inverse.TransformPoint(p)).Intersect(new(0, 0, effectTarget.Width, effectTarget.Height));
        EffectPreviewPolygon = Corners(imageRegion.Value).Select(ElementGeometry.LocalToPlane(effectTarget).TransformPoint).ToArray();
        Marquee = ElementGeometry.LocalToPlane(effectTarget).TransformBounds(imageRegion.Value);
    }
    private static RectD SourceRegion(CanvasElement image, RectD local)
    {
        var crop = (image.ImageCrop ?? new RectD(0, 0, 1, 1)).Normalize().Intersect(new(0, 0, 1, 1));
        return new(crop.X + local.X / image.Width * crop.Width, crop.Y + local.Y / image.Height * crop.Height,
            local.Width / image.Width * crop.Width, local.Height / image.Height * crop.Height);
    }
    private void CommitImageCrop()
    {
        if (!IsCurrentCropTarget() || effectTarget is not { } target || imageRegion is not { Width: >= 1, Height: >= 1 } local) return;
        var normalized = SourceRegion(target, local);
        if (normalized.IsEmpty) return;
        var id = target.Id;
        Session.Execute(L.Get("Editor.History.CropImage"), d =>
        {
            var image = d.Elements.First(e => e.Id == id);
            image.ImageCrop = normalized;
            image.Width = local.Width; image.Height = local.Height;
            if (image.ProjectivePlacement is { } placement)
                image.ProjectivePlacement = placement.Compose(Projective2D.FromAffine(Affine2D.Translation(local.X, local.Y)));
            else image.Transform = image.Transform.Compose(Affine2D.Translation(local.X, local.Y));
        });
    }
    private bool IsCurrentCropTarget() => Session.SceneRevision == cropRevision &&
        ImageToolEligibility.TryGetCropTarget(Session, out var selected) && ReferenceEquals(selected, effectTarget);
    private static double UniformScale(PointD from, PointD to) => Math.Max(.02,
        (to.X * from.X + to.Y * from.Y) / Math.Max(1e-12, from.X * from.X + from.Y * from.Y));
    public static PointD[] Corners(RectD b) => [new(b.Left, b.Top), new(b.Right, b.Top), new(b.Right, b.Bottom), new(b.Left, b.Bottom)];
}
