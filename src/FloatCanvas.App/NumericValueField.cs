using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;

namespace FloatCanvas.App;

/// <summary>One numeric value with precise text entry and a common-range slider. Synchronization is silent.</summary>
internal sealed class NumericValueField : UserControl
{
    private readonly double minimum, maximum;
    private readonly bool integerOnly;
    private bool synchronizing;
    private double value;
    internal TextBox Input { get; }
    internal Slider Slider { get; }
    internal event Action<double>? ValueChanged;
    internal event Action? InvalidInput;
    internal event Action? SliderValueCommitted;

    internal NumericValueField(double minimum, double maximum, double? sliderMinimum = null, double? sliderMaximum = null, double step = 1, bool integerOnly = false)
        : this(new TextBox { MinHeight = 30 }, minimum, maximum, sliderMinimum, sliderMaximum, step, integerOnly) { }

    internal NumericValueField(TextBox input, double minimum, double maximum, double? sliderMinimum = null, double? sliderMaximum = null, double step = 1, bool integerOnly = false)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum < minimum || step <= 0 || !double.IsFinite(step)) throw new ArgumentOutOfRangeException(nameof(minimum));
        this.minimum = minimum; this.maximum = maximum; this.integerOnly = integerOnly; Input = input;
        if(OverlayWindowScope.Current is not null)InputMethod.SetIsInputMethodEnabled(Input,false);
        Slider = new Slider { Minimum = Math.Clamp(sliderMinimum ?? minimum, minimum, maximum),
            Maximum = Math.Clamp(sliderMaximum ?? maximum, minimum, maximum), SmallChange = step, LargeChange = step * 10,
            TickFrequency = step, IsSnapToTickEnabled = integerOnly, IsMoveToPointEnabled = true, MinHeight = 20, Margin = new(1, 1, 1, 0) };
        if (Slider.Maximum < Slider.Minimum) throw new ArgumentOutOfRangeException(nameof(sliderMaximum));
        string label = AutomationProperties.GetName(Input);
        if (!string.IsNullOrWhiteSpace(label)) AutomationProperties.SetName(Slider, label);
        var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.Children.Add(Input); Grid.SetRow(Slider, 1); panel.Children.Add(Slider); Content = panel;
        Input.TextChanged += (_, _) =>
        {
            if (synchronizing) return;
            if (!TryGetValue(out double next)) { InvalidInput?.Invoke(); return; }
            synchronizing = true;
            try { Slider.Value = Math.Clamp(next, Slider.Minimum, Slider.Maximum); }
            finally { synchronizing = false; }
            if (value == next) return; value = next; ValueChanged?.Invoke(next);
        };
        Slider.ValueChanged += (_, _) =>
        {
            if (synchronizing) return;
            double next = Math.Round(Math.Clamp(Slider.Value, minimum, maximum), integerOnly ? 0 : 3);
            synchronizing = true;
            try { Slider.Value = next; Input.Text = next.ToString("0.###", CultureInfo.CurrentCulture); }
            finally { synchronizing = false; }
            if (value == next) return; value = next; ValueChanged?.Invoke(next);
            SliderValueCommitted?.Invoke();
        };
        value = minimum;
        if (TryGetValue(out double initial)) value = initial;
        Slider.Value = Math.Clamp(value, Slider.Minimum, Slider.Maximum);
    }

    internal double Value
    {
        get => value;
        set
        {
            if (!double.IsFinite(value) || value < minimum || value > maximum || (integerOnly && value != Math.Truncate(value))) throw new ArgumentOutOfRangeException(nameof(value));
            synchronizing = true;
            try { this.value = value; Input.Text = value.ToString("0.###", CultureInfo.CurrentCulture); Slider.Value = Math.Clamp(value, Slider.Minimum, Slider.Maximum); }
            finally { synchronizing = false; }
        }
    }

    internal bool TryGetValue(out double result) =>
        (double.TryParse(Input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out result) || double.TryParse(Input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) &&
        double.IsFinite(result) && result >= minimum && result <= maximum && (!integerOnly || result == Math.Truncate(result));
}
