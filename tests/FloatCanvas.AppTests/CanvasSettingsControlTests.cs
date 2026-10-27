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
    private static void ResetFitsChosenScreen(Action<string, bool> check)
    {
        MonitorInfo[] monitors = [new("large", new(0, 0, 3840, 2160), new(0, 0, 3840, 2100), 144, 144, true),
            new("small", new(3840, 300, 1280, 720), new(3840, 300, 1280, 680), 96, 96, false)];
        foreach (var origin in new[] { new PointD(), new PointD(1600, -500) })
        foreach (bool locked in new[] { false, true })
        foreach (string device in new[] { "small", "large" })
        {
            var target = monitors.Single(monitor => monitor.DeviceName == device).Bounds;
            var source = monitors.Single(monitor => monitor.DeviceName != device).Bounds;
            var projection = CanvasCalibration.CreateInitial(new(source.Left - origin.X, source.Top - origin.Y, source.Width, source.Height), 8, 6);
            projection.Enabled = true;
            if (locked) projection.LockedGridCellSize = projection.CellSize;
            var settings = new CanvasSettings { Projection = projection, Grid = new() { FlatCellSize = projection.CellSize } };
            var viewport = new RectD(-origin.X, -origin.Y, 5120, 2160);
            var reference = DesktopSession.ResolveCanvasReferenceBounds(monitors, device, default, origin, viewport);
            var reset = DesktopSession.CreateResetCanvasProjection(settings, reference);
            var physical = reset.Quad.Select(point => point + origin).ToArray();
            check("actual perspective reset fits its chosen monitor instead of inheriting another screen's size; target=" + device + "; locked=" + locked + "; origin=" + origin,
                physical.All(point => point.X >= target.Left && point.X <= target.Right && point.Y >= target.Top && point.Y <= target.Bottom));
            var expectedSize = device == "small" ? new PointD(576, 432) : new PointD(1728, 1296);
            check("reset scales both down and back up according to the selected monitor",
                Math.Abs(physical.Max(point => point.X) - physical.Min(point => point.X) - expectedSize.X) < 1e-7 &&
                Math.Abs(physical.Max(point => point.Y) - physical.Min(point => point.Y) - expectedSize.Y) < 1e-7);
            check("target-sized reset keeps its grid counts, enabled state and lock choice", reset.Enabled && reset.Columns == 8 && reset.Rows == 6 &&
                reset.LockedGridCellSize.HasValue == locked && (!locked || reset.LockedGridCellSize == reset.CellSize));
            var center = physical.Aggregate(new PointD(), (sum, point) => sum + point) * .25;
            check("target-sized reset centers within the selected physical monitor", center.DistanceTo(new(target.Left + target.Width / 2d, target.Top + target.Height / 2d)) < 1e-7);
        }
    }

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        ResetFitsChosenScreen(check);
        var window = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
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
            window.Refresh(new() { Projection = new() { Enabled = true } });
            window.PrepareForShow();
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
            CanvasGridSettingsControlsTests.Run(check);
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
        window.Refresh(settings);
        window.PrepareForShow();
        check("grid size lock defaults off with original integer controls visible", toggle.IsChecked == false && toggle.IsEnabled &&
            columns.Visibility == Visibility.Visible && rows.Visibility == Visibility.Visible && lockedColumns.Visibility == Visibility.Collapsed);
        toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        check("grid size lock uses its own single semantic event", lockEdits == 1 && requestedLock == true && countEdits == 0);
        settings.Projection.LockedGridCellSize = 100; settings.Projection.CellSize = 105.1234;
        window.Refresh(settings);
        window.PrepareForShow();
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
        window.PrepareForShow();
        check("in-gesture refresh updates computed coverage beyond editable integer limits", lockedColumns.Text == "800" && lockedRows.Text == "600" && countEdits == 0 && lockEdits == 1);
        settings.Projection.CellSize = .0001;
        window.RefreshGridCounts(settings.Projection);
        window.PrepareForShow();
        check("tiny locked coverage remains positive instead of rounding to zero", double.Parse(lockedColumns.Text, System.Globalization.CultureInfo.CurrentCulture) > 0 &&
            double.Parse(lockedRows.Text, System.Globalization.CultureInfo.CurrentCulture) > 0);
        settings.Projection.Enabled = false;
        window.Refresh(settings);
        window.PrepareForShow();
        check("turning perspective off retains dormant lock state while displaying only flat grid size", toggle.IsChecked == true &&
            toggle.Visibility == Visibility.Collapsed && Field<Grid>(window, "countPair").Visibility == Visibility.Collapsed &&
            Field<StackPanel>(window, "flatSizePanel").Visibility == Visibility.Visible && countEdits == 0);
        toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        check("disabled perspective cannot emit a grid lock edit", lockEdits == 1);
        settings.Projection.Enabled = true; settings.Projection.CellSize = 105.1234; settings.Projection.LockedGridCellSize = null;
        window.Refresh(settings);
        window.PrepareForShow();
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
            bool failed = false;
            using(var held=new FileStream(Path.Combine(directory,"settings.json"),FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                try { DesktopSession.RememberCanvasReferenceDisplay(settings,"pad"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            }
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
        window.PrepareForShow();
        var menu = Field<ContextMenu>(window,"resetMenu");
        check("reset lists the default primary target separately from every active monitor", menu.Items.Count == 3 &&
            Field<Button>(window, "resetChoices").Visibility == Visibility.Visible && Field<Button>(window, "resetChoices").IsEnabled);
        check("target-display dropdown was retired", typeof(CanvasSettingsWindow).GetField("referenceMonitor", BindingFlags.Instance|BindingFlags.NonPublic) is null);
        int requests = 0, choices = 0; string? selected = null, preference = "pad";
        void Choose(string? device) { requests++; selected = device; }
        Action<string?> remember = device => { choices++; preference = device; };
        var selectedEvent = typeof(CanvasSettingsWindow).GetEvent("ResetDisplaySelected");
        check("reset target selection has a separate preference-only notification", selectedEvent is not null);
        selectedEvent?.AddEventHandler(window, remember);
        window.ResetDisplayRequested += Choose;
        try
        {
            Field<Button>(window,"reset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("main reset action uses the selected display", requests == 1 && selected == "pad");
            var defaultItem = menu.Items.OfType<MenuItem>().Single(item => item.Tag is null);
            check("the default reset choice is named independently of numbered monitors", Equals(defaultItem.Header,
                FloatCanvas.Localization.AppText.Get("Canvas.PrimaryDisplay")));
            defaultItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check("choosing the primary default changes only its target and unnumbered caption", requests == 1 && choices == 1 && preference is null &&
                Equals(Field<Button>(window,"reset").Content, FloatCanvas.Localization.AppText.Get("Canvas.ResetOnDisplay")) && defaultItem.IsChecked &&
                menu.Items.OfType<MenuItem>().Count(item => item.IsChecked) == 1);
            defaultItem.IsChecked = false;
            defaultItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check("reselecting the active reset target keeps one check without resetting or resaving", requests == 1 && choices == 1 &&
                defaultItem.IsChecked && menu.Items.OfType<MenuItem>().Count(item => item.IsChecked) == 1);
            Field<Button>(window,"reset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("the unnumbered reset action invokes the default only after the main button is clicked", requests == 2 && selected is null);
            var primaryItem = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "main"));
            primaryItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check("the explicit primary monitor remains a numbered choice separate from the default", requests == 2 && choices == 2 && preference == "main" &&
                primaryItem.IsChecked && !defaultItem.IsChecked && Equals(Field<Button>(window,"reset").Content,
                    FloatCanvas.Localization.AppText.Format("Canvas.ResetOnNumberedDisplay", 1)));
            var padItem = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "pad"));
            padItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check("choosing a secondary monitor does not execute a reset", requests == 2 && choices == 3 && preference == "pad" && padItem.IsChecked &&
                menu.Items.OfType<MenuItem>().Count(item => item.IsChecked) == 1 && Equals(Field<Button>(window,"reset").Content,
                    FloatCanvas.Localization.AppText.Format("Canvas.ResetOnNumberedDisplay", 2)));
            Field<Button>(window,"reset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("a later main-button click executes the chosen secondary target once", requests == 3 && selected == "pad");
            window.SetReferenceMonitors([monitors[1], monitors[0]], preference);
            window.PrepareForShow();
            check("topology reordering retains the device choice and updates its numbered caption without resetting", requests == 3 && choices == 3 &&
                menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "pad")).IsChecked && Equals(Field<Button>(window,"reset").Content,
                    FloatCanvas.Localization.AppText.Format("Canvas.ResetOnNumberedDisplay", 1)));
            window.SetReferenceMonitors(monitors, null);
            window.PrepareForShow();
            check("an unnumbered primary default remains the default across monitor reordering", requests == 3 && choices == 3 &&
                menu.Items.OfType<MenuItem>().Single(item => item.Tag is null).IsChecked && Equals(Field<Button>(window,"reset").Content,
                    FloatCanvas.Localization.AppText.Get("Canvas.ResetOnDisplay")));
            window.SetReferenceMonitors(monitors, "missing");
            window.PrepareForShow();
            check("an unavailable reset device falls back to the distinct default without executing", requests == 3 &&
                menu.Items.OfType<MenuItem>().Single(item => item.Tag is null).IsChecked && Equals(Field<Button>(window,"reset").Content,
                    FloatCanvas.Localization.AppText.Get("Canvas.ResetOnDisplay")));
        }
        finally { window.ResetDisplayRequested -= Choose; selectedEvent?.RemoveEventHandler(window, remember); }
        var bounds = DesktopSession.ResolveCanvasReferenceBounds(monitors,"pad",default,default,new(0,0,3840,3600));
        check("explicit reset uses physical secondary bounds", bounds == new RectD(307,2160,2160,1440));
        var initial = CanvasCalibration.CreateInitial(bounds);
        check("secondary reset centers the four-corner plane", initial.Quad.Aggregate(new PointD(),(a,p)=>a+p)*.25 == bounds.Center);
        check("unnumbered reset selects primary even with toolbar on another screen", DesktopSession.ResolveCanvasReferenceBounds(monitors,null,new(500,2400),default,new(0,0,3840,3600)) == new RectD(0,0,3840,2160));
        MonitorInfo[] left = [new("left",new(-1920,-200,1920,1080),new(-1920,-200,1920,1040),96,96,true)];
        check("reset converts negative screen coordinates once into document coordinates", DesktopSession.ResolveCanvasReferenceBounds(left,null,default,new(-2000,-500),new(0,0,6000,4000)) == new RectD(80,300,1920,1080));
        window.SetReferenceMonitors([monitors[1]],"pad");
        window.PrepareForShow();
        check("a sole active display uses an unnumbered reset button with no dropdown", menu.Items.Count == 1 &&
            Field<Button>(window, "resetChoices").Visibility == Visibility.Collapsed && !Field<Button>(window, "resetChoices").IsEnabled &&
            Field<Button>(window, "reset").IsEnabled && Equals(Field<Button>(window,"reset").Content, FloatCanvas.Localization.AppText.Get("Canvas.ResetOnDisplay")));
    }
}
