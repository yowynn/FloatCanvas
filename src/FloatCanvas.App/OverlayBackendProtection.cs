using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Optional backend policy. A durable marker also protects the next launch from native crashes.</summary>
internal sealed class OverlayBackendProtection
{
    private readonly string marker;
    private readonly bool desktopOnly, retry;
    private readonly Func<(bool Available, string Detail)> probe;
    private bool attempted, enabled, disabled;
    internal string? Failure { get; private set; }
    internal bool IsDisabled => disabled || desktopOnly;
    private sealed record State(string Phase, string Detail);

    internal OverlayBackendProtection(string profile, bool desktopOnly, bool retry,
        Func<(bool Available, string Detail)>? probe = null)
    {
        marker = Path.Combine(profile, "overlay-backend-state.json");
        this.desktopOnly = desktopOnly; this.retry = retry;
        this.probe = probe ?? ProbeProcess;
    }

    internal bool TryEnable(Action enable)
    {
        if (desktopOnly || disabled) return false;
        if (attempted) return enabled;
        attempted = true;
        try
        {
            if (File.Exists(marker) && !retry)
            {
                Disable("Previous enhanced session did not finish cleanly, or the backend was disabled.");
                return false;
            }
            // This must reach disk before loading any uncertain native ABI, even in the probe.
            WriteState("active", "Probing and initializing the enhanced backend.");
            var result = probe();
            if (!result.Available) { Disable(result.Detail); return false; }
            enable(); enabled = true;
            return true;
        }
        catch (Exception error) when (Recoverable(error))
        {
            Disable(error.Message);
            return false;
        }
    }

    internal void Disable(string detail)
    {
        disabled = true; Failure = detail;
        try { WriteState("disabled", detail); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Failure += " (Safety marker: " + error.Message + ")"; }
    }

    // Call only after the presentation and native input owner have disposed successfully.
    internal void Complete()
    {
        if (!enabled || disabled) return;
        try { File.Delete(marker); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Failure = error.Message; }
    }

    private void WriteState(string phase, string detail)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        string temporary = marker + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new State(phase, detail)));
        File.Move(temporary, marker, true);
    }

    internal static bool Recoverable(Exception error) => error is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException);

    internal static bool IsInteropFailure(Exception error) => error is SystemOverlayException or COMException or
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or
        System.ComponentModel.Win32Exception;

    private static (bool Available, string Detail) ProbeProcess()
    {
        // Always run the actual apphost, including when invoked from a test assembly.
        string executable = Path.Combine(AppContext.BaseDirectory, "FloatCanvas.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--probe-system-overlay");
        return ProbeProcess(start, 5000);
    }

    internal static (bool Available, string Detail) ProbeProcess(ProcessStartInfo start, int timeoutMilliseconds)
    {
        using var process = Process.Start(start) ?? throw new IOException("Overlay probe did not start.");
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill(); process.WaitForExit(1000);
            return (false, "Enhanced backend probe timed out.");
        }
        return process.ExitCode == 0 ? (true, "Available") :
            (false, "Enhanced backend probe failed: " + process.ExitCode + " " + process.StandardError.ReadToEnd());
    }

    internal static int RunProbe()
    {
        // Do not display Windows fault dialogs if the ABI itself crashes this isolated process.
        SetErrorMode(0x0001 | 0x0002);
        try { OverlayHost.ProbeSystemInput(); return 0; }
        catch (Exception error) when (Recoverable(error))
        { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
    }

    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
}
