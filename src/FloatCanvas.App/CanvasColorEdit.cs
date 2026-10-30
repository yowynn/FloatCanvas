using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>One continuous canvas color or numeric edit owns one settings transaction.</summary>
internal sealed class CanvasColorEdit(Func<EditorSession> current, Func<bool> canMutate, Action prepare, Action changed)
{
    private EditorSession? session;
    private long transaction;
    internal bool IsActive => session is not null;
    private bool Owns => session is not null && ReferenceEquals(session, current()) && session.IsTransactionActive && session.TransactionId == transaction;
    internal bool Begin()
    {
        if (Owns) return true;
        session = null;
        if (!canMutate()) return false;
        prepare();
        if (current().IsTransactionActive) return false;
        session = current(); session.BeginTransaction(L.Get("Canvas.Title")); transaction = session.TransactionId;
        return true;
    }
    internal bool Preview(Action<CanvasSettings> update)
    {
        if (!Owns || !canMutate()) return false;
        update(session!.Document.Canvas); session.NotifySettingsChanged(); changed(); return true;
    }
    internal void Complete(bool commit)
    {
        bool owns = Owns; var previous = session; session = null;
        if (!owns) return;
        if (commit && canMutate()) previous!.CommitSettings(); else previous!.Cancel();
        changed();
    }
}
