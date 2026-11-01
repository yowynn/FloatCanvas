using FloatCanvas.Core;
using FloatCanvas.Storage;

namespace FloatCanvas.App;

internal sealed record PrefabPreviewInput(SceneDocument Document, AssetResource[] Resources, TimeSpan AnimationTime);

/// <summary>Composes staged source graphs through their owners without publishing or writing files.</summary>
internal static class PrefabPreviewComposer
{
    internal static PrefabPreviewInput Capture(PrefabEditContext current, AssetLibrary assets, TimeSpan animationTime = default)
    {
        SceneDocument? result = null;
        foreach (var context in current.Chain())
        {
            var edited = result ?? context.Session.Document;
            if (context.Draft is { } draft) result = context.Owner.CreatePrefabPreview(draft, edited);
            else
            {
                PrefabPackage package; PointD origin;
                if (context.SourceEntryDefinitionId is { } entryId)
                {
                    package = new() { ResourceId = edited.ResourceId, EntryDefinitionId = entryId,
                        Definitions = edited.Definitions.Select(definition => definition.Clone()).ToList(), Provenance = edited.Provenance };
                    var entry = package.Definitions.Single(definition => definition.Id == entryId);
                    entry.Graph = SceneGraphClone.CloneGraph(edited).Graph; origin = entry.Pivot;
                }
                else (package, origin) = PrefabLinkResolver.ProjectContent(new LoadedProject(edited, assets), context.Name);
                var source = context.Owner.Document.Definitions.Single(definition => definition.Id == context.DefinitionId).Source!.Clone();
                source.ImportOrigin = origin;
                result = context.Owner.PreparePrefabRefresh(context.DefinitionId, package, assets, source).Candidate;
            }
        }
        var snapshot = result ?? throw new InvalidOperationException("No prefab preview context.");
        // Immutable byte references are captured on the owning UI thread before dispatch.
        return new(snapshot, snapshot.ReferencedAssets.Select(assets.Get).ToArray(), animationTime);
    }
}
