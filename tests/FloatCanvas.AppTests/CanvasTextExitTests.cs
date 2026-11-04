using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class CanvasTextExitTests
{
    internal static void Run(Action<string, bool> check)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        ApplicationPaths.Configure(["--profile", Path.Combine(Path.GetTempPath(), "FloatCanvas-text-exit-" + Guid.NewGuid().ToString("N"))]);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var monitor = new MonitorInfo("text-exit", new(0, 0, 640, 360), new(0, 0, 640, 330), 96, 96, true);
        Func<IReadOnlyList<MonitorInfo>> screens = () => [monitor];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), hidden, null, [OverlayMonitorScope.PrimaryScreen, screens, false], null)!;
        using var desktop = new DesktopSession(app, host);
        T Field<T>(string name) => (T)typeof(DesktopSession).GetField(name, hidden)!.GetValue(desktop)!;
        void Set(string name, object? value) => typeof(DesktopSession).GetField(name, hidden)!.SetValue(desktop, value);
        object? Call(string name, params object?[] values) => typeof(DesktopSession).GetMethod(name, hidden)!.Invoke(desktop, values);
        Field<System.Windows.Forms.NotifyIcon>("_tray").Visible = false;
        Field<Window>("_toolbar").Show();
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, true);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
        var editor = Field<EditorController>("_editor");
        editor.Session.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 80 });
        Call("CurrentGroupCanvasView");
        Call("ChangeCanvasViewing", new CanvasViewTransform(1.5, new PointD(20, 30)), false);
        var navigation = Field<Button>("_canvasNavigationButton");
        var menu = Field<OverlayContextMenu>("_canvasNavigationMenu");
        int history = editor.Session.UndoCount;
        navigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check("navigation opens its separate menu with reset as the first command", menu.IsOpen &&
            ((MenuItem)menu.Items[0]).Header.Equals(FloatCanvas.Localization.AppText.Get("Canvas.ResetView")));
        check("open navigation button describes its reset action", System.Windows.Automation.AutomationProperties.GetName(navigation) == FloatCanvas.Localization.AppText.Get("Canvas.ResetView"));
        navigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check("a second navigation click resets and closes without authoring history", !menu.IsOpen &&
            Field<CanvasViewTransform>("_canvasView") == CanvasViewTransform.Identity && editor.Session.UndoCount == history);
        editor.Session.Document.Canvas.Projection = CanvasCalibration.CreateInitial(editor.Session.Document.Viewport);
        editor.Session.Document.Canvas.Projection.Enabled = true;
        editor.Tool = EditorTool.Canvas;
        Call("SynchronizeCanvasTool");
        check("selecting the canvas tool directly activates perspective calibration", Field<CanvasCalibrationController>("_canvasCalibration").IsActive);
        Call("EndCanvasCalibration", false, true);
        editor.Session.Document.Canvas.Projection.Enabled = false;
        editor.Tool = EditorTool.Text;
        foreach (bool billboard in new[] { false, true })
        foreach (bool commit in new[] { false, true })
        {
            editor.BillboardWorkflow = BillboardCreationWorkflow.ScreenFirst;
            editor.Session.Document.Canvas.Projection.Enabled = billboard;
            editor.Session.NotifyChanged();
            Call("CurrentGroupCanvasView");
            editor.BillboardMode = billboard;
            Call("ShowTextEditor", null, new PointD(200, 100));
            Field<InlineTextEdit>("_inlineText").Input.Text = "Keep canvas visible";
            check("text exit fixture exercises the requested placement mode billboard=" + billboard,
                Field<InlineTextEdit>("_inlineText").HasScreenDraft == billboard);
            using var snapshot = (CanvasPreviewSnapshot)Call("CaptureCanvasPreview")!;
            using var renderer = new CanvasPreviewRenderer();
            using var frame = renderer.Render(snapshot);
            Field<IDisposable?>("_presentedPixels")?.Dispose();
            Set("_presentedPixels", frame.Pixels.Retain());
            Set("_presentedCanvasContext", snapshot.Context);
            Set("_submittingCanvasContext", snapshot.Context);
            Set("_submittingCanvasFrame", frame.Retain());
            Call("FinishInlineText", commit, false);
            Call("CompleteCanvasSubmission", new OverlayFrameTiming(1, 2, true, []));
            check("late text frame completion retains the displayed baseline", Field<object?>("_presentedPixels") is not null);
            var request = (OverlayFrameRequest)Activator.CreateInstance(typeof(OverlayFrameRequest), hidden, null, [null], null)!;
            Call("BeginCanvasSubmission", request);
            check("text exit retains displayed canvas until replacement pixels exist commit=" + commit, request.Deferred);
            check("text exit retains the unrelated rectangle commit=" + commit,
                editor.Session.Document.Elements.Any(element => element.Kind == ElementKind.Rectangle));
            typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, false);
            var hiddenRequest = (OverlayFrameRequest)Activator.CreateInstance(typeof(OverlayFrameRequest), hidden, null, [null], null)!;
            Call("BeginCanvasSubmission", hiddenRequest);
            check("text exit never retains artwork after visibility is turned off commit=" + commit, !hiddenRequest.Deferred);
            typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, true);
        }
    }
}
