namespace FloatCanvas.Windows;

/// <summary>Caches supported/unsupported results; transient failures may retry after bounded backoff.</summary>
internal sealed class RetryableCapabilityProbe(Func<bool> probe, Func<long>? clock = null)
{
    private readonly object gate = new();
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private Task<bool>? task;
    private long retryAfter;
    private int failures;

    internal Task<bool> GetAsync()
    {
        lock (gate)
        {
            if (task is null || task.IsFaulted && now() >= retryAfter)
                task = Task.Run(() =>
                {
                    try { return probe(); }
                    catch
                    {
                        lock (gate) retryAfter = now() + Math.Min(30_000, 1_000L << Math.Min(failures++, 5));
                        throw;
                    }
                });
            return task;
        }
    }
}
