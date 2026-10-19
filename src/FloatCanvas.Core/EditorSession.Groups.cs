using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

public sealed partial class EditorSession
{
    /// <summary>The outer group is selected as a unit; individual=true keeps direct member editing available.</summary>
    public IEnumerable<Guid> SelectionUnitIds(Guid elementId, bool individual = false)
    {
        var element = Document.Elements.FirstOrDefault(e => e.Id == elementId);
        if (element is null) return [];
        return !individual && Document.RootGroupId(element) is { } group
            ? Document.GroupElements(group).Where(e => !e.Locked).Select(e => e.Id).ToArray()
            : [element.Id];
    }

    public Guid? GroupSelection(string? name = null)
    {
        var selected = SelectedElements().Where(e => !e.Locked).Select(e => e.Id).ToHashSet();
        if (selected.Count < 2) return null;
        Guid id = Guid.NewGuid();
        Execute(L.Get("History.GroupElements"), document =>
        {
            var complete = document.Groups.Where(g => document.GroupElements(g.Id).All(e => selected.Contains(e.Id))).Select(g => g.Id).ToHashSet();
            var roots = document.Groups.Where(g => complete.Contains(g.Id) && (g.ParentId is null || !complete.Contains(g.ParentId.Value))).ToArray();
            var nestedElements = roots.SelectMany(g => document.GroupElements(g.Id)).Select(e => e.Id).ToHashSet();
            foreach (var root in roots) root.ParentId = id;
            foreach (var element in document.Elements.Where(e => selected.Contains(e.Id) && !nestedElements.Contains(e.Id))) element.GroupId = id;
            document.Groups.Add(new() { Id = id, Name = name ?? "" });
            SetSelectionCore(selected);
        });
        return id;
    }

    /// <summary>Removes one outer grouping level and preserves any nested groups and all element transforms.</summary>
    public void UngroupSelection()
    {
        var complete = Document.Groups.Where(g => Document.GroupElements(g.Id).All(e => Selection.Contains(e.Id))).Select(g => g.Id).ToHashSet();
        var roots = Document.Groups.Where(g => complete.Contains(g.Id) && (g.ParentId is null || !complete.Contains(g.ParentId.Value))).ToArray();
        if (roots.Length == 0) return;
        Execute(L.Get("History.UngroupElements"), document =>
        {
            foreach (var root in roots)
            {
                foreach (var element in document.Elements.Where(e => e.GroupId == root.Id)) element.GroupId = root.ParentId;
                foreach (var group in document.Groups.Where(g => g.ParentId == root.Id)) group.ParentId = root.ParentId;
                document.Groups.Remove(root);
            }
        });
    }

    /// <summary>Vector preset/clipboard snapshot, with exact selected members and independent group metadata.</summary>
    public SceneDocument CreateSelectionSnapshot(bool normalizeOrigin = true)
    {
        var snapshot = Document.CloneElements(Selection);
        if (snapshot.Elements.Count == 0) return snapshot;
        var bounds = snapshot.Elements.Select(e => ElementGeometry.Bounds(e)).Aggregate(RectD.Union);
        snapshot.Viewport = new(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        if (normalizeOrigin)
        {
            var translation = Affine2D.Translation(-bounds.Left, -bounds.Top);
            foreach (var element in snapshot.Elements) BillboardGeometry.ApplyPlaneTransform(element, translation);
            snapshot.Viewport = new(0, 0, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        }
        // The preset carries object opacity. The receiving document owns its overall layer opacity.
        snapshot.LayerOpacity = 1;
        return snapshot;
    }

    /// <summary>Imports editable entities, fresh element/effect/group IDs and immutable referenced resources atomically.</summary>
    public void InsertDocument(SceneDocument source, AssetLibrary sourceAssets, Affine2D transform, bool group = false, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceAssets);
        if (!double.IsFinite(transform.M11) || !double.IsFinite(transform.M12) || !double.IsFinite(transform.M21) ||
            !double.IsFinite(transform.M22) || !double.IsFinite(transform.OffsetX) || !double.IsFinite(transform.OffsetY) || !transform.TryInverse(out _))
            throw new ArgumentException("The insertion transform must be finite and invertible.", nameof(transform));
        // Resolve every resource before adding any entities to the receiving document.
        var resources = source.Elements.Where(e => e.AssetId is not null).Select(e => e.AssetId!).Distinct(StringComparer.Ordinal).Select(sourceAssets.Get).ToArray();
        var map = resources.ToDictionary(r => r.Id, r => Assets.Add(r.Data.ToArray(), r.MediaType), StringComparer.Ordinal);
        var snapshot = source.CloneElements(source.Elements.Select(e => e.Id), true, map);
        if (snapshot.Elements.Count == 0) return;
        if (group && snapshot.Elements.Count > 1 &&
            (snapshot.RootGroupId(snapshot.Elements[0]) is not { } existing || snapshot.Elements.Any(e => snapshot.RootGroupId(e) != existing)))
        {
            var outer = new CanvasGroup();
            foreach (var nested in snapshot.Groups.Where(g => g.ParentId is null)) nested.ParentId = outer.Id;
            foreach (var element in snapshot.Elements.Where(e => e.GroupId is null)) element.GroupId = outer.Id;
            snapshot.Groups.Add(outer);
        }
        Execute(label ?? L.Get("History.InsertPreset"), document =>
        {
            document.Groups.AddRange(snapshot.Groups);
            Selection.Clear();
            foreach (var element in snapshot.Elements)
            {
                BillboardGeometry.ApplyPlaneTransform(element, transform);
                document.Elements.Add(element); Selection.Add(element.Id);
                if (element.Kind == ElementKind.Number) document.NextNumber = Math.Max(document.NextNumber, (int)Math.Min(int.MaxValue, (long)element.Number + 1));
            }
        });
    }
}
