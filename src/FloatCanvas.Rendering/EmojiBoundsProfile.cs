using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

internal readonly record struct EmojiFontFrame(string FontId, float CenterXEm, float CenterYEm, string ExpectedSha256);
internal readonly record struct EmojiBoundsProfileMismatch(string FontId, string ExpectedSha256, string ActualSha256);

/// <summary>Generated inputs for the robust shared emoji frame. Regenerate when any fingerprint changes.</summary>
internal static class EmojiBoundsProfile
{
    internal const int AlgorithmVersion = 1;
    internal const string CatalogVersion = "18.0";
    internal const string CatalogSha256 = "80F8DED6CA21066FF96105A9507E5BE862DE368ACA0075656D48851FAF256C28";
    internal const string SurveyDate = "2026-11-22";
    internal const float SideEm = 1.27734375f;
    internal const string OutlierPolicy =
        "Exclude isolated malformed advance/ink outliers; fit any excluded glyph around the frame center at render time.";

    internal static readonly IReadOnlyList<EmojiFontFrame> Frames =
    [
        new(EmojiFontIds.SystemSegoe, .6865234375f, -.4140625f,
            "451DF9BB5546055AF625FF86AA72695DF43E8E170B4C6C1316FE44E9C7A22F7C"),
        new(EmojiFontIds.NotoColor, .623046875f, -.341796875f,
            "B8E25EA68DB82F9E4D0AEE921F4420BE2BE39887BD5C893A2AD98710531F9D0C"),
        new(EmojiFontIds.OpenMojiColor, .64453125f, -.3759765625f,
            "036934C52C573F8A6208B15E971B7F26E8B1F494AAA901A0D9231F04BC01F587"),
        new(EmojiFontIds.BlobmojiColor, .623046875f, -.341796875f,
            "CB1A3C09DCDC12AC4B6833BEA1A3ABE75F7D2357A266064B439B9D4975FE83C5")
    ];

    internal static EmojiFontFrame For(string? fontId)
    {
        string normalized = EmojiFontIds.Normalize(fontId);
        return Frames.Single(frame => frame.FontId == normalized);
    }

    internal static SKRect Frame(string? fontId, float referenceSize)
    {
        var definition = For(fontId);
        float half = SideEm * referenceSize / 2;
        float x = definition.CenterXEm * referenceSize;
        float y = definition.CenterYEm * referenceSize;
        return new(x - half, y - half, x + half, y + half);
    }

    internal static IReadOnlyList<EmojiBoundsProfileMismatch> FingerprintMismatches()
    {
        var mismatches = new List<EmojiBoundsProfileMismatch>();
        foreach (var frame in Frames)
        {
            string actual = EmojiFontSources.Sha256(frame.FontId);
            if (actual.Length == 0 || string.Equals(actual, frame.ExpectedSha256, StringComparison.Ordinal)) continue;
            mismatches.Add(new(frame.FontId, frame.ExpectedSha256, actual));
        }
        return mismatches;
    }
}
