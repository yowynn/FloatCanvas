using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FloatCanvas.Windows;

/// <summary>Agile WinRT delegate. The native capture thread only signals the owning worker.</summary>
internal static unsafe class WgcFrameArrival
{
    internal static readonly Guid HandlerIid = CreateHandlerIid();
    private static readonly Guid Unknown = new("00000000-0000-0000-c000-000000000046"), Agile = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    private static readonly nint Vtable = CreateVtable();
    private sealed class State(Action signal) { internal Action? Signal = signal; internal int References = 1; }

    internal static nint Create(Action signal)
    {
        var memory = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        try { memory[0] = Vtable; memory[1] = GCHandle.ToIntPtr(GCHandle.Alloc(new State(signal))); return (nint)memory; }
        catch { NativeMemory.Free(memory); throw; }
    }
    internal static void Detach(nint handler) { if (handler != 0) Interlocked.Exchange(ref Get(handler).Signal, null); }
    internal static void Release(nint handler) { if (handler != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)handler)[2])(handler); }
    private static State Get(nint handler) => (State)GCHandle.FromIntPtr(((nint*)handler)[1]).Target!;
    private static Guid CreateHandlerIid()
    {
        const string signature = "pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};rc(Windows.Graphics.Capture.Direct3D11CaptureFramePool;{24eb6d22-1975-422e-82e7-780dbd8ddf24});cinterface(IInspectable))";
        byte[] name = Encoding.UTF8.GetBytes(signature), input = new byte[16 + name.Length];
        new Guid("11f47ad5-7b73-42c0-abae-878b1e16adee").TryWriteBytes(input, true, out _); name.CopyTo(input, 16);
        byte[] hash = SHA1.HashData(input); hash[6] = (byte)((hash[6] & 15) | 0x50); hash[8] = (byte)((hash[8] & 63) | 0x80);
        return new Guid(hash.AsSpan(0, 16), true);
    }
    private static nint CreateVtable()
    {
        var table = (nint*)NativeMemory.AllocZeroed(4, (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReleaseCore;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&Invoke;
        return (nint)table;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Query(nint handler, Guid* iid, nint* result)
    {
        *result = 0;
        if (*iid != HandlerIid && *iid != Unknown && *iid != Agile) return unchecked((int)0x80004002);
        *result = handler; Interlocked.Increment(ref Get(handler).References); return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint handler) => (uint)Interlocked.Increment(ref Get(handler).References);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseCore(nint handler)
    {
        int remaining = Interlocked.Decrement(ref Get(handler).References);
        if (remaining == 0) { GCHandle.FromIntPtr(((nint*)handler)[1]).Free(); NativeMemory.Free((void*)handler); }
        return (uint)remaining;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Invoke(nint handler, nint sender, nint args)
    {
        // Teardown can race a queued native notification. No driver callback may throw
        // into WinRT or call an already disposed event/UI/graphics object.
        try { Volatile.Read(ref Get(handler).Signal)?.Invoke(); } catch { }
        return 0;
    }
}
