using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private static readonly int[] NudgeIntervals = [360, 145, 115, 90, 70, 55, 45];
    private sealed class KeyboardNudge(EditorSession session, CommandChord chord, PointD step, SelectionTransformSnapshot original)
    {
        internal EditorSession Session { get; } = session;
        internal SceneDocument Document { get; } = session.Document;
        internal long TransactionId { get; } = session.TransactionId;
        internal long ScopeVersion { get; } = session.EditScopeVersion;
        internal HashSet<Guid> Selection { get; } = [.. session.Selection];
        internal SelectionTransformSnapshot Original { get; } = original;
        internal CommandChord Chord { get; } = chord;
        internal PointD Step { get; } = step;
        internal int Steps { get; set; }
        internal bool OwnsTransaction => Session.IsTransactionActive && Session.TransactionId == TransactionId &&
            ReferenceEquals(Session.Document, Document) && Session.EditScopeVersion == ScopeVersion;
    }

    private KeyboardNudge? _keyboardNudge;
    private DispatcherTimer? _keyboardNudgeTimer;

    private static PointD SelectionMoveStep(string commandId)
    {
        bool fast = commandId.StartsWith("Move.Fast", StringComparison.Ordinal);
        int direction = int.Parse(commandId[(fast ? 9 : 5)..]);
        int amount = fast ? 10 : 1;
        return new(direction == 0x25 ? -amount : direction == 0x27 ? amount : 0,
            direction == 0x26 ? -amount : direction == 0x28 ? amount : 0);
    }

    private void BeginKeyboardNudge(EditorCommand command, CommandChord chord)
    {
        if (!CanExecuteEditorCommand(command)) return;
        if (_editor.HasPointerGesture || _editor.HasPendingBillboardOperation || _editor.IsBorrowingSelection) _editor.Cancel();
        if (Active.IsTransactionActive || !Active.Selection.Any(id => !Active.IsNodeLocked(id))) return;

        var session = Active;
        var original = session.CaptureSelectionTransform();
        session.BeginTransaction(L.Get("History.TransformElements"));
        _keyboardNudge = new(session, chord, SelectionMoveStep(command.Id), original);
        if (_keyboardNudgeTimer is null)
        {
            _keyboardNudgeTimer = new DispatcherTimer(DispatcherPriority.Background, _app.Dispatcher);
            _keyboardNudgeTimer.Tick += RepeatKeyboardNudge;
        }
        StepKeyboardNudge(_keyboardNudge);
    }

    private void RepeatKeyboardNudge(object? sender, EventArgs args)
    {
        if (_keyboardNudge is not { } nudge) return;
        if (!_host.IsVisible || DocumentOperationBusy ||
            SessionKeyboardRouter.IsModal || OverlayContextMenu.HasOpenMenu || _inlineText is not null ||
            !_host.IsEditingChordHeld(nudge.Chord.Key, nudge.Chord.Modifiers) ||
            !ReferenceEquals(Active, nudge.Session) ||
            !nudge.OwnsTransaction || !nudge.Selection.SetEquals(Active.Selection))
        { FinishKeyboardNudge(); return; }
        StepKeyboardNudge(nudge);
    }

    private void StepKeyboardNudge(KeyboardNudge nudge)
    {
        var offset = nudge.Step * ++nudge.Steps;
        if (!_editor.TryTransformSelectionInCanvas(nudge.Original, Affine2D.Translation(offset.X, offset.Y)))
        {
            FinishKeyboardNudge();
            UpdateStatus(L.Get("Perspective.InputOutsidePlane"));
            return;
        }
        if (!ReferenceEquals(_keyboardNudge, nudge)) return;
        _keyboardNudgeTimer!.Interval = TimeSpan.FromMilliseconds(NudgeIntervals[Math.Min(nudge.Steps - 1, NudgeIntervals.Length - 1)]);
        _keyboardNudgeTimer.Start();
    }

    private void ReleaseEditorKey(int key)
    {
        if (_keyboardNudge?.Chord.Key == key) FinishKeyboardNudge();
        ReleaseBorrowedTool(key);
    }

    private bool FinishKeyboardNudge(bool cancel = false)
    {
        if (_keyboardNudge is not { } nudge) return false;
        _keyboardNudge = null;
        _keyboardNudgeTimer?.Stop();
        if (nudge.OwnsTransaction)
        {
            if (cancel) nudge.Session.Cancel();
            else nudge.Session.Commit();
        }
        return true;
    }
}
