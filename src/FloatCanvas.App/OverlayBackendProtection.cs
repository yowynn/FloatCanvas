using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Current-launch capability policy. Persisted state is diagnostic, never an availability veto.</summary>
internal sealed class OverlayBackendProtection
{
    private readonly string marker;
    internal OverlayBackendMode Mode { get; }
    internal bool AllowsAutomaticFallback => Mode == OverlayBackendMode.Auto;
    private readonly Func<ProbeResult> probe;
    private bool attempted, enabled, disabled;
    internal string? Failure { get; private set; }
    internal string? Warning { get; private set; }
    internal bool IsDisabled => disabled || Mode == OverlayBackendMode.Desktop;
    private sealed record State(string Phase, string Detail);
    internal readonly record struct ProbeResult(bool Available, string Detail, bool Inconclusive = false);

    internal OverlayBackendProtection(string profile, bool desktopOnly, bool retry,
        Func<ProbeResult>? probe = null)
        : this(profile, desktopOnly ? OverlayBackendMode.Desktop : OverlayBackendMode.Auto, probe) { }

    internal OverlayBackendProtection(string profile, OverlayBackendMode mode, Func<ProbeResult>? probe = null)
    {
        marker = Path.Combine(profile, "overlay-backend-state.json");
        Mode = mode;
        // The legacy retry switch remains accepted; every ordinary launch now revalidates.
        this.probe = probe ?? ProbeProcess;
    }

    internal bool TryEnable(Action enable)
    {
        if (Mode == OverlayBackendMode.Desktop || disabled) return false;
        if (attempted) return enabled;
        attempted = true;
        if (Mode == OverlayBackendMode.Enhanced)
        {
            enable(); enabled = true;
            return true; // Forced selection neither probes nor catches an initialization failure.
        }
        try
        {
            // A past process exit and writable diagnostics do not establish current support.
            WriteState("active", "Probing and initializing the enhanced backend.");
            var result = probe();
            if (!result.Available && !result.Inconclusive) { Disable(result.Detail); return false; }
            if (result.Inconclusive) AddWarning(result.Detail);
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
        if (!AllowsAutomaticFallback) return;
        disabled = true; Failure = detail;
        WriteState("disabled", detail);
    }

    // Call only after the presentation and native input owner have disposed successfully.
    internal void Complete()
    {
        if (!AllowsAutomaticFallback || !enabled || disabled) return;
        try { File.Delete(marker); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { AddWarning(error.Message); }
    }

    private void WriteState(string phase, string detail)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            string temporary = marker + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new State(phase, detail)));
            File.Move(temporary, marker, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { AddWarning("Diagnostic state: " + error.Message); }
    }
    private void AddWarning(string detail) => Warning = Warning is null ? detail : Warning + "\n" + detail;

    internal static bool Recoverable(Exception error) => error is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException);

    internal static bool IsInteropFailure(Exception error) => error is SystemOverlayException or COMException or
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or
        System.ComponentModel.Win32Exception;

    private static ProbeResult ProbeProcess()
    {
        // Always run the actual apphost, including when invoked from a test assembly.
        string executable = Path.Combine(AppContext.BaseDirectory, "FloatCanvas.exe");
        string managedHost = Path.Combine(AppContext.BaseDirectory, "FloatCanvas.Host.exe");
        if (File.Exists(managedHost)) executable = managedHost;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--probe-system-overlay");
        return ProbeProcess(start, 5000);
    }

    internal static ProbeResult ProbeProcess(ProcessStartInfo start, int timeoutMilliseconds)
    {
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Overlay probe did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                process.Kill(); process.WaitForExit(1000);
                return new(false, "Enhanced backend probe timed out; trying actual initialization.", true);
            }
            output.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? new(true, "Available") :
                new(false, "Enhanced backend probe failed: " + process.ExitCode + " " + error.GetAwaiter().GetResult());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return new(false, "Isolated probe could not run; trying actual initialization: " + error.Message, true); }
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
