using System.Runtime.ExceptionServices;
using FloatCanvas.Windows;
using SkiaSharp;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.WindowsTests;

internal static class DirtyOutputRenderingTests
{
    internal static void Performance()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                nint foreground = GetForegroundWindow();
                int left = GetSystemMetrics(76) + GetSystemMetrics(78) + 4096;
                var monitors = new[]
                {
                    new MonitorInfo("4k-a", new(left, 0, 3840, 2160), new(left, 0, 3840, 2160), 96, 96, true),
                    new MonitorInfo("4k-b", new(left + 3840, 0, 3840, 2160), new(left + 3840, 0, 3840, 2160), 96, 96, false)
                };
                using var host = new OverlayHost(OverlayMonitorScope.AllScreens, () => monitors, false);
                host.Draw += surface => { using var paint = new SKPaint { Color = SKColors.Red }; surface.Canvas.DrawRect(left + 20, 20, 100, 100, paint); };
                host.SetVisible(true);
                var timings = new List<OverlayFrameTiming>(); host.FrameSubmitted += timings.Add;
                foreach (bool gpu in new[] { false, true })
                {
                    host.ApplyRenderPreference(gpu); host.SetGpuWorkload(true); host.Render();
                    foreach (bool dirty in new[] { false, true })
                    {
                        timings.Clear();
                        for (int i = 0; i < 20; i++) host.Render(dirty ? [new(left + 20, 20, 100, 100)] : null);
                        double Percentile(IEnumerable<double> values, double fraction) { var sorted = values.Order().ToArray(); return sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1]; }
                        var frames = timings.Select(timing => System.Diagnostics.Stopwatch.GetElapsedTime(timing.StartedTimestamp, timing.CompletedTimestamp).TotalMilliseconds);
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Backend = host.LastCompletedRenderBackend.ToString(), RequestedGpu = gpu, DirtyOnly = dirty,
                            Frames = timings.Count, OutputA = timings.SelectMany(t => t.Outputs).Count(t => t.DeviceName == "4k-a"),
                            OutputB = timings.SelectMany(t => t.Outputs).Count(t => t.DeviceName == "4k-b"),
                            P50Milliseconds = Percentile(frames, .5), P95Milliseconds = Percentile(frames, .95),
                            PrivateBytes = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64,
                            OwnOffscreenWindows = true, ForegroundUnchanged = foreground == GetForegroundWindow(), host.LastGpuFailure
                        }));
                    }
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    internal static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                nint foreground = GetForegroundWindow();
                int left = GetSystemMetrics(76) + GetSystemMetrics(78) + 4096;
                var monitors = new[]
                {
                    new MonitorInfo("dirty-a", new(left, 0, 128, 128), new(left, 0, 128, 128), 96, 96, true),
                    new MonitorInfo("dirty-b", new(left + 128, 0, 128, 128), new(left + 128, 0, 128, 128), 96, 96, false)
                };
                using var host = new OverlayHost(OverlayMonitorScope.AllScreens, () => monitors, false);
                host.ApplyRenderPreference(false);
                host.Draw += surface => surface.Canvas.Clear(SKColors.Red);
                host.SetVisible(true);
                List<string> submitted = [];
                host.FrameSubmitted += timing => submitted.AddRange(timing.Outputs.Select(output => output.DeviceName));
                host.Render(); check(submitted.Count == 2, "Initial frame presents both outputs"); submitted.Clear();
                host.Render([new(left + 10, 10, 100, 100)]);
                check(submitted.SequenceEqual(["dirty-a"]), "100x100 local animation skips the unchanged output"); submitted.Clear();
                host.Render([new(left + 120, 10, 20, 20)]);
                check(submitted.Count == 2, "Boundary-crossing damage redraws both intersecting outputs"); submitted.Clear();
                host.Render([]); check(submitted.Count == 0, "No damage causes no readback or presentation");
                host.Render(); check(submitted.Count == 2, "Full edit redraw clears old and new positions on both outputs");
                check(GetForegroundWindow() == foreground && !host.IsEditing, "Output invalidation neither activates nor acquires input");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
