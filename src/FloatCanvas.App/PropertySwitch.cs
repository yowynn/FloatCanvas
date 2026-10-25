using System.Windows.Controls;

namespace FloatCanvas.App;

/// <summary>Mixed selection is displayed, but a user action always chooses a definite value.</summary>
internal sealed class PropertySwitch : CheckBox
{
    protected override void OnToggle() => SetCurrentValue(IsCheckedProperty, IsChecked != true);
}
