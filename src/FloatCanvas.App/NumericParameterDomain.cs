namespace FloatCanvas.App;

/// <summary>Persisted tool domains are distinct from the rail's useful working range.</summary>
internal readonly record struct NumericParameterDomain(double Minimum, double Maximum, double RailMinimum, double RailMaximum, double Step = 1, bool Integer = false)
{
    internal bool Contains(double value) => double.IsFinite(value) && value >= Minimum && value <= Maximum && (!Integer || value == Math.Truncate(value));
    internal static NumericParameterDomain ForTool(string key) => key switch
    {
        "StrokeWidth" => new(0, 1024, 0, 32, .1),
        "CaptureBorderWidth" => new(0, 1024, 0, 16, .1),
        "Opacity" => new(0, 100, 0, 100),
        "FontSize" => new(double.Epsilon, 4096, 8, 144),
        "NumberStart" => new(-999999, 999999, 1, 100, Integer: true),
        "NumberStep" => new(-999999, 999999, -10, 10, Integer: true),
        "NextNumber" => new(int.MinValue, int.MaxValue, 1, 100, Integer: true),
        "DefaultWidth" or "DefaultHeight" => new(1, 1000000, 1, 600),
        "ArrowHeadSize" => new(0, 512, 0, 64),
        "CornerRadius" => new(0, double.MaxValue, 0, 64),
        "EffectStrength" => new(double.Epsilon, 1024, .1, 64, .1),
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };
    internal static NumericParameterDomain ForTool(string key, EditorTool tool)
    {
        var domain = ForTool(key);
        return (key, tool) switch
        {
            ("StrokeWidth", EditorTool.Highlighter) => domain with { RailMinimum = 16, RailMaximum = 64 },
            ("FontSize", EditorTool.Number or EditorTool.Emoji) => domain with { RailMinimum = 16, RailMaximum = 256 },
            ("EffectStrength", EditorTool.Pixelate) => domain with { RailMinimum = 1 },
            _ => domain
        };
    }
    internal static NumericParameterDomain ForStyle(string key) => key == "Opacity" ? new(0, 1, 0, 1) : ForTool(key);
}
