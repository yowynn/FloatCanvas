using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class ClipboardBusyTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-ClipboardBusy", Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        try
        {
            using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), PrivateInstance, null, [OverlayMonitorScope.PrimaryScreen, false], null)!;
            using var session = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>("_tray").Visible = false;
            var editor = Field<EditorController>("_editor");
            var toolbar = Field<Window>("_toolbar");
            editor.Session.Add(new() { Kind = ElementKind.Rectangle, Width = 60, Height = 40 });
            editor.Session.SelectAll();
            var existing = app.Windows.Cast<Window>().ToHashSet();
            var selected = editor.Session.Selection.ToArray();
            int undoCount = editor.Session.UndoCount;
            bool openedDialog = false;
            var modalWatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
            modalWatch.Tick += (_, _) =>
            {
                foreach (var modal in app.Windows.Cast<Window>().Where(window => !existing.Contains(window) && window.IsVisible).ToArray())
                {
                    openedDialog = true;
                    modal.Close();
                }
            };

            using var releaseClipboard = new ManualResetEventSlim();
            using var clipboardOpened = new ManualResetEventSlim();
            bool locked = false;
            var holder = new Thread(() =>
            {
                locked = OpenClipboard(0);
                clipboardOpened.Set();
                if (!locked) return;
                try { releaseClipboard.Wait(); }
                finally { CloseClipboard(); }
            }) { IsBackground = true };
            holder.Start();
            try
            {
                // Abort the fixture before releasing a stuck operation into the shared clipboard.
                using var watchdog = new Timer(_ => Environment.Exit(1), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
                check("clipboard contention fixture opens the real OS clipboard without changing its contents",
                    clipboardOpened.Wait(TimeSpan.FromSeconds(1)) && locked);
                modalWatch.Start();
                var copyMethod = typeof(DesktopSession).GetMethod("CopySelectionAsync", PrivateInstance)!;
                var operation = (Task)copyMethod.Invoke(session, [true])!;
                var frame = new DispatcherFrame();
                _ = operation.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                operation.GetAwaiter().GetResult();
            }
            finally
            {
                releaseClipboard.Set();
                holder.Join();
                modalWatch.Stop();
            }

            check("clipboard contention reports in place without opening an input modal", !openedDialog &&
                !SessionWindowCoordinator.HasModalInput && Field<TextBlock>("_status").Text == L.Get("Clipboard.Busy"));
            check("failed cut immediately releases the shared interaction lock", toolbar.IsEnabled && host.CanBeginCanvasGesture?.Invoke() == true);
            check("failed cut keeps the original selection, layers and undo history",
                editor.Session.Selection.SetEquals(selected) && editor.Session.Document.Elements.Count == 1 && editor.Session.UndoCount == undoCount);

            T Field<T>(string name) => (T)typeof(DesktopSession).GetField(name, PrivateInstance)!.GetValue(session)!;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            app.Shutdown();
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(nint window);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();
}
