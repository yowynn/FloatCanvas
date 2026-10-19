using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

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
            width.Input.Text = "65";
            check("canvas width rejects out of range input without slider commit", !width.TryGetValue(out _) && widthEdits == 1);
            var continuous = new NumericValueField(0, 100);
            continuous.Slider.Value = 8.125;
            check("integer canvas fields do not change shared continuous sliders", continuous.Value == 8.125 && !continuous.Slider.IsSnapToTickEnabled);
            GridSizeLock(window, check);
            MonitorSelection(window, check);
            ReferencePreference(window, check);
        }
        finally { window.CloseForDispose(); }
        if (Environment.GetEnvironmentVariable("FLOATCANVAS_GRID_LOCK_UI_OUTPUT") is { Length: > 0 } output)
            CanvasGridLockUiEvidence.Run(output, check);
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static void GridSizeLock(CanvasSettingsWindow window, Action<string, bool> check)
    {
        var toggle = Field<CheckBox>(window, "gridSizeLock");
        var columns = Field<NumericValueField>(window, "columns"); var rows = Field<NumericValueField>(window, "rows");
        var lockedColumns = Field<TextBox>(window, "lockedColumns"); var lockedRows = Field<TextBox>(window, "lockedRows");
        int lockEdits = 0, countEdits = 0; bool? requestedLock = null;
        window.GridSizeLockChanged += value => { lockEdits++; requestedLock = value; };
        window.GridCountsChanged += (_, _) => countEdits++;
        var settings = new CanvasSettings { Projection = new() { Enabled = true, Columns = 8, Rows = 6, CellSize = 100 } };
        window.Refresh(settings, false);
        check("grid size lock defaults off with original integer controls visible", toggle.IsChecked == false && toggle.IsEnabled &&
            columns.Visibility == Visibility.Visible && rows.Visibility == Visibility.Visible && lockedColumns.Visibility == Visibility.Collapsed);
        toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        check("grid size lock uses its own single semantic event", lockEdits == 1 && requestedLock == true && countEdits == 0);
        settings.Projection.LockedGridCellSize = 100; settings.Projection.CellSize = 105.1234;
        window.Refresh(settings, true);
        check("locked count controls are read-only and preserve fractional coverage", lockedColumns.IsReadOnly && lockedRows.IsReadOnly &&
            lockedColumns.Text == 8.41.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) &&
            lockedRows.Text == 6.307.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) &&
            columns.Visibility == Visibility.Collapsed && rows.Visibility == Visibility.Collapsed && lockedColumns.Visibility == Visibility.Visible);
        columns.Input.Text = "27";
        typeof(CanvasSettingsWindow).GetMethod("CommitCounts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        columns.Slider.Value = 29;
        check("locked count controls cannot feed displayed or hidden drafts into calibration", countEdits == 0 && settings.Projection.Columns == 8 && settings.Projection.Rows == 6);
        settings.Projection.CellSize = 10_000;
        window.RefreshGridCounts(settings.Projection);
        check("in-gesture refresh updates computed coverage beyond editable integer limits", lockedColumns.Text == "800" && lockedRows.Text == "600" && countEdits == 0 && lockEdits == 1);
        settings.Projection.CellSize = .0001;
        window.RefreshGridCounts(settings.Projection);
        check("tiny locked coverage remains positive instead of rounding to zero", double.Parse(lockedColumns.Text, System.Globalization.CultureInfo.CurrentCulture) > 0 &&
            double.Parse(lockedRows.Text, System.Globalization.CultureInfo.CurrentCulture) > 0);
        settings.Projection.Enabled = false;
        window.Refresh(settings, false);
        check("turning perspective off retains a readable lock and computed counts", toggle.IsChecked == true && !toggle.IsEnabled &&
            lockedColumns.Visibility == Visibility.Visible && columns.Visibility == Visibility.Collapsed && countEdits == 0);
        toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        check("disabled perspective cannot emit a grid lock edit", lockEdits == 1);
        settings.Projection.Enabled = true; settings.Projection.CellSize = 105.1234; settings.Projection.LockedGridCellSize = null;
        window.Refresh(settings, false);
        check("unlocking restores the original reference counts and integer sliders", toggle.IsChecked == false && columns.Value == 8 && rows.Value == 6 &&
            columns.Visibility == Visibility.Visible && lockedColumns.Visibility == Visibility.Collapsed && countEdits == 0);
        columns.Slider.Value = 9;
        check("unlocked integer count editing still commits once", countEdits == 1);
        toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        check("unlock requests remain distinct from count changes", lockEdits == 2 && requestedLock == false && countEdits == 1);
    }

    private static void ReferencePreference(CanvasSettingsWindow window, Action<string, bool> check)
    {
        string originalProfile = ApplicationPaths.ProfileDirectory;
        var originalScope = ApplicationPaths.RequestedMonitorScope;
        string directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-ResetPreference-" + Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", directory]);
        MonitorInfo main = new("main", new(0,0,1920,1080), new(0,0,1920,1040),96,96,true);
        MonitorInfo pad = new("pad", new(1920,0,1280,720), new(1920,0,1280,680),120,120,false);
        try
        {
            var settings = UserSettings.Load(); settings.Language = "en"; settings.NumberStart = 23;
            DesktopSession.RememberCanvasReferenceDisplay(settings, "pad");
            check("explicit reset target survives settings save and restart", UserSettings.Load().CanvasReferenceDisplay == "pad");
            check("reset target persistence preserves unrelated preferences", UserSettings.Load().NumberStart == 23 && UserSettings.Load().Language == "en");
            DesktopSession.NormalizeCanvasResetDisplay(settings,[main,pad]);
            check("an available explicit reset target remains selected", settings.CanvasReferenceDisplay == "pad");
            DesktopSession.NormalizeCanvasResetDisplay(settings,[main]);
            check("scope changes fall back to the unnumbered reset option and persist it", settings.CanvasReferenceDisplay is null && UserSettings.Load().CanvasReferenceDisplay is null);
            DesktopSession.NormalizeCanvasResetDisplay(settings,[main,pad]);
            check("returning displays do not revive an invalidated reset choice", settings.CanvasReferenceDisplay is null);
            DesktopSession.RememberCanvasReferenceDisplay(settings,"missing");
            DesktopSession.NormalizeCanvasResetDisplay(settings,[main,pad]);
            check("a missing device falls back even when multiple active displays remain", settings.CanvasReferenceDisplay is null && UserSettings.Load().CanvasReferenceDisplay is null);
            string blocked = Path.Combine(directory,"settings.json.tmp"); Directory.CreateDirectory(blocked);
            bool failed = false;
            try { DesktopSession.RememberCanvasReferenceDisplay(settings,"pad"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            check("failed reset preference save restores memory and disk", failed && settings.CanvasReferenceDisplay is null && UserSettings.Load().CanvasReferenceDisplay is null);
            DesktopSession.RememberCanvasReferenceDisplay(settings,null);
            check("selecting the current default requires no redundant settings write", settings.CanvasReferenceDisplay is null);
        }
        finally
        {
            var args = new List<string> { "--profile",originalProfile };
            if (originalScope is { } scope) args.Add(scope == OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens");
            ApplicationPaths.Configure(args.ToArray());
        }
    }
    private static void MonitorSelection(CanvasSettingsWindow window, Action<string, bool> check)
    {
        MonitorInfo[] monitors = [
            new("main", new(0,0,3840,2160),new(0,0,3840,2052),144,144,true),
            new("pad",new(307,2160,2160,1440),new(307,2160,2160,1380),120,120,false)];
        window.SetReferenceMonitors(monitors,"pad");
        var menu = Field<ContextMenu>(window,"resetMenu");
        check("split reset menu contains the default and each active display", menu.Items.Count == 3 && menu.Items.Cast<MenuItem>().Select(i => (string)i.Tag).SequenceEqual(new[] { "", "main", "pad" }));
        check("target-display dropdown was retired", typeof(CanvasSettingsWindow).GetField("referenceMonitor", BindingFlags.Instance|BindingFlags.NonPublic) is null);
        int requests = 0; string? selected = null;
        void Choose(string? device) { requests++; selected = device; }
        window.ResetDisplayRequested += Choose;
        try
        {
            ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check("choosing an explicit menu option requests an immediate reset on that display", requests == 1 && selected == "main");
            Field<Button>(window,"reset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("main reset action executes the currently remembered choice", requests == 2 && selected == "pad");
        }
        finally { window.ResetDisplayRequested -= Choose; }
        var bounds = DesktopSession.ResolveCanvasReferenceBounds(monitors,"pad",default,default,new(0,0,3840,3600));
        check("explicit reset uses physical secondary bounds", bounds == new RectD(307,2160,2160,1440));
        var initial = CanvasCalibration.CreateInitial(bounds);
        check("secondary reset centers the four-corner plane", initial.Quad.Aggregate(new PointD(),(a,p)=>a+p)*.25 == bounds.Center);
        check("unnumbered reset selects primary even with toolbar on another screen", DesktopSession.ResolveCanvasReferenceBounds(monitors,null,new(500,2400),default,new(0,0,3840,3600)) == new RectD(0,0,3840,2160));
        MonitorInfo[] left = [new("left",new(-1920,-200,1920,1080),new(-1920,-200,1920,1040),96,96,true)];
        check("reset converts negative screen coordinates once into document coordinates", DesktopSession.ResolveCanvasReferenceBounds(left,null,default,new(-2000,-500),new(0,0,6000,4000)) == new RectD(80,300,1920,1080));
        window.SetReferenceMonitors([monitors[1]],"pad");
        check("a sole nonprimary scope display uses the unnumbered reset option", menu.Items.Count == 1 && (string)((MenuItem)menu.Items[0]).Tag == "");
    }
}
