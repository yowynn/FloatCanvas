using FloatCanvas.Core;

internal static class SharedBillboardSafetyTests
{
    internal static void Run(Action<bool, string> check)
    {
        var leaf = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 20,
            Style = new() { StrokeWidth = 2 } };
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [leaf.Id],
            Transform = Affine2D.Translation(0, -2000), Billboard = new(default, 0) };
        leaf.GroupId = group.Id;
        var inner = new PrefabDefinition { Graph = new() { Elements = [leaf], Groups = [group], RootOrder = [group.Id] } };
        var nested = new PrefabInstance { DefinitionId = inner.Id, Transform = Affine2D.Translation(30, 40) };
        var outer = new PrefabDefinition { Graph = new() { Instances = [nested], RootOrder = [nested.Id] } };
        var instance = new PrefabInstance { DefinitionId = outer.Id, Transform = Affine2D.Translation(100, 200),
            Billboard = new(new(30, -1960), 0) };
        var root = new SceneDocument { Viewport = new(0, 0, 1000, 800), Definitions = [inner, outer],
            Instances = [instance], RootOrder = [instance.Id] };
        root.Canvas.Projection = new() { Enabled = true,
            Quad = [new(0, 0), new(1000, 0), new(1000, 800), new(0, 800)],
            Matrix = new(1, 0, 0, 0, 1, 0, 0, .001, 1) };
        var draft = new SceneDocument { Elements = inner.Graph.Elements, Groups = inner.Graph.Groups, RootOrder = inner.Graph.RootOrder };
        var local = SceneEvaluation.Build(draft).Leaves.Single().RenderElement;
        var displayed = SceneEvaluation.Build(root).Leaves.Single().RenderElement;
        var host = ContainerPlacementContext.Identity.Enter(instance.Transform, instance.ElevationScale, instance.Billboard)
            .Compose(ContainerPlacementContext.Identity.Enter(nested.Transform, nested.ElevationScale, nested.Billboard));
        var mapping = CanvasMapping.CreateSurfaceView(root, host, LayerPlacement.Identity, CanvasViewTransform.Identity, host: host);
        var rootMapping = CanvasMapping.Create(root);
        check(!rootMapping.IsElementSafe(local), "fixture: the inner card's local anchor is behind the root horizon");
        check(rootMapping.IsElementSafe(displayed), "fixture: the outer shared card places the nested content safely");
        check(mapping.ElementToScreen(local).TransformPoint(default).DistanceTo(new(100 / 1.2, 200 / 1.2)) < 1e-7,
            "nested shared content renders at the outer card's ground point");
        check(mapping.IsElementSafe(local), "nested shared billboard safety must accept safe displayed content despite an unsafe local anchor");

        foreach (var transform in new[] { Affine2D.Translation(10, 15), Affine2D.Rotation(25), Affine2D.Scale(1.5, .75) })
        {
            leaf.Transform = transform;
            var edited = SceneEvaluation.Build(draft).Leaves.Single().RenderElement;
            var world = SceneEvaluation.Build(root).Leaves.Single().RenderElement;
            var viewing = new CanvasViewTransform(1.5, new(20, -10));
            var editingMap = CanvasMapping.CreateSurfaceView(root, host, LayerPlacement.Identity, viewing, host: host);
            var displayMap = CanvasMapping.CreateViewing(root, viewing);
            check(displayMap.IsElementSafe(world), "fixture: moved, rotated or resized nested content remains safe");
            check(editingMap.IsElementSafe(edited), "shared safety accepts movement, rotation and resize with a zoomed camera");
            check(editingMap.ProjectedBounds(edited) == displayMap.ProjectedBounds(world),
                "shared editing and the complete scene agree on displayed bounds after a transform");
        }

        // With no external host, the element already belongs to the displayed root namespace.
        var direct = CanvasMapping.CreateSurfaceView(root, host, LayerPlacement.Identity, CanvasViewTransform.Identity);
        check(direct.IsElementSafe(displayed), "surface view without a host still accepts an already resolved safe element");
        check(!direct.IsElementSafe(local), "surface view without a host still rejects a genuinely unsafe anchor");

        // The inverse regression: safe local geometry must not hide an unsafe host placement.
        group.Transform = Affine2D.Identity; leaf.Transform = Affine2D.Identity;
        instance.Billboard = new(default, CanvasProjectionSafety.MaximumCoordinate * 2);
        local = SceneEvaluation.Build(draft).Leaves.Single().RenderElement;
        displayed = SceneEvaluation.Build(root).Leaves.Single().RenderElement;
        host = ContainerPlacementContext.Identity.Enter(instance.Transform, instance.ElevationScale, instance.Billboard)
            .Compose(ContainerPlacementContext.Identity.Enter(nested.Transform, nested.ElevationScale, nested.Billboard));
        mapping = CanvasMapping.CreateSurfaceView(root, host, LayerPlacement.Identity, CanvasViewTransform.Identity, host: host);
        check(rootMapping.IsElementSafe(local), "fixture: the local anchor alone is safe");
        check(!rootMapping.IsElementSafe(displayed), "fixture: the host places content beyond the supported coordinate limit");
        check(!mapping.IsElementSafe(local), "shared safety must reject unsafe displayed content even with a safe local anchor");
    }
}
