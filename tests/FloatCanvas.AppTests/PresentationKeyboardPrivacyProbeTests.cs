using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using FloatCanvas.App;

internal static class PresentationKeyboardPrivacyProbeTests
{
    private sealed record Result(long Id, nint Window, bool? Password, int ThreadId, ApartmentState Apartment);
    internal static void Run(Action<string, bool> check)
    {
        Lazy(check); Values(check); Unknown(check); Coalescing(check); HungDisposal(check); CallbackFailure(check);
    }

    private static Result Take(BlockingCollection<Result> results)
    {
        if (!results.TryTake(out var result, TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Privacy probe fake query did not complete.");
        return result;
    }
    private static Action<long, nint, bool?> Capture(BlockingCollection<Result> results) =>
        (id, window, password) => results.Add(new(id, window, password, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));

    private static void Lazy(Action<string, bool> check)
    {
        int calls = 0; using var probe = new PresentationKeyboardPrivacyProbe((_, _, _) => calls++, () => { calls++; return false; }, () => 1);
        check("privacy probe construction does not query accessibility or start a completed worker", calls == 0 && !probe.Completed.IsCompleted);
        probe.Dispose(); probe.Request(1, 1);
        check("disposing an unused privacy probe completes without starting a worker", calls == 0 && probe.Completed.IsCompletedSuccessfully);
    }

    private static void Values(Action<string, bool> check)
    {
        using var results = new BlockingCollection<Result>(); var values = new Queue<bool?>([true, false, null]); int caller = Environment.CurrentManagedThreadId;
        using var probe = new PresentationKeyboardPrivacyProbe(Capture(results), () => values.Dequeue(), () => 7);
        int? worker = null;
        for (int index = 0; index < 3; index++)
        {
            probe.Request(10 + index, 7); var result = Take(results); worker ??= result.ThreadId;
            check("privacy probe preserves true false and unknown result " + index, result.Id == 10 + index && result.Window == 7 && result.Password == new bool?[] { true, false, null }[index]);
            check("privacy probe completes on one non-UI MTA worker " + index, result.ThreadId != caller && result.ThreadId == worker && result.Apartment == ApartmentState.MTA);
        }
        probe.Dispose(); check("idle privacy worker exits after disposal", probe.Completed.Wait(TimeSpan.FromSeconds(5)));
    }

    private static void Unknown(Action<string, bool> check)
    {
        using var results = new BlockingCollection<Result>(); int queryCalls = 0; nint current = 2;
        using var probe = new PresentationKeyboardPrivacyProbe(Capture(results), () => { queryCalls++; if (queryCalls == 1) throw new InvalidOperationException("Fake unavailable provider"); return false; }, () => current);
        probe.Request(1, 1); var mismatch = Take(results);
        check("a foreground mismatch avoids querying another application's focused element", mismatch.Password is null && queryCalls == 0);
        probe.Request(2, 2); check("an accessibility failure remains unknown", Take(results).Password is null);
        probe.Request(3, 2); check("a failed query does not poison the single privacy worker", Take(results).Password == false && queryCalls == 2);
        probe.Request(4, 0); check("a missing expected foreground is unknown without a provider query", Take(results).Password is null && queryCalls == 2);
        probe.Dispose(); check("privacy failure worker remains disposable", probe.Completed.Wait(TimeSpan.FromSeconds(5)));

        using var changedResults = new BlockingCollection<Result>(); current = 11;
        using var changed = new PresentationKeyboardPrivacyProbe(Capture(changedResults), () => { current = 12; return false; }, () => current);
        changed.Request(5, 11); var switched = Take(changedResults);
        check("a foreground switch during a query discards its known result", switched.Id == 5 && switched.Window == 11 && switched.Password is null);
        changed.Dispose(); check("foreground-switch worker exits", changed.Completed.Wait(TimeSpan.FromSeconds(5)));

        using var foregroundResults = new BlockingCollection<Result>();
        using var unavailable = new PresentationKeyboardPrivacyProbe(Capture(foregroundResults), () => true, () => throw new InvalidOperationException("Fake foreground failure"));
        unavailable.Request(6, 12); check("foreground read failure remains unknown", Take(foregroundResults).Password is null);
        unavailable.Dispose(); check("foreground-failure worker exits", unavailable.Completed.Wait(TimeSpan.FromSeconds(5)));
    }

    private static void Coalescing(Action<string, bool> check)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var results = new BlockingCollection<Result>();
        int queryCalls = 0; var threads = new ConcurrentDictionary<int, bool>(); nint current = 1;
        using var probe = new PresentationKeyboardPrivacyProbe(Capture(results), () =>
        {
            threads.TryAdd(Environment.CurrentManagedThreadId, true);
            if (Interlocked.Increment(ref queryCalls) == 1) { entered.Set(); release.Wait(); }
            return true;
        }, () => current);
        try
        {
            probe.Request(1, 1); check("first privacy request starts exactly one in-flight fake query", entered.Wait(TimeSpan.FromSeconds(5)) && queryCalls == 1);
            var time = Stopwatch.StartNew();
            for (int id = 2; id <= 1001; id++) probe.Request(id, 2);
            time.Stop();
            check("one thousand privacy requests do not wait for the in-flight query", time.Elapsed < TimeSpan.FromSeconds(1) && queryCalls == 1 && threads.Count == 1);
            current = 2; release.Set(); var first = Take(results); var last = Take(results);
            check("privacy pending work is coalesced to the newest request and foreground", first.Id == 1 && first.Password is null && last.Id == 1001 && last.Window == 2 && last.Password == true && queryCalls == 2 && threads.Count == 1);
            probe.Dispose(); check("coalesced privacy worker exits after the latest request", probe.Completed.Wait(TimeSpan.FromSeconds(5)) && results.Count == 0);
            Console.WriteLine($"PRIVACY PROBE 1000 coalesced Request ms={time.Elapsed.TotalMilliseconds:F3}; queries={queryCalls}; workerThreads={threads.Count}");
        }
        finally { release.Set(); probe.Dispose(); probe.Completed.Wait(TimeSpan.FromSeconds(5)); }
    }

    private static void HungDisposal(Action<string, bool> check)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int calls = 0, callbacks = 0;
        using var probe = new PresentationKeyboardPrivacyProbe((_, _, _) => Interlocked.Increment(ref callbacks), () =>
        { Interlocked.Increment(ref calls); entered.Set(); release.Wait(); return false; }, () => 1);
        try
        {
            probe.Request(1, 1); check("fake stalled provider enters the single privacy worker", entered.Wait(TimeSpan.FromSeconds(5)));
            for (int id = 2; id <= 1001; id++) probe.Request(id, 1);
            var time = Stopwatch.StartNew(); probe.Dispose(); time.Stop();
            check("privacy disposal closes the gate without joining a stalled query", time.Elapsed < TimeSpan.FromSeconds(1) && !probe.Completed.IsCompleted && calls == 1 && callbacks == 0);
            probe.Request(2000, 1); release.Set();
            check("late privacy results and pending work are discarded after disposal", probe.Completed.Wait(TimeSpan.FromSeconds(5)) && calls == 1 && callbacks == 0);
            Console.WriteLine($"PRIVACY PROBE stalled Dispose ms={time.Elapsed.TotalMilliseconds:F3}; queries={calls}; callbacks={callbacks}");
        }
        finally { release.Set(); probe.Dispose(); probe.Completed.Wait(TimeSpan.FromSeconds(5)); }
    }

    private static void CallbackFailure(Action<string, bool> check)
    {
        using var entered = new ManualResetEventSlim(); using var results = new BlockingCollection<Result>(); int callbacks = 0;
        using var probe = new PresentationKeyboardPrivacyProbe((id, window, password) =>
        {
            if (Interlocked.Increment(ref callbacks) == 1) { entered.Set(); throw new InvalidOperationException("Fake callback failure"); }
            Capture(results)(id, window, password);
        }, () => false, () => 1);
        probe.Request(1, 1); check("fake privacy callback can fail without escaping to the process", entered.Wait(TimeSpan.FromSeconds(5)));
        probe.Request(2, 1); check("the same privacy worker handles the request after a callback failure", Take(results).Id == 2);
        probe.Dispose(); check("callback-failure privacy worker exits", probe.Completed.Wait(TimeSpan.FromSeconds(5)));
    }
}
