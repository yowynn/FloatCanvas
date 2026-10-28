using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;

internal static class WindowRobustnessTests
{
    internal static void Run(Action<string, bool> check)
    {
        DispatcherResponsivenessTests.Run(check);
        PresentationPickingOwnershipTests.Run(check);
        var work = new Rect(0, 0, 1000, 1000);
        check("popup tries above candidate before sliding an overflowing below candidate over its anchor",
            OverlayPopupPlacement.Place([new(20, 984), new(20, 746)], new(180, 200), work, new()) == new Point(20, 746));
        check("popup preferred candidate is stable when several choices fit",
            OverlayPopupPlacement.Place([new(20, 20), new(40, 40)], new(180, 200), work, new()) == new Point(20, 20));
        check("popup offset participates in candidate selection",
            OverlayPopupPlacement.Place([new(20, 799), new(20, 500)], new(180, 200), work, new(0, 4)) == new Point(20, 504));
        check("popup positive offset is constrained before returning the final rectangle",
            OverlayPopupPlacement.Place([new(900, 900)], new(100, 100), work, new(4, 4)) == new Point(900, 900));
        check("popup negative offset is constrained before returning the final rectangle",
            OverlayPopupPlacement.Place([new(0, 0)], new(100, 100), work, new(-4, -4)) == new Point(0, 0));
        check("popup chooses greatest visible candidate when none fit",
            OverlayPopupPlacement.Place([new(980, 980), new(20, 900)], new(180, 200), work, new()) == new Point(20, 800));
        check("oversized popup pins to the work area's top-left instead of a negative clamp range",
            OverlayPopupPlacement.Place([new(900, 900)], new(1400, 1200), work, new()) == new Point(0, 0));
        foreach (var scales in new[] { new Vector(1, 1), new Vector(1.5, 1.5), new Vector(2, 2), new Vector(1.25, 1.75) })
        {
            var secondary = new Rect(-1920, -300, 1920, 1080);
            var size = new Size(Math.Ceiling(200 * scales.X), Math.Ceiling(160 * scales.Y));
            foreach (var offset in new[] { new Vector(6, 6), new Vector(-6, -6) })
            {
                var physicalOffset = new Vector(offset.X * scales.X, offset.Y * scales.Y);
                foreach (var point in new[] { secondary.TopLeft, secondary.TopRight, secondary.BottomLeft, secondary.BottomRight })
                {
                    var result = OverlayPopupPlacement.Place([point], size, secondary, physicalOffset);
                    check($"popup bounds include offsets on every negative-coordinate monitor edge ({scales}, {point}, {offset})",
                        secondary.Contains(new Rect(result, size)));
                }
            }
        }
        bool nativeCalled = false;
        var fallbackResult = AppDialog.ShowCore(null, "Original failure", "Failure", MessageBoxButton.OKCancel, MessageBoxImage.Error,
            () => throw new InvalidOperationException("Injected WPF construction failure"), (owner, message, title, flags) =>
            {
                nativeCalled = true;
                check("dialog construction failure preserves original message/buttons without constructing WPF fallback content",
                    owner == 0 && message == "Original failure" && (flags & 0xFF) == 0x11);
                return (int)MessageBoxResult.Cancel;
            });
        check("native fallback propagates the native result through the shared native modal route", nativeCalled && fallbackResult == MessageBoxResult.Cancel);
    }

    internal static void RunNative(Action<string, bool> check)
    {
        var desktop = new System.Text.StringBuilder(256);
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, desktop, 512, out _) ||
            !desktop.ToString().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Use --isolated-ui-desktop --window-robustness-only; the fixture never switches the visible desktop.");
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Run(check);
        var results = new List<(string Name, bool Passed)>();
        void Observe(string name, bool passed) { results.Add((name, passed)); Console.WriteLine($"WINDOW_ROBUSTNESS {passed}: {name}"); }
        NativePlacement(Observe);
        NativeOrder(Observe);
        ModalExit(Observe);
        NativeFallbackLifetime(Observe);
        PublicationHandshake(Observe);
        DispatcherResponsivenessTests.RunNative(Observe);
        foreach (var result in results) check(result.Name, result.Passed);
    }

    private static void NativePlacement(Action<string, bool> check)
    {
        var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var target = new Border { Width = 60, Height = 30 };
        var owner = new Window { Content = target, Width = 60, Height = 30, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Left = work.Left + 30, Top = work.Bottom - 70,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        owner.Show(); owner.UpdateLayout();
        SetWindowPos(new WindowInteropHelper(owner).Handle, 0, work.Left + 30, work.Bottom - 70, 0, 0, 0x215);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(target);
        using var scope = new OverlayWindowScope(new WindowInteropHelper(owner).Handle, () => false, installAsCurrent: false);
        try
        {
            var child = new Border { Width = 120, Height = 200 };
            using var popup = new OverlayPopupWindow(child, target, PlacementMode.Custom, 0, 4, () => { },
                (size, anchor, _) => [new(new Point(0, anchor.Height + 4), PopupPrimaryAxis.Horizontal),
                    new(new Point(0, -size.Height - 4), PopupPrimaryAxis.Horizontal)], ownerScope: scope, returnFocus: false);
            var source = (HwndSource)typeof(OverlayPopupWindow).GetField("source", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(popup)!;
            GetWindowRect(source.Handle, out var actual);
            double expected = target.PointToScreen(new(0, -200 - 4)).Y + 4 * dpi.DpiScaleY;
            Console.WriteLine($"POPUP_GEOMETRY actual={actual.Left},{actual.Top},{actual.Right},{actual.Bottom} expectedTop={expected} dpi={dpi.DpiScaleX},{dpi.DpiScaleY} anchor={target.PointToScreen(new Point())} anchorSize={target.RenderSize} work={work}");
            check("custom popup chooses its fully visible above-anchor fallback instead of clamping the first choice across its anchor", Math.Abs(actual.Top - expected) <= 1);
            check("popup final offsets remain in the work area", actual.Bottom <= work.Bottom);
        }
        finally { owner.Close(); }
    }

    private static void NativeOrder(Action<string, bool> check)
    {
        nint first = CreateOwn(), peer = CreateOwn(), popup = CreateOwn(first);
        nint foreground = GetForegroundWindow();
        try
        {
            SetWindowPos(first, -1, 0, 0, 0, 0, 0x213);
            SetWindowPos(peer, -1, 0, 0, 0, 0, 0x213);
            SetWindowPos(popup, -1, 0, 0, 0, 0, 0x213);
            var before = Ordered(first, peer, popup);
            OverlayCoordinates.PositionToolWindow(first, new(-16000, -15000));
            check("position-only operation retains the complete owner/peer/popup relative order", before.SequenceEqual(Ordered(first, peer, popup)));
            SetWindowPos(popup, peer, 0, 0, 0, 0, 0x213);
            var ownerPeers = Ordered(first, peer);
            OverlayCoordinates.RaiseToolWindow(popup);
            check("raising a popup preserves the relative order of its owner and peer", ownerPeers.SequenceEqual(Ordered(first, peer)));
            check("position and raise do not take foreground", GetForegroundWindow() == foreground);
        }
        finally { DestroyWindow(popup); DestroyWindow(peer); DestroyWindow(first); }
    }

    private static void ModalExit(Action<string, bool> check)
    {
        var bounds = new PixelBounds(-20000, -20000, 500, 500);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("modal-robustness", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        var toolbar = NewWindow("toolbar"); toolbar.Show();
        using var coordinator = new SessionWindowCoordinator(host, toolbar);
        var outer = NewWindow("outer", toolbar);
        new WindowInteropHelper(outer).EnsureHandle();
        var inner = NewWindow("inner", outer);
        var order = new List<string>(); bool cleanupByTest = false;
        outer.Closed += (_, _) => order.Add("outer"); inner.Closed += (_, _) => order.Add("inner");
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timeout.Tick += (_, _) => { timeout.Stop(); cleanupByTest = true; inner.Close(); outer.Close(); };
        outer.Loaded += (_, _) => outer.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => SessionWindowCoordinator.ShowModal(inner));
        inner.Loaded += (_, _) => inner.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => coordinator.SuspendEditingWindows());
        try
        {
            timeout.Start(); SessionWindowCoordinator.ShowModal(outer);
            check("mode exit itself unwinds nested modal dialogs without test-side cancellation", !cleanupByTest && order.SequenceEqual(new[] { "inner", "outer" }));
            check("nested modal exit reenables the toolbar HWND", IsWindowEnabled(new WindowInteropHelper(toolbar).Handle));
            check("modal exit leaves no visible dialog or disabled owner", !inner.IsVisible && !outer.IsVisible && toolbar.IsEnabled);
            check("nested modal exit completely balances the thread's modal stack", !ComponentDispatcher.IsThreadModal);
        }
        finally { timeout.Stop(); if (inner.IsVisible) inner.Close(); if (outer.IsVisible) outer.Close(); toolbar.Close(); }
    }

    private static void NativeFallbackLifetime(Action<string, bool> check)
    {
        var bounds = new PixelBounds(-20000, -20000, 500, 500);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("fallback-robustness", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        var toolbar = NewWindow("fallback toolbar");
        bool released = false; int releases = 0, restores = 0;
        using var coordinator = new SessionWindowCoordinator(host, toolbar, () =>
        {
            released = true; releases++;
            return new Lifetime(() => { released = false; restores++; });
        });
        try
        {
            var result = AppDialog.ShowCore(null, "original failure", "Failure", MessageBoxButton.OK, MessageBoxImage.Error,
                () => throw new InvalidOperationException("WPF factory fault"), (_, _, _, _) =>
                {
                    check("ownerless WPF construction fallback hands input off before entering native message box", released);
                    return (int)MessageBoxResult.OK;
                });
            check("native fallback restores its input scope once after the message loop returns", result == MessageBoxResult.OK && !released && releases == 1 && restores == 1);
            var expected = new System.IO.IOException("native message box fault");
            Exception? observed = null;
            try
            {
                AppDialog.ShowCore(toolbar, "original failure", "Failure", MessageBoxButton.OK, MessageBoxImage.Error,
                    () => throw new InvalidOperationException("WPF factory fault"), (_, _, _, _) =>
                    { check("owned WPF construction fallback also hands input off before native entry", released); throw expected; });
            }
            catch (Exception error) { observed = error; }
            check("native fallback failure releases the scope without replacing the original native error", ReferenceEquals(observed, expected) && !released && releases == 2 && restores == 2);
        }
        finally { toolbar.Close(); }
    }

    private static void PublicationHandshake(Action<string, bool> check)
    {
        var bounds = new PixelBounds(-20000, -20000, 100, 100);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("publication-handshake", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        nint foreground = GetForegroundWindow();
        check("hidden canvas cannot acknowledge a preview as presented", !host.TryRenderRegions([bounds]));
        host.SetVisible(true);
        check("successful native output submission acknowledges a preview as presented", host.TryRenderRegions([bounds]));
        check("nonintersecting dirty output cannot acknowledge an unsubmitted preview", !host.TryRenderRegions([new(0, 0, 10, 10)]));
        bool nestedResult = true;
        Action<SkiaSharp.SKSurface> draw = _ => nestedResult = host.TryRenderRegions([bounds]);
        host.Draw += draw;
        try { check("outer submission succeeds while reentrant submission is explicitly skipped", host.TryRenderRegions([bounds]) && !nestedResult); }
        finally { host.Draw -= draw; }
        var expected = new InvalidOperationException("private submission preparation fault");
        Action<OverlayFrameRequest> prepare = _ => throw expected;
        host.FrameRendering += prepare;
        Exception? observed = null;
        try { host.Render(); }
        catch (Exception error) { observed = error; }
        finally { host.FrameRendering -= prepare; }
        check("failed frame preparation releases the reentrancy guard for the next native submission", ReferenceEquals(observed, expected) && host.TryRenderRegions([bounds]));
        check("offscreen publication handshake does not take foreground", GetForegroundWindow() == foreground);
    }

    private sealed class Lifetime(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    private static Window NewWindow(string title, Window? owner = null) => new() { Title = title, Owner = owner,
        Content = new TextBlock { Text = title }, Width = 200, Height = 100, Left = -20000, Top = -20000,
        ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
    private static nint CreateOwn(nint owner = 0)
    {
        nint handle = CreateWindowEx(0x08000088, "STATIC", "FloatCanvas window robustness", 0x80000000,
            -20000, -20000, 50, 50, owner, 0, 0, 0);
        if (handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        ShowWindow(handle, 8); return handle;
    }
    private static nint[] Ordered(params nint[] handles)
    {
        var order = new List<nint>();
        for (nint window = GetTopWindow(0); window != 0; window = GetWindow(window, 2)) if (handles.Contains(window)) order.Add(window);
        return order.ToArray();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extendedStyle, string cls, string name, uint style, int x, int y, int width, int height, nint owner, nint menu, nint module, nint args);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint handle);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, System.Text.StringBuilder value, int length, out int needed);
}
