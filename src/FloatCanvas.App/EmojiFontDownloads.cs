using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class EmojiFontDownloads
{
    internal const string LocalSourceArgument = "--emoji-font-download-base";
    private const int MaximumFontBytes = 16 * 1024 * 1024;
    private static readonly Uri ReleaseSource = new(GitHubUpdateClient.ReleasesUrl + "/download/v0.4.2/");
    private static readonly GitHubUpdateClient ReleaseClient = new();
    private static readonly HttpClient LocalClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
    private static readonly SemaphoreSlim InstallGate = new(1);
    private static Uri? localSource;

    internal static void Configure(string[] args)
    {
        int index = Array.IndexOf(args, LocalSourceArgument);
        localSource = null;
        if (index < 0) return;
        if (index + 1 >= args.Length || !Uri.TryCreate(args[index + 1].TrimEnd('/') + "/", UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttp || url.Host != "127.0.0.1" || url.UserInfo.Length != 0 ||
            url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new ArgumentException(L.Get("Emoji.Font.InvalidLocalSource"));
        localSource = url;
    }

    internal static bool CanDownload(string fontId) =>
        fontId is EmojiFontIds.NotoColor or EmojiFontIds.OpenMojiColor or EmojiFontIds.BlobmojiColor;

    internal static Uri DownloadUri(string fontId)
    {
        if (!CanDownload(fontId)) throw new InvalidOperationException(L.Get("Emoji.Font.NoDownload"));
        return new Uri(localSource ?? ReleaseSource, Uri.EscapeDataString(Path.GetFileName(EmojiFontSources.PathFor(fontId))));
    }

    internal static async Task InstallAsync(string fontId)
    {
        var url = DownloadUri(fontId);
        string path = EmojiFontSources.PathFor(fontId);
        await InstallGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (EmojiFontSources.IsAvailable(fontId)) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var token = timeout.Token;
            using var response = url.IsLoopback
                ? await LocalClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false)
                : await ReleaseClient.GetReleaseAssetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumFontBytes)
                throw new InvalidDataException(L.Get("Emoji.Font.InvalidDownload"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, useAsync: true))
                {
                    byte[] buffer = new byte[65_536];
                    int total = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    {
                        total += count;
                        if (total > MaximumFontBytes) throw new InvalidDataException(L.Get("Emoji.Font.InvalidDownload"));
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }
                }
                if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), EmojiBoundsProfile.For(fontId).ExpectedSha256, StringComparison.Ordinal))
                    throw new InvalidDataException(L.Get("Emoji.Font.InvalidDownload"));
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            EmojiFontSources.Refresh();
        }
        catch (HttpRequestException ex) when (!url.IsLoopback && ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HttpRequestException(L.Get("Emoji.Font.ReleaseAssetMissing"), ex, ex.StatusCode);
        }
        finally { InstallGate.Release(); }
    }
}
