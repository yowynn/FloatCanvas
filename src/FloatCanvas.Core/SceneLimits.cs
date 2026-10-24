using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

/// <summary>Capacity contract shared by editing and persistence.</summary>
public static class SceneLimits
{
    public const int MaximumElements = 100_000;
    public const int MaximumPathPoints = 2_000_000;
    public const int MaximumGroupDepth = 64;
    public const long MaximumSceneJsonBytes = 128L * 1024 * 1024;
    public static void ValidateCapacity(SceneDocument document)
    {
        if (document.Elements.Count > MaximumElements || document.Groups.Count > MaximumElements)
            throw new InvalidDataException(L.Get("Scene.ElementCountExceeded"));
        if (document.Elements.Append(document.Background).Any(e => e is not null && (e.Points is null || e.Points.Count > MaximumPathPoints)))
            throw new InvalidDataException(L.Get("Scene.PathCapacityExceeded"));
        var parents = new Dictionary<Guid, Guid?>();
        foreach (var group in document.Groups)
            if (group is null || !parents.TryAdd(group.Id, group.ParentId)) throw new InvalidDataException(L.Get("Scene.InvalidElementId"));
        var depths = new Dictionary<Guid, int>();
        foreach (var group in document.Groups)
        {
            var chain = new List<Guid>(); var visited = new HashSet<Guid>(); Guid? current = group.Id;
            int depth = 0;
            while (current is { } id)
            {
                if (depths.TryGetValue(id, out depth)) break;
                if (!visited.Add(id) || !parents.TryGetValue(id, out current)) throw new InvalidDataException(L.Get("Scene.InvalidElementId"));
                chain.Add(id);
                if (chain.Count > MaximumGroupDepth) throw new InvalidDataException(L.Get("Scene.GroupDepthExceeded"));
            }
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (++depth > MaximumGroupDepth) throw new InvalidDataException(L.Get("Scene.GroupDepthExceeded"));
                depths[chain[i]] = depth;
            }
        }
        // An inexpensive upper bound keeps ordinary commits independent of unchanged
        // path size. Only documents near the package JSON ceiling need a streaming count.
        long upperBound = 4096L + (document.Canvas?.Projection?.Quad?.Count ?? 0) * 96L;
        foreach (var element in document.Elements.Append(document.Background).OfType<CanvasElement>())
            upperBound += 2048L + element.Points.Count * 96L + (element.Effects?.Count ?? 0) * 512L +
                6L * ((element.Name?.Length ?? 0) + (element.Text?.Length ?? 0) + (element.AssetId?.Length ?? 0) + (element.Style?.FontFamily?.Length ?? 0));
        upperBound += document.Groups.Sum(group => 192L + (group.Name?.Length ?? 0) * 6L);
        upperBound += document.DisplayLayout.Sum(display => 256L + (display?.DeviceName?.Length ?? 0) * 6L);
        if (upperBound > MaximumSceneJsonBytes) ValidateJsonCapacity(document, MaximumSceneJsonBytes);
    }

    internal static void ValidateJsonCapacity(SceneDocument document, long maximumBytes)
    {
        using var counter = new CountingStream(maximumBytes);
        System.Text.Json.JsonSerializer.Serialize(counter, document);
    }
    private sealed class CountingStream(long maximum) : Stream
    {
        private long count;
        public override void Write(byte[] buffer, int offset, int length) => Write(buffer.AsSpan(offset, length));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (count > maximum - buffer.Length) throw new InvalidDataException(L.Get("Scene.JsonCapacityExceeded"));
            count += buffer.Length;
        }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => count; public override long Position { get => count; set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int length) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
}
