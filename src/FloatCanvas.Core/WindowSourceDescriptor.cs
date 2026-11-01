namespace FloatCanvas.Core;

public enum WindowSourceFit { Contain, Stretch }

/// <summary>Authored connection intent and bounded display hints; never a runtime window identity.</summary>
public sealed class WindowSourceDescriptor
{
    public Guid BindingIntent { get; set; }
    public string TitleHint { get; set; } = "";
    public string ProcessNameHint { get; set; } = "";
    public string WindowClassHint { get; set; } = "";
    public WindowSourceFit Fit { get; set; }

    public WindowSourceDescriptor Clone() => (WindowSourceDescriptor)MemberwiseClone();
    public bool IsValid() => BindingIntent != Guid.Empty && Enum.IsDefined(Fit)
        && ValidHint(TitleHint, 512) && ValidHint(ProcessNameHint, 260) && ValidHint(WindowClassHint, 256);

    private static bool ValidHint(string? value, int maximumLength) => value is not null && value.Length <= maximumLength
        && !value.Any(char.IsControl);
}

public static class WindowSourceLimits
{
    public static bool IsValidElement(CanvasElement element) => element.WindowSource is not { } source ||
        element.Kind == ElementKind.Image && source.IsValid() && double.IsFinite(element.Width) && element.Width > 0 &&
        double.IsFinite(element.Height) && element.Height > 0;

    public static long EstimateBytes(WindowSourceDescriptor? source) => source is null ? 0 :
        128L + ((source.TitleHint?.Length ?? 0) + (source.ProcessNameHint?.Length ?? 0) + (source.WindowClassHint?.Length ?? 0)) * 2L;
}
