namespace FloatCanvas.Storage;

internal static class FileLease
{
    internal static async Task<FileStream> AcquireAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }
    }
}
