using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;

/// <summary>Native-publication uncertainty may abort canvas gestures, but never another editor's transaction.</summary>
internal static class PresentationPickingOwnershipTests
{
    internal static void Run(Action<string, bool> check)
    {
        ImmediateDrawingSuspension(check);
        SelectionSuspension(check);
        ExternalTransactionSurvivesCancel(check);
        ReplacedTransactionIsNotClaimed(check);
        QueuedReplacedTransactionIsNotClaimed(check);
    }

    private static void ImmediateDrawingSuspension(Action<string, bool> check)
    {
        var session = new EditorSession(new() { Viewport = new(0, 0, 800, 600) });
        var editor = new EditorController(session) { Tool = EditorTool.Freehand };
        bool suspended = false;
        editor.Changed += () =>
        {
            if (suspended || !session.IsTransactionActive || session.Document.Elements.Count == 0) return;
            suspended = true;
            editor.SuspendPresentedPicking();
        };
        try
        {
            editor.Pointer(Pointer(OverlayPointerKind.Down));
            editor.Pointer(Pointer(OverlayPointerKind.Up));
            check("publication failure reentrant in first pen notification rolls back the immediately owned gesture",
                suspended && !session.IsTransactionActive && session.Document.Elements.Count == 0 && session.UndoCount == 0);
            editor.ResumePresentedPicking();
            editor.Pointer(Pointer(OverlayPointerKind.Down)); editor.Pointer(Pointer(OverlayPointerKind.Up));
            check("resuming after hidden-draft cancellation permits the next pen gesture to finish",
                !session.IsTransactionActive && session.Document.Elements.Count == 1 && session.UndoCount == 1);
        }
        finally { editor.DetachSession(); }
    }

    private static void SelectionSuspension(Action<string, bool> check)
    {
        var item = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 80,
            Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } };
        var session = new EditorSession(new() { Viewport = new(0, 0, 800, 600), Elements = [item] });
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        bool suspended = false;
        editor.Changed += () =>
        {
            // StartSelect publishes the new selection before starting its move transaction.
            if (suspended || session.IsTransactionActive || !session.Selection.Contains(item.Id)) return;
            suspended = true;
            editor.SuspendPresentedPicking();
        };
        try
        {
            editor.Pointer(Pointer(OverlayPointerKind.Down));
            editor.Pointer(Pointer(OverlayPointerKind.Up));
            check("reentrant selection publication cannot leave a transaction started after suspension",
                suspended && !session.IsTransactionActive && session.UndoCount == 0 && session.Document.Elements.Single().Transform == Affine2D.Identity);
        }
        finally { editor.DetachSession(); }
    }

    private static void ExternalTransactionSurvivesCancel(Action<string, bool> check)
    {
        var session = new EditorSession(new() { Viewport = new(0, 0, 800, 600) });
        var editor = new EditorController(session) { Tool = EditorTool.Freehand };
        session.BeginTransaction("new canvas color edit");
        long transaction = session.TransactionId;
        session.Document.Canvas.BackgroundColor = 0xFF123456; session.NotifySettingsChanged();
        try
        {
            editor.SuspendPresentedPicking();
            editor.Pointer(Pointer(OverlayPointerKind.Cancel));
            check("suspension and a late native cancel preserve an unrelated color transaction",
                session.IsTransactionActive && session.TransactionId == transaction && session.Document.Canvas.BackgroundColor == 0xFF123456 && session.UndoCount == 0);
        }
        finally { session.Cancel(); editor.DetachSession(); }
    }

    private static void ReplacedTransactionIsNotClaimed(Action<string, bool> check)
    {
        var session = new EditorSession(new() { Viewport = new(0, 0, 800, 600) });
        var editor = new EditorController(session) { Tool = EditorTool.Freehand };
        bool replaced = false;
        long externalTransaction = -1;
        editor.Changed += () =>
        {
            if (replaced || !session.IsTransactionActive || session.Document.Elements.Count == 0) return;
            replaced = true;
            session.Cancel();
            session.BeginTransaction("new inspector color edit"); externalTransaction = session.TransactionId;
            session.Document.Canvas.BackgroundColor = 0xFF778899; session.NotifySettingsChanged();
            editor.SuspendPresentedPicking();
        };
        try
        {
            editor.Pointer(Pointer(OverlayPointerKind.Down));
            editor.Pointer(Pointer(OverlayPointerKind.Cancel));
            check("a reentrant external replacement is never claimed as the returning pointer call's transaction",
                replaced && session.IsTransactionActive && session.TransactionId == externalTransaction &&
                session.Document.Canvas.BackgroundColor == 0xFF778899 && session.Document.Elements.Count == 0 && session.UndoCount == 0);
        }
        finally { session.Cancel(); editor.DetachSession(); }
    }

    private static OverlayPointerEvent Pointer(OverlayPointerKind kind) => new(kind, new(40, 40), OverlayMouseButton.Left, OverlayModifiers.None);

    private static void QueuedReplacedTransactionIsNotClaimed(Action<string, bool> check)
    {
        var selected = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 80,
            Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } };
        var session = new EditorSession(new() { Viewport = new(0, 0, 800, 600), Elements = [selected] });
        session.SetSelection([selected.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Freehand, BackgroundPickingEnabled = true };
        var worker = (SemaphoreSlim)typeof(EditorController).GetField("pickingWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        bool replaced = false;
        long externalTransaction = -1;
        session.Changed += (_, _) =>
        {
            if (replaced || !session.IsTransactionActive || session.Document.Elements.Count != 2) return;
            replaced = true;
            session.Cancel();
            session.BeginTransaction("new external edit during queued replay"); externalTransaction = session.TransactionId;
            session.Document.Canvas.BackgroundColor = 0xFFAABBCC; session.NotifySettingsChanged();
            editor.SuspendPresentedPicking();
        };
        worker.Wait();
        bool released = false;
        try
        {
            editor.Pointer(Pointer(OverlayPointerKind.Down) with { Modifiers = OverlayModifiers.Control });
            PumpOne();
            check("queued ownership fixture waits for background picking before replay", !editor.PendingPicking.IsCompleted && !replaced && !session.IsTransactionActive);
            worker.Release(); released = true;
            Pump(editor.PendingPicking);
            editor.Pointer(Pointer(OverlayPointerKind.Cancel));
            check("queued replay cannot claim or cancel a transaction replaced from Session.Changed",
                replaced && session.IsTransactionActive && session.TransactionId == externalTransaction &&
                session.Document.Canvas.BackgroundColor == 0xFFAABBCC && session.Document.Elements.Count == 1 &&
                session.Document.Elements[0].Id == selected.Id && session.UndoCount == 0);
            session.CommitSettings();
            editor.ResumePresentedPicking(); editor.Pointer(Pointer(OverlayPointerKind.Cancel));
            check("queued replacement leaves no half gesture after the external edit is committed",
                !session.IsTransactionActive && session.UndoCount == 1 && session.Document.Elements.Count == 1 && session.Document.Canvas.BackgroundColor == 0xFFAABBCC);
        }
        finally
        {
            if (!released) worker.Release();
            session.Cancel(); editor.DetachSession(); Pump(editor.PendingPicking);
        }
    }

    private static void Pump(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10)) { PumpOne(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Queued ownership fixture did not finish.");
        task.GetAwaiter().GetResult();
    }
    private static void PumpOne()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
