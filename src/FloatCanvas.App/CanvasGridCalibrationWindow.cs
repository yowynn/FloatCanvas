using System.Windows;
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
        var panel = new StackPanel { Margin = new(18) };
        panel.Children.Add(new TextBlock { Text = L.Get("Canvas.GridCalibrationSpans"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
        var columns = new NumericValueField(1, 256, integerOnly: true) { Value = 1 };
        var rows = new NumericValueField(1, 256, integerOnly: true) { Value = 1 };
        panel.Children.Add(new TextBlock { Text = L.Get("Canvas.Columns") }); panel.Children.Add(columns);
        panel.Children.Add(new TextBlock { Text = L.Get("Canvas.Rows") }); panel.Children.Add(rows);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 10) }; panel.Children.Add(status);
        var apply = new Button { Content = L.Get("Canvas.ApplyPlane"), MinHeight = 32, Margin = new(0, 0, 0, 6) };
        var cancel = new Button { Content = L.Get("Dialog.Cancel"), MinHeight = 32 }; panel.Children.Add(apply); panel.Children.Add(cancel);
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
            field.Input.LostKeyboardFocus += (_, _) => Refresh(); field.SliderValueCommitted += Refresh;
            field.Input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Refresh(); e.Handled = true; } };
        }
        apply.Click += (_, _) => { Refresh(); if (candidate is null) return; Result = candidate; SessionWindowCoordinator.Close(this); };
        cancel.Click += (_, _) => SessionWindowCoordinator.Close(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SessionWindowCoordinator.Close(this); e.Handled = true; } };
        Content = panel; AppTheme.EnsureDialogPalette(this); AppWindowChrome.ApplyDialog(this); Refresh();
    }
}
