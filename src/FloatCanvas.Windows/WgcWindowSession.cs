using System.Runtime.InteropServices;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static FloatCanvas.Windows.WgcWindowInterop;
using static Vortice.Direct3D11.D3D11;

namespace FloatCanvas.Windows;

/// <summary>The item outlives paused frame pools so an old HWND can never silently select a replacement window.</summary>
internal sealed unsafe class WgcWindowItem : IDisposable
{
    private nint item, handler;
    private long token;
    private bool subscribed;
    private int closed;
    internal nint Pointer => item;
    internal bool IsClosed => Volatile.Read(ref closed) != 0;
    private static readonly Guid ClosedHandlerIid = WgcFrameArrival.CreateHandlerIid(
        "pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};rc(Windows.Graphics.Capture.GraphicsCaptureItem;{79c3f95b-31f7-4ec2-a464-632ef5d30760});cinterface(IInspectable))");

    internal WgcWindowItem(WindowCaptureTarget target, Action closedNotification)
    {
        if (!target.IsCurrent) throw new InvalidOperationException("The selected window is no longer available.");
        nint factory = Factory("Windows.Graphics.Capture.GraphicsCaptureItem", new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"));
        try
        {
            var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); nint value = 0;
            HR(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Vtable(factory, 3))(factory, target.Window, &iid, &value)); item = value;
            handler = WgcFrameArrival.Create(() => { Volatile.Write(ref closed, 1); closedNotification(); }, ClosedHandlerIid);
            long eventToken = 0;
            HR(((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)Vtable(item, 8))(item, handler, &eventToken));
            token = eventToken; subscribed = true;
            if (!target.IsCurrent) throw new InvalidOperationException("The selected window changed while connecting.");
        }
        catch { Dispose(); throw; }
        finally { Release(ref factory); }
    }
    internal SizeInt32 Size
    {
        get { SizeInt32 size = default; HR(((delegate* unmanaged[Stdcall]<nint, SizeInt32*, int>)Vtable(item, 7))(item, &size)); return size; }
    }
    public void Dispose()
    {
        WgcFrameArrival.Detach(handler);
        try
        {
            if (subscribed) { subscribed = false; HR(((delegate* unmanaged[Stdcall]<nint, long, int>)Vtable(item, 9))(item, token)); }
        }
        finally { WgcFrameArrival.Release(handler); handler = 0; Release(ref item); }
    }
}

/// <summary>One active full-window frame pool on its owning MTA capture worker. Does not own the item.</summary>
internal sealed unsafe class WgcWindowSession : IDisposable
{
    private nint pool, session, winrtDevice, handler;
    private long eventToken, timestamp;
    private bool subscribed;
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private WindowFrameDownsampler? reducer;
    private SizeInt32 poolSize;
    internal bool Resizing { get; private set; }
    internal long Timestamp => timestamp;

    internal WgcWindowSession(WgcWindowItem item, Action frameAvailable)
    {
        try
        {
            poolSize = item.Size;
            if (!WindowCapturePolicy.TryOutputSize(poolSize.Width, poolSize.Height, out _)) throw new NotSupportedException("The window exceeds the capture size budget.");
            D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out device, out context).CheckError();
            using (var multithread = context.QueryInterface<ID3D11Multithread>()) multithread.SetMultithreadProtected(true);
            nint inspectable = 0;
            try
            {
                using var dxgi = device.QueryInterface<IDXGIDevice>();
                HR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out inspectable));
                winrtDevice = Query(inspectable, new("A37624AB-8D5F-4650-9D3E-9EAE3D9BC670"));
            }
            finally { Release(ref inspectable); }
            nint factory = Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", new("589B103F-6BBC-5DF5-A991-02E28B3B66D5"));
            try
            {
                nint value = 0;
                HR(((delegate* unmanaged[Stdcall]<nint, nint, int, int, SizeInt32, nint*, int>)Vtable(factory, 6))(factory, winrtDevice, 87, 2, poolSize, &value)); pool = value;
            }
            finally { Release(ref factory); }
            nint capture = 0; HR(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vtable(pool, 10))(pool, item.Pointer, &capture)); session = capture;
            var cursorIid = new Guid("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
            if (Marshal.QueryInterface(session, in cursorIid, out nint cursor) >= 0)
            {
                try { HR(((delegate* unmanaged[Stdcall]<nint, byte, int>)Vtable(cursor, 7))(cursor, 0)); }
                finally { Release(ref cursor); }
            }
            // Preserve the system's capture-border policy. No access request or preference change.
            handler = WgcFrameArrival.Create(frameAvailable); long token = 0;
            HR(((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)Vtable(pool, 8))(pool, handler, &token));
            eventToken = token; subscribed = true;
            reducer = new(device, context);
            HR(((delegate* unmanaged[Stdcall]<nint, int>)Vtable(session, 6))(session));
        }
        catch { Dispose(); throw; }
    }

    internal bool TryRead(CancellationToken cancellationToken, out SKBitmap? bitmap, out WindowCaptureSize content)
    {
        bitmap = null; content = default; Resizing = false;
        nint latest = 0; long newest = timestamp;
        SizeInt32 resize = default;
        try
        {
            for (int count = 0; count < 3; count++)
            {
                cancellationToken.ThrowIfCancellationRequested(); nint frame = 0;
                try
                {
                    HR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtable(pool, 7))(pool, &frame));
                    if (frame == 0) break;
                    SizeInt32 size = default; long time = 0;
                    HR(((delegate* unmanaged[Stdcall]<nint, SizeInt32*, int>)Vtable(frame, 8))(frame, &size));
                    if (size.Width <= 0 || size.Height <= 0) continue;
                    if (!WindowCapturePolicy.TryOutputSize(size.Width, size.Height, out _)) throw new NotSupportedException("The resized window exceeds the capture size budget.");
                    if (size.Width != poolSize.Width || size.Height != poolSize.Height) { resize = size; break; }
                    HR(((delegate* unmanaged[Stdcall]<nint, long*, int>)Vtable(frame, 7))(frame, &time));
                    long capturedAt = WgcDesktopSession.ToQpc(time);
                    if (capturedAt <= newest) continue;
                    CloseFrame(ref latest); latest = frame; frame = 0; newest = capturedAt;
                    content = new(size.Width, size.Height);
                }
                finally { CloseFrame(ref frame); }
            }
            if (resize.Width > 0)
            {
                CloseFrame(ref latest);
                HR(((delegate* unmanaged[Stdcall]<nint, nint, int, int, SizeInt32, int>)Vtable(pool, 6))(pool, winrtDevice, 87, 2, resize));
                poolSize = resize; Resizing = true; return false;
            }
            if (latest == 0) return false;
            nint surface = 0, access = 0, texturePointer = 0;
            try
            {
                HR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtable(latest, 6))(latest, &surface));
                access = Query(surface, new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"));
                var iid = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
                HR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtable(access, 3))(access, &iid, &texturePointer));
                using var texture = new ID3D11Texture2D(texturePointer); texturePointer = 0;
                var actual = texture.Description;
                if (actual.Width < content.Width || actual.Height < content.Height)
                    resize = new() { Width = content.Width, Height = content.Height };
                else { bitmap = reducer!.Read(texture, content.Width, content.Height, cancellationToken); timestamp = newest; return true; }
            }
            finally { Release(ref texturePointer); Release(ref access); Release(ref surface); }
            // ContentSize can move ahead of a buffer during a resize. Release every
            // checked-out frame before rebuilding; an old buffer is never sampled.
            CloseFrame(ref latest);
            HR(((delegate* unmanaged[Stdcall]<nint, nint, int, int, SizeInt32, int>)Vtable(pool, 6))(pool, winrtDevice, 87, 2, resize));
            poolSize = resize; Resizing = true; return false;
        }
        finally { CloseFrame(ref latest); }
    }
    public void Dispose()
    {
        WgcFrameArrival.Detach(handler);
        try { if (subscribed) { subscribed = false; HR(((delegate* unmanaged[Stdcall]<nint, long, int>)Vtable(pool, 9))(pool, eventToken)); } }
        finally
        {
            try { Close(session); }
            finally
            {
                try { Close(pool); }
                finally
                {
                    WgcFrameArrival.Release(handler); handler = 0;
                    Release(ref session); Release(ref pool); Release(ref winrtDevice);
                    reducer?.Dispose(); reducer = null; context?.Dispose(); context = null; device?.Dispose(); device = null;
                }
            }
        }
    }
}

internal static unsafe class WgcWindowInterop
{
    [StructLayout(LayoutKind.Sequential)] internal struct SizeInt32 { public int Width, Height; }
    internal static nint Factory(string name, Guid iid)
    {
        nint text = 0;
        try { HR(WindowsCreateString(name, (uint)name.Length, out text)); HR(RoGetActivationFactory(text, in iid, out nint result)); return result; }
        finally { if (text != 0) WindowsDeleteString(text); }
    }
    internal static nint Query(nint instance, Guid iid) { HR(Marshal.QueryInterface(instance, in iid, out nint result)); return result; }
    internal static nint Vtable(nint instance, int slot) => ((nint*)(*(nint*)instance))[slot];
    internal static void HR(int result) => Marshal.ThrowExceptionForHR(result);
    internal static void Release(ref nint instance) { if (instance == 0) return; nint value = instance; instance = 0; Marshal.Release(value); }
    internal static void CloseFrame(ref nint frame) { try { Close(frame); } finally { Release(ref frame); } }
    internal static void Close(nint instance)
    {
        if (instance == 0) return;
        var iid = new Guid("30D5A829-7FA4-4026-83BB-D75BAE4EA99E"); nint close = 0;
        try { if (Marshal.QueryInterface(instance, in iid, out close) >= 0 && close != 0) HR(((delegate* unmanaged[Stdcall]<nint, int>)Vtable(close, 6))(close)); }
        finally { Release(ref close); }
    }
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, uint length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("d3d11.dll")] internal static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
