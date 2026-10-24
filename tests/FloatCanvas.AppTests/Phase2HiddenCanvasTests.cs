using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class Phase2HiddenCanvasTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host)
    {
        var editor = (EditorController)typeof(DesktopSession).GetField("_editor", Private)!.GetValue(session)!;
        host.SetVisible(true);
        var original = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 30, Transform = Affine2D.Translation(30, 30) };
        editor.Session.Add(original); editor.Session.SelectAll();
        Invoke("ToggleCanvasVisibility");
        editor.Tool = EditorTool.Rectangle;
        var bounds = host.Monitors.Single().Bounds;
        Send(OverlayPointerKind.Down, 140, 130); Send(OverlayPointerKind.Move, 240, 200);
        check("hidden canvas permits a draft while keeping existing content hidden", !host.IsVisible &&
            editor.Session.IsTransactionActive && editor.Session.Document.Elements.Count == 2 && !editor.Session.Selection.Contains(original.Id));
        var preview = (SceneDocument)Invoke("HiddenCanvasPreview")!;
        check("only the new draft is sent to the preview renderer", host.IsPreviewVisible && preview.Background is null &&
            preview.Canvas.BackgroundColor == 0 && preview.Elements.Count == 1 && preview.Elements[0].Id != original.Id);
        Send(OverlayPointerKind.Up, 240, 200);
        check("committing a new hidden-mode layer opens the eye and selects the new item", host.IsVisible &&
            !editor.Session.IsTransactionActive && editor.Session.Selection.Count == 1 && !editor.Session.Selection.Contains(original.Id));
        Invoke("ToggleCanvasVisibility"); editor.Tool = EditorTool.Select;
        Send(OverlayPointerKind.Down, 35, 35); Send(OverlayPointerKind.Up, 35, 35);
        check("hidden existing layers cannot be selected or moved", !host.IsVisible && editor.Session.Selection.Count == 0 &&
            editor.Session.Document.Elements.Single(item => item.Id == original.Id).Transform == Affine2D.Translation(30, 30));
        editor.Tool = EditorTool.Rectangle;
        Send(OverlayPointerKind.Down, 300, 220); Send(OverlayPointerKind.Move, 350, 260); Send(OverlayPointerKind.Cancel, 350, 260);
        check("cancelling a hidden draft keeps the eye closed and removes only the draft", !host.IsVisible && !host.IsPreviewVisible && editor.Session.Document.Elements.Count == 2);
        var grid = new CanvasGridScaleController();
        check("hidden old pixels do not block canvas grid adjustment", grid.Down(editor.Session, new(35, 35), editor.Session.Document.Viewport, hitExistingContent: false));
        grid.Cancel();
        check("hidden canvas allows insertion entry points without opening the eye early", (bool)Invoke("CanAddCanvasContent")! && !host.IsVisible);
        editor.Session.Add(new CanvasElement { Kind = ElementKind.Ellipse, Width = 20, Height = 20 });
        check("atomic insertion entry points reveal after their commit", host.IsVisible && !host.IsPreviewVisible);

        object? Invoke(string method, params object[] args) => typeof(DesktopSession).GetMethod(method, Private)!.Invoke(session, args);
        void Send(OverlayPointerKind kind, int x, int y) => Invoke("HandleCanvasPointer",
            new OverlayPointerEvent(kind, new(bounds.Left + x, bounds.Top + y), OverlayMouseButton.Left, OverlayModifiers.None));
    }
}
