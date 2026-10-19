using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Interop;
using FloatCanvas.Core;
using FloatCanvas.PlatformTests;
using FloatCanvas.Windows;
using Wpf = System.Windows;
using L = FloatCanvas.Localization.AppText;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace FloatCanvas.SessionTests;

internal static partial class Program
{
    private static readonly List<object> MultimonitorStages = [];

    // Opt-in physical-topology acceptance. Never changes Windows display configuration,
    // injects keyboard/mouse input, samples unrelated pixels, or addresses another process.
    private static int RunMultimonitorProbe(string[] args)
    {
        int index = Array.IndexOf(args, "--multimonitor-artifacts");
        string output = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "artifacts", "FloatCanvas-multimonitor-20261018", "native");
        Directory.CreateDirectory(output);
        Profile = Path.Combine(output, "profile-" + Guid.NewGuid().ToString("N"));
        ConfigureProfile(Profile);
        Directory.CreateDirectory(Profile);
        // Avoid registration conflicts with the user's running application.
        File.WriteAllText(Path.Combine(Profile, "settings.json"), JsonSerializer.Serialize(new
        {
            Toggle = new HotkeyBinding(7, 0x86), Hide = new HotkeyBinding(7, 0x87),
            PrimaryScreenOnly = true, PreferGpuAcceleration = false, Language = "zh-CN", Theme = "light"
        }));
        var app = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Failure = e.Exception.ToString(); e.Handled = true; };
        int result = 1;
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Failure = "Physical multimonitor probe exceeded 180 seconds.";
            Write(); Environment.Exit(1);
        }, null, TimeSpan.FromSeconds(180), Timeout.InfiniteTimeSpan);
        app.Startup += async (_, _) =>
        {
            try { await ExercisePhysicalMonitors(app); result = Failure is null ? 0 : 1; }
            catch (Exception ex) { Failure = ex.ToString(); Console.Error.WriteLine(ex); }
            finally { Write(); app.Shutdown(result); }
        };
        app.Run();
        Console.WriteLine($"Physical multimonitor checks: {Results.Count(r => r.Passed)}/{Results.Count}; report={Path.Combine(output, "multimonitor.json")}");
        return result;

        void Write() => File.WriteAllText(Path.Combine(output, "multimonitor.json"), JsonSerializer.Serialize(new
        {
            Passed = result == 0 && Failure is null, Timestamp = DateTimeOffset.Now, Profile, Failure,
            Checks = Results.ToArray(), Stages = MultimonitorStages.ToArray(), PhysicalInputInjection = false,
            Scope = "Real physical monitor enumeration and production settings Save callback, overlay HWNDs, editing lease/focus, UI z-order and controlled own-button pixel ROIs. Calibration uses production managed pointer routing with physical coordinates; no hardware pointer messages are injected. No Windows display settings or foreign process windows are changed."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task ExercisePhysicalMonitors(Wpf.Application app)
    {
        var monitors = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).ToArray();
        MultimonitorStages.Add(new { Phase = "PhysicalInventory", Monitors = monitors });
        Check(monitors.Length >= 2 && monitors.Count(m => m.IsPrimary) == 1, "Physical primary and extended screens are present before the fixture begins");
        var primary = monitors.Single(m => m.IsPrimary);
        var pad = monitors.First(m => !m.IsPrimary);
        var union = monitors.Select(m => new RectD(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height)).Aggregate(RectD.Union);
        var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [OverlayMonitorScope.PrimaryScreen, false], null)!;
        object? session = null;
        KeyboardInputIsolation? isolation = null;
        try
        {
            session = Activator.CreateInstance(DesktopType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [app, host], null)!;
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            await CallAsync(session, "InitializeDocumentsAsync", false);
            PauseAutomaticRecovery(session);
            var live = Field<EditorSession>(session, "_live");
            var marker = new CanvasElement { Kind = ElementKind.Rectangle, Width = union.Width, Height = union.Height,
                Transform = Affine2D.Translation(union.X, union.Y), Style = new() { Fill = 0xFFEC00EC, Stroke = 0xFFEC00EC, StrokeWidth = 0 } };
            live.Add(marker, "Own physical multimonitor fixture"); live.SetSelection([]);
            var toolbar = Field<Wpf.Window>(session, "_toolbar");
            toolbar.Title = "FloatCanvas physical multimonitor probe";
            toolbar.Show();
            await SettleOwnToolbarScene(app);
            var toolbarHandle = new WindowInteropHelper(toolbar).Handle;
            Native.SetForegroundWindow(toolbarHandle); Native.SetFocus(toolbarHandle);
            Console.WriteLine($"WAITING_FOR_MULTIMONITOR_FOREGROUND pid={Environment.ProcessId} hwnd={toolbarHandle}");
            var timer = Stopwatch.StartNew();
            while (Native.GetForegroundWindow() != toolbarHandle)
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(90)) throw new InvalidOperationException("Own toolbar could not acquire foreground; no input was injected.");
                await Task.Delay(25);
            }
            Check((bool)Call(session, "SetEditing", true)!, "Production session enters editing with its own physical primary overlay");
            host.SetVisible(true);
            Call(session, "ToggleInspector");
            var inspector = Field<Wpf.Window>(session, "_inspector");
            inspector.Left = (primary.WorkArea.Left + 30) * 96d / primary.DpiX;
            inspector.Top = (primary.WorkArea.Top + 220) * 96d / primary.DpiY;
            await SettleOwnToolbarScene(app);
            isolation = (KeyboardInputIsolation?)typeof(OverlayHost).GetField("_keyboardIsolation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);
            Check(isolation is { IsLeaseActive: true }, "Production keyboard isolation owns a healthy lease");
            long lease = isolation!.CurrentLeaseId, epoch = host.EditingSessionVersion;
            int undoBeforeScope = live.UndoCount;
            Check(host.Scope == OverlayMonitorScope.PrimaryScreen && host.Monitors.Count == 1 && host.Diagnostics.Count == 1, "Initial real HWND set contains only the primary screen");
            await SaveScopeRenderFailure();
            await SaveScope(false, "primary-to-all");
            await SaveScope(true, "all-to-primary");
            Call(session, "ToggleCanvasVisibility");
            await SaveScope(false, "hidden-primary-to-all");
            Check(!host.IsVisible && host.Diagnostics.All(p => !p.DisplayVisible), "Saving scope while hidden never flashes replacement display HWNDs");
            await SaveScope(true, "hidden-all-to-primary");
            Call(session, "ToggleCanvasVisibility");
            await SaveScope(false, "visible-primary-to-all-for-calibration");
            Check(ReferenceEquals(Active(session), live) && live.Document.Elements.Single().Id == marker.Id && live.UndoCount == undoBeforeScope,
                "All scope changes preserve the same document, content and undo history");

            for (int cycle = 0; cycle < 3; cycle++)
            {
                foreach (var next in new[] { OverlayMonitorScope.PrimaryScreen, OverlayMonitorScope.AllScreens })
                {
                    var input = host.Diagnostics.Single(p => p.Bounds == primary.Bounds).InputHandle;
                    Check(Native.SetForegroundWindow(input), $"Input-foreground cycle {cycle} can activate only its own primary input HWND");
                    Native.SetFocus(input);
                    await SettleOwnToolbarScene(app);
                    Check(Native.GetForegroundWindow() == input && GetFocus() == input && isolation.CurrentLeaseId == lease,
                        $"Input-foreground cycle {cycle} begins with exact input foreground/focus and the original lease");
                    host.SetMonitorScope(next);
                    AssertInputContinuation($"input-foreground-{cycle}-{next}-immediate");
                    AssertOwnUi($"input-foreground-{cycle}-{next}-immediate");
                    // Give queued focus notifications and the hook worker a chance to expose
                    // a transient foreign/zero foreground during destruction of the old input.
                    await Task.Delay(100);
                    await SettleOwnToolbarScene(app);
                    AssertInputContinuation($"input-foreground-{cycle}-{next}-after-events");
                }
            }

            Call(session, "ToggleCanvasSettings");
            await SettleOwnToolbarScene(app);
            var canvasWindow = Field<Wpf.Window>(session, "_canvasSettingsWindow");
            Call(session, "SetCanvasProjection", true);
            var reference = (System.Windows.Controls.ContextMenu)canvasWindow.GetType().GetField("resetMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvasWindow)!;
            Check(reference.Items.Count == monitors.Length + 1, "Production reset menu lists the default and every actual scope monitor");
            var padReset = reference.Items.Cast<System.Windows.Controls.MenuItem>().Single(item => Equals(item.Tag, pad.DeviceName));
            padReset.RaiseEvent(new Wpf.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent, padReset));
            Check((string?)Field<object>(session, "_settings").GetType().GetProperty("CanvasReferenceDisplay")!.GetValue(Field<object>(session, "_settings")) == pad.DeviceName, "Selecting the Pad reset updates the application reference preference and resets immediately");
            var expected = CanvasCalibration.CreateInitial(new(pad.Bounds.Left, pad.Bounds.Top, pad.Bounds.Width, pad.Bounds.Height));
            Check(live.Document.Canvas.Projection.Quad.Count == 4 && live.Document.Canvas.Projection.Quad.Zip(expected.Quad).All(p => p.First.DistanceTo(p.Second) < .001),
                "Production Reset centers all four calibration corners within the selected physical Pad");
            MultimonitorStages.Add(new { Phase = "PadReferenceReset", Pad = pad, Quad = live.Document.Canvas.Projection.Quad.ToArray(), Center = QuadCenter(live.Document.Canvas.Projection.Quad) });
            Call(session, "BeginCanvasCalibration");
            Check((bool)Field<object>(session, "_canvasCalibration").GetType().GetProperty("IsActive")!.GetValue(Field<object>(session, "_canvasCalibration"))!, "Production calibration begins on the Pad frame");
            var initial = live.Document.Canvas.Projection.Quad.ToArray();
            var start = QuadCenter(initial);
            var destination = new PointD(primary.Bounds.Left + primary.Bounds.Width / 2d, primary.Bounds.Top + primary.Bounds.Height / 2d);
            int beforeDrag = live.UndoCount;
            Pointer(OverlayPointerKind.Down, start, OverlayMouseButton.Left);
            Pointer(OverlayPointerKind.Move, destination, OverlayMouseButton.None);
            Pointer(OverlayPointerKind.Up, destination, OverlayMouseButton.Left);
            var moved = live.Document.Canvas.Projection.Quad.ToArray();
            Check(moved.Zip(initial).All(p => (p.First - p.Second).DistanceTo(destination - start) < .01) && QuadCenter(moved).DistanceTo(destination) < .01,
                "Managed physical center drag moves the entire Pad frame to the main screen with an identical four-corner translation");
            Check(live.UndoCount == beforeDrag + 1 && !live.IsTransactionActive, "Cross-screen center drag commits exactly one undo transaction");
            MultimonitorStages.Add(new { Phase = "PadCenterDraggedToPrimary", Start = start, Destination = destination, Initial = initial, Quad = moved, live.UndoCount });
            Check(live.Undo() && live.Document.Canvas.Projection.Quad.Zip(initial).All(p => p.First.DistanceTo(p.Second) < .001), "Cross-screen translation can undo exactly to its Pad reference");
            Check(live.Redo() && live.Document.Canvas.Projection.Quad.Zip(moved).All(p => p.First.DistanceTo(p.Second) < .001), "Cross-screen translation can redo exactly to its primary placement");
            Check(host.IsEditing && host.IsKeyboardIsolationActive && isolation.CurrentLeaseId == lease && host.EditingSessionVersion == epoch,
                "Scope changes and cross-screen calibration keep the original editing epoch and keyboard lease");
            Call(session, "EndCanvasCalibration", true);
            Call(session, "SetEditing", false);
            Check(!host.IsEditing && !host.IsKeyboardIsolationActive && host.CaptureHandle == 0, "Production exit releases the own editing lease and pointer capture");

            void Pointer(OverlayPointerKind kind, PointD point, OverlayMouseButton button) =>
                Call(session, "HandleCalibrationPointer", new OverlayPointerEvent(kind, new(point.X, point.Y), button, OverlayModifiers.None));

            void AssertInputContinuation(string phase)
            {
                nint foreground = Native.GetForegroundWindow(), focus = GetFocus();
                MultimonitorStages.Add(new { Phase = phase, Foreground = (long)foreground, Focus = (long)focus, Windows = WindowRows(host), host.IsEditing, host.IsKeyboardIsolationActive, Lease = isolation.CurrentLeaseId, Epoch = host.EditingSessionVersion, isolation.LastEndReason });
                Check(host.IsEditing && host.IsKeyboardIsolationActive && isolation.CurrentLeaseId == lease && host.EditingSessionVersion == epoch &&
                    host.Diagnostics.Any(p => p.InputHandle == foreground && p.InputHandle == focus) && host.CaptureHandle == 0,
                    phase + ": replacement input owns foreground/focus with unchanged epoch and lease after old input destruction");
            }

            async Task SaveScopeRenderFailure()
            {
                Call(session, "ShowHotkeySettings");
                await SettleOwnToolbarScene(app);
                var settingsWindow = app.Windows.Cast<Wpf.Window>().Single(w => w.Title == L.Get("Settings.Title"));
                var scope = Descendants(settingsWindow).OfType<ComboBox>().Single(combo => combo.Items.Count == 2 && Equals(combo.Items[0], L.Get("Settings.PrimaryOnly")));
                scope.SelectedIndex = 1;
                var save = Descendants(settingsWindow).OfType<Button>().Single(b => Equals(b.Content, L.Get("Settings.Save")));
                var originalHandles = host.Diagnostics.Select(p => p.DisplayHandle).ToArray();
                int draws = 0;
                void FailDraw(SkiaSharp.SKSurface _) { if (++draws == 1) throw new InvalidOperationException("Injected native candidate render failure"); }
                host.Draw += FailDraw;
                try { save.RaiseEvent(new Wpf.RoutedEventArgs(Button.ClickEvent, save)); }
                finally { host.Draw -= FailDraw; }
                Check(draws == 1 && host.Scope == OverlayMonitorScope.PrimaryScreen && originalHandles.SequenceEqual(host.Diagnostics.Select(p => p.DisplayHandle)),
                    "Actual Save render failure restores the exact original HWND set and screen scope");
                using var persisted = JsonDocument.Parse(File.ReadAllText(Path.Combine(Profile!, "settings.json")));
                Check(persisted.RootElement.GetProperty("PrimaryScreenOnly").GetBoolean(), "Actual Save render failure rolls the already-written scope preference back on disk");
                Check(settingsWindow.IsVisible && Descendants(settingsWindow).OfType<TextBlock>().Any(text => text.Text.Contains("Injected native candidate render failure", StringComparison.Ordinal)),
                    "Actual Save render failure stays in settings and explains its failure");
                Check(host.IsEditing && host.IsKeyboardIsolationActive && isolation.CurrentLeaseId == lease && host.EditingSessionVersion == epoch,
                    "Actual Save render failure preserves the exact editing epoch and keyboard lease");
                MultimonitorStages.Add(new { Phase = "SaveRenderFailureRolledBack", Windows = WindowRows(host), Scope = host.Scope.ToString(), SettingsVisible = settingsWindow.IsVisible, Lease = isolation.CurrentLeaseId, Epoch = host.EditingSessionVersion });
                settingsWindow.Close();
                await SettleOwnToolbarScene(app);
            }

            async Task SaveScope(bool primaryOnly, string phase)
            {
                Call(session, "ShowHotkeySettings");
                await SettleOwnToolbarScene(app);
                var settingsWindow = app.Windows.Cast<Wpf.Window>().Single(w => w.Title == L.Get("Settings.Title"));
                nint settingsHandle = new WindowInteropHelper(settingsWindow).Handle;
                Check(Native.GetForegroundWindow() == settingsHandle, phase + ": production settings owns foreground before Save");
                var scope = Descendants(settingsWindow).OfType<ComboBox>().Single(combo => combo.Items.Count == 2 && Equals(combo.Items[0], L.Get("Settings.PrimaryOnly")));
                scope.SelectedIndex = primaryOnly ? 0 : 1;
                var save = Descendants(settingsWindow).OfType<Button>().Single(b => Equals(b.Content, L.Get("Settings.Save")));
                bool eventObserved = false, focusPreserved = false;
                void Changed()
                {
                    eventObserved = true;
                    focusPreserved = Native.GetForegroundWindow() == settingsHandle;
                    MultimonitorStages.Add(new { Phase = phase + "-scope-event", Foreground = (long)Native.GetForegroundWindow(), Settings = (long)settingsHandle, Scope = host.Scope.ToString(), Monitors = host.Monitors.ToArray(), Windows = WindowRows(host), host.IsVisible, host.IsEditing, host.EditingSessionVersion, Lease = isolation.CurrentLeaseId });
                }
                host.MonitorsChanged += Changed;
                try { save.RaiseEvent(new Wpf.RoutedEventArgs(Button.ClickEvent, save)); }
                finally { host.MonitorsChanged -= Changed; }
                // Observe the synchronous production return, before a dispatcher/render correction.
                int expectedCount = primaryOnly ? 1 : monitors.Length;
                Check(host.Monitors.Count == expectedCount && host.Diagnostics.Count == expectedCount && host.Scope == (primaryOnly ? OverlayMonitorScope.PrimaryScreen : OverlayMonitorScope.AllScreens), phase + ": actual Save immediately switches the physical HWND monitor set");
                Check(eventObserved && focusPreserved, phase + ": rebuild preserves settings foreground throughout the scope event");
                Check(!settingsWindow.IsVisible, phase + ": successful production Save closes settings");
                Check(host.IsEditing && host.IsKeyboardIsolationActive && isolation.CurrentLeaseId == lease && host.EditingSessionVersion == epoch && host.CaptureHandle == 0, phase + ": exact keyboard lease and epoch survive without pointer capture");
                Check(host.Diagnostics.All(p => p.DisplayVisible == host.IsVisible && p.InputVisible), phase + ": replacement HWND visibility matches the current canvas mode");
                AssertOwnUi(phase);
                using var persisted = JsonDocument.Parse(File.ReadAllText(Path.Combine(Profile!, "settings.json")));
                Check(persisted.RootElement.GetProperty("PrimaryScreenOnly").GetBoolean() == primaryOnly, phase + ": the active scope preference is also persisted");
                await SettleOwnToolbarScene(app);
            }

            void AssertOwnUi(string phase)
            {
                var buttons = Field<System.Collections.IDictionary>(session, "_toolbarToolButtons");
                var target = (Button)buttons[buttons.Keys.Cast<object>().Single(key => key.ToString() == "Freehand")]!;
                var close = (Button)inspector.Template.FindName("Close", inspector);
                var observations = new[] { ObserveUi(toolbar, target), ObserveUi(inspector, close) };
                MultimonitorStages.Add(new { Phase = phase + "-returned-immediate", Scope = host.Scope.ToString(), Windows = WindowRows(host), Ui = observations, Foreground = (long)Native.GetForegroundWindow(), Focus = (long)GetFocus() });
                Check(observations.All(o => o.Visible && o.HitOwnWindow && host.Diagnostics.All(p => o.Rank < CurrentToolbarZRank(p.InputHandle) && (!p.DisplayVisible || o.Rank < CurrentToolbarZRank(p.DisplayHandle)))), phase + ": toolbar and Inspector are immediately hittable above every native overlay");
                if (host.IsVisible) Check(observations.All(o => o.Pixels.OccludingPixels == 0), phase + ": controlled button ROIs contain no magenta canvas occlusion");
            }

            MultimonitorUiObservation ObserveUi(Wpf.Window window, Button button)
            {
                nint handle = new WindowInteropHelper(window).Handle;
                var start = button.PointToScreen(new(4, 4));
                var end = button.PointToScreen(new(button.ActualWidth - 4, button.ActualHeight - 4));
                var center = button.PointToScreen(new(button.ActualWidth / 2, button.ActualHeight / 2));
                var roi = System.Drawing.Rectangle.FromLTRB((int)Math.Ceiling(start.X), (int)Math.Ceiling(start.Y), (int)Math.Floor(end.X), (int)Math.Floor(end.Y));
                if (roi.Width <= 0 || roi.Height <= 0 || !primary.Bounds.Contains(new(roi.Left, roi.Top)) || !primary.Bounds.Contains(new(roi.Right - 1, roi.Bottom - 1)))
                    throw new InvalidOperationException("Controlled own-button ROI must lie entirely within the fixture's primary monitor.");
                return new(window.Title, (long)handle, window.IsVisible && Native.IsWindowVisible(handle), Native.WindowFromPoint(new((int)center.X, (int)center.Y)) == handle,
                    CurrentToolbarZRank(handle), roi, host.IsVisible ? ReadOwnPixels(roi) : new(0, 0));
            }
        }
        finally
        {
            if (session is IDisposable disposable) disposable.Dispose(); else host.Dispose();
            foreach (var window in app.Windows.Cast<Wpf.Window>().ToArray()) window.Close();
        }
        Check(isolation is not null && !isolation.IsThreadAlive && !isolation.IsLeaseActive, "Disposing this fixture stops its own keyboard worker and lease");
    }

    private static PointD QuadCenter(IReadOnlyList<PointD> points) => points.Aggregate(new PointD(), (sum, p) => sum + p) * .25;
    private sealed record MultimonitorUiObservation(string Title, long Handle, bool Visible, bool HitOwnWindow, int Rank, System.Drawing.Rectangle Roi, OwnPixelObservation Pixels);
    private static object[] WindowRows(OverlayHost host) => host.Diagnostics.Select(p => (object)new { Display = (long)p.DisplayHandle, Input = (long)p.InputHandle, p.Bounds, p.DisplayVisible, p.InputVisible }).ToArray();
    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject root)
    {
        yield return root;
        foreach (object child in Wpf.LogicalTreeHelper.GetChildren(root))
            if (child is Wpf.DependencyObject dependency)
                foreach (var descendant in Descendants(dependency)) yield return descendant;
    }
}
