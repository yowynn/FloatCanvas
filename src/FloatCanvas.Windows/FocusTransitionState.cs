namespace FloatCanvas.Windows;

/// <summary>A UI-turn bounded permit, never a reason to suppress input for an external foreground.</summary>
internal sealed class FocusTransitionState
{
    private long transition;
    private long permittedLease;
    private bool editing;
    public long SessionVersion { get; private set; }
    public FocusTransitionToken Pending { get; private set; }

    public void BeginSession()
    {
        SessionVersion++;
        editing = true;
        Pending = default;
    }

    public void EndSession()
    {
        SessionVersion++;
        editing = false;
        Pending = default;
    }

    public FocusTransitionToken Begin(long lease)
    {
        if (!editing) return default;
        permittedLease = lease;
        return Pending = new(SessionVersion, ++transition);
    }

    public bool IsCurrent(FocusTransitionToken token) =>
        editing && token != default && token == Pending && token.SessionVersion == SessionVersion;

    public bool HasPending => IsCurrent(Pending);

    public bool CanResume(long lease, KeyboardLeaseEndReason? reason, bool ownsForeground) =>
        HasPending && ownsForeground && lease == permittedLease && reason == KeyboardLeaseEndReason.ForegroundLost;

    public void ReplacedLease(long lease) => permittedLease = lease;

    public bool Complete(FocusTransitionToken token)
    {
        if (!IsCurrent(token)) return false;
        Pending = default;
        return true;
    }

    public void Cancel() => Pending = default;
}

internal readonly record struct FocusTransitionToken(long SessionVersion, long TransitionVersion);
