using System.Runtime.InteropServices;

// Read only the documented, current-thread TSF interfaces. No private WPF state,
// input generation, target-process access, activation or document mutation.
internal static class TextServicesProbe
{
    internal static nint FocusedDocument()
    {
        Marshal.ThrowExceptionForHR(TF_GetThreadMgr(out var manager));
        if(manager is null)return 0;
        Marshal.ThrowExceptionForHR(manager.GetFocus(out var document));
        if(document is null)return 0;
        nint identity=Marshal.GetIUnknownForObject(document);
        Marshal.Release(identity);return identity;
    }
    [DllImport("msctf.dll",ExactSpelling=true)]private static extern int TF_GetThreadMgr(out ThreadManager? manager);
    [ComImport,Guid("AA80E801-2021-11D2-93E0-0060B067B86E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ThreadManager
    {
        [PreserveSig]int Activate(out uint client);
        [PreserveSig]int Deactivate();
        [PreserveSig]int CreateDocumentMgr(out DocumentManager document);
        [PreserveSig]int EnumDocumentMgrs(out nint documents);
        [PreserveSig]int GetFocus(out DocumentManager? document);
    }
    [ComImport,Guid("AA80E7F4-2021-11D2-93E0-0060B067B86E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface DocumentManager
    {
        [PreserveSig]int CreateContext(uint client,uint flags,nint owner,out nint context,out uint cookie);
        [PreserveSig]int Push(nint context);
        [PreserveSig]int Pop(uint flags);
        [PreserveSig]int GetTop(out nint context);
    }
}
