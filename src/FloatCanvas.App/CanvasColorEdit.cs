using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>One continuous canvas color or numeric edit owns one settings transaction.</summary>
internal sealed class CanvasColorEdit(Func<EditorSession> current, Func<bool> canMutate, Action prepare, Action changed,
    Func<CanvasSettingsTarget>? captureTarget = null)
{
    private EditorSession? session;
    private long transaction;
    private CanvasSettingsTarget? target;
    internal bool IsActive => session is not null;
    private bool Owns => session is not null && ReferenceEquals(session, current()) && target?.IsCurrent == true && session.IsTransactionActive && session.TransactionId == transaction;
    internal bool Begin()
    {
        if (Owns) return true;
        if (session is not null) Complete(false);
        if (!canMutate()) return false;
        prepare();
        if (current().IsTransactionActive) return false;
        session = current(); target = captureTarget?.Invoke() ?? new(session);
        if (!target.IsCurrent || !target.IsWritable) { session = null; target = null; return false; }
        session.BeginTransaction(L.Get("Canvas.Title")); transaction = session.TransactionId;
        return true;
    }
    internal bool Preview(Action<CanvasSettings> update)
    {
        if (!Owns || !canMutate()) return false;
        if (!target!.Change(update)) return false; changed(); return true;
    }
    internal void Complete(bool commit)
    {
        bool owns = Owns; var previous = session;
        bool ownsTransaction = previous?.IsTransactionActive == true && previous.TransactionId == transaction;
        session = null; target = null;
        if (!ownsTransaction) return;
        if (owns && commit && canMutate()) previous!.CommitSettings(); else previous!.Cancel();
        changed();
    }
}
