using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Real offscreen canvas windows keep pending data out of hidden controls.</summary>
internal static class CanvasHiddenRefreshTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var findings = new List<(string Name, bool Pass)>();
        foreach (bool enhanced in new[] { false, true })
        {
            string backend = enhanced ? "enhanced" : "ordinary";
            using var owner = new HwndSource(new HwndSourceParameters("Canvas hidden refresh owner")
            { Width = 1, Height = 1, PositionX = -10000, PositionY = -10000, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x080000A0 });
            using var scope = enhanced ? new OverlayWindowScope(owner.Handle, () => true) : null;
            AppTheme.Apply("dark");
            var window = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly())
            { Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0, IsHitTestVisible = false, WindowStartupLocation = WindowStartupLocation.Manual };
            window.SourceInitialized += (_, _) =>
            {
                nint handle = new WindowInteropHelper(window).Handle;
                SetWindowLongPtrW(handle, -20, GetWindowLongPtrW(handle, -20) | 0x080000A0);
            };
            if (enhanced) OverlayWindowScope.Prepare(window);
            try
            {
                findings.Add(($"{backend} never-shown canvas defers its preset tile construction", Field<CanvasPresetGallery>(window, "presets").OfficialTiles.Children.Count == 0));
                window.Show(); Pump();
                var background = Field<ColorField>(window, "background"); var gridColor = Field<ColorField>(window, "gridColor");
                var columns = Field<NumericValueField>(window, "columns"); var rows = Field<NumericValueField>(window, "rows");
                var size = Field<NumericValueField>(window, "gridSize"); var width = Field<NumericValueField>(window, "width");
                var lockedColumns = Field<TextBox>(window, "lockedColumns"); var lockedRows = Field<TextBox>(window, "lockedRows");
                var pattern = Field<ComboBox>(window, "pattern");
                var gallery = Field<CanvasPresetGallery>(window, "presets");
                var firstPreset = gallery.OfficialTiles.Children.OfType<Button>().First();
                var main = new MonitorInfo("main", new(0,0,1920,1080), new(0,0,1920,1040), 96,96,true);
                var second = new MonitorInfo("second", new(1920,0,1280,720), new(1920,0,1280,680), 96,96,false);
                var initial = new CanvasSettings { BackgroundColor = 0xFF112233, Projection = new() { Enabled = true, Columns = 8, Rows = 6 },
                    Grid = new() { Visible = true, Color = 0xFF345678, Width = 2, FlatCellSize = 48 } };
                window.Refresh(initial, false); window.SetReferenceMonitors([main], null); Pump();
                window.Hide(); Pump();
                string before = Snapshot(); object? previewBefore = firstPreset.Content;
                int presetRefreshes = 0;
                var descriptor = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(Button));
                EventHandler changed = (_, _) => presetRefreshes++;
                descriptor.AddValueChanged(firstPreset, changed);
                try
                {
                    CanvasSettings latest = initial;
                    for (int index = 0; index < 5; index++)
                    {
                        latest = new CanvasSettings { BackgroundColor = 0xFF223300u + (uint)index,
                            DepthPriorityOcclusion = true, Projection = new() { Enabled = true, Columns = 20 + index, Rows = 10 + index, CellSize = 2 + index },
                            Grid = new() { Visible = true, Color = 0xFF445500u + (uint)index, Width = 3 + index, FlatCellSize = 100 + index, Pattern = StrokePattern.Dotted } };
                        window.Refresh(latest, index % 2 == 0);
                    }
                    var countUpdate = latest.Projection.Clone(); countUpdate.Columns = 33; countUpdate.Rows = 17;
                    window.RefreshGridCounts(countUpdate);
                    window.SetReferenceMonitors([main, second], "second");
                    Pump();
                    findings.Add(($"{backend} hidden canvas remains loaded without mutating control values", window.IsLoaded && !window.IsVisible && Snapshot() == before));
                    findings.Add(($"{backend} hidden canvas does not rebuild presets or reset-display controls", presetRefreshes == 0 &&
                        ReferenceEquals(previewBefore, firstPreset.Content) && Field<ContextMenu>(window, "resetMenu").Items.Count == 1));
                    window.Show(); Pump();
                    findings.Add(($"{backend} canvas reopening applies only the newest queued state", background.Value == latest.BackgroundColor && gridColor.Value == latest.Grid.Color &&
                        columns.Value == 33 && rows.Value == 17 && width.Value == latest.Grid.Width && Equals((pattern.SelectedItem as ComboBoxItem)?.Tag, latest.Grid.Pattern)));
                    findings.Add(($"{backend} canvas reopening synchronizes presets once", presetRefreshes == 1));
                    findings.Add(($"{backend} canvas reopening applies queued display choices", Field<ContextMenu>(window, "resetMenu").Items.Count == 2 && Field<Button>(window, "resetChoices").Visibility == Visibility.Visible));
                }
                finally { descriptor.RemoveValueChanged(firstPreset, changed); }

                var flat = initial.Clone(); flat.Projection.Enabled = false; flat.Projection.Columns = 41; flat.Projection.Rows = 29;
                flat.Projection.LockedGridCellSize = 100; flat.Grid.FlatCellSize = 73.75; flat.Grid.Visible = false; flat.Grid.Width = 9;
                double oldColumns = columns.Value, oldRows = rows.Value;
                string oldLockedColumns = lockedColumns.Text, oldLockedRows = lockedRows.Text;
                window.Refresh(flat, false); Pump();
                findings.Add(($"{backend} flat canvas refresh does not touch hidden perspective inputs", !columns.IsVisible && !rows.IsVisible &&
                    columns.Value == oldColumns && rows.Value == oldRows && lockedColumns.Text == oldLockedColumns && lockedRows.Text == oldLockedRows));
                findings.Add(($"{backend} flat canvas refresh synchronizes its visible size field", size.IsVisible && size.Value == 73.75));
                findings.Add(($"{backend} grid visibility off preserves and refreshes the still-visible grid property rows", gridColor.IsVisible && width.IsVisible && pattern.IsVisible && width.Value == 9));

                var perspective = flat.Clone(); perspective.Projection.Enabled = true; perspective.Projection.Columns = 7; perspective.Projection.Rows = 9;
                perspective.Projection.CellSize = 200; perspective.Grid.FlatCellSize = 999;
                window.Refresh(perspective, false); Pump();
                findings.Add(($"{backend} perspective refresh leaves the hidden flat size field untouched", !size.IsVisible && size.Value == 73.75));
                findings.Add(($"{backend} locked counts update readouts without updating hidden editable counts", lockedColumns.IsVisible && lockedRows.IsVisible &&
                    !columns.IsVisible && !rows.IsVisible && columns.Value == oldColumns && rows.Value == oldRows &&
                    lockedColumns.Text == "14" && lockedRows.Text == "18"));
                var live = perspective.Projection.Clone(); live.Columns = 11; live.Rows = 13;
                window.RefreshGridCounts(live); Pump();
                findings.Add(($"{backend} live calibration refresh updates only visible locked count readouts", lockedColumns.Text == "22" && lockedRows.Text == "26" &&
                    columns.Value == oldColumns && rows.Value == oldRows));

                var colorRow = (FrameworkElement)gridColor.Parent; var widthRow = (FrameworkElement)width.Parent; var patternRow = (FrameworkElement)pattern.Parent;
                colorRow.Visibility = widthRow.Visibility = patternRow.Visibility = Visibility.Collapsed;
                uint previousColor = gridColor.Value; double previousWidth = width.Value; object? previousPattern = pattern.SelectedItem;
                perspective.Grid.Color = 0xFFABCDEF; perspective.Grid.Width = 17; perspective.Grid.Pattern = StrokePattern.Dashed;
                window.Refresh(perspective, false); Pump();
                findings.Add(($"{backend} actually hidden grid rows skip value and selection refresh", gridColor.Value == previousColor && width.Value == previousWidth && ReferenceEquals(pattern.SelectedItem, previousPattern)));
                colorRow.Visibility = widthRow.Visibility = patternRow.Visibility = Visibility.Visible;
                window.Refresh(perspective, false); Pump();
                findings.Add(($"{backend} revealed grid rows receive the latest pending values", gridColor.Value == 0xFFABCDEF && width.Value == 17 && Equals((pattern.SelectedItem as ComboBoxItem)?.Tag, StrokePattern.Dashed)));
                findings.Add(($"{backend} canvas refresh never activates a fixture window", !FixtureOwnsForeground()));

                string Snapshot() => JsonSerializer.Serialize(new { Background = background.Value, GridColor = gridColor.Value, Columns = columns.Input.Text, Rows = rows.Input.Text,
                    Size = size.Input.Text, Width = width.Input.Text, Projection = Field<CheckBox>(window, "projection").IsChecked,
                    Grid = Field<CheckBox>(window, "gridVisible").IsChecked, Pattern = (pattern.SelectedItem as ComboBoxItem)?.Tag,
                    LockedColumns = lockedColumns.Text, LockedRows = lockedRows.Text, Reset = Field<Button>(window, "reset").Content });
            }
            finally { window.CloseForDispose(); Pump(); }
        }
        foreach (var finding in findings) Console.WriteLine($"CANVAS_HIDDEN_FINDING {finding.Pass}: {finding.Name}");
        foreach (var finding in findings) check(finding.Name, finding.Pass);
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    private static bool FixtureOwnsForeground() { GetWindowThreadProcessId(GetForegroundWindow(), out uint process); return process == Environment.ProcessId; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
}
