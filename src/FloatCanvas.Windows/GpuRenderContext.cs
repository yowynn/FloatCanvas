using System.ComponentModel;
using System.Runtime.InteropServices;
using SkiaSharp;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

/// <summary>One thread-affine hardware GL context. Offscreen pixels are read into the existing
/// premultiplied DIB so transparency, capture exclusion and input windows retain their contracts.</summary>
internal sealed class GpuRenderContext : IDisposable
{
    private const string GlClassName = "FloatCanvas.GpuContext";
    private static readonly object ClassLock = new();
    private static readonly WindowProcedure Procedure = DefWindowProcW;
    private static bool registered;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private nint window, dc, renderingContext;
    private GRGlInterface? gl;
    private GRContext? context;
    private readonly Dictionary<DibBuffer, SKSurface> surfaces = [];
    internal string Details { get; private set; } = "";

    internal GpuRenderContext()
    {
        try
        {
            lock (ClassLock)
            {
                if (!registered)
                {
                    var value = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Style = 0x20,
                        Procedure = Procedure, Instance = GetModuleHandleW(null), ClassName = GlClassName };
                    if (RegisterClassExW(ref value) == 0) throw NativeError("Register GPU window class");
                    registered = true;
                }
            }
            window = CreateWindowExW(ExToolWindow | ExNoActivate, GlClassName, "", WsPopup,
                0, 0, 1, 1, 0, 0, GetModuleHandleW(null), 0);
            if (window == 0) throw NativeError("Create GPU context window");
            dc = GetDC(window);
            if (dc == 0) throw NativeError("Acquire GPU context DC");
            var descriptor = new PixelFormatDescriptor { Size = (ushort)Marshal.SizeOf<PixelFormatDescriptor>(), Version = 1,
                Flags = 0x4 | 0x20 | 0x1, ColorBits = 32, AlphaBits = 8, StencilBits = 8 };
            int format = ChoosePixelFormat(dc, ref descriptor);
            if (format == 0 || !SetPixelFormat(dc, format, ref descriptor)) throw NativeError("Set GPU pixel format");
            renderingContext = wglCreateContext(dc);
            if (renderingContext == 0) throw NativeError("Create GL context");
            using var current = MakeCurrent();
            string vendor = Marshal.PtrToStringAnsi(glGetString(0x1F00)) ?? "";
            string renderer = Marshal.PtrToStringAnsi(glGetString(0x1F01)) ?? "";
            string version = Marshal.PtrToStringAnsi(glGetString(0x1F02)) ?? "";
            if (string.IsNullOrWhiteSpace(renderer) || renderer.Contains("GDI Generic", StringComparison.OrdinalIgnoreCase) ||
                renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) || renderer.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase) ||
                renderer.Contains("Software", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("A hardware OpenGL renderer is unavailable.");
            gl = GRGlInterface.CreateOpenGl(ResolveProcedure) ?? throw new NotSupportedException("OpenGL interface is unavailable.");
            context = GRContext.CreateGl(gl) ?? throw new NotSupportedException("Skia GPU context is unavailable.");
            context.SetResourceCacheLimit(128 * 1024 * 1024);
            Details = $"{vendor} / {renderer} / {version}";
        }
        catch { Dispose(); throw; }
    }

    internal IDisposable MakeCurrent()
    {
        if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("GPU context thread changed.");
        nint previousDc = wglGetCurrentDC(), previousContext = wglGetCurrentContext();
        if (!wglMakeCurrent(dc, renderingContext)) throw NativeError("Activate GL context");
        return new CurrentScope(previousDc, previousContext);
    }

    internal SKSurface SurfaceFor(DibBuffer buffer)
    {
        if (context is null || context.IsAbandoned) throw new InvalidOperationException("GPU context was lost.");
        if (!surfaces.TryGetValue(buffer, out var surface))
        {
            surface = SKSurface.Create(context, true, buffer.ImageInfo) ?? throw new InvalidOperationException("GPU surface allocation failed.");
            surfaces.Add(buffer, surface);
        }
        return surface;
    }

    internal void ReadPixels(SKSurface surface, DibBuffer buffer)
    {
        if (context is null || context.IsAbandoned ||
            !surface.ReadPixels(buffer.ImageInfo, buffer.Pixels, buffer.ImageInfo.RowBytes, 0, 0))
            throw new InvalidOperationException("GPU frame readback failed.");
    }

    internal void ReleaseSurfaces()
    {
        foreach (var surface in surfaces.Values) surface.Dispose();
        surfaces.Clear();
    }

    public void Dispose()
    {
        if (window == 0 && renderingContext == 0) return;
        // Disposal must run while this context is current, before its DC/window go away.
        nint previousDc = wglGetCurrentDC(), previousContext = wglGetCurrentContext();
        bool active = renderingContext != 0 && wglMakeCurrent(dc, renderingContext);
        try
        {
            if (!active) context?.AbandonContext();
            ReleaseSurfaces(); context?.Dispose(); context = null; gl?.Dispose(); gl = null;
        }
        finally
        {
            if (active) wglMakeCurrent(previousContext == renderingContext ? 0 : previousDc, previousContext == renderingContext ? 0 : previousContext);
            if (renderingContext != 0) { wglDeleteContext(renderingContext); renderingContext = 0; }
            if (dc != 0) { ReleaseDC(window, dc); dc = 0; }
            if (window != 0) { DestroyWindow(window); window = 0; }
        }
    }

    private static nint ResolveProcedure(string name)
    {
        nint address = wglGetProcAddress(name);
        return address == 0 || address == 1 || address == 2 || address == 3 || address == -1
            ? GetProcAddress(GetModuleHandleW("opengl32.dll"), name) : address;
    }
    private static Win32Exception NativeError(string operation) => new(Marshal.GetLastWin32Error(), operation);
    private sealed class CurrentScope(nint dc, nint context) : IDisposable
    { public void Dispose() => wglMakeCurrent(dc, context); }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormatDescriptor
    {
        internal ushort Size, Version;
        internal uint Flags;
        internal byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift,
            AlphaBits, AlphaShift, AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits,
            DepthBits, StencilBits, LayerType, Reserved;
        internal uint LayerMask, VisibleMask, DamageMask;
    }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int ChoosePixelFormat(nint dc, ref PixelFormatDescriptor descriptor);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetPixelFormat(nint dc, int format, ref PixelFormatDescriptor descriptor);
    [DllImport("opengl32.dll", SetLastError = true)] private static extern nint wglCreateContext(nint dc);
    [DllImport("opengl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool wglMakeCurrent(nint dc, nint context);
    [DllImport("opengl32.dll")] private static extern nint wglGetCurrentContext();
    [DllImport("opengl32.dll")] private static extern nint wglGetCurrentDC();
    [DllImport("opengl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool wglDeleteContext(nint context);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] private static extern nint wglGetProcAddress(string name);
    [DllImport("opengl32.dll")] private static extern nint glGetString(uint name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern nint GetProcAddress(nint module, string name);
}
