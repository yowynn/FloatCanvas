using FloatCanvas.Windows;

namespace FloatCanvas.InputTests;

internal static class PresentationMeasurements
{
    internal const int SampleCapacity = 4096;
    private const int InputSequenceLimit = SampleCapacity * 8;
    // Native entry evidence retained only the low 32 bits of the original marker.
    // Keep the complete prefix and sequence inside that transport width; decoding
    // still compares the full ULONG_PTR value and never masks an unknown prefix.
    private const uint InputMarker = 0x46430000;
    internal static nuint EncodeSequence(int sequence) => sequence is > 0 and < InputSequenceLimit
        ? (nuint)(InputMarker | (uint)sequence) : throw new ArgumentOutOfRangeException(nameof(sequence));
    internal static int DecodeSequence(nuint value) => (ulong)value > InputMarker &&
        (ulong)value < (ulong)InputMarker + InputSequenceLimit ? (int)((ulong)value - InputMarker) : 0;

    internal static OverlayPoint InitialPointerBaseline(OverlayPoint actualCursor, PixelBounds guardedClient)
    {
        if (!double.IsFinite(actualCursor.X) || !double.IsFinite(actualCursor.Y) || !guardedClient.Contains(actualCursor))
            throw new InvalidOperationException("The actual initial cursor is outside the guarded owned client.");
        return actualCursor;
    }

    internal static MonitorInfo PrimaryTile(MonitorInfo monitor)
    {
        if (!monitor.IsPrimary || monitor.Bounds.Width < 1920 || monitor.Bounds.Height < 1080)
            throw new InvalidOperationException("The probe needs a real primary display containing a 1920x1080 tile; no display settings will be changed.");
        var bounds = new PixelBounds(monitor.Bounds.Left, monitor.Bounds.Top, 1920, 1080);
        int left = Math.Max(bounds.Left, monitor.WorkArea.Left), top = Math.Max(bounds.Top, monitor.WorkArea.Top);
        int right = Math.Min(bounds.Right, monitor.WorkArea.Right), bottom = Math.Min(bounds.Bottom, monitor.WorkArea.Bottom);
        if (right <= left || bottom <= top) throw new InvalidOperationException("The primary tile has no usable work area.");
        return monitor with { Bounds = bounds, WorkArea = new(left, top, right - left, bottom - top) };
    }

    internal static PresentationTiming Statistics(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Any(x => !double.IsFinite(x) || x < 0)) throw new ArgumentOutOfRangeException(nameof(values));
        return sorted.Length == 0 ? new(0, null, null, null, null, null) : new(sorted.Length, sorted[0], sorted[^1], sorted.Average(),
            sorted[(int)Math.Ceiling(sorted.Length * .5) - 1], sorted[(int)Math.Ceiling(sorted.Length * .95) - 1]);
    }

    internal static SubmissionSummary Summarize(IReadOnlyList<OverlayFrameTiming> samples, long start, long end, long frequency)
    {
        if (end <= start || frequency <= 0) throw new ArgumentOutOfRangeException(nameof(end));
        var frames = samples.Where(f => f.Succeeded && f.CompletedTimestamp >= start && f.CompletedTimestamp < end).ToArray();
        for (int i = 1; i < frames.Length; i++)
            if (frames[i].CompletedTimestamp < frames[i - 1].CompletedTimestamp)
                throw new InvalidOperationException("Native submission timestamps are out of order.");
        double Ms(long ticks) => ticks * 1000d / frequency;
        var intervals = frames.Zip(frames.Skip(1), (a, b) => Ms(b.CompletedTimestamp - a.CompletedTimestamp)).ToArray();
        double seconds = (end - start) / (double)frequency;
        var outputs = frames.SelectMany(f => f.Outputs).ToArray();
        return new(seconds, frames.Length, outputs.Length, frames.Length / seconds, outputs.Length / seconds,
            frames.Length == 0 ? null : Ms(frames[0].CompletedTimestamp - start),
            frames.Length == 0 ? null : Ms(end - frames[^1].CompletedTimestamp),
            Statistics(intervals), intervals.Count(x => x > 1000d / 60),
            Statistics(frames.Select(f => Ms(f.CompletedTimestamp - f.StartedTimestamp))),
            Statistics(outputs.Select(o => o.DrawAndFlushMilliseconds)), Statistics(outputs.Select(o => o.PresentMilliseconds)), intervals);
    }

    // Runs before Program touches native cursor/window APIs; no Application, HWND, input or timer is created.
    internal static int RunLogicChecks()
    {
        int checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new InvalidOperationException(name); }
        void Reject(Action operation, string name)
        {
            bool rejected = false;
            try { operation(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { rejected = true; }
            Check(rejected, name);
        }
        foreach (int sequence in new[] { 1, 2, 1200, 4095, 4097, 8193, 12289, InputSequenceLimit - 1 })
            Check(DecodeSequence(EncodeSequence(sequence)) == sequence, "Tagged sequence round trip across independent scene ranges and boundaries");
        Check(PresentationProbeNative.InputStructureBytes == 40, "The probe uses the native x64 INPUT layout without calling SendInput");
        Check(PresentationProbeNative.InputMouseOffsetBytes == 8 && PresentationProbeNative.MouseExtraOffsetBytes == 24,
            "INPUT and MOUSEINPUT marker offsets match the x64 ABI");
        Check(EncodeSequence(1) == 0x46430001 && EncodeSequence(InputSequenceLimit - 1) == 0x46437FFF,
            "Complete marker and bounded sequence fit a positive 32-bit transport");
        var allCaseSequences = Enumerable.Range(0, 4).SelectMany(index =>
            Enumerable.Range(index * SampleCapacity + 1, 120 * 11)).ToArray();
        Check(allCaseSequences.Select(EncodeSequence).Distinct().Count() == allCaseSequences.Length &&
            allCaseSequences.All(sequence => DecodeSequence((nuint)checked((uint)EncodeSequence(sequence))) == sequence),
            "All four disjoint case ranges retain full identity through unsigned 32-bit metadata transport");
        Check(new nuint[] { 0, 1, 2, 42 }.All(value => DecodeSequence(value) == 0),
            "Bare sequences and unmarked input are not attributed to the producer");
        Check(DecodeSequence(0x46430000) == 0 && DecodeSequence(0x46438000) == 0,
            "Marker-only and out-of-range sequence boundaries are rejected");
        Check(DecodeSequence(0x46420001) == 0 && DecodeSequence(unchecked((nuint)0x4643435000000001UL)) == 0 &&
            DecodeSequence(unchecked((nuint)0x0000000146430001UL)) == 0,
            "Other prefixes, the obsolete 64-bit marker and unknown high bits are rejected without masking");
        Reject(() => EncodeSequence(0), "Zero sequence rejected");
        Reject(() => EncodeSequence(-1), "Negative sequence rejected");
        Reject(() => EncodeSequence(InputSequenceLimit), "Unbounded sequence rejected");
        var guardedClient = new PixelBounds(51, 85, 900, 600);
        var actualInitialCursor = new OverlayPoint(537, 356);
        var pointerBaseline = InitialPointerBaseline(actualInitialCursor, guardedClient);
        Check(pointerBaseline == actualInitialCursor && pointerBaseline != new OverlayPoint(301, 345),
            "A targeted down at the gesture anchor does not change the actual physical cursor baseline");
        Check(pointerBaseline == new OverlayPoint(537, 356) && pointerBaseline != new OverlayPoint(538, 356),
            "An initial old-position repeat is unchanged while any subsequent position change remains distinct");
        Reject(() => InitialPointerBaseline(new(guardedClient.Right, guardedClient.Top), guardedClient),
            "An initial cursor outside the owned client is rejected");
        Reject(() => InitialPointerBaseline(new(double.NaN, 356), guardedClient), "A non-finite initial cursor is rejected");
        var monitor = new MonitorInfo("real-device", new(-100, -20, 3840, 2160), new(-100, 52, 3840, 2088), 144, 144, true);
        var tile = PrimaryTile(monitor);
        Check(tile.Bounds == new PixelBounds(-100, -20, 1920, 1080), "Tile keeps physical origin and exact dimensions");
        Check(tile.WorkArea == new PixelBounds(-100, 52, 1920, 1008), "Tile work area is a physical intersection");
        Check(tile.DpiX == 144 && tile.DpiY == 144 && tile.DeviceName == monitor.DeviceName && tile.IsPrimary, "Tile preserves device and DPI metadata");
        Check(monitor.Bounds.Width == 3840 && monitor.WorkArea.Width == 3840, "Tile construction never changes the real layout record");
        Reject(() => PrimaryTile(monitor with { IsPrimary = false }), "Secondary tile rejected");
        Reject(() => PrimaryTile(monitor with { Bounds = new(0, 0, 1280, 720) }), "Undersized display rejected without reconfiguration");
        var output = new OverlayOutputTiming("real-device", tile.Bounds, 1000, 1010, 7, 3);
        OverlayFrameTiming Frame(long completed, bool success = true, int outputs = 1) =>
            new(completed - 10, completed, success, Enumerable.Repeat(output, outputs).ToArray());
        var summary = Summarize([Frame(999), Frame(1100, outputs: 2), Frame(1120), Frame(1140), Frame(1500, false), Frame(2000)], 1000, 2000, 1000);
        Check(summary.SuccessfulRenderCalls == 3 && summary.SuccessfulOutputSubmissions == 4, "Window boundaries and failed render calls are excluded");
        Check(summary.RenderSubmissionsPerSecond == 3 && summary.OutputSubmissionsPerSecond == 4, "Rates use actual completed counts over wall time, not reciprocal CPU time");
        Check(summary.CompletionIntervalsMilliseconds.Samples == 2 && summary.CompletionIntervalsMilliseconds.P95 == 20, "Completion intervals use consecutive timestamps");
        Check(summary.IntervalsOver60HzBudget == 2, "Budget misses use measured native completion intervals");
        Check(summary.HostRenderMilliseconds.P95 == 10 && summary.PresentMilliseconds.P95 == 3, "Native stages remain distinct");
        Check(summary.FirstCompletionDelayMilliseconds == 100 && summary.LastCompletionToWindowEndMilliseconds == 860, "Window edge gaps are retained");
        Check(Statistics([]).Samples == 0 && Statistics([]).P95 is null, "No samples produce no fabricated timing");
        Check(Statistics([1, 2, 3, 4]).P50 == 2 && Statistics([1, 2, 3, 4]).P95 == 4, "Nearest-rank percentiles");
        Reject(() => Summarize([], 1, 1, 1000), "Empty measurement duration rejected");
        Reject(() => Summarize([], 1, 2, 0), "Invalid frequency rejected");
        Reject(() => Summarize([Frame(1140), Frame(1120)], 1000, 2000, 1000), "Out-of-order frames rejected");
        Reject(() => Statistics([double.NaN]), "Non-finite samples rejected");
        var bounded = new BoundedSamples<int>(2);
        Check(bounded.TryAdd(1) && bounded.TryAdd(2) && !bounded.TryAdd(3) && bounded.Count == 2 && bounded.Overflowed, "Diagnostic storage is bounded and overflow is explicit");
        Console.WriteLine($"PASS {checks} presentation measurement logic checks; no windows, input or timed benchmark ran.");
        return 0;
    }
}

internal sealed class BoundedSamples<T>(int capacity)
{
    private readonly List<T> values = new(capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity)));
    internal IReadOnlyList<T> Values => values;
    internal int Count => values.Count;
    internal bool Overflowed { get; private set; }
    internal bool TryAdd(T value)
    {
        if (values.Count >= capacity) { Overflowed = true; return false; }
        values.Add(value); return true;
    }
}

internal sealed record PresentationTiming(int Samples, double? Min, double? Max, double? Mean, double? P50, double? P95);
internal sealed record SubmissionSummary(double MeasuredSeconds, int SuccessfulRenderCalls, int SuccessfulOutputSubmissions,
    double RenderSubmissionsPerSecond, double OutputSubmissionsPerSecond, double? FirstCompletionDelayMilliseconds,
    double? LastCompletionToWindowEndMilliseconds, PresentationTiming CompletionIntervalsMilliseconds, int IntervalsOver60HzBudget,
    PresentationTiming HostRenderMilliseconds, PresentationTiming DrawAndFlushMilliseconds, PresentationTiming PresentMilliseconds,
    double[] RawCompletionIntervalsMilliseconds);
