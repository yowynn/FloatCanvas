namespace FloatCanvas.Core;

/// <summary>Stable project identifiers for the selectable emoji artwork sources.</summary>
public static class EmojiFontIds
{
    public const string SystemSegoe = "system.segoe-ui-emoji";
    public const string NotoColor = "noto.color-emoji.colrv1";
    public const string OpenMojiColor = "openmoji.color.colrv0";
    public const string BlobmojiColor = "blobmoji.color.colrv1";
    public const string Default = SystemSegoe;

    public static bool IsKnown(string? id) => id is SystemSegoe or NotoColor or OpenMojiColor or BlobmojiColor;
    public static string Normalize(string? id) => IsKnown(id) ? id! : Default;
}
