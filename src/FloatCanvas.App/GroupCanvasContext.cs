namespace FloatCanvas.App;

internal enum GroupCanvasMode { Temporary, RootProject }
internal enum GroupStorageKind { Ordinary, Embedded, ExternalFile }

/// <summary>The current group's presentation and owner-supplied operations.</summary>
internal sealed record GroupCanvasContext(
    string Name,
    GroupStorageKind Storage,
    string? SourcePath,
    GroupCanvasMode Mode,
    bool IsEntered,
    Action? Temporary = null,
    Action? Root = null,
    Action? Export = null,
    Action? Detach = null,
    bool IsReadOnly = false,
    bool IsRootContext = false,
    Action? Embed = null);
