using System.Runtime.InteropServices;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

public sealed partial class OverlayHost
{
    private nint _desktopCanvasTextDocument;

    private void FocusDesktopCanvasTextServices(nint canvas)
    {
        // Detaching IMM is not enough after a WPF editor has focused a TSF document.
        // Use the UI thread's existing manager and a document without a text store.
        if (GetFocus() != canvas || GetModuleHandleW("msctf.dll") == 0 ||
            TF_GetThreadMgr(out var manager) < 0 || manager is null) return;
        if (_desktopCanvasTextDocument == 0)
        {
            if (manager.CreateDocumentMgr(out var document) < 0) return;
            _desktopCanvasTextDocument = document;
        }
        if (_desktopCanvasTextDocument != 0) manager.SetFocus(_desktopCanvasTextDocument);
    }

    private void ReleaseDesktopCanvasTextServices()
    {
        if (_desktopCanvasTextDocument == 0) return;
        Marshal.Release(_desktopCanvasTextDocument);
        _desktopCanvasTextDocument = 0;
    }

    private static void DisableCanvasInputMethod(nint window) => ImmAssociateContext(window, 0);

    [DllImport("imm32.dll")] private static extern nint ImmAssociateContext(nint window, nint context);
    [DllImport("msctf.dll", ExactSpelling = true)] private static extern int TF_GetThreadMgr(out ThreadManager? manager);

    [ComImport, Guid("AA80E801-2021-11D2-93E0-0060B067B86E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ThreadManager
    {
        [PreserveSig] int Activate(out uint client);
        [PreserveSig] int Deactivate();
        [PreserveSig] int CreateDocumentMgr(out nint document);
        [PreserveSig] int EnumDocumentMgrs(out nint documents);
        [PreserveSig] int GetFocus(out nint document);
        [PreserveSig] int SetFocus(nint document);
    }
}
