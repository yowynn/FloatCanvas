using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace FloatCanvas.App;

/// <summary>Small, best-effort local diagnostic files. Callers supply state metadata, never document or input content.</summary>
internal sealed class ApplicationDiagnostics
{
    internal const string FileName = "diagnostics.log";
    internal const long DefaultMaximumBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly string Version = typeof(ApplicationDiagnostics).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    private readonly string profile;
    private readonly long maximumBytes;
    private string? actualLogPath;

    internal ApplicationDiagnostics(string profileDirectory, long maximumBytes = DefaultMaximumBytes)
    {
        profile = Path.GetFullPath(profileDirectory);
        if (maximumBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.maximumBytes = maximumBytes;
    }

    internal string LogPath => actualLogPath ?? Path.Combine(profile, FileName);

    /// <summary>Returns false on any logging failure. No alternative profile or directory is ever selected.</summary>
    internal bool Write(string eventName, object? metadata = null, Exception? exception = null, Guid? requestId = null) =>
        WriteCore(FileName, eventName, metadata, exception, requestId);

    internal bool WriteUnhandled(Exception exception) => WriteCore("errors.log", "App.UnhandledException", null, exception, null);

    private bool WriteCore(string name, string eventName, object? metadata, Exception? exception, Guid? requestId)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(profile);
                string path = Path.Combine(profile, name);
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read | FileShare.Delete);
                string? actual = ResolvePath(stream.SafeFileHandle);
                if (name == FileName && actual is not null) actualLogPath = actual;
                string? actualProfile = actual is null ? null : Path.GetDirectoryName(actual);
                var record = new DiagnosticRecord(DateTimeOffset.UtcNow, Environment.ProcessId, Version,
                    profile, actualProfile, eventName, requestId, metadata,
                    exception is null ? null : new(exception.GetType().FullName ?? exception.GetType().Name,
                        Limit(exception.Message, 4096), Limit(exception.ToString(), 32768)));
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
                if (bytes.Length + 1 > maximumBytes)
                {
                    // Keep each line valid JSON and bounded rather than writing a partial record.
                    record = record with { Metadata = null, Exception = null, EntryTruncated = true };
                    bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
                    if (bytes.Length + 1 > maximumBytes) return false;
                }
                if (stream.Length + bytes.Length + 1 > maximumBytes)
                {
                    stream.Dispose();
                    Rotate(path);
                    using var fresh = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
                    fresh.Write(bytes); fresh.WriteByte((byte)'\n');
                }
                else { stream.Seek(0, SeekOrigin.End); stream.Write(bytes); stream.WriteByte((byte)'\n'); }
                return true;
            }
        }
        catch { return false; } // Diagnostic IO/serialization must never alter an application or exception path.
    }

    private static void Rotate(string path)
    {
        if (File.Exists(path + ".2")) File.Delete(path + ".2");
        if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2");
        if (File.Exists(path)) File.Move(path, path + ".1");
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length] + " [truncated]";

    private static string? ResolvePath(SafeFileHandle handle)
    {
        try
        {
            var buffer = new StringBuilder(512);
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) return null;
            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) return null;
            }
            string path = buffer.ToString();
            return path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] :
                path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }
        catch { return null; }
    }

    private sealed record DiagnosticException(string Type, string Message, string Stack);
    private sealed record DiagnosticRecord(DateTimeOffset AtUtc, int ProcessId, string Version,
        string RequestedProfile, string? ActualProfile, string Event, Guid? RequestId,
        object? Metadata, DiagnosticException? Exception, bool EntryTruncated = false);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
