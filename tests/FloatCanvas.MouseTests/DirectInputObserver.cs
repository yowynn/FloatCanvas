using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace FloatCanvas.MouseTests;

internal static class DirectInputObserver
{
    public static async Task RunChildAsync(string pipeName, int parentPid, int x, int y, int width, int height)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000);
        if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actualPid) || actualPid != parentPid)
            throw new InvalidOperationException("DirectInput observer pipe server PID does not match its parent.");
        using var reader = new StreamReader(pipe); using var writer = new StreamWriter(pipe) { AutoFlush = true };
        using var window = new ProbeWindow("浮绘：DirectInput 非独占观察窗口", "Background nonexclusive DirectInput observer\nNo Raw Input registration is performed by this fixture.", x + width + 30, y, 650, height, false);
        DirectInputWorker created;
        try { created = await DirectInputWorker.CreateAsync(window.Handle, [(uint)parentPid]); }
        catch (ProbeInitializationException error)
        {
            string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            string command = line is null ? "info" : JsonSerializer.Deserialize<string>(line) ?? "info";
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ObserverReply(command, new ObserverSnapshot { ProcessId = Environment.ProcessId,
                WindowHandle = window.Handle.ToInt64(), Role = "DirectInputBackgroundNonexclusive", Failure = error.ToString(), DirectInput = error.Diagnostic as DirectInputSnapshot,
                CancellationRequested = window.CancellationRequested }, error.Message)));
            return;
        }
        using var device = created;
        window.Message += (id, key, _) => { if (id is 0x10 or 0x211 or 0x231 || (id == 0x100 && key == 27)) device.RequestStop(); };
        while (await reader.ReadLineAsync() is string line)
        {
            string command = JsonSerializer.Deserialize<string>(line) ?? throw new InvalidDataException("Invalid DirectInput observer command.");
            string? error = null;
            try
            {
                if (command == "start") await device.StartAsync(0xA, "inactive"); // BACKGROUND | NONEXCLUSIVE
                else if (command.StartsWith("reset:", StringComparison.Ordinal)) await device.ResetAsync(command[6..]);
                else if (command == "snapshot") await device.EndCaptureAsync();
                else if (command == "stop") { await device.StopAsync("ParentStop"); device.Dispose(); }
                else if (command != "info") error = "Unknown private DirectInput observer command.";
            }
            catch (Exception failure) { error = failure.ToString(); }
            var reply = new ObserverReply(command, new ObserverSnapshot { ProcessId = Environment.ProcessId, WindowHandle = window.Handle.ToInt64(),
                Role = "DirectInputBackgroundNonexclusive", DirectInput = device.Snapshot(), RawInputRegistered = false, CancellationRequested = window.CancellationRequested }, error);
            await writer.WriteLineAsync(JsonSerializer.Serialize(reply));
            if (command == "stop") break;
        }
    }
}
