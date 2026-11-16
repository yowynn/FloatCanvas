namespace FloatCanvas.App;

/// <summary>Keyboard increments are independent of the pointer drag sensitivity.</summary>
internal static class NumericKeyboardSteps
{
    internal static double ForProperty(string key, EditorTool? tool = null)
    {
        if (key.EndsWith("Milliseconds", StringComparison.Ordinal)) return 10;
        if (key is "TrailWidthPixels" || key.EndsWith(".StrokeWidthPixels", StringComparison.Ordinal)) return .1;
        if (key == "EffectStrength") return tool == EditorTool.Pixelate ? 1 : .1;
        return 1;
    }
}
