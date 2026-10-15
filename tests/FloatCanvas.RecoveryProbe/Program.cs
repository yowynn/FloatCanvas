using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static async Task<int> Main(string[] args)
    {
        var checks = new List<object>();
        string? report = Value(args, "--report");
        try
        {
            if (args.Length < 2 || args[0] is not ("--seed" or "--verify"))
                throw new ArgumentException("Use --seed <isolated-profile> or --verify <isolated-profile>, optional --report <path>, --receipt <app-smoke.json>, --expect-live-layout.");
            var profile = Path.GetFullPath(args[1]);
            var fixturePath = Path.Combine(profile, "probe-fixture.json");
            var store = new RecoveryStore(Path.Combine(profile, "recovery"));
            if (args[0] == "--seed")
            {
                if (File.Exists(fixturePath) || File.Exists(store.CurrentPath) || File.Exists(store.BackupPath))
                    throw new InvalidOperationException("Seed requires an empty, isolated profile; existing recovery data will not be overwritten.");
                var png = FixturePng(24, 16);
                var assets = new AssetLibrary(); var assetId = assets.Add(png);
                var state = new WorkspaceState
                {
                    LiveDocument = new SceneDocument
                    {
                        Viewport = new(0, 0, 641, 479), NextNumber = 38,
                        DisplayLayout = [new() { DeviceName = "RecoveryProbe:seed-layout", Bounds = new(0, 0, 641, 479), DpiX = 96, DpiY = 96 }],
                        Elements =
                        [
                            new() { Kind = ElementKind.Text, Text = "跨进程恢复 · 浮绘\n自建样本", Width = 250, Height = 68,
                                Transform = Affine2D.Translation(120, 140), Style = new() { FontSize = 22, Stroke = 0xFF2383E2 } },
                            new() { Kind = ElementKind.Number, Number = 37, Width = 46, Height = 46,
                                Transform = Affine2D.Translation(410, 170), Style = new() { Fill = 0xFFFFEEEE, Stroke = 0xFFEF4444 } },
                            new() { Kind = ElementKind.Image, AssetId = assetId, Width = 240, Height = 160,
                                Transform = Affine2D.Translation(180, 300).Compose(Affine2D.Rotation(30)).Compose(Affine2D.Scale(1.25, 1.25)) }
                        ]
                    },
                    CaptureOrigin = new(512, 128),
                    CaptureDocument = new SceneDocument
                    {
                        Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, 24, 16),
                        Background = new() { Kind = ElementKind.Image, AssetId = assetId, Width = 24, Height = 16, Locked = true },
                        Elements = [new() { Kind = ElementKind.Text, Text = "草稿", Width = 24, Height = 16, Style = new() { FontSize = 8 } }]
                    }
                };
                await store.SaveAsync(state, assets);
                var fixture = new Fixture(state, assetId, Hex(SHA256.HashData(png)), 24, 16,
                    Hex(SHA256.HashData(await File.ReadAllBytesAsync(store.CurrentPath))));
                await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(fixture, Json));
                Check(true, "Independent profile seeded using production RecoveryStore", checks);
            }

            var expected = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(fixturePath)) ?? throw new InvalidDataException("Fixture manifest is missing.");
            var recovered = await store.LoadAsync() ?? throw new InvalidDataException("Production store found no recoverable workspace.");
            Check(!recovered.FromBackup, "Current recovery generation is valid; no fallback was needed", checks);
            SceneValidation.Validate(recovered.State);
            var actualLive = recovered.State.LiveDocument;
            var normalizedLive = actualLive.Clone();
            normalizedLive.Viewport = expected.Expected.LiveDocument.Viewport;
            normalizedLive.DisplayLayout = expected.Expected.LiveDocument.DisplayLayout.Select(d => d.Clone()).ToList();
            Check(Equal(normalizedLive, expected.Expected.LiveDocument), "All live typed IDs, semantics, style, geometry and transforms remain exact", checks);
            Check(Equal(recovered.State.CaptureDocument, expected.Expected.CaptureDocument) && recovered.State.CaptureOrigin == expected.Expected.CaptureOrigin,
                "Frozen draft, background, typed objects and screen origin remain exact", checks);
            Check(recovered.Assets.Count == 1 && recovered.Assets.TryGet(expected.AssetId, out _), "Image resource survives and remains content-addressed/deduplicated", checks);
            var pngBytes = recovered.Assets.Get(expected.AssetId).Data;
            Check(Hex(SHA256.HashData(pngBytes.Span)) == expected.PngSha256, "Image PNG bytes match original SHA256", checks);
            Check(pngBytes.Span[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
                BinaryPrimitives.ReadInt32BigEndian(pngBytes.Span.Slice(16, 4)) == expected.PngWidth &&
                BinaryPrimitives.ReadInt32BigEndian(pngBytes.Span.Slice(20, 4)) == expected.PngHeight,
                "Retained resource has the seeded PNG signature and dimensions", checks);
            if (args.Contains("--expect-live-layout"))
                Check(actualLive.DisplayLayout.Count > 0 && actualLive.DisplayLayout.All(d => !d.DeviceName.StartsWith("RecoveryProbe:", StringComparison.Ordinal)) &&
                    actualLive.DisplayLayout.Select(d => d.Bounds).Aggregate(RectD.Union) == actualLive.Viewport,
                    "Real application replaced the synthetic seed layout and persisted its recovered document", checks);

            if (Value(args, "--receipt") is { } receiptPath)
            {
                using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
                var root = receipt.RootElement;
                Check(root.GetProperty("Passed").GetBoolean() && !root.GetProperty("CapturedInput").GetBoolean() && root.GetProperty("Mode").GetString() == "PassThrough",
                    "Second application process reports successful recovered pass-through initialization", checks);
                var actualElements = root.GetProperty("LiveElements").EnumerateArray().ToArray();
                Check(actualElements.Length == expected.Expected.LiveDocument.Elements.Count, "Second process actually loaded all expected live typed objects", checks);
                foreach (var element in expected.Expected.LiveDocument.Elements)
                {
                    var actual = actualElements.Single(item => item.GetProperty("Id").GetGuid() == element.Id);
                    Check(actual.GetProperty("Kind").GetInt32() == (int)element.Kind && actual.GetProperty("Text").GetString() == element.Text &&
                        actual.GetProperty("Number").GetInt32() == element.Number && actual.GetProperty("AssetId").GetString() == element.AssetId &&
                        JsonSerializer.Deserialize<Affine2D>(actual.GetProperty("Transform").GetRawText()) == element.Transform,
                        $"Second process receipt preserves {element.Kind} identity, semantics and full transform", checks);
                }
                Check(root.GetProperty("ResourcesComplete").GetBoolean() && root.GetProperty("AssetCount").GetInt32() == recovered.Assets.Count,
                    "Second process confirms every live/draft resource is loaded", checks);
                Check(root.GetProperty("CaptureElementCount").GetInt32() == expected.Expected.CaptureDocument!.Elements.Count,
                    "Second process actually loaded the frozen draft", checks);
                Check(JsonSerializer.Deserialize<RectD>(root.GetProperty("Viewport").GetRawText()) == actualLive.Viewport,
                    "Second process viewport matches persisted real monitor layout", checks);
            }

            var observation = new
            {
                Passed = true, Timestamp = DateTimeOffset.Now, Mode = args[0], Profile = profile, Checks = checks,
                CurrentHash = Hex(SHA256.HashData(await File.ReadAllBytesAsync(store.CurrentPath))),
                SeedHash = expected.SeedRecoverySha256,
                ActualLive = new { actualLive.Viewport, actualLive.DisplayLayout, Elements = actualLive.Elements.Select(e => new { e.Id, Kind = e.Kind.ToString(), e.Text, e.Number, e.AssetId, e.Transform }) },
                CaptureElements = recovered.State.CaptureDocument?.Elements.Count, CaptureOrigin = recovered.State.CaptureOrigin,
                Assets = new { recovered.Assets.Count, expected.PngSha256, expected.PngWidth, expected.PngHeight },
                Failure = (string?)null
            };
            if (report is not null) await WriteReport(report, observation);
            Console.WriteLine($"PASS {checks.Count} production recovery probe checks: {args[0]} {profile}");
            return 0;
        }
        catch (Exception ex)
        {
            if (report is not null) await WriteReport(report, new { Passed = false, Timestamp = DateTimeOffset.Now, Checks = checks, Failure = ex.ToString() });
            Console.Error.WriteLine(ex); return 1;
        }
    }

    private static string? Value(string[] args, string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static bool Equal<T>(T first, T second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    private static void Check(bool condition, string description, List<object> results)
    { results.Add(new { Description = description, Passed = condition }); if (!condition) throw new InvalidDataException(description); }
    private static async Task WriteReport(string path, object value)
    { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, Json)); }
    private sealed record Fixture(WorkspaceState Expected, string AssetId, string PngSha256, int PngWidth, int PngHeight, string SeedRecoverySha256);

    private static byte[] FixturePng(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height); header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                for (var x = 0; x < width; x++) zlib.Write(new byte[] { (byte)(x * 9), (byte)(y * 13), 220, (byte)(x == 0 || y == 0 ? 0 : 170) });
            }
        Chunk(output, "IDAT", compressed.ToArray()); Chunk(output, "IEND", []); return output.ToArray();
    }
    private static void Chunk(Stream output, string type, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); output.Write(length);
        byte[] name = Encoding.ASCII.GetBytes(type); output.Write(name); output.Write(bytes);
        uint crc = 0xffffffff;
        foreach (byte value in name.Concat(bytes)) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1; }
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); output.Write(length);
    }
}
