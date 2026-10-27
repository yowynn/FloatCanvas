using System.IO;
using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;
using Controls = System.Windows.Controls;

internal static class PresetSizingCompatibilityTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var session = new EditorSession(); var editor = new EditorController(session);
        var library = new ToolPresetLibrary(Path.Combine(ApplicationPaths.ProfileDirectory, "sizing-presets.json"));
        editor.ApplyParameters(library.Apply("official.number"));
        Click(editor, 200, 200); var number = session.Document.Elements.Single();
        check("official number has a 72 pixel badge and locked instance aspect", number.Width == 72 && number.Height == 72 && ElementGeometry.PreservesAspect(number));
        editor.ApplyParameters(library.Apply("official.emoji"));
        Click(editor, 400, 200); var emoji = session.Document.Elements.Last();
        check("equal number and emoji sizes have equal geometric selection frames", ElementGeometry.LocalBounds(number) == ElementGeometry.LocalBounds(emoji));
        check("emoji instances preserve aspect by default", ElementGeometry.PreservesAspect(emoji));
        var panel = new ToolOptionsPanel(editor, library, new ColorLibrary(Path.Combine(ApplicationPaths.ProfileDirectory, "sizing-colors.json"))); panel.PrepareForShow();
        var rows = Field<Dictionary<string, System.Windows.FrameworkElement>>(panel, "rows");
        check("emoji tools hide aspect options", rows["Aspect"].Visibility == System.Windows.Visibility.Collapsed);
        editor.Tool = EditorTool.Number; panel.PrepareForShow();
        var captions = Field<Dictionary<string, Controls.TextBlock>>(panel, "captions");
        check("number tools label their size and color roles", captions["FontSize"].Text == FloatCanvas.Localization.AppText.Get("ToolOptions.Size") && captions["Foreground"].Text == FloatCanvas.Localization.AppText.Get("Color.NumberShape"));
        check("number tools hide aspect options", rows["Aspect"].Visibility == System.Windows.Visibility.Collapsed);
        check("linked colors are persistent model state", typeof(ElementStyle).GetProperty("LinkColors") is not null);
        foreach (var preset in library.Official.Where(p => p.Parameters.Tool is EditorTool.Rectangle or EditorTool.Ellipse))
            check(preset.Id + " has one consistent 108 pixel side", preset.Parameters.DefaultWidth == 108 || preset.Parameters.DefaultHeight == 108);
        check("official text uses 36 point type", library.Apply("official.text").Style.FontSize == 36);
        var stretched = emoji.Clone(); stretched.Width = 144; stretched.Height = 36; stretched.Transform = Affine2D.Translation(10, 10);
        using var renderer = new SceneRenderer(); using var bitmap = SKBitmap.Decode(renderer.ExportPng(new SceneDocument { Viewport = new(0, 0, 180, 80), Elements = [stretched] }, session.Assets, true));
        var ink = Enumerable.Range(0, bitmap.Height).SelectMany(y => Enumerable.Range(0, bitmap.Width).Where(x => bitmap.GetPixel(x, y).Alpha > 16).Select(x => (X:x,Y:y))).ToArray();
        check("emoji artwork stretches to its actual frame", ink.Max(p => p.X) - ink.Min(p => p.X) > 138 && ink.Max(p => p.Y) - ink.Min(p => p.Y) < 38);
        LinkedColors(editor, library, check);
        var badgeScene = new SceneDocument { Viewport = new(0,0,72,72), Elements = [number.Clone()] }; badgeScene.Elements[0].Transform = Affine2D.Identity;
        using var badge = SKBitmap.Decode(renderer.ExportPng(badgeScene, session.Assets, true));
        check("new badges use foreground red for shapes and background white for labels", badge.GetPixel(36, 8) == new SKColor(ColorLibrary.ForegroundRed) && badge.Pixels.Count(p => p == SKColors.White) > 30);
        var oldBadge = number.Clone(); oldBadge.NumberUsesForeground = false; oldBadge.Style.Stroke = 0xFF00FF00; oldBadge.Style.Fill = 0xFF2383E2;
        badgeScene.Elements = [oldBadge]; oldBadge.Transform = Affine2D.Identity;
        using var legacy = SKBitmap.Decode(renderer.ExportPng(badgeScene, session.Assets, true));
        check("legacy saved number colors retain their previous blue shape and white label", legacy.GetPixel(36,8) == new SKColor(0xFF2383E2) && legacy.Pixels.Count(p => p == SKColors.White) > 20);
    }
    private static void LinkedColors(EditorController editor, ToolPresetLibrary library, Action<string,bool> check)
    {
        editor.Tool = EditorTool.Rectangle; editor.Style.Stroke = 0xFF2383E2; editor.Style.Fill = 0;
        var panel = new ToolOptionsPanel(editor, library, new ColorLibrary(Path.Combine(ApplicationPaths.ProfileDirectory,"linked-colors.json"))); panel.PrepareForShow();
        var linked = Field<Controls.CheckBox>(panel, "linkColors"); linked.IsChecked = true; linked.RaiseEvent(new(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        panel.PrepareForShow();
        var rows = Field<Dictionary<string,System.Windows.FrameworkElement>>(panel,"rows");
        check("linked tool colors expose one foreground color and preserve the saved background", editor.Style.LinkColors && editor.Style.Fill == 0 && rows["Background"].Visibility == System.Windows.Visibility.Collapsed);
        var saved = library.Save("Linked shape", editor.Parameters);
        check("user presets persist their independent linked-color state", new ToolPresetLibrary(Path.Combine(ApplicationPaths.ProfileDirectory,"sizing-presets.json")).User.Single(p=>p.Id==saved.Id).Parameters.Style.LinkColors);
        var shape = new CanvasElement { Kind = ElementKind.Rectangle, Width=40, Height=40, Style=editor.Style.Clone() };
        var scene = new SceneDocument { Viewport=new(-4,-4,48,48),Elements=[shape] };
        using var renderer = new SceneRenderer(); using var bitmap = SKBitmap.Decode(renderer.ExportPng(scene,new(),true));
        check("linked shape fill renders and hit-tests with the foreground despite transparent saved fill", bitmap.GetPixel(24,24)==new SKColor(0xFF2383E2) && ElementGeometry.HitTest(shape,new(20,20),0));
        var session = new EditorSession(scene); session.SetSelection([shape.Id]); var inspector = new InspectorWindow(()=>session); inspector.PrepareForShow();
        var containers = Field<Dictionary<string,System.Windows.FrameworkElement>>(inspector,"fieldContainers");
        check("linked layer inspector exposes one color and its link checkbox", containers["Fill"].Visibility==System.Windows.Visibility.Collapsed && containers["LinkColors"].Visibility==System.Windows.Visibility.Visible);
        string path=Path.Combine(ApplicationPaths.ProfileDirectory,"linked-project.floatcanvas"); var store=new ProjectStore();
        store.SaveAsync(path,scene,new()).GetAwaiter().GetResult();
        check("project roundtrip preserves linked colors", store.LoadAsync(path).GetAwaiter().GetResult().Document.Elements.Single().Style.LinkColors);
        var recovery=new RecoveryStore(Path.Combine(ApplicationPaths.ProfileDirectory,"linked-recovery"));
        recovery.SaveAsync(new WorkspaceState {LiveDocument=scene},new()).GetAwaiter().GetResult();
        check("workspace recovery preserves linked colors", recovery.LoadAsync().GetAwaiter().GetResult()!.State.LiveDocument.Elements.Single().Style.LinkColors);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Click(EditorController editor, double x, double y)
    {
        editor.Pointer(new(OverlayPointerKind.Down, new(x,y), OverlayMouseButton.Left, OverlayModifiers.None));
        editor.Pointer(new(OverlayPointerKind.Up, new(x,y), OverlayMouseButton.Left, OverlayModifiers.None));
    }
}
