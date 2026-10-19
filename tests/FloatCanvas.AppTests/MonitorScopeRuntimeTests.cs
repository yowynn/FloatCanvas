using System.Reflection;
using System.Runtime.InteropServices;
using FloatCanvas.Windows;
using FloatCanvas.App;

internal static class MonitorScopeRuntimeTests
{
    internal static void Run(Action<string, bool> check)
    {
        var oldDpi = SetThreadDpiAwarenessContext(-4);
        try
        {
            var inventory = (IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            Console.WriteLine("Read-only physical monitor inventory (PMv2): " + System.Text.Json.JsonSerializer.Serialize(inventory));
        }
        finally { if (oldDpi != 0) SetThreadDpiAwarenessContext(oldDpi); }
        var apply = typeof(OverlayHost).GetMethod("SetMonitorScope", [typeof(OverlayMonitorScope)]);
        check("Running host exposes an immediate monitor-scope operation", apply is not null);
        MonitorInfo primary = new("primary", new(20, 20, 48, 32), new(20, 20, 48, 32), 96, 96, true);
        MonitorInfo secondary = new("secondary", new(-48, 20, 48, 32), new(-48, 20, 48, 32), 144, 144, false);
        IReadOnlyList<MonitorInfo> layout = new[] { secondary, primary };
        int reads = 0;
        Func<IReadOnlyList<MonitorInfo>> provider = () => { reads++; return layout; };
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
        int changes = 0;
        host.MonitorsChanged += () => changes++;
        var foreground = GetForegroundWindow();
        var old = host.Diagnostics.Single();
        apply!.Invoke(host, [OverlayMonitorScope.AllScreens]);
        check("Changing scope immediately adds both real overlay pairs", host.Scope == OverlayMonitorScope.AllScreens && host.Monitors.Count == 2 && host.Diagnostics.Count == 2);
        check("New scope keeps physical coordinates and per-monitor DPI", host.Monitors[0] == primary && host.Monitors[1] == secondary);
        check("Scope rebuild destroys obsolete input and display handles", !IsWindow(old.InputHandle) && !IsWindow(old.DisplayHandle));
        check("Scope change notifies its owner exactly once", changes == 1);
        check("Hidden scope rebuild never shows an overlay or enables editing", !host.IsVisible && !host.IsEditing && host.Diagnostics.All(x => !x.DisplayVisible && !x.InputVisible));
        check("Hidden scope rebuild preserves the user's foreground", GetForegroundWindow() == foreground);
        var handles = host.Diagnostics.Select(x => x.DisplayHandle).ToArray();
        int beforeReads = reads;
        apply.Invoke(host, [OverlayMonitorScope.AllScreens]);
        check("Saving unchanged scope avoids native rebuild and notifications", reads == beforeReads && changes == 1 && handles.SequenceEqual(host.Diagnostics.Select(x => x.DisplayHandle)));
        apply.Invoke(host, [OverlayMonitorScope.PrimaryScreen]);
        check("Changing back immediately removes the secondary overlay", host.Scope == OverlayMonitorScope.PrimaryScreen && host.Monitors.Count == 1 && host.PrimaryMonitor == primary && changes == 2);
        bool invalid = false;
        try { apply.Invoke(host, [(OverlayMonitorScope)99]); }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException) { invalid = true; }
        check("Invalid scope is rejected without replacing live windows", invalid && host.Scope == OverlayMonitorScope.PrimaryScreen && changes == 2);
        handles = host.Diagnostics.Select(x => x.DisplayHandle).ToArray();
        layout = [];
        bool unavailable = false;
        try { apply.Invoke(host, [OverlayMonitorScope.AllScreens]); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { unavailable = true; }
        check("Failed enumeration preserves the previous scope, monitors and window handles", unavailable && host.Scope == OverlayMonitorScope.PrimaryScreen && host.PrimaryMonitor == primary && changes == 2 && handles.SequenceEqual(host.Diagnostics.Select(x => x.DisplayHandle)));
        layout = new[] { secondary, primary };
        int notificationsBeforeFailure = changes;
        handles = host.Diagnostics.Select(x => x.DisplayHandle).ToArray();
        int draws = 0;
        void FailFirstDraw(SkiaSharp.SKSurface _) { if (++draws == 1) throw new InvalidOperationException("Injected candidate render failure"); }
        host.Draw += FailFirstDraw;
        // Arm rendering without showing old or candidate HWNDs. The first Draw throws
        // before the candidate can reach its commit/show phase.
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
        bool renderingFailed = false;
        try { apply.Invoke(host, [OverlayMonitorScope.AllScreens]); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException failure && failure.Message == "Injected candidate render failure") { renderingFailed = true; }
        finally
        {
            host.Draw -= FailFirstDraw;
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, false);
        }
        check("Candidate Render failure retains the exact previous monitor HWNDs and scope", renderingFailed && draws == 1 && host.Scope == OverlayMonitorScope.PrimaryScreen && host.PrimaryMonitor == primary && handles.SequenceEqual(host.Diagnostics.Select(x => x.DisplayHandle)) && host.Diagnostics.All(p => IsWindow(p.DisplayHandle) && IsWindow(p.InputHandle)));
        check("Candidate Render failure never shows windows, changes foreground or publishes a layout", host.Diagnostics.All(p => !p.DisplayVisible && !p.InputVisible) && GetForegroundWindow() == foreground && changes == notificationsBeforeFailure);
        void InvalidateFirstShow() => DestroyWindow(host.Diagnostics[0].DisplayHandle);
        host.InputActivated += InvalidateFirstShow;
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
        bool showingFailed = false;
        try { apply.Invoke(host, [OverlayMonitorScope.AllScreens]); }
        catch (TargetInvocationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception) { showingFailed = true; }
        finally
        {
            host.InputActivated -= InvalidateFirstShow;
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, false);
        }
        check("Candidate first-show failure retains the exact previous monitor HWNDs and scope", showingFailed && host.Scope == OverlayMonitorScope.PrimaryScreen && host.PrimaryMonitor == primary && handles.SequenceEqual(host.Diagnostics.Select(x => x.DisplayHandle)) && host.Diagnostics.All(p => IsWindow(p.DisplayHandle) && IsWindow(p.InputHandle)));
        check("Candidate first-show failure keeps original windows hidden and does not publish a layout", host.Diagnostics.All(p => !p.DisplayVisible && !p.InputVisible) && GetForegroundWindow() == foreground && changes == notificationsBeforeFailure);
        using (var capture = host.BeginCleanCapture())
        {
            check("Hidden capture fixture establishes a native continuation without sampling pixels", host.IsCapturing);
            apply.Invoke(host, [OverlayMonitorScope.AllScreens]);
        }
        check("Scope rebuild cancels an in-flight native capture and keeps replacement windows hidden", !host.IsCapturing && !host.IsEditing && host.Diagnostics.All(x => !x.DisplayVisible && !x.InputVisible));
        apply.Invoke(host, [OverlayMonitorScope.PrimaryScreen]);
        var session = (DesktopSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        var settings = new UserSettings { PrimaryScreenOnly = false };
        typeof(DesktopSession).GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, host);
        typeof(DesktopSession).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, settings);
        var originalProfile = ApplicationPaths.ProfileDirectory;
        try
        {
            ApplicationPaths.Configure(["--profile", originalProfile]);
            session.ApplyMonitorScopeSetting();
            check("Production settings apply path expands a running primary host immediately", host.Scope == OverlayMonitorScope.AllScreens && host.Monitors.Count == 2);
            ApplicationPaths.Configure(["--profile", originalProfile, "--all-screens"]);
            settings.PrimaryScreenOnly = true;
            session.ApplyMonitorScopeSetting();
            check("Explicit all-screen launch override remains authoritative when saving primary preference", host.Scope == OverlayMonitorScope.AllScreens && settings.PrimaryScreenOnly);
            ApplicationPaths.Configure(["--profile", originalProfile, "--primary-screen"]);
            settings.PrimaryScreenOnly = false;
            session.ApplyMonitorScopeSetting();
            check("Explicit primary launch override remains authoritative when saving all-screen preference", host.Scope == OverlayMonitorScope.PrimaryScreen && !settings.PrimaryScreenOnly);
            ApplicationPaths.Configure(["--profile", originalProfile]);
            session.ApplyMonitorScopeSetting();
            settings.PrimaryScreenOnly = true;
            session.ApplyMonitorScopeSetting();
            check("Production settings apply path can switch back immediately without an override", host.Scope == OverlayMonitorScope.PrimaryScreen && host.Monitors.Count == 1);
        }
        finally { ApplicationPaths.Configure(["--profile", originalProfile]); }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint handle);
}
