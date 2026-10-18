using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using MapFlags = Vortice.Direct3D11.MapFlags;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Windows;

internal interface ILiveDesktopOutputSession : IDisposable
{
    MonitorInfo Monitor { get; }
    int Rotation { get; }
    PixelBounds Region { get; }
    SKBitmap? Bitmap { get; }
    long Timestamp { get; }
    bool ProtectedContentMaskedOut { get; }
    LiveDesktopFrameTiming? Timing { get; }
    bool Update(PixelBounds region, long boundary, CancellationToken cancellationToken, nint[] ownWindows);
}

/// <summary>A worker-thread WGC monitor session. Only this session excludes the process's windows;
/// global display affinity and foreground/input state are never changed here.</summary>
internal sealed unsafe class WgcDesktopSession : ILiveDesktopOutputSession
{
    private nint item, pool, session, display, winrtDevice;
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private ID3D11Texture2D? desktop, staging;
    private nint[]? excludedWindows;
    private ulong requiredIteration;
    private long cleanBoundary, waitingSince = Stopwatch.GetTimestamp();
    private bool started, disposed;
    private nint frameHandler;
    private long frameToken;
    private bool frameSubscribed;
    public MonitorInfo Monitor { get; }
    public int Rotation => 0; // WGC resources are already oriented in physical display coordinates.
    public PixelBounds Region { get; private set; }
    public SKBitmap? Bitmap { get; private set; }
    public long Timestamp { get; private set; }
    public bool ProtectedContentMaskedOut => false; // WGC exposes no equivalent DXGI metadata.
    public LiveDesktopFrameTiming? Timing { get; private set; }
    internal ulong ConfigurationIteration { get; private set; }

    internal WgcDesktopSession(MonitorInfo monitor, bool probeOnly = false, Action? frameAvailable = null)
    {
        Monitor = monitor;
        try
        {
            nint monitorHandle = MonitorFromPoint(new(monitor.Bounds.Left + monitor.Bounds.Width / 2, monitor.Bounds.Top + monitor.Bounds.Height / 2), 2);
            nint factory = Factory("Windows.Graphics.Capture.GraphicsCaptureItem", new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"));
            try
            {
                var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); nint value = 0;
                HR(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Vtable(factory, 4))(factory, monitorHandle, &iid, &value)); item = value;
            }
            finally { Release(ref factory); }
            SizeInt32 size = default; HR(((delegate* unmanaged[Stdcall]<nint, SizeInt32*, int>)Vtable(item, 7))(item, &size));
            if (size.Width != monitor.Bounds.Width || size.Height != monitor.Bounds.Height)
                throw new InvalidOperationException(L.Get("Capture.DisplayLayoutChanged"));
            D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out device, out context).CheckError();
            // ID3D11Multithread belongs to the immediate context, not the device.
            using (var multithread = context.QueryInterface<ID3D11Multithread>()) multithread.SetMultithreadProtected(true);
            nint inspectableDevice = 0;
            try
            {
                using (var dxgi = device.QueryInterface<IDXGIDevice>()) HR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out inspectableDevice));
                // The interop function returns IInspectable. A frame pool expects the
                // distinct IDirect3DDevice ABI, even where both pointers happen to coincide.
                winrtDevice = Query(inspectableDevice, new("A37624AB-8D5F-4650-9D3E-9EAE3D9BC670"));
            }
            finally { Release(ref inspectableDevice); }
            factory = Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", new("589B103F-6BBC-5DF5-A991-02E28B3B66D5"));
            try
            {
                nint value = 0;
                HR(((delegate* unmanaged[Stdcall]<nint, nint, int, int, SizeInt32, nint*, int>)Vtable(factory, 6))(factory, winrtDevice, 87, 2, size, &value)); pool = value;
            }
            finally { Release(ref factory); }
            nint capture = 0; HR(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vtable(pool, 10))(pool, item, &capture)); session = capture;
            display = Query(session, new("BB91F61B-218A-587D-8580-2701A74C0525"));
            if (!probeOnly)
            {
                SetBoolean(new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E"), false); // cursor
                // Full-trust setter only. No access request, picker or system preference change.
                SetBoolean(new("F2CDD966-22AE-5EA1-9596-3A289344C3BE"), false); // system capture border
                if(frameAvailable is not null)
                {
                    frameHandler=WgcFrameArrival.Create(frameAvailable); long token=0;
                    HR(((delegate* unmanaged[Stdcall]<nint,nint,long*,int>)Vtable(pool,8))(pool,frameHandler,&token));
                    frameToken=token; frameSubscribed=true;
                }
            }
        }
        catch { Dispose(); throw; }
    }

    internal static bool ProbeSessionExclusion()
    {
        bool initialized = false;
        try
        {
            InitializeRuntime(); initialized = true;
            using var dpi = new ThreadDpiAwarenessScope(WindowsCapabilities.Current);
            nint monitor = MonitorFromPoint(new(0, 0), 1);
            var info = new MonitorInformation { Size = (uint)Marshal.SizeOf<MonitorInformation>() };
            if (monitor == 0 || !GetMonitorInfoW(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var bounds = new PixelBounds(info.Bounds.Left, info.Bounds.Top, info.Bounds.Right - info.Bounds.Left, info.Bounds.Bottom - info.Bounds.Top);
            using var source = new WgcDesktopSession(new("WGC capability probe", bounds, bounds, 96, 96, true), probeOnly: true);
            return true;
        }
        catch (Exception error) when (IsMissingCapability(error)) { return false; }
        finally { if (initialized) UninitializeRuntime(); }
    }

    internal static bool IsMissingCapability(Exception error) => error.HResult is
        unchecked((int)0x80004002) or // E_NOINTERFACE: update/feature not available.
        unchecked((int)0x80040154) or // REGDB_E_CLASSNOTREG: Windows Capture class absent.
        unchecked((int)0x80040111) or // CLASS_E_CLASSNOTAVAILABLE.
        unchecked((int)0x8000000F);   // RO_E_METADATA_NAME_NOT_FOUND.

    internal static void InitializeRuntime() => HR(RoInitialize(1));
    internal static void UninitializeRuntime() => RoUninitialize();

    internal static nint[] EnumerateOwnWindows()
    {
        uint process = (uint)Environment.ProcessId; var windows = new List<nint>(); Exception? failure = null;
        bool Enumerate(nint window, nint _)
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(window, out uint owner);
                if (owner == process) windows.Add(window);
                return true;
            }
            catch (Exception error) { failure = error; return false; }
        }
        if (!EnumWindows(Enumerate, 0)) throw failure ?? new Win32Exception(Marshal.GetLastWin32Error());
        return windows.Distinct().Order().ToArray();
    }

    public bool Update(PixelBounds region, long boundary, CancellationToken cancellationToken, nint[] ownWindows)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool changed = Region != region || cleanBoundary != boundary;
        if (excludedWindows is null || !excludedWindows.SequenceEqual(ownWindows))
        {
            // The list contains only the caller's native top-level HWNDs. Never accept an
            // old configuration frame merely because its pixel timestamp is newer.
            nint list = WgcWindowIds.Create(ownWindows); ulong iteration = 0;
            try { HR(((delegate* unmanaged[Stdcall]<nint, nint, ulong*, int>)Vtable(display, 6))(display, list, &iteration)); }
            finally { WgcWindowIds.Release(list); }
            requiredIteration = iteration; excludedWindows = [.. ownWindows];
            InvalidateDesktop(); changed = true;
        }
        if (cleanBoundary != boundary)
        { cleanBoundary = boundary; if (Timestamp < boundary) InvalidateDesktop(); }
        if (Region != region) { Region = region; Bitmap?.Dispose(); Bitmap = null; }
        if (!started) { HR(((delegate* unmanaged[Stdcall]<nint, int>)Vtable(session, 6))(session)); started = true; waitingSince = Stopwatch.GetTimestamp(); }
        // Drain the bounded two-frame pool, retaining only the newest accepted desktop.
        ReadLatest(boundary, cancellationToken, ref changed);
        if (desktop is null)
        {
            if (Stopwatch.GetElapsedTime(waitingSince) > TimeSpan.FromSeconds(3))
                throw new TimeoutException(L.Format("Capture.FreshFrameTimedOut", Monitor.DeviceName));
            return false;
        }
        if (!changed && Bitmap is not null) return false;
        var crop = LiveDesktopCapture.SourceRegion(Monitor.Bounds, region, checked((int)desktop.Description.Width), checked((int)desktop.Description.Height), 0);
        if (staging is null || staging.Description.Width != crop.Width || staging.Description.Height != crop.Height)
        {
            staging?.Dispose(); var description = desktop.Description;
            description.Width = (uint)crop.Width; description.Height = (uint)crop.Height;
            description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read; description.MiscFlags = ResourceOptionFlags.None;
            staging = device!.CreateTexture2D(description);
        }
        context!.CopySubresourceRegion(staging, 0, 0, 0, 0, desktop, 0, new Box(crop.Left, crop.Top, 0, crop.Right, crop.Bottom, 1));
        var mapped = context.Map(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            var bitmap = LiveDesktopCapture.CopyRotatedRoi(mapped.DataPointer, checked((int)mapped.RowPitch), crop.Width, crop.Height, region, 0, cancellationToken);
            Bitmap?.Dispose(); Bitmap = bitmap;
            if(Timing is {} timing) Timing=timing with {ReadbackCompletedTimestamp=Stopwatch.GetTimestamp()};
            return true;
        }
        finally { context.Unmap(staging, 0); }
    }

    private void ReadLatest(long boundary, CancellationToken cancellationToken, ref bool changed)
    {
        nint latest=0;long latestTimestamp=Timestamp,acquiredAt=0;ulong latestIteration=0;int drained=0,rejected=0;
        try
        {
            // Keep a checked-out newest valid frame, close older candidates immediately,
            // and perform one GPU copy/readback after draining the bounded pool.
            for(int read=0;read<3;read++)
            {
                cancellationToken.ThrowIfCancellationRequested();nint frame=0,frame3=0;
                try
                {
                    HR(((delegate* unmanaged[Stdcall]<nint,nint*,int>)Vtable(pool,7))(pool,&frame));
                    if(frame==0)break;drained++;
                    frame3=Query(frame,new("71616DC8-FEA5-5741-A3D8-591ACC39A9EE"));
                    ulong iteration=0;long time=0;
                    HR(((delegate* unmanaged[Stdcall]<nint,ulong*,int>)Vtable(frame3,6))(frame3,&iteration));
                    HR(((delegate* unmanaged[Stdcall]<nint,long*,int>)Vtable(frame,7))(frame,&time));
                    long timestamp=ToQpc(time);
                    if(!AcceptFrame(iteration,requiredIteration,timestamp,boundary,latestTimestamp))
                    {rejected++;continue;}
                    CloseFrame(ref latest);latest=frame;frame=0;latestTimestamp=timestamp;latestIteration=iteration;acquiredAt=Stopwatch.GetTimestamp();
                }
                finally {Release(ref frame3);CloseFrame(ref frame);}
            }
            if(latest==0)return;
            CopyFrame(latest,latestTimestamp,latestIteration);changed=true;
            Timing=new(acquiredAt,0,latestIteration,drained,rejected);
        }
        finally {CloseFrame(ref latest);}
    }

    private void CopyFrame(nint frame,long timestamp,ulong iteration)
    {
        nint surface = 0, access = 0, texturePointer = 0;
        try
        {
            SizeInt32 size = default; HR(((delegate* unmanaged[Stdcall]<nint, SizeInt32*, int>)Vtable(frame, 8))(frame, &size));
            if (size.Width != Monitor.Bounds.Width || size.Height != Monitor.Bounds.Height) throw new InvalidOperationException(L.Get("Capture.DisplayLayoutChanged"));
            HR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtable(frame, 6))(frame, &surface));
            access = Query(surface, new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"));
            var iid = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
            HR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtable(access, 3))(access, &iid, &texturePointer));
            using var texture = new ID3D11Texture2D(texturePointer); texturePointer = 0;
            var description = texture.Description;
            if (description.Format != Format.B8G8R8A8_UNorm) throw new NotSupportedException(L.Format("Capture.UnsupportedPixelFormat", description.Format));
            if (description.Width != Monitor.Bounds.Width || description.Height != Monitor.Bounds.Height) throw new InvalidOperationException(L.Get("Capture.DisplayLayoutChanged"));
            if (desktop is null)
            {
                description.Usage = ResourceUsage.Default; description.BindFlags = BindFlags.None;
                description.CPUAccessFlags = CpuAccessFlags.None; description.MiscFlags = ResourceOptionFlags.None;
                desktop = device!.CreateTexture2D(description);
            }
            context!.CopyResource(desktop, texture); Timestamp = timestamp; ConfigurationIteration = iteration;
        }
        finally
        {
            Release(ref texturePointer); Release(ref access); Release(ref surface);
        }
    }

    private static void CloseFrame(ref nint frame) {try{Close(frame);}finally{Release(ref frame);}}

    internal static bool AcceptFrame(ulong frameIteration, ulong requiredIteration, long timestamp, long boundary, long newestTimestamp=0) =>
        frameIteration >= requiredIteration && timestamp > newestTimestamp && timestamp > 0 && timestamp >= boundary;
    internal static long ToQpc(long time100ns) => checked((long)Math.Floor(time100ns * (Stopwatch.Frequency / 10_000_000d)));
    internal void InvalidateOwnWindows() { excludedWindows = null; InvalidateDesktop(); }
    private void InvalidateDesktop() { desktop?.Dispose(); desktop = null; Bitmap?.Dispose(); Bitmap = null; Timestamp = 0; Timing=null; waitingSince = Stopwatch.GetTimestamp(); }
    private void SetBoolean(Guid iid, bool value)
    {
        nint target = Query(session, iid);
        try { HR(((delegate* unmanaged[Stdcall]<nint, byte, int>)Vtable(target, 7))(target, (byte)(value ? 1 : 0))); }
        finally { Release(ref target); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        WgcFrameArrival.Detach(frameHandler);
        try
        {
            if(frameSubscribed)
            {frameSubscribed=false;HR(((delegate* unmanaged[Stdcall]<nint,long,int>)Vtable(pool,9))(pool,frameToken));}
        }
        finally
        {
            try { Close(session); }
            finally
            {
                try {Close(pool);}
                finally
                {
                    WgcFrameArrival.Release(frameHandler);frameHandler=0;
                    Release(ref display); Release(ref session); Release(ref pool); Release(ref item);
                    Bitmap?.Dispose(); staging?.Dispose(); desktop?.Dispose(); Release(ref winrtDevice); context?.Dispose(); device?.Dispose();
                }
            }
        }
    }
    private static nint Factory(string name, Guid iid)
    {
        nint text = 0;
        try { HR(WindowsCreateString(name, (uint)name.Length, out text)); HR(RoGetActivationFactory(text, in iid, out nint result)); return result; }
        finally { if (text != 0) WindowsDeleteString(text); }
    }
    private static nint Query(nint instance, Guid iid) { HR(Marshal.QueryInterface(instance, in iid, out nint result)); return result; }
    private static nint Vtable(nint instance, int slot) => ((nint*)(*(nint*)instance))[slot];
    private static void HR(int hr) => Marshal.ThrowExceptionForHR(hr);
    private static void Release(ref nint value) { if (value != 0) { Marshal.Release(value); value = 0; } }
    private static void Close(nint instance)
    {
        if (instance == 0) return;
        var iid = new Guid("30D5A829-7FA4-4026-83BB-D75BAE4EA99E"); nint close = 0;
        try { if (Marshal.QueryInterface(instance, in iid, out close) >= 0 && close != 0) HR(((delegate* unmanaged[Stdcall]<nint, int>)Vtable(close, 6))(close)); }
        finally { Release(ref close); }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct SizeInt32 { public int Width; public int Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInformation { public uint Size; public Rect Bounds; public Rect WorkArea; public uint Flags; }
    private delegate bool WindowEnum(nint window, nint data);
    [DllImport("combase.dll")] private static extern int RoInitialize(uint kind);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, uint length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInformation info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(WindowEnum callback, nint parameter);
}

// Minimal native ABI collection. No WinRT SDK projection or target-framework
// upgrade is required. Windows.UI.WindowId is a single UInt64; shared native
// vtables live for the process lifetime, while every instance has COM ownership.
internal static unsafe class WgcWindowIds
{
    internal static readonly Guid IterableIid = Parameterized("faa585ea-6214-4217-afda-7f46de5869b3");
    internal static readonly Guid IteratorIid = Parameterized("6a79e863-4300-459a-9966-cbb660963ee1");
    private static readonly Guid UnknownIid = new("00000000-0000-0000-c000-000000000046"), InspectableIid = new("af86e2e0-b12d-4c6a-9c5a-d7aa65101e90"), AgileIid = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    private static readonly nint IterableVtable = Build(false), IteratorVtable = Build(true);
    private sealed class State(ulong[] values, bool iterator) { internal readonly ulong[] Values = values; internal readonly bool Iterator = iterator; internal int Position; internal int References = 1; }
    internal static nint Create(params nint[] windows) => Allocate(windows.Select(window => (ulong)(nuint)window).ToArray(), false);
    private static nint Allocate(ulong[] values, bool iterator)
    {
        var memory = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        try
        {
            memory[0] = iterator ? IteratorVtable : IterableVtable; memory[1] = GCHandle.ToIntPtr(GCHandle.Alloc(new State(values, iterator)));
            return (nint)memory;
        }
        catch { NativeMemory.Free(memory); throw; }
    }
    internal static void Release(nint instance) { if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)instance)[2])(instance); }
    private static State Get(nint instance) => (State)GCHandle.FromIntPtr(((nint*)instance)[1]).Target!;
    private static nint Build(bool iterator)
    {
        var table = (nint*)NativeMemory.AllocZeroed((nuint)(iterator ? 10 : 7), (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReleaseCore;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)&GetIids;
        table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&ClassName;
        table[5] = (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&Trust;
        if (iterator)
        {
            table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong*, int>)&Current;
            table[7] = (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&HasCurrent;
            table[8] = (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&MoveNext;
            table[9] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong*, uint*, int>)&GetMany;
        }
        else table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&First;
        return (nint)table;
    }
    private static Guid Parameterized(string generic)
    {
        Span<byte> ns = stackalloc byte[16]; new Guid("11f47ad5-7b73-42c0-abae-878b1e16adee").TryWriteBytes(ns, true, out _);
        byte[] name = Encoding.UTF8.GetBytes("pinterface({" + generic + "};struct(Windows.UI.WindowId;u8))");
        byte[] input = new byte[16 + name.Length]; ns.CopyTo(input); name.CopyTo(input, 16);
        var hash = SHA1.HashData(input); hash[6] = (byte)((hash[6] & 0x0f) | 0x50); hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), true);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Query(nint instance, Guid* iid, nint* result)
    {
        *result = 0; var state = Get(instance);
        if (*iid != UnknownIid && *iid != InspectableIid && *iid != AgileIid && *iid != (state.Iterator ? IteratorIid : IterableIid)) return unchecked((int)0x80004002);
        *result = instance; Interlocked.Increment(ref state.References); return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static uint AddRef(nint instance) => (uint)Interlocked.Increment(ref Get(instance).References);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseCore(nint instance)
    {
        var state = Get(instance); int remaining = Interlocked.Decrement(ref state.References);
        if (remaining == 0) { GCHandle.FromIntPtr(((nint*)instance)[1]).Free(); NativeMemory.Free((void*)instance); }
        return (uint)remaining;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetIids(nint instance, uint* count, nint* ids)
    {
        *count = 0; *ids = 0;
        try { *ids = Marshal.AllocCoTaskMem(sizeof(Guid)); *(Guid*)*ids = Get(instance).Iterator ? IteratorIid : IterableIid; *count = 1; return 0; }
        catch (Exception error) { return error.HResult; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int ClassName(nint instance, nint* name) { *name = 0; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Trust(nint instance, int* trust) { *trust = 0; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int First(nint instance, nint* iterator)
    {
        *iterator = 0;
        try { *iterator = Allocate(Get(instance).Values, true); return 0; }
        catch (Exception error) { return error.HResult; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Current(nint instance, ulong* value)
    {
        var state = Get(instance); lock (state) { if (state.Position >= state.Values.Length) return unchecked((int)0x8000000B); *value = state.Values[state.Position]; return 0; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int HasCurrent(nint instance, byte* current) { var state = Get(instance); lock (state) { *current = (byte)(state.Position < state.Values.Length ? 1 : 0); return 0; } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int MoveNext(nint instance, byte* current) { var state = Get(instance); lock (state) { if (state.Position < state.Values.Length) state.Position++; *current = (byte)(state.Position < state.Values.Length ? 1 : 0); return 0; } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetMany(nint instance, uint capacity, ulong* values, uint* count)
    {
        var state = Get(instance); lock (state) { uint actual = 0; while (actual < capacity && state.Position < state.Values.Length) values[actual++] = state.Values[state.Position++]; *count = actual; return 0; }
    }
}
