using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class CanvasGridSettingsControlsTests
{
    internal static void Run(Action<string, bool> check)
    {
        const double precise = 73.1234567890123;
        var current = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 1920, 1080)),
            Grid = new() { FlatCellSize = precise, FlatOrigin = new(960, 540) } };
        var window = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
        try
        {
            var size = Field<NumericValueField>(window, "gridSize");
            var counts = Field<Grid>(window, "countPair");
            var flat = Field<StackPanel>(window, "flatSizePanel");
            var columns = Field<NumericValueField>(window, "columns");
            var rows = Field<NumericValueField>(window, "rows");
            var edits = new List<double>(); int countEdits = 0, lockEdits = 0, pinEdits = 0;
            window.GridCountsChanged += (_, _) => countEdits++;
            window.GridSizeLockChanged += _ => lockEdits++;
            window.GridSizeChanged += value =>
            {
                edits.Add(value); current.Grid.FlatCellSize = value; window.Refresh(current, false); window.PrepareForShow();
            };
            window.PinChanged += () => pinEdits++;
            window.Refresh(current, false);
            window.PrepareForShow();
            check("flat canvas options expose one size field and hide perspective counts and lock", flat.Visibility == Visibility.Visible &&
                counts.Visibility == Visibility.Collapsed && Field<CheckBox>(window, "gridSizeLock").Visibility == Visibility.Collapsed);
            check("flat size text preserves complete stored double precision", size.Input.Text == precise.ToString("G", CultureInfo.CurrentCulture) &&
                size.TryGetValue(out double actual) && actual == precise && size.Value == precise);
            check("flat size uses a continuous common-range slider without integer snapping", size.Slider.Minimum == 1 && size.Slider.Maximum == 400 && !size.Slider.IsSnapToTickEnabled);
            columns.Slider.Value = 12; rows.Slider.Value = 11;
            check("hidden perspective count controls cannot emit changes in flat mode", countEdits == 0 && lockEdits == 0 && current.Projection.Columns == 8 && current.Projection.Rows == 6);
            SendEnter(size.Input);
            check("committing unchanged precise flat size is silent", edits.Count == 0 && current.Grid.FlatCellSize == precise);
            const double changed = 81.9876543210123;
            size.Input.Text = changed.ToString("G", CultureInfo.InvariantCulture); SendEnter(size.Input);
            check("precise flat size text emits one semantic edit and preserves all digits", edits.Count == 1 && edits[0] == changed &&
                current.Grid.FlatCellSize == changed && size.Input.Text == changed.ToString("G", CultureInfo.CurrentCulture));
            foreach (string invalid in new[] { ".5", "1000001", "NaN", "invalid" })
            {
                size.Input.Text = invalid; SendEnter(size.Input);
                check("invalid flat size restores its exact previous value without an edit", edits.Count == 1 && current.Grid.FlatCellSize == changed &&
                    size.Input.Text == changed.ToString("G", CultureInfo.CurrentCulture));
            }
            for (int iteration = 0; iteration < 100; iteration++)
            {
                current.Projection.Enabled = true; window.Refresh(current, false); window.PrepareForShow();
                current.Projection.Enabled = false; window.Refresh(current, false); window.PrepareForShow();
            }
            check("mode refresh roundtrips do not feed display values into either grid setting", edits.Count == 1 && countEdits == 0 && lockEdits == 0 &&
                size.TryGetValue(out double restored) && restored == changed && current.Projection.Columns == 8 && current.Projection.Rows == 6);
            current.Projection.Enabled = true; window.Refresh(current, false);
            window.PrepareForShow();
            check("perspective canvas options restore count fields and hide the flat size editor", counts.Visibility == Visibility.Visible && flat.Visibility == Visibility.Collapsed &&
                Field<CheckBox>(window, "gridSizeLock").Visibility == Visibility.Visible && columns.Value == 8 && rows.Value == 6);
            size.Slider.Value = 92; size.Input.Text = "93.75"; SendEnter(size.Input);
            check("hidden flat size edits cannot escape while perspective is enabled", edits.Count == 1 && current.Grid.FlatCellSize == changed);
            current.Projection.Enabled = false; current.Grid.FlatCellSize = .125; window.Refresh(current, false); window.PrepareForShow(); SendEnter(size.Input);
            check("opening or leaving a legacy sub-unit size preserves the saved value without silently clamping", current.Grid.FlatCellSize == .125 && edits.Count == 1 &&
                size.Input.Text == .125.ToString("G", CultureInfo.CurrentCulture));
            check("canvas configuration starts with an unpinned tool-options policy", !window.IsPinned && pinEdits == 0);
            var pin = Field<Button>(window, "pin"); pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("canvas configuration exposes the shared pin action and one pin-state event", window.IsPinned && pinEdits == 1 && edits.Count == 1 && countEdits == 0);
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("unpinning canvas configuration restores the normal tool-panel policy", !window.IsPinned && pinEdits == 2);
            check("canvas control fixture creates no native window", !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0);
        }
        finally { window.CloseForDispose(); }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void SendEnter(UIElement target) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, Key.Enter)
    { RoutedEvent = Keyboard.KeyDownEvent });
    private sealed class DetachedSource : PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }
}
