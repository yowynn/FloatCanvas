using FloatCanvas.Windows;

internal static class RetryableCapabilityProbeTests
{
    internal static void Run(Action<bool, string> check)
    {
        long clock = 0; int calls = 0;
        var probe = new RetryableCapabilityProbe(() => ++calls == 1 ? throw new IOException("transient device loss") : true, () => clock);
        Task<bool> first = probe.GetAsync();
        try { first.GetAwaiter().GetResult(); } catch (IOException) { }
        check(first.IsFaulted, "Transient capability failure is surfaced");
        check(ReferenceEquals(first, probe.GetAsync()) && calls == 1, "Repeated requests inside backoff do not reprobe");
        clock = 1000;
        check(probe.GetAsync().GetAwaiter().GetResult() && calls == 2, "Capability recovers without a process restart");
        clock = 60000;
        check(probe.GetAsync().Result && calls == 2, "Successful capability stays cached");
        int unsupportedCalls = 0;
        var absent = new RetryableCapabilityProbe(() => { unsupportedCalls++; return false; }, () => clock);
        check(!absent.GetAsync().Result, "Known absent API returns unsupported"); clock += 60000;
        check(!absent.GetAsync().Result && unsupportedCalls == 1, "Known unsupported result never busy-retries");
    }
}
