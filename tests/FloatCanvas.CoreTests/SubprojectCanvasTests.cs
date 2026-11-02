using FloatCanvas.Core;

internal static class SubprojectCanvasTests
{
    internal static void Run(Action<bool, string> check)
    {
        Resources(check); Editing(check); DefinitionDraft(check); RefreshOrigin(check);
        SelectionPreview(check, "rotated", Affine2D.Rotation(24), false);
        SelectionPreview(check, "mirrored", Affine2D.Scale(-1.2, .8), false);
        SelectionPreview(check, "sheared tiny", new(1.2, .4, .15, .9, 0, 0), true);
    }

    private static void Resources(Action<bool, string> check)
    {
        var group = new CanvasGroup { PreviewCanvas = Preview("group-image"), OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var definition = new PrefabDefinition { PreviewCanvas = Preview("definition-image") };
        var instance = new PrefabInstance { DefinitionId = definition.Id };
        var document = new SceneDocument { Groups = [group], Definitions = [definition], Instances = [instance], RootOrder = [group.Id, instance.Id],
            RootPreviewCanvases = new() { [RootCanvasKind.Foreground] = Preview("foreground-image") } };
        var map = document.ReferencedAssets.ToDictionary(id => id, id => "copy-" + id);
        var clone = document.Clone(assetIdMap: map);
        check(clone.ReferencedAssets.Order().SequenceEqual(map.Values.Order()) && group.PreviewCanvas!.Background!.AssetId == "group-image",
            "all preview background assets participate in deep clone/remapping without mutating their author");
        var imported = PrefabClosure.PrepareImport(new(), document, document.Definitions);
        check(imported.ReferencedAssetIds.Contains("definition-image") && imported.ReferencedAssetIds.Contains("group-image"),
            "cross-document dependency closure retains definition and nested group preview assets");
        var package = new PrefabPackage { EntryDefinitionId = definition.Id, Definitions = [definition] };
        check(package.ReferencedAssets.SequenceEqual(["definition-image"]), "a preview-only image remains a package resource");
    }

    private static void Editing(Action<bool, string> check)
    {
        var group = new CanvasGroup { Transform = Affine2D.Translation(400, 200).Compose(Affine2D.Rotation(20)) };
        var child = Box(); child.GroupId = group.Id;
        var main = Box(); var foreground = Box();
        var document = new SceneDocument { Groups = [group], Elements = [child, main, foreground] };
        SceneCanvasRoots.Assign(document, foreground.Id, RootCanvasKind.Foreground);
        document.Canvas.Projection.Enabled = true; document.Canvas.Grid.FlatTranslation = new(81, 62);
        var session = new EditorSession(document); session.SetEditScope(group.Id);
        var neutral = session.GetEditingSubprojectCanvas();
        check(!neutral.Canvas.Projection.Enabled && neutral.Canvas.Grid.FlatTranslation == default && group.PreviewCanvas is null,
            "missing group preview is neutral and reading does not create persisted metadata");
        var placement = group.Transform;
        session.ChangeEditingSubprojectCanvas("independent origin", preview => { preview.Origin = new(8, 13); preview.LayerOpacity = .4; });
        var world = new SceneGraph(session.Document).ToWorld(session.Document.Elements.Single(element => element.Id == child.Id));
        var previewPoint = session.GetEditingSubprojectPlacement().PlaneTransform.TransformPoint(world.Transform.TransformPoint(default));
        check(previewPoint.DistanceTo(new PointD(-8, -13)) < 1e-7 && session.Document.Groups.Single().Transform == placement && session.UndoCount == 1,
            "independent origin rebases only preview mapping and leaves the host placement unchanged");
        check(session.Undo() && session.Document.Groups.Single().PreviewCanvas is null && session.Redo() && session.GetEditingSubprojectCanvas().LayerOpacity == .4,
            "group preview settings are one complete undoable author edit");
        session.SetEditScope(null); session.SetRootCanvas(RootCanvasKind.Foreground);
        session.ChangeEditingSubprojectCanvas("foreground perspective", preview => preview.Canvas.Projection.Enabled = true);
        check(session.GetEditingSubprojectCanvas().Canvas.Projection.Enabled && CanvasMapping.Create(session.Document, RootCanvasKind.Foreground).PlaneToScreen == Projective2D.Identity,
            "foreground can retain an independent perspective while remaining screen-plane in its root project");
        var mainOnly = SceneCanvasRoots.ExtractRootContent(session.Document, RootCanvasKind.Main);
        check(mainOnly.Elements.Count == 2 && mainOnly.Elements.All(element => element.Id != foreground.Id) && mainOnly.RootCanvasAssignments.Count == 0 && mainOnly.RootPreviewCanvases.Count == 0,
            "root extraction includes only the chosen authored root and its canvas header");
        var detached = new EditorSession(independentCanvasOrigin: new(2, 3));
        detached.ChangeEditingSubprojectCanvas("origin", preview => preview.Origin = new(40, 50));
        check(detached.UndoCount == 1 && detached.Undo() && detached.GetRootSubprojectCanvas().Origin == new PointD(2, 3) && detached.Redo() && detached.GetRootSubprojectCanvas().Origin == new PointD(40, 50),
            "detached entry origin participates in document history without adding a scene wire authority");
    }

    private static void DefinitionDraft(Action<bool, string> check)
    {
        var definition = new PrefabDefinition { Pivot = new(3, 4), Graph = new() { Elements = [Box()] } };
        var owner = new EditorSession(new() { Definitions = [definition] }); owner.Document.Canvas.Projection.Enabled = true;
        var draft = owner.BeginPrefabEdit(definition.Id);
        check(!draft.Session.Document.Canvas.Projection.Enabled && draft.Session.GetRootSubprojectCanvas().Origin == definition.Pivot,
            "legacy definition draft uses neutral settings and its pivot as initial independent origin instead of host perspective");
        draft.Session.ChangeEditingSubprojectCanvas("own preview", preview => { preview.Canvas.BackgroundColor = 0xff123456; preview.Origin = new(10, 20); });
        var revision = definition.Revision; owner.ApplyPrefabEdit(owner.PreparePrefabEdit(draft));
        var published = owner.Document.Definitions.Single();
        check(published.PreviewCanvas?.Origin == new PointD(10, 20) && published.PreviewCanvas.Canvas.BackgroundColor == 0xff123456 && published.Revision != revision && owner.Document.Canvas.BackgroundColor == 0,
            "publishing an independent definition preview updates its metadata/revision and never the host canvas");
        check(owner.Undo() && owner.Document.Definitions.Single().PreviewCanvas is null, "definition preview publication is undoable with the graph");
    }

    private static void SelectionPreview(Action<bool, string> check, string name, Affine2D basis, bool tiny)
    {
        var group = new CanvasGroup { Transform = Affine2D.Translation(230, 170).Compose(basis), PreviewCanvas = new() { Origin = new(7, 9) } };
        var element = Box(); element.GroupId = group.Id; element.Transform = Affine2D.Translation(30, 40);
        if (tiny) { element.Width = element.Height = 1; element.Style.StrokeWidth = 0; }
        var session = new EditorSession(new() { Groups = [group], Elements = [element] }); session.SetEditScope(group.Id); session.SetSelection([element.Id]);
        var unnormalized = session.CreateSelectionSnapshot(false); var snapshot = session.CreateSelectionSnapshot();
        var settings = session.BuildSelectionPreviewSettings(snapshot); var display = new SceneDocument(); settings.ApplyTo(display);
        var before = session.GetEditingSubprojectPlacement(); var original = SceneEvaluation.Build(unnormalized).RenderDocument.Elements.Single();
        var after = SceneEvaluation.Build(snapshot).RenderDocument.Elements.Single();
        var oldHeader = new SceneDocument(); session.GetEditingSubprojectCanvas().ApplyTo(oldHeader);
        var expected = CanvasMapping.CreateView(oldHeader, before).ElementToScreen(original);
        var actual = CanvasMapping.Create(display).ElementToScreen(after);
        check(new[] { default(PointD), new PointD(40, 20), new PointD(20, 10) }.All(point => expected.TransformPoint(point).DistanceTo(actual.TransformPoint(point)) < 1e-7),
            name + " normalized selection preview composes scope placement and origin exactly once");
        check(CanvasCalibration.TryGridToScreen(settings.Canvas.Projection.Columns, settings.Canvas.Projection.Rows, settings.Canvas.Projection.Quad, out _, allowMirrored: true),
            name + " selection uses a nondegenerate calibrated view reference independent of tiny ink bounds");
        var created = session.CreatePrefabFromSelection("selection");
        check(session.Document.Definitions.Single(item => item.Id == created.DefinitionId).PreviewCanvas is not null,
            name + " new shared definitions retain the normalized independent selection preview");
    }

    private static void RefreshOrigin(Action<bool, string> check)
    {
        var entry = new PrefabDefinition { Graph = new() { Elements = [Box()] }, PreviewCanvas = new() { Origin = new(10, 20) } };
        var package = new PrefabPackage { EntryDefinitionId = entry.Id, Definitions = [entry] };
        var source = new ExternalPrefabSource { Locator = "C:/fixture/source.floatcanvas", SourceResourceId = package.ResourceId,
            EntryKind = PrefabSourceKind.ProjectContent, ImportOrigin = new(100, 200), LastAcceptedFileSha256 = new string('a', 64) };
        var session = new EditorSession(); var imported = session.ImportPrefab(package, session.Assets, LayerPlacement.Identity, source);
        var accepted = source.Clone(); accepted.ImportOrigin += new PointD(40, -30); accepted.LastAcceptedFileSha256 = new string('b', 64);
        session.ApplyPrefabEdit(session.PreparePrefabRefresh(imported.DefinitionId, package, session.Assets, accepted));
        var updated = session.Document.Definitions.Single();
        check(updated.PreviewCanvas!.Origin == new PointD(50, -10) && updated.Pivot == default && updated.Graph.Elements.Single().Transform.TransformPoint(default) == new PointD(40, -30),
            "linked refresh rebases independent preview origin alongside retained author geometry without changing the pivot");
        var draft = session.BeginPrefabEdit(updated.Id); var header = new SceneDocument(); draft.Session.GetRootSubprojectCanvas().ApplyTo(header);
        var mapping = CanvasMapping.CreateView(header, draft.Session.GetEditingSubprojectPlacement());
        check(mapping.ElementToScreen(draft.Session.Document.Elements.Single()).TransformPoint(default).DistanceTo(new(-10, -20)) < 1e-7,
            "linked refresh preserves independent preview pixels while retaining the host placement");
    }

    private static SubprojectCanvasSettings Preview(string assetId) => new() { Background = new() { Kind = ElementKind.Image, AssetId = assetId, Width = 100, Height = 100 } };
    private static CanvasElement Box() => new() { Kind = ElementKind.Rectangle, Width = 40, Height = 20 };
}
