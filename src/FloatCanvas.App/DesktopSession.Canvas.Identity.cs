using System.Runtime.CompilerServices;
using FloatCanvas.Core;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private sealed class CanvasIdentity(string document, string canvasNamespace)
    {
        internal string Document { get; } = document;
        internal string Namespace { get; set; } = canvasNamespace;
        internal Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);
    }
    private ConditionalWeakTable<EditorSession, CanvasIdentity>? _canvasIdentities;
    private static string CanvasDocumentIdentity(EditorSession session) => $"{session.Document.ResourceId:N}:{session.Document.Kind}";
    private static string CanvasFileNamespace(EditorSession session, string path) =>
        $"file:{System.IO.Path.GetFullPath(path).ToUpperInvariant()}#{(session.Document.ResourceIdentityPersistent ? CanvasDocumentIdentity(session) : $"legacy:{session.Document.Kind}")}";

    private CanvasIdentity CanvasIdentityFor(EditorSession session)
    {
        _canvasIdentities ??= new();
        string document = CanvasDocumentIdentity(session);
        if (_canvasIdentities.TryGetValue(session, out var prior))
        {
            if (prior.Document == document) return prior;
            _canvasIdentities.Remove(session);
        }
        string? path = _documentFiles?.GetValueOrDefault(session)?.Path;
        string space = path is not null ? CanvasFileNamespace(session, path) :
            _canvasViews?.WorkspaceNamespace(document) ?? $"workspace:{document}";
        var identity = new CanvasIdentity(document, space); _canvasIdentities.Add(session, identity);
        _canvasViews?.BindWorkspace(document, space); return identity;
    }

    private string CanvasEditingNamespace()
    {
        if (_prefabEditContext is not { } context) return CanvasIdentityFor(Active).Namespace;
        var chain = context.Chain().ToArray();
        var source = chain.FirstOrDefault(item => item.SourcePath is not null);
        string space = source is not null ? SourceCanvasNamespace(source, source.SourcePath!) :
            CanvasIdentityFor(chain[^1].Owner).Namespace;
        return ReferenceEquals(source, context) ? space : $"{space}/definition:{context.DefinitionId:N}";
    }

    private void BindCanvasIdentity(EditorSession session, string target, bool copyViews, bool overwriteViews = true)
    {
        _canvasViews ??= new(ApplicationPaths.ProfileDirectory);
        var identity = CanvasIdentityFor(session); string old = identity.Namespace;
        if (copyViews && old != target)
        {
            CopyCanvasViews(identity, old, target, overwriteViews, IsCurrentCanvasIdentityOwner(session));
        }
        identity.Namespace = target; _canvasViews.BindWorkspace(identity.Document, target);
        _ = SaveCanvasViewingAsync();
    }

    private bool IsCurrentCanvasIdentityOwner(EditorSession session) => ReferenceEquals(Active, session) ||
        _prefabEditContext?.Chain().Any(item => ReferenceEquals(item.Owner, session) || ReferenceEquals(item.Session, session)) == true;

    private void CopyCanvasViews(CanvasIdentity identity, string old, string target, bool overwrite, bool rewriteCurrentKey)
    {
        if (old == target) return;
        RememberCanvasViewing(); _canvasViews!.CopyNamespace(old, target, overwrite);
        foreach (var alias in identity.Aliases.Keys.ToArray()) if (identity.Aliases[alias] == old) identity.Aliases[alias] = target;
        identity.Aliases[old] = target;
        if (rewriteCurrentKey && _canvasViewKey?.StartsWith(old + "/", StringComparison.Ordinal) == true)
        { _canvasViewKey = target + _canvasViewKey[old.Length..]; _canvasViewEpoch = new(); }
    }

    private static string SourceCanvasNamespace(PrefabEditContext context, string path)
    {
        var resource = context.Owner.Document.Definitions.FirstOrDefault(item => item.Id == context.DefinitionId)?.Source?.SourceResourceId ?? context.Session.Document.ResourceId;
        return $"source:{System.IO.Path.GetFullPath(path).ToUpperInvariant()}#{resource:N}";
    }
    private void CopySourceCanvasViews(PrefabEditContext context, string oldPath, string newPath)
    {
        _canvasViews ??= new(ApplicationPaths.ProfileDirectory);
        CopyCanvasViews(CanvasIdentityFor(context.Session), SourceCanvasNamespace(context, oldPath), SourceCanvasNamespace(context, newPath), true, IsCurrentCanvasIdentityOwner(context.Session));
        _ = SaveCanvasViewingAsync();
    }

    private string CanvasHistoryKey(string key)
    {
        var identity = CanvasIdentityFor(Active);
        foreach (var alias in identity.Aliases)
            if (key.StartsWith(alias.Key + "/", StringComparison.Ordinal)) return alias.Value + key[alias.Key.Length..];
        return key;
    }
}
