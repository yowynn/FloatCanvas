using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace FloatCanvas.Windows;

/// <summary>Scales the complete window on the GPU before one bounded CPU readback. Owned by one capture worker.</summary>
internal sealed unsafe class WindowFrameDownsampler : IDisposable
{
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader pixel;
    private readonly ID3D11SamplerState sampler;
    private readonly ID3D11RasterizerState rasterizer;
    private ID3D11Texture2D? source, output, staging;
    private ID3D11ShaderResourceView? sourceView;
    private ID3D11RenderTargetView? outputView;
    private WindowCaptureSize sourceSize, outputSize;
    private static readonly Lazy<(byte[] Vertex, byte[] Pixel)> Shaders = new(() => (Compile("vs", "vs_4_0"), Compile("ps", "ps_4_0")));

    internal WindowFrameDownsampler(ID3D11Device device, ID3D11DeviceContext context)
    {
        this.device = device; this.context = context;
        var shaders = Shaders.Value;
        vertex = device.CreateVertexShader(shaders.Vertex);
        try
        {
            pixel = device.CreatePixelShader(shaders.Pixel);
            try
            {
                sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
                try { rasterizer = device.CreateRasterizerState(RasterizerDescription.CullNone); }
                catch { sampler.Dispose(); throw; }
            }
            catch { pixel.Dispose(); throw; }
        }
        catch { vertex.Dispose(); throw; }
    }

    internal SKBitmap Read(ID3D11Texture2D captured, int width, int height, CancellationToken cancellationToken)
    {
        if (!WindowCapturePolicy.TryOutputSize(width, height, out var desired)) throw new NotSupportedException("The complete window exceeds the capture budget.");
        var description = captured.Description;
        if (description.Format != Format.B8G8R8A8_UNorm || description.Width < width || description.Height < height || description.SampleDescription.Count != 1)
            throw new NotSupportedException("Unsupported window capture surface.");
        EnsureSurfaces(new(width, height), desired);
        cancellationToken.ThrowIfCancellationRequested();
        context.CopySubresourceRegion(source!, 0, 0, 0, 0, captured, 0, new Box(0, 0, 0, width, height, 1));
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex); context.PSSetShader(pixel);
        context.RSSetState(rasterizer); context.RSSetViewport(0, 0, desired.Width, desired.Height, 0, 1);
        context.PSSetSampler(0, sampler); context.PSSetShaderResource(0, sourceView!);
        context.OMSetRenderTargets(outputView!);
        try { context.Draw(3, 0); }
        finally
        { context.ClearState(); }
        context.CopyResource(staging!, output!);
        var mapped = context.Map(staging!, 0, MapMode.Read, MapFlags.None);
        try
        {
            int rowBytes = checked(desired.Width * 4);
            if (mapped.RowPitch < rowBytes) throw new InvalidOperationException("Invalid window capture row pitch.");
            var bitmap = new SKBitmap(new SKImageInfo(desired.Width, desired.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            try
            {
                byte* destination = (byte*)bitmap.GetPixels();
                for (int row = 0; row < desired.Height; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Buffer.MemoryCopy((byte*)mapped.DataPointer + (nuint)row * mapped.RowPitch,
                        destination + row * bitmap.RowBytes, bitmap.RowBytes, rowBytes);
                }
                bitmap.SetImmutable(); return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        finally { context.Unmap(staging!, 0); }
    }

    private void EnsureSurfaces(WindowCaptureSize full, WindowCaptureSize reduced)
    {
        if (full == sourceSize && reduced == outputSize && source is not null) return;
        ReleaseSurfaces();
        try
        {
            source = device.CreateTexture2D(Description(full, ResourceUsage.Default, BindFlags.ShaderResource, CpuAccessFlags.None));
            sourceView = device.CreateShaderResourceView(source);
            output = device.CreateTexture2D(Description(reduced, ResourceUsage.Default, BindFlags.RenderTarget, CpuAccessFlags.None));
            outputView = device.CreateRenderTargetView(output);
            staging = device.CreateTexture2D(Description(reduced, ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Read));
            sourceSize = full; outputSize = reduced;
        }
        catch { ReleaseSurfaces(); throw; }
    }
    private static Texture2DDescription Description(WindowCaptureSize size, ResourceUsage usage, BindFlags binding, CpuAccessFlags access) => new()
    {
        Width = (uint)size.Width, Height = (uint)size.Height, ArraySize = 1, MipLevels = 1,
        Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0), Usage = usage, BindFlags = binding, CPUAccessFlags = access
    };
    private void ReleaseSurfaces()
    {
        sourceView?.Dispose(); sourceView = null; outputView?.Dispose(); outputView = null;
        staging?.Dispose(); staging = null; output?.Dispose(); output = null; source?.Dispose(); source = null;
        sourceSize = outputSize = default;
    }
    public void Dispose()
    { ReleaseSurfaces(); rasterizer.Dispose(); sampler.Dispose(); pixel.Dispose(); vertex.Dispose(); }

    private static byte[] Compile(string entry, string target)
    {
        const string program = "struct V{float4 p:SV_POSITION;float2 uv:TEXCOORD0;}; V vs(uint id:SV_VertexID){V o;float2 uv=float2((id<<1)&2,id&2);o.p=float4(uv.x*2-1,1-uv.y*2,0,1);o.uv=uv;return o;} Texture2D image:register(t0);SamplerState linearClamp:register(s0);float4 ps(V i):SV_TARGET{return image.Sample(linearClamp,i.uv);}";
        byte[] sourceText = Encoding.ASCII.GetBytes(program); nint code = 0, errors = 0;
        try
        {
            int result = D3DCompile(sourceText, (nuint)sourceText.Length, null, 0, 0, entry, target, 0x800, 0, out code, out errors);
            if (result < 0) throw new InvalidOperationException("The window sampling shader could not be compiled.", Marshal.GetExceptionForHR(result));
            var table = *(nint**)code;
            nint pointer = ((delegate* unmanaged[Stdcall]<nint, nint>)table[3])(code);
            nuint size = ((delegate* unmanaged[Stdcall]<nint, nuint>)table[4])(code);
            if (size is 0 or > 1_048_576) throw new InvalidOperationException("Invalid window sampling shader size.");
            byte[] resultBytes = new byte[(int)size]; Marshal.Copy(pointer, resultBytes, 0, resultBytes.Length); return resultBytes;
        }
        finally { if (errors != 0) Marshal.Release(errors); if (code != 0) Marshal.Release(code); }
    }
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile([In] byte[] source, nuint size, string? name, nint definitions, nint includes,
        string entry, string target, uint flags, uint effectFlags, out nint code, out nint errors);
}
