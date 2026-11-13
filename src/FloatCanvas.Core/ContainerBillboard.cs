namespace FloatCanvas.Core;

/// <summary>A container's stable local reference. Its Transform translation is the ground point in its parent.</summary>
public sealed record ContainerBillboard(PointD ReferencePoint, double Height)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => double.IsFinite(ReferencePoint.X) && double.IsFinite(ReferencePoint.Y) && double.IsFinite(Height);

    public Affine2D FlatPlacement(LayerPlacement placement) =>
        Affine2D.Translation(placement.PlaneTransform.OffsetX, placement.PlaneTransform.OffsetY - Height * placement.ElevationScale)
            .Compose(placement.PlaneTransform with { OffsetX = 0, OffsetY = 0 })
            .Compose(Affine2D.Translation(-ReferencePoint.X, -ReferencePoint.Y));
}

/// <summary>Detached rendering metadata. Never saved into authored nodes or used as an editable transform.</summary>
public sealed record BillboardSurfacePlacement(PointD Anchor, double Height, Projective2D ContentPlacement)
{
    public Projective2D ToTarget(PointD anchor, double scale) =>
        Projective2D.FromAffine(Affine2D.Translation(anchor.X, anchor.Y - Height * scale).Compose(Affine2D.Scale(scale, scale)))
            .Compose(ContentPlacement);
}

/// <summary>Resolves a hierarchy once, separating the outer depth owner from its flat, editable contents.</summary>
public readonly record struct ContainerPlacementContext(LayerPlacement Placement, Projective2D FlatPrefix,
    BillboardSurfacePlacement? Surface)
{
    public static ContainerPlacementContext Identity => new(LayerPlacement.Identity, Projective2D.Identity, null);

    /// <summary>Places an independently resolved source namespace into this context.</summary>
    public ContainerPlacementContext Compose(ContainerPlacementContext inner)
    {
        if (inner.Surface is not { } card) return this with { Placement = Placement.Compose(inner.Placement) };
        var anchor = Placement.PlaneTransform.TransformPoint(card.Anchor);
        var content = Projective2D.FromAffine(Placement.PlaneTransform with { OffsetX = 0, OffsetY = 0 }).Compose(card.ContentPlacement);
        double height = card.Height * Placement.ElevationScale;
        if (Surface is null) return new(inner.Placement, inner.FlatPrefix, new(anchor, height, content));
        var flat = new BillboardSurfacePlacement(anchor, height, content).ToTarget(anchor, 1);
        return new(inner.Placement, FlatPrefix.Compose(flat).Compose(inner.FlatPrefix), Surface);
    }

    public CanvasElement ResolveWorld(CanvasElement world)
    {
        if (world.BillboardSurface is not { } card) return Resolve(world);
        var context = Compose(new(LayerPlacement.Identity, Projective2D.Identity, card));
        var result = world.Clone();
        result.BillboardSurface = context.Surface! with { ContentPlacement = context.Surface!.ContentPlacement.Compose(context.FlatPrefix) };
        result.BillboardAnchor = result.BillboardSurface.Anchor; result.BillboardHeight = result.BillboardSurface.Height;
        return result;
    }

    public ContainerPlacementContext Enter(Affine2D transform, double elevation, ContainerBillboard? billboard)
    {
        var placed = Placement.Compose(new(transform, elevation));
        if (billboard is null) return this with { Placement = placed };
        if (Surface is not null)
            return new(LayerPlacement.Identity, FlatPrefix.Compose(Projective2D.FromAffine(billboard.FlatPlacement(placed))), Surface);
        var anchor = new PointD(placed.PlaneTransform.OffsetX, placed.PlaneTransform.OffsetY);
        var content = Projective2D.FromAffine((placed.PlaneTransform with { OffsetX = 0, OffsetY = 0 })
            .Compose(Affine2D.Translation(-billboard.ReferencePoint.X, -billboard.ReferencePoint.Y)));
        return new(LayerPlacement.Identity, Projective2D.Identity, new(anchor, billboard.Height * placed.ElevationScale, content));
    }

    public CanvasElement Resolve(CanvasElement source)
    {
        var world = source.Clone(); SceneGraph.ApplyPlacement(world, Placement);
        if (Surface is { } surface)
        {
            world.BillboardSurface = surface with { ContentPlacement = surface.ContentPlacement.Compose(FlatPrefix).Compose(ElementGeometry.LocalToPlane(world)) };
            world.IsBillboard = true; world.BillboardAnchor = surface.Anchor; world.BillboardHeight = surface.Height;
        }
        return world;
    }

    public Projective2D PlaneToTarget(Projective2D root)
    {
        if (Surface is not { } surface) return root.Compose(Projective2D.FromAffine(Placement.PlaneTransform));
        if (!BillboardGeometry.TryDepthScale(root, surface.Anchor, out double scale) || !root.TryTransformPoint(surface.Anchor, out var anchor))
            throw new InvalidOperationException("Unsafe container placement.");
        return surface.ToTarget(anchor, scale).Compose(FlatPrefix).Compose(Projective2D.FromAffine(Placement.PlaneTransform));
    }
}
