using FloatCanvas.Core;
using FloatCanvas.Rendering;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Editable clipboard snapshots and one-step insertion without OS clipboard access.</summary>
internal static class ObjectClipboardCommand
{
    private sealed record OwnerStamp(WeakReference<EditorSession> Owner, string ContentHash);
    private sealed class OwnerTransferProof(EditorSession owner)
    {
        internal Guid Token { get; } = Guid.NewGuid();
        internal WeakReference<EditorSession> Owner { get; } = new(owner);
        internal HashSet<string> ContentHashes { get; } = [];
    }
    private static readonly ConditionalWeakTable<SceneDocument, OwnerStamp> snapshotOwners = new();
    private static readonly ConditionalWeakTable<EditorSession, OwnerTransferProof> ownerProofs = new();
    private static readonly Dictionary<Guid, WeakReference<OwnerTransferProof>> transfers = [];
    private static readonly object transferGate = new();
    private static string ContentHash(SceneDocument document) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document))));

    // A token is meaningful only in this process, for this exact copied graph and owner.
    // Reopened/external payloads cannot establish WithinOwner import merely by matching definition IDs.
    internal static Guid RegisterTransfer(EditorSession owner, SceneDocument snapshot)
    {
        var proof = ownerProofs.GetValue(owner, static session => new(session)); string hash = ContentHash(snapshot);
        lock (transferGate)
        {
            proof.ContentHashes.Add(hash); transfers[proof.Token] = new(proof);
            if (transfers.Count % 64 == 0)
                foreach (var expired in transfers.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray()) transfers.Remove(expired);
        }
        return proof.Token;
    }
    private static bool SameOwner(EditorSession target, SceneDocument source, Guid? transfer)
    {
        string? hash = null;
        if (snapshotOwners.TryGetValue(source, out var direct) && direct.Owner.TryGetTarget(out var directOwner) && ReferenceEquals(directOwner, target))
        {
            hash = ContentHash(source); if (direct.ContentHash == hash) return true;
        }
        if (transfer is not { } token) return false;
        lock (transferGate)
            return transfers.TryGetValue(token, out var weak) && weak.TryGetTarget(out var proof) && proof.Owner.TryGetTarget(out var owner) &&
                ReferenceEquals(owner, target) && proof.ContentHashes.Contains(hash ?? ContentHash(source));
    }
    internal static SceneDocument CreateRasterSnapshot(EditorSession session, SceneDocument objects)
    {
        var snapshot = objects.Clone();
        snapshot.Canvas = session.Document.Canvas.Clone();
        var world = SceneEvaluation.Build(snapshot).RenderDocument;
        var visible = world.Elements.Where(element => element.Visible).ToArray();
        if (visible.Length > 0)
        {
            snapshot.Viewport = visible.Select(element => SceneRenderer.ProjectedBounds(world, element, true))
                .Aggregate(RectD.Union).Inflate(1).Intersect(session.Document.Viewport);
        }
        return snapshot;
    }

    internal static SceneDocument CreateSnapshot(EditorSession session)
    {
        var snapshot = session.CreateSelectionSnapshot(normalizeOrigin: false);
        var graph = new SceneGraph(snapshot);
        graph.RemoveNodes(snapshot.Elements.Select(element => element.Id).Concat(snapshot.Instances.Select(instance => instance.Id))
            .Where(id => !graph.IsVisible(id)).ToArray());
        snapshot.PruneGroups();
        snapshot.Definitions = PrefabClosure.ReachableDefinitions(snapshot, snapshot.Definitions).ToList();
        var world = SceneEvaluation.Build(snapshot).RenderDocument;
        var visible = world.Elements.Where(element => element.Visible).ToArray();
        if (visible.Length > 0)
        {
            // Preserve desktop positions and the visible clipboard appearance, including thin lines.
            snapshot.Viewport = visible.Select(WorldBounds).Aggregate(RectD.Union).Inflate(1);
            snapshot.LayerOpacity = session.Document.LayerOpacity;
        }
        snapshotOwners.Add(snapshot, new(new(session), ContentHash(snapshot)));
        return snapshot;
    }

    internal static void Insert(EditorSession target, SceneDocument source, AssetLibrary assets, RectD visibleArea, Guid? ownerTransfer = null)
    {
        if (source.Elements.Count == 0 && source.Groups.Count == 0 && source.Instances.Count == 0) return;
        bool sameOwner = SameOwner(target, source, ownerTransfer);
        bool preserveDefinitions = sameOwner;
        var snapshot = source.Clone();
        if (preserveDefinitions)
        {
            var currentIds = target.Document.Definitions.Select(definition => definition.Id).ToHashSet();
            preserveDefinitions = snapshot.Instances.All(instance => currentIds.Contains(instance.DefinitionId));
            if (preserveDefinitions) snapshot.Definitions = PrefabClosure.ReachableDefinitions(snapshot, target.Document.Definitions).Select(definition => definition.Clone()).ToList();
        }
        foreach (var element in snapshot.Elements) element.Locked = false;
        foreach (var group in snapshot.Groups) group.Locked = false;
        foreach (var instance in snapshot.Instances) instance.Locked = false;
        var offset = new PointD(16, 16);
        var world = SceneEvaluation.Build(snapshot).RenderDocument;
        var visible = world.Elements.Where(element => element.Visible).ToArray();
        var bounds = visible.Length == 0 ? snapshot.Viewport : visible.Select(WorldBounds).Aggregate(RectD.Union);
        var shiftedBounds = new RectD(bounds.X + offset.X, bounds.Y + offset.Y, bounds.Width, bounds.Height);
        // Keep the normal paste offset unless the entire selection would be off the current screen.
        if (!shiftedBounds.Intersects(visibleArea)) offset += visibleArea.Center - shiftedBounds.Center;
        var mapping = CanvasMapping.Create(target.Document, target.ActiveRootCanvas);
        foreach (var element in world.Elements)
        {
            var candidate = element.Clone();
            BillboardGeometry.ApplyPlaneTransform(candidate, Affine2D.Translation(offset.X, offset.Y));
            if (!mapping.IsElementSafe(candidate)) throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
        }
        target.InsertDocument(snapshot, assets, Affine2D.Translation(offset.X, offset.Y), label: L.Get("History.Paste"),
            preserveDefinitions: preserveDefinitions, preserveNumberCounter: sameOwner);
    }
    private static RectD WorldBounds(CanvasElement element) => element.IsBillboard
        ? BillboardGeometry.ElementToTarget(element, Projective2D.Identity).TransformBounds(ElementGeometry.StrokedLocalBounds(element))
        : ElementGeometry.Bounds(element);
}
