namespace FloatCanvas.App;

/// <summary>An async window request belongs to the mode and document that initiated it.</summary>
internal readonly record struct WindowSessionRequest(long Version, bool Editing, object Document)
{
    internal bool IsCurrent(long version, bool editing, object document) =>
        Version == version && Editing == editing && ReferenceEquals(Document, document);
}
