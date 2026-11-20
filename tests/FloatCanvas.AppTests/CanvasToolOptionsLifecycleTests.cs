using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class CanvasToolOptionsLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // Run on an isolated desktop: real production window handoffs, no injected input.
    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string profile = ApplicationPaths.ProfileDirectory;
        var scope = ApplicationPaths.RequestedMonitorScope;
        string scratch = Path.Combine(Path.GetTempPath(), "FloatCanvas-tool-config-" + Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", scratch, "--primary-screen"]);
        new UserSettings { Theme = "light", Language = "zh-CN" }.Save();
        var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
        Func<IReadOnlyList<MonitorInfo>> provider = () => [primary];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
        using var session = new DesktopSession(app, host);
        try
        {
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            var toolbar = Field<Window>(session, "_toolbar"); toolbar.ShowActivated = false; toolbar.Opacity = 0; toolbar.Show();
            var ordinary = Field<Window>(session, "_toolOptionsWindow"); ordinary.ShowActivated = false; ordinary.Opacity = 0;
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, true);
            Call(session, "ActivateTool", EditorTool.Freehand);
            Field<Button>(session, "_toolOptionsPin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("brush config pin is the shared tool configuration state", Field<bool>(session, "_toolOptionsPinned") && ordinary.IsVisible);
            double left = ordinary.Left, top = ordinary.Top;
            Call(session, "SwitchToolState", EditorTool.Canvas, false);
            Field<ToolOptionsPanel>(session, "_toolOptionsPanel").PrepareForShow();
            ordinary.UpdateLayout();
            check("direct canvas selection resolves its content before the shared host refreshes", ordinary.Title == L.Get("Tool.Canvas") && ReferenceEquals(ordinary, Field<Window>(session, "_toolOptionsWindow")));
            var canvas = Field<CanvasToolOptions>(session, "_canvasToolOptions");
            check("canvas content uses the same window and pin as brush configuration", canvas.IsVisible && Field<bool>(session, "_toolOptionsPinned") && ordinary.IsVisible);
            check("canvas handoff keeps the configuration origin within screen constraints", Math.Abs(ordinary.Left - left) < 1 && Math.Abs(ordinary.Top - top) < 1);
            Field<Button>(session, "_toolOptionsPin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("canvas pin control updates the shared pin", !Field<bool>(session, "_toolOptionsPinned") && !Field<bool>(session, "_toolOptionsPinned"));
            Field<Button>(session, "_toolOptionsPin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Call(session, "ActivateTool", EditorTool.Rectangle);
            check("rectangle inherits the pin in the same configuration window", ordinary.IsVisible && !canvas.IsVisible && Field<bool>(session, "_toolOptionsPinned"));
            Call(session, "ActivateTool", EditorTool.Canvas);
            check("returning to canvas preserves the shared pin", canvas.IsVisible && Field<bool>(session, "_toolOptionsPinned") && ordinary.IsVisible);
            ordinary.Close();
            check("caption close clears the shared pin and closes only the configuration", !canvas.IsVisible && !ordinary.IsVisible && !Field<bool>(session, "_toolOptionsPinned") && host.IsEditing);
            Call(session, "OpenToolOptions");
            check("common open command reopens the current canvas tool unpinned", canvas.IsVisible && !Field<bool>(session, "_toolOptionsPinned"));
            Call(session, "CloseToolOptions");
            check("common close command closes canvas configuration too", !canvas.IsVisible);
            Call(session, "OpenToolOptions"); Call(session, "ActivateTool", EditorTool.Canvas);
            check("reclicking current tool toggles canvas configuration closed", !canvas.IsVisible);
            Call(session, "OpenToolOptions"); Call(session, "DismissCanvasSettings", WindowDismissReason.CanvasPress);
            check("canvas press dismissal shares transient close semantics", !canvas.IsVisible && !Field<bool>(session, "_toolOptionsPinned"));
            Call(session, "OpenToolOptions"); Call(session, "OnKey", 0x1B);
            check("canvas escape dismisses unpinned configuration without leaving editing", !canvas.IsVisible && host.IsEditing);
            Call(session, "OpenToolOptions");
            var editor = Field<EditorController>(session, "_editor"); int history = editor.Session.UndoCount;
            var size = Field<NumericValueField>(canvas, "gridSize"); double original = size.Value;
            size.BeginGesture(); size.Input.Text = (original + 7).ToString(System.Globalization.CultureInfo.CurrentCulture);
            check("canvas numeric adjustment uses the normal transactional preview", size.IsInteracting && editor.Session.IsTransactionActive);
            Call(session, "ActivateTool", EditorTool.Rectangle);
            check("switching configuration cancels an unfinished canvas gesture", !size.IsInteracting && !editor.Session.IsTransactionActive && editor.Session.UndoCount == history);
            Call(session, "ActivateTool", EditorTool.Canvas);
            editor.Session.Document.Canvas.Grid.Visible = true; Call(session, "RefreshCanvasSettingsTarget");
            var width = Field<NumericValueField>(canvas, "width"); width.BeginGesture(); width.Slider.Value = 7;
            typeof(DesktopSession).GetProperty("_documentBusy", Private)!.SetValue(session, true);
            check("document busy state cancels the current gesture and disables the shared host", !ordinary.IsEnabled && !width.IsInteracting && !editor.Session.IsTransactionActive && editor.Session.UndoCount == history);
            typeof(DesktopSession).GetProperty("_documentBusy", Private)!.SetValue(session, false);
            check("document completion reenables the same configuration window", ordinary.IsEnabled && ReferenceEquals(Field<Window>(session, "_toolOptionsWindow"), ordinary));
            Call(session, "SetCanvasProjection", true);
            canvas.PrepareForShow();
            var depth = Field<CaptionModeSwitch>(canvas, "depthPriorityOcclusion");
            var appearance = Field<CanvasPreviewAppearance>(session, "_canvasPreviewAppearance");
            int beforeDepth = editor.Session.UndoCount; long revision = editor.Session.PreviewRevision;
            string snapshot = System.Text.Json.JsonSerializer.Serialize(editor.Session.CreateCommittedSnapshot());
            depth.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("caption rejects a stale target before the queued frame refresh", !appearance.DepthPriorityOcclusion);
            Call(session, "RefreshCanvasSettingsTarget"); canvas.PrepareForShow();
            depth.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("production caption toggles the run-local depth preference", appearance.DepthPriorityOcclusion && !depth.SelectedFirst);
            check("production depth toggle leaves saved content and history untouched", editor.Session.UndoCount == beforeDepth && editor.Session.PreviewRevision == revision &&
                System.Text.Json.JsonSerializer.Serialize(editor.Session.CreateCommittedSnapshot()) == snapshot);
            using (var frame = (CanvasPreviewSnapshot)typeof(DesktopSession).GetMethod("CaptureCanvasPreview", Private)!.Invoke(session, null)!)
                check("production capture applies depth only to the frozen preview", frame.Context.DepthPriorityOcclusion && frame.FrozenEvaluation!.SourceDocument.Canvas.DepthPriorityOcclusion &&
                    !editor.Session.Document.Canvas.DepthPriorityOcclusion);
            editor.Session.Undo(); Call(session, "RefreshCanvasSettingsTarget"); canvas.PrepareForShow();
            check("undoing authored projection preserves depth choice in a disabled caption", appearance.DepthPriorityOcclusion && !depth.SelectedFirst && !depth.IsEnabled);
            editor.Session.Redo(); Call(session, "RefreshCanvasSettingsTarget"); canvas.PrepareForShow();
            check("redoing perspective reenables the retained depth choice", depth.IsEnabled && !depth.SelectedFirst);
            Call(session, "OpenToolOptions");
            Call(session, "ActivateTool", EditorTool.Freehand);
            var brushHeader = Field<CaptionModeSwitch>(Field<ToolOptionsPanel>(session, "_toolOptionsPanel"), "freehandSmoothing");
            check("switching from canvas restores the brush correction switch in the shared caption",
                ordinary.IsVisible && ReferenceEquals(Field<ContentControl>(session, "_toolOptionsHeaderContent").Content, brushHeader));
            Call(session, "ActivateTool", EditorTool.Canvas);
            check("returning to canvas restores the same caption and preview choice", ReferenceEquals(Field<ContentControl>(session, "_toolOptionsHeaderContent").Content, canvas.Header.Actions) && !depth.SelectedFirst);
            check("tool configuration tests never show overlay input windows or acquire isolation", !host.IsKeyboardIsolationActive);
        }
        finally
        {
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, false);
            session.Dispose();
            var args = new List<string> { "--profile", profile };
            if (scope is { } value) args.Add(value == OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens");
            ApplicationPaths.Configure(args.ToArray());
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }

    private static T Field<T>(object source, string name) => (T)(CanvasOptionsTestWindow.Unwrap(source)).GetType().GetField(name, Private)!.GetValue(CanvasOptionsTestWindow.Unwrap(source))!;
    private static object? Call(object source, string name, params object[] args) => source.GetType().GetMethod(name, Private)!.Invoke(source, args);
}
