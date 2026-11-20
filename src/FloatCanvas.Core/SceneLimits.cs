using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

/// <summary>Capacity contract shared by editing and persistence.</summary>
public static class SceneLimits
{
    private static readonly System.Text.Json.JsonSerializerOptions capacityJson = new() { MaxDepth = 128 };
    public const int MaximumElements = 100_000;
    public const int MaximumPathPoints = 2_000_000;
    public const int MaximumGroupDepth = 64;
    public const int MaximumDefinitions = 4096;
    public const int MaximumExpandedLeaves = 100_000;
    public const int MaximumExpandedNodes = 300_000;
    public const int MaximumExpandedPathPoints = 16_000_000;
    public const int MaximumExpandedEffects = 2_000_000;
    public const long MaximumSceneJsonBytes = 128L * 1024 * 1024;
    public static void ValidateCapacity(SceneDocument document)
    {
        _ = PrefabClosure.Validate(document);
        SceneCanvasRoots.Validate(document);
        var graphs = new SceneNodeGraph[] { document }.Concat(document.Definitions.Select(definition => definition.Graph));
        var previews = document.RootPreviewCanvases.Values.Concat(document.Definitions.Select(definition => definition.PreviewCanvas)
            .OfType<SubprojectCanvasSettings>()).Concat(graphs.SelectMany(graph => graph.Groups).Select(group => group.PreviewCanvas).OfType<SubprojectCanvasSettings>()).ToArray();
        var elements = graphs.SelectMany(graph => graph.Elements).Concat(previews.Select(preview => preview.Background)).Append(document.Background).OfType<CanvasElement>();
        if (elements.Any(e => e.Points is null || e.Points.Count > MaximumPathPoints || e.ImageFrame?.Count > MaximumPathPoints))
            throw new InvalidDataException(L.Get("Scene.PathCapacityExceeded"));
        if (elements.Any(element => element.Style is null || !AppearanceRegistry.IsValidColorRoles(element.Style.AdditionalColors) ||
            !ElementAppearance.IsValidVariant(element.Kind, element.Style.Variant) || !Enum.IsDefined(element.NumberTextSizeMode) ||
            !NumberSequence.IsValidMarker(element) || !PresentationLimits.IsValidElement(element) || !WindowSourceLimits.IsValidElement(element)))
            throw new InvalidDataException(L.Get("Scene.InvalidElementStyle"));
        if (document.Background?.WindowSource is not null || previews.Any(preview => preview.Background?.WindowSource is not null)) throw new InvalidDataException("A canvas background cannot be a live window source.");
        // An inexpensive upper bound keeps ordinary commits independent of unchanged
        // path size. Only documents near the package JSON ceiling need a streaming count.
        long upperBound = 4096L + document.RootCanvasAssignments.Count * 96L + (document.Canvas?.Projection?.Quad?.Count ?? 0) * 96L;
        upperBound += previews.Sum(preview => 4096L + (preview.Canvas?.Projection?.Quad?.Count ?? 0) * 96L);
        foreach (var element in elements)
            upperBound += 2048L + (element.Points.Count + (element.ImageFrame?.Count ?? 0)) * 96L + (element.Effects?.Count ?? 0) * 512L +
                6L * ((element.Name?.Length ?? 0) + (element.Text?.Length ?? 0) + (element.AssetId?.Length ?? 0) + (element.Style?.FontFamily?.Length ?? 0)) +
                (element.Style?.AdditionalColors.Sum(pair => 64L + pair.Key.Length * 6L) ?? 0) + (element.Presentation is null ? 0 : 1024L) +
                WindowSourceLimits.EstimateBytes(element.WindowSource) * 3L;
        foreach (var graph in graphs)
        {
            upperBound += graph.Groups.Sum(group => 1024L + (group.Name?.Length ?? 0) * 6L + (group.ChildOrder?.Count ?? 0) * 80L);
            upperBound += graph.Instances.Sum(instance => 1024L + (instance.Name?.Length ?? 0) * 6L);
            upperBound += (graph.RootOrder?.Count ?? 0) * 80L;
        }
        upperBound += document.Definitions.Sum(definition => 2048L + definition.Name.Length * 6L +
            (definition.Source is { } source ? 4096L + (source.Locator?.Length ?? 0) * 6L + source.OriginClosure.Count * 512L + source.ImportedDefinitionIds.Count * 160L : 0));
        upperBound += document.DisplayLayout.Sum(display => 256L + (display?.DeviceName?.Length ?? 0) * 6L);
        if (upperBound > MaximumSceneJsonBytes) ValidateJsonCapacity(document, MaximumSceneJsonBytes);
    }

    internal static void ValidateJsonCapacity(SceneDocument document, long maximumBytes) => ValidatePayloadJsonCapacity(document, maximumBytes);
    public static void ValidatePayloadJsonCapacity<T>(T document, long maximumBytes = MaximumSceneJsonBytes)
    {
        using var counter = new CountingStream(maximumBytes);
        System.Text.Json.JsonSerializer.Serialize(counter, document, capacityJson);
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
