using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloatCanvas.App;

/// <summary>Exercises the production release parser, HTTP boundaries and archive validator without network or application replacement.</summary>
internal static class GitHubUpdateClientTests
{
    internal static void Run(Action<string, bool> check) => RunAsync(check).GetAwaiter().GetResult();
    private static async Task RunAsync(Action<string, bool> check)
    {
        SemVersionOrder(check);
        await ReleaseSelection(check);
        await HttpFailures(check);
        await PackageValidation(check);
    }

    private static void SemVersionOrder(Action<string, bool> check)
    {
        string[] ordered = { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0" };
        for (int i = 1; i < ordered.Length; i++) check("SemVer precedence " + ordered[i], Version(ordered[i - 1]).CompareTo(Version(ordered[i])) < 0);
        check("numeric preview identifiers compare numerically", Version("0.2.0-preview.10").CompareTo(Version("0.2.0-preview.2")) > 0);
        check("build metadata does not change precedence", Version("1.0.0+local.77").CompareTo(Version("1.0.0+other")) == 0);
        check("large identifiers do not overflow", Version("99999999999999999999999.0.0").CompareTo(Version("9999999999999999999999.0.0")) > 0);
        foreach (string invalid in new[] { "1.2", "01.2.3", "1.02.3", "1.2.03", "1.2.3-preview.01", "1.2.3-", "1.2.3+", "1.2.3-preview..1", "v1.2.3", "1.2.3/other", "1.2.3\n" })
            check("invalid SemVer rejected " + invalid, !SemVersion.TryParse(invalid, out _));
    }

    private static async Task ReleaseSelection(Action<string, bool> check)
    {
        using var empty = Client(_ => Response("[]"));
        check("repository with no releases is a normal empty result", (await empty.CheckAsync("0.2.0-preview.1", default)).Update is null);
        using var firstPreview = Client(_ => Response(JsonSerializer.Serialize(new[] { Release("0.2.0-preview.1", prerelease: true) })));
        check("preview updates are opt-in even when the current build is a preview", (await firstPreview.CheckAsync("0.2.0-preview.0", default)).Update is null);
        check("explicit opt-in discovers preview.0 to preview.1", (await firstPreview.CheckAsync("0.2.0-preview.0", default, true)).Update?.Version == "0.2.0-preview.1");
        string releases = JsonSerializer.Serialize(new[] { Release("0.3.0-preview.10"), Release("0.3.0-preview.2"), Release("0.2.0"), Release("9.0.0", draft: true), Release("1.0.0-beta", prerelease: false) });
        using var client = Client(_ => Response(releases));
        check("opted-in preview build can update to highest compatible preview", (await client.CheckAsync("0.2.0-preview.1", default, true)).Update?.Version == "1.0.0-beta");
        check("preview build with opt-in off only sees stable releases", (await client.CheckAsync("0.2.0-preview.1", default)).Update?.Version == "0.2.0");
        check("stable build excludes both preview tags and GitHub prerelease flag", (await client.CheckAsync("0.1.0", default)).Update?.Version == "0.2.0");
        check("stable build may explicitly opt into previews", (await client.CheckAsync("0.1.0", default, true)).Update?.Version == "1.0.0-beta");
        using var flagged = Client(_ => Response(JsonSerializer.Serialize(new[] { Release("1.0.0", prerelease: true) })));
        check("GitHub prerelease flag excludes even a stable-looking tag by default", (await flagged.CheckAsync("0.2.0", default)).Update is null);
        check("explicit opt-in permits a stable-looking tag marked prerelease by GitHub", (await flagged.CheckAsync("0.2.0", default, true)).Update?.Version == "1.0.0");
        using var numeric = Client(_ => Response(JsonSerializer.Serialize(new[] { Release("0.3.0-preview.2"), Release("0.3.0-preview.10") })));
        check("highest preview is chosen independently of API order", (await numeric.CheckAsync("0.3.0-preview.1", default, true)).Update?.Version == "0.3.0-preview.10");
        using var equal = Client(_ => Response(JsonSerializer.Serialize(new[] { Release("0.2.0+server") })));
        check("equal precedence with changed build metadata is not an update", (await equal.CheckAsync("0.2.0+local", default)).Update is null);
        using var noHash = Client(_ => Response(JsonSerializer.Serialize(new[] { Release("1.0.0", hash: false, sidecar: false) })));
        check("release without integrity metadata is not offered", (await noHash.CheckAsync("0.2.0", default)).Update is null);
        using var hostile = Client(_ => Response(JsonSerializer.Serialize(new[] { Release("1.0.0", host: "evil.example") })));
        await Reject<InvalidDataException>("release cannot point at another download host", () => hostile.CheckAsync("0.2.0", default), check);
        using var pages = Client(request => request.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal) ? Response(JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(i => Release("0.1." + i)))) : Response(JsonSerializer.Serialize(new[] { Release("1.0.0") })));
        check("release selection follows bounded API pagination", (await pages.CheckAsync("0.1.0", default)).Update?.Version == "1.0.0");
        const string notes = "## Current version\n<script>literal only</script>";
        using var current = Client(_ => Response(JsonSerializer.Serialize(new[] { new { tag_name = "v0.2.0-preview.4", draft = false, prerelease = true, body = notes } })));
        var currentResult = await current.CheckAsync("0.2.0-preview.4+local", default);
        check("up-to-date preview notes remain available with opt-in off and no package assets", currentResult.Update is null && currentResult.CurrentNotes == notes);
        check("new and legacy settings leave preview updates disabled", !new UserSettings().IncludePrereleaseUpdates && !JsonSerializer.Deserialize<UserSettings>("{}")!.IncludePrereleaseUpdates);
        foreach (bool enabled in new[] { false, true })
            check($"preview update preference {enabled} survives persistence", JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(new UserSettings { IncludePrereleaseUpdates = enabled }))!.IncludePrereleaseUpdates == enabled);
    }

    private static async Task HttpFailures(Action<string, bool> check)
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.NotFound })
        {
            using var client = Client(_ => new HttpResponseMessage(status));
            try { await client.CheckAsync("0.2.0", default); check("HTTP " + status + " fails clearly", false); }
            catch (HttpRequestException ex) { check("HTTP " + status + " retains status and readable message", ex.StatusCode == status && ex.Message.Length > 25); }
        }
        using var oversized = Client(_ => Response(new string(' ', 4 * 1024 * 1024 + 1)));
        await Reject<InvalidDataException>("large release metadata rejected", () => oversized.CheckAsync("0.2.0", default), check);
        using var invalidJson = Client(_ => Response("{}"));
        await Reject<InvalidDataException>("non-list metadata rejected", () => invalidJson.CheckAsync("0.2.0", default), check);
        using var canceled = Client(_ => Response("[]"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Reject<OperationCanceledException>("user cancellation is preserved", () => canceled.CheckAsync("0.2.0", cancellation.Token), check);
        using var redirect = Client(_ => Redirect("http://github.com/yowynn/FloatCanvas/releases"));
        await Reject<InvalidDataException>("HTTP downgrade redirect rejected", () => redirect.CheckAsync("0.2.0", default), check);
    }

    private static async Task PackageValidation(Action<string, bool> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-updates-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string install = Path.Combine(root, "existing-install"); Directory.CreateDirectory(install); File.WriteAllText(Path.Combine(install, "FloatCanvas.exe"), "existing installation sentinel");
        try
        {
            byte[] valid = Archive("1.0.0");
            var reports = new List<UpdateProgress>();
            using (var client = DownloadClient(valid))
            {
                var result = await client.DownloadAsync(Update(valid), root, new InlineProgress(reports.Add), default);
                check("validated package returns its single versioned directory", Path.GetFileName(result.PackageDirectory) == "FloatCanvas-1.0.0-win-x64" && result.Version == "1.0.0");
                check("validated package returns all relative files including manifest", result.Files.Count == 6 && result.Files.Contains("Docs/readme.txt") && result.Files.Contains("PACKAGE-INFO.json") && result.Files.All(p => !Path.IsPathRooted(p)));
                check("download progress includes expected total and final byte count", reports.Count > 0 && reports[^1].ReceivedBytes == valid.Length && reports.All(p => p.TotalBytes == valid.Length));
            }
            using (var client = DownloadClient(valid, sidecar: true))
            {
                var result = await client.DownloadAsync(Update(valid) with { Sha256 = null, ChecksumUrl = Asset("1.0.0", ".zip.sha256") }, root, null, default);
                check("sidecar-only package hash is supported", File.Exists(Path.Combine(result.PackageDirectory, "FloatCanvas.exe")));
            }
            await BadDownload("incorrect package hash rejected", valid, Update(valid) with { Sha256 = new string('0', 64) }, root, check);
            await BadDownload("truncated package rejected", valid, Update(valid) with { Size = valid.Length + 1 }, root, check);
            await BadDownload("package URL from another repository rejected", valid, Update(valid) with { PackageUrl = new Uri("https://github.com/other/FloatCanvas/releases/download/v1.0.0/FloatCanvas-1.0.0-win-x64.zip") }, root, check);
            await BadDownload("digest and sidecar must agree", valid, Update(valid) with { Sha256 = new string('0', 64), ChecksumUrl = Asset("1.0.0", ".zip.sha256") }, root, check, sidecar: true);
            using (var client = DownloadClient(valid, sidecar: true, checksumFilename: "another.zip"))
                await Reject<InvalidDataException>("checksum must name the exact release package", () => client.DownloadAsync(Update(valid) with { ChecksumUrl = Asset("1.0.0", ".zip.sha256") }, root, null, default), check);
            foreach (string unsafePath in new[] { "../escape.txt", "/absolute.txt", "C:/drive.txt", "FloatCanvas-1.0.0-win-x64/../escape.txt", "FloatCanvas-1.0.0-win-x64\\escape.txt", "FloatCanvas-1.0.0-win-x64/file:stream", "FloatCanvas-1.0.0-win-x64/CON.txt", "FloatCanvas-1.0.0-win-x64/ending. ", "OtherRoot/file.txt" })
            {
                byte[] zip = Archive("1.0.0", extra: new[] { (unsafePath, "bad", 0) });
                await BadDownload("unsafe ZIP path rejected " + unsafePath, zip, Update(zip), root, check);
            }
            byte[] duplicate = Archive("1.0.0", extra: new[] { ("FloatCanvas-1.0.0-win-x64/FLOATCANVAS.EXE", "duplicate", 0) });
            await BadDownload("case-insensitive duplicate ZIP file rejected", duplicate, Update(duplicate), root, check);
            byte[] symlink = Archive("1.0.0", extra: new[] { ("FloatCanvas-1.0.0-win-x64/link", "target", unchecked((int)0xA1FF0000)) });
            await BadDownload("symbolic link ZIP entry rejected", symlink, Update(symlink), root, check);
            byte[] bomb = (byte[])valid.Clone();
            for (int i = 0; i < bomb.Length - 28; i++)
                if (BitConverter.ToUInt32(bomb, i) == 0x02014b50) { BitConverter.GetBytes(129u * 1024 * 1024).CopyTo(bomb, i + 24); break; }
            await BadDownload("ZIP expansion size limit is checked before decompression", bomb, Update(bomb), root, check);
            using (var cancellation = new CancellationTokenSource())
            using (var client = DownloadClient(valid))
                await Reject<OperationCanceledException>("cancellation during package download is preserved", () => client.DownloadAsync(Update(valid), root, new InlineProgress(_ => cancellation.Cancel()), cancellation.Token), check);
            foreach (string manifest in new[] { "{}", "not json", Manifest("1.0.0", product: "Other App"), Manifest("1.0.1"), Manifest("1.0.0", rid: "win-arm64"), Manifest("1.0.0", configuration: "Debug"), Manifest("1.0.0", selfContained: true), Manifest("1.0.0", files: new[] { "FloatCanvas.exe" }) })
            {
                byte[] zip = Archive("1.0.0", manifest);
                await BadDownload("incompatible package manifest rejected " + manifest[..Math.Min(35, manifest.Length)], zip, Update(zip), root, check);
            }
            byte[] fullManifest = Archive("1.0.0", Manifest("1.0.0", files: Names));
            using (var client = DownloadClient(fullManifest)) check("complete optional manifest file list accepted", (await client.DownloadAsync(Update(fullManifest), root, null, default)).Files.Count == Names.Length);
            using (var client = Client(request => request.RequestUri!.Host == "github.com" ? Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/file?token=public-signed") : Bytes(valid)))
                check("GitHub official asset CDN redirects accepted", (await client.DownloadAsync(Update(valid), root, null, default)).Files.Count == 6);
            using (var client = Client(_ => Redirect("https://attacker.example/asset.zip")))
                await Reject<InvalidDataException>("asset redirect to unrelated host rejected", () => client.DownloadAsync(Update(valid), root, null, default), check);
            check("failed downloads never change an existing installation", File.ReadAllText(Path.Combine(install, "FloatCanvas.exe")) == "existing installation sentinel" && !File.Exists(Path.Combine(root, "escape.txt")));
            check("failed staging directories are removed", Directory.EnumerateDirectories(root, "download-*").All(d => Directory.EnumerateDirectories(d).Count() == 1 && !File.Exists(Path.Combine(d, "package.zip"))));
        }
        finally { Directory.Delete(root, true); }
    }

    private static readonly string[] Names = { "PACKAGE-INFO.json", "FloatCanvas.exe", "FloatCanvas.dll", "FloatCanvas.deps.json", "FloatCanvas.runtimeconfig.json", "Docs/readme.txt" };
    private static SemVersion Version(string value) { SemVersion.TryParse(value, out var version); return version!; }
    private static Uri Asset(string version, string suffix = ".zip") => new($"https://github.com/yowynn/FloatCanvas/releases/download/v{version}/FloatCanvas-{version}-win-x64{suffix}");
    private static AvailableUpdate Update(byte[] data) => new("1.0.0", "Notes", new Uri("https://github.com/yowynn/FloatCanvas/releases/tag/v1.0.0"), Asset("1.0.0"), data.Length, Convert.ToHexString(SHA256.HashData(data)), null);
    private static object Release(string version, bool draft = false, bool prerelease = false, bool hash = true, bool sidecar = true, string host = "github.com") => new
    {
        tag_name = "v" + version, draft, prerelease, html_url = $"https://github.com/yowynn/FloatCanvas/releases/tag/v{version}", body = "Release notes",
        assets = new[] { new { name = $"FloatCanvas-{version}-win-x64.zip", size = 123, state = "uploaded", digest = hash ? "sha256:" + new string('a', 64) : (string?)null, browser_download_url = $"https://{host}/yowynn/FloatCanvas/releases/download/v{version}/FloatCanvas-{version}-win-x64.zip" } }
            .Concat(sidecar ? new[] { new { name = $"FloatCanvas-{version}-win-x64.zip.sha256", size = 120, state = "uploaded", digest = (string?)null, browser_download_url = Asset(version, ".zip.sha256").ToString() } } : Array.Empty<object>()).ToArray()
    };
    private static string Manifest(string version, string product = "浮绘 FloatCanvas", string rid = "win-x64", string configuration = "Release", bool selfContained = false, string[]? files = null) => files is null ? JsonSerializer.Serialize(new { Product = product, Version = version, RuntimeIdentifier = rid, Configuration = configuration, SelfContained = selfContained }) : JsonSerializer.Serialize(new { Product = product, Version = version, RuntimeIdentifier = rid, Configuration = configuration, SelfContained = selfContained, Files = files });
    private static byte[] Archive(string version, string? manifest = null, (string Path, string Content, int Attributes)[]? extra = null)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (string name in Names)
            {
                var entry = archive.CreateEntry($"FloatCanvas-{version}-win-x64/" + name);
                using var writer = new StreamWriter(entry.Open()); writer.Write(name == "PACKAGE-INFO.json" ? manifest ?? Manifest(version) : "test file " + name);
            }
            foreach (var item in extra ?? Array.Empty<(string, string, int)>())
            {
                var entry = archive.CreateEntry(item.Path); entry.ExternalAttributes = item.Attributes;
                using var writer = new StreamWriter(entry.Open()); writer.Write(item.Content);
            }
        }
        return memory.ToArray();
    }
    private static GitHubUpdateClient DownloadClient(byte[] data, bool sidecar = false, string? checksumFilename = null) => Client(request => request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) && sidecar ? Response(Convert.ToHexString(SHA256.HashData(data)) + "  " + (checksumFilename ?? "FloatCanvas-1.0.0-win-x64.zip")) : Bytes(data));
    private static GitHubUpdateClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) => new(new HttpClient(new MemoryHandler(responder)));
    private static HttpResponseMessage Response(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8) };
    private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    private static HttpResponseMessage Redirect(string location) { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri(location); return response; }
    private static async Task BadDownload(string label, byte[] data, AvailableUpdate update, string directory, Action<string, bool> check, bool sidecar = false)
    {
        using var client = DownloadClient(data, sidecar);
        await Reject<InvalidDataException>(label, () => client.DownloadAsync(update, directory, null, default), check);
    }
    private static async Task Reject<T>(string label, Func<Task> action, Action<string, bool> check) where T : Exception
    {
        try { await action(); check(label, false); }
        catch (T) { check(label, true); }
    }
    private sealed class MemoryHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = responder(request); response.RequestMessage = request; return Task.FromResult(response);
        }
    }
    private sealed class InlineProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress> { public void Report(UpdateProgress value) => report(value); }
}
