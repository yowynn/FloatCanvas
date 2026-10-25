using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>The persisted aspect preference, including a mixed multi-selection.</summary>
internal sealed class AspectRatioButton : ToggleButton
{
    internal AspectRatioButton()
    {
        Width = Height = 28; MinWidth = MinHeight = 0; Margin = new(4, 0, 4, 0);
        HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(StyleProperty, "CanvasAspectButton");
        AutomationProperties.SetName(this, L.Get("Inspector.KeepAspect"));
        Checked += (_, _) => UpdateDescription(); Unchecked += (_, _) => UpdateDescription(); Indeterminate += (_, _) => UpdateDescription();
        UpdateDescription();
    }
    protected override void OnToggle() => IsChecked = IsChecked != true;
    private void UpdateDescription()
    {
        string state = L.Get(IsChecked is null ? "Property.AspectMixed" : IsChecked == true ? "Property.AspectLocked" : "Property.AspectUnlocked");
        ToolTip = L.Get("Inspector.KeepAspect") + " — " + state;
        AutomationProperties.SetHelpText(this, (string)ToolTip);
    }
}
