using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

internal static class Phase2SaveTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession session)
    {
        var editor = Field<EditorController>(session, "_editor");
        var target = editor.Session;
        target.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 50, Height = 30 });
        Field<UserSettings>(session, "_settings").ToolbarExpanded = true;
        Pump();
        string path = Path.Combine(ApplicationPaths.ProfileDirectory, "save-binding.floatcanvas");
        var binding = typeof(DesktopSession).GetMethod("FileState", Private)!.Invoke(session, [target])!;
        binding.GetType().GetField("Path")!.SetValue(binding, path);
        var indicator = Field<BoundFileIndicator>(session, "_boundFileIndicator");
        indicator.Refresh(null, false, true);
        var saved = (Task<bool>)typeof(DesktopSession).GetMethod("SaveSessionAsync", Private)!.Invoke(session, [target, false])!;
        while (!saved.IsCompleted) Pump();
        check("successful save immediately refreshes the bound-file bubble", saved.GetAwaiter().GetResult() &&
            indicator.Visibility == Visibility.Visible && Equals(indicator.ToolTip, path));
        var readback = new ProjectStore().LoadAsync(path).GetAwaiter().GetResult();
        check("bound-file indication follows a completed readable project write", readback.Document.Elements.Single().Id == target.Document.Elements.Single().Id);

        var menu = indicator.ContextMenu;
        var revert = (MenuItem)menu.Items[0];
        check("bound-file menu orders revert, locate, separator and detach", menu.Items.Count == 4 &&
            Equals(revert.Header, L.Get("Project.Revert")) && Equals(((MenuItem)menu.Items[1]).Header, L.Get("Project.Locate")) &&
            menu.Items[2] is Separator && Equals(((MenuItem)menu.Items[3]).Header, L.Get("Project.Detach")));
        RefreshMenu();
        check("revert is disabled for a saved project", !revert.IsEnabled);

        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Red);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        string asset = target.Assets.Add(encoded.ToArray());
        target.Execute("unsaved content and canvas settings", document =>
        {
            document.Elements[0].Width = 90;
            document.Elements.Add(new CanvasElement { Kind = ElementKind.Image, AssetId = asset, Width = 20, Height = 20 });
            document.LayerOpacity = .42;
        });
        target.SelectAll();
        var before = target.Document.Clone();
        int undoCount = target.UndoCount;
        RefreshMenu();
        check("revert is enabled for unsaved edits", revert.IsEnabled);
        typeof(DesktopSession).GetField("_documentBusyValue", Private)!.SetValue(session, true);
        RefreshMenu();
        check("revert is disabled during a document operation", !revert.IsEnabled);
        typeof(DesktopSession).GetField("_documentBusyValue", Private)!.SetValue(session, false);
        RefreshMenu();
        revert.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        while ((bool)typeof(DesktopSession).GetField("_documentBusyValue", Private)!.GetValue(session)!) Pump();
        Pump(); RefreshMenu();
        check("revert reloads the complete saved document without changing its binding", SameContent(target.Document, readback.Document) &&
            Equals(binding.GetType().GetField("Path")!.GetValue(binding), path));
        check("revert is one undo entry and clears the unsaved badge", target.UndoCount == undoCount + 1 &&
            target.UndoLabel == L.Get("Project.Revert") && !revert.IsEnabled &&
            System.Windows.Automation.AutomationProperties.GetItemStatus(indicator) == "");
        check("revert retains assets needed to undo discarded image edits", target.Assets.TryGet(asset, out _));
        Shortcut(0x5A);
        check("Ctrl+Z restores the pre-revert document and selection", SameContent(target.Document, before) && target.Selection.Count == 2);
        typeof(DesktopSession).GetMethod("RefreshBoundFileIndicator", Private)!.Invoke(session, null);
        RefreshMenu();
        check("undoing revert marks the project unsaved and enables revert again", revert.IsEnabled &&
            System.Windows.Automation.AutomationProperties.GetItemStatus(indicator) == L.Get("Project.Unsaved"));
        Shortcut(0x59);
        check("Ctrl+Y reapplies the saved document", SameContent(target.Document, readback.Document));
        RefreshMenu();
        check("redoing revert returns to saved state", !revert.IsEnabled);
        var disk = new ProjectStore().LoadAsync(path).GetAwaiter().GetResult();
        check("revert, undo and redo never write the associated project", SameContent(disk.Document, readback.Document));
        Shortcut(0x5A); Shortcut(0x5A); RefreshMenu();
        check("undoing past the revert to the original save point also disables revert", SameContent(target.Document, readback.Document) && !revert.IsEnabled);
        Shortcut(0x59);
        saved = (Task<bool>)typeof(DesktopSession).GetMethod("SaveSessionAsync", Private)!.Invoke(session, [target, false])!;
        while (!saved.IsCompleted) Pump();
        disk = new ProjectStore().LoadAsync(path).GetAwaiter().GetResult();
        check("the restored unsaved state can still be saved normally with its image asset", saved.GetAwaiter().GetResult() &&
            SameContent(disk.Document, before) && disk.Assets.TryGet(asset, out _));
        RefreshMenu();
        check("a subsequent successful save disables revert", !revert.IsEnabled);

        void RefreshMenu() => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        void Shortcut(int key)
        {
            var original = new byte[256];
            if (!GetKeyboardState(original)) throw new InvalidOperationException("Could not capture test keyboard state.");
            var control = new byte[256]; control[0x11] = control[0xA2] = 0x80;
            try
            {
                if (!SetKeyboardState(control)) throw new InvalidOperationException("Could not set test keyboard state.");
                typeof(DesktopSession).GetMethod("OnKey", Private)!.Invoke(session, [key]);
            }
            finally { SetKeyboardState(original); }
            Pump();
        }
    }
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    private static bool SameContent(SceneDocument a, SceneDocument b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
