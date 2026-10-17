using FloatCanvas.Core;

namespace FloatCanvas.App;

internal sealed record StampPlacementRequest(long Token, Guid StampId, Guid StampRevision, bool UseSource, PointD Position,
    EditorSession Target, long SceneRevision, long TransactionId, EditorTool Tool, long ModeVersion);

/// <summary>Async stamp reads may finish only in the exact editor context that requested them.</summary>
internal sealed class StampPlacementRequestGuard : IDisposable
{
    private readonly EditorController editor;
    private EditorSession observedSession;
    private EditorTool observedTool;
    private long modeVersion;

    public StampPlacementRequestGuard(EditorController editor)
    {
        this.editor = editor; observedSession = editor.Session; observedTool = editor.Tool;
        editor.Changed += ObserveMode;
    }
    public StampPlacementRequest Capture(long token, Guid id, Guid revision, bool source, PointD position) => new(token, id, revision, source, position,
        editor.Session, editor.Session.SceneRevision, editor.Session.TransactionId, editor.Tool, modeVersion);

    public bool CanContinue(StampPlacementRequest request, long currentToken, Guid currentStampRevision, bool available) =>
        available && request.Token == currentToken && request.StampRevision == currentStampRevision && request.ModeVersion == modeVersion &&
        request.Tool == editor.Tool && ReferenceEquals(request.Target, editor.Session) && request.SceneRevision == editor.Session.SceneRevision &&
        request.TransactionId == editor.Session.TransactionId && !editor.Session.IsTransactionActive;

    public bool CanPlace(StampPlacementRequest request, long currentToken, Guid? selectedStamp, Guid currentStampRevision, bool editing, bool available) =>
        editing && editor.Tool == EditorTool.Stamp && request.StampId == selectedStamp && CanContinue(request, currentToken, currentStampRevision, available);

    public void Invalidate() => modeVersion++;
    private void ObserveMode()
    {
        if (ReferenceEquals(observedSession, editor.Session) && observedTool == editor.Tool) return;
        observedSession = editor.Session; observedTool = editor.Tool; Invalidate();
    }
    public void Dispose() => editor.Changed -= ObserveMode;
}
