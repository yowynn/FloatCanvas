using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FloatCanvas.App;

internal sealed record AvailableUpdate(string Version, string Notes, Uri ReleaseUrl, Uri PackageUrl, long Size, string? Sha256, Uri? ChecksumUrl);
internal sealed record UpdateCheckResult(AvailableUpdate? Update, string? CurrentNotes);
internal sealed record UpdateProgress(long ReceivedBytes, long TotalBytes);
internal sealed record PreparedUpdate(string PackageDirectory, string Version, IReadOnlyList<string> Files);

/// <summary>Public releases only. Downloads and validates into a private staging directory, never into the running application.</summary>
internal sealed class GitHubUpdateClient : IDisposable
{
    internal static readonly Uri ProjectUrl = new("https://github.com/yowynn/FloatCanvas");
    internal static readonly Uri ChangelogUrl = new("https://github.com/yowynn/FloatCanvas/blob/main/CHANGELOG.md");
    private const long MaxPackageBytes = 128L * 1024 * 1024, MaxExpandedBytes = 512L * 1024 * 1024;
    private const int MaxMetadataBytes = 4 * 1024 * 1024, MaxEntries = 5000;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    internal GitHubUpdateClient(HttpClient? client = null)
    {
        ownsClient = client is null;
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken, bool includePrereleases = false)
    {
        if (!SemVersion.TryParse(currentVersion, out var current)) throw new FormatException("The current application version is not a valid semantic version.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            AvailableUpdate? best = null; SemVersion? bestVersion = null; string? currentNotes = null;
            for (int page = 1; page <= 10; page++)
            {
                var uri = new Uri($"https://api.github.com/repos/yowynn/FloatCanvas/releases?per_page=100&page={page}");
                byte[] bytes = await ReadSmallAsync(uri, MaxMetadataBytes, false, timeout.Token).ConfigureAwait(false);
                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub returned an invalid releases list.");
                int count = 0;
                foreach (var release in json.RootElement.EnumerateArray())
                {
                    count++;
                    if (release.ValueKind != JsonValueKind.Object || Boolean(release, "draft")) continue;
                    string tag = Text(release, "tag_name");
                    string value = tag.StartsWith('v') ? tag[1..] : tag;
                    if (!SemVersion.TryParse(value, out var version)) continue;
                    if (version!.CompareTo(current) == 0 && currentNotes is null) currentNotes = Text(release, "body");
                    if (version.CompareTo(current) <= 0 || (!includePrereleases && (version.IsPrerelease || Boolean(release, "prerelease"))) ||
                        version.CompareTo(bestVersion) <= 0) continue;
                    string filename = PackageName(value) + ".zip";
                    if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
                    var packages = assets.EnumerateArray().Where(a => Text(a, "name") == filename && Text(a, "state") is "uploaded" or "").ToArray();
                    if (packages.Length == 0) continue;
                    if (packages.Length != 1) throw new InvalidDataException("The release contains duplicate update packages.");
                    var package = packages[0];
                    long size = package.TryGetProperty("size", out var sizeValue) && sizeValue.ValueKind == JsonValueKind.Number && sizeValue.TryGetInt64(out var n) ? n : 0;
                    if (size <= 0 || size > MaxPackageBytes) throw new InvalidDataException("The update package exceeds the supported size limit.");
                    string? hash = Digest(Text(package, "digest"));
                    var sums = assets.EnumerateArray().Where(a => Text(a, "name") == filename + ".sha256").ToArray();
                    if (sums.Length > 1) throw new InvalidDataException("The release contains duplicate package checksums.");
                    if (hash is null && sums.Length == 0) continue;
                    Uri packageUrl = RepositoryUri(Text(package, "browser_download_url"), $"/releases/download/{tag}/{filename}");
                    Uri releaseUrl = RepositoryUri(Text(release, "html_url"), $"/releases/tag/{tag}");
                    Uri? checksum = sums.Length == 0 ? null : RepositoryUri(Text(sums[0], "browser_download_url"), $"/releases/download/{tag}/{filename}.sha256");
                    best = new AvailableUpdate(value, Text(release, "body"), releaseUrl, packageUrl, size, hash, checksum);
                    bestVersion = version;
                }
                if (count < 100) break;
            }
            return new(best, currentNotes);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Checking GitHub releases timed out. Please try again.", ex); }
        catch (JsonException ex) { throw new InvalidDataException("GitHub returned invalid release metadata.", ex); }
    }

    internal async Task<PreparedUpdate> DownloadAsync(AvailableUpdate update, string cacheDirectory, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        if (!SemVersion.TryParse(update.Version, out _)) throw new InvalidDataException("The update version is invalid.");
        string packageName = PackageName(update.Version), filename = packageName + ".zip";
        ValidateDownloadUri(update.PackageUrl, filename);
        if (update.ChecksumUrl is not null) ValidateDownloadUri(update.ChecksumUrl, filename + ".sha256");
        if (update.Size <= 0 || update.Size > MaxPackageBytes || (update.Sha256 is null && update.ChecksumUrl is null)) throw new InvalidDataException("The release does not contain a supported package and checksum.");
        string? digest = update.Sha256 is null ? null : ValidHash(update.Sha256);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        string cache = Path.GetFullPath(cacheDirectory);
        AssertNoReparseAncestors(cache); Directory.CreateDirectory(cache);
        string staging = Path.Combine(cache, "download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        bool complete = false;
        try
        {
            if (update.ChecksumUrl is not null)
            {
                string sumText = Encoding.UTF8.GetString(await ReadSmallAsync(update.ChecksumUrl, 8192, true, timeout.Token).ConfigureAwait(false));
                string sidecar = ReadChecksum(sumText, filename);
                if (digest is not null && !string.Equals(digest, sidecar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("GitHub and the release checksum disagree.");
                digest = sidecar;
            }
            string zipPath = Path.Combine(staging, "package.zip");
            using (var response = await GetAsync(update.PackageUrl, true, timeout.Token).ConfigureAwait(false))
            {
                long? length = response.Content.Headers.ContentLength;
                if (length is not null && length != update.Size) throw new InvalidDataException("The update package size does not match its release metadata.");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[81920]; long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    received += read;
                    if (received > update.Size || received > MaxPackageBytes) throw new InvalidDataException("The download exceeds the expected package size.");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    progress?.Report(new UpdateProgress(received, update.Size));
                }
                if (received != update.Size) throw new InvalidDataException("The update package download is incomplete.");
                string actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actual, digest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update package checksum is incorrect. No application files were changed.");
            }
            string directory = Path.Combine(staging, packageName);
            var files = await ExtractAsync(zipPath, staging, packageName, timeout.Token).ConfigureAwait(false);
            ValidateManifest(directory, update.Version, files);
            File.Delete(zipPath); complete = true;
            return new PreparedUpdate(directory, update.Version, files.AsReadOnly());
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Downloading the update timed out. Please try again.", ex); }
        finally { if (!complete) { try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, bool asset, CancellationToken cancellationToken)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (!AllowedRequest(uri, asset)) throw new InvalidDataException("The update URL is outside the project's GitHub release service.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("FloatCanvas-Updater/1.0");
            request.Headers.Accept.ParseAdd(asset ? "application/octet-stream" : "application/vnd.github+json");
            if (!asset) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is Uri final && !AllowedRequest(final, asset)) { response.Dispose(); throw new InvalidDataException("The update redirected outside GitHub's download hosts."); }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirects == 5) throw new HttpRequestException("GitHub returned an invalid or excessive redirect chain.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                string message = status switch
                {
                    HttpStatusCode.Unauthorized => "GitHub denied access to the public release. No sign-in token is required; please try again later.",
                    HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub temporarily limited update requests. Please try again later.",
                    HttpStatusCode.NotFound => "The GitHub release or update package could not be found.",
                    _ => $"GitHub update request failed (HTTP {(int)status})."
                };
                response.Dispose(); throw new HttpRequestException(message, null, status);
            }
            return response;
        }
        throw new HttpRequestException("GitHub returned too many redirects.");
    }

    private async Task<byte[]> ReadSmallAsync(Uri uri, int limit, bool asset, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(uri, asset, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The update metadata exceeds the supported size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(); byte[] buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("The update metadata exceeds the supported size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task<List<string>> ExtractAsync(string zipPath, string staging, string packageName, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count is 0 or > MaxEntries) throw new InvalidDataException("The update archive contains an invalid number of entries.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>(); long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = entry.FullName;
            bool isDirectory = path.EndsWith('/');
            string trimmed = isDirectory ? path[..^1] : path;
            string[] parts = trimmed.Split('/');
            if (path.Length > 1024 || parts.Length is 0 or > 20 || parts[0] != packageName || (!isDirectory && parts.Length < 2) || parts.Any(p => !SafeSegment(p)) || path.Contains('\\') || !paths.Add(trimmed)) throw new InvalidDataException("The update archive contains an unsafe or duplicate path.");
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((unixType != 0 && unixType != (isDirectory ? 0x4000 : 0x8000)) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The update archive contains an unsupported link or special file.");
            if (isDirectory) { if (entry.Length != 0) throw new InvalidDataException("An update directory contains file data."); continue; }
            if (entry.Length <= 0 && parts[^1] is "FloatCanvas.exe" or "FloatCanvas.dll") throw new InvalidDataException("The update archive contains an empty application file.");
            if (entry.Length > MaxPackageBytes || entry.Length < 0 || expanded > MaxExpandedBytes - entry.Length) throw new InvalidDataException("The expanded update exceeds the supported size limit.");
            expanded += entry.Length;
            string destination = Path.GetFullPath(Path.Combine(staging, Path.Combine(parts)));
            if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update archive escapes its staging directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            byte[] buffer = new byte[81920]; long written = 0; int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                written += read;
                if (written > entry.Length) throw new InvalidDataException("The update archive has inconsistent expanded lengths.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (written != entry.Length) throw new InvalidDataException("An update file is incomplete.");
            files.Add(string.Join('/', parts.Skip(1)));
        }
        return files;
    }

    private static void ValidateManifest(string directory, string version, List<string> files)
    {
        foreach (string required in new[] { "PACKAGE-INFO.json", "FloatCanvas.exe", "FloatCanvas.dll", "FloatCanvas.deps.json", "FloatCanvas.runtimeconfig.json" })
            if (!files.Contains(required, StringComparer.Ordinal) || new FileInfo(Path.Combine(directory, required)).Length == 0) throw new InvalidDataException("The update package is missing " + required + ".");
        string manifest = Path.Combine(directory, "PACKAGE-INFO.json");
        if (new FileInfo(manifest).Length > MaxMetadataBytes) throw new InvalidDataException("The package manifest exceeds the supported size limit.");
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(manifest), new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (Text(root, "DistributionTrust") == "UnsignedValidation") throw new InvalidDataException("A local validation package cannot be installed as a release.");
            if (Text(root, "Product") != "浮绘 FloatCanvas" || Text(root, "Version") != version || Text(root, "RuntimeIdentifier") != "win-x64" ||
                Text(root, "Configuration") != "Release" || !root.TryGetProperty("SelfContained", out var contained) || contained.ValueKind != JsonValueKind.False)
                throw new InvalidDataException("The update package manifest does not match this product, version, architecture or release configuration.");
            if (root.TryGetProperty("Files", out var listed))
            {
                if (listed.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The package file list is invalid.");
                var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in listed.EnumerateArray())
                {
                    string? file = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (file is null || file.Contains('\\') || file.Split('/').Any(p => !SafeSegment(p)) || !declared.Add(file)) throw new InvalidDataException("The package file list contains invalid or duplicate entries.");
                }
                if (!declared.SetEquals(files)) throw new InvalidDataException("The package file list does not cover the complete update.");
            }
        }
        catch (JsonException ex) { throw new InvalidDataException("The update package manifest is invalid.", ex); }
    }

    private static bool SafeSegment(string value)
    {
        if (value.Length is 0 or > 200 || value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ') || value.Any(c => c < 32 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\\')) return false;
        string name = value.Split('.')[0];
        return !Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    private static string ReadChecksum(string text, string filename)
    {
        var match = Regex.Match(text.Trim('\uFEFF', '\r', '\n', ' ', '\t'), @"^([0-9a-fA-F]{64})[ \t]+\*?([^\r\n]+)$", RegexOptions.CultureInvariant);
        if (!match.Success || match.Groups[2].Value != filename) throw new InvalidDataException("The release checksum is not bound to the expected package filename.");
        return ValidHash(match.Groups[1].Value);
    }
    private static string? Digest(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!value.StartsWith("sha256:", StringComparison.Ordinal)) return null;
        return ValidHash(value[7..]);
    }
    private static string ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : throw new InvalidDataException("The release SHA256 digest is invalid.");
    private static string PackageName(string version) => "FloatCanvas-" + version + "-win-x64";
    private static string Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Boolean(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool Https(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
    private static Uri RepositoryUri(string value, string suffix)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !Https(uri) || uri.Host != "github.com" || Uri.UnescapeDataString(uri.AbsolutePath) != "/yowynn/FloatCanvas" + suffix || !string.IsNullOrEmpty(uri.Query)) throw new InvalidDataException("The release points outside the project's GitHub repository.");
        return uri;
    }
    private static void ValidateDownloadUri(Uri uri, string filename)
    {
        if (!Https(uri) || uri.Host != "github.com" || !string.IsNullOrEmpty(uri.Query)) throw new InvalidDataException("The update package URL is invalid.");
        string[] parts = Uri.UnescapeDataString(uri.AbsolutePath).Split('/');
        if (parts.Length != 7 || parts[1] != "yowynn" || parts[2] != "FloatCanvas" || parts[3] != "releases" || parts[4] != "download" || !SemVersion.TryParse(parts[5].StartsWith('v') ? parts[5][1..] : parts[5], out _) || parts[6] != filename || !filename.StartsWith(PackageName(parts[5].StartsWith('v') ? parts[5][1..] : parts[5]) + ".zip", StringComparison.Ordinal)) throw new InvalidDataException("The update package URL is outside the project release.");
    }
    private static bool AllowedRequest(Uri uri, bool asset)
    {
        if (!Https(uri)) return false;
        if (!asset) return uri.Host == "api.github.com" && uri.AbsolutePath == "/repos/yowynn/FloatCanvas/releases";
        return uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/yowynn/FloatCanvas/releases/download/", StringComparison.Ordinal) ||
            uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com";
    }
    private static void AssertNoReparseAncestors(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The update cache must not contain symbolic links or junctions.");
    }
    public void Dispose() { if (ownsClient) client.Dispose(); }
}
