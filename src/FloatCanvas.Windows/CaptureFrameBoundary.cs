namespace FloatCanvas.Windows;

/// <summary>How a request establishes that the acquired desktop is ready for its window policy.</summary>
public enum CaptureFrameRequirement
{
    /// <summary>After hiding or changing visuals, wait for a desktop image presented after that change.</summary>
    RequirePresentAfterBoundary,
    /// <summary>Exclusion is already established; acquire the current desktop from a newly created duplication.</summary>
    CurrentDesktopFromNewDuplication
}

/// <summary>
/// QPC boundary established by the UI after its complete capture window policy is applied. A static
/// desktop's last content change may predate this request; that is distinct from reusing an old capture.
/// The current-desktop policy requires a new duplication created after this boundary, never cached pixels.
/// </summary>
public readonly record struct CaptureFrameBoundary(long Timestamp, CaptureFrameRequirement Requirement)
{
    internal void Validate(long now)
    {
        if (!Enum.IsDefined(Requirement)) throw new ArgumentOutOfRangeException(nameof(Requirement));
        if (Timestamp < 0 || Timestamp > now ||
            (Requirement == CaptureFrameRequirement.CurrentDesktopFromNewDuplication && Timestamp == 0))
            throw new ArgumentOutOfRangeException(nameof(Timestamp));
    }

    internal bool Accepts(long lastPresentTimestamp, long duplicationStartedTimestamp, long acquiredTimestamp)
    {
        // All modes use this request's newly created COM session. A resource whose timestamp is
        // zero can be a pointer-only update and cannot establish a valid desktop image here.
        if (duplicationStartedTimestamp < Timestamp || acquiredTimestamp < duplicationStartedTimestamp ||
            lastPresentTimestamp <= 0 || lastPresentTimestamp > acquiredTimestamp) return false;
        return Requirement switch
        {
            CaptureFrameRequirement.RequirePresentAfterBoundary => lastPresentTimestamp >= Timestamp,
            CaptureFrameRequirement.CurrentDesktopFromNewDuplication => Timestamp > 0,
            _ => false
        };
    }
}
