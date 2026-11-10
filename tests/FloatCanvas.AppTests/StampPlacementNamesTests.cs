using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

internal static class StampPlacementNamesTests
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        string language = L.Preference;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            {
                L.Initialize(locale);
                var source = new EditorSession(new SceneDocument
                {
                    Viewport = new(0, 0, 800, 600),
                    Elements = [Box("成员 A", 20), Box("成员 B", 90)]
                });
                source.SetSelection(source.Document.Elements.Select(element => element.Id));
                string original = JsonSerializer.Serialize(source.Document);
                string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-StampNames", Guid.NewGuid().ToString("N"));
                var library = new StampLibrary(profile); await library.LoadAsync();
                var editable = await library.SaveSelectionAsync(source, "印章A");
                var loaded = await library.LoadStampAsync(editable.Id);
                check(locale + " loaded editable stamp retains its catalog name outside the source graph", loaded.Name == "印章A" && JsonSerializer.Serialize(source.Document) == original);
                var target = Empty(); StampLibrary.Place(loaded, target, new(300, 200));
                var wrapper = target.Document.Groups.Single();
                check(locale + " a multi-layer stamp creates one folder named after the stamp", wrapper.Name == "印章A" && target.Document.Elements.All(element => element.GroupId == wrapper.Id) && target.UndoCount == 1);
                check(locale + " naming the placed folder preserves its member names", target.Document.Elements.Select(element => element.Name).SequenceEqual(new[] { "成员 A", "成员 B" }));
                target.RenameLayer(wrapper.Id, "手动命名");
                check(locale + " placed stamp groups support rename and undo", target.Document.Groups.Single().Name == "手动命名" && target.Undo() && target.Document.Groups.Single().Name == "印章A");
                check(locale + " one undo removes the complete named stamp and redo restores its name", target.Undo() && target.Document.Groups.Count == 0 && target.Document.Elements.Count == 0 && target.Redo() && target.Document.Groups.Single().Name == "印章A");
                string project = Path.Combine(profile, "placed.floatcanvas"); await new ProjectStore().SaveAsync(project, target.Document, target.Assets);
                check(locale + " placed folder name survives project serialization", (await new ProjectStore().LoadAsync(project)).Document.Groups.Single().Name == "印章A");

                source.SetSelection(source.Document.Elements.Select(element => element.Id));
                var sourceGroup = source.GroupSelection("源组合")!.Value; source.SetSelection([sourceGroup]);
                var single = await library.SaveSelectionAsync(source, "单组印章");
                var singleLoaded = await library.LoadStampAsync(single.Id);
                string singleOriginal = JsonSerializer.Serialize(singleLoaded.Document);
                var singleTarget = Empty(); StampLibrary.Place(singleLoaded, singleTarget, new(250, 180));
                check(locale + " a single source group uses the stamp name without an extra wrapper", singleTarget.Document.Groups.Count == singleLoaded.Document.Groups.Count && new SceneGraph(singleTarget.Document).FindGroup(new SceneGraph(singleTarget.Document).EnumerateChildren(null).Single())?.Name == "单组印章" && singleTarget.UndoCount == 1);
                check(locale + " placement never renames the immutable stamp source", JsonSerializer.Serialize(singleLoaded.Document) == singleOriginal);

                var shared = await library.SaveSelectionAsync(source, "原共享印章", StampSaveMode.SharedPrefab);
                await library.RenameAsync(shared.Id, "新共享印章");
                var sharedLoaded = await library.LoadStampAsync(shared.Id); var sharedTarget = Empty();
                StampLibrary.Place(sharedLoaded, sharedTarget, new(250, 180));
                check(locale + " renamed shared stamps name the new instance from the current catalog", sharedTarget.Document.Instances.Single().Name == "新共享印章" && sharedTarget.Document.Definitions.Single().Name == "原共享印章" && sharedTarget.UndoCount == 1);
                StampLibrary.Place(sharedLoaded, sharedTarget, new(400, 180), "当前印章名");
                check(locale + " a cached shared definition keeps one definition while the next placement uses the current name", sharedTarget.Document.Definitions.Count == 1 && sharedTarget.Document.Instances.Last().Name == "当前印章名" && sharedTarget.UndoCount == 2);
                var cachedTarget = Empty(); StampLibrary.Place(loaded, cachedTarget, new(250, 180), "改名后的印章A");
                check(locale + " a cached editable stamp accepts the catalog's current name at placement", cachedTarget.Document.Groups.Single().Name == "改名后的印章A");

                var raster = await library.SaveSelectionAsync(source, "图片印章", StampSaveMode.RasterMerge);
                var rasterLoaded = await library.LoadStampAsync(raster.Id); var rasterTarget = Empty();
                StampLibrary.Place(rasterLoaded, rasterTarget, new(300, 200));
                check(locale + " a raster stamp names its image layer without inventing a folder", rasterTarget.Document.Elements.Single().Name == "图片印章" && rasterTarget.Document.Groups.Count == 0 && rasterTarget.UndoCount == 1);
                var billboardTarget = Empty(); billboardTarget.Document.Canvas.Projection = CanvasCalibration.CreateInitial(billboardTarget.Document.Viewport);
                billboardTarget.Document.Canvas.Projection.Enabled = true;
                StampLibrary.PlaceBillboardRaster(rasterLoaded, billboardTarget, null, new(300, 200), "立牌印章");
                check(locale + " billboard image stamps also use the current stamp name in one undo", billboardTarget.Document.Elements.Single() is { Name: "立牌印章", IsBillboard: true } && billboardTarget.UndoCount == 1);

                var defaults = Empty(); defaults.CreatePopulatedGroup(L.Format("Layers.NumberedGroup", 1));
                var next = defaults.CreatePopulatedGroup();
                check(locale + " newly created groups get a numbered name avoiding existing group names", defaults.Document.Groups.Single(group => group.Id == next).Name == L.Format("Layers.NumberedGroup", 2));
                defaults.SetEditScope(next); var nested = defaults.CreatePopulatedGroup();
                check(locale + " numbered defaults remain distinct inside a nested scope", defaults.Document.Groups.Single(group => group.Id == nested).Name == L.Format("Layers.NumberedGroup", 3) && defaults.Document.Groups.Single(group => group.Id == nested).ParentId == next);
            }
        }
        finally { L.Initialize(language); }
    }

    private static EditorSession Empty() => new(new SceneDocument { Viewport = new(0, 0, 800, 600) });
    private static CanvasElement Box(string name, double x) => new()
    { Kind = ElementKind.Rectangle, Name = name, Width = 40, Height = 30, Transform = Affine2D.Translation(x, 40), Style = new() { StrokeWidth = 0 } };
}
