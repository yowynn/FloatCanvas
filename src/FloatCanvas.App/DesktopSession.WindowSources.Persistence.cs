using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace FloatCanvas.App;

/// <summary>Owns UI-retained frames until encoding finishes; all generated resources remain package-local.</summary>
internal sealed class WindowDocumentPackageCapture : IDisposable
{
    private readonly SceneDocument document;
    private readonly AssetResource[] resources;
    private readonly Dictionary<SourceNodeAddress, WindowFrameLease> frames = [];
    internal int FrameCount => frames.Count;
    internal WindowDocumentPackageCapture(SceneDocument document, IEnumerable<AssetResource> resources,
        Func<SourceNodeAddress, Guid, WindowFrameLease?> retain)
    {
        this.document = document.Clone(); this.resources = resources.ToArray();
        try
        {
            foreach (var pair in document.Elements.Select(e => (Address: new SourceNodeAddress(null, e.Id), Element: e))
                .Concat(document.Definitions.SelectMany(d => d.Graph.Elements.Select(e => (Address: new SourceNodeAddress(d.Id, e.Id), Element: e)))))
                if (pair.Element is { Kind: ElementKind.Image, WindowSource: { } source } && retain(pair.Address, source.BindingIntent) is { } frame)
                {
                    if (frame.Source.OwnerResourceId != document.ResourceId || frame.Source.Source != pair.Address || frame.BindingIntent != source.BindingIntent)
                    { frame.Dispose(); throw new InvalidDataException("The retained window frame does not belong to this package source."); }
                    frames.Add(pair.Address, frame);
                }
        }
        catch { Dispose(); throw; }
    }
    internal WindowSourceFallbackPlan Prepare(CancellationToken token = default)
    {
        var encoded = new Dictionary<WindowFrameId, AssetResource>(); var local = new AssetLibrary(); var replacements = new Dictionary<SourceNodeAddress, AssetResource>();
        foreach (var pair in frames)
        {
            token.ThrowIfCancellationRequested();
            if (!encoded.TryGetValue(pair.Value.Id, out var resource))
            {
                using var png = pair.Value.Image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("Cannot encode a retained window frame.");
                resource = local.Get(local.Add(png.ToArray())); encoded.Add(pair.Value.Id, resource);
            }
            replacements.Add(pair.Key, resource);
        }
        token.ThrowIfCancellationRequested();
        return WindowSourceFallbackPlanner.Prepare(document, replacements, resources);
    }
    public void Dispose() { foreach (var frame in frames.Values) frame.Dispose(); frames.Clear(); }
}

internal sealed record WindowWorkspacePackagePlan(WorkspaceState State, AssetResource[] Resources);

internal sealed class WindowWorkspacePackageCapture : IDisposable
{
    private readonly WorkspaceState workspace;
    private readonly List<(Action<SceneDocument> Set, WindowDocumentPackageCapture Capture)> documents = [];
    internal int FrameCount => documents.Sum(item => item.Capture.FrameCount);
    internal WindowWorkspacePackageCapture(WorkspaceState workspace, IEnumerable<AssetResource> resources,
        Func<SceneDocument, SourceNodeAddress, Guid, WindowFrameLease?> retain)
    {
        this.workspace = workspace.Clone(); var available = resources.ToArray();
        try
        {
            Add(workspace.LiveDocument, d => this.workspace.LiveDocument = d);
            if (workspace.CaptureDocument is { } capture) Add(capture, d => this.workspace.CaptureDocument = d);
            var from = workspace.PendingPrefabEdit; var to = this.workspace.PendingPrefabEdit;
            while (from is not null && to is not null)
            {
                var target = to; Add(from.DraftDocument, d => target.DraftDocument = d); from = from.ParentEdit; to = to.ParentEdit;
            }
        }
        catch { Dispose(); throw; }
        void Add(SceneDocument document, Action<SceneDocument> set) => documents.Add((set,
            new WindowDocumentPackageCapture(document, available, (address, intent) => retain(document, address, intent))));
    }
    internal WindowWorkspacePackagePlan Prepare(CancellationToken token = default)
    {
        var resources = new Dictionary<string, AssetResource>(StringComparer.Ordinal);
        foreach (var (set, capture) in documents)
        {
            token.ThrowIfCancellationRequested(); var plan = capture.Prepare(token); set(plan.Document);
            foreach (var resource in plan.Resources) resources.TryAdd(resource.Id, resource);
        }
        return new(workspace, resources.Values.ToArray());
    }
    public void Dispose() { foreach (var item in documents) item.Capture.Dispose(); documents.Clear(); }
}

internal static class WindowSourcePersistence
{
    internal static IEnumerable<SceneDocument> Documents(WorkspaceState workspace) => new[] { workspace.LiveDocument }
        .Concat(workspace.CaptureDocument is { } capture ? [capture] : [])
        .Concat(PrefabDraftState.Enumerate(workspace.PendingPrefabEdit).Select(state => state.DraftDocument));
    internal static WorkspaceState AuthoredProjection(WorkspaceState workspace)
    {
        var result = workspace.Clone(); result.LiveDocument = AuthoredSceneContent.CreateProjection(result.LiveDocument);
        if (result.CaptureDocument is { } capture) result.CaptureDocument = AuthoredSceneContent.CreateProjection(capture);
        foreach (var state in PrefabDraftState.Enumerate(result.PendingPrefabEdit)) state.DraftDocument = AuthoredSceneContent.CreateProjection(state.DraftDocument);
        return result;
    }
    internal static PrefabPackage CreateSourcePrefabPackage(SceneDocument snapshot, Guid entryId, bool authoredChanged, Guid? savedRevision)
    {
        var package = new PrefabPackage { ResourceId = snapshot.ResourceId, EntryDefinitionId = entryId,
            Definitions = snapshot.Definitions.Select(definition => definition.Clone()).ToList(), Provenance = snapshot.Provenance };
        var entry = package.Definitions.Single(definition => definition.Id == entryId);
        var main = SceneCanvasRoots.ExtractRootContent(snapshot, RootCanvasKind.Main);
        entry.Graph = SceneGraphClone.CloneGraph(main).Graph;
        entry.PreviewCanvas = SubprojectCanvasSettings.FromDocument(main, entry.PreviewCanvas?.Origin ?? entry.Pivot);
        entry.Revision = authoredChanged ? Guid.NewGuid() : savedRevision ?? entry.Revision;
        var root = new PrefabInstance { DefinitionId = entryId };
        package.Definitions = PrefabClosure.ReachableDefinitions(new() { Instances = [root], RootOrder = [root.Id] }, package.Definitions).ToList();
        return package;
    }
}

internal sealed class WindowMediaCheckpointState
{
    private sealed class OwnerState { internal long Generation; }
    private readonly ConditionalWeakTable<EditorSession, OwnerState> owners = new();
    internal long Generation { get; private set; }
    internal long RecoveryGeneration { get; private set; }
    internal DateTime LastCheckpointUtc { get; private set; }
    internal void Record(EditorSession owner, DateTime? now = null)
    {
        if (Generation == 0) LastCheckpointUtc = now ?? DateTime.UtcNow;
        owners.GetValue(owner, static _ => new OwnerState()).Generation = ++Generation;
    }
    internal long ForOwner(EditorSession owner) => owners.TryGetValue(owner, out var state) ? state.Generation : 0;
    internal bool Pending => Generation > RecoveryGeneration;
    internal bool IsDue(DateTime now) => Pending && now - LastCheckpointUtc >= TimeSpan.FromSeconds(30);
    internal void Saved(long generation, DateTime now) { RecoveryGeneration = Math.Max(RecoveryGeneration, generation); LastCheckpointUtc = now; }
}

internal sealed partial class DesktopSession
{
    private readonly WindowMediaCheckpointState _windowMediaCheckpoint = new();
    private DispatcherTimer? _windowMediaTimer;
    partial void ScheduleWindowMediaCheckpoint()
    {
        if (_documentStopped || _disposed) return;
        _windowMediaCheckpoint.Record(Active); EnsureWindowMediaTimer();
    }
    private void EnsureWindowMediaTimer()
    {
        if (_documentStopped || _disposed || !_windowMediaCheckpoint.Pending) return;
        if (_windowMediaTimer is null)
        {
            _windowMediaTimer = new DispatcherTimer(DispatcherPriority.Background);
            _windowMediaTimer.Tick += (_, _) =>
            {
                _windowMediaTimer?.Stop();
                if (_windowMediaCheckpoint.IsDue(DateTime.UtcNow)) ScheduleRecovery(); else EnsureWindowMediaTimer();
            };
        }
        if (!_windowMediaTimer.IsEnabled)
        {
            var remaining = TimeSpan.FromSeconds(30) - (DateTime.UtcNow - _windowMediaCheckpoint.LastCheckpointUtc);
            _windowMediaTimer.Interval = remaining > TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            _windowMediaTimer.Start();
        }
    }
    partial void DisposeWindowMediaCheckpoint() { _windowMediaTimer?.Stop(); _windowMediaTimer = null; }
    private bool HasWindowMediaPending(EditorSession owner) => _windowMediaCheckpoint.ForOwner(owner) > FileState(owner).SavedMediaVersion;
    private WindowDocumentPackageCapture CaptureWindowPackage(EditorSession owner, SceneDocument snapshot, IEnumerable<AssetResource>? resources = null)
        => new(snapshot, resources ?? ResourceSnapshot(snapshot), (address, intent) => TryRetainWindowSourceFrame(owner, address, intent, out var frame) ? frame : null);
    private WindowWorkspacePackageCapture CaptureWindowWorkspace(WorkspaceState snapshot, IEnumerable<AssetResource> resources)
    {
        var owners = new Dictionary<SceneDocument, EditorSession>(ReferenceEqualityComparer.Instance) { [snapshot.LiveDocument] = _live };
        if (snapshot.CaptureDocument is { } capture && _capture is { } captureOwner) owners.Add(capture, captureOwner);
        var pending = snapshot.PendingPrefabEdit; var context = _prefabEditContext;
        while (pending is not null && context is not null)
        {
            if (pending.OwnerResourceId != context.Owner.Document.ResourceId || pending.DefinitionId != context.DefinitionId ||
                pending.ExpectedRevision != context.ExpectedRevision || pending.DraftDocument.ResourceId != context.Session.Document.ResourceId) break;
            owners.Add(pending.DraftDocument, context.Session); pending = pending.ParentEdit; context = context.Parent;
        }
        return new(snapshot, resources, (document, address, intent) => owners.TryGetValue(document, out var owner) &&
            TryRetainWindowSourceFrame(owner, address, intent, out var frame) ? frame : null);
    }
    private async Task<WindowSourceFallbackPlan> PrepareWindowPackageAsync(EditorSession owner, SceneDocument snapshot, IEnumerable<AssetResource>? resources = null)
    {
        using var capture = CaptureWindowPackage(owner, snapshot, resources);
        return await Task.Run(() => capture.Prepare(_documentLifetime.Token), _documentLifetime.Token);
    }
    private async Task<(PrefabPackage Package, AssetLibrary Assets)> PrepareWindowPrefabPackageAsync(EditorSession owner, PrefabPackage package,
        Guid? rootDefinitionId = null, SceneDocument? sourceSnapshot = null)
    {
        var prepared = await PrepareWindowPackageAsync(owner, sourceSnapshot ?? owner.Document.Clone());
        var result = package.Clone();
        var definitions = prepared.Document.Definitions.ToDictionary(d => d.Id);
        foreach (var definition in result.Definitions)
            if (definition.Id == rootDefinitionId) definition.Graph = SceneGraphClone.CloneGraph(prepared.Document).Graph;
            else if (definitions.TryGetValue(definition.Id, out var source)) definition.Graph = SceneGraphClone.CloneGraph(source.Graph).Graph;
        var available = prepared.Resources.ToDictionary(r => r.Id, StringComparer.Ordinal);
        return (result, ResourceLibrary(result.ReferencedAssets.Select(id => available.TryGetValue(id, out var resource)
            ? resource : throw new InvalidDataException("A prefab snapshot resource is missing."))));
    }
}
