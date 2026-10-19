using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class MoveIntoViewRuntimeFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession desktop, OverlayHost host)
    {
        var editor = Field<EditorController>(desktop, "_editor"); var session = editor.Session; var viewport = session.Document.Viewport;
        var visible = Box(viewport.Left + 80, viewport.Top + 80, 0xFF2383E2);
        var outside = Box(viewport.Right + 120, viewport.Top + 180, 0xFFEA6840);
        var locked = Box(viewport.Right + 200, viewport.Top + 40, 0xFF888888); locked.Locked = true;
        var hidden = Box(viewport.Left - 200, viewport.Top + 80, 0xFF888888); hidden.Visible = false;
        session.Execute("fixture scene", document => document.Elements = [visible, outside, locked, hidden]);
        session.SetSelection([visible.Id]);
        string before = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount;
        byte[] beforePreview = WorkspacePreview.Render(session.Document, session.Assets)!;
        var menu = Preview4MenuTests.Items(Field<ContextMenu>(desktop, "_toolbarMenu")).Single(item => Equals(item.Header, L.Get("Menu.MoveIntoView")));
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
        check("actual menu recovers only the fully offscreen object", visible.Transform.OffsetX == viewport.Left + 80 && outside.Transform.OffsetX == viewport.Right - 80 && outside.Transform.OffsetY == viewport.Top + 180 && locked.Transform.OffsetX == viewport.Right + 200 && hidden.Transform.OffsetX == viewport.Left - 200);
        check("actual recovery keeps editing and selection with one undo", host.IsEditing && session.Selection.SetEquals([visible.Id]) && session.UndoCount == undo + 1);
        string path = Path.Combine(Path.GetTempPath(), "FloatCanvas-move-recovered-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, WorkspacePreview.Render(session.Document, session.Assets)!); Console.WriteLine("MOVE_RENDER " + path);
        string beforePath = Path.ChangeExtension(path, ".before.png"); File.WriteAllBytes(beforePath, beforePreview); Console.WriteLine("MOVE_BEFORE_RENDER " + beforePath);
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
        check("a repeated production menu click leaves the scene and undo count unchanged", session.UndoCount == undo + 1 && visible.Transform.OffsetX == viewport.Left + 80);
        check("production recovery undo restores the prior scene", session.Undo() && JsonSerializer.Serialize(session.Document) == before);
        typeof(DesktopSession).GetField("_documentBusyValue", Private)!.SetValue(desktop, true);
        try
        {
            typeof(DesktopSession).GetMethod("MoveIntoView", Private)!.Invoke(desktop, null);
            check("document operations prevent concurrent menu recovery", session.UndoCount == undo && JsonSerializer.Serialize(session.Document) == before);
        }
        finally { typeof(DesktopSession).GetField("_documentBusyValue", Private)!.SetValue(desktop, false); }
    }
    private static CanvasElement Box(double x, double y, uint color) => new() { Kind = ElementKind.Rectangle, Width = 80, Height = 60, Transform = Affine2D.Translation(x, y), Style = new() { Fill = color, StrokeWidth = 0 } };
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
}
