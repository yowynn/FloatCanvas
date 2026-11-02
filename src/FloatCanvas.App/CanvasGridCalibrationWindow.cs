using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class CanvasGridCalibrationWindow : Window
{
    internal CanvasSettings? Result { get; private set; }
    internal CanvasGridCalibrationWindow(Window owner, CanvasSettings initial, IReadOnlyList<PointD> points,
        Action<CanvasSettings?> preview)
    {
        Title = L.Get("Canvas.GridCalibration"); Owner = owner; Topmost = true; ShowInTaskbar = false;
        Width = 420; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = WindowLayout.ContentMargin() };
        panel.Children.Add(new TextBlock { Text = L.Get("Canvas.GridCalibrationSpans"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
        var columns = new NumericValueField(1, 256, integerOnly: true) { Value = 1 };
        var rows = new NumericValueField(1, 256, integerOnly: true) { Value = 1 };
        foreach (var (field, key, kind) in new[] { (columns, "Canvas.Columns", NumericPropertyKind.Columns), (rows, "Canvas.Rows", NumericPropertyKind.Rows) })
        {
            var label = new PropertyLabel(L.Get(key)); field.SetScrubAnchor(kind, label.Text); field.SetLabel(label);
            AutomationProperties.SetName(field.Input, label.Text); AutomationProperties.SetName(field.Slider, label.Text);
            panel.Children.Add(PropertyPanelLayout.Row(label, field));
        }
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 10) }; panel.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = L.Get("Canvas.ApplyPlane"), MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 96, IsDefault = true };
        var cancel = new Button { Content = L.Get("Dialog.Cancel"), MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 96, IsCancel = true, Margin = new(0, 0, 8, 0) };
        actions.Children.Add(cancel); actions.Children.Add(apply); panel.Children.Add(actions);
        CanvasSettings? candidate = null;
        void Refresh()
        {
            candidate = null;
            if (columns.TryGetValue(out double c) && rows.TryGetValue(out double r) &&
                CanvasGridCalibration.TryFit(initial, points, (int)c, (int)r, out var fitted, out _))
            {
                candidate = fitted; var bounds = CanvasProjectionGeometry.ResolvePlaneBounds(fitted.Projection);
                status.Text = L.Format("Canvas.GridCalibrationResult", bounds.Width / bounds.Height, fitted.Grid.PlaneCellSize);
            }
            else status.Text = L.Get("Canvas.GridCalibrationInvalid");
            apply.IsEnabled = candidate is not null; preview(candidate);
        }
        foreach (var field in new[] { columns, rows })
        {
            field.ValueChanged += _ => { if (field.IsInteracting) Refresh(); };
            field.Input.LostKeyboardFocus += (_, _) => Refresh(); field.SliderValueCommitted += Refresh;
            field.Input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Refresh(); e.Handled = true; } };
        }
        apply.Click += (_, _) => { Refresh(); if (candidate is null) return; Result = candidate; SessionWindowCoordinator.Close(this); };
        cancel.Click += (_, _) => SessionWindowCoordinator.Close(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SessionWindowCoordinator.Close(this); e.Handled = true; } };
        Content = panel; AppTheme.EnsureDialogPalette(this); AppWindowChrome.ApplyDialog(this); Refresh();
    }
}
