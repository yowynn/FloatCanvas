using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal static class CanvasPreviewDamage
{
    internal static IReadOnlyList<PixelBounds>? Between(CanvasPreviewSnapshot? before, CanvasPreviewSnapshot after,
        SceneRenderer renderer, AssetLibrary assets)
    {
        var next = after.Document;
        if (before is null || before.Context != after.Context || before.PreferGpu != after.PreferGpu ||
            before.Document.Kind != next.Kind || before.Document.Viewport != next.Viewport || before.Document.LayerOpacity != next.LayerOpacity ||
            before.Context.Calibration || after.Context.Calibration || after.Context.Placing ||
            !ReferenceEquals(before.Document.Background, next.Background) ||
            JsonSerializer.Serialize(before.Document.Canvas) != JsonSerializer.Serialize(next.Canvas) ||
            next.Canvas.Projection.Enabled && next.Elements.Any(item => item.IsBillboard) || SceneRenderer.HasLiveEffects(next) ||
            SceneRenderer.HasLiveEffects(before.Document) || next.Elements.Any(item => item.Effects.Count > 0) ||
            before.Document.Elements.Any(item => item.Effects.Count > 0)) return null;
        var damage = new List<PixelBounds>();
        var old = before.Document.Elements.ToDictionary(item => item.Id);
        foreach (var item in next.Elements)
        {
            if (old.Remove(item.Id, out var prior))
            {
                if (ReferenceEquals(prior, item)) continue;
                Add(before.Document, prior);
            }
            Add(next, item);
        }
        foreach (var item in old.Values) Add(before.Document, item);
        if (after.PixelDamage is { } pixels) damage.AddRange(pixels);
        if (before.AnimationTime != after.AnimationTime)
            foreach (var bounds in renderer.AnimatedImageBounds(next, assets)) AddBounds(bounds);
        return damage;

        void Add(SceneDocument document, CanvasElement item)
        {
            if (!item.Visible || item.Style.Opacity <= 0) return;
            AddBounds(SceneRenderer.ProjectedBounds(document, item, includeStroke: true));
        }
        void AddBounds(RectD bounds)
        {
            if (bounds.IsEmpty) return;
            var area = bounds.Inflate(3);
            double left = Math.Floor(area.Left + after.Context.Origin.X), top = Math.Floor(area.Top + after.Context.Origin.Y);
            double right = Math.Ceiling(area.Right + after.Context.Origin.X), bottom = Math.Ceiling(area.Bottom + after.Context.Origin.Y);
            if (!double.IsFinite(left + top + right + bottom) || right - left > int.MaxValue || bottom - top > int.MaxValue)
            { damage.AddRange(after.Outputs); return; }
            damage.Add(new((int)left, (int)top, (int)(right - left), (int)(bottom - top)));
        }
    }
}
