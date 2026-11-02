using FloatCanvas.Core;

namespace FloatCanvas.App;

/// <summary>A link observation is reusable only for the same locator, resource and accepted snapshot.</summary>
internal readonly record struct PrefabLinkStatusBinding(string? Locator, bool IsRelative, Guid ResourceId,
    PrefabSourceKind EntryKind, Guid? EntryId, string? AcceptedFileSha256, Guid? AcceptedRevision)
{
    internal static PrefabLinkStatusBinding Capture(ExternalPrefabSource source) => new(source.Locator, source.IsRelative,
        source.SourceResourceId, source.EntryKind, source.EntryDefinitionId, source.LastAcceptedFileSha256, source.LastAcceptedContentRevision);
    internal bool Matches(ExternalPrefabSource? source) => source is { IsLinked: true } && Capture(source) == this;
}
