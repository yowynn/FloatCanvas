using System.Collections.Immutable;
using FloatCanvas.Core;

namespace FloatCanvas.App;

/// <summary>Immutable tool and preset settings. Placing a custom marker copies just one string.</summary>
internal sealed record NumberSequenceSettings
{
    internal static NumberSequenceSettings Default { get; } = new();
    public NumberFormat Format { get; init; }
    public ImmutableArray<string> Items { get; init; } = [];
    public int FirstNumber { get; init; } = 1;
    public bool Cycle { get; init; }

    internal bool IsValid() => Enum.IsDefined(Format) && !Items.IsDefault &&
        Items.All(item => !string.IsNullOrWhiteSpace(item) && NumberSequence.IsSingleLineLabel(item));

    internal string Label(int number)
    {
        if (Format != NumberFormat.Custom) return NumberSequence.Format(number, Format);
        long index = (long)number - FirstNumber;
        if (Cycle && Items.Length > 0) index = (index % Items.Length + Items.Length) % Items.Length;
        return index >= 0 && index < Items.Length ? Items[(int)index] : NumberSequence.Format(number, NumberFormat.Decimal);
    }

    internal void ApplyTo(CanvasElement marker)
    {
        marker.NumberFormat = Format;
        if (Format == NumberFormat.Custom) marker.Text = Label(marker.Number);
    }
}
