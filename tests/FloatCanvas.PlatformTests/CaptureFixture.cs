using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.PlatformTests;

/// <summary>Tests the production DXGI capture using only a known area of a self-owned lower window.</summary>
internal static class CaptureFixture
{
    public static async Task<int> Run(string evidencePath, bool primaryScreenProbe = false, bool waitForActivation = false)
    {
        var previousForeground = Native.GetForegroundWindow();
        Native.GetCursorPos(out var previousCursor);
        var checks = new List<object>();
        var captures = new List<object>();
        var errors = new List<string>();
        var passed = false;
        var originalPassedCount = 0;
        MonitorInfo[] monitors = [];
        NativeTestWindow? target = null;
        OverlayHost? host = null;
        PrimaryScreenFixture? primaryProbe = null;
        try
        {
            target = new NativeTestWindow();
            // Keep our known background above unrelated applications before creating the overlay.
            Native.SetWindowPos(target.Handle, -1, 0, 0, 0, 0, 0x13);
            if (primaryScreenProbe && waitForActivation) await PrimaryScreenFixture.WaitForExternalActivation(target);
            var marker = target.ScreenPoint(160, 180);
            var cropBounds = new PixelBounds(marker.X - 48, marker.Y - 48, 96, 96);
            if (!target.OwnsPoint(new(cropBounds.Left, cropBounds.Top)) ||
                !target.OwnsPoint(new(cropBounds.Right - 1, cropBounds.Bottom - 1)))
                throw new InvalidOperationException("Capture evidence crop would leave the self-owned target.");
            await Task.Delay(80);
            var backgroundColor = DesktopColor(marker);
            Check("known-self-target-white-background", White(backgroundColor),
                $"Self-owned target desktop RGB={backgroundColor.Red},{backgroundColor.Green},{backgroundColor.Blue}.");
            host = primaryScreenProbe ? PrimaryScreenFixture.CreateHost(out primaryProbe) : new OverlayHost();
            monitors = host.Monitors.ToArray();
            host.Error += ex => errors.Add(ex.ToString());
            host.Draw += surface =>
            {
                var canvas = surface.Canvas;
                using var paint = new SKPaint { Color = new SKColor(40, 110, 240), IsAntialias = false };
                canvas.DrawRect(marker.X - 32, marker.Y - 32, 64, 64, paint);
            };
            host.SetVisible(true);
            using var service = new CaptureService();
            foreach (var edit in new[] { false, true })
            {
                var stateName = edit ? "editing" : "passthrough";
                host.SetEditing(edit);
                host.Render();
                await Task.Delay(100);
                var visibleColor = DesktopColor(marker);
                Check(stateName + "-opaque-marker-visible-before-capture", Blue(visibleColor),
                    $"Actual desktop RGB={visibleColor.Red},{visibleColor.Green},{visibleColor.Blue}; opaque marker should be blue.");
                long notBefore;
                CapturedDesktop desktop;
                var timer = Stopwatch.StartNew();
                try
                {
                    using var scope = host.HideForCapture();
                    notBefore = scope.NotBeforeTimestamp;
                    Check(stateName + "-capture-scope-hides-host", !host.IsVisible && !host.IsEditing &&
                        host.Diagnostics.All(d => !Native.IsWindowVisible(d.DisplayHandle) && !Native.IsWindowVisible(d.InputHandle)),
                        "Production capture scope temporarily hides both display and input surfaces.");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var requestedMonitors = host.Monitors.ToArray();
                    primaryProbe?.VerifyCaptureRequest(stateName, requestedMonitors);
                    desktop = await service.CaptureAsync(requestedMonitors, notBefore, timeout.Token);
                    primaryProbe?.VerifyCaptureResult(stateName, desktop);
                }
                finally
                {
                    Check(stateName + "-capture-restores-host-state", host.IsVisible && host.IsEditing == edit &&
                        host.Diagnostics.All(d => Native.IsWindowVisible(d.DisplayHandle) && Native.IsWindowVisible(d.InputHandle) == edit),
                        "Disposing the production scope restores visibility and mode on success, failure, or cancellation.");
                }
                Check(stateName + "-all-output-frames-are-fresh", desktop.Outputs.Count == monitors.Length &&
                    desktop.Outputs.All(o => o.LastPresentTimestamp >= notBefore),
                    $"All {desktop.Outputs.Count} captured DXGI outputs have LastPresentTimestamp >= {notBefore}.");
                using var decoded = SKBitmap.Decode(desktop.PngBytes) ?? throw new InvalidOperationException("Capture PNG could not be decoded.");
                var cleanColor = decoded.GetPixel(marker.X - desktop.Bounds.Left, marker.Y - desktop.Bounds.Top);
                Check(stateName + "-native-capture-excludes-marker", White(cleanColor) && !Blue(cleanColor),
                    $"Decoded production capture RGB={cleanColor.Red},{cleanColor.Green},{cleanColor.Blue}; expected self-owned white background, not blue ink.");
                using var crop = new SKBitmap();
                var cropRect = new SKRectI(cropBounds.Left - desktop.Bounds.Left, cropBounds.Top - desktop.Bounds.Top,
                    cropBounds.Right - desktop.Bounds.Left, cropBounds.Bottom - desktop.Bounds.Top);
                if (!decoded.ExtractSubset(crop, cropRect)) throw new InvalidOperationException("Could not extract safe evidence crop.");
                using var cropImage = SKImage.FromBitmap(crop);
                using var encoded = cropImage.Encode(SKEncodedImageFormat.Png, 100);
                Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
                var cropPath = Path.Combine(Path.GetDirectoryName(evidencePath)!, "capture-clean-" + stateName + ".png");
                File.WriteAllBytes(cropPath, encoded.ToArray());
                captures.Add(new { mode = stateName, notBeforeTimestamp = notBefore, capturedAtUtc = desktop.CapturedAtUtc,
                    elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, desktop.Bounds, desktop.Outputs,
                    marker = new { marker.X, marker.Y }, sampledColor = new { cleanColor.Red, cleanColor.Green, cleanColor.Blue, cleanColor.Alpha },
                    evidenceCrop = cropPath, evidenceCropBounds = cropBounds,
                    privacy = "Only this 96x96 self-owned window region was saved. Full desktop PNG remained in memory only." });
                await Task.Delay(60);
                Check(stateName + "-marker-restored-after-capture", Blue(DesktopColor(marker)),
                    "The original opaque annotation is visible again after capture.");
            }
            host.SetEditing(true);
            var cancellationObserved = false;
            using (host.HideForCapture())
            {
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                var requestedMonitors = host.Monitors.ToArray();
                primaryProbe?.VerifyCaptureRequest("cancellation", requestedMonitors);
                try { await service.CaptureAsync(requestedMonitors, canceled.Token); }
                catch (OperationCanceledException) { cancellationObserved = true; }
            }
            Check("capture-cancellation-is-explicit", cancellationObserved,
                "Production capture returns cancellation as a failure, not a cached/blank success or skipped test.");
            Check("capture-cancellation-restores-edit-mode", host.IsVisible && host.IsEditing &&
                host.Diagnostics.All(d => Native.IsWindowVisible(d.DisplayHandle) && Native.IsWindowVisible(d.InputHandle)),
                "Exception/cancellation path still restores original edit and visibility state.");
            passed = errors.Count == 0;
        }
        catch (Exception ex)
        {
            errors.Add(ex.ToString());
            Console.Error.WriteLine(ex);
        }
        finally
        {
            host?.Dispose();
            target?.Dispose();
            Native.SetCursorPos(previousCursor.X, previousCursor.Y);
            if (previousForeground != 0) Native.SetForegroundWindow(previousForeground);
            Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, generatedAt = DateTimeOffset.Now, os = Environment.OSVersion.VersionString,
                test = "M1 production capture exclusion", actualMonitors = monitors,
                originalCheckCount = checks.Count,
                originalPassedCount,
                primaryScreenProbe = primaryProbe?.Evidence,
                checks, captures, errors, passed,
                limitations = new[] { "This fixture did not automate or save other applications.",
                    "Hardware present on this machine only; Win10, mixed-DPI multi-monitor, HDR, protected content, and games are not established by this result." }
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Capture evidence: {evidencePath}");
            Console.WriteLine($"{checks.Count} capture checks; {errors.Count} errors; passed={passed}.");
        }
        return passed ? 0 : 1;

        void Check(string name, bool success, string detail)
        {
            checks.Add(new { name, passed = success, detail });
            if (success) originalPassedCount++;
            Console.WriteLine($"{(success ? "PASS" : "FAIL")} {name}: {detail}");
            if (!success) throw new InvalidOperationException("Check failed: " + name);
        }
    }

    private static SKColor DesktopColor(Native.Point point)
    {
        var dc = Native.GetDC(0);
        try
        {
            var color = Native.GetPixel(dc, point.X, point.Y);
            if (color == 0xffffffff) throw new InvalidOperationException("GetPixel on the self-owned marker failed.");
            return new((byte)(color & 255), (byte)(color >> 8 & 255), (byte)(color >> 16 & 255), 255);
        }
        finally { Native.ReleaseDC(0, dc); }
    }

    private static bool White(SKColor color) => color.Red >= 250 && color.Green >= 250 && color.Blue >= 250;
    private static bool Blue(SKColor color) => Math.Abs(color.Red - 40) <= 3 &&
        Math.Abs(color.Green - 110) <= 3 && Math.Abs(color.Blue - 240) <= 3;
}
