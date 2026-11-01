namespace FloatCanvas.Core;

public sealed record CanvasAspectPreset(string Key, double? Aspect);
public static class CanvasAspectPresets
{
    public static IReadOnlyList<CanvasAspectPreset> All { get; } =
    [
        new("Canvas.AspectCustom", null), new("Canvas.AspectA4", 210.0 / 297), new("Canvas.AspectA5", 148.0 / 210),
        new("Canvas.AspectLetter", 8.5 / 11), new("Canvas.AspectLegal", 8.5 / 14),
        new("Canvas.AspectSquare", 1), new("Canvas.Aspect43", 4.0 / 3), new("Canvas.Aspect169", 16.0 / 9)
    ];
}
