using System.IO;
using System.Windows.Threading;
using FloatCanvas.App;

internal static class DispatcherResponsivenessTests
{
    internal static void Run(Action<string, bool> check)
    {
        long clock = 0; var queue = new Queue<Action>(); var samples = new List<DispatcherDelaySample>();
        using var monitor = new DispatcherResponsivenessMonitor(action => { queue.Enqueue(action); return true; }, () => clock, samples.Add);
        monitor.Check();
        for (int index = 0; index < 100; index++) monitor.Check();
        check("dispatcher monitor bounds outstanding queue work to one probe", queue.Count == 1 && samples.Count == 0);
        clock = 499; monitor.Check();
        check("dispatcher monitor has no action below the diagnostic threshold", samples.Count == 0);
        clock = 700; monitor.Check(); monitor.Check();
        check("blocked dispatcher is observable before its callback can run", samples is [{ State: "Delayed", DelayMilliseconds: 700, ObservedWhilePending: true }]);
        clock = 1200; queue.Dequeue()();
        check("dispatcher recovery records actual queue time on the same probe", samples.Count == 2 && samples[1] is { State: "Recovered", DelayMilliseconds: 1200, ObservedWhilePending: true } && samples[0].Probe == samples[1].Probe);
        monitor.Check(); var late = queue.Dequeue(); monitor.Dispose(); clock = 3000; late(); monitor.Check();
        check("disposed monitor ignores queued completions and schedules no new work", samples.Count == 2 && queue.Count == 0);
        int tries = 0;
        using var shutdown = new DispatcherResponsivenessMonitor(_ => { tries++; return false; }, () => clock, _ => throw new InvalidOperationException());
        shutdown.Check(); shutdown.Check();
        check("rejected dispatcher enqueue does not leave a stuck pending probe", tries == 2);
        queue.Clear(); samples.Clear(); clock = 0;
        using var recoveryOnly = new DispatcherResponsivenessMonitor(action => { queue.Enqueue(action); return true; }, () => clock, samples.Add);
        recoveryOnly.Check(); clock = 800; queue.Dequeue()();
        check("delayed completion remains observable if the sampling worker itself was delayed", samples is [{ State: "Recovered", DelayMilliseconds: 800, ObservedWhilePending: false }]);
    }

    internal static void RunNative(Action<string, bool> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-dispatcher-monitor", Guid.NewGuid().ToString("N"));
        var diagnostics = new ApplicationDiagnostics(directory);
        using var monitor = new DispatcherResponsivenessMonitor(Dispatcher.CurrentDispatcher, diagnostics);
        try
        {
            monitor.Check();
            // Deliberately stop this isolated test dispatcher, not the user's application.
            Thread.Sleep(650);
            bool delayed = SpinWait.SpinUntil(() => Read(diagnostics.LogPath).Contains("Delayed"), TimeSpan.FromSeconds(3));
            check("pool observer records a real dispatcher stall while its UI callback is still blocked", delayed);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            check("real dispatcher recovery is logged after queue processing resumes", SpinWait.SpinUntil(() => Read(diagnostics.LogPath).Contains("Recovered"), TimeSpan.FromSeconds(3)));
        }
        finally { monitor.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static string Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file); return reader.ReadToEnd();
        }
        catch (IOException) { return ""; }
    }
}
