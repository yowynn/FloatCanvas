using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed record EditingScopeDescription(string Path, string Mode, string State, IReadOnlyList<(string Name, Action Navigate)> Ancestors, string CurrentName,
    ScopeNavigationPath? Navigation = null, Action? ToggleMode = null, Func<bool>? CanToggleMode = null, object? ContextKey = null, bool CanBrowse = true, bool CanAuthor = true, string? ReadOnlyReason = null)
{
    internal string Caption => Path + " · " + Mode + (State.Length > 0 ? " · " + State : "");
}

internal sealed partial class DesktopSession
{
    private ScopeNavigationHistory? _scopeNavigationHistory;
    private (EditorSession Owner, long Generation, long Version, RootCanvasKind Root, PrefabEditContext? Context, GroupCanvasMode Mode)? _shownEditingScope;
    private (EditorSession, long, long, RootCanvasKind, PrefabEditContext?, GroupCanvasMode) EditingScopeStamp =>
        (Active, Active.DocumentGeneration, Active.EditScopeVersion, Active.ActiveRootCanvas, _prefabEditContext, _groupCanvasMode);
    private void RefreshEditingScopeIfChanged()
    {
        if (_shownEditingScope != EditingScopeStamp) RefreshEditingScope();
    }
    private EditingScopeDescription DescribeEditingScope()
    {
        var owner = Active; var context = _prefabEditContext;
        var chain = context?.Chain().Reverse().ToArray() ?? [];
        var rootOwner = chain.FirstOrDefault()?.Owner ?? owner;
        var entries = new List<ScopeNavigationEntry>(); var boundaries = new List<ScopeBoundary>();
        void AddSession(EditorSession session, string name, string glyph)
        {
            void Add(string caption, string artwork, Guid[] groups)
            {
                var address = new ScopeAddress(rootOwner, rootOwner.DocumentGeneration, rootOwner.ActiveRootCanvas, boundaries.ToArray(), groups);
                bool local = ReferenceEquals(session, owner);
                string key = string.Join('/', boundaries.Select(b => (b.InstanceId ?? b.DefinitionId).ToString())) + ":" + string.Join('/', groups);
                entries.Add(new(key, caption, artwork, () => _ = RunCommand(() => NavigateScopeAddressAsync(address)), groups.LastOrDefault() is var id && id != Guid.Empty ? id : null,
                    local, () => ResolveScopeAddress(address) is not null, () => CanNavigateScopeAddress(address),
                    () => ScopeAddressName(address, caption),
                    () => IsLocalScopeAddress(address)));
            }
            Add(name, glyph, []);
            var path = new List<Guid>();
            foreach (var group in session.EditScopePath) { path.Add(group.Id); Add(ScopeGroupName(group), LayersWindow.Folder, path.ToArray()); }
        }
        string rootName = L.Get("RootCanvas." + rootOwner.ActiveRootCanvas), rootGlyph = ScopeNavigation.RootGlyphFor(rootOwner.ActiveRootCanvas);
        foreach (var item in chain)
        {
            AddSession(item.Owner, rootName, rootGlyph);
            boundaries.Add(new(item.DefinitionId, item.EnteredInstanceId, item.ReturnScopeId,
                item.ContentOrigin == PrefabContentOrigin.ExternalSource ? item.Session.Document : null, item.ExpectedRevision));
            rootName = item.DisplayName;
            rootGlyph = item.ContentOrigin is PrefabContentOrigin.ExternalSource or PrefabContentOrigin.SavedSnapshot ? LayersWindow.LinkedInstance : LayersWindow.Instance;
        }
        AddSession(owner, rootName, rootGlyph);
        var navigation = (_scopeNavigationHistory ??= new()).Update((rootOwner, rootOwner.DocumentGeneration, rootOwner.ActiveRootCanvas), entries);
        var ancestors = entries.SkipLast(1).Select(e => (e.DisplayName, e.Navigate)).ToArray();
        string path = string.Join(" › ", entries.Select(e => e.DisplayName));
        long modeScopeVersion = owner.EditScopeVersion, modeGeneration = owner.DocumentGeneration;
        bool CanToggleMode() => ReferenceEquals(owner, Active) && ReferenceEquals(context, _prefabEditContext) && owner.DocumentGeneration == modeGeneration && owner.EditScopeVersion == modeScopeVersion &&
            !DocumentOperationBusy && !owner.IsTransactionActive;
        bool lockedBrowsing = context?.LockedBrowsing == true || owner.EditScopeId is { } lockedScope && owner.IsNodeLocked(lockedScope);
        bool inheritedLock = context?.InheritedLock == true || owner.EditScopeId is { } currentGroup && owner.Document.Groups.First(g => g.Id == currentGroup).Locked == false && lockedBrowsing;
        string? readOnlyReason = lockedBrowsing ? L.Get(inheritedLock ? "LayerExperience.AncestorLockedBrowse" : "LayerExperience.LockedBrowse") : owner.IsReadOnly ? L.Get("EditingScope.ReadOnly") : null;
        return new(path, L.Get(_groupCanvasMode == GroupCanvasMode.Temporary ? "GroupCanvas.Temporary" : "GroupCanvas.RootProject"),
            lockedBrowsing ? readOnlyReason! : owner.IsReadOnly ? L.Get(context?.IsOfflineSnapshot == true ? context.SnapshotReason ?? "GroupCanvas.SnapshotStatus" : context?.ContentOrigin == PrefabContentOrigin.ExternalSource ? "GroupCanvas.LinkedStatus" : "EditingScope.ReadOnly") : context is not null ? L.Get("EditingScope.Shared") : "", ancestors, entries[^1].DisplayName,
            navigation, () => { if (CanToggleMode()) SetGroupCanvasMode(_groupCanvasMode == GroupCanvasMode.Temporary ? GroupCanvasMode.RootProject : GroupCanvasMode.Temporary); }, CanToggleMode,
            (owner, owner.DocumentGeneration, owner.EditScopeVersion, context), CanAuthor: readOnlyReason is null, ReadOnlyReason: readOnlyReason);
    }
    private void RefreshEditingScope()
    {
        bool changed = _shownEditingScope != EditingScopeStamp;
        _shownEditingScope = EditingScopeStamp;
        if (changed) RefreshBoundFileIndicator();
        var scope = DescribeEditingScope();
        _rootCanvasHeader?.SetScope(scope, Active.EditScopeId is not null || _prefabEditContext is not null);
        if (changed) _layers?.PrepareForShow();
        _layers?.SetNavigation(scope);
        if (changed && _canvasSettingsWindow is not null) RefreshCanvasSettingsTarget();
        if (_toolbarLauncher is not null) _toolbarLauncher.ToolTip = scope.Caption + "\n" + L.Get("ToolbarV2.LauncherHint");
    }
    private static string ScopeGroupName(CanvasGroup group) => string.IsNullOrWhiteSpace(group.Name) ? L.Get("Layers.UntitledGroup") : group.Name;
    // Online project browsing uses the original source graph, whereas the stored
    // import may contain a wrapper and remapped nodes. Retain only this branch's
    // last loaded read-only graph; reopening still reloads and validates the source.
    private sealed record ScopeBoundary(Guid DefinitionId, Guid? InstanceId, Guid? OwnerScope,
        SceneDocument? LoadedSource = null, Guid SourceRevision = default);
    private sealed record ScopeAddress(EditorSession RootOwner, long Generation, RootCanvasKind Root, ScopeBoundary[] Boundaries, Guid[] Groups);

    private SceneNodeGraph? ResolveScopeAddress(ScopeAddress address, bool checkLocks = false)
    {
        var chain = _prefabEditContext?.Chain().Reverse().ToArray() ?? [];
        var root = chain.FirstOrDefault()?.Owner ?? Active;
        if (!ReferenceEquals(root, address.RootOwner) || root.DocumentGeneration != address.Generation || root.ActiveRootCanvas != address.Root) return null;
        SceneNodeGraph graph = root.Document; var definitions = root.Document.Definitions; bool livePrefix = true;
        for (int i = 0; i < address.Boundaries.Length; i++)
        {
            var boundary = address.Boundaries[i];
            if (boundary.InstanceId is { } id && !graph.Instances.Any(n => n.Id == id && n.DefinitionId == boundary.DefinitionId && n.ParentId == boundary.OwnerScope)) return null;
            var definition = definitions.FirstOrDefault(d => d.Id == boundary.DefinitionId); if (definition is null) return null;
            livePrefix = livePrefix && i < chain.Length && chain[i].DefinitionId == boundary.DefinitionId && chain[i].EnteredInstanceId == boundary.InstanceId;

            if (livePrefix)
            { graph = chain[i].Session.Document; definitions = chain[i].Session.Document.Definitions; }
            else if (definition.Source?.IsLinked == true && definition.Revision == boundary.SourceRevision && boundary.LoadedSource is { } source)
            { graph = source; definitions = source.Definitions; }
            else graph = definition.Graph;
        }
        Guid? parent = null;
        foreach (var id in address.Groups)
        { var group = graph.Groups.FirstOrDefault(g => g.Id == id); if (group is null || group.ParentId != parent) return null; parent = id; }
        if (address.Boundaries.Length == 0 && parent is { } node && SceneCanvasRoots.Resolve(root.Document, node) != address.Root) return null;
        return graph;
    }
    private bool CanNavigateScopeAddress(ScopeAddress address) => !DocumentOperationBusy && !Active.IsTransactionActive && ResolveScopeAddress(address) is not null;

    private bool IsLocalScopeAddress(ScopeAddress address)
    {
        var chain = _prefabEditContext?.Chain().Reverse().ToArray() ?? [];
        return address.Boundaries.Length == chain.Length && address.Boundaries.Select((b,i) => b.DefinitionId == chain[i].DefinitionId && b.InstanceId == chain[i].EnteredInstanceId).All(matches => matches);
    }

    private string ScopeAddressName(ScopeAddress address, string fallback)
    {
        if (address.Groups.Length > 0)
            return ResolveScopeAddress(address)?.Groups.FirstOrDefault(g => g.Id == address.Groups[^1]) is { } group ? ScopeGroupName(group) : fallback;
        if (address.Boundaries.Length == 0) return L.Get("RootCanvas." + address.Root);
        var boundary = address.Boundaries[^1];
        var owner = ResolveScopeAddress(address with { Boundaries = address.Boundaries[..^1], Groups = [] });
        if (boundary.InstanceId is { } id && owner?.Instances.FirstOrDefault(i => i.Id == id) is { Name.Length: > 0 } instance) return instance.Name;
        return fallback;
    }

    private async Task NavigateScopeAddressAsync(ScopeAddress address)
    {
        if (!CanNavigateScopeAddress(address)) return;
        _layers?.CompletePendingNavigation(); _inspector?.CompletePendingEdit(); FinishInlineText(); _editor.Cancel();
        var chain = _prefabEditContext?.Chain().Reverse().ToArray() ?? [];
        int common = 0;
        while (common < chain.Length && common < address.Boundaries.Length && chain[common].DefinitionId == address.Boundaries[common].DefinitionId && chain[common].EnteredInstanceId == address.Boundaries[common].InstanceId) common++;
        while ((_prefabEditContext?.Chain().Count() ?? 0) > common)
            if (!await CompleteCurrentPrefabAsync()) return;
        for (int i = common; i < address.Boundaries.Length; i++)
        {
            if (ResolveScopeAddress(address) is null) return;
            var boundary = address.Boundaries[i]; var owner = Active;
            owner.SetEditScope(boundary.OwnerScope);
            await EditPrefabDefinitionCoreAsync(boundary.DefinitionId, boundary.InstanceId);
            if (ReferenceEquals(Active, owner)) return;
        }
        if (!CanNavigateScopeAddress(address)) return;
        Guid? scope = address.Groups.Length == 0 ? null : address.Groups[^1];
        var child = Active.EditScopePath.FirstOrDefault(g => g.ParentId == scope)?.Id;
        Active.SetEditScope(scope); if (child is { } selected) Active.SetSelection([selected]);
        OnEditorChanged(); RefreshEditingScope(); RefreshPrefabWindows();
    }
}
