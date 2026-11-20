using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class ToolOptionsPanel
{
    private readonly NumberFormatPicker numberFormatPicker = new();
    private readonly Button editNumberSequence = new()
    {
        Content = L.Get("Numbering.Edit"),
        MinHeight = PropertyPanelLayout.ControlHeight,
        Padding = new(10, 2, 10, 2),
        Visibility = Visibility.Collapsed
    };

    private readonly Button resetNextNumber = new()
    {
        Width = PropertyPanelLayout.ControlHeight, Height = PropertyPanelLayout.ControlHeight,
        MinWidth = PropertyPanelLayout.ControlHeight, MinHeight = PropertyPanelLayout.ControlHeight,
        Padding = new(0), Content = ActionIconButton.Glyph(PanelActionIcon.Refresh)
    };

    private void InitializeNumberReset()
    {
        string label = L.Get("Numbering.ResetNext");
        resetNextNumber.ToolTip = label;
        AutomationProperties.SetName(resetNextNumber, label);
        resetNextNumber.Click += (_, _) =>
        {
            if (controller.Tool != EditorTool.Number || controller.Session.IsReadOnly || controller.Session.IsTransactionActive) return;
            SetNextNumber(controller.NumberStart);
            numericFields["NextNumber"].SetModelValue(controller.Session.Document.NextNumber);
            ClearStatus(); ParametersChanged?.Invoke();
        };
    }

    private void RefreshNumberReset() =>
        resetNextNumber.IsEnabled = !controller.Session.IsReadOnly && !controller.Session.IsTransactionActive;

    private void SetNextNumber(int value)
    {
        var session = controller.Session;
        if (session.Document.NextNumber != value)
            session.ExecuteSettings(L.Get("History.NumberCounter"), document => document.NextNumber = value);
    }

    private void AddNumberFormatRow()
    {
        AutomationProperties.SetName(editNumberSequence, L.Get("Numbering.Edit"));
        AddRow(parameterGrid, "NumberFormat", L.Get("Numbering.Format"), PropertyPanelLayout.WithTrailingAction(numberFormatPicker, editNumberSequence));
        numberFormatPicker.SelectionChanged += (_, _) =>
        {
            if (refreshing || controller.Tool != EditorTool.Number || numberFormatPicker.SelectedValue is not NumberFormat format) return;
            Change(p => p.Numbering = controller.Numbering with { Format = format });
            Refresh();
        };
        editNumberSequence.Click += (_, _) => EditNumberSequence();
    }

    private void RefreshNumberSequence(ToolParameters parameters)
    {
        bool number = parameters.Tool == EditorTool.Number;
        Show("NumberFormat", number);
        var format = parameters.Numbering?.Format ?? NumberFormat.Decimal;
        editNumberSequence.Visibility = number && format == NumberFormat.Custom ? Visibility.Visible : Visibility.Collapsed;
        if (number)
        {
            numberFormatPicker.SetCustomExamples(controller.Numbering.Items);
            numberFormatPicker.SelectedValue = format;
            RefreshNumberReset();
        }
    }

    private void EditNumberSequence()
    {
        var original = controller.Numbering;
        if (controller.Tool != EditorTool.Number || original.Format != NumberFormat.Custom) return;
        var session = controller.Session;
        using var parentPopup = PopupChildDialog.RetainFor(this);
        var dialog = new NumberSequenceEditorWindow(original, Window.GetWindow(this));
        if (SessionWindowCoordinator.ShowModal(dialog) != true || dialog.Result is not { } result || ReferenceEquals(result, original) ||
            controller.Tool != EditorTool.Number || !ReferenceEquals(controller.Session, session) || !ReferenceEquals(controller.Numbering, original)) return;
        Change(p => p.Numbering = result);
        numberFormatPicker.SetCustomExamples(controller.Numbering.Items);
    }
}
