using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class ClickPlacementThresholdTests
{
    private static readonly EditorTool[] Tools = [EditorTool.Rectangle, EditorTool.RoundedRectangle,
        EditorTool.Ellipse];

    internal static void Run(Action<string, bool> check)
    {
        foreach (var tool in Tools)
        {
            var (session, editor) = Create(tool);
            Send(editor, OverlayPointerKind.Down, new(100, 100));
            Send(editor, OverlayPointerKind.Move, new(103, 102));
            Send(editor, OverlayPointerKind.Up, new(103, 102));
            var item = session.Document.Elements.Single();
            var clickBounds = ElementGeometry.Bounds(item, false);
            var expectedClick = ExpectedClickBounds(tool);
            check(tool + " small physical jitter keeps the configured click size instead of a tiny drag",
                NearlyRect(clickBounds, expectedClick) &&
                session.UndoCount == 1 && !session.IsTransactionActive);
        }
        SymbolPlacements(check);
        SizedPlacements(check);

        foreach (double threshold in new[] { 6d, 9d, 12d })
        foreach (var tool in Tools)
        {
            var (session, editor) = Create(tool); Threshold(editor, threshold);
            Send(editor, OverlayPointerKind.Down, new(100, 100));
            var preview = session.Document.Elements.Single();
            check($"{tool} threshold={threshold} starts a default-sized reversible preview",
                preview.Width == 90 && preview.Height == 40 && session.IsTransactionActive && session.UndoCount == 0);
            Send(editor, OverlayPointerKind.Move, new(100 + threshold - .01, 100));
            check($"{tool} threshold={threshold} does not flash a tiny preview inside the click tolerance",
                preview.Width == 90 && preview.Height == 40 && preview.Transform == Affine2D.Translation(55, 80));
            editor.ModifiersChanged(OverlayModifiers.Shift, new(100 + threshold - .01, 100));
            check($"{tool} Shift inside the click tolerance updates the default preview without starting a drag",
                preview.Width == 90 && preview.Height == 90);
            Send(editor, OverlayPointerKind.Up, new(100 + threshold - .01, 100), OverlayModifiers.Shift);
            var shiftedClick = ExpectedShiftedClickBounds(tool);
            check($"{tool} threshold={threshold} keeps click geometry on release and one undo",
                NearlyRect(ElementGeometry.Bounds(preview, false), shiftedClick) && session.UndoCount == 1);

            (session, editor) = Create(tool); Threshold(editor, threshold);
            Send(editor, OverlayPointerKind.Down, new(100, 100));
            Send(editor, OverlayPointerKind.Move, new(100 + threshold, 100));
            Send(editor, OverlayPointerKind.Up, new(100 + threshold, 100));
            var item = session.Document.Elements.Single();
            var thresholdBounds = ElementGeometry.Bounds(item, false);
            check($"{tool} exact threshold={threshold} starts explicit drag geometry",
                NearlyRect(thresholdBounds, new RectD(100, 100, threshold, 1)));

            (session, editor) = Create(tool); Threshold(editor, threshold);
            Send(editor, OverlayPointerKind.Down, new(100, 100));
            Send(editor, OverlayPointerKind.Move, new(160, 135));
            Send(editor, OverlayPointerKind.Up, new(160, 135));
            item = session.Document.Elements.Single();
            check($"{tool} threshold={threshold} retains free sizing for a deliberate drag",
                NearlyRect(ElementGeometry.Bounds(item, false), new RectD(100, 100, 60, 35)));
        }
        StartThresholdIsStable(check); ScreenSpaceThreshold(check); OtherGestures(check); DpiSelection(check);
        InvalidBoxDefaultsDoNotCommit(check);
    }

    private static void SymbolPlacements(Action<string, bool> check)
    {
        var (session, editor) = Create(EditorTool.Symbol);
        Send(editor, OverlayPointerKind.Down, new(100, 100));
        Send(editor, OverlayPointerKind.Move, new(103, 102));
        Send(editor, OverlayPointerKind.Up, new(103, 102));
        check("Symbol small jitter uses the configured long-edge size with natural heart proportions",
            NearlyRect(ElementGeometry.Bounds(session.Document.Elements.Single(), false), ExpectedSymbolBounds(100, 100, 90)) &&
            session.UndoCount == 1 && !session.IsTransactionActive);

        foreach (double threshold in new[] { 6d, 9d, 12d })
        {
            (session, editor) = Create(EditorTool.Symbol); Threshold(editor, threshold);
            Send(editor, OverlayPointerKind.Down, new(100, 100));
            var initial = ExpectedSymbolBounds(100, 100, 90);
            check($"Symbol threshold={threshold} starts a natural-size reversible preview",
                NearlyRect(ElementGeometry.Bounds(session.Document.Elements.Single(), false), initial) && session.IsTransactionActive);
            Send(editor, OverlayPointerKind.Move, new(100 + threshold - .01, 100));
            check($"Symbol threshold={threshold} preserves its natural preview inside the click tolerance",
                NearlyRect(ElementGeometry.Bounds(session.Document.Elements.Single(), false), initial));
            Send(editor, OverlayPointerKind.Up, new(100 + threshold - .01, 100));
            check($"Symbol threshold={threshold} commits one unchanged click placement",
                NearlyRect(ElementGeometry.Bounds(session.Document.Elements.Single(), false), initial) && session.UndoCount == 1);

            (session, editor) = Create(EditorTool.Symbol); Threshold(editor, threshold);
            Send(editor, OverlayPointerKind.Down, new(100, 100));
            Send(editor, OverlayPointerKind.Move, new(160, 135));
            Send(editor, OverlayPointerKind.Up, new(160, 135));
            var size = SymbolGeometry.Shape(SymbolKind.Heart).SizeAtLongEdge(60);
            check($"Symbol threshold={threshold} retains natural proportions for a deliberate drag",
                NearlyRect(ElementGeometry.Bounds(session.Document.Elements.Single(), false), new RectD(100, 100, size.X, size.Y)));
        }
    }

    private static void InvalidBoxDefaultsDoNotCommit(Action<string, bool> check)
    {
        foreach (var tool in Tools.Append(EditorTool.Symbol))
        foreach (var pair in new[] { (Width: 0d, Height: 0d), (Width: -10d, Height: -5d), (Width: double.NaN, Height: 40d) })
        {
            var session = new EditorSession();
            var editor = new EditorController(session)
            {
                Tool = tool,
                Symbol = SymbolKind.Heart,
                DefaultWidth = pair.Width,
                DefaultHeight = pair.Height
            };
            Send(editor, OverlayPointerKind.Down, new(100, 100));
            bool rejectedOnPress = editor.IsPointerBlocked && session.Document.Elements.Count == 0 &&
                session.UndoCount == 0 && !session.IsTransactionActive;
            Send(editor, OverlayPointerKind.Up, new(100, 100));
            check($"{tool} invalid default {pair.Width}/{pair.Height} never commits a degenerate layer",
                rejectedOnPress && session.Document.Elements.Count == 0 && session.UndoCount == 0 &&
                !session.IsTransactionActive);
        }
    }

    private static void SizedPlacements(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Number, EditorTool.Emoji })
        {
            var (session, editor) = Create(tool); editor.NumberStep = 3;
            Send(editor, OverlayPointerKind.Down, new(100, 100));
            var initial = ElementGeometry.Bounds(session.Document.Elements.Single(), false);
            Send(editor, OverlayPointerKind.Move, new(103, 102)); Send(editor, OverlayPointerKind.Up, new(103, 102));
            check(tool + " small jitter keeps its existing click size and centered placement",
                ElementGeometry.Bounds(session.Document.Elements.Single(), false) == initial && session.UndoCount == 1 &&
                session.Document.NextNumber == (tool == EditorTool.Number ? 4 : 1));
            foreach (double threshold in new[] { 6d, 9d, 12d })
            {
                (session, editor) = Create(tool); Threshold(editor, threshold);
                Send(editor, OverlayPointerKind.Down, new(100, 100));
                initial = ElementGeometry.Bounds(session.Document.Elements.Single(), false);
                Send(editor, OverlayPointerKind.Move, new(100 + threshold - .01, 100));
                check($"{tool} threshold={threshold} preserves its default preview below the DPI-scaled tolerance",
                    ElementGeometry.Bounds(session.Document.Elements.Single(), false) == initial);
                Send(editor, OverlayPointerKind.Up, new(100 + threshold - .01, 100));
                check($"{tool} threshold={threshold} commits one unchanged click placement",
                    ElementGeometry.Bounds(session.Document.Elements.Single(), false) == initial && session.UndoCount == 1);

                (session, editor) = Create(tool); Threshold(editor, threshold);
                Send(editor, OverlayPointerKind.Down, new(100, 100));
                Send(editor, OverlayPointerKind.Move, new(100 + threshold, 100)); Send(editor, OverlayPointerKind.Up, new(100 + threshold, 100));
                check($"{tool} exact threshold={threshold} still enables explicit drag scaling",
                    Math.Abs(ElementGeometry.Bounds(session.Document.Elements.Single(), false).Width - threshold) < 1e-6 && session.UndoCount == 1);
            }
            (session, editor) = Create(tool); Threshold(editor, 12);
            Send(editor, OverlayPointerKind.Down, new(100, 100));
            initial = ElementGeometry.Bounds(session.Document.Elements.Single(), false);
            Send(editor, OverlayPointerKind.Move, new(180, 140)); Send(editor, OverlayPointerKind.Up, new(180, 140));
            check(tool + " keeps its existing proportional size for a deliberate drag",
                ElementGeometry.Bounds(session.Document.Elements.Single(), false) != initial && session.UndoCount == 1);
        }
    }

    private static void StartThresholdIsStable(Action<string, bool> check)
    {
        var (session, editor) = Create(EditorTool.Rectangle); Threshold(editor, 12);
        Send(editor, OverlayPointerKind.Down, new(100, 100)); Threshold(editor, 6);
        Send(editor, OverlayPointerKind.Move, new(109, 100)); Send(editor, OverlayPointerKind.Up, new(109, 100));
        check("crossing to a lower-DPI monitor does not shrink the threshold locked at pointer down", session.Document.Elements.Single().Width == 90);
        (session, editor) = Create(EditorTool.Rectangle); Threshold(editor, 6);
        Send(editor, OverlayPointerKind.Down, new(100, 100));
        Send(editor, OverlayPointerKind.Move, new(107, 100)); Send(editor, OverlayPointerKind.Up, new(101, 100));
        check("a gesture that exceeded the threshold stays a drag after returning near its origin", session.Document.Elements.Single().Width == 1);
        (session, editor) = Create(EditorTool.Rectangle);
        Send(editor, OverlayPointerKind.Down, new(100, 100)); Send(editor, OverlayPointerKind.Up, new(108, 104));
        check("release-only displacement still starts a drag when no Move arrived", session.Document.Elements.Single().Width == 8 && session.Document.Elements.Single().Height == 4);
        (session, editor) = Create(EditorTool.Rectangle);
        Send(editor, OverlayPointerKind.Down, new(100, 100)); Send(editor, OverlayPointerKind.Move, new(103, 102)); editor.Cancel();
        check("cancel removes a click preview without history", session.Document.Elements.Count == 0 && session.UndoCount == 0 && !session.IsTransactionActive);
        foreach (double offset in new[] { 4d, 4.3d })
        {
            (session, editor) = Create(EditorTool.Ellipse);
            Send(editor, OverlayPointerKind.Down, new(100, 100)); Send(editor, OverlayPointerKind.Up, new(100 + offset, 100 + offset));
            check($"the six-pixel radial threshold treats diagonal offset {offset} consistently",
                Math.Abs(session.Document.Elements.Single().Width - (offset == 4 ? 90 : offset)) < 1e-9);
        }
    }

    private static void ScreenSpaceThreshold(Action<string, bool> check)
    {
        var document = new SceneDocument { Viewport = new(0, 0, 1000, 800) };
        var projection = CanvasCalibration.CreateInitial(document.Viewport, 10, 8);
        projection.Matrix = new(4, 0, 0, 0, 4, 0, 0, 0, 1); projection.Enabled = true;
        projection.Quad = projection.Quad.Select(projection.Matrix.TransformPoint).ToList(); document.Canvas.Projection = projection;
        var session = new EditorSession(document); var editor = new EditorController(session)
            { Tool = EditorTool.Rectangle, DefaultWidth = 90, DefaultHeight = 40, ScreenOrigin = new(-1920, -300) };
        Send(editor, OverlayPointerKind.Down, new(-1520, 100)); Send(editor, OverlayPointerKind.Up, new(-1515, 100));
        check("projection and a negative desktop origin keep jitter distance in physical screen pixels",
            session.Document.Elements.Single().Width == 90 && session.Document.Elements.Single().Height == 40);

        document = new() { Viewport = new(0, 0, 1000, 800) };
        projection = CanvasCalibration.CreateInitial(document.Viewport, 10, 8);
        projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .001, 1); projection.Enabled = true;
        projection.Quad = projection.Quad.Select(projection.Matrix.TransformPoint).ToList(); document.Canvas.Projection = projection;
        session = new(document); editor = new(session) { Tool = EditorTool.Rectangle, BillboardWorkflow = BillboardCreationWorkflow.FixedDepthLine, BillboardMode = true, DefaultWidth = 90, DefaultHeight = 40 };
        Send(editor, OverlayPointerKind.Down, new(300, 300)); Send(editor, OverlayPointerKind.Up, new(300, 300));
        Send(editor, OverlayPointerKind.Down, new(100, 100)); Send(editor, OverlayPointerKind.Move, new(103, 102)); Send(editor, OverlayPointerKind.Up, new(103, 102));
        var billboard = session.Document.Elements.Single();
        check("billboard click jitter retains default logical size and fixed-depth placement",
            billboard.IsBillboard && billboard.Width == 90 && billboard.Height == 40 && session.UndoCount == 1 &&
            Math.Abs(CanvasMapping.Create(document).ProjectedBounds(billboard, false).Width - 63) < 1e-6);
    }

    private static void OtherGestures(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Line, EditorTool.Arrow, EditorTool.DoubleArrow, EditorTool.Freehand, EditorTool.Highlighter })
        {
            var (session, editor) = Create(tool); Threshold(editor, 30);
            Send(editor, OverlayPointerKind.Down, new(100, 100)); Send(editor, OverlayPointerKind.Move, new(103, 100)); Send(editor, OverlayPointerKind.Up, new(103, 100));
            var item = session.Document.Elements.Single();
            check(tool + " retains the existing short-drag input instead of inheriting the click-placement threshold",
                item.Points.Count >= 2 && item.Points[^1].X == 3 && session.UndoCount == 1);
        }
        var pair = Create(EditorTool.Rectangle); Threshold(pair.Editor, 30);
        pair.Session.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 80, Transform = Affine2D.Translation(50, 50) });
        pair.Session.SelectAll(); var id = pair.Session.Document.Elements.Single().Id;
        Send(pair.Editor, OverlayPointerKind.Down, new(100, 90)); Send(pair.Editor, OverlayPointerKind.Move, new(103, 90)); Send(pair.Editor, OverlayPointerKind.Up, new(103, 90));
        check("adjusting an existing selected rectangle still moves three physical pixels", pair.Session.Document.Elements.Single(item => item.Id == id).Transform == Affine2D.Translation(53, 50));
        pair = Create(EditorTool.Rectangle); Threshold(pair.Editor, 30);
        pair.Session.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 80, Transform = Affine2D.Translation(50, 50) });
        Send(pair.Editor, OverlayPointerKind.Down, new(50, 50)); Send(pair.Editor, OverlayPointerKind.Move, new(53, 53)); Send(pair.Editor, OverlayPointerKind.Up, new(53, 53));
        check("resizing a selected rectangle still responds inside the click-placement tolerance",
            ElementGeometry.Bounds(pair.Session.Document.Elements.Single(), false).Width < 100 && pair.Session.UndoCount == 2);
        pair = Create(EditorTool.CaptureRegion); Threshold(pair.Editor, 30);
        CaptureSelection? capture = null; pair.Editor.CaptureSelectionCompleted += value => capture = value;
        Send(pair.Editor, OverlayPointerKind.Down, new(100, 100)); Send(pair.Editor, OverlayPointerKind.Up, new(103, 103));
        check("a valid three-pixel capture keeps its existing two-pixel geometry minimum", capture?.Bounds == new RectD(100, 100, 3, 3));
    }

    private static void DpiSelection(Action<string, bool> check)
    {
        var bounds = new PixelBounds(-1920, 0, 1920, 1080);
        MonitorInfo[] monitors = [new("left-200", bounds, bounds, 192, 192, false),
            new("primary-100", new(0, 0, 1920, 1080), new(0, 0, 1920, 1080), 96, 96, true),
            new("right-150", new(1920, 0, 1600, 900), new(1920, 0, 1600, 900), 144, 144, false)];
        var method = typeof(DesktopSession).GetMethod("ClickPlacementThresholdFor", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var (point, expected) in new[] { (new OverlayPoint(-100, 100), 12d), (new OverlayPoint(0, 100), 6d), (new OverlayPoint(2100, 100), 9d) })
            check($"pointer-start monitor DPI sets click tolerance to {expected} physical pixels", (double)method.Invoke(null, [monitors, point])! == expected);
        check("an outside-monitor coordinate conservatively uses the primary monitor DPI", (double)method.Invoke(null, [monitors, new OverlayPoint(9000, 9000)])! == 6);
        check("an unavailable monitor list retains the six-pixel controller default", (double)method.Invoke(null, [Array.Empty<MonitorInfo>(), new OverlayPoint(0, 0)])! == 6);
    }
    private static (EditorSession Session, EditorController Editor) Create(EditorTool tool)
    { var session = new EditorSession(); return (session, new(session) { Tool = tool, DefaultWidth = 90, DefaultHeight = 40, Symbol = SymbolKind.Heart }); }
    private static RectD ExpectedClickBounds(EditorTool tool) => tool switch
    {
        _ when tool is EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse => new(55, 80, 90, 40),
        _ => throw new ArgumentOutOfRangeException(nameof(tool))
    };
    private static RectD ExpectedShiftedClickBounds(EditorTool tool) => tool switch
    {
        _ when tool is EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse => new(55, 55, 90, 90),
        _ => throw new ArgumentOutOfRangeException(nameof(tool))
    };
    private static RectD ExpectedSymbolBounds(double centerX, double centerY, double longEdge)
    {
        var size = SymbolGeometry.Shape(SymbolKind.Heart).SizeAtLongEdge(longEdge);
        return new(centerX - size.X / 2, centerY - size.Y / 2, size.X, size.Y);
    }
    private static bool NearlyRect(RectD actual, RectD expected) =>
        Math.Abs(actual.X - expected.X) < 1e-6 && Math.Abs(actual.Y - expected.Y) < 1e-6 &&
        Math.Abs(actual.Width - expected.Width) < 1e-6 && Math.Abs(actual.Height - expected.Height) < 1e-6;
    private static void Threshold(EditorController editor, double threshold) => typeof(EditorController)
        .GetProperty("ClickPlacementDragThreshold", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, threshold);
    private static void Send(EditorController editor, OverlayPointerKind kind, OverlayPoint point, OverlayModifiers modifiers = OverlayModifiers.None) =>
        editor.Pointer(new(kind, point, OverlayMouseButton.Left, modifiers));
}
