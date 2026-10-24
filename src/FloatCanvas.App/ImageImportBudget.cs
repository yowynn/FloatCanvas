using System.IO;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class ImageImportBudget
{
    internal const int MaximumFiles = 32;
    internal const long MaximumFileBytes = 64L * 1024 * 1024;
    internal const long MaximumBatchBytes = 128L * 1024 * 1024;
    internal static void RequireBytes(long bytes, long maximum = MaximumFileBytes)
    {
        if (bytes < 0 || bytes > maximum) throw new InvalidDataException(L.Get("Images.ImportCapacityExceeded"));
    }
    internal static byte[] Read(Stream input)
    {
        if (input.CanSeek) { RequireBytes(input.Length); input.Position = 0; }
        using var output = new MemoryStream();
        var buffer = new byte[81920]; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        { RequireBytes(output.Length + read); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    internal static async Task<byte[]> ReadAsync(Stream input, CancellationToken ct)
    {
        if (input.CanSeek) RequireBytes(input.Length);
        using var output = new MemoryStream();
        var buffer = new byte[81920]; int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        { RequireBytes(output.Length + read); await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false); }
        return output.ToArray();
    }
    internal static void ValidateResources(IEnumerable<FloatCanvas.Core.AssetResource> resources)
    {
        // A saved document may accumulate many individually valid imports. Its total pixel
        // count is not a single allocation; decoding is bounded by the renderer's cache.
        foreach (var resource in resources) _ = ImageDecodeBudget.ReadPixelCount(resource.Data.Span);
    }
    internal static void ValidateImportBatch(IEnumerable<FloatCanvas.Core.AssetResource> resources)
    {
        long pixels = 0;
        foreach (var resource in resources)
        {
            pixels = checked(pixels + ImageDecodeBudget.ReadPixelCount(resource.Data.Span));
            if (pixels > ImageDecodeBudget.MaximumBatchPixels) throw new InvalidDataException(L.Get("Images.ImportCapacityExceeded"));
        }
    }
}
