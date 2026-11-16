using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class ProjectPreviewNavigationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, string? output = null)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Func<IReadOnlyList<MonitorInfo>> screens = () => [new("navigation", new(0, 0, 640, 360), new(0, 0, 640, 330), 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null,
            [OverlayMonitorScope.AllScreens, screens, false], null)!;
        using var desktop = new DesktopSession(app, host);
        T Field<T>(string name) => (T)typeof(DesktopSession).GetField(name, Hidden)!.GetValue(desktop)!;
        object? Call(string name, params object?[] args) => typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(desktop, args);
        Field<System.Windows.Forms.NotifyIcon>("_tray").Visible = false;
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, true);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
        DesktopSyntheticKeyboard.Allow(desktop, host);
        var editor = Field<EditorController>("_editor");
        editor.BackgroundPickingEnabled = false;
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 20 };
        var inner = new CanvasGroup { Name = "Inner", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [element.Id] };
        var outer = new CanvasGroup { Name = "Outer", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [inner.Id] };
        inner.ParentId = outer.Id; element.GroupId = inner.Id;
        editor.Session.ReplaceDocument(new() { Viewport = new(0, 0, 640, 360), Elements = [element], Groups = [outer, inner], RootOrder = [outer.Id] });
        editor.Session.SetEditScope(inner.Id);
        Call("SetGroupCanvasMode", GroupCanvasMode.RootProject);
        Call("CurrentGroupCanvasView");
        editor.Tool = EditorTool.Rectangle;
        string before = JsonSerializer.Serialize(editor.Session.Document);
        int undo = editor.Session.UndoCount;
        Call("OnCanvasKeyInput", new OverlayKeyEvent(0x20, OverlayModifiers.None, false));
        var initial = Field<CanvasViewTransform>("_canvasView");
        foreach (var (kind, x, y) in new[] { (OverlayPointerKind.Down, 200d, 140d), (OverlayPointerKind.Move, 230d, 160d), (OverlayPointerKind.Up, 230d, 160d) })
            Call("HandleCanvasPointer", new OverlayPointerEvent(kind, new(x, y), OverlayMouseButton.Left, OverlayModifiers.None));
        check("Space pan in a deep project preview retains mode and scope",
            Field<GroupCanvasMode>("_groupCanvasMode") == GroupCanvasMode.RootProject && editor.Session.EditScopeId == inner.Id);
        var panned = Field<CanvasViewTransform>("_canvasView");
        check("Space pan moves the root camera without editing artwork", panned.Offset == initial.Offset + new PointD(30, 20) &&
            before == JsonSerializer.Serialize(editor.Session.Document) && editor.Session.UndoCount == undo && editor.Tool == EditorTool.Rectangle);
        Call("HandleCanvasPointer", new OverlayPointerEvent(OverlayPointerKind.Wheel, new(230, 160), OverlayMouseButton.None, OverlayModifiers.Control, 120));
        var zoomed = Field<CanvasViewTransform>("_canvasView");
        check("Ctrl wheel retains project mode and zooms the root camera", Field<GroupCanvasMode>("_groupCanvasMode") == GroupCanvasMode.RootProject && zoomed.Zoom > panned.Zoom);
        ((Action<int>)typeof(OverlayHost).GetField("KeyReleased", Hidden)!.GetValue(host)!)(0x20);
        var rootTarget = (CanvasSettingsTarget)Call("CaptureCanvasSettingsTarget")!;
        check("deep project canvas operations target the document canvas", !rootTarget.Independent && ReferenceEquals(rootTarget.Settings, editor.Session.Document.Canvas));
        check("deep project view may be applied to the root canvas", (bool)typeof(DesktopSession).GetProperty("CanApplyCanvasViewing", Hidden)!.GetValue(desktop)!);
        editor.Tool = EditorTool.Canvas;
        Call("SynchronizeCanvasTool");
        double gridSize = CanvasGridGeometry.ResolveCellSize(editor.Session.Document.Canvas, editor.Session.Document.Viewport);
        Call("HandleCanvasPointer", new OverlayPointerEvent(OverlayPointerKind.Wheel, new(230, 160), OverlayMouseButton.None, OverlayModifiers.Shift, 120));
        check("Shift wheel leaves root camera, grid and group settings unchanged",
            Field<GroupCanvasMode>("_groupCanvasMode") == GroupCanvasMode.RootProject && editor.Session.EditScopeId == inner.Id &&
            CanvasGridGeometry.ResolveCellSize(editor.Session.Document.Canvas, editor.Session.Document.Viewport) == gridSize && inner.PreviewCanvas is null &&
            Field<CanvasViewTransform>("_canvasView") == zoomed);
        zoomed = Field<CanvasViewTransform>("_canvasView");
        Call("SetGroupCanvasMode", GroupCanvasMode.Temporary); Call("CurrentGroupCanvasView");
        Call("ChangeCanvasViewing", new CanvasViewTransform(3, new(2, 4)), false);
        Call("SetGroupCanvasMode", GroupCanvasMode.RootProject); Call("CurrentGroupCanvasView");
        check("independent viewing does not replace the project camera", SameView(Field<CanvasViewTransform>("_canvasView"), zoomed));
        var navigation = Field<Button>("_canvasNavigationButton");
        foreach (string theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme);
            check(theme + " changed view uses the eye's complete orange appearance", SameBrush(navigation.Background, "CanvasHiddenSurface") &&
                SameBrush(navigation.BorderBrush, "CanvasHiddenBorder") && SameBrush(NavigationInk().Stroke, "CanvasHiddenText") && navigation.BorderThickness == new Thickness(1));
            SaveNavigation(theme + "-changed");
            Call("ExecuteCanvasViewing", CanvasViewingCommand.Reset);
            check(theme + " resetting view restores neutral background, border and icon", SameBrush(navigation.Background, "CanvasButton") &&
                navigation.BorderBrush == Brushes.Transparent && SameBrush(NavigationInk().Stroke, "CanvasText"));
            SaveNavigation(theme + "-default");
            Call("ChangeCanvasViewing", zoomed, false);
        }
        AppTheme.Apply("light");
        editor.Session.SetEditScope(null); Call("CurrentGroupCanvasView");
        check("leaving the group retains the same root camera", SameView(Field<CanvasViewTransform>("_canvasView"), zoomed));

        bool SameBrush(Brush brush, string key) => brush is SolidColorBrush actual && navigation.FindResource(key) is SolidColorBrush expected && actual.Color == expected.Color;
        System.Windows.Shapes.Path NavigationInk() => ((Canvas)((Viewbox)navigation.Content).Child).Children.OfType<System.Windows.Shapes.Path>().Single();
        static bool SameView(CanvasViewTransform a, CanvasViewTransform b) => Math.Abs(a.Zoom - b.Zoom) < 1e-9 && a.Offset.DistanceTo(b.Offset) < 1e-7 && a.PlaneOffset.DistanceTo(b.PlaneOffset) < 1e-7;
        void SaveNavigation(string name)
        {
            if (output is null) return;
            System.IO.Directory.CreateDirectory(output);
            navigation.Measure(new Size(40, 40)); navigation.Arrange(new Rect(new Point(), navigation.DesiredSize)); navigation.UpdateLayout();
            foreach (double scale in new[] { 1d, 1.5, 2d })
            {
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(navigation.ActualWidth * scale),
                    (int)Math.Ceiling(navigation.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(navigation);
                using var stream = System.IO.File.Create(System.IO.Path.Combine(output, $"view-{name}-{scale}.png"));
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); encoder.Save(stream);
            }
        }
    }
}
