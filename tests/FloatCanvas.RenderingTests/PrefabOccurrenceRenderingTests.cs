using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class PrefabOccurrenceRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        Identity(check);
        foreach (bool perspective in new[] { false, true })
        foreach (bool depth in new[] { false, true })
        foreach (bool filters in new[] { false, true })
        {
            var fixture = Create(perspective, depth, filters); string before = JsonSerializer.Serialize(fixture.Source);
            foreach (int milliseconds in new[] { 30, 150 })
            {
                using var renderer = new SceneRenderer { AnimationTime = TimeSpan.FromMilliseconds(milliseconds) };
                var view = SceneEvaluation.Build(fixture.Source); var reference = Reference(fixture, view);
                using var actual = SKBitmap.Decode(renderer.ExportPng(fixture.Source, fixture.Assets, true));
                using var expected = SKBitmap.Decode(renderer.ExportPng(reference, fixture.Assets, true));
                check($"prefab nested affine billboard image animation and filters match an independent world reference p={perspective} d={depth} f={filters} t={milliseconds}", actual.Pixels.SequenceEqual(expected.Pixels));
                var board = view.OccurrencesOf(new(fixture.Inner.Id, fixture.Board.Id)).First();
                var point = CanvasMapping.Create(reference).ProjectedBounds(reference.Elements.Single(e => e.Id == board.RuntimeElementId)).Center;
                var hit = renderer.PickDepth(fixture.Source, fixture.Assets, point, 0);
                var expectedHit = renderer.PickDepth(reference, fixture.Assets, point, 0);
                check($"prefab picking and depth share the rendered occurrence identity p={perspective} d={depth} f={filters} t={milliseconds}", hit?.Id == expectedHit?.Id);
            }
            check("prefab rendering never mutates or flattens its source graph", JsonSerializer.Serialize(fixture.Source) == before && fixture.Source.Elements.Count == 1 && fixture.Source.Instances.Count == 2);
        }
    }
    private static void Identity(Action<string, bool> check)
    {
        var fixture = Create(false, false, false); var source = fixture.Source;
        string before = JsonSerializer.Serialize(source); var view = SceneEvaluation.Build(source);
        check("prefab evaluation leaves source graph and definition bytes unchanged", before == JsonSerializer.Serialize(source));
        check("same authored Guid in host and two definitions has disjoint source addresses", view.Leaves.Count == 7 && view.Leaves.Select(leaf => leaf.RuntimeElementId).Distinct().Count() == 7 &&
            view.OccurrencesOf(new(null, fixture.Rectangle.Id)).Count == 1 && view.OccurrencesOf(new(fixture.Outer.Id, fixture.Rectangle.Id)).Count == 2 && view.OccurrencesOf(new(fixture.Inner.Id, fixture.Board.Id)).Count == 2);
        var first = view.OccurrencesOfHostNode(fixture.First.Id);
        check("instance and ordinary group occurrence indexes include nested virtual content", first.Count == 3 && view.OccurrencesOfHostNode(fixture.Host.Id).Count == 3 && view.OccurrencesOfHostNode(fixture.Second.Id).Count == 3);
        check("virtual hits resolve to real host editing nodes and never definition members", first.All(leaf => view.HostNodeOfOccurrence(leaf.RuntimeElementId) == fixture.First.Id &&
            view.ResolveSelectableNode(leaf.RuntimeElementId) == fixture.Host.Id && view.ResolveSelectableNode(leaf.RuntimeElementId, fixture.Host.Id) == fixture.First.Id &&
            view.ResolveSelectableNode(leaf.RuntimeElementId, fixture.Second.Id) is null && view.SourceAddress(leaf.RuntimeElementId) == leaf.Source));
        check("runtime views retain their exact evaluation and cannot double apply placement", ReferenceEquals(SceneEvaluation.Build(view.RenderDocument), view) &&
            view.RenderDocument.Instances.Count == 0 && view.RenderDocument.Definitions.Count == 0 && view.RenderDocument.Groups.Count == 0 && view.RenderDocument.Elements.All(e => e.GroupId is null));
        check("definition source IDs are not mistaken for a first occurrence", view.FindElement(fixture.Rectangle.Id)!.Id == source.Elements.Single().Id && view.FindElement(fixture.Image.Id) is null);
        var ids = view.Leaves.Select(leaf => (leaf.OccurrencePath, leaf.RuntimeElementId)).ToArray();
        fixture.Outer.Revision = Guid.NewGuid(); fixture.Rectangle.Style.Fill = 0xFF00AA44; fixture.Host.Transform = Affine2D.Translation(70, 25);
        var updated = SceneEvaluation.Build(source);
        check("revision color and ordinary parent movement preserve occurrence identities", ids.SequenceEqual(updated.Leaves.Select(leaf => (leaf.OccurrencePath, leaf.RuntimeElementId))));
        check("a published occurrence snapshot is detached from later definition edits", view.OccurrencesOf(new(fixture.Outer.Id, fixture.Rectangle.Id)).All(leaf => leaf.RenderElement.Style.Fill != fixture.Rectangle.Style.Fill));
        var collision = source.Clone(); var real = collision.Elements.Single(); Guid oldId = real.Id;
        real.Id = updated.OccurrencesOfHostNode(fixture.First.Id)[0].RuntimeElementId;
        collision.RootOrder = collision.RootOrder!.Select(id => id == oldId ? real.Id : id).ToList();
        bool rejected = false; try { SceneEvaluation.Build(collision); } catch (InvalidDataException) { rejected = true; }
        check("runtime identity collisions with authored host nodes are rejected explicitly", rejected);
        check("instance expansion keeps one authored copy of each definition's content", source.Elements.Count == 1 && source.Definitions.Sum(d => d.Graph.Elements.Count) == 3);
    }

    private sealed record Fixture(SceneDocument Source, AssetLibrary Assets, CanvasGroup Host, PrefabInstance First, PrefabInstance Second,
        PrefabDefinition Outer, PrefabDefinition Inner, CanvasGroup InnerGroup, PrefabInstance Nested, CanvasElement Rectangle, CanvasElement Board, CanvasElement Image);
    private static Fixture Create(bool perspective, bool depth, bool filters)
    {
        var assets = new AssetLibrary(); string imageId = assets.Add(Gif(), "image/gif"); Guid shared = Guid.NewGuid();
        var board = new CanvasElement { Id = shared, Kind = ElementKind.Rectangle, Width = 30, Height = 38, IsBillboard = true,
            BillboardAnchor = new(5, 70), BillboardHeight = 14, Transform = Affine2D.Rotation(9), Locked = true, Style = new() { Fill = 0xFFCC4433, StrokeWidth = 1 } };
        var image = new CanvasElement { Kind = ElementKind.Image, Width = 36, Height = 24, AssetId = imageId, Transform = Affine2D.Translation(35, 30),
            Effects = [new() { Kind = ImageEffectKind.Pixelate, Bounds = new(.1, .2, .5, .3), Amount = 4 }], Style = new() { StrokeWidth = 0 } };
        var inner = new PrefabDefinition { Name = "inner", Graph = new() { Elements = [board, image] } };
        if (filters) inner.Graph.Elements.Add(new() { Kind = ElementKind.LiveBlur, Width = 74, Height = 54, Transform = Affine2D.Translation(20, 22),
            Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(0, 0, 1, 1), Amount = 5 }], Style = new() { StrokeWidth = 0, Opacity = .45 } });
        var host = new CanvasGroup { Transform = Affine2D.Translation(50, 25).Compose(Affine2D.Rotation(7)), ElevationScale = 1.3,
            OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var group = new CanvasGroup { Id = host.Id, Transform = Affine2D.Translation(5, 12).Compose(Affine2D.Rotation(11)).Compose(Affine2D.Scale(1.1, .85)),
            ElevationScale = 1.2, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var rectangle = new CanvasElement { Id = shared, GroupId = group.Id, Kind = ElementKind.Rectangle, Width = 60, Height = 42,
            Style = new() { Fill = 0xBB19A9DE, StrokeWidth = 2 } };
        var nested = new PrefabInstance { DefinitionId = inner.Id, ParentId = group.Id, Transform = Affine2D.Translation(65, 28).Compose(Affine2D.Rotation(-5)), ElevationScale = 1.1 };
        group.ChildOrder = [rectangle.Id, nested.Id];
        var outer = new PrefabDefinition { Name = "outer", Graph = new() { Groups = [group], Elements = [rectangle], Instances = [nested], RootOrder = [group.Id] } };
        var first = new PrefabInstance { ParentId = host.Id, DefinitionId = outer.Id, Transform = Affine2D.Translation(10, 10).Compose(Affine2D.Scale(.9, .9)), ElevationScale = 2 };
        var second = new PrefabInstance { DefinitionId = outer.Id, Transform = Affine2D.Translation(330, 150).Compose(Affine2D.Rotation(-8)).Compose(Affine2D.Scale(.7, .7)), ElevationScale = 1.5 };
        host.ChildOrder = [first.Id]; var backdrop = rectangle.Clone(); backdrop.GroupId = null; backdrop.Transform = Affine2D.Translation(10, 15);
        var source = new SceneDocument { Viewport = new(0, 0, 640, 360), Elements = [backdrop], Groups = [host], Instances = [first, second], Definitions = [outer, inner], RootOrder = [backdrop.Id, host.Id, second.Id], LayerOpacity = .81 };
        source.Canvas.BackgroundColor = 0xFFE1F0FF; source.Canvas.Grid.Visible = true; source.Canvas.Grid.FlatCellSize = 64;
        if (perspective)
        {
            source.Canvas.Projection = CanvasCalibration.CreateInitial(source.Viewport); source.Canvas.Projection.Enabled = true;
            source.Canvas.Projection.Matrix = new(1, .08, 15, .03, 1, 8, .0004, .00015, 1);
            source.Canvas.DepthPriorityOcclusion = depth;
        }
        return new(source, assets, host, first, second, outer, inner, group, nested, rectangle, board, image);
    }
    private static SceneDocument Reference(Fixture fixture, EvaluatedScene view)
    {
        var reference = new SceneDocument { Viewport = fixture.Source.Viewport, Canvas = fixture.Source.Canvas.Clone(), LayerOpacity = fixture.Source.LayerOpacity,
            Elements = [fixture.Source.Elements.Single().Clone()] };
        foreach (var instance in new[] { fixture.First, fixture.Second })
        {
            var parent = instance == fixture.First ? fixture.Host.Transform : Affine2D.Identity;
            double elevation = (instance == fixture.First ? fixture.Host.ElevationScale : 1) * instance.ElevationScale * fixture.InnerGroup.ElevationScale;
            parent = parent.Compose(instance.Transform).Compose(fixture.InnerGroup.Transform);
            Add(fixture.Rectangle, fixture.Outer.Id, parent, elevation);
            foreach (var leaf in fixture.Inner.Graph.Elements) Add(leaf, fixture.Inner.Id, parent.Compose(fixture.Nested.Transform), elevation * fixture.Nested.ElevationScale);
            void Add(CanvasElement source, Guid definition, Affine2D plane, double height)
            {
                var world = source.Clone(); world.GroupId = null; world.Locked = false;
                if (source.IsBillboard)
                {
                    world.BillboardAnchor = plane.TransformPoint(source.BillboardAnchor);
                    world.Transform = (plane with { OffsetX = 0, OffsetY = 0 }).Compose(source.Transform with { OffsetX = 0, OffsetY = 0 });
                    world.BillboardHeight *= height;
                }
                else world.Transform = plane.Compose(source.Transform);
                world.Id = view.OccurrencesOf(new(definition, source.Id)).Single(leaf => leaf.HostNodeId == instance.Id).RuntimeElementId;
                reference.Elements.Add(world);
            }
        }
        return reference;
    }
    private static byte[] Gif()
    {
        // Two independently encoded 1x1 frames (red, blue), 100 ms each, looping.
        return [.. "GIF89a"u8.ToArray(), 1,0,1,0,0x80,0,0, 255,0,0, 0,0,255,
            0x21,0xFF,11,.. "NETSCAPE2.0"u8.ToArray(),3,1,0,0,0,
            0x21,0xF9,4,0,10,0,0,0, 0x2C,0,0,0,0,1,0,1,0,0, 2,2,0x44,1,0,
            0x21,0xF9,4,0,10,0,0,0, 0x2C,0,0,0,0,1,0,1,0,0, 2,2,0x4C,1,0,0x3B];
    }
}
