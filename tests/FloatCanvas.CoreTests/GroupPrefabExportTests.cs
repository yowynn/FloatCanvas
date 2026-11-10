using System.Text.Json;
using FloatCanvas.Core;

internal static class GroupPrefabExportTests
{
    internal static void Run(Action<bool, string> check)
    {
        NestedLocalContents(check);
        SingleLayerAndForeignTransaction(check);
        LegacyOrder(check);
    }

    private static void NestedLocalContents(Action<bool, string> check)
    {
        var assets = new AssetLibrary(); string image = assets.Add([1, 2, 3], "image/png"), unused = assets.Add([4, 5, 6], "image/png");
        var a = new CanvasElement { Name = "Plane", Kind = ElementKind.Rectangle, Width = 40, Height = 20,
            Transform = Affine2D.Translation(8, 9).Compose(Affine2D.Rotation(13)), Style = new() { Fill = 0x7F102030, Stroke = 0xFF304050, StrokeWidth = 2, Opacity = .6 } };
        var b = new CanvasElement { Name = "Billboard", Kind = ElementKind.Image, Width = 30, Height = 50, AssetId = image,
            Transform = Affine2D.Translation(7, 11), IsBillboard = true, BillboardAnchor = new(4, 8), BillboardHeight = 17,
            ImageCrop = new(.1, .2, .7, .6), Visible = false, Locked = true, Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(.2, .2, .3, .4) }] };
        var nested = new CanvasGroup { Name = "Nested", Transform = Affine2D.Translation(9, 15).Compose(Affine2D.Scale(.7, 1.2)),
            ElevationScale = .8, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [b.Id] }; b.GroupId = nested.Id;
        var sourceLeaf = new CanvasElement { Name = "Shared image", Kind = ElementKind.Image, AssetId = image, Width = 20, Height = 10 };
        var shared = new PrefabDefinition { Name = "Shared", Graph = new() { Elements = [sourceLeaf], RootOrder = [sourceLeaf.Id] } };
        shared.Source = new() { Locator = "external.floatprefab", IsRelative = true, SourceResourceId = Guid.NewGuid(), EntryKind = PrefabSourceKind.PrefabDefinition,
            EntryDefinitionId = Guid.NewGuid(), LastAcceptedFileSha256 = new string('A', 64), ImportedDefinitionIds = new() { [Guid.NewGuid()] = shared.Id } };
        var inner = new PrefabInstance { DefinitionId = shared.Id, Name = "Dependent instance", Transform = Affine2D.Translation(20, 4), ElevationScale = 1.1 };
        var entry = new PrefabDefinition { Name = "External parent", Graph = new() { Instances = [inner], RootOrder = [inner.Id] },
            Source = new() { Locator = "C:/fixture/parent.floatcanvas", SourceResourceId = Guid.NewGuid(), EntryKind = PrefabSourceKind.ProjectContent,
                LastAcceptedFileSha256 = new string('B', 64), OriginClosure = [new(Guid.NewGuid(), PrefabSourceKind.ProjectContent)] } };
        var instance = new PrefabInstance { DefinitionId = entry.Id, Name = "Nested shared", Transform = Affine2D.Translation(60, 20).Compose(Affine2D.Rotation(-7)), ElevationScale = 1.3 };
        var liveImage = new CanvasElement { Name = "Window source", Kind = ElementKind.Image, AssetId = image, Width = 15, Height = 9,
            Transform = Affine2D.Translation(20, 50), WindowSource = new() { BindingIntent = Guid.NewGuid(), TitleHint = "Source hint", ProcessNameHint = "process", WindowClassHint = "class" } };
        var group = new CanvasGroup { Name = "印章 A", Transform = Affine2D.Translation(15, 22).Compose(Affine2D.Rotation(19)), ElevationScale = 2.5,
            OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [a.Id, nested.Id, instance.Id, liveImage.Id] };
        a.GroupId = nested.ParentId = instance.ParentId = liveImage.GroupId = group.Id;
        var outside = new CanvasElement { Name = "Outside", Kind = ElementKind.Rectangle, Width = 10, Height = 10 };
        var ancestor = new CanvasGroup { Name = "Ancestor", Transform = Affine2D.Translation(100, -40).Compose(Affine2D.Rotation(21)).Compose(Affine2D.Scale(1.4, .8)),
            ElevationScale = 1.7, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [group.Id, outside.Id] };
        group.ParentId = outside.GroupId = ancestor.Id;
        var unusedDefinition = new PrefabDefinition { Name = "Unused", Graph = new() { Elements = [new() { Kind = ElementKind.Image, AssetId = unused, Width = 10, Height = 10 }] } };
        var owner = new EditorSession(new() { Elements = [a, b, liveImage, outside], Groups = [ancestor, group, nested], Instances = [instance],
            RootOrder = [ancestor.Id], Definitions = [shared, entry, unusedDefinition], LayerOpacity = .3 }, assets);
        owner.Execute("earlier authored change", document => document.Elements.Single(item => item.Id == outside.Id).Width = 11);
        owner.Execute("later authored change", document => document.Elements.Single(item => item.Id == outside.Id).Width = 12); owner.Undo();
        owner.SetEditScope(ancestor.Id); owner.SetSelection([outside.Id]);
        var graph = new SceneGraph(owner.Document); var members = graph.DescendantNodeIds(group.Id).ToHashSet();
        var expected = SceneEvaluation.Build(owner.Document).Leaves.Where(leaf => members.Contains(leaf.HostNodeId)).Select(leaf => leaf.RenderElement).ToArray();
        var originalDocument = owner.Document;
        string before = Json(owner.Document); int history = owner.UndoCount, redo = owner.RedoCount; long revision = owner.SceneRevision, content = owner.ContentVersion, scopeVersion = owner.EditScopeVersion;
        var selected = owner.Selection.ToHashSet(); var package = owner.ExportGroupPrefab(group.Id);
        check(ReferenceEquals(owner.Document, originalDocument) && Json(owner.Document) == before && history == 1 && owner.UndoCount == history && redo == 1 && owner.RedoCount == redo && owner.SceneRevision == revision && owner.ContentVersion == content &&
            owner.EditScopeVersion == scopeVersion && owner.EditScopeId == ancestor.Id && selected.SetEquals(owner.Selection), "group export does not alter document name scope selection history or revision");
        var exported = package.Definitions.Single(item => item.Id == package.EntryDefinitionId);
        check(exported.Name == group.Name && package.ResourceId != owner.Document.ResourceId && exported.Id != group.Id,
            "ordinary group exports a separate portable resource and named entry definition");
        check(exported.Graph.Groups.All(item => item.Id != group.Id && item.Id != ancestor.Id) && exported.Graph.RootOrder!.SequenceEqual(group.ChildOrder!),
            "exported definition exposes direct members without an extra group wrapper");
        check(exported.Graph.Elements.Single(item => item.Id == a.Id).GroupId is null && exported.Graph.Groups.Single(item => item.Id == nested.Id).ParentId is null &&
            exported.Graph.Instances.Single().ParentId is null && exported.Graph.Elements.Single(item => item.Id == b.Id).GroupId == nested.Id,
            "only immediate member parents become root while all deeper ownership stays intact");
        check(exported.Graph.Elements.Single(item => item.Id == a.Id).Transform == a.Transform && exported.Graph.Groups.Single().Transform == nested.Transform &&
            exported.Graph.Groups.Single().ElevationScale == nested.ElevationScale && exported.Graph.Instances.Single().Transform == instance.Transform,
            "group and ancestor placement are not baked into the exported local geometry");
        check(exported.Graph.Elements.Single(item => item.Id == b.Id).Visible == b.Visible && exported.Graph.Elements.Single(item => item.Id == b.Id).Locked == b.Locked &&
            Json(exported.Graph.Elements.Single(item => item.Id == b.Id).Style) == Json(b.Style), "export retains each member's own visibility lock and appearance");
        check(package.Definitions.Count == 3 && package.Definitions.All(item => item.Id != unusedDefinition.Id) &&
            package.Definitions.Where(item => item.Source is not null).All(item => item.Source!.Locator is null && !item.Source.IsRelative && item.Source.ImportedDefinitionIds.Count == 0),
            "export contains only the reachable shared and external dependency closure without active file paths");
        check(package.ReferencedAssets.SequenceEqual([image]) && assets.Count == 2 && assets.Get(image).Data.Span.SequenceEqual(new byte[] { 1, 2, 3 }),
            "export retains exact required image resource ids and leaves the owner's resources untouched");
        var exportedSource = exported.Graph.Elements.Single(item => item.Id == liveImage.Id);
        check(exportedSource.WindowSource?.BindingIntent == liveImage.WindowSource!.BindingIntent && !ReferenceEquals(exportedSource.WindowSource, liveImage.WindowSource) && exportedSource.AssetId == image,
            "window source settings and fallback assets remain owned authored data in the exported graph");
        var placement = graph.NodeWorldPlacement(group.Id);
        var placed = new PrefabInstance { DefinitionId = exported.Id, Transform = placement.PlaneTransform, ElevationScale = placement.ElevationScale,
            Visible = graph.IsVisible(group.Id), Locked = graph.IsLocked(group.Id) };
        var actual = SceneEvaluation.Build(new() { Instances = [placed], RootOrder = [placed.Id], Definitions = package.Definitions.Select(item => item.Clone()).ToList() }).Leaves.Select(leaf => leaf.RenderElement).ToArray();
        check(expected.Length == actual.Length && expected.Zip(actual).All(pair => pair.First.Name == pair.Second.Name && Near(pair.First.Transform, pair.Second.Transform) &&
            pair.First.BillboardAnchor.DistanceTo(pair.Second.BillboardAnchor) < 1e-7 && Math.Abs(pair.First.BillboardHeight - pair.Second.BillboardHeight) < 1e-7 &&
            pair.First.Visible == pair.Second.Visible && pair.First.Locked == pair.Second.Locked && Json(pair.First.Style) == Json(pair.Second.Style)),
            "placing the package at the original group world placement reproduces ancestor transforms mixed billboards and draw order");
        exported.Graph.Elements[0].Name = "mutated exported copy"; exported.Graph.Groups[0].ChildOrder!.Clear();
        package.Definitions.Single(item => item.Id == shared.Id).Graph.Elements[0].Width = 99;
        check(Json(owner.Document) == before, "mutating exported direct members and dependencies cannot mutate the owner");
    }

    private static void SingleLayerAndForeignTransaction(Action<bool, string> check)
    {
        var group = new CanvasGroup { Name = "Single-layer group", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [], Transform = Affine2D.Translation(80, 50) };
        var content = new CanvasElement { Kind = ElementKind.Rectangle, Width = 8, Height = 8, GroupId = group.Id }; group.ChildOrder!.Add(content.Id);
        var owner = new EditorSession(new() { Elements = [content], Groups = [group], RootOrder = [group.Id] }); owner.SetEditScope(group.Id);
        string before = Json(owner.Document); var package = owner.ExportGroupPrefab(group.Id); var entry = package.Definitions.Single();
        check(entry.Name == group.Name && entry.Graph.Elements.Count == 1 && entry.Graph.Groups.Count == 0 && entry.Graph.Instances.Count == 0 && entry.Graph.RootOrder is { Count: 1 },
            "a single-layer ordinary group exports a valid definition without creating a wrapper");
        check(Json(owner.Document) == before && owner.EditScopeId == group.Id && owner.UndoCount == 0, "single-layer group export leaves the current group and author history unchanged");
        owner.BeginTransaction("foreign operation"); long transaction = owner.TransactionId;
        _ = owner.ExportGroupPrefab(group.Id);
        check(owner.IsTransactionActive && owner.TransactionId == transaction && Json(owner.Document) == before && owner.UndoCount == 0,
            "read-only group export never cancels or replaces an active foreign transaction");
        owner.Cancel();
        bool rejected = false; before = Json(owner.Document);
        try { _ = owner.ExportGroupPrefab(Guid.NewGuid()); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && before == Json(owner.Document) && owner.UndoCount == 0, "missing group export rejects without inventing content or changing the owner");
    }

    private static void LegacyOrder(Action<bool, string> check)
    {
        var group = new CanvasGroup { Name = "Legacy parent", Transform = Affine2D.Translation(30, 20) };
        var nested = new CanvasGroup { Name = "Interleaved child", ParentId = group.Id, Transform = Affine2D.Rotation(16) };
        CanvasElement Leaf(string name, Guid? parent) => new() { Name = name, GroupId = parent, Kind = ElementKind.Rectangle, Width = 10, Height = 10 };
        var a = Leaf("A", group.Id); var b = Leaf("B", nested.Id); var outside = Leaf("Outside", null); var c = Leaf("C", group.Id); var d = Leaf("D", nested.Id);
        var owner = new EditorSession(new() { Elements = [a, b, outside, c, d], Groups = [group, nested] });
        string before = Json(owner.Document); var package = owner.ExportGroupPrefab(group.Id); var entry = package.Definitions.Single();
        check(entry.Graph.RootOrder!.SequenceEqual([a.Id, b.Id, c.Id, d.Id]) && entry.Graph.Groups.Single().OrderMode == GroupOrderMode.LegacyPassThrough,
            "legacy export removes outside paint tokens while retaining interleaved member order");
        var instance = new PrefabInstance { DefinitionId = entry.Id, Transform = group.Transform };
        var expected = SceneEvaluation.Build(owner.Document).Leaves.Where(leaf => leaf.RenderElement.Name != "Outside").Select(leaf => leaf.RenderElement).ToArray();
        var actual = SceneEvaluation.Build(new() { Instances = [instance], Definitions = package.Definitions, RootOrder = [instance.Id] }).Leaves.Select(leaf => leaf.RenderElement).ToArray();
        check(expected.Select(item => item.Name).SequenceEqual(actual.Select(item => item.Name)) && expected.Zip(actual).All(pair => Near(pair.First.Transform, pair.Second.Transform)),
            "legacy interleaved group export reproduces its original member geometry and paint sequence");
        check(before == Json(owner.Document) && owner.UndoCount == 0, "legacy group export does not consolidate or edit the original group");
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
    private static bool Near(Affine2D left, Affine2D right) => left.TransformPoint(default).DistanceTo(right.TransformPoint(default)) < 1e-7 &&
        left.TransformPoint(new(1, 1)).DistanceTo(right.TransformPoint(new(1, 1))) < 1e-7;
}
