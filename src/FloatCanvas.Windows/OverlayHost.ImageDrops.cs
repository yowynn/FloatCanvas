using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using static FloatCanvas.Windows.NativeMethods;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Windows;

public sealed partial class OverlayHost
{
    private ImageDropRegistrations<NativeImageDropTarget>? imageDropRegistrations;
    private Func<IDataObject, bool>? imageDropSupports;
    private Func<IDataObject, OverlayPoint, bool>? imageDrop;

    /// <summary>A temporary visible input surface for an external drag. It never activates or starts input isolation.</summary>
    public bool IsReceivingDrop { get; private set; }
    /// <summary>True only between an OLE target's DragEnter and DragLeave/Drop, including unsupported data.</summary>
    public bool IsOleDragOver => imageDropRegistrations?.Targets.Any(target => target.IsDragOver) == true;
    /// <summary>Reads the user's primary mouse button, respecting Windows swapped-button preferences without generating input.</summary>
    public static bool IsPrimaryMouseButtonDown =>
        (OleDropNative.GetAsyncKeyState(ImageDropProtocol.PrimaryMouseVirtualKey(OleDropNative.GetSystemMetrics(23) != 0)) & 0x8000) != 0;

    /// <summary>
    /// Enables native OLE drop targets on this host's input HWNDs. Both callbacks run synchronously on the owner STA.
    /// The data object is borrowed: copy any required bounded data before returning; never retain it or await its reads.
    /// Drop points preserve the per-monitor-aware input HWND's signed physical screen coordinates without DPI scaling.
    /// Image decoding and supported formats belong to the caller. Only copy effects can be returned to the source.
    /// </summary>
    public void EnableImageDrops(Func<IDataObject, bool> supports, Func<IDataObject, OverlayPoint, bool> drop)
    {
        CheckThread(); ArgumentNullException.ThrowIfNull(supports); ArgumentNullException.ThrowIfNull(drop);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException(L.Get("ImageDropHost.RequiresSta"));
        if (imageDropRegistrations is not null) { imageDropSupports = supports; imageDrop = drop; return; }
        var registrations = new ImageDropRegistrations<NativeImageDropTarget>(() => OleDropNative.OleInitialize(0),
            OleDropNative.OleUninitialize, (window, target) => OleDropNative.RegisterDragDrop(window, target),
            OleDropNative.RevokeDragDrop, target => target.Clear(), ReportImageDropError);
        imageDropSupports = supports; imageDrop = drop; imageDropRegistrations = registrations;
        try { RegisterImageDropTargets(); }
        catch
        {
            imageDropRegistrations = null; imageDropSupports = null; imageDrop = null;
            registrations.Dispose(); throw;
        }
    }

    /// <summary>Shows a temporary topmost input surface without foreground/focus, mouse capture or keyboard isolation.</summary>
    public bool TryStartDropReception(out string? failure)
    {
        CheckThread(); failure = null;
        if (IsReceivingDrop) return true;
        if (imageDropRegistrations is null || imageDropRegistrations.Targets.Count != _windows.Count || _windows.Count == 0 ||
            IsEditing || IsKeyboardIsolationActive || _captureActive)
        { failure = L.Get("ImageDropHost.Unavailable"); return false; }
        IsReceivingDrop = true;
        try
        {
            foreach (var pair in _windows)
            {
                ShowWindow(pair.Input.Handle, 8); // SW_SHOWNA: external source retains activation and its drag capture.
                if (!SetWindowPos(pair.Input.Handle, HwndTopmost, 0, 0, 0, 0, 0x13))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), L.Get("ImageDropHost.ShowFailed"));
            }
            return true;
        }
        catch (Exception error) { EndDropReceptionCore(); failure = error.Message; return false; }
    }

    /// <summary>Ends only temporary reception and clears hover data; normal editing input visibility is preserved.</summary>
    public void EndDropReception() { CheckThread(); EndDropReceptionCore(); }

    private void EndDropReceptionCore()
    {
        imageDropRegistrations?.ClearHover();
        if (!IsReceivingDrop) return;
        IsReceivingDrop = false;
        foreach (var pair in _windows) ShowWindow(pair.Input.Handle, 0);
    }

    private void RegisterImageDropTargets()
    {
        if (imageDropRegistrations is null) return;
        foreach (var pair in _windows)
        {
            nint window = pair.Input.Handle;
            var protocol = new ImageDropProtocol(
                () => !_disposed && !_captureActive && (IsEditing || IsReceivingDrop) && imageDropRegistrations?.Contains(window) == true,
                data => imageDropSupports?.Invoke(data) == true,
                (data, point) => imageDrop?.Invoke(data, point) == true,
                ReportImageDropError);
            imageDropRegistrations.Add(window, new(protocol));
        }
    }

    private void RevokeImageDropTarget(nint window) => imageDropRegistrations?.Remove(window);
    private void DisposeImageDrops()
    {
        var registrations = imageDropRegistrations;
        imageDropRegistrations = null; imageDropSupports = null; imageDrop = null;
        registrations?.Dispose();
    }
    private void ReportImageDropError(Exception error) { try { Error?.Invoke(error); } catch { } }

    private static class OleDropNative
    {
        [DllImport("ole32.dll")] internal static extern int OleInitialize(nint reserved);
        [DllImport("ole32.dll")] internal static extern void OleUninitialize();
        [DllImport("ole32.dll")] internal static extern int RegisterDragDrop(nint window, [MarshalAs(UnmanagedType.Interface)] INativeImageDropTarget target);
        [DllImport("ole32.dll")] internal static extern int RevokeDragDrop(nint window);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    }
}
