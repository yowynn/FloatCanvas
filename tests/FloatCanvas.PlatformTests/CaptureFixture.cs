using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
        var observations = new List<object>();
        var errors = new List<string>();
        var passed = false;
        var originalPassedCount = 0;
        MonitorInfo[] monitors = [];
        NativeTestWindow? target = null;
        CaptureLowerPeer? lowerPeer = null;
        OverlayHost? host = null;
        PrimaryScreenFixture? primaryProbe = null;
        try
        {
            // The lower application has a different, verified process identity. An in-process
            // topmost target would correctly be protected above the overlay as an editor tool.
            lowerPeer = await CaptureLowerPeer.StartAsync(Path.GetDirectoryName(evidencePath)!);
            target = lowerPeer.Target;
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
            Observe("known-background", marker);
            host = primaryScreenProbe ? PrimaryScreenFixture.CreateHost(out primaryProbe) : new OverlayHost();
            monitors = host.Monitors.ToArray();
            host.FrameSubmitted += frame =>
            {
                var value = new { stage = "submitted-frame", frame.Succeeded, frame.Outputs, host.PresentedFrameCount, host.LastRenderedPixelCount };
                observations.Add(value);
            };
            host.Error += ex => errors.Add(ex.ToString());
            host.Draw += surface =>
            {
                var canvas = surface.Canvas;
                using var paint = new SKPaint { Color = new SKColor(40, 110, 240), IsAntialias = false };
                canvas.DrawRect(marker.X - 32, marker.Y - 32, 64, 64, paint);
                using var pixel = new SKBitmap(1, 1);
                var point = canvas.TotalMatrix.MapPoint(marker.X, marker.Y);
                bool read = surface.ReadPixels(pixel.Info, pixel.GetPixels(), pixel.RowBytes, (int)point.X, (int)point.Y);
                var color = read ? pixel.GetPixel(0, 0) : default;
                var value = new { stage = "own-surface-marker", read, point.X, point.Y, color.Red, color.Green, color.Blue, color.Alpha };
                observations.Add(value);
            };
            host.SetVisible(true);
            using var service = new CaptureService();
            foreach (var edit in new[] { false, true })
            {
                var stateName = edit ? "editing" : "passthrough";
                if (edit)
                {
                    var click = target.ScreenPoint(420, 340);
                    Check("owned-lower-foreground-point-is-white", White(DesktopColor(click)), "The real foreground grant starts at a white point of the verified owned lower window.");
                    await lowerPeer.GrantForegroundAsync(click, host.Diagnostics.Single(pair => pair.Bounds.Contains(new(click.X, click.Y))).DisplayHandle);
                }
                host.SetEditing(edit);
                host.Render();
                await Task.Delay(100);
                Observe(stateName + "-before-capture", marker);
                var visibleColor = DesktopColor(marker);
                Check(stateName + "-opaque-marker-visible-before-capture", Blue(visibleColor),
                    $"Actual desktop RGB={visibleColor.Red},{visibleColor.Green},{visibleColor.Blue}; opaque marker should be blue.");
                CaptureFrameBoundary boundary;
                CaptureVisibilityStrategy strategy = default;
                long editingEpoch = host.EditingSessionVersion;
                nint foreground = Native.GetForegroundWindow();
                var originalWindows = ReadWindows(host);
                WindowPolicy[] capturedWindows = [];
                CapturedDesktop desktop;
                var timer = Stopwatch.StartNew();
                try
                {
                    using var scope = host.BeginCleanCapture();
                    boundary = scope.FrameBoundary; strategy = scope.Strategy; capturedWindows = ReadWindows(host);
                    Check(stateName + "-capture-scope-preserves-mode-and-excludes-host", host.IsCapturing && host.IsVisible && host.IsEditing == edit &&
                        host.EditingSessionVersion == editingEpoch && Native.GetForegroundWindow() == foreground &&
                        CapturedWindowPolicy(strategy, originalWindows, capturedWindows, monitors),
                        $"Strategy={strategy}; logical visibility/editing, foreground and epoch {editingEpoch} stay unchanged; own HWND affinity/visibility/geometry satisfy this strategy.");
                    Check(stateName + "-capture-supplies-typed-frame-boundary", boundary.Timestamp > 0 &&
                        boundary.Requirement == CaptureFrameRequirement.CurrentDesktopFromNewDuplication,
                        $"Typed frame boundary={boundary.Timestamp}, requirement={boundary.Requirement}.");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var requestedMonitors = host.Monitors.ToArray();
                    primaryProbe?.VerifyCaptureRequest(stateName, requestedMonitors);
                    desktop = await service.CaptureAsync(requestedMonitors, boundary, timeout.Token);
                    primaryProbe?.VerifyCaptureResult(stateName, desktop);
                }
                finally
                {
                    Check(stateName + "-capture-restores-host-state", !host.IsCapturing && host.IsVisible && host.IsEditing == edit &&
                        host.EditingSessionVersion == editingEpoch && ReadWindows(host).SequenceEqual(originalWindows),
                        "Disposing the production scope restores owned affinity, geometry and visibility without changing the editing epoch.");
                }
                Check(stateName + "-all-output-frames-are-fresh", desktop.Outputs.Count == monitors.Length &&
                    desktop.Outputs.All(o => o.FrameRequirement == boundary.Requirement && o.FrameBoundaryTimestamp == boundary.Timestamp &&
                        o.DuplicationStartedTimestamp >= boundary.Timestamp && o.AcquiredTimestamp >= o.DuplicationStartedTimestamp &&
                        o.LastPresentTimestamp > 0 && o.LastPresentTimestamp <= o.AcquiredTimestamp &&
                        (boundary.Requirement != CaptureFrameRequirement.RequirePresentAfterBoundary || o.LastPresentTimestamp >= boundary.Timestamp)),
                    $"All {desktop.Outputs.Count} outputs belong to a new duplication after boundary {boundary.Timestamp}, with valid acquired/last-present timestamps under {boundary.Requirement}.");
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
                captures.Add(new { mode = stateName, frameBoundary = boundary, strategy, editingEpoch, foreground = foreground.ToInt64(), originalWindows, capturedWindows, capturedAtUtc = desktop.CapturedAtUtc,
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
            long cancellationEpoch = host.EditingSessionVersion;
            var cancellationWindows = ReadWindows(host);
            using (var scope = host.BeginCleanCapture())
            {
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                var requestedMonitors = host.Monitors.ToArray();
                primaryProbe?.VerifyCaptureRequest("cancellation", requestedMonitors);
                try { await service.CaptureAsync(requestedMonitors, scope.FrameBoundary, canceled.Token); }
                catch (OperationCanceledException) { cancellationObserved = true; }
            }
            Check("capture-cancellation-is-explicit", cancellationObserved,
                "Production capture returns cancellation as a failure, not a cached/blank success or skipped test.");
            Check("capture-cancellation-restores-edit-mode", !host.IsCapturing && host.IsVisible && host.IsEditing &&
                host.EditingSessionVersion == cancellationEpoch && ReadWindows(host).SequenceEqual(cancellationWindows),
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
            var foreground = Native.GetForegroundWindow();
            bool ownedForeground = foreground != 0 && (foreground == target?.Handle || host?.Diagnostics.Any(pair =>
                pair.DisplayHandle == foreground || pair.InputHandle == foreground) == true);
            if (ownedForeground && previousForeground != 0) Native.SetForegroundWindow(previousForeground);
            host?.Dispose();
            target?.Dispose();
            lowerPeer?.Dispose();
            Native.SetCursorPos(previousCursor.X, previousCursor.Y);
            Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, generatedAt = DateTimeOffset.Now, os = Environment.OSVersion.VersionString,
                test = "M1 production capture exclusion", actualMonitors = monitors,
                originalCheckCount = checks.Count,
                originalPassedCount,
                primaryScreenProbe = primaryProbe?.Evidence,
                lowerApplication = lowerPeer?.Evidence, checks, captures, observations, errors, passed,
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

        void Observe(string stage, Native.Point marker)
        {
            var own = host?.Diagnostics ?? [];
            var handles = own.SelectMany(row => new[] { row.DisplayHandle, row.InputHandle }).Append(target!.Handle).ToHashSet();
            var windows = new List<object>();
            nint window = ProbeNative.GetTopWindow(0); int rank = 0;
            for (; window != 0 && rank < 8192; window = ProbeNative.GetWindow(window, 2), rank++)
            {
                if (!handles.Contains(window)) continue;
                ProbeNative.GetWindowThreadProcessId(window, out uint process);
                bool affinityRead = ProbeNative.GetWindowDisplayAffinity(window, out uint affinity);
                Native.GetWindowRect(window, out var bounds);
                windows.Add(new { handle = window.ToInt64(), process, rank, target = window == target.Handle,
                    style = Native.ExtendedStyle(window).ToInt64(), visible = Native.IsWindowVisible(window),
                    affinityRead, affinity, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, dpi = Native.GetDpiForWindow(window) });
            }
            var freshMarker = target.ScreenPoint(160, 180); var foreground = Native.GetForegroundWindow(); var hit = Native.WindowFromPoint(marker);
            ProbeNative.GetWindowThreadProcessId(hit, out uint hitProcess); ProbeNative.GetWindowThreadProcessId(foreground, out uint foregroundProcess);
            var dpiContext = ProbeNative.GetThreadDpiAwarenessContext();
            var value = new { stage, process = Environment.ProcessId, marker = new { marker.X, marker.Y }, freshMarker = new { freshMarker.X, freshMarker.Y },
                dpiContext = dpiContext.ToInt64(), dpiAwareness = ProbeNative.GetAwarenessFromDpiAwarenessContext(dpiContext),
                foreground = foreground.ToInt64(), foregroundProcess, hit = hit.ToInt64(), hitProcess,
                desktopPixel = DesktopColor(marker).ToString(), hostFrames = host?.PresentedFrameCount, windows };
            observations.Add(value); Console.WriteLine("CAPTURE_OBSERVATION " + JsonSerializer.Serialize(value));
        }
    }

    private sealed record WindowPolicy(long Handle, bool Input, bool Visible, PixelBounds Bounds, uint Affinity);
    private static WindowPolicy[] ReadWindows(OverlayHost host) => host.Diagnostics.SelectMany(pair => new[]
    {
        Read(pair.DisplayHandle, false), Read(pair.InputHandle, true)
    }).ToArray();
    private static WindowPolicy Read(nint handle, bool input)
    {
        if (!Native.GetWindowRect(handle, out var bounds) || !ProbeNative.GetWindowDisplayAffinity(handle, out uint affinity))
            throw new InvalidOperationException("Cannot read the owned capture HWND policy.");
        ProbeNative.GetWindowThreadProcessId(handle, out uint process);
        if (process != Environment.ProcessId) throw new InvalidOperationException("Capture HWND does not belong to this fixture host.");
        return new(handle.ToInt64(), input, Native.IsWindowVisible(handle), new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), affinity);
    }
    private static bool CapturedWindowPolicy(CaptureVisibilityStrategy strategy, WindowPolicy[] original, WindowPolicy[] captured, MonitorInfo[] monitors)
    {
        if (captured.Length != original.Length) return false;
        return original.All(before => captured.SingleOrDefault(after => after.Handle == before.Handle) is { } after &&
            (strategy switch
            {
                CaptureVisibilityStrategy.ExcludeVisible => after.Visible == before.Visible && after.Bounds == before.Bounds && after.Affinity == 0x11,
                CaptureVisibilityStrategy.HideVisuals => after.Input
                    ? after.Visible == before.Visible && after.Bounds == before.Bounds && after.Affinity == 0x11
                    : !after.Visible || after.Affinity == 0x11,
                CaptureVisibilityStrategy.LegacyInputOffscreen => after.Input
                    ? after.Visible == before.Visible && (!before.Visible || monitors.All(monitor =>
                        after.Bounds.Right <= monitor.Bounds.Left || after.Bounds.Left >= monitor.Bounds.Right ||
                        after.Bounds.Bottom <= monitor.Bounds.Top || after.Bounds.Top >= monitor.Bounds.Bottom))
                    : !after.Visible,
                _ => false
            }));
    }

    private static class ProbeNative
    {
        [DllImport("user32.dll")] internal static extern nint GetTopWindow(nint window);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
        [DllImport("user32.dll")] internal static extern nint GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] internal static extern int GetAwarenessFromDpiAwarenessContext(nint context);
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
