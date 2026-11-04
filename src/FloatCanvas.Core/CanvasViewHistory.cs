namespace FloatCanvas.Core;

/// <summary>Local viewing state attached to one authored history entry, never serialized into scene content.</summary>
public sealed record CanvasViewHistoryState(string CanvasKey, PointD ObservationCenter, double Zoom, PointD PlaneOffset = default);
public sealed record CanvasViewHistoryChange(CanvasViewHistoryState Before, CanvasViewHistoryState After);

public sealed partial class EditorSession
{
    public event Action<CanvasViewHistoryState>? CanvasViewRestored;

    /// <summary>Commits a prevalidated authored candidate with its local view receipt as one content undo.</summary>
    public void ApplyCanvasView(string label, SceneDocument candidate, CanvasViewHistoryChange viewing)
    {
        RequireWritableScope();
        ArgumentNullException.ThrowIfNull(candidate); ArgumentNullException.ThrowIfNull(viewing);
        if (candidate.ResourceId != Document.ResourceId || candidate.Kind != Document.Kind)
            throw new ArgumentException("A canvas edit must retain its document identity.", nameof(candidate));
        foreach (var state in new[] { viewing.Before, viewing.After })
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(state.CanvasKey);
            _ = CanvasViewTransform.Restore(state.ObservationCenter, state.Zoom, Document.Viewport);
        }
        BeginTransaction(label);
        try { Document = candidate.Clone(); CommitCore(settingsOnly: false, viewing: viewing); }
        catch { Cancel(); throw; }
    }
}
