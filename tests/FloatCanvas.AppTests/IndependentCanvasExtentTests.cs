using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class IndependentCanvasExtentTests
{
    private static readonly RectD Screen = new(0, 0, 3840, 2160);
    private static readonly PointD[] Points = (from y in new[] { 108d, 1080d, 2052d }
        from x in new[] { 192d, 1920d, 3648d } select new PointD(x, y)).ToArray();
    private const uint Ink = 0xffef6040, Background = 0xff214f69;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Extent(check, RootCanvasKind.Main, grouped: true);
        Extent(check, RootCanvasKind.Main, grouped: false);
        Extent(check, RootCanvasKind.Foreground, grouped: false);
        Extent(check, RootCanvasKind.Background, grouped: false);
        ReferenceSettings(check);
    }

    private static void Extent(Action<string, bool> check, RootCanvasKind root, bool grouped)
    {
        var document = new SceneDocument { Viewport = Screen };
        var nodes = Points.Select(point => new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 80,
            Transform = Affine2D.Translation(point.X - 40, point.Y - 40),
            Style = new() { Foreground = 0, Background = Ink, StrokeWidth = 0, Variant = ElementAppearance.ShapeIndependent } }).ToList();
        document.Elements = nodes;
        CanvasGroup? group = null;
        if (grouped)
        {
            group = new() { ChildOrder = nodes.Select(node => node.Id).ToList(), OrderMode = GroupOrderMode.AtomicBlock };
            foreach (var node in nodes) node.GroupId = group.Id;
            document.Groups = [group]; document.RootOrder = [group.Id];
        }
        else foreach (var node in nodes) SceneCanvasRoots.Assign(document, node.Id, root);
        var session = new EditorSession(document); session.SetRootCanvas(root);
        if (group is not null) session.SetEditScope(group.Id);
        string before = System.Text.Json.JsonSerializer.Serialize(document);
        var editor = new EditorController(session) { Tool = EditorTool.Select, BackgroundPickingEnabled = false };
        try
        {
            var scene = new GroupCanvasScene();
            var view = scene.Capture(session, null, GroupCanvasMode.Temporary, default, session.Assets);
            editor.SetCanvasView(view.EditingPlacement is { } placement ? CanvasMapping.CreateView(view.Document, placement, view.EditingRootCanvas) :
                    CanvasMapping.CreateViewing(view.Document, CanvasViewTransform.Identity, view.EditingRootCanvas),
                view.Origin, view.Epoch, view.EditingPlacement, view.Document, view.EditingRootCanvas, independent: true);
            editor.ResumePresentedPicking();
            using var worker = new CanvasPreviewRenderer();
            using var snapshot = Snapshot(session, view, view.Document.Clone());
            using var frame = worker.Render(snapshot);
            using var pixels = Pixels(frame);
            check("independent 4K display keeps authored ink visible at every screen corner and edge", Points.All(point => pixels.GetPixel((int)point.X, (int)point.Y).Alpha > 0));
            check("independent 4K fallback picking reaches every screen corner and edge", Points.All(point => Pick(editor, point)));
            editor.ApplyPresentedScene(frame.PickingDocument, frame.PickingImages, frame.AnimationTime, frame.PickingResources,
                frame.ContentRevision, frame.PickingWindowFrames, frame.WindowOwnerContext, frame.PickingEvaluation);
            editor.ResumePresentedPicking();
            check("independent 4K presented picking reaches every screen corner and edge", Points.All(point => Pick(editor, point)));
            var fill = new SceneDocument { Viewport = view.Document.Viewport, Canvas = view.Document.Canvas.Clone() };
            fill.Canvas.BackgroundColor = Background; fill.Canvas.Grid.Visible = true; fill.Canvas.Grid.FlatCellSize = 100;
            using var fillSnapshot = Snapshot(session, view, fill, editing: false);
            using var fillFrame = worker.Render(fillSnapshot); using var fillPixels = Pixels(fillFrame);
            check("independent 4K background and grid have the same full display extent as authored ink", Points.All(point => fillPixels.GetPixel((int)point.X, (int)point.Y).Alpha == 255));
            check("view expansion does not change saved preview parameters or history", before == System.Text.Json.JsonSerializer.Serialize(document) && session.UndoCount == 0);
        }
        finally { editor.DetachSession(); }
    }

    private static void ReferenceSettings(Action<string, bool> check)
    {
        foreach (bool perspective in new[] { false, true })
        {
            var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [], PreviewCanvas = new() { Viewport = new(0, 0, 640, 480), Origin = new(12, 34) } };
            group.PreviewCanvas.Canvas.Projection.Enabled = perspective;
            var session = new EditorSession(new() { Viewport = Screen, Groups = [group], RootOrder = [group.Id] });
            session.SetEditScope(group.Id);
            var original = new SceneDocument(); group.PreviewCanvas.ApplyTo(original);
            var reference = CanvasMapping.Create(original);
            var scene = new GroupCanvasScene();
            var bounds = new RectD(-1000, -200, 4840, 2360);
            var view = scene.Capture(session, null, GroupCanvasMode.Temporary, default, session.Assets, bounds);
            var mapping = CanvasMapping.Create(view.Document);
            check("expanded and negative display bounds preserve implicit grid and perspective calibration",
                view.Document.Viewport == bounds && mapping.GridOrigin == reference.GridOrigin && mapping.GridCellSize == reference.GridCellSize &&
                Points.All(point => mapping.PlaneToScreen.TransformPoint(point).DistanceTo(reference.PlaneToScreen.TransformPoint(point)) < 1e-7));
            var resized = scene.Capture(session, null, GroupCanvasMode.Temporary, default, session.Assets, Screen);
            check("display resize invalidates old frame identity without changing saved reference viewport",
                !ReferenceEquals(view.Epoch, resized.Epoch) && group.PreviewCanvas.Viewport == new RectD(0, 0, 640, 480));
            var target = new CanvasSettingsTarget(session, true, editingViewport: bounds);
            check("canvas gestures and rendering share the expanded extent and effective grid",
                target.Viewport == bounds && target.CreateDisplayDocument().Viewport == bounds && target.Settings.Grid.FlatOrigin == view.Document.Canvas.Grid.FlatOrigin);
        }
    }

    private static CanvasPreviewSnapshot Snapshot(EditorSession session, GroupCanvasView view, SceneDocument document, bool editing = true) => new()
    {
        Context = new(session, new object(), view.Origin, false, false, false, session.Document, GroupEpoch: view.Epoch),
        SceneIdentity = document, Document = document, FrozenEvaluation = new(document),
        EditingEvaluation = editing ? new(session.Document.Clone()) : null, EditingPlacement = editing ? view.EditingPlacement : null,
        EditingRootCanvas = view.EditingRootCanvas, AuthorRootCanvas = session.ActiveRootCanvas, EditingScopeId = session.EditScopeId,
        IndependentCanvas = true, Resources = [], Outputs = [new(0, 0, 3840, 2160)], SourceDocument = session.Document,
        Revision = session.SceneRevision, ContentRevision = session.PreviewRevision, GraphChanged = true, BypassCache = true
    };

    private static SKBitmap Pixels(CanvasPreviewFrame frame)
    {
        using var surface = SKSurface.Create(new SKImageInfo(3840, 2160, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent); frame.Pixels.Draw(surface.Canvas);
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }

    private static bool Pick(EditorController editor, PointD point)
    {
        editor.Session.SetSelection([]);
        editor.Pointer(new(OverlayPointerKind.Down, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
        editor.Pointer(new(OverlayPointerKind.Up, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
        return editor.Session.Selection.Count > 0;
    }
}
