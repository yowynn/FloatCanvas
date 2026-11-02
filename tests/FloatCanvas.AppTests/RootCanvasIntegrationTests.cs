using System.Reflection;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class RootCanvasIntegrationTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("dark");
        var document = new SceneDocument { Viewport = new(0, 0, 800, 600) };
        document.Canvas.Projection = CanvasCalibration.CreateInitial(document.Viewport);
        document.Canvas.Projection.Enabled = true;
        document.Canvas.Projection.Matrix = Projective2D.FromAffine(Affine2D.Translation(30, 40).Compose(Affine2D.Scale(1.2, .8)));
        document.Canvas.Projection.Quad = EditorController.Corners(CanvasProjectionGeometry.ResolvePlaneBounds(document.Canvas.Projection))
            .Select(document.Canvas.Projection.Matrix.TransformPoint).ToList();
        document.Canvas.Grid.FlatTranslation = new(90, 70);
        var session = new EditorSession(document);
        var editor = new EditorController(session) { Tool = EditorTool.Rectangle };
        editor.Style.Fill = 0xFF5577CC;
        foreach (var root in new[] { RootCanvasKind.Main, RootCanvasKind.Foreground, RootCanvasKind.Background })
        {
            session.SetRootCanvas(root);
            editor.Pointer(new(OverlayPointerKind.Down, new(160, 130), OverlayMouseButton.Left, OverlayModifiers.None));
            editor.Pointer(new(OverlayPointerKind.Move, new(280, 230), OverlayMouseButton.Left, OverlayModifiers.None));
            editor.Pointer(new(OverlayPointerKind.Up, new(280, 230), OverlayMouseButton.Left, OverlayModifiers.None));
            var created = session.Document.Elements.Last();
            var bounds = editor.CurrentCanvasMapping.ProjectedBounds(created, includeStroke: false);
            check(root + " pointer creation uses its own camera and persists its root", SceneCanvasRoots.Resolve(session.Document, created.Id) == root &&
                Math.Abs(bounds.Left - 160) < 1e-5 && Math.Abs(bounds.Top - 130) < 1e-5 && Math.Abs(bounds.Width - 120) < 1e-5);
        }
        var window = new LayersWindow(() => session);
        try
        {
            var choice = (SegmentedChoice)typeof(LayersWindow).GetField("rootCanvasChoice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            foreach (var root in new[] { RootCanvasKind.Main, RootCanvasKind.Foreground, RootCanvasKind.Background })
            {
                window.PrepareForShow(); choice.SelectedValue = root; window.PrepareForShow();
                check(root + " layer navigation lists only the selected root", session.ActiveRootCanvas == root && window.VisibleNodes.Count == 1 &&
                    window.VisibleNodes.All(row => SceneCanvasRoots.Resolve(session.Document, row.Id) == root));
                editor.Tool = EditorTool.Select;
                editor.Pointer(new(OverlayPointerKind.Down, new(210, 180), OverlayMouseButton.Left, OverlayModifiers.None));
                editor.Pointer(new(OverlayPointerKind.Up, new(210, 180), OverlayMouseButton.Left, OverlayModifiers.None));
                check(root + " picking ignores overlapping content on the other roots", session.Selection.Count == 1 &&
                    session.Selection.All(id => SceneCanvasRoots.Resolve(session.Document, id) == root));
            }
        }
        finally { window.Close(); }
        var request = new SnapshotCaptureRequest(session, 1);
        session.SetRootCanvas(RootCanvasKind.Foreground);
        check("switching roots invalidates an asynchronous capture without dirtying the scene", !request.MatchesContent(session) && session.UndoCount == 3);
        using var renderer = new SceneRenderer();
        using (var edit = new InlineTextEdit(session, null, new(330, 210), new(), renderer))
        {
            edit.Input.Text = "foreground";
            check("inline foreground text has a screen-plane caret and root slot", SceneCanvasRoots.Resolve(session.Document, edit.Target.Id) == RootCanvasKind.Foreground &&
                edit.LocalToScreen.TransformPoint(default).DistanceTo(new(330, 210)) < 1e-6);
            edit.Finish(true);
        }
        using var image = SKSurface.Create(new SKImageInfo(8, 8)); image.Canvas.Clear(SKColors.Blue);
        using var imageSnapshot = image.Snapshot();
        using var png = imageSnapshot.Encode(SKEncodedImageFormat.Png, 100);
        var normalized = new NormalizedImage(png.ToArray(), 8, 8);
        ImageImportCommand.Insert(session, [normalized], new(100, 100, 300, 240));
        check("image import inherits the foreground root", session.Selection.All(id => SceneCanvasRoots.Resolve(session.Document, id) == RootCanvasKind.Foreground));
        var prepared = CanvasContentImport.PrepareAsync(session, [], [normalized], new(100, 100, 300, 240), null, 1, null, default).GetAwaiter().GetResult();
        int count = session.Document.Elements.Count;
        session.SetRootCanvas(RootCanvasKind.Background);
        check("a prepared image batch cannot publish into a different root", !prepared.TryCommit(session) && session.Document.Elements.Count == count);
        session.SetRootCanvas(RootCanvasKind.Foreground);
        prepared = CanvasContentImport.PrepareAsync(session, [], [normalized], new(100, 100, 300, 240), null, 1, null, default).GetAwaiter().GetResult();
        check("a prepared image batch publishes its foreground membership atomically", prepared.TryCommit(session) &&
            session.Selection.All(id => SceneCanvasRoots.Resolve(session.Document, id) == RootCanvasKind.Foreground));
        var snapshotImage = SnapshotImageCommand.Insert(session, new(png.ToArray(), new(250, 180, 8, 8), true));
        check("foreground screenshot placement remains in screen coordinates", SceneCanvasRoots.Resolve(session.Document, snapshotImage.Id) == RootCanvasKind.Foreground &&
            editor.CurrentCanvasMapping.ProjectedBounds(snapshotImage, false).Center.DistanceTo(new(254, 184)) < 1e-6);
        var freezer = new CanvasPreviewScene(); var frozen = freezer.Capture(session.Document, 1, 1, 1, -1, -1, false);
        check("preview freezing retains root membership", frozen.RootCanvasAssignments.OrderBy(pair => pair.Key).SequenceEqual(session.Document.RootCanvasAssignments.OrderBy(pair => pair.Key)));
        var mainPackage = PrefabLinkResolver.ProjectContent(new(session.Document, session.Assets), "mixed roots").Package;
        var importRoot = new PrefabInstance { DefinitionId = mainPackage.EntryDefinitionId };
        var imported = SceneEvaluation.Build(new SceneDocument { Instances = [importRoot], Definitions = mainPackage.Definitions, RootOrder = [importRoot.Id] });
        check("placing a complete three-root project extracts only Main author nodes", imported.Leaves.Count == SceneEvaluation.Build(session.Document).Leaves.Count(leaf => leaf.RootCanvas == RootCanvasKind.Main));
        var selected = session.CreateSelectionSnapshot(normalizeOrigin: false);
        var shared = (PrefabPackage)typeof(StampLibrary).GetMethod("SharedPackage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [selected, session.Assets, "foreground stamp", Guid.NewGuid(), session.BuildSelectionPreviewSettings(selected, normalizeOrigin: false)])!;
        check("a selected foreground subtree remains exportable as a local shared stamp", shared.Definitions.Count > 0);
        var editingSource = new SceneDocument { Elements = [new() { Kind = ElementKind.Rectangle, Width = 40, Height = 30 }] };
        var picking = CanvasPreviewRenderer.EditingDocument(SceneEvaluation.Build(editingSource), session.Document, LayerPlacement.Identity, RootCanvasKind.Foreground);
        check("root-project editing of a foreground prefab keeps source identity and screen-space picking", picking.Elements.Count == 1 &&
            CanvasMapping.Create(picking).PlaneToScreen == Projective2D.Identity && picking.RootCanvasAssignments.Count == 0);
        var safety = CanvasSafetyResolution.AffectedIds(session.Document);
        check("main perspective safety never relocates a foreground or background root", safety.All(id => SceneCanvasRoots.Resolve(session.Document, id) == RootCanvasKind.Main));
        AppTheme.Apply("light");
    }
}
