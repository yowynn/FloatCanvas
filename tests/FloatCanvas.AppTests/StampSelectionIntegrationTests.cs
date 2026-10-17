using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>
/// Deterministic integration regression for changing from a shortcut stamp to an unpinned
/// inventory stamp while its asynchronous package load is pending. Own hidden native host,
/// private temporary profile, no hotkeys, activation, editing, or injected desktop input.
/// </summary>
internal static class StampSelectionIntegrationTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run stamp selection integration in a fresh process.");
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvasStampSelection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile); ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        DesktopSession? session = null; OverlayHost? host = null; SemaphoreSlim? gate = null; bool gateHeld = false;
        nint[] handles = []; nint foreground = GetForegroundWindow();
        try
        {
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), PrivateInstance, null, [OverlayMonitorScope.PrimaryScreen, false], null)!;
            handles = host.Diagnostics.SelectMany(pair => new[] { pair.DisplayHandle, pair.InputHandle }).Append(host.ControlHandle).ToArray();
            session = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            var editor = Field<EditorController>(session, "_editor");
            var library = Field<StampLibrary>(session, "_stampLibrary");
            var saved = Task.Run(async () =>
            {
                await library.LoadAsync();
                var source = new EditorSession(new SceneDocument { Elements = [new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 30 }] });
                source.SelectAll();
                var a = await library.SaveSelectionAsync(source, "Shortcut A");
                var b = await library.SaveSelectionAsync(source, "Unpinned B");
                int slot = library.Pins.ToList().FindIndex(id => id == b.Id);
                if (slot >= 0) await library.PinAsync(slot, null);
                return (a, b, loaded: await library.LoadStampAsync(a.Id));
            }).GetAwaiter().GetResult();
            Pump();
            check("stamp race fixture has an existing pinned A and an unpinned B", library.Pins.Contains(saved.a.Id) && !library.Pins.Contains(saved.b.Id));
            Set("_selectedStampId", (Guid?)saved.a.Id); Set("_selectedStamp", saved.loaded);
            Set("_selectedStampRevision", saved.a.Revision); Set("_selectedStampFromSlot", true);
            editor.Tool = EditorTool.Stamp; Invoke("RefreshToolbarState");
            editor.Tool = EditorTool.Freehand; Pump();
            gate = Field<SemaphoreSlim>(library, "gate"); gate.Wait(); gateHeld = true;
            var select = typeof(DesktopSession).GetMethod("SelectStamp", PrivateInstance, null, [typeof(Guid), typeof(bool), typeof(bool)], null)!;
            select.Invoke(session, [saved.b.Id, false, false]);
            check("real SelectStamp is suspended inside the package gate", Field<bool>(session, "_stampSelectionPending") && Field<object?>(session, "_selectedStamp") is null);
            check("pending inventory request already owns its non-slot source flags", !Field<bool>(session, "_selectedStampFromSlot") && !Field<bool>(session, "_selectedStampUsesSource"));
            Invoke("RefreshToolbarState"); Pump();
            check("validation and queued render keep unpinned B selected while IO is pending", editor.Tool == EditorTool.Stamp &&
                Field<Guid?>(session, "_selectedStampId") == saved.b.Id && Field<bool>(session, "_stampSelectionPending"));
            check("pending request does not expose a canvas input surface or activate a window", !host.IsEditing && !host.IsVisible &&
                host.Diagnostics.All(pair => !pair.InputVisible && !pair.DisplayVisible) && app.Windows.Cast<Window>().All(window => !window.IsVisible) && GetForegroundWindow() == foreground);

            // A genuine tool change invalidates the request before allowing the package read to
            // finish. This exercises the real guard without taking an editing lease or foreground.
            editor.Tool = EditorTool.Select;
            gate.Release(); gateHeld = false;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Field<bool>(session, "_stampSelectionPending") && DateTime.UtcNow < deadline)
            {
                Pump(); Thread.Sleep(5);
            }
            Pump();
            check("the actual asynchronous load resumes and completes after releasing its gate", !Field<bool>(session, "_stampSelectionPending"));
            check("a later tool choice rejects the old completed stamp without entering editing", editor.Tool == EditorTool.Select &&
                Field<object?>(session, "_selectedStamp") is null && !host.IsEditing && host.Diagnostics.All(pair => !pair.InputVisible));
        }
        finally
        {
            if (gateHeld) gate!.Release();
            session?.Dispose(); if (session is null) host?.Dispose();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            app.Shutdown(); Directory.Delete(profile, recursive: true);
        }
        check("stamp race fixture destroys every own host HWND", handles.All(handle => !IsWindow(handle)));

        void Set(string name, object? value) => typeof(DesktopSession).GetField(name, PrivateInstance)!.SetValue(session, value);
        void Invoke(string name) => typeof(DesktopSession).GetMethod(name, PrivateInstance)!.Invoke(session, null);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
}
