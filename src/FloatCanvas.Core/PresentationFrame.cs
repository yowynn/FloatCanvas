using System.Collections.ObjectModel;
using System.Text;

namespace FloatCanvas.Core;

public sealed record PresentationKeyCap(int VirtualKey, string Label, bool IsModifier = false, int ScanCode = 0, bool IsExtended = false);
public enum PresentationMouseButton { Left = 0, Right = 1, Middle = 2, X1 = 3, X2 = 4 }
public readonly record struct PresentationClick(PointD ScreenPoint, PresentationMouseButton Button, TimeSpan Timestamp);
public readonly record struct PresentationTrailSample(PointD ScreenPoint, TimeSpan Timestamp, bool StartsSegment = false);

public sealed class PresentationChord
{
    public PresentationChord(IEnumerable<PresentationKeyCap> keys, TimeSpan timestamp, bool isHeld = false)
    {
        var owned = PresentationFrame.Freeze(keys, PresentationLimits.MaximumChordKeys);
        if (owned.Length == 0 || timestamp < TimeSpan.Zero || owned.Any(key => !PresentationFrame.ValidKey(key))) throw new ArgumentException("Invalid presentation chord.");
        Keys = Array.AsReadOnly(owned); Timestamp = timestamp; IsHeld = isHeld;
    }
    public IReadOnlyList<PresentationKeyCap> Keys { get; }
    public TimeSpan Timestamp { get; }
    public bool IsHeld { get; }
}

/// <summary>Owned immutable host input snapshot. Points are global physical screen pixels; times share one monotonic epoch.</summary>
public sealed class PresentationFrame
{
    public static PresentationFrame Empty { get; } = new(0, 0, TimeSpan.Zero);
    public PresentationFrame(long epoch, long sequence, TimeSpan time, IEnumerable<PresentationKeyCap>? pressedKeys = null,
        IEnumerable<PresentationChord>? recentChords = null, PointD? cursorScreen = null, IEnumerable<PresentationClick>? clicks = null,
        IEnumerable<PresentationTrailSample>? trail = null, bool keyboardSuppressed = false)
    {
        if (epoch < 0 || sequence < 0 || time < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(time));
        var keys = Freeze(pressedKeys, PresentationLimits.MaximumHeldKeys); var chords = Freeze(recentChords, PresentationLimits.MaximumChords);
        var clickValues = Freeze(clicks, PresentationLimits.MaximumClicks); var trailValues = Freeze(trail, PresentationLimits.MaximumTrailPoints);
        if (keys.Any(key => !ValidKey(key)) || chords.Any(chord => chord is null || chord.Timestamp > time) || cursorScreen is { } cursor && !ValidPoint(cursor) ||
            clickValues.Any(click => !ValidPoint(click.ScreenPoint) || !Enum.IsDefined(click.Button) || click.Timestamp < TimeSpan.Zero || click.Timestamp > time) ||
            trailValues.Any(point => !ValidPoint(point.ScreenPoint) || point.Timestamp < TimeSpan.Zero || point.Timestamp > time))
            throw new ArgumentException("Invalid presentation input snapshot.");
        long payload = 128L + keys.Sum(KeyBytes) + chords.Sum(chord => 32L + chord.Keys.Sum(KeyBytes)) + clickValues.Length * 40L + trailValues.Length * 40L;
        if (payload > PresentationLimits.MaximumFramePayloadBytes) throw new ArgumentException("Presentation frame capacity exceeded.");
        Epoch = epoch; Sequence = sequence; Time = time; CursorScreen = cursorScreen; KeyboardSuppressed = keyboardSuppressed;
        PressedKeys = Array.AsReadOnly(keys); RecentChords = Array.AsReadOnly(chords); Clicks = Array.AsReadOnly(clickValues); Trail = Array.AsReadOnly(trailValues);
        PayloadBytes = payload;
    }
    public long Epoch { get; }
    public long Sequence { get; }
    public TimeSpan Time { get; }
    public IReadOnlyList<PresentationKeyCap> PressedKeys { get; }
    public IReadOnlyList<PresentationChord> RecentChords { get; }
    public PointD? CursorScreen { get; }
    public IReadOnlyList<PresentationClick> Clicks { get; }
    public IReadOnlyList<PresentationTrailSample> Trail { get; }
    public bool KeyboardSuppressed { get; }
    public long PayloadBytes { get; }
    private readonly PresentationFrame? outsideFloatCanvas, floatCanvasOnly;

    /// <summary>Combine three independently captured keyboard histories with one shared pointer snapshot.</summary>
    public static PresentationFrame WithKeyboardScopes(PresentationFrame allApplications,
        PresentationFrame outsideFloatCanvas, PresentationFrame floatCanvasOnly)
    {
        ArgumentNullException.ThrowIfNull(allApplications); ArgumentNullException.ThrowIfNull(outsideFloatCanvas); ArgumentNullException.ThrowIfNull(floatCanvasOnly);
        if (new[] { allApplications, outsideFloatCanvas, floatCanvasOnly }.Any(frame => frame.outsideFloatCanvas is not null ||
            frame.Epoch != allApplications.Epoch || frame.Sequence != allApplications.Sequence || frame.Time != allApplications.Time ||
            frame.KeyboardSuppressed != allApplications.KeyboardSuppressed)) throw new ArgumentException("Inconsistent presentation keyboard snapshots.");
        return new(allApplications, outsideFloatCanvas, floatCanvasOnly);
    }

    private PresentationFrame(PresentationFrame all, PresentationFrame outside, PresentationFrame inside)
    {
        Epoch = all.Epoch; Sequence = all.Sequence; Time = all.Time; CursorScreen = all.CursorScreen;
        KeyboardSuppressed = all.KeyboardSuppressed; PressedKeys = all.PressedKeys; RecentChords = all.RecentChords;
        Clicks = all.Clicks; Trail = all.Trail; outsideFloatCanvas = outside; floatCanvasOnly = inside;
        PayloadBytes = all.PayloadBytes + outside.PayloadBytes + inside.PayloadBytes;
        if (PayloadBytes > PresentationLimits.MaximumScopedFramePayloadBytes) throw new ArgumentException("Presentation scoped frame capacity exceeded.");
    }

    /// <summary>Restricted layers never reuse an unclassified all-application history.</summary>
    public PresentationFrame ForKeyboardScope(PresentationKeyboardScope scope) => scope switch
    {
        PresentationKeyboardScope.AllApplications => this,
        PresentationKeyboardScope.OutsideFloatCanvas => outsideFloatCanvas ?? Empty,
        PresentationKeyboardScope.FloatCanvasOnly => floatCanvasOnly ?? Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };
    internal static T[] Freeze<T>(IEnumerable<T>? source, int maximum)
    {
        var result = source?.Take(maximum + 1).ToArray() ?? [];
        if (result.Length > maximum) throw new ArgumentException("Presentation input capacity exceeded.");
        return result;
    }
    internal static bool ValidKey(PresentationKeyCap? key) => key is not null && key.VirtualKey is > 0 and < 256 &&
        key.ScanCode is >= 0 and <= ushort.MaxValue &&
        key.Label is { Length: > 0 and <= PresentationLimits.MaximumKeyLabelLength } && !key.Label.Any(char.IsControl);
    private static long KeyBytes(PresentationKeyCap key) => 32L + Encoding.UTF8.GetByteCount(key.Label);
    private static bool ValidPoint(PointD point) => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        point.X is >= int.MinValue and <= int.MaxValue && point.Y is >= int.MinValue and <= int.MaxValue;
}
