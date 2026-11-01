using System.Runtime.InteropServices;
using FloatCanvas.Windows;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace FloatCanvas.WindowsTests;

/// <summary>Opt-in GPU fixture. Own textures only: no HWNDs, desktop frames or input APIs.</summary>
internal static class WindowDownsampleNativeTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(WindowCapturePolicy.TryOutputSize(3840, 2160, out var size) && size == new WindowCaptureSize(1920, 1080), "A complete 4K window is reduced to 1080p before CPU readback");
        check(WindowCapturePolicy.TryOutputSize(1080, 3840, out size) && size.Height == 1080 && size.Width == 303, "Portrait capture preserves its full content inside the output budget");
        check(!WindowCapturePolicy.TryOutputSize(int.MaxValue, 1, out _) && !WindowCapturePolicy.TryOutputSize(1, int.MaxValue, out _) &&
            !WindowCapturePolicy.TryOutputSize(16384, 16384, out _) && !WindowCapturePolicy.TryOutputSize(0, 1, out _), "Invalid and oversized source dimensions fail before allocation");
        D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using (device)
        using (context)
        using (var reducer = new WindowFrameDownsampler(device, context))
        using (var texture = MakeTexture(3840, 2160))
        {
            using var frame = reducer.Read(texture, 3840, 2160, CancellationToken.None);
            check(frame.Width == 1920 && frame.Height == 1080 && frame.IsImmutable, "GPU output is an owned immutable bounded BGRA frame");
            check(frame.GetPixel(100, 100) == SKColors.Red && frame.GetPixel(1800, 100) == SKColors.Green &&
                frame.GetPixel(100, 1000) == SKColors.Blue && frame.GetPixel(1800, 1000) == SKColors.Yellow,
                "GPU reduction includes all four source quadrants rather than cropping the upper-left window");
            check((long)frame.RowBytes * frame.Height <= (long)1920 * 1080 * 4, "Full-window CPU backing stays within the fixed readback budget");
            using var croppedContent = reducer.Read(texture, 1920, 1080, CancellationToken.None);
            check(croppedContent.Width == 1920 && croppedContent.Height == 1080 && croppedContent.GetPixel(1800, 1000) == SKColors.Red,
                "Frame ContentSize excludes undefined texture padding before GPU scaling");
            using var small = MakeTexture(160, 120);
            using var resized = reducer.Read(small, 160, 120, CancellationToken.None);
            check(resized.Width == 160 && resized.Height == 120 && resized.GetPixel(150, 110) == SKColors.Yellow,
                "Source resize rebuilds surfaces and keeps full-window colors without upscaling");
            using var again = reducer.Read(texture, 3840, 2160, CancellationToken.None);
            check(again.GetPixel(1800, 1000) == SKColors.Yellow && frame.GetPixel(1800, 1000) == SKColors.Yellow,
                "A later size change does not mutate an earlier caller-owned frame");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool rejected = false;
            try { using var ignored = reducer.Read(texture, 3840, 2160, cancelled.Token); }
            catch (OperationCanceledException) { rejected = true; }
            check(rejected, "Cancelled readback cannot return a partially copied frame");
        }

        ID3D11Texture2D MakeTexture(int width, int height)
        {
            byte[] bytes = new byte[checked(width * height * 4)];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                SKColor color = y < height / 2 ? x < width / 2 ? SKColors.Red : SKColors.Green : x < width / 2 ? SKColors.Blue : SKColors.Yellow;
                int index = (y * width + x) * 4;
                bytes[index] = color.Blue; bytes[index + 1] = color.Green; bytes[index + 2] = color.Red; bytes[index + 3] = color.Alpha;
            }
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.None
                }, [new SubresourceData(pinned.AddrOfPinnedObject(), (uint)(width * 4), 0)]);
            }
            finally { pinned.Free(); }
        }
    }
}
