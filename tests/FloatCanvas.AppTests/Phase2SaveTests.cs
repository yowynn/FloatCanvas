using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;

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
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
