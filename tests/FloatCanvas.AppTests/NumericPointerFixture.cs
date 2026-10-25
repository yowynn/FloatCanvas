using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Opt-in pointer diagnostics in an isolated profile. A start button enters
/// this fixture's own bounded desktop session; the preview exits after four minutes.
/// Gesture logging belongs only to the test process, never the shipped application.</summary>
internal static class NumericPointerFixture
{
    internal static void Preview(string output, bool system = false, bool desktop = false, bool inspector = false)
    {
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var host = system || desktop ? CreateHost() : null;
        if (system) host!.EnableSystemInput();
        using var session = host is null ? null : new DesktopSession(app, host);
        FloatCanvas.Localization.AppText.Initialize("zh-CN"); AppTheme.Apply("light");
        T Field<T>(string name) => (T)typeof(DesktopSession).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        var controller = session is null ? new EditorController(new EditorSession()) : Field<EditorController>("_editor");
        controller.Tool = EditorTool.Rectangle;
        var panel = session is null ? new ToolOptionsPanel(controller, new(Path.Combine(output, "presets.json")), new(Path.Combine(output, "colors.json"))) : Field<ToolOptionsPanel>("_toolOptionsPanel");
        if (inspector)
        {
            Field<Window>("_toolbar").Show();
            Field<Window>("_toolbar").UpdateLayout();
            controller.Session.Add(new() { Kind = ElementKind.Rectangle, Width = 160, Height = 100 });
            typeof(DesktopSession).GetMethod("ToggleInspector", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(session, null);
        }
        var window = inspector ? Field<InspectorWindow>("_inspector") : session is null ? new Window { Content = panel, Width = ToolOptionsPanel.PreferredWidth + 2, Height = 680 } : Field<Window>("_toolOptionsWindow");
        window.Title = "FloatCanvas Numeric Pointer Check"; window.Left = 180; window.Top = 120; window.ShowInTaskbar = true;
        if (session is null) AppWindowChrome.Apply(window);
        else { Field<System.Windows.Forms.NotifyIcon>("_tray").Visible = false; typeof(DesktopSession).GetField("_toolOptionsPinned", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, true); }
        panel.Refresh();
        var fields = (Dictionary<string, NumericValueField>)(inspector ? typeof(InspectorWindow) : typeof(ToolOptionsPanel)).GetField("numericFields", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(inspector ? window : panel)!;
        var captions = fields.ToDictionary(pair => pair.Key, pair => pair.Value.DragAnchor);
        var trace = Path.Combine(output, "pointer.log");
        void Log(string line) { File.AppendAllText(trace, line + Environment.NewLine); Console.WriteLine(line); }
        panel.ParametersChanged += () => Log($"PARAMETERS width={controller.DefaultWidth} height={controller.DefaultHeight} locked={controller.ConstrainAspect}");
        if (inspector) controller.Session.Changed += (_, _) =>
        {
            var layer = controller.Session.Document.Elements.FirstOrDefault();
            if (layer is not null) Log($"LAYER width={layer.Width} height={layer.Height} locked={layer.ConstrainAspect}");
        };
        foreach (string key in inspector ? new[] { "StrokeWidth", "W", "H", "Opacity" } : new[] { "StrokeWidth", "DefaultWidth", "DefaultHeight" })
        {
            var field = fields[key]; double start = 0;
            field.GestureStarted += () => { start = field.Value; Log($"BEGIN {key} {start}"); };
            field.GestureCompleted += commit => Log($"END {key} commit={commit} before={start} value={field.Value} changed={start != field.Value}");
            field.ValueChanged += value => Log($"VALUE {key} {value}");
            foreach (var target in new FrameworkElement[] { captions[key], field.Slider })
            {
                target.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) => Log($"DOWN {key}/{target.GetType().Name} left={e.LeftButton} captured={Mouse.Captured?.GetType().Name} active={field.IsInteracting}")), true);
                target.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler((_, e) => { if (e.LeftButton == MouseButtonState.Pressed) Log($"MOVE {key}/{target.GetType().Name} x={e.GetPosition(window).X:0.0} captured={Mouse.Captured?.GetType().Name} active={field.IsInteracting}"); }), true);
                target.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, e) => Log($"LOST {key}/{target.GetType().Name} source={e.OriginalSource?.GetType().Name} active={field.IsInteracting}")), true);
            }
        }
        window.Closed += (_, _) => app.Shutdown();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromMinutes(4) }; timeout.Tick += (_, _) => { timeout.Stop(); app.Shutdown(); };
        timeout.Start();
        if (host is not null)
        {
            window.Hide(); Field<Window>("_toolbar").Hide();
            var start = new Button { Content = "Start isolated numeric check", Margin = new Thickness(20) };
            var gate = new Window { Title = "FloatCanvas Numeric Start", Content = start, Width = 340, Height = 140, Left = 180, Top = 120, ShowInTaskbar = true };
            host.InputActivated += () => { window.Show(); Log($"EDITING ready={host.HasEditingInput}"); };
            start.Click += (_, _) =>
            {
                gate.Hide();
                host.SetVisible(true);
                typeof(DesktopSession).GetMethod("SetEditing", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(session, [true, false]);
                window.Show(); Log($"EDITING request={host.IsEditing}");
            };
            gate.Show();
        }
        else window.Show();
        Log("READY"); app.Run();
    }

    private static OverlayHost CreateHost()
    {
        var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
        Func<IReadOnlyList<MonitorInfo>> provider = () => [primary with { Bounds = new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 30, 640, 360) }];
        return (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), BindingFlags.NonPublic | BindingFlags.Instance, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
    }
}
