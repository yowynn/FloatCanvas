using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal static class CanvasPreviewDamage
{
    internal static IReadOnlyList<PixelBounds>? Between(CanvasPreviewSnapshot? before, CanvasPreviewSnapshot after,
        SceneRenderer renderer, AssetLibrary assets, IReadOnlyList<RectD>? presentationDamage = null, IReadOnlyList<RectD>? windowDamage = null)
    {
        var next = after.Document;
        if (before is null || before.ScreenDraft is not null || after.ScreenDraft is not null || before.Context != after.Context || before.PreferGpu != after.PreferGpu ||
            before.Document.Kind != next.Kind || before.Document.Viewport != next.Viewport || before.Document.LayerOpacity != next.LayerOpacity ||
            before.Context.Calibration || after.Context.Calibration || after.Context.Placing ||
            !ReferenceEquals((before.SourceDocument ?? before.Document).Background, (after.SourceDocument ?? next).Background) ||
            JsonSerializer.Serialize(before.Document.Canvas) != JsonSerializer.Serialize(next.Canvas) ||
            next.Canvas.Projection.Enabled && next.Elements.Any(item => item.IsBillboard) || SceneRenderer.HasLiveEffects(next) ||
            SceneRenderer.HasLiveEffects(before.Document) || next.Elements.Any(item => item.Effects.Count > 0) ||
            before.Document.Elements.Any(item => item.Effects.Count > 0)) return null;
        var damage = new List<PixelBounds>();
        if (ReferenceEquals(before.Document, next) && before.Revision == after.Revision && before.ContentRevision == after.ContentRevision)
        {
            AddTransientDamage(); return damage;
        }
        var oldSources = before.SourceDocument?.Elements.ToDictionary(item => item.Id);
        if (JsonSerializer.Serialize(before.SourceDocument?.Definitions ?? before.Document.Definitions) !=
            JsonSerializer.Serialize(after.SourceDocument?.Definitions ?? next.Definitions)) return null;
        var newSources = after.SourceDocument?.Elements.ToDictionary(item => item.Id);
        var oldLeaves = before.Evaluation?.Leaves.Select((leaf, index) => (leaf, index)).ToDictionary(pair => pair.leaf.RuntimeElementId);
        var newLeaves = after.Evaluation?.Leaves.Select((leaf, index) => (leaf, index)).ToDictionary(pair => pair.leaf.RuntimeElementId);
        var old = before.Document.Elements.ToDictionary(item => item.Id);
        foreach (var item in next.Elements)
        {
            if (old.Remove(item.Id, out var prior))
            {
                if (ReferenceEquals(prior, item)) continue;
                if (oldLeaves is not null && newLeaves is not null && oldLeaves.TryGetValue(item.Id, out var oldOccurrence) &&
                    newLeaves.TryGetValue(item.Id, out var newOccurrence) && oldOccurrence.leaf.Source.DefinitionId is not null &&
                    oldOccurrence.leaf.Source == newOccurrence.leaf.Source && oldOccurrence.index == newOccurrence.index &&
                    oldOccurrence.leaf.ParentWorld == newOccurrence.leaf.ParentWorld && prior.Visible == item.Visible && prior.Locked == item.Locked) continue;
                if (oldSources is not null && newSources is not null && oldLeaves is not null && newLeaves is not null &&
                    oldSources.TryGetValue(item.Id, out var oldSource) && newSources.TryGetValue(item.Id, out var newSource) && ReferenceEquals(oldSource, newSource) &&
                    oldLeaves.TryGetValue(item.Id, out var oldLeaf) && newLeaves.TryGetValue(item.Id, out var newLeaf) &&
                    oldLeaf.index == newLeaf.index && oldLeaf.leaf.ParentWorld == newLeaf.leaf.ParentWorld && prior.Visible == item.Visible && prior.Locked == item.Locked) continue;
                Add(before.Document, prior);
            }
            Add(next, item);
        }
        foreach (var item in old.Values) Add(before.Document, item);
        AddTransientDamage();
        return damage;

        void AddTransientDamage()
        {
            if (presentationDamage is not null) foreach (var bounds in presentationDamage) AddBounds(bounds);
            if (windowDamage is not null) foreach (var bounds in windowDamage) AddBounds(bounds);
            if (after.PixelDamage is { } pixels) damage.AddRange(pixels);
            if (before.AnimationTime != after.AnimationTime)
                foreach (var bounds in renderer.AnimatedImageBounds(next, assets)) AddBounds(bounds);
        }

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
