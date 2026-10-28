namespace FloatCanvas.App;

/// <summary>Persisted tool domains are distinct from the rail's useful working range.</summary>
internal readonly record struct NumericParameterDomain(double Minimum, double Maximum, double RailMinimum, double RailMaximum, double Step = 1, bool Integer = false)
{
    internal bool Contains(double value) => double.IsFinite(value) && value >= Minimum && value <= Maximum && (!Integer || value == Math.Truncate(value));
    internal static NumericParameterDomain ForTool(string key) => key switch
    {
        "StrokeWidth" or "CaptureBorderWidth" => new(0, 1024, 0, 64, .1),
        "Opacity" => new(0, 100, 0, 100),
        "FontSize" => new(double.Epsilon, 4096, 1, 512),
        "NumberStart" => new(-999999, 999999, 1, 100, Integer: true),
        "NumberStep" => new(-999999, 999999, -100, 100, Integer: true),
        "NextNumber" => new(int.MinValue, int.MaxValue, 1, 100, Integer: true),
        "DefaultWidth" or "DefaultHeight" => new(1, 1000000, 1, 600),
        "ArrowHeadSize" => new(0, 512, 0, 100),
        "CornerRadius" => new(0, double.MaxValue, 0, 100),
        "EffectStrength" => new(double.Epsilon, 1024, .01, 100, .1),
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };
    internal static NumericParameterDomain ForStyle(string key) => key == "Opacity" ? new(0, 1, 0, 1) : ForTool(key);
}
