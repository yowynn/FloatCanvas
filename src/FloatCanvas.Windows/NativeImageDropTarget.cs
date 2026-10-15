using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace FloatCanvas.Windows;

[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeImageDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keyState, NativeDropPoint point, ref uint effect);
    [PreserveSig] int DragOver(uint keyState, NativeDropPoint point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keyState, NativeDropPoint point, ref uint effect);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeDropPoint(int x, int y)
{
    internal readonly int X = x, Y = y;
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class NativeImageDropTarget(ImageDropProtocol protocol) : INativeImageDropTarget
{
    internal bool IsDragOver => protocol.IsDragOver;
    internal void Clear() => protocol.Leave();
    public int DragEnter(IDataObject data, uint keyState, NativeDropPoint point, ref uint effect)
    { effect = protocol.Enter(data, effect); return 0; }
    public int DragOver(uint keyState, NativeDropPoint point, ref uint effect)
    { effect = protocol.Over(effect); return 0; }
    public int DragLeave() { protocol.Leave(); return 0; }
    public int Drop(IDataObject data, uint keyState, NativeDropPoint point, ref uint effect)
    { effect = protocol.Drop(data, new(point.X, point.Y), effect); return 0; }
}
