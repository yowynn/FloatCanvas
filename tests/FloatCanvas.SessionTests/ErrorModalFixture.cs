using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.PlatformTests;
using FloatCanvas.Windows;
using Wpf = System.Windows;

namespace FloatCanvas.SessionTests;

/// <summary>Observes only this thread's owned error dialog; never injects input or activates a foreign window.</summary>
internal static class ErrorModalFixture
{
    internal const string Message = "浮绘自建错误提示验收：仅关闭本进程所属对话框。";
    public static void Observe(Action showError, Wpf.Window toolbar, OverlayHost host, ErrorModalEvidence evidence, Action<string> fatal)
    {
        var owner = new WindowInteropHelper(toolbar).Handle;
        evidence.OwnerHandle = owner.ToInt64(); evidence.ProcessId = Environment.ProcessId;
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(25) };
        var elapsed = Stopwatch.StartNew();
        long firstSeen = -1;
        bool closeRequested = false;
        timer.Tick += (_, _) =>
        {
            // This callback runs in MessageBox's native nested dispatcher pump.
            var candidates = new List<nint>();
            ModalNative.EnumThreadWindows(ModalNative.GetCurrentThreadId(), (hwnd, _) =>
            {
                if (IsOwnedDialog(hwnd, owner)) candidates.Add(hwnd);
                return true;
            }, 0);
            evidence.ElapsedMilliseconds = elapsed.ElapsedMilliseconds;
            if (candidates.Count == 1)
            {
                nint dialog = candidates[0];
                if (firstSeen < 0) firstSeen = elapsed.ElapsedMilliseconds;
                var controls = new List<nint>();
                ModalNative.EnumChildWindows(dialog, (child, _) =>
                {
                    if (OwnProcess(child)) controls.Add(child);
                    return true;
                }, 0);
                nint ok = ModalNative.GetDlgItem(dialog, 1);
                if (ok == 0)
                {
                    var buttons = controls.Where(child => ClassName(child) == "Button").ToArray();
                    if (buttons.Length == 1) ok = buttons[0];
                }
                evidence.DialogHandle = dialog.ToInt64();
                evidence.OkHandle = ok.ToInt64();
                evidence.Visible = Native.IsWindowVisible(dialog);
                evidence.Enabled = ModalNative.IsWindowEnabled(dialog);
                evidence.OwnerDisabled = !ModalNative.IsWindowEnabled(owner);
                evidence.ForegroundHandle = Native.GetForegroundWindow().ToInt64();
                evidence.IsForeground = Native.GetForegroundWindow() == dialog;
                evidence.ExtendedStyle = Native.ExtendedStyle(dialog).ToInt64();
                evidence.Topmost = (evidence.ExtendedStyle & 8) != 0;
                evidence.OkIsOwnedEnabledVisible = ok != 0 && OwnProcess(ok) && ModalNative.GetParent(ok) == dialog
                    && ModalNative.IsWindowEnabled(ok) && Native.IsWindowVisible(ok);
                evidence.OkParent = ModalNative.GetParent(ok).ToInt64();
                evidence.OkEnabled = ModalNative.IsWindowEnabled(ok); evidence.OkVisible = Native.IsWindowVisible(ok);
                evidence.ChildControls.Clear();
                foreach (nint child in controls)
                {
                    evidence.ChildControls.Add($"{child.ToInt64()}:id={ModalNative.GetDlgCtrlID(child)}:class={ClassName(child)}:parent={ModalNative.GetParent(child).ToInt64()}:enabled={ModalNative.IsWindowEnabled(child)}:visible={Native.IsWindowVisible(child)}:text={WindowText(child)}");
                }
                evidence.HasExpectedMessage = controls.Any(child => WindowText(child) == Message);
                evidence.DialogTitle = WindowText(dialog);
                evidence.ButtonText = WindowText(ok);
                evidence.ButtonCommandId = ok == 0 ? 0 : ModalNative.GetDlgCtrlID(ok);
                evidence.ButtonIsConfirm = evidence.ButtonText?.Replace("&", "", StringComparison.Ordinal).Trim() is "确定" or "OK";
                var overlayHandles = host.Diagnostics.SelectMany(d => new[] { d.DisplayHandle, d.InputHandle }).Append(owner).ToHashSet();
                evidence.VisibleOverlayAboveDialog = HasVisibleOverlayAbove(dialog, overlayHandles);
                evidence.OkRootAtPhysicalCenter = evidence.OkIsOwnedEnabledVisible && OwnsPhysicalOkCenter(dialog, ok);

                // Let normal activation settle, but never call SetForegroundWindow to manufacture proof.
                if (closeRequested || elapsed.ElapsedMilliseconds - firstSeen < 150
                    || !evidence.IsForeground && elapsed.ElapsedMilliseconds - firstSeen < 1000) return;
                if (!IsOwnedDialog(dialog, owner) || !evidence.OkIsOwnedEnabledVisible || !evidence.HasExpectedMessage
                    || !evidence.ButtonIsConfirm || !evidence.OkRootAtPhysicalCenter || evidence.ButtonCommandId <= 0)
                {
                    evidence.Failure = "The self-owned error dialog no longer has a safe confirmation-button command target.";
                    timer.Stop(); fatal(evidence.Failure); return;
                }
                closeRequested = true;
                evidence.CloseCommandPosted = ModalNative.PostMessage(dialog, 0x111, (nuint)evidence.ButtonCommandId, ok);
                if (!evidence.CloseCommandPosted)
                {
                    evidence.Failure = "Posting its actual confirmation-button command to the self-owned dialog failed.";
                    timer.Stop(); fatal(evidence.Failure);
                }
            }
            if (elapsed.Elapsed > TimeSpan.FromSeconds(6))
            {
                evidence.Failure = "No safe, owner-matched modal completion within six seconds; only the test process will terminate.";
                timer.Stop(); fatal(evidence.Failure);
            }
        };
        timer.Start();
        try { showError(); }
        finally { timer.Stop(); }
        evidence.Returned = true;
        evidence.DialogClosed = evidence.DialogHandle != 0 && !ModalNative.IsWindow(new nint(evidence.DialogHandle));
        evidence.OwnerEnabledAfter = ModalNative.IsWindowEnabled(owner);
    }

    private static bool IsOwnedDialog(nint hwnd, nint owner)
    {
        if (!OwnProcess(hwnd) || ModalNative.GetWindow(hwnd, 4) != owner) return false;
        var name = new StringBuilder(64);
        return ModalNative.GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString() == "#32770";
    }
    private static bool OwnProcess(nint hwnd) => ModalNative.GetWindowThreadProcessId(hwnd, out uint pid) != 0 && pid == Environment.ProcessId;
    private static string ClassName(nint hwnd)
    {
        var name = new StringBuilder(64); ModalNative.GetClassName(hwnd, name, name.Capacity); return name.ToString();
    }
    private static string WindowText(nint hwnd)
    {
        if (hwnd == 0 || !OwnProcess(hwnd)) return "";
        var text = new StringBuilder(1024); ModalNative.GetWindowText(hwnd, text, text.Capacity); return text.ToString();
    }
    private static bool HasVisibleOverlayAbove(nint dialog, HashSet<nint> overlayHandles)
    {
        for (nint above = ModalNative.GetWindow(dialog, 3); above != 0; above = ModalNative.GetWindow(above, 3))
            if (overlayHandles.Contains(above) && Native.IsWindowVisible(above)) return true;
        return false;
    }
    private static bool OwnsPhysicalOkCenter(nint dialog, nint ok)
    {
        nint prior = ModalNative.SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            if (!Native.GetWindowRect(ok, out var bounds)) return false;
            nint at = Native.WindowFromPoint(new((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2));
            return ModalNative.GetAncestor(at, 2) == dialog;
        }
        finally { if (prior != 0) ModalNative.SetThreadDpiAwarenessContext(prior); }
    }
}

internal sealed class ErrorModalEvidence
{
    public int ProcessId { get; set; }
    public long OwnerHandle { get; set; }
    public long DialogHandle { get; set; }
    public long OkHandle { get; set; }
    public long ForegroundHandle { get; set; }
    public long OkParent { get; set; }
    public long ExtendedStyle { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public bool Visible { get; set; }
    public bool Enabled { get; set; }
    public bool OwnerDisabled { get; set; }
    public bool IsForeground { get; set; }
    public bool Topmost { get; set; }
    public bool OkIsOwnedEnabledVisible { get; set; }
    public bool OkEnabled { get; set; }
    public bool OkVisible { get; set; }
    public List<string> ChildControls { get; } = [];
    public bool VisibleOverlayAboveDialog { get; set; }
    public bool OkRootAtPhysicalCenter { get; set; }
    public bool CloseCommandPosted { get; set; }
    public bool Returned { get; set; }
    public bool DialogClosed { get; set; }
    public bool OwnerEnabledAfter { get; set; }
    public string? Failure { get; set; }
    public string? DialogTitle { get; set; }
    public string? ButtonText { get; set; }
    public bool HasExpectedMessage { get; set; }
    public bool ButtonIsConfirm { get; set; }
    public int ButtonCommandId { get; set; }
}

internal static class ModalNative
{
    internal delegate bool EnumWindowProc(nint hwnd, nint lparam);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumThreadWindows(uint threadId, EnumWindowProc callback, nint lparam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumChildWindows(nint parent, EnumWindowProc callback, nint lparam);
    [DllImport("user32.dll")] internal static extern int GetDlgCtrlID(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder buffer, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder buffer, int capacity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetDlgItem(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint hwnd, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint value);
}
