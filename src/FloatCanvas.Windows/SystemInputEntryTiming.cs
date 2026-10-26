namespace FloatCanvas.Windows;

/// <summary>Native entry owns the key-release deadline; ACK gets its own budget once that wait ends.</summary>
internal sealed class SystemInputEntryTiming
{
    private long? acknowledgementDeadline;
    internal void Begin() => acknowledgementDeadline=null;
    internal bool TimedOut(bool waitingForEntryKeys,long now)
    {
        if(waitingForEntryKeys)return false;
        acknowledgementDeadline??=now+2000;
        return now>=acknowledgementDeadline;
    }
}
