using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class CanvasSettingsControlTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var window = new CanvasSettingsWindow();
        try
        {
            var columns = Field<NumericValueField>(window, "columns");
            var rows = Field<NumericValueField>(window, "rows");
            var width = Field<NumericValueField>(window, "width");
            check("canvas grid uses foreground palette while background retains background palette",
                Field<ColorField>(window, "gridColor").Role == ColorRole.Foreground && Field<ColorField>(window, "background").Role == ColorRole.Background);
            check("canvas line patterns reuse icon and text previews", Field<ComboBox>(window, "pattern").Items.Cast<ComboBoxItem>().All(item =>
                item.Content is StackPanel panel && panel.Children[0] is Image && panel.Children[1] is TextBlock));
            check("canvas count sliders retain integer range and snap to whole cells", new[] { columns, rows }.All(field =>
                field.Slider.Minimum == 1 && field.Slider.Maximum == 256 && field.Slider.TickFrequency == 1 && field.Slider.IsSnapToTickEnabled));
            int countEdits = 0, widthEdits = 0;
            window.GridCountsChanged += (_, _) => countEdits++;
            window.GridWidthChanged += _ => widthEdits++;
            window.Refresh(new(), false);
            check("canvas numeric refresh creates no semantic edits", countEdits == 0 && widthEdits == 0);
            columns.Input.Text = "9.5";
            check("fractional cell count draft is rejected without edits", !columns.TryGetValue(out _) && countEdits == 0);
            typeof(CanvasSettingsWindow).GetMethod("CommitCounts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            check("invalid cell count commit restores current count", columns.Value == 8 && columns.Input.Text == "8" && countEdits == 0);
            columns.Slider.Value = 12.4;
            check("cell slider normalizes to integer and commits once", columns.Value == 12 && columns.Input.Text == "12" && countEdits == 1);
            width.Slider.Value = 3.125;
            check("canvas width keeps fractional precision and original bounds", width.Value == 3.125 && widthEdits == 1 && width.Slider.Minimum == 1 && width.Slider.Maximum == 20 && !width.Slider.IsSnapToTickEnabled);
            width.Input.Text = "21";
            check("canvas width rejects out of range input without slider commit", !width.TryGetValue(out _) && widthEdits == 1);
            var continuous = new NumericValueField(0, 100);
            continuous.Slider.Value = 8.125;
            check("integer canvas fields do not change shared continuous sliders", continuous.Value == 8.125 && !continuous.Slider.IsSnapToTickEnabled);
        }
        finally { window.CloseForDispose(); }
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
