using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal enum EditorTool
{
    Select, Freehand, Highlighter, Line, Arrow, DoubleArrow, Rectangle, RoundedRectangle, Ellipse,
    Triangle, Text, Number, Image, Eraser, Crop, Blur, Pixelate,
    // Retained numeric value for reading old tool caches and presets only. Never activates.
    Redact, CaptureRegion, Symbol, Emoji, Stamp, Canvas
}

/// <summary>Converts native pointer gestures into atomic document operations.</summary>
internal sealed partial class EditorController
{
    public EditorSession Session { get; private set; }
    internal bool CanvasContentHidden { get; set; }
    private SceneRenderer? selectionRenderer;
    private SceneRenderer SelectionRenderer
    {
        get
        {
            var renderer = selectionRenderer ??= new() { CacheByteLimit = 64L * 1024 * 1024 };
            renderer.AnimationTime = presentedScene is null ? AnimationTime : presentedTime;
            renderer.PresentedImages = presentedImages;
            return renderer;
        }
    }
    private EditorTool tool = EditorTool.Freehand;
    public EditorTool Tool
    {
        get => tool;
        set
        {
            if (value == EditorTool.Redact) value = EditorTool.Blur;
            if (value == EditorTool.Crop && !CanCropSelection) value = EditorTool.Select;
            if (value == EditorTool.Triangle)
            {
                var parameters = Parameters; parameters.Tool = value; ApplyParameters(parameters); return;
            }
            if (temporaryMove || tool != value) { Cancel(); tool = value; Changed?.Invoke(); }
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
    private int numberStart = 1, numberSequenceStart = 1;
    internal bool IsUpdatingNumberStart { get; private set; }
    public int NumberStart
    {
        get => numberStart;
        set => SetNumberStart(value, Session.Document.NextNumber == numberSequenceStart);
    }
    private void SetNumberStart(int value, bool followStart)
    {
        numberStart = numberSequenceStart = value;
        if (!followStart || Session.Document.NextNumber == value) return;
        Session.Document.NextNumber = value;
        IsUpdatingNumberStart = true;
        try { Session.NotifySettingsChanged(); }
        finally { IsUpdatingNumberStart = false; }
    }
    public int NumberStep { get; set; } = 1;
    public CaptureShape CaptureShape { get; set; } = CaptureShape.Rectangle;
    public CaptureSource CaptureSource { get; set; } = CaptureSource.Background;
    public double CaptureBorderWidth { get; set; }
    public uint CaptureBorderColor { get; set; } = ColorLibrary.ForegroundRed;
    public ToolParameters Parameters => new() { Tool = Tool, Style = Style.Clone(), CornerRadius = CornerRadius,
        Symbol = Symbol, ConstrainAspect = Tool != EditorTool.CaptureRegion && ConstrainAspect, EffectStrength = EffectStrength, Emoji = Emoji, DefaultWidth = DefaultWidth, DefaultHeight = DefaultHeight,
        NumberStart = NumberStart, NumberStep = NumberStep, CaptureShape = CaptureShape, CaptureSource = CaptureSource,
        CaptureBorderWidth = CaptureBorderWidth, CaptureBorderColor = CaptureBorderColor };
    public void ApplyParameters(ToolParameters parameters, bool? followNumberStart = null)
    {
        if (!parameters.IsValid()) throw new ArgumentException(nameof(parameters));
        // Old presets retain their serialized enum; only future drawing uses the unified symbol tool.
        Cancel(); tool = parameters.Tool == EditorTool.Redact ? EditorTool.Blur :
            parameters.Tool == EditorTool.Triangle ? EditorTool.Symbol :
            parameters.Tool == EditorTool.Crop && !CanCropSelection ? EditorTool.Select : parameters.Tool;
        Style = parameters.Style.Clone(); CornerRadius = parameters.CornerRadius;
        if(parameters.Tool==EditorTool.Number && parameters.NumberColorRolesVersion==0)
        { Style.Stroke=(Style.Fill>>24)==0?Style.Stroke:Style.Fill;Style.Fill=0xFFFFFFFF; }
        Symbol = parameters.Tool == EditorTool.Triangle ? SymbolKind.Triangle : parameters.Symbol;
        // Legacy capture recipes may carry an aspect lock; capture creation only follows held Shift.
        ConstrainAspect = parameters.Tool != EditorTool.CaptureRegion && parameters.ConstrainAspect; EffectStrength = parameters.EffectStrength;
        Emoji = parameters.Emoji; DefaultWidth = parameters.DefaultWidth; DefaultHeight = parameters.DefaultHeight;
        CaptureShape = parameters.CaptureShape; CaptureSource = parameters.CaptureSource;
        CaptureBorderWidth = parameters.CaptureBorderWidth; CaptureBorderColor = parameters.CaptureBorderColor;
        // Other tool snapshots carry hidden sequence fields too; they must not
        // replace the number tool's actual start or move this canvas's counter.
        if (parameters.Tool == EditorTool.Number)
            SetNumberStart(parameters.NumberStart, followNumberStart ?? Session.Document.NextNumber == numberSequenceStart);
        else numberStart = parameters.NumberStart;
        NumberStep = parameters.NumberStep;
        Changed?.Invoke();
    }
    public PointD ScreenOrigin { get; set; }
    internal TimeSpan AnimationTime { get; set; }
    public RectD? Marquee { get; private set; }
    public IReadOnlyList<PointD>? EffectPreviewPolygon { get; private set; }
    public CaptureSelection? CapturePreview { get; private set; }
    public bool HasCaptureGesture => gesture is "capture" or "capture-polygon";
    internal bool IsPointerBlocked { get; private set; }
    public event Action? Changed;
    public event Action<string>? Message;
    public event Action<CanvasElement?, PointD>? EditText;
    internal event Action<PointD>? PropertiesRequested;
    internal event Action? Cancelling;
    public event Action<CaptureSelection>? CaptureSelectionCompleted;
    public event Action<RectD, ImageEffectKind, double, uint>? DesktopEffectRequested;
    private PointD start;
    private PointD startScreen, pointerScreen;
    private CanvasElement? drawing;
    private CanvasElement? effectTarget;
    private long cropRevision;
    private string? gesture;
    private Guid[]? selectionOnClick;
    private RectD selectionBounds;
    private Dictionary<Guid, Affine2D> transforms = [];
    private CanvasElement? resizeSource;
    private CanvasElement? billboardEndpointSource;
    private SceneRenderer? resizeTextRenderer;
    private Dictionary<Guid,CanvasElement> resizeTextSources = [];
    private int endpoint;
    private RectD? imageRegion;
    private (PointD Position, OverlayModifiers Modifiers, OverlayMouseButton Button)? lastGestureMove;
    private bool deselectOnClick, gestureMoved;
    internal double ClickPlacementDragThreshold { get; set; } = 6;
    private double clickPlacementGestureThreshold = 6;
    private bool shiftDeselectOnClick;
    private Guid? shiftDeselectTarget;
    private readonly List<PointD> capturePoints = [];
    private const int MaximumCapturePoints = 65536;

    internal static bool IsCreationTool(EditorTool value) => value is EditorTool.Freehand or EditorTool.Highlighter or EditorTool.Line or
        EditorTool.Arrow or EditorTool.DoubleArrow or EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse or
        EditorTool.Triangle or EditorTool.Symbol or EditorTool.Text or EditorTool.Number or EditorTool.Emoji or EditorTool.Image or EditorTool.CaptureRegion;
    internal static bool UsesDefaultSize(EditorTool value) => value is
        EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse or EditorTool.Triangle or EditorTool.Symbol;
    internal bool ShouldShowSelection(OverlayModifiers modifiers) =>
        !CanvasContentHidden && InteractionTool is not (EditorTool.Eraser or EditorTool.Canvas) && (modifiers & OverlayModifiers.Control) == 0 && Session.Selection.Count > 0;

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
                bool active = gesture is "draw" or "endpoint" ? drawing?.Id == id || strokeFragmentIds.Contains(id) : transforms.ContainsKey(id);
                if (active) { first = Math.Min(first, i); last = i; }
            }
            return last < 0 ? (-1, -1, true) : (first, last, false);
        }
    }

    public EditorController(EditorSession session) { Session = session; session.Changed += OnChanged; }
    internal void DetachSession() { CancelPicking(); ClearPresentedScene(); Session.Changed -= OnChanged; selectionRenderer?.Dispose(); selectionRenderer = null; }
    public bool CanCropSelection => ImageToolEligibility.TryGetCropTarget(Session, out _);
    private void OnChanged(object? sender, EventArgs e)
    {
        if (!Session.Document.Canvas.Projection.Enabled) { billboardMode = false; ClearBillboardDepth(); }
        if (gesture == "image-crop" && !IsCurrentCropTarget()) Cancel();
        if (tool == EditorTool.Crop && !CanCropSelection) tool = EditorTool.Select;
        Changed?.Invoke();
    }
    public void ReplaceSession(EditorSession session, PointD origin)
    {
        Cancel();
        ClearPresentedScene();
        Session.Changed -= OnChanged;
        Session = session;
        if (!session.Document.Canvas.Projection.Enabled) billboardMode = false;
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
        => CancelGesture(rollback: true, notify: true);

    private void CancelGesture(bool rollback, bool notify)
    {
        CancelPicking();
        temporaryMove = false;
        if (notify) Cancelling?.Invoke();
        resizeTextRenderer?.Dispose(); resizeTextRenderer = null;
        resizeTextSources.Clear();
        gesture = null; drawing = effectTarget = resizeSource = billboardEndpointSource = null; Marquee = imageRegion = null; EffectPreviewPolygon = null;
        lastGestureMove = null;
        CapturePreview = null; capturePoints.Clear();
        straightStrokeStart=null;gestureModifiers=OverlayModifiers.None;modifierTime=null;
        deselectOnClick = gestureMoved = false;
        shiftDeselectOnClick = false;
        shiftDeselectTarget = null;
        selectionOnClick = null;
        ClearBillboardDepth(); pendingBillboardInsertion = null; billboardSources.Clear(); billboardMoveDelta = null;
        IsPointerBlocked = false; strokeInterrupted = false; strokeLocalBounds = null; strokeFragmentIds.Clear();
        if (rollback) Session.Cancel();
        Changed?.Invoke();
    }
    public void Pointer(OverlayPointerEvent e) => PointerForCanvas(e, e.Kind == OverlayPointerKind.DoubleClick);

    internal void PointerForCanvas(OverlayPointerEvent e, bool propertiesDoubleClick)
    {
        if (presentedPickingSuspended)
        {
            if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick) Message?.Invoke(L.Get("Rendering.PresentationFailed"));
            if (e.Kind == OverlayPointerKind.Cancel) SuspendPresentedPicking();
            return;
        }
        if (BackgroundPickingEnabled && !_replayingPointer && QueueBackgroundPointer(e, propertiesDoubleClick)) return;
        if (!HandleTemporaryMovePointer(e) && Tool != EditorTool.Canvas) PointerCore(e, propertiesDoubleClick);
        // A selection notification can synchronously suspend presentation before
        // StartSelect has begun its transaction. Do not leave that later start alive.
        if (presentedPickingSuspended) SuspendPresentedPicking();
    }

    private void PointerCore(OverlayPointerEvent e, bool propertiesDoubleClick)
    {
        if (e.Kind == OverlayPointerKind.Cancel) { Cancel(); return; }
        if(e.Kind==OverlayPointerKind.Move&&gesture is not null&&e.Time is { } inputTime&&modifierTime is { } boundary&&unchecked((int)(inputTime-boundary))<0)return;
        pointerScreen = new(e.Position.X - ScreenOrigin.X, e.Position.Y - ScreenOrigin.Y);
        if (e.Button == OverlayMouseButton.Right && e.Kind == OverlayPointerKind.Down) { Cancel(); Tool = EditorTool.Select; return; }
        if (HandleBillboardDepthInput(e)) return;
        bool screenGesture = HasCaptureGesture || Tool == EditorTool.CaptureRegion && gesture is null;
        PointD p;
        if (screenGesture) p = pointerScreen;
        else if (UsesBillboardDrawingInput || gesture is null && billboardDepth is not null) p = BillboardInput(pointerScreen);
        else if (!CanvasMapping.Create(Session.Document).TryToPlane(pointerScreen, out p) && !TryBillboardSelectionInput(out p))
        {
            if (IsPlanarStrokeDrawing)
            {
                strokeInterrupted = true;
                if (e.Kind == OverlayPointerKind.Up) FinishSafeStroke();
            }
            else if (e.Kind == OverlayPointerKind.Up) Cancel();
            RejectPointer(e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick or OverlayPointerKind.Up);
            return;
        }
        if (gesture is null || e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick) SetPointerBlocked(false);
        if (gesture == "image-crop" && !IsCurrentCropTarget()) { Cancel(); return; }
        if (e.Button == OverlayMouseButton.Right && e.Kind == OverlayPointerKind.Down) { Cancel(); Tool = EditorTool.Select; return; }
        if (gesture == "capture-polygon" && e.Button == OverlayMouseButton.Left &&
            e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick)
        {
            if (e.Kind == OverlayPointerKind.DoubleClick ||
                capturePoints.Count >= 3 && p.DistanceTo(capturePoints[0]) <= 8) CompleteCaptureSelection();
            else
            {
                AddCapturePoint((e.Modifiers&OverlayModifiers.Shift)!=0&&capturePoints.Count>0?EightDirectionEnd(capturePoints[^1],p):p);
                UpdateCapturePreview(p, e.Modifiers, includeHover: false);
            }
            return;
        }
        if (propertiesDoubleClick && e.Button == OverlayMouseButton.Left && Tool != EditorTool.Text)
        {
            Cancel();
            var hit = HitTestScreen(pointerScreen, 4);
            if (hit is not null)
            {
                Session.SetSelection([hit.Id]);PropertiesRequested?.Invoke(pointerScreen);return;
            }
        }
        if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && e.Button == OverlayMouseButton.Left)
        {
            var retainedDepth = billboardDepth;
            Cancel(); billboardDepth = retainedDepth;
            start = p; startScreen = pointerScreen; lastGestureMove = (p, e.Modifiers, e.Button);
            clickPlacementGestureThreshold = double.IsFinite(ClickPlacementDragThreshold) && ClickPlacementDragThreshold > 0
                ? ClickPlacementDragThreshold : 6;
            gestureModifiers=e.Modifiers;
            bool creating = IsCreationTool(Tool);
            bool forceCreate = (e.Modifiers & OverlayModifiers.Control) != 0;
            if (Tool == EditorTool.Text && e.Kind == OverlayPointerKind.DoubleClick && !forceCreate &&
                HitTestScreen(pointerScreen, 4) is { Kind: ElementKind.Text } existingText)
            { EditText?.Invoke(existingText, p); return; }
            deselectOnClick = creating && !forceCreate && Session.Selection.Count > 0;
            var selectionPoint = p;
            bool canSelect = !screenGesture || CanvasMapping.Create(Session.Document).TryToPlane(pointerScreen, out selectionPoint) ||
                TryBillboardSelectionInput(out selectionPoint);
            if (ShouldShowSelection(e.Modifiers) && canSelect && IsSelectionFrameHit(pointerScreen, includeInterior: true))
            {
                start = selectionPoint;
                StartSelect(selectionPoint, e.Modifiers, selectedOnly: Tool != EditorTool.Select);
                deselectOnClick = false; return;
            }
            if(Tool==EditorTool.Text)
            {
                var text=forceCreate?null:HitTestScreen(pointerScreen,4);
                EditText?.Invoke(text?.Kind==ElementKind.Text?text:null,p);
                return;
            }
            if (deselectOnClick && Tool == EditorTool.Image)
            { Session.SetSelection([]); return; }
            if (Tool is EditorTool.Number or EditorTool.Emoji)
            {
                BeginSizedPlacement(p); BeginBillboardDrawing();
                if (!IsDrawingSafe()) { Cancel(); RejectPointer(true); return; }
                Session.NotifyChanged(); return;
            }
            if (Tool == EditorTool.Image) { Message?.Invoke(L.Get("Editor.ImportImageHint")); return; }
            if (Tool == EditorTool.Stamp) return;
            if (Tool == EditorTool.Crop)
            {
                if (!ImageToolEligibility.TryGetCropTarget(Session, out effectTarget))
                { Message?.Invoke(L.Get("Editor.CropImageHint")); return; }
                cropRevision = Session.SceneRevision;
                gesture = "image-crop"; UpdateImageRegion(p,e.Modifiers); return;
            }
            if (Tool == EditorTool.CaptureRegion)
            {
                gesture = CaptureShape == CaptureShape.PolygonLasso ? "capture-polygon" : "capture";
                if (CaptureShape is CaptureShape.FreehandLasso or CaptureShape.PolygonLasso) capturePoints.Add(p);
                if(CaptureShape==CaptureShape.FreehandLasso&&(e.Modifiers&OverlayModifiers.Shift)!=0)straightStrokeStart=0;
                UpdateCapturePreview(p, e.Modifiers, includeHover: false); return;
            }
            if (Tool is EditorTool.Blur or EditorTool.Pixelate)
            {
                if (Tool is EditorTool.Blur or EditorTool.Pixelate && Session.Document.Kind == DocumentKind.LiveOverlay)
                { gesture = "desktop-effect"; Marquee = new(p.X,p.Y,0,0); return; }
                if (!TryGetSelectedImageTarget(out effectTarget, canvasFallback: true)) return;
                effectTarget ??= Session.Document.Background;
                if (effectTarget == null)
                { gesture = "desktop-effect"; Marquee = new(p.X, p.Y, 0, 0); return; }
                if (!CanEditImage(effectTarget)) return;
                gesture = "effect"; UpdateImageRegion(p,e.Modifiers); return;
            }
            if (Tool == EditorTool.Eraser) { gesture = "erase"; BeginPointerTransaction(L.Get("Editor.History.Erase")); Erase(p); return; }
            if (Tool == EditorTool.Select) { StartSelect(p, e.Modifiers); return; }
            gesture = "draw";
            BeginPointerTransaction(L.Format("Editor.History.Draw", L.Get("Editor.ElementKind." + Tool)));
            drawing = new CanvasElement { Kind = Enum.Parse<ElementKind>(Tool.ToString()), Style = Style.Clone(), CornerRadius = CornerRadius,
                Symbol = Symbol, Transform = Affine2D.Translation(p.X, p.Y), Points = [new(0, 0)] };
            if(ElementGeometry.IsBoxShape(drawing.Kind))drawing.ConstrainAspect=ConstrainAspect;
            if (Tool == EditorTool.Highlighter) drawing.Style.StrokeWidth = Math.Max(16, Style.StrokeWidth);
            if(drawing.Kind is ElementKind.Freehand or ElementKind.Highlighter&&(e.Modifiers&OverlayModifiers.Shift)!=0)straightStrokeStart=0;
            if (UsesDefaultSize(Tool)) ApplyDefaultSize(drawing, start, e.Modifiers);
            BeginBillboardDrawing();
            if (!IsDrawingSafe()) { Cancel(); RejectPointer(true); return; }
            Session.Document.Elements.Add(drawing);
            Session.Selection.Clear();
            if (Tool is not (EditorTool.Freehand or EditorTool.Highlighter) && !ElementGeometry.IsLine(drawing.Kind))
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
            TrackGestureMovement();
            MoveSafely(p, e.Modifiers);
        }
        else if (e.Kind == OverlayPointerKind.Up && gesture != null && e.Button == OverlayMouseButton.Left)
        {
            if (gesture == "capture-polygon") return;
            if (gesture == "capture")
            {
                UpdateCapturePreview(p, e.Modifiers, includeHover: false);
                CompleteCaptureSelection(); return;
            }
            TrackGestureMovement();
            MoveSafely(p, e.Modifiers);
            if (IsPointerBlocked && !IsPlanarStrokeDrawing)
            {
                Cancel();
                SetPointerBlocked(!CanvasMapping.Create(Session.Document).IsScreenSafe(pointerScreen));
                Message?.Invoke(L.Get("Perspective.InputOutsidePlane")); return;
            }
            if (billboardDrawing && !billboardDraftValid)
            { var depth = billboardDepth; Cancel(); billboardDepth = depth; Changed?.Invoke(); return; }
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
                        Session.Document.NextNumber = NumberSequence.Advance(Session.Document.NextNumber, drawing.Number, NumberStep);
                    Session.Commit();
                }
                else if (completed == "draw" && !gestureMoved && drawing is not null && ElementGeometry.IsLine(drawing.Kind))
                { Session.Cancel(); if (deselectOnClick) Session.SetSelection([]); }
                else if (completed == "draw" && !gestureMoved && drawing is not null && UsesDefaultSize(Tool))
                { PrepareBillboardDrawing(); ApplyDefaultSize(drawing, start, e.Modifiers); if (RefreshBillboardDrawing() && IsDrawingSafe()) Session.Commit(); else { Session.Cancel(); RejectPointer(true); } }
                else if (completed == "draw" && deselectOnClick && !gestureMoved)
                { Session.Cancel(); Session.SetSelection([]); }
                else if (completed == "move" && shiftDeselectOnClick && !gestureMoved)
                { var ids = shiftDeselectTarget is { } id ? Session.SelectionUnitIds(id).ToArray() : []; Session.Cancel(); Session.SetSelection(Session.Selection.Except(ids)); }
                else if (completed == "move" && !gestureMoved && selectionOnClick is { } clicked)
                { Session.Commit(); Session.SetSelection(clicked); }
                else Session.Commit();
            }
            resizeTextRenderer?.Dispose(); resizeTextRenderer = null;
            resizeTextSources.Clear();
            straightStrokeStart=null;gestureModifiers=OverlayModifiers.None;modifierTime=null;
            drawing = effectTarget = null; Marquee = imageRegion = null; EffectPreviewPolygon = null; Changed?.Invoke();
            strokeInterrupted = false; strokeLocalBounds = null; strokeFragmentIds.Clear();
            ClearBillboardDepth(); billboardSources.Clear();
            selectionOnClick = null;
            shiftDeselectTarget = null;
        }
    }

    internal bool IsSelectionFrameHit(PointD screen, bool includeInterior = false)
    {
        if (CanvasContentHidden) return false;
        if (!Session.SelectedElements().Any(element => element.Visible && !element.Locked)) return false;
        if (SelectionScreenRotationHandle is { } rotation && screen.DistanceTo(rotation) < 14 ||
            BillboardSelectionGuide is { } guide && screen.DistanceTo(guide.Center) < 12 ||
            SelectionScreenEndpoints.Any(endpoint => screen.DistanceTo(endpoint) < 12)) return true;
        var corners = SelectionScreenCorners;
        return includeInterior && corners.Count == 4 && ElementGeometry.PointInPolygon(screen, corners) ||
            corners.Any(corner => corner.DistanceTo(screen) < 12) ||
            corners.Count == 4 && Enumerable.Range(0, 4).Any(index =>
                ElementGeometry.DistanceToSegment(screen, corners[index], corners[(index + 1) % 4]) <= 6);
    }

    private void ApplyDefaultSize(CanvasElement element, PointD center, OverlayModifiers modifiers)
    {
        double width = DefaultWidth, height = DefaultHeight;
        // Locked dimensions already describe the intended ratio. Shift on an
        // unlocked tool retains the quick square/circle insertion shortcut.
        if (!ConstrainAspect && (modifiers & OverlayModifiers.Shift) != 0) height = width;
        element.Width = width; element.Height = height; element.Points.Clear();
        element.Transform = Affine2D.Translation(center.X - width / 2, center.Y - height / 2);
    }

    private void TrackGestureMovement() => gestureMoved |= pointerScreen.DistanceTo(startScreen) >=
        (gesture == "draw" && (UsesDefaultSize(Tool) || Tool is EditorTool.Number or EditorTool.Emoji)
            ? clickPlacementGestureThreshold : 2);

    private void BeginSizedPlacement(PointD center)
    {
        var style = Style.Clone();
        if (Tool == EditorTool.Number)
        {
            double diameter = style.FontSize;
            drawing = new() { Kind = ElementKind.Number, Width = diameter, Height = diameter,
                Style = style, Number = Session.Document.NextNumber, NumberUsesForeground = true, ConstrainAspect = true };
        }
        else
        {
            style.FontFamily = EmojiGlyphs.FontFamily;
            drawing = new() { Kind = ElementKind.Text, Text = Emoji, Width = style.FontSize, Height = style.FontSize, Style = style, ConstrainAspect = true };
        }
        drawing.Transform = Affine2D.Translation(center.X - drawing.Width / 2, center.Y - drawing.Height / 2);
        BeginPointerTransaction(L.Get("History.AddElement"));
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

    private void StartSelect(PointD p, OverlayModifiers modifiers, bool selectedOnly = false)
    {
        if (TryStartBillboardHeight()) return;
        var bounds = Session.SelectionBounds();
        var selected = Session.SelectedElements().Where(x => !x.Locked).ToArray();
        if (bounds != null && selected.Length > 0)
        {
            selectionBounds = bounds.Value;
            if (selected.Length == 1 && selected[0].Kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow && selected[0].Points.Count > 1)
            {
                var element = selected[0];
                foreach (var i in new[] { 0, element.Points.Count - 1 })
                    if (CanvasMapping.Create(Session.Document).ElementToScreen(element).TransformPoint(element.Points[i]).DistanceTo(pointerScreen) < 12)
                    { drawing = element; billboardEndpointSource = element.IsBillboard ? element.Clone() : null; endpoint = i; gesture = "endpoint"; BeginPointerTransaction(L.Get("Editor.History.AdjustEndpoint")); return; }
            }
            if (SelectionScreenRotationHandle is { } rotation && pointerScreen.DistanceTo(rotation) < 14) gesture = "rotate";
            else if (SelectionScreenCorners.Any(c => pointerScreen.DistanceTo(c) < 12)) gesture = "resize";
            else if ((modifiers & OverlayModifiers.Alt) == 0 && SelectionScreenCorners is { Count: 4 } screenCorners &&
                ElementGeometry.PointInPolygon(pointerScreen, screenCorners)) gesture = "move";
        }
        if (gesture == null && selectedOnly && bounds is not null)
        { selectionBounds = bounds.Value; gesture = "move"; }
        if (gesture == "move" && !temporaryMove && !selectedOnly && modifiers is OverlayModifiers.None or OverlayModifiers.Shift &&
            HitTestScreen(pointerScreen, 7) is { } foreground)
        {
            var unit = Session.SelectionUnitIds(foreground.Id).ToArray();
            if (modifiers == OverlayModifiers.Shift)
            {
                if (unit.All(Session.Selection.Contains)) { shiftDeselectOnClick = true; shiftDeselectTarget = foreground.Id; }
                else selectionOnClick = Session.Selection.Union(unit).ToArray();
            }
            else if (!unit.All(Session.Selection.Contains)) selectionOnClick = unit;
        }
        if (gesture == null)
        {
            var hit = HitTestScreen(pointerScreen, 7);
            if (hit == null) { gesture = "marquee"; Marquee = new(pointerScreen.X, pointerScreen.Y, 0, 0); return; }
            bool individual = (modifiers & OverlayModifiers.Alt) != 0;
            if ((modifiers & OverlayModifiers.Shift) != 0)
            {
                var ids = Session.SelectionUnitIds(hit.Id, individual).ToArray();
                if (ids.All(Session.Selection.Contains)) { shiftDeselectOnClick = true; shiftDeselectTarget = hit.Id; }
                else { Session.SetSelection(Session.Selection.Union(ids)); return; }
            }
            var unit = Session.SelectionUnitIds(hit.Id, individual);
            if (individual || !unit.All(Session.Selection.Contains)) Session.SetSelection(unit);
            selectionBounds = Session.SelectionBounds()!.Value;
            gesture = "move";
        }
        transforms = Session.SelectedElements().Where(x => !x.Locked).ToDictionary(x => x.Id, x => x.Transform);
        billboardSources = Session.SelectedElements().Where(x => !x.Locked && x.IsBillboard).ToDictionary(x => x.Id, x => x.Clone());
        resizeSource = gesture == "resize" && selected.Length == 1 && (ElementGeometry.IsBoxShape(selected[0].Kind) || selected[0].Kind == ElementKind.Text) ? selected[0].Clone() : null;
        resizeTextSources=gesture=="resize"?selected.Where(element=>element.Kind==ElementKind.Text).ToDictionary(element=>element.Id,element=>element.Clone()):[];
        if (resizeTextSources.Count>0) resizeTextRenderer = new();
        BeginPointerTransaction(L.Get("Editor.History.Transform"));
    }

    private void Move(PointD p, OverlayModifiers modifiers)
    {
        gestureModifiers=modifiers;
        // An overlapping foreground layer wins a click, while dragging from the same
        // selected-frame body moves the current selection. Ignore click-sized jitter
        // until the existing physical movement threshold establishes that intent.
        if (gesture == "move" && (selectionOnClick is not null || shiftDeselectOnClick) && !gestureMoved) return;
        if (MoveBillboardSelection(p, modifiers)) return;
        if (HasCaptureGesture) { UpdateCapturePreview(p, modifiers); return; }
        if (gesture is "effect" or "image-crop") { UpdateImageRegion(p,modifiers); Changed?.Invoke(); return; }
        if (gesture == "marquee") { Marquee = RectD.FromPoints(startScreen, pointerScreen); Changed?.Invoke(); return; }
        if (gesture == "desktop-effect") { Marquee = RectD.FromPoints(start,ConstrainBox(modifiers)?SquareEnd(start,p):p); Changed?.Invoke(); return; }
        if (gesture == "erase") { Erase(p); return; }
        if (gesture == "endpoint" && drawing != null)
        {
            if (drawing.IsBillboard)
            {
                var source = billboardEndpointSource ?? drawing;
                var canvasMapping = CanvasMapping.Create(Session.Document);
                var screenMap = canvasMapping.ElementToScreen(source);
                if (!screenMap.TryInverse(out var toLocal)) return;
                var end = pointerScreen;
                if ((modifiers & OverlayModifiers.Shift) != 0)
                    end = EightDirectionEnd(screenMap.TransformPoint(source.Points[endpoint == 0 ? source.Points.Count - 1 : 0]), end);
                drawing.Points = [..source.Points]; drawing.Points[endpoint] = toLocal.TransformPoint(end);
                if (canvasMapping.TryGetEqualDepthLine(canvasMapping.ToScreen(source.BillboardAnchor), out var depth))
                    BillboardGeometry.TryPlaceFromScreen(drawing, canvasMapping, depth, screenMap);
                Session.NotifyChanged(); return;
            }
            var mapping=ElementGeometry.LocalToPlane(drawing);
            if(!mapping.TryInverse(out var inverse))return;
            if((modifiers&OverlayModifiers.Shift)!=0)
                p=EightDirectionEnd(mapping.TransformPoint(drawing.Points[endpoint==0?drawing.Points.Count-1:0]),p);
            drawing.Points[endpoint] = inverse.TransformPoint(p); Session.NotifyChanged(); return;
        }
        if (gesture == "draw" && drawing != null)
        {
            PrepareBillboardDrawing();
            if (UsesDefaultSize(Tool) && !gestureMoved)
            {
                ApplyDefaultSize(drawing, start, modifiers);
                RefreshBillboardDrawing(); Session.NotifyChanged(); return;
            }
            if (Tool is EditorTool.Number or EditorTool.Emoji)
            {
                ResizePlacement(p); RefreshBillboardDrawing(); Session.NotifyChanged(); return;
            }
            var local = p - start;
            if (drawing.Kind is ElementKind.Freehand or ElementKind.Highlighter)
            {
                UpdateStroke(drawing.Points,local,modifiers);
            }
            else if (drawing.Kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow)
                drawing.Points = [new(0, 0), (modifiers&OverlayModifiers.Shift)!=0?EightDirectionEnd(start,p)-start:local];
            else
            {
                if (ConstrainBox(modifiers)) p = ConstrainAspect && UsesDefaultSize(Tool) ? DefaultRatioEnd(start, p) : SquareEnd(start, p);
                var rect = RectD.FromPoints(start, p);
                drawing.Width = Math.Max(1, rect.Width); drawing.Height = Math.Max(1, rect.Height);
                drawing.Transform = Affine2D.Translation(rect.X, rect.Y); drawing.Points.Clear();
            }
            RefreshBillboardDrawing(); Session.NotifyChanged(); return;
        }
        Affine2D delta;
        if (gesture == "move")
        { var move = billboardMoveDelta ?? ConstrainMove(p - start, modifiers); delta = Affine2D.Translation(move.X, move.Y); }
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
            if (single.Length == 1 && resizeSource is { } source)
            {
                var element = single[0]; var original = transforms[element.Id];
                if (!ElementGeometry.LocalToPlane(source).TryInverse(out var inverse)) return;
                var localCenter = ElementGeometry.LocalBounds(source).Center;
                var fromLocal = inverse.TransformPoint(start) - localCenter;
                var toLocal = inverse.TransformPoint(p) - localCenter;
                double scaleX = Math.Abs(fromLocal.X) < .001 ? 1 : Math.Max(.02, toLocal.X / fromLocal.X);
                double scaleY = Math.Abs(fromLocal.Y) < .001 ? 1 : Math.Max(.02, toLocal.Y / fromLocal.Y);
                if (ElementGeometry.PreservesAspect(source) ^ ((modifiers & OverlayModifiers.Shift) != 0)) scaleX = scaleY = UniformScale(fromLocal, toLocal);
                if (source.Kind == ElementKind.Text)
                {
                    TextFrameLayout.Resize(element, source, scaleX, scaleY, resizeTextRenderer!);
                    Session.NotifyChanged(); return;
                }
                element.Width = source.Width; element.Height = source.Height; element.CornerRadius = source.CornerRadius; element.Transform = original;
                ElementGeometry.ResizeShapeGeometry(element, scaleX, scaleY);
                Session.NotifyChanged(); return;
            }
            var center = selectionBounds.Center;
            var from = start - center; var to = p - center;
            var sx = Math.Abs(from.X) < 1 ? 1 : Math.Max(.02, to.X / from.X);
            var sy = Math.Abs(from.Y) < 1 ? 1 : Math.Max(.02, to.Y / from.Y);
            bool keepAspect = single.All(ElementGeometry.PreservesAspect);
            if (keepAspect ^ ((modifiers & OverlayModifiers.Shift) != 0)) sx = sy = UniformScale(from, to);
            delta = Affine2D.Around(center, Affine2D.Scale(sx, sy));
        }
        else return;
        foreach (var element in Session.SelectedElements())
            if (transforms.TryGetValue(element.Id, out var original))
            {
                if (element.IsBillboard) continue;
                if(gesture=="resize"&&resizeTextSources.TryGetValue(element.Id,out var textSource))
                    TextFrameLayout.Transform(element,textSource,delta,resizeTextRenderer!);
                else element.Transform = delta.Compose(original);
            }
        Session.NotifyChanged();
    }

    private void Erase(PointD p)
    {
        if (CanvasDepthOrder.IsEnabled(PickingDocument))
        {
            if (HitTestScreen(pointerScreen, 10) is { } visible) Session.Document.Elements.Remove(visible);
        }
        else Session.Document.Elements.RemoveAll(e => !e.Locked && ElementHitTestScreen(e, pointerScreen, 10));
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
    private void UpdateImageRegion(PointD p,OverlayModifiers modifiers)
    {
        if (effectTarget == null) return;
        var targetMapping = effectTarget.IsBillboard ? CanvasMapping.Create(Session.Document).ElementToScreen(effectTarget) : ElementGeometry.LocalToPlane(effectTarget);
        if (!targetMapping.TryInverse(out var inverse)) return;
        var anchor=inverse.TransformPoint(effectTarget.IsBillboard ? startScreen : start);
        var end=inverse.TransformPoint(effectTarget.IsBillboard ? pointerScreen : p);
        if(ConstrainBox(modifiers))
        {
            anchor=new(Math.Clamp(anchor.X,0,effectTarget.Width),Math.Clamp(anchor.Y,0,effectTarget.Height));
            end=SquareEnd(anchor,end);var delta=end-anchor;
            double side=Math.Min(Math.Abs(delta.X),Math.Min(delta.X<0?anchor.X:effectTarget.Width-anchor.X,delta.Y<0?anchor.Y:effectTarget.Height-anchor.Y));
            end=anchor+new PointD(Math.CopySign(side,delta.X),Math.CopySign(side,delta.Y));
        }
        imageRegion = RectD.FromPoints(anchor,end).Intersect(new(0, 0, effectTarget.Width, effectTarget.Height));
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
