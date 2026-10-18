using System.Runtime.InteropServices;
using FloatCanvas.Windows;
using SkiaSharp;
using Vortice.DXGI;

namespace FloatCanvas.WindowsTests;

internal static class CaptureFrameBoundaryTests
{
    internal static void Run(Action<bool, string> Check)
    {
        var visual = new CaptureFrameBoundary(100, CaptureFrameRequirement.RequirePresentAfterBoundary);
        var current = new CaptureFrameBoundary(100, CaptureFrameRequirement.CurrentDesktopFromNewDuplication);
        visual.Validate(120); current.Validate(120);
        Check(!visual.Accepts(99, 110, 120), "visual transition rejects a desktop image predating its complete boundary");
        Check(visual.Accepts(100, 110, 120), "visual transition accepts a desktop present at its boundary");
        Check(visual.Accepts(119, 110, 120), "visual transition accepts a later desktop present");
        Check(current.Accepts(99, 110, 120), "current-desktop mode accepts an unchanged desktop acquired from a new request session");
        Check(current.Accepts(1, 110, 120), "old content-update time alone does not make the current static desktop stale");
        Check(current.Accepts(119, 110, 120), "current-desktop mode also accepts an updated desktop");
        foreach (var boundary in new[] { visual, current })
        {
            Check(!boundary.Accepts(0, 110, 120), "pointer-only metadata cannot establish an image in either mode");
            Check(!boundary.Accepts(-1, 110, 120), "negative desktop metadata is rejected");
            Check(!boundary.Accepts(121, 110, 120), "a desktop timestamp later than resource acquisition is rejected");
            Check(!boundary.Accepts(105, 99, 120), "a session created before readiness cannot qualify even with a later desktop present");
            Check(!boundary.Accepts(105, 110, 109), "acquisition cannot predate this request's duplication");
        }
        Check(current.Accepts(90, 100, 100), "equal boundary and acquisition ticks do not manufacture a minimum latency");
        Check(!new CaptureFrameBoundary(0, CaptureFrameRequirement.CurrentDesktopFromNewDuplication).Accepts(90, 100, 100),
            "current-desktop mode never uses zero as an unbounded readiness shortcut");
        Check(new CaptureFrameBoundary(0, CaptureFrameRequirement.RequirePresentAfterBoundary).Accepts(90, 100, 100),
            "legacy zero-boundary calls retain their existing nonzero desktop-frame behavior");
        foreach (var invalid in new[]
        {
            new CaptureFrameBoundary(-1, CaptureFrameRequirement.RequirePresentAfterBoundary),
            new CaptureFrameBoundary(121, CaptureFrameRequirement.RequirePresentAfterBoundary),
            new CaptureFrameBoundary(0, CaptureFrameRequirement.CurrentDesktopFromNewDuplication),
            new CaptureFrameBoundary(100, (CaptureFrameRequirement)77)
        })
        {
            bool rejected = false;
            try { invalid.Validate(120); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "invalid/future/undefined frame contracts are rejected before GPU acquisition");
        }
        var output = new CapturedOutputInfo("synthetic", new(-10, -20, 3, 2), 90, 0)
        {
            FrameRequirement = current.Requirement, FrameBoundaryTimestamp = 100,
            DuplicationStartedTimestamp = 110, AcquiredTimestamp = 120
        };
        Check(output.AcceptedUnchangedDesktop && output.LastPresentTimestamp == 90 && output.AcquiredTimestamp == 120,
            "diagnostics distinguish content update time from request acquisition time without rewriting timestamps");
        Check(!(output with { FrameRequirement = visual.Requirement }).AcceptedUnchangedDesktop,
            "strict mode never labels an old present as an accepted current desktop");
        Check(!(output with { LastPresentTimestamp = 100 }).AcceptedUnchangedDesktop,
            "a changed desktop is not labelled an unchanged desktop");

        // Both readiness contracts feed the same exact pixel copy. There is no image cache, alpha
        // inversion or rescale in this path; changing the supplied frame changes the output pixels.
        int pitch = 20;
        byte[] bytes = new byte[pitch * 2];
        Array.Fill(bytes, (byte)0xEE);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
                BitConverter.GetBytes((uint)(0x00100000 * (y * 3 + x + 1) + y * 3 + x + 1)).CopyTo(bytes, y * pitch + x * 4);
        var expected = new Dictionary<ModeRotation, (int Width, int Height, int[] Values)>
        {
            [ModeRotation.Identity] = (3, 2, [1, 2, 3, 4, 5, 6]),
            [ModeRotation.Rotate90] = (2, 3, [4, 1, 5, 2, 6, 3]),
            [ModeRotation.Rotate180] = (3, 2, [6, 5, 4, 3, 2, 1]),
            [ModeRotation.Rotate270] = (2, 3, [3, 6, 2, 5, 1, 4])
        };
        nint memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            foreach (var (rotation, spec) in expected)
            {
                using var image = CaptureService.CopyRotated(memory, pitch, 3, 2,
                    new(-600, -400, spec.Width, spec.Height), rotation, CancellationToken.None);
                Check(image.Width == spec.Width && image.Height == spec.Height && image.ColorType == SKColorType.Bgra8888,
                    "desktop copy keeps exact physical dimensions at negative screen origins");
                for (int y = 0; y < spec.Height; y++)
                    for (int x = 0; x < spec.Width; x++)
                    {
                        var pixel = image.GetPixel(x, y); int id = spec.Values[y * spec.Width + x];
                        Check(pixel.Blue == id && pixel.Red == id * 16 && pixel.Green == 0 && pixel.Alpha == 255,
                            "padded raw desktop rows survive every rotation without darkening or interpolation");
                    }
            }
            BitConverter.GetBytes(0x0000FABC).CopyTo(bytes, 0);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            using var next = CaptureService.CopyRotated(memory, pitch, 3, 2, new(-600, -400, 3, 2),
                ModeRotation.Identity, CancellationToken.None);
            Check(next.GetPixel(0, 0) == new SKColor(0, 0xFA, 0xBC), "a new resource's changed pixels replace the earlier sample without cached-image reuse");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool stopped = false;
            try { using var unused = CaptureService.CopyRotated(memory, pitch, 3, 2, new(0, 0, 3, 2), ModeRotation.Identity, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "pixel copy still observes request cancellation");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
