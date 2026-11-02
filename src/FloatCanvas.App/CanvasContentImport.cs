using System.IO;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal delegate Task<PreparedCanvasContentBatch> CanvasContentPreparation(EditorSession target, IReadOnlyList<string> paths,
    IReadOnlyList<NormalizedImage> extraImages, RectD placement, PointD? dropCenter, double opacity, string? ownerPath, CancellationToken token);

/// <summary>An owned, fully validated placement candidate. Reading and decoding never touch the live session.</summary>
internal sealed class PreparedCanvasContentBatch
{
    private readonly EditorSession owner;
    private readonly SceneDocument original, candidate;
    private readonly long revision, contentVersion, scopeVersion, transactionId;
    private readonly AssetResource[] resources;
    private readonly Guid[] selectedIds;
    private bool consumed;
    internal int ImageCount { get; }
    internal int ProjectCount { get; }
    internal bool HasProjects => ProjectCount > 0;
    internal int NodeCount => selectedIds.Length;
    internal IReadOnlyList<Guid> SelectedIds => Array.AsReadOnly(selectedIds);

    internal PreparedCanvasContentBatch(EditorSession owner, SceneDocument original, long revision, long contentVersion,
        long scopeVersion, long transactionId, SceneDocument candidate, AssetResource[] resources, Guid[] selectedIds,
        int imageCount, int projectCount)
    {
        this.owner = owner; this.original = original; this.revision = revision; this.contentVersion = contentVersion;
        this.scopeVersion = scopeVersion; this.transactionId = transactionId; this.candidate = candidate;
        this.resources = resources; this.selectedIds = selectedIds; ImageCount = imageCount; ProjectCount = projectCount;
    }
    internal bool IsCurrent(EditorSession target) => !consumed && ReferenceEquals(owner, target) &&
        ReferenceEquals(original, target.Document) && revision == target.SceneRevision && contentVersion == target.ContentVersion &&
        scopeVersion == target.EditScopeVersion && transactionId == target.TransactionId && !target.IsTransactionActive && target.CanInsertInEditScope;

    internal bool TryCommit(EditorSession target)
    {
        if (selectedIds.Length == 0 || !IsCurrent(target)) return false;
        // This copy is the final author snapshot; callers cannot edit the prepared graph between validation and publication.
        var document = candidate.Clone(); SceneValidation.Validate(document);
        var supplied = resources.Select(resource => resource.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in document.ReferencedAssets)
            if (!target.Assets.TryGet(id, out _) && !supplied.Contains(id)) throw new KeyNotFoundException(id);
        if (!IsCurrent(target)) return false;
        target.Execute(L.Get("Placement.ImportTitle"), live =>
        {
            foreach (var resource in resources) target.Assets.Add(resource);
            live.Elements = document.Elements; live.Groups = document.Groups; live.Instances = document.Instances;
            live.Definitions = document.Definitions; live.RootOrder = document.RootOrder;
            live.RootCanvasAssignments = new(document.RootCanvasAssignments);
            target.Selection.Clear(); target.Selection.UnionWith(selectedIds);
        });
        consumed = true; return true;
    }
}

/// <summary>Places ordinary images and linked project/prefab snapshots through one atomic author transaction.</summary>
internal static class CanvasContentImport
{
    private const long MaximumBatchFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumReferencedBytes = 1024L * 1024 * 1024;
    internal static bool IsProjectPath(string path) => Path.GetExtension(path).Equals(".floatcanvas", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".floatprefab", StringComparison.OrdinalIgnoreCase);

    internal static Task<PreparedCanvasContentBatch> PrepareAsync(EditorSession target, IReadOnlyList<string> paths,
        IReadOnlyList<NormalizedImage> extraImages, RectD placement, PointD? dropCenter, double opacity, string? ownerPath, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(paths); ArgumentNullException.ThrowIfNull(extraImages);
        if (target.IsTransactionActive || !target.CanInsertInEditScope) throw new InvalidOperationException(L.Get("Text.ActionInProgress"));
        if (paths.Count + extraImages.Count is 0 or > ImageImportBudget.MaximumFiles) throw new InvalidDataException(L.Get("Images.ImportCapacityExceeded"));
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1 || !Finite(placement) || placement.Width <= 0 || placement.Height <= 0 ||
            dropCenter is { } point && (!double.IsFinite(point.X) || !double.IsFinite(point.Y))) throw new ArgumentOutOfRangeException(nameof(placement));
        var original = target.Document; var snapshot = original.Clone(); var assets = target.Assets.Resources.ToArray();
        long revision = target.SceneRevision, contentVersion = target.ContentVersion, scopeVersion = target.EditScopeVersion, transactionId = target.TransactionId;
        Guid? scope = target.EditScopeId; var rootCanvas = target.ActiveRootCanvas;
        var pathSnapshot = paths.Select(Path.GetFullPath).ToArray(); var extras = extraImages.ToArray();
        return Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested(); long fileBytes = extras.Sum(image => (long)image.PngBytes.Length);
            foreach (var path in pathSnapshot)
            {
                long length = new FileInfo(path).Length;
                if (!IsProjectPath(path)) ImageImportBudget.RequireBytes(length);
                if (length < 0 || length > MaximumBatchFileBytes - fileBytes) throw new InvalidDataException(L.Get("Images.ImportCapacityExceeded"));
                fileBytes += length;
            }
            var library = new AssetLibrary(); foreach (var resource in assets) library.Add(resource);
            var staging = new EditorSession(snapshot, library, historyLimit: 0, historyByteLimit: 0);
            staging.SetRootCanvas(rootCanvas); staging.SetEditScope(scope);
            var resolver = new PrefabLinkResolver(); var selected = new List<Guid>();
            int imageCount = 0, projectCount = 0, index = 0; long imageBytes = 0, imagePixels = 0;
            var center = dropCenter ?? placement.Center;
            foreach (var path in pathSnapshot)
            {
                token.ThrowIfCancellationRequested(); var itemCenter = center + new PointD(index * 20, index * 20);
                if (IsProjectPath(path))
                {
                    var source = await resolver.PrepareAsync(staging, path, ownerPath, linked: true, ct: token).ConfigureAwait(false);
                    var metadata = source.Source.Clone(); metadata.Locator = source.Path; metadata.IsRelative = false;
                    var existing = staging.Document.Definitions.FirstOrDefault(definition => definition.Source is { IsLinked: true } link &&
                        link.SourceResourceId == metadata.SourceResourceId && link.EntryKind == metadata.EntryKind && link.EntryDefinitionId == metadata.EntryDefinitionId &&
                        SamePath(link, ownerPath, source.Path));
                    string name = Path.GetFileNameWithoutExtension(path);
                    Guid instanceId;
                    if (existing is not null)
                    {
                        if (!string.Equals(existing.Source!.LastAcceptedFileSha256, metadata.LastAcceptedFileSha256, StringComparison.OrdinalIgnoreCase))
                            staging.ApplyPrefabEdit(staging.PreparePrefabRefresh(existing.Id, source.Package, source.Assets, metadata));
                        // Refresh retains the original source anchor for existing copies.
                        // The accepted graph can therefore differ from the normalized incoming package.
                        var transform = Placement(staging.Document.Definitions, existing.Id, placement, itemCenter);
                        instanceId = staging.PlacePrefab(existing.Id, transform, name);
                    }
                    else
                    {
                        var transform = Placement(source.Package.Definitions, source.Package.EntryDefinitionId, placement, itemCenter);
                        instanceId = staging.ImportPrefab(source.Package, source.Assets, transform, metadata, name: name).InstanceId;
                    }
                    selected.Add(instanceId); projectCount++;
                }
                else
                {
                    var normalized = await ImageImportCommand.NormalizeFilesAsync([path], token).ConfigureAwait(false);
                    foreach (var image in normalized) AddImage(image, itemCenter);
                }
                index++;
                RequireResourceBudget(staging.Document, library);
            }
            foreach (var image in extras) { token.ThrowIfCancellationRequested(); AddImage(image, center + new PointD(index * 20, index * 20)); index++; }
            token.ThrowIfCancellationRequested(); SceneValidation.Validate(staging.Document); RequireResourceBudget(staging.Document, library);
            var resources = staging.Document.ReferencedAssets.Distinct(StringComparer.Ordinal).Select(library.Get).ToArray();
            return new PreparedCanvasContentBatch(target, original, revision, contentVersion, scopeVersion, transactionId,
                staging.Document.Clone(), resources, selected.ToArray(), imageCount, projectCount);

            void AddImage(NormalizedImage image, PointD itemCenter)
            {
                ImageImportBudget.RequireBytes(image.PngBytes.LongLength);
                imageBytes = checked(imageBytes + image.PngBytes.LongLength); ImageImportBudget.RequireBytes(imageBytes, ImageImportBudget.MaximumBatchBytes);
                imagePixels = checked(imagePixels + ImageDecodeBudget.ReadPixelCount(image.PngBytes));
                if (imagePixels > ImageDecodeBudget.MaximumBatchPixels) throw new InvalidDataException(L.Get("Images.ImportCapacityExceeded"));
                ImageImportCommand.Insert(staging, [image], placement, dropCenter: itemCenter, opacity: opacity);
                selected.Add(staging.Selection.Single()); imageCount++;
            }
        }, token);
    }
    private static LayerPlacement Placement(IReadOnlyList<PrefabDefinition> definitions, Guid definitionId, RectD placement, PointD center)
    {
        var root = new PrefabInstance { DefinitionId = definitionId };
        var geometry = new EditorSession(new() { Instances = [root], Definitions = definitions.Select(definition => definition.Clone()).ToList(), RootOrder = [root.Id] });
        var bounds = geometry.NodeBounds(root.Id) ?? new RectD(0, 0, 1, 1);
        double scale = Math.Min(1, Math.Min(placement.Width * .8 / Math.Max(1, bounds.Width), placement.Height * .8 / Math.Max(1, bounds.Height)));
        return new(Affine2D.Translation(center.X - bounds.Center.X * scale, center.Y - bounds.Center.Y * scale).Compose(Affine2D.Scale(scale, scale)), scale);
    }
    private static bool SamePath(ExternalPrefabSource source, string? ownerPath, string path)
    {
        try { return string.Equals(PrefabLinkResolver.ResolvePath(source, ownerPath), path, StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException) { return false; }
    }
    private static void RequireResourceBudget(SceneDocument document, AssetLibrary library)
    {
        long total = 0;
        foreach (var id in document.ReferencedAssets.Distinct(StringComparer.Ordinal))
        {
            total = checked(total + library.Get(id).Data.Length);
            if (total > MaximumReferencedBytes) throw new InvalidDataException(L.Get("Project.AssetCapacityExceeded"));
        }
    }
    private static bool Finite(RectD value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Width) && double.IsFinite(value.Height);
}
