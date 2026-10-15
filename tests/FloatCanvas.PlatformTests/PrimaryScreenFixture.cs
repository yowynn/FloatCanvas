using System.Diagnostics;
using FloatCanvas.Windows;

namespace FloatCanvas.PlatformTests;

/// <summary>Opt-in scope checks; the extra monitor exists only in an injected enumeration.</summary>
internal sealed class PrimaryScreenFixture
{
    private readonly List<ScopeCheck> _checks = [];
    private readonly IReadOnlyList<MonitorInfo> _enumeration;
    private readonly MonitorInfo _realPrimary;
    private readonly int _physicalMonitorCount;

    private PrimaryScreenFixture(MonitorInfo realPrimary, int physicalMonitorCount)
    {
        _realPrimary = realPrimary;
        _physicalMonitorCount = physicalMonitorCount;
        var secondary = new MonitorInfo("FloatCanvas.Synthetic.Nonexistent." + Guid.NewGuid().ToString("N"),
            new PixelBounds(Math.Min(realPrimary.Bounds.Left, 0) - 2560, -720, 2560, 1440),
            new PixelBounds(Math.Min(realPrimary.Bounds.Left, 0) - 2560, -720, 2560, 1400), 192, 192, false);
        // Deliberately precede the real primary so selecting the first unfiltered item would fail.
        _enumeration = Array.AsReadOnly(new[] { secondary, realPrimary });
    }

    internal object Evidence => new
    {
        SyntheticTopology = true,
        PhysicalMonitorCount = _physicalMonitorCount,
        PhysicalMonitorCountSource = "GetSystemMetrics(SM_CMONITORS); synthetic monitor is not included",
        Scope = OverlayMonitorScope.PrimaryScreen.ToString(),
        RealPrimary = _realPrimary,
        InjectedEnumeration = _enumeration,
        AdditionalScopeCheckCount = _checks.Count,
        AdditionalScopePassedCount = _checks.Count(c => c.Passed),
        AdditionalScopeChecks = _checks,
        Limitations = "The non-existent negative-origin 200% secondary tests pre-allocation filtering only; this is not physical multi-monitor or mixed-DPI acceptance."
    };

    internal static OverlayHost CreateHost(out PrimaryScreenFixture fixture)
    {
        MonitorInfo primary;
        using (var discovery = new OverlayHost(OverlayMonitorScope.PrimaryScreen)) primary = discovery.PrimaryMonitor;
        var probe = fixture = new(primary, Native.GetSystemMetrics(80)); // SM_CMONITORS
        var host = new OverlayHost(OverlayMonitorScope.PrimaryScreen, () => probe._enumeration);
        try { probe.VerifyHost(host); return host; }
        catch { host.Dispose(); throw; }
    }

    private void VerifyHost(OverlayHost host)
    {
        Check("primary-screen-scope-selected", host.Scope == OverlayMonitorScope.PrimaryScreen,
            $"Production host scope={host.Scope}; injected enumeration has {_enumeration.Count} items with synthetic secondary first.");
        Check("primary-screen-active-monitors-filtered", host.Monitors.Count == 1 && host.Monitors[0] == _realPrimary,
            "The active collection contains the actual primary only, including its original bounds, work area and X/Y DPI.");
        Check("primary-screen-placement-monitor-preserved", host.PrimaryMonitor == _realPrimary,
            $"PrimaryMonitor={host.PrimaryMonitor.DeviceName}, bounds={host.PrimaryMonitor.Bounds}, DPI={host.PrimaryMonitor.DpiX}/{host.PrimaryMonitor.DpiY}.");
        var pairs = host.Diagnostics;
        Check("primary-screen-only-one-native-pair", pairs.Count == 1 && pairs[0].DisplayHandle != 0 &&
            pairs[0].InputHandle != 0 && pairs[0].DisplayHandle != pairs[0].InputHandle,
            $"Native display/input pairs={pairs.Count}; only the real primary owns an enumerated display/input pair.");
        foreach (var (name, handle) in new[] { ("display", pairs[0].DisplayHandle), ("input", pairs[0].InputHandle) })
        {
            var read = Native.GetWindowRect(handle, out var bounds);
            var dpi = Native.GetDpiForWindow(handle);
            Check("primary-screen-" + name + "-real-bounds-and-dpi", read &&
                bounds.Left == _realPrimary.Bounds.Left && bounds.Top == _realPrimary.Bounds.Top &&
                bounds.Right == _realPrimary.Bounds.Right && bounds.Bottom == _realPrimary.Bounds.Bottom && dpi == _realPrimary.DpiX,
                $"Actual HWND rectangle=({bounds.Left},{bounds.Top})-({bounds.Right},{bounds.Bottom}), DPI={dpi}; real primary={_realPrimary.Bounds}, DPI={_realPrimary.DpiX}.");
        }
    }

    internal void VerifyCaptureRequest(string stage, IReadOnlyList<MonitorInfo> requested) =>
        Check(stage + "-primary-only-dxgi-request", requested.Count == 1 && requested[0] == _realPrimary,
            $"CaptureService receives {requested.Count} active monitor(s); only the real primary is allowed, not the non-existent injected DXGI output.");

    internal void VerifyCaptureResult(string stage, CapturedDesktop captured) =>
        Check(stage + "-primary-only-dxgi-result", captured.Bounds == _realPrimary.Bounds && captured.Outputs.Count == 1 &&
            string.Equals(captured.Outputs[0].DeviceName, _realPrimary.DeviceName, StringComparison.OrdinalIgnoreCase) &&
            captured.Outputs[0].Bounds == _realPrimary.Bounds,
            $"Actual DXGI outputs={captured.Outputs.Count}, desktop bounds={captured.Bounds}; expected one real primary {_realPrimary.DeviceName}.");

    internal static async Task WaitForExternalActivation(NativeTestWindow target)
    {
        // The operator activates this exact owned window; no focus repair or synthetic click/key.
        Console.WriteLine($"WAITING_FOR_SELF_TARGET_ACTIVATION hwnd={target.Handle} pid={Environment.ProcessId} title=浮绘 · 自建输入验证窗口");
        var timer = Stopwatch.StartNew();
        while (Native.GetForegroundWindow() != target.Handle)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(45))
                throw new InvalidOperationException("Self-target activation timed out; no focus repair or input injection was attempted.");
            await Task.Delay(20);
        }
        Console.WriteLine($"SELF_TARGET_ACTIVE hwnd={target.Handle} pid={Environment.ProcessId}");
    }

    private void Check(string name, bool passed, string detail)
    {
        _checks.Add(new(name, passed, detail));
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} scope {name}: {detail}");
        if (!passed) throw new InvalidOperationException("Primary-screen scope check failed: " + name);
    }

    private sealed record ScopeCheck(string Name, bool Passed, string Detail);
}
