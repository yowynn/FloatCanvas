using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

/// <summary>Independent WinForms auxiliary surfaces using synthetic pixels outside
/// all physical monitors. Never starts a user's capture program or captures the desktop.</summary>
internal sealed class ScreenshotPeerFixture : IDisposable
{
    private readonly Process _process;
    internal nint Handle { get; }
    private ScreenshotPeerFixture(Process process, nint handle) { _process = process; Handle = handle; }

    internal static ScreenshotPeerFixture Start(string mode, string name = "ShareX")
    {
        string executable = Path.Combine(AppContext.BaseDirectory, name + ".exe");
        if (!File.Exists(executable)) File.Copy(Path.ChangeExtension(typeof(ScreenshotPeerFixture).Assembly.Location, ".exe"), executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true };
        start.ArgumentList.Add("--screenshot-peer"); start.ArgumentList.Add(mode);
        var process = Process.Start(start)!;
        try
        {
            var ready = process.StandardOutput.ReadLineAsync();
            if (!ready.Wait(TimeSpan.FromSeconds(10)) || !long.TryParse(ready.Result, out long handle))
                throw new InvalidOperationException("Screenshot peer did not publish its HWND.");
            return new(process, (nint)handle);
        }
        catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
    }

    internal static void Run(string mode)
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        using var form = new PeerForm { MainSurface = mode == "main", ClickThrough = mode == "transparent", Text = mode switch { "localized" => "未命名工具 - 区域截图", "fullscreen-editor" => "Arbitrary editor - 640x360", _ => "Arbitrary temporary surface" },
            StartPosition = Forms.FormStartPosition.Manual, ShowInTaskbar = false,
            FormBorderStyle = mode == "bordered" ? Forms.FormBorderStyle.Sizable : Forms.FormBorderStyle.None,
            TopMost = mode != "notopmost", Bounds = new(-20000, -20000, mode == "small" ? 320 : 640, 360) };
        form.Show();
        // Apply the native flag before publishing readiness; WinForms can defer its
        // TopMost property until the message pump starts on an isolated desktop.
        if (!SetWindowPos(form.Handle, mode == "notopmost" ? -2 : -1, 0, 0, 0, 0, 0x13))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if ((((long)GetWindowLongPtrW(form.Handle, -20) & 8) != 0) != (mode != "notopmost"))
            throw new InvalidOperationException("Screenshot peer has not acquired its requested native topmost state.");
        Console.WriteLine((long)form.Handle); Console.Out.Flush();
        Forms.Application.Run(form);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            PostMessage(Handle, 0x10, 0, 0);
            if (!_process.WaitForExit(5000)) { _process.Kill(); _process.WaitForExit(); }
        }
        _process.Dispose();
    }

    private sealed class PeerForm : Forms.Form
    {
        internal bool MainSurface;
        internal bool ClickThrough;
        protected override bool ShowWithoutActivation => true;
        protected override Forms.CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000080; if (MainSurface) value.ExStyle = (value.ExStyle & ~0x80) | 0x40000; if (ClickThrough) value.ExStyle |= 0x20; return value; }
        }
    }
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
