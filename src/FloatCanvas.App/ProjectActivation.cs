using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FloatCanvas.App;

// The existing profile mutex chooses the owner. This pipe transfers project paths to it.
internal sealed class ProjectActivation : IDisposable
{
    private readonly string _name;
    private readonly Func<string, Task<bool>> _receive;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;
    private NamedPipeServerStream? _server;

    internal ProjectActivation(string profileId, Action<string> receive)
        : this(profileId, path => { receive(path); return Task.FromResult(true); }) { }

    internal ProjectActivation(string profileId, Func<string, Task<bool>> receive)
    {
        _name = PipeName(profileId);
        _receive = receive;
        _listener = ListenAsync();
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _server = server;
            try
            {
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                byte[] header = new byte[4];
                await server.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                int length = BitConverter.ToInt32(header);
                if (length is <= 0 or > 65536) continue;
                byte[] payload = new byte[length];
                await server.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
                string path = ProjectLaunchRequest.NormalizePath(new UTF8Encoding(false, true).GetString(payload));
                bool accepted = await _receive(path).WaitAsync(timeout.Token).ConfigureAwait(false);
                await server.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ArgumentException or InvalidOperationException) { }
            finally { _server = null; }
        }
    }

    internal static async Task<bool> ForwardAsync(string profileId, string path, int timeoutMilliseconds = 4000)
    {
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(profileId), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out uint processId)) AllowSetForegroundWindow(processId);
            byte[] payload = Encoding.UTF8.GetBytes(ProjectLaunchRequest.NormalizePath(path));
            await client.WriteAsync(BitConverter.GetBytes(payload.Length), timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            byte[] response = new byte[1];
            await client.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(false);
            return response[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _server?.Dispose();
        // Keep the token source alive until the cancelled listener has unwound.
        _ = _listener.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }

    private static string PipeName(string profileId) => "FloatCanvas.Open." + System.Diagnostics.Process.GetCurrentProcess().SessionId + "." + profileId;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint processId);
}
