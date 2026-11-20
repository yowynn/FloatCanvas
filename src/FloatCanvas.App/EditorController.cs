using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal enum EditorTool
{
    Select = 0, Freehand = 1, Highlighter = 2, Line = 3, Arrow = 4, DoubleArrow = 5, Rectangle = 6, RoundedRectangle = 7, Ellipse = 8, Triangle = 9, Text = 10, Number = 11, Image = 12, Eraser = 13, Crop = 14, Blur = 15, Pixelate = 16,
    CaptureRegion = 18, Symbol = 19, Emoji = 20, Stamp = 21, Canvas = 22, Presentation = 23, PointerPresentation = 24

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
            renderer.WindowFrames = presentedWindowFrames;
            renderer.WindowOwnerContext = presentedWindowOwnerContext;
            return renderer;
        }
    }
    private EditorTool tool = EditorTool.Freehand;
    internal bool SmoothFreehand { get; set; }
    private FreehandPathSmoother? freehandCorrection;
    internal long PlacementContextVersion { get; private set; }
    public EditorTool Tool
    {
        get => tool;
        set
        {
            if (!Enum.IsDefined(value)) return;
            if (value == EditorTool.Crop && !CanCropSelection) value = EditorTool.Select;
            if (value == EditorTool.Triangle)
            {
                var parameters = Parameters; parameters.Tool = value;
                if (!ElementAppearance.IsValidVariant(ElementKind.Triangle, parameters.Style.Variant)) parameters.Style.Variant = null;
                ApplyParameters(parameters); return;
            }
            if (temporaryMove || tool != value)
            {
                PlacementContextVersion++;
                Cancel();
                if (!Enum.TryParse<ElementKind>(value.ToString(), out var kind) || !ElementAppearance.IsValidVariant(kind, Style.Variant)) Style.Variant = null;
                tool = value; Changed?.Invoke();
            }
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
    public NumberTextSizeMode NumberTextSizeMode { get; set; } = NumberTextSizeMode.FitBadge;
    public CaptureShape CaptureShape { get; set; } = CaptureShape.Rectangle;
    public CaptureSource CaptureSource { get; set; } = CaptureSource.Background;
    public double CaptureBorderWidth { get; set; }
    public PresentationSettings? Presentation { get; set; }
    public PresentationPointerSettings? PointerPresentation { get; set; }
    public ToolParameters Parameters
    {
        get
        {
            var parameters = new ToolParameters { Tool = Tool, Style = Style.Clone(), CornerRadius = CornerRadius,
        Symbol = Symbol, ConstrainAspect = Tool != EditorTool.CaptureRegion && ConstrainAspect, EffectStrength = EffectStrength, Emoji = Emoji, DefaultWidth = DefaultWidth, DefaultHeight = DefaultHeight,
        NumberStart = NumberStart, NumberStep = NumberStep, NumberTextSizeMode = NumberTextSizeMode, CaptureShape = CaptureShape, CaptureSource = CaptureSource,
        CaptureBorderWidth = CaptureBorderWidth, Presentation = Presentation?.Clone(),
        PointerPresentation = PointerPresentation?.Clone() };
            parameters.NormalizeForCreation(); return parameters;
        }
    }
    public void ApplyParameters(ToolParameters parameters, bool? followNumberStart = null)
    {
        parameters = parameters.Clone(); parameters.NormalizeForCreation();
        if (!parameters.IsValid()) throw new ArgumentException(nameof(parameters));
        // Old presets retain their serialized enum; only future drawing uses the unified symbol tool.
        var nextTool = parameters.Tool == EditorTool.Triangle ? EditorTool.Symbol :
            parameters.Tool == EditorTool.Crop && !CanCropSelection ? EditorTool.Select : parameters.Tool;
        if (tool != nextTool) PlacementContextVersion++;
        Cancel(); tool = nextTool;
        Style = parameters.Style.Clone(); CornerRadius = parameters.CornerRadius;
        NumberTextSizeMode = parameters.NumberTextSizeMode;
        Presentation = parameters.Presentation?.Clone();
        PointerPresentation = parameters.PointerPresentation?.Clone();
        Symbol = parameters.Tool == EditorTool.Triangle ? SymbolKind.Triangle : parameters.Symbol;
        // Legacy capture recipes may carry an aspect lock; capture creation only follows held Shift.
        ConstrainAspect = parameters.Tool != EditorTool.CaptureRegion && parameters.ConstrainAspect; EffectStrength = parameters.EffectStrength;
        Emoji = parameters.Emoji; DefaultWidth = parameters.DefaultWidth; DefaultHeight = parameters.DefaultHeight;
        CaptureShape = parameters.CaptureShape; CaptureSource = parameters.CaptureSource;
        CaptureBorderWidth = parameters.CaptureBorderWidth;
        // Other tool snapshots carry hidden sequence fields too; they must not
        // replace the number tool's actual start or move this canvas's counter.
        if (parameters.Tool == EditorTool.Number)
            SetNumberStart(parameters.NumberStart, followNumberStart ?? Session.Document.NextNumber == numberSequenceStart);
        else numberStart = parameters.NumberStart;
        NumberStep = parameters.NumberStep;
        Changed?.Invoke();
    }
    private PointD screenOrigin;
    public PointD ScreenOrigin
    {
        get => screenOrigin;
        set { if (screenOrigin == value) return; screenOrigin = value; PlacementContextVersion++; }
    }
    private CanvasMapping? viewMapping;
    private PointD? viewOrigin;
    private object? viewEpoch;
    private LayerPlacement? viewPlacement;
    private ContainerPlacementContext? viewContainer;
    private SceneDocument? viewDocument;
    private RootCanvasKind viewRootCanvas;
    private bool independentCanvasView;
    private CanvasViewTransform canvasView = CanvasViewTransform.Identity;
    internal bool HasPointerGesture => gesture is not null || temporaryMove || _pointerPickingRunning;
    internal bool IsMovingCanvasSelection => gesture == "move" && OwnsPointerTransaction;
    private RootCanvasKind PickingRootCanvas => viewPlacement is not null ? RootCanvasKind.Main : Session.ActiveRootCanvas;
    internal PointD CanvasOrigin => viewOrigin ?? ScreenOrigin;
    internal RectD CanvasViewport => (viewDocument ?? Session.Document).Viewport;
    internal double CanvasDisplayOpacity => (viewDocument ?? Session.Document).LayerOpacity;
    internal CanvasMapping CurrentCanvasMapping => viewMapping ?? CanvasMapping.Create(Session.Document, Session.ActiveRootCanvas);
    internal bool HasSharedCardContext => !independentCanvasView && viewContainer?.Surface is not null;
    internal void SetCanvasView(CanvasMapping? mapping, PointD? origin, object? epoch, LayerPlacement? placement = null,
        SceneDocument? displayDocument = null, RootCanvasKind displayRootCanvas = RootCanvasKind.Main, bool independent = false,
        CanvasViewTransform? viewing = null, bool preserveMoveGesture = false, ContainerPlacementContext? container = null)
    {
        bool changed = !ReferenceEquals(viewEpoch, epoch) || canvasView != (viewing ?? CanvasViewTransform.Identity);
        viewMapping = mapping; viewOrigin = origin; viewEpoch = epoch; viewPlacement = placement; viewDocument = displayDocument; viewRootCanvas = displayRootCanvas;
        independentCanvasView = independent; viewContainer = container;
        canvasView = viewing ?? CanvasViewTransform.Identity;
        if (!changed) return;
        PlacementContextVersion++;
        ClearPresentedScene();
        if (!preserveMoveGesture || !IsMovingCanvasSelection) SuspendPresentedPicking();
    }
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
    internal event Action<Guid>? PrefabEditRequested;
    internal event Action<Guid>? WindowSourceCropRequested;
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
    private PointD arcHandleGrabOffset;
    private (double Offset, double Bulge) arcHandleStart;
    private SceneRenderer? resizeTextRenderer;
    private Dictionary<Guid,CanvasElement> resizeTextSources = [];
    private int endpoint;
    private RectD? imageRegion;
    private (PointD Position, OverlayModifiers Modifiers, OverlayMouseButton Button)? lastGestureMove;
    private bool deselectOnClick, gestureMoved;
    private bool duplicateOnDrag, duplicatedOnDrag, centeredGesture, deepSelectionGesture;
    internal double ClickPlacementDragThreshold { get; set; } = 6;
    private double clickPlacementGestureThreshold = 6;
    private bool shiftDeselectOnClick;
    private Guid? shiftDeselectTarget;
    private readonly List<PointD> capturePoints = [];
    private const int MaximumCapturePoints = 65536;

    internal static bool IsCreationTool(EditorTool value) => value is EditorTool.Freehand or EditorTool.Highlighter or EditorTool.Line or
        EditorTool.Arrow or EditorTool.DoubleArrow or EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse or
        EditorTool.Triangle or EditorTool.Symbol or EditorTool.Text or EditorTool.Number or EditorTool.Emoji or EditorTool.Image or EditorTool.CaptureRegion or EditorTool.Presentation;
    internal static bool UsesDefaultSize(EditorTool value) => value is
        EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse or EditorTool.Triangle or EditorTool.Symbol or EditorTool.Presentation;
    internal bool ShouldShowSelection(OverlayModifiers modifiers) =>
        !CanvasContentHidden && InteractionTool != EditorTool.PointerPresentation && InteractionTool is not (EditorTool.Eraser or EditorTool.Canvas) && (modifiers & OverlayModifiers.Control) == 0 && Session.Selection.Count > 0;

    /// <summary>The contiguous z-order span whose pixels can change during this pointer gesture.</summary>
    public (int First, int Last, bool BypassCache) RasterPreview
    {
        get
        {
            if (!Session.IsTransactionActive) return (-1, -1, false);
            // Flat leaf moves also capture the original grab for edge panning;
            // they still have an exact contiguous leaf span for raster reuse.
            if (nodeTransform is not null && UsesHierarchicalSelection) return (-1, -1, true);
            // Erasure changes the element list. Unknown transactions may mutate any content.
            // Both use the reference draw path rather than reuse potentially stale surfaces.
            if (gesture is not ("draw" or "endpoint" or "corner-radius" or "move" or "rotate" or "resize")) return (-1, -1, true);
            int first = int.MaxValue, last = -1;
            for (int i = 0; i < Session.Document.Elements.Count; i++)
            {
                var id = Session.Document.Elements[i].Id;
                bool active = gesture switch
                {
                    "corner-radius" => cornerRadiusDrag?.Target.Id == id,
                    "draw" or "endpoint" => drawing?.Id == id || strokeFragmentIds.Contains(id),
                    _ => transforms.ContainsKey(id)
                };
                if (active) { first = Math.Min(first, i); last = i; }
            }
            return last < 0 ? (-1, -1, true) : (first, last, false);
        }
    }

    internal event Action<SceneDocument, long>? SceneCommitted;
    private void OnContentCommitted(SceneDocument document, long scope) => SceneCommitted?.Invoke(document, scope);
    public EditorController(EditorSession session) { Session = session; session.Changed += OnChanged; session.ContentCommitted += OnContentCommitted; }
    internal void DetachSession() { CancelPicking(); ClearPresentedScene(); Session.Changed -= OnChanged; Session.ContentCommitted -= OnContentCommitted; selectionRenderer?.Dispose(); selectionRenderer = null; }
    public bool CanCropSelection => ImageToolEligibility.TryGetCropTarget(Session, out _);
    internal bool TryRedirectWindowSourceCrop()
    {
        if (!ImageToolEligibility.TryGetCropTarget(Session, out var image) || image!.WindowSource is null) return false;
        if (!Session.IsTransactionActive) WindowSourceCropRequested?.Invoke(image.Id);
        return true;
    }
    private void OnChanged(object? sender, EventArgs e)
    {
        if (!CurrentCanvasMapping.IsPerspective) billboardMode = false;
        if (!publishingScreenBillboard && (screenBillboardDrawing || screenBillboardRegion) &&
            (!CurrentCanvasMapping.IsPerspective || !IsScreenDraftContextCurrent)) { Cancel(); return; }
        if (!CurrentCanvasMapping.IsPerspective) { billboardMode = false; ClearBillboardDepth(); }
        if (gesture == "image-crop" && !IsCurrentCropTarget()) Cancel();
        if (tool == EditorTool.Crop && !CanCropSelection) tool = EditorTool.Select;
        Changed?.Invoke();
    }
    public void ReplaceSession(EditorSession session, PointD origin)
    {
        PlacementContextVersion++;
        Cancel();
        ClearPresentedScene();
        Session.Changed -= OnChanged;
        Session.ContentCommitted -= OnContentCommitted;
        Session = session;
        viewMapping = null; viewOrigin = null; viewEpoch = null; viewPlacement = null; viewContainer = null; viewDocument = null; independentCanvasView = false;
        if (!session.Document.Canvas.Projection.Enabled) billboardMode = false;
        session.Changed += OnChanged;
        session.ContentCommitted += OnContentCommitted;
        ScreenOrigin = origin;
        if (tool == EditorTool.Crop && !CanCropSelection) tool = EditorTool.Select;
        Changed?.Invoke();
    }
    public bool TryToDocument(OverlayPoint p, out PointD point) =>
        CurrentCanvasMapping.TryToPlane(new(p.X - CanvasOrigin.X, p.Y - CanvasOrigin.Y), out point);
    public PointD ToDocument(OverlayPoint p) => TryToDocument(p, out var point) ? point :
        throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
    public void Cancel()
        => CancelGesture(rollback: true, notify: true);

    private void CancelGesture(bool rollback, bool notify)
    {
        var cancelledDraft = rollback && HasScreenBillboardDraft && OwnsPointerTransaction && IsScreenDraftContextCurrent ? Session.Document : null;
        long cancelledScope = Session.EditScopeVersion;
        CancelPicking();
        temporaryMove = false; temporarySelectionHeld = false;
        if (notify && (_pointerOwnedSession is null || OwnsPointerTransaction)) Cancelling?.Invoke();
        resizeTextRenderer?.Dispose(); resizeTextRenderer = null;
        resizeTextSources.Clear();
        nodeTransform = null; nodeOriginalElements.Clear(); nodeOriginalAuthoredElements.Clear(); drawingParents.Clear();
        gesture = null; drawing = effectTarget = resizeSource = billboardEndpointSource = null; Marquee = imageRegion = null; EffectPreviewPolygon = null;
        cornerRadiusDrag = null;
        lastGestureMove = null;
        CapturePreview = null; capturePoints.Clear();
        straightStrokeStart=null;gestureModifiers=OverlayModifiers.None;modifierTime=null; freehandCorrection = null;
        deselectOnClick = gestureMoved = duplicateOnDrag = duplicatedOnDrag = centeredGesture = false;
        shiftDeselectOnClick = false;
        shiftDeselectTarget = null;
        selectionOnClick = null;
        ClearBillboardDepth(); pendingBillboardInsertion = null; billboardSources.Clear(); billboardMoveDelta = null;
        pendingBillboardPreview = null; pendingBillboardCandidate = null; pendingBillboardPressed = false;
        IsPointerBlocked = false; strokeInterrupted = false; strokeLocalBounds = null; strokeFragmentIds.Clear();
        if (rollback && OwnsPointerTransaction) Session.Cancel();
        _pointerOwnedSession = null; _pointerOwnedTransaction = 0;
        Changed?.Invoke();
        if (cancelledDraft is not null) ScreenBillboardCancelled?.Invoke(cancelledDraft, cancelledScope);
    }
    public void Pointer(OverlayPointerEvent e) => PointerForCanvas(e, e.Kind == OverlayPointerKind.DoubleClick);

    internal void PointerForCanvas(OverlayPointerEvent e, bool propertiesDoubleClick)
    {
        if (Tool == EditorTool.PointerPresentation && !IsBorrowingSelection) { if (e.Kind == OverlayPointerKind.Cancel) Cancel(); return; }
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
        if (!IsBorrowingSelection && Tool is EditorTool.Freehand or EditorTool.Highlighter or EditorTool.Line or EditorTool.Arrow or EditorTool.DoubleArrow &&
            (e.Modifiers & OverlayModifiers.Alt) != 0)
        {
            if (gesture is null && e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick &&
                !IsSelectionFrameHit(new(e.Position.X-CanvasOrigin.X,e.Position.Y-CanvasOrigin.Y),includeInterior:true)) return;
            if (drawing is not null && e.Kind == OverlayPointerKind.Move) return;
            if (drawing is not null && e.Kind == OverlayPointerKind.Up)
                e=e with { Position=new(pointerScreen.X+CanvasOrigin.X,pointerScreen.Y+CanvasOrigin.Y), Modifiers=gestureModifiers };
        }
        // External cancellation, scope changes or a new owner can end a gesture's
        // transaction before its queued pointer edges arrive.
        if (gesture is not null && _pointerOwnedSession is not null && !OwnsPointerTransaction)
        {
            CancelGesture(rollback: false, notify: false);
            if (e.Kind is not (OverlayPointerKind.Down or OverlayPointerKind.DoubleClick)) return;
        }
        if(e.Kind==OverlayPointerKind.Move&&gesture is not null&&e.Time is { } inputTime&&modifierTime is { } boundary&&unchecked((int)(inputTime-boundary))<0)return;
        pointerScreen = new(e.Position.X - CanvasOrigin.X, e.Position.Y - CanvasOrigin.Y);
        if (!IsScreenDraftContextCurrent) { Cancel(); return; }
        if (e.Button == OverlayMouseButton.Right && e.Kind == OverlayPointerKind.Down) { Cancel(); return; }
        if (HandleBillboardDepthInput(e)) return;
        bool screenGesture = cornerRadiusDrag is not null || HasCaptureGesture || Tool == EditorTool.CaptureRegion && gesture is null || UsesScreenCreationInput(e.Modifiers);
        PointD p;
        if (screenGesture) p = pointerScreen;
        else if (UsesBillboardDrawingInput || gesture is null && billboardDepth is not null) p = BillboardInput(pointerScreen);
        else if (!CurrentCanvasMapping.TryToPlane(pointerScreen, out p) && !TryBillboardSelectionInput(out p))
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
        if (e.Button == OverlayMouseButton.Right && e.Kind == OverlayPointerKind.Down) { Cancel(); return; }
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
            if (OpenSelectionTarget(pointerScreen)) return;
        }
        if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && e.Button == OverlayMouseButton.Left)
        {
            var retainedDepth = billboardDepth;
            Cancel(); billboardDepth = retainedDepth;
            if (Session.IsTransactionActive) return;
            if (IsCreationTool(Tool) && !Session.CanInsertInEditScope) { Message?.Invoke(L.Get("Inspector.LockedSuffix")); return; }
            start = p; startScreen = pointerScreen; lastGestureMove = (p, e.Modifiers, e.Button);
            clickPlacementGestureThreshold = double.IsFinite(ClickPlacementDragThreshold) && ClickPlacementDragThreshold > 0
                ? ClickPlacementDragThreshold : 6;
            gestureModifiers=e.Modifiers;
            centeredGesture = (e.Modifiers & OverlayModifiers.Alt) != 0;
            bool creating = IsCreationTool(Tool);
            bool forceCreate = (e.Modifiers & OverlayModifiers.Control) != 0;
            if (Tool == EditorTool.Text && e.Kind == OverlayPointerKind.DoubleClick && !forceCreate &&
                HitTestScreen(pointerScreen, 4) is { Kind: ElementKind.Text } existingText)
            {
                if (IsInstanceHit(existingText.Id))
                { if (ResolveHitHostNode(existingText.Id) is { } instance) PrefabEditRequested?.Invoke(instance); }
                else EditText?.Invoke(existingText, p);
                return;
            }
            deselectOnClick = creating && !forceCreate && Session.Selection.Count > 0;
            var selectionPoint = p;
            bool canSelect = !screenGesture || CurrentCanvasMapping.TryToPlane(pointerScreen, out selectionPoint) ||
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
                if (text is not null && IsInstanceHit(text.Id))
                { if (ResolveHitSelectableNode(text.Id) is { } instance) Session.SetSelection([instance]); return; }
                EditText?.Invoke(text?.Kind==ElementKind.Text?text:null,p);
                return;
            }
            if (deselectOnClick && Tool == EditorTool.Image)
            { Session.SetSelection([]); return; }
            if (Tool is EditorTool.Number or EditorTool.Emoji)
            {
                BeginSizedPlacement(p);
                if (!IsDrawingSafe()) { Cancel(); RejectPointer(true); return; }
                NotifyGeometryChanged(); return;
            }
            if (Tool == EditorTool.Image) { Message?.Invoke(L.Get("Editor.ImportImageHint")); return; }
            if (Tool == EditorTool.Stamp) return;
            if (Tool == EditorTool.Crop)
            {
                if (!ImageToolEligibility.TryGetCropTarget(Session, out effectTarget))
                { Message?.Invoke(L.Get("Editor.CropImageHint")); return; }
                if (effectTarget!.WindowSource is not null) { TryRedirectWindowSourceCrop(); effectTarget = null; return; }
                cropRevision = Session.SceneRevision;
                gesture = "image-crop"; UpdateImageRegion(p,e.Modifiers); return;
            }
            if (Tool == EditorTool.CaptureRegion)
            {
                BeginScreenBillboardRegion();
                gesture = CaptureShape == CaptureShape.PolygonLasso ? "capture-polygon" : "capture";
                if (CaptureShape is CaptureShape.FreehandLasso or CaptureShape.PolygonLasso) capturePoints.Add(p);
                if(CaptureShape==CaptureShape.FreehandLasso&&(e.Modifiers&OverlayModifiers.Shift)!=0)straightStrokeStart=0;
                UpdateCapturePreview(p, e.Modifiers, includeHover: false); return;
            }
            if (Tool is EditorTool.Blur or EditorTool.Pixelate)
            {
                if (Tool is EditorTool.Blur or EditorTool.Pixelate && Session.Document.Kind == DocumentKind.LiveOverlay)
                { gesture = "desktop-effect"; BeginScreenBillboardRegion(); Marquee = new(p.X,p.Y,0,0); return; }
                if (!TryGetSelectedImageTarget(out effectTarget, canvasFallback: true)) return;
                effectTarget ??= Session.Document.Background;
                if (effectTarget == null)
                { gesture = "desktop-effect"; BeginScreenBillboardRegion(); Marquee = new(p.X, p.Y, 0, 0); return; }
                if (!CanEditImage(effectTarget)) return;
                gesture = "effect"; UpdateImageRegion(p,e.Modifiers); return;
            }
            if (Tool == EditorTool.Eraser) { gesture = "erase"; BeginPointerTransaction(L.Get("Editor.History.Erase")); Erase(p); return; }
            if (Tool == EditorTool.Select) { StartSelect(p, e.Modifiers); return; }
            gesture = "draw";
            BeginPointerTransaction(L.Format("Editor.History.Draw", L.Get("Editor.ElementKind." + Tool)));
            drawing = new CanvasElement { Kind = Enum.Parse<ElementKind>(Tool.ToString()), Style = Style.Clone(), CornerRadius = CornerRadius,
                Symbol = Symbol, Transform = Affine2D.Translation(p.X, p.Y), Points = [new(0, 0)] };
            if (Tool == EditorTool.Presentation)
            { drawing.Presentation = (Presentation ?? new()).Clone(); drawing.Presentation.Modules = PresentationModules.Keys; }
            if(ElementGeometry.IsBoxShape(drawing.Kind) || drawing.Kind == ElementKind.Presentation)drawing.ConstrainAspect=drawing.Kind == ElementKind.Presentation || ConstrainAspect;
            if (Tool == EditorTool.Highlighter) drawing.Style.StrokeWidth = Math.Max(16, Style.StrokeWidth);
            if (drawing.Kind == ElementKind.Freehand && SmoothFreehand) freehandCorrection = new(drawing.Style.StrokeWidth);
            if(drawing.Kind is ElementKind.Freehand or ElementKind.Highlighter&&(e.Modifiers&OverlayModifiers.Shift)!=0)straightStrokeStart=0;
            if (UsesDefaultSize(Tool)) ApplyDefaultSize(drawing, start, e.Modifiers);
            BeginBillboardDrawing();
            if (!IsDrawingSafe()) { Cancel(); RejectPointer(true); return; }
            InsertWorldDrawing();
            Session.Selection.Clear();
            if (!screenBillboardDrawing && Tool is not (EditorTool.Freehand or EditorTool.Highlighter) && !ElementGeometry.IsLine(drawing.Kind))
                Session.Selection.Add(drawing.Id);
            NotifyGeometryChanged();
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
            MoveSafely(p, e.Modifiers, finishStroke: true);
            if (IsPointerBlocked && !IsPlanarStrokeDrawing)
            {
                Cancel();
                SetPointerBlocked(!CurrentCanvasMapping.IsScreenSafe(pointerScreen));
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
            else if (OwnsPointerTransaction)
            {
                if (completed == "draw" && Tool is EditorTool.Number or EditorTool.Emoji)
                {
                    if (drawing?.Kind == ElementKind.Number)
                        Session.Document.NextNumber = NumberSequence.Advance(Session.Document.NextNumber, drawing.Number, NumberStep);
                    CommitPointerEdit();
                }
                else if (completed == "draw" && !gestureMoved && drawing is not null && ElementGeometry.IsLine(drawing.Kind))
                { Session.Cancel(); if (deselectOnClick) Session.SetSelection([]); }
                else if (completed == "draw" && !gestureMoved && drawing is not null && UsesDefaultSize(Tool))
                { PrepareBillboardDrawing(); ApplyDefaultSize(drawing, start, e.Modifiers); if (RefreshBillboardDrawing() && IsDrawingSafe()) CommitPointerEdit(); else { Session.Cancel(); RejectPointer(true); } }
                else if (completed == "draw" && deselectOnClick && !gestureMoved)
                { Session.Cancel(); Session.SetSelection([]); }
                else if (completed == "move" && shiftDeselectOnClick && !gestureMoved)
                { var ids = shiftDeselectTarget is { } id && ResolveHitSelectableNode(id,deepSelectionGesture) is { } node ? new[] { node } : []; Session.Cancel(); Session.SetSelection(Session.Selection.Except(ids)); }
                else if (completed == "move" && !gestureMoved && selectionOnClick is { } clicked)
                { CommitPointerEdit(); Session.SetSelection(clicked); }
                else if (completed == "draw" && drawing is { } arc && ElementGeometry.LineArcHandle(arc).HasValue)
                {
                    if (!screenBillboardDrawing) Session.Selection.Add(arc.Id);
                    CommitPointerEdit();
                }
                else CommitPointerEdit();
            }
            resizeTextRenderer?.Dispose(); resizeTextRenderer = null;
            resizeTextSources.Clear();
            straightStrokeStart=null;gestureModifiers=OverlayModifiers.None;modifierTime=null; freehandCorrection = null;
            cornerRadiusDrag = null;
            drawing = effectTarget = null; Marquee = imageRegion = null; EffectPreviewPolygon = null; Changed?.Invoke();
            strokeInterrupted = false; strokeLocalBounds = null; strokeFragmentIds.Clear();
            ClearBillboardDepth(); billboardSources.Clear();
            selectionOnClick = null;
            shiftDeselectTarget = null;
            _pointerOwnedSession = null; _pointerOwnedTransaction = 0;
        }
    }

    internal bool IsSelectionFrameHit(PointD screen, bool includeInterior = false)
    {
        if (CanvasContentHidden) return false;
        if (!SelectedEditingWorldElements(editableOnly: true).Any(element => element.Visible)) return false;
        if (SelectionScreenRotationHandle is { } rotation && screen.DistanceTo(rotation) < 14 ||
            BillboardSelectionGuide is { } guide && screen.DistanceTo(guide.Center) < 12 ||
            SelectionScreenArcHandle is { } arc && screen.DistanceTo(arc) < 12 ||
            SelectionScreenCornerRadiusHandle is { } radius && screen.DistanceTo(radius) < CornerRadiusHitDistance ||
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
        if (element.Kind == ElementKind.Presentation)
        {
            using var renderer = new SceneRenderer();
            var natural = PresentationLayout.NaturalSize(element.Presentation ?? new(), element.Style, renderer);
            width = natural.X; height = natural.Y;
        }
        // Locked dimensions already describe the intended ratio. Shift on an
        // unlocked tool retains the quick square/circle insertion shortcut.
        if (element.Kind != ElementKind.Presentation && !ConstrainAspect && (modifiers & OverlayModifiers.Shift) != 0) height = width;
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
                Style = style, Number = Session.Document.NextNumber, NumberTextSizeMode = NumberTextSizeMode, ConstrainAspect = true };
        }
        else
        {
            style.FontFamily = EmojiGlyphs.FontFamily;
            drawing = new() { Kind = ElementKind.Text, Text = Emoji, Width = style.FontSize, Height = style.FontSize, Style = style, ConstrainAspect = true };
        }
        drawing.Transform = Affine2D.Translation(center.X - drawing.Width / 2, center.Y - drawing.Height / 2);
        BeginPointerTransaction(L.Get("History.AddElement"));
        gesture = "draw";
        BeginBillboardDrawing();
        // These tools keep their continuous click-to-insert behavior even when another object is selected.
        deselectOnClick = false;
        InsertWorldDrawing();
        Session.Selection.Clear(); if (!screenBillboardDrawing) Session.Selection.Add(drawing.Id);
        NotifyGeometryChanged();
    }

    private void ResizePlacement(PointD point, OverlayModifiers modifiers)
    {
        if (drawing is null || !gestureMoved) return;
        var delta = point - start;
        double sx = Math.Max(1 / drawing.Width, Math.Abs(delta.X) / drawing.Width);
        double sy = Math.Max(1 / drawing.Height, Math.Abs(delta.Y) / drawing.Height);
        if (ElementGeometry.PreservesAspect(drawing) ^ ((modifiers & OverlayModifiers.Shift) != 0)) sx = sy = Math.Max(sx, sy);
        double x = delta.X < 0 ? start.X - drawing.Width * sx : start.X;
        double y = delta.Y < 0 ? start.Y - drawing.Height * sy : start.Y;
        // Scale the complete instance so the glyph and badge grow together; tool defaults stay unchanged.
        drawing.Transform = Affine2D.Translation(x, y).Compose(Affine2D.Scale(sx, sy));
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
        centeredGesture = (modifiers & OverlayModifiers.Alt) != 0;
        deepSelectionGesture = (modifiers & OverlayModifiers.Control) != 0;
        duplicateOnDrag = centeredGesture; duplicatedOnDrag = false;
        var selected = SelectedEditingWorldElements(editableOnly: true).ToArray();
        if ((modifiers & OverlayModifiers.Control) == 0 && selected is [var shape] && TryStartCornerRadius(shape)) return;
        if (TryStartBillboardHeight()) return;
        var bounds = Session.SelectionBounds(editableOnly: true);
        if (bounds != null && selected.Length > 0 && (modifiers & OverlayModifiers.Control)==0)
        {
            selectionBounds = bounds.Value;
            if (!HasNodeSelection && selected.Length == 1 && selected[0].Kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow && selected[0].Points.Count > 1)
            {
                var element = selected[0];
                var screen = CurrentCanvasMapping.ElementToScreen(element);
                if (ScreenArcHandle(element) is { } handle && handle.DistanceTo(pointerScreen) < 12 &&
                    element.Points.All(point => screen.TransformPoint(point).DistanceTo(pointerScreen) >= handle.DistanceTo(pointerScreen)))
                {
                    BeginWorldEndpoint(Session.Document.Elements.Single(source => source.Id == element.Id));
                    billboardEndpointSource = element.IsBillboard ? element.Clone() : null;
                    endpoint = -1;
                    arcHandleGrabOffset = pointerScreen - handle;
                    arcHandleStart = (element.Style.LineArcOffset, element.Style.LineArcBulge!.Value);
                    gesture = "endpoint";
                    BeginPointerTransaction(L.Get("Editor.History.AdjustArc"));
                    return;
                }
                foreach (var i in new[] { 0, element.Points.Count - 1 })
                    if (screen.TransformPoint(element.Points[i]).DistanceTo(pointerScreen) < 12)
                    { BeginWorldEndpoint(Session.Document.Elements.Single(source => source.Id == element.Id)); billboardEndpointSource = element.IsBillboard ? element.Clone() : null; endpoint = i; gesture = "endpoint"; BeginPointerTransaction(L.Get("Editor.History.AdjustEndpoint")); return; }
            }
            if (SelectionScreenRotationHandle is { } rotation && pointerScreen.DistanceTo(rotation) < 14) gesture = "rotate";
            else if (SelectionScreenCorners.Any(c => pointerScreen.DistanceTo(c) < 12)) gesture = "resize";
            else if ((modifiers & OverlayModifiers.Control) == 0 && SelectionScreenCorners is { Count: 4 } screenCorners &&
                ElementGeometry.PointInPolygon(pointerScreen, screenCorners)) gesture = "move";
        }
        if (gesture == null && selectedOnly && bounds is not null)
        { selectionBounds = bounds.Value; gesture = "move"; }
        if (gesture == "move" && !selectedOnly && modifiers is OverlayModifiers.None or OverlayModifiers.Shift &&
            HitTestScreen(pointerScreen, 7) is { } foreground)
        {
            var unit = ResolveHitSelectableNode(foreground.Id) is { } foregroundNode ? new[] { foregroundNode } : [];
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
            bool individual = (modifiers & OverlayModifiers.Control) != 0;
            if ((modifiers & OverlayModifiers.Shift) != 0)
            {
                var ids = ResolveHitSelectableNode(hit.Id, individual) is { } additiveNode ? new[] { additiveNode } : [];
                if (ids.All(Session.Selection.Contains)) { shiftDeselectOnClick = true; shiftDeselectTarget = hit.Id; }
                else { Session.SetSelection(Session.Selection.Union(ids)); return; }
            }
            var unit = ResolveHitSelectableNode(hit.Id, individual) is { } selectedNode ? new[] { selectedNode } : [];
            if (individual && !shiftDeselectOnClick || !unit.All(Session.Selection.Contains)) Session.SetSelection(unit);
            selectionBounds = Session.SelectionBounds(editableOnly: true)!.Value;
            gesture = "move";
        }
        transforms = Session.SelectedElements().Where(x => !x.Locked).ToDictionary(x => x.Id, x => x.Transform);
        billboardSources = Session.SelectedElements().Where(x => !x.Locked && x.IsBillboard).ToDictionary(x => x.Id, x => x.Clone());
        resizeSource = gesture == "resize" && selected.Length == 1 && (ElementGeometry.IsBoxShape(selected[0].Kind) || selected[0].Kind is ElementKind.Text or ElementKind.Presentation) ? selected[0].Clone() : null;
        resizeTextSources=gesture=="resize"?selected.Where(element=>element.Kind==ElementKind.Text).ToDictionary(element=>element.Id,element=>element.Clone()):[];
        if (resizeTextSources.Count>0) resizeTextRenderer = new();
        PrepareNodeTransform();
        BeginPointerTransaction(L.Get("Editor.History.Transform"));
    }

    private void Move(PointD p, OverlayModifiers modifiers, bool finishStroke = false)
    {
        gestureModifiers=modifiers;
        if (gesture == "move" && duplicateOnDrag && !duplicatedOnDrag)
        {
            if (!gestureMoved) return;
            Session.DuplicateSelection(0, 0, withinTransaction: true);
            duplicatedOnDrag = true; selectionOnClick = null; shiftDeselectOnClick = false;
            transforms = Session.SelectedElements().Where(x=>!x.Locked).ToDictionary(x=>x.Id,x=>x.Transform);
            billboardSources = Session.SelectedElements().Where(x=>!x.Locked && x.IsBillboard).ToDictionary(x=>x.Id,x=>x.Clone());
            PrepareNodeTransform();
        }
        // A plain click must not round-trip an unchanged billboard anchor through
        // the projection and turn floating-point noise into a document edit.
        if (gesture == "move" && !gestureMoved && pointerScreen == startScreen) return;
        // An overlapping foreground layer wins a click, while dragging from the same
        // selected-frame body moves the current selection. Ignore click-sized jitter
        // until the existing physical movement threshold establishes that intent.
        if (gesture == "move" && (selectionOnClick is not null || shiftDeselectOnClick) && !gestureMoved) return;
        if (MoveNodeSelection(p, modifiers)) return;
        if (MoveBillboardSelection(p, modifiers)) return;
        if (HasCaptureGesture) { UpdateCapturePreview(p, modifiers); return; }
        if (gesture is "effect" or "image-crop") { UpdateImageRegion(p,modifiers); Changed?.Invoke(); return; }
        if (gesture == "marquee") { Marquee = RectD.FromPoints(startScreen, pointerScreen); Changed?.Invoke(); return; }
        if (gesture == "desktop-effect") { Marquee = RectD.FromPoints(start,ConstrainBox(modifiers)?SquareEnd(start,p):p); Changed?.Invoke(); return; }
        if (gesture == "erase") { Erase(p); return; }
        if (gesture == "endpoint" && drawing != null)
        {
            if (endpoint == -1)
            {
                var source = billboardEndpointSource ?? drawing;
                var canvasMapping = CurrentCanvasMapping;
                var screenMap = canvasMapping.ElementToScreen(source);
                if (!screenMap.TryInverse(out var toLocal) ||
                    !toLocal.TryTransformPoint(pointerScreen - arcHandleGrabOffset, out var handle)) { RejectPointer(); return; }
                var (offset, bulge) = pointerScreen == startScreen && !gestureMoved ? arcHandleStart :
                    ElementGeometry.LineArcCoordinatesAt(source, handle, (modifiers & OverlayModifiers.Shift) != 0);
                if (drawing.Style.LineArcBulge != bulge || drawing.Style.LineArcOffset != offset)
                {
                    drawing.Style.LineArcBulge = bulge;
                    drawing.Style.LineArcOffset = offset;
                    if (drawing.IsBillboard &&
                        (!canvasMapping.TryGetEqualDepthLine(canvasMapping.ToScreen(source.BillboardAnchor), out var depth) ||
                         !BillboardGeometry.TryPlaceFromScreen(drawing, canvasMapping, depth, screenMap)))
                    { RejectPointer(); return; }
                    NotifyGeometryChanged();
                }
                return;
            }
            if (drawing.IsBillboard)
            {
                var source = billboardEndpointSource ?? drawing;
                var canvasMapping = CurrentCanvasMapping;
                var screenMap = canvasMapping.ElementToScreen(source);
                if (!screenMap.TryInverse(out var toLocal)) return;
                var end = pointerScreen;
                if ((modifiers & OverlayModifiers.Shift) != 0)
                    end = EightDirectionEnd(screenMap.TransformPoint(source.Points[endpoint == 0 ? source.Points.Count - 1 : 0]), end);
                drawing.Points = [..source.Points]; drawing.Points[endpoint] = toLocal.TransformPoint(end);
                if (canvasMapping.TryGetEqualDepthLine(canvasMapping.ToScreen(source.BillboardAnchor), out var depth))
                    BillboardGeometry.TryPlaceFromScreen(drawing, canvasMapping, depth, screenMap);
                NotifyGeometryChanged(); return;
            }
            var mapping=ElementGeometry.LocalToPlane(drawing);
            if(!mapping.TryInverse(out var inverse))return;
            if((modifiers&OverlayModifiers.Shift)!=0)
                p=EightDirectionEnd(mapping.TransformPoint(drawing.Points[endpoint==0?drawing.Points.Count-1:0]),p);
            drawing.Points[endpoint] = inverse.TransformPoint(p); NotifyGeometryChanged(); return;
        }
        if (gesture == "draw" && drawing != null)
        {
            PrepareBillboardDrawing();
            if (UsesDefaultSize(Tool) && !gestureMoved)
            {
                ApplyDefaultSize(drawing, start, modifiers);
                RefreshBillboardDrawing(); NotifyGeometryChanged(); return;
            }
            if (Tool is EditorTool.Number or EditorTool.Emoji)
            {
                ResizePlacement(p, modifiers); RefreshBillboardDrawing(); NotifyGeometryChanged(); return;
            }
            var local = p - start;
            if (drawing.Kind is ElementKind.Freehand or ElementKind.Highlighter)
            {
                UpdateStroke(drawing.Points, local, modifiers, finishStroke);
            }
            else if (drawing.Kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow)
                drawing.Points = [new(0, 0), (modifiers&OverlayModifiers.Shift)!=0?EightDirectionEnd(start,p)-start:local];
            else
            {
                if (drawing.Kind == ElementKind.Presentation)
                {
                    using var renderer = new SceneRenderer();
                    var natural = PresentationLayout.NaturalSize(drawing.Presentation ?? new(), Style, renderer);
                    var drag = p - start;
                    double height = Math.Max(1, Math.Abs(drag.Y));
                    double width = (modifiers & OverlayModifiers.Shift) == 0 ? height * natural.X / natural.Y : Math.Max(1, Math.Abs(drag.X));
                    p = start + new PointD(Math.CopySign(width, drag.X), Math.CopySign(height, drag.Y));
                    drawing.Style.FontSize = Math.Clamp(Style.FontSize * height / natural.Y, 1, 4096);
                }
                else if (ConstrainBox(modifiers)) p = ConstrainAspect && UsesDefaultSize(Tool) ? DefaultRatioEnd(start, p) : SquareEnd(start, p);
                var rect = RectD.FromPoints(centeredGesture ? start - (p - start) : start, p);
                drawing.Width = Math.Max(1, rect.Width); drawing.Height = Math.Max(1, rect.Height);
                drawing.Transform = Affine2D.Translation(rect.X, rect.Y); drawing.Points.Clear();
            }
            RefreshBillboardDrawing(); NotifyGeometryChanged(); return;
        }
        Affine2D delta;
        if (gesture == "move")
        { var move = billboardMoveDelta ?? ConstrainMove(p - start, modifiers); delta = Affine2D.Translation(move.X, move.Y); }
        else if (gesture == "rotate")
        {
            delta = RotationDelta(selectionBounds.Center, start, p, modifiers);
        }
        else if (gesture == "resize")
        {
            var single = Session.SelectedElements().Where(e => !e.Locked).ToArray();
            if (single.Length == 1 && resizeSource is { } source)
            {
                var element = single[0]; var original = transforms[element.Id];
                if (!ElementGeometry.LocalToPlane(source).TryInverse(out var inverse)) return;
                var localCenter = ResizePivot(ElementGeometry.LocalBounds(source), inverse.TransformPoint(start));
                var fromLocal = inverse.TransformPoint(start) - localCenter;
                var toLocal = inverse.TransformPoint(p) - localCenter;
                double scaleX = Math.Abs(fromLocal.X) < .001 ? 1 : Math.Max(.02, toLocal.X / fromLocal.X);
                double scaleY = Math.Abs(fromLocal.Y) < .001 ? 1 : Math.Max(.02, toLocal.Y / fromLocal.Y);
                if (ElementGeometry.PreservesAspect(source) ^ ((modifiers & OverlayModifiers.Shift) != 0)) scaleX = scaleY = UniformScale(fromLocal, toLocal);
                if (source.Kind == ElementKind.Text)
                {
                    TextFrameLayout.Resize(element, source, scaleX, scaleY, resizeTextRenderer!);
                    PreserveResizePivot(source, element, localCenter);
                    NotifyGeometryChanged(); return;
                }
                if (source.Kind == ElementKind.Presentation) PresentationFrameLayout.Resize(element, source, scaleX, scaleY);
                else
                {
                    element.Width = source.Width; element.Height = source.Height; element.CornerRadius = source.CornerRadius; element.Transform = original;
                    ElementGeometry.ResizeShapeGeometry(element, scaleX, scaleY);
                }
                PreserveResizePivot(source, element, localCenter);
                NotifyGeometryChanged(); return;
            }
            var center = ResizePivot(selectionBounds, start);
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
        NotifyGeometryChanged();
    }

    private void Erase(PointD p)
    {
        if (CanvasDepthOrder.IsEnabled(PickingDocument))
        {
            if (HitTestScreen(pointerScreen, 10) is { } visible) new SceneGraph(Session.Document).RemoveNodes([visible.Id]);
        }
        else
        {
            var graph = new SceneGraph(Session.Document);
            graph.RemoveNodes(Session.Document.Elements.Where(e => !graph.IsLocked(e.Id) && ElementHitTestScreen(e, pointerScreen, 10)).Select(e => e.Id).ToArray());
        }
        NotifyGeometryChanged();
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
        if (Session.Selection.Any(id => Session.Document.Groups.Any(group => group.Id == id)))
        { image = null; Message?.Invoke(L.Get("Editor.SelectSingleImage")); return false; }
        image = selected.Length == 1 ? selected[0] : null;
        if (selected.Length > 1 && !canvasFallback) { Message?.Invoke(L.Get("Editor.SelectSingleImage")); return false; }
        if (image is not null && Session.IsNodeLocked(image.Id)) { Message?.Invoke(L.Get("Editor.ImageTargetLocked")); return false; }
        return true;
    }
    private void UpdateImageRegion(PointD p,OverlayModifiers modifiers)
    {
        if (effectTarget == null) return;
        var world = new SceneGraph(Session.Document).ToWorld(effectTarget);
        var targetMapping = world.IsBillboard ? CurrentCanvasMapping.ElementToScreen(world) : ElementGeometry.LocalToPlane(world);
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
        EffectPreviewPolygon = Corners(imageRegion.Value).Select(ElementGeometry.LocalToPlane(world).TransformPoint).ToArray();
        Marquee = ElementGeometry.LocalToPlane(world).TransformBounds(imageRegion.Value);
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
            ElementGeometry.CropImageFrame(image, local);
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
