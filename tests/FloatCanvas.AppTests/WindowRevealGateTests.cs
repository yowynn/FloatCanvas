using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;

/// <summary>First visible frames of reused per-pixel windows; owned windows only, no injected input.</summary>
internal static class WindowRevealGateTests
{
    /// <returns>False when the screen-pixel checks could not run; callers must not report them as passed.</returns>
    internal static bool Run(Action<string, bool> check)
    {
        var border = new Border { Background = Brushes.Red };
        var window = Fixture(border);
        try
        {
            window.Show(); Pump(250);
            var handle = new WindowInteropHelper(window).Handle;
            var center = window.PointToScreen(new Point(24, 24));
            int x = (int)center.X, y = (int)center.Y;
            if (WindowFromPoint(new POINT { X = x, Y = y }) != handle || Sample(x, y) != 0xFF0000)
            { Console.WriteLine("UNVERIFIED reveal pixel checks: the fixture window is not the visible top window at its sample point."); return false; }

            int stale = 0, late = 0, unpending = 0;
            var colors = new[] { (Brushes.Lime, 0x00FF00), (Brushes.Blue, 0x0000FF) };
            for (int trial = 0; trial < 12; trial++)
            {
                var (brush, rgb) = colors[trial % 2];
                window.Hide(); Pump(40); border.Background = brush; Pump(20);
                int? first = null;
                WindowRevealGate.Show(window, () => first = Sample(x, y));
                if (!WindowRevealGate.IsPending(window) || !window.IsVisible || WindowRevealGate.IsPresented(window)) unpending++;
                PumpUntil(() => first is not null, 1000);
                if (first != rgb) stale++;
                if (first is null) late++;
            }
            check("every hidden reused window is shown pending and unpresented before its current frame", unpending == 0);
            check("revealing a reused window never exposes its retained pre-hide frame", stale == 0);
            check("reveal completes without waiting for the timeout", late == 0);

            window.Hide(); Pump(40); border.Background = Brushes.Red; Pump(20);
            bool revealed = false;
            WindowRevealGate.Show(window, () => revealed = true);
            window.Hide();
            Pump(400);
            check("hiding a pending reveal cancels it and its callback", !revealed && !WindowRevealGate.IsPending(window) && !window.IsVisible);
            window.Show(); Pump(250);
            check("a cancelled reveal leaves the window uncloaked for direct shows", Sample(x, y) == 0xFF0000);

            check("concealing keeps WPF visibility but removes pixels and hit testing", WindowRevealGate.Conceal(window) && window.IsVisible &&
                WindowRevealGate.IsConcealed(window) && WindowFromPoint(new POINT { X = x, Y = y }) != handle);
            border.Background = Brushes.Blue; Pump(120);
            int? warm = null;
            WindowRevealGate.Show(window, () => warm = Sample(x, y));
            check("revealing a concealed rendered window is immediate and current", warm == 0x0000FF && !WindowRevealGate.IsConcealed(window));

            WindowRevealGate.Conceal(window);
            border.Background = Brushes.Lime; WindowRevealGate.MarkChanged(window);
            int? changed = null;
            WindowRevealGate.Show(window, () => changed = Sample(x, y));
            PumpUntil(() => changed is not null, 1000);
            check("a concealed window changed just before reveal waits for its new frame", changed == 0x00FF00);

            var previous = WindowRevealGate.Cloak;
            try
            {
                WindowRevealGate.Cloak = (_, _) => false;
                window.Hide(); Pump(40);
                bool fallback = false;
                WindowRevealGate.Show(window, () => fallback = true);
                check("without DWM cloaking the window falls back to a direct show", fallback && window.IsVisible && !WindowRevealGate.IsPending(window));
                check("without DWM cloaking conceal reports that the caller must hide", !WindowRevealGate.Conceal(window));
            }
            finally { WindowRevealGate.Cloak = previous; }

            // Hold the UI thread so no render callbacks arrive before the timer is due.
            window.Hide(); Pump(40);
            bool timedOut = false;
            WindowRevealGate.Show(window, () => timedOut = true);
            Thread.Sleep((int)WindowRevealGate.Timeout.TotalMilliseconds + 50);
            PumpUntil(() => timedOut, 1000);
            check("a reveal always completes within its bounded timeout", timedOut && !WindowRevealGate.IsPending(window) && Sample(x, y) == 0x00FF00);
            return true;
        }
        finally { window.Close(); }
    }

    /// <summary>Reveal state transitions through a scripted cloak; independent of what is on screen.</summary>
    internal static void RunState(Action<string, bool> check)
    {
        var window = Fixture(new Border { Background = Brushes.Red });
        var previousCloak = WindowRevealGate.Cloak;
        var previousDiagnostic = WindowRevealGate.Diagnostic;
        var events = new List<string>();
        bool cloaked = false, failUncloak = false;
        WindowRevealGate.Cloak = (_, value) => { if (!value && failUncloak) return false; cloaked = value; return true; };
        WindowRevealGate.Diagnostic = (name, _) => events.Add(name);
        try
        {
            int once = 0, other = 0;
            Action counted = () => once++;
            WindowRevealGate.Show(window, counted);
            check("a shown window is not presented while its reveal is pending", window.IsVisible && WindowRevealGate.IsPending(window) && !WindowRevealGate.IsPresented(window));
            WindowRevealGate.Show(window, counted);
            WindowRevealGate.Show(window, () => other++);
            PumpUntil(() => !WindowRevealGate.IsPending(window), 1000);
            check("repeated shows in one reveal cycle run each distinct callback once", once == 1 && other == 1 &&
                WindowRevealGate.IsPresented(window) && !cloaked);

            check("a concealed window is not presented", WindowRevealGate.Conceal(window) && !WindowRevealGate.IsPresented(window));
            window.Hide(); Pump(40);
            check("hiding a concealed window releases its cloak", !cloaked && !WindowRevealGate.IsConcealed(window));

            int revealed = 0;
            failUncloak = true;
            WindowRevealGate.Show(window, () => revealed++);
            PumpUntil(() => events.Contains("Window.UncloakFailed"), 1000);
            check("a failed uncloak is reported and stays pending without presenting or running callbacks",
                events.Contains("Window.UncloakFailed") && WindowRevealGate.IsPending(window) && !WindowRevealGate.IsPresented(window) && revealed == 0 && cloaked);
            failUncloak = false;
            PumpUntil(() => !WindowRevealGate.IsPending(window), 1000);
            check("a later uncloak retry presents the window and runs its callback once", revealed == 1 &&
                WindowRevealGate.IsPresented(window) && !cloaked && events.Contains("Window.UncloakRecovered"));

            window.Hide(); Pump(40);
            events.Clear(); revealed = 0; failUncloak = true;
            WindowRevealGate.Show(window, () => revealed++);
            PumpUntil(() => events.Contains("Window.UncloakAbandoned"), 3000);
            check("uncloak retries are bounded and an abandoned reveal is reported, not presented", events.Contains("Window.UncloakAbandoned") &&
                !WindowRevealGate.IsPending(window) && !WindowRevealGate.IsPresented(window) && revealed == 0 && cloaked);
            failUncloak = false;
            WindowRevealGate.Show(window);
            check("showing an abandoned window again retries and runs its pending callback", revealed == 1 &&
                WindowRevealGate.IsPresented(window) && !cloaked);
        }
        finally
        {
            window.Close();
            WindowRevealGate.Cloak = previousCloak;
            WindowRevealGate.Diagnostic = previousDiagnostic;
        }
    }

    /// <summary>Handle-first reveal must not break controls that attach hooks on SourceInitialized.</summary>
    internal static void RunCanvasTextInput(Action<string, bool> check)
    {
        var owner = Fixture(new Border { Background = Brushes.Transparent });
        owner.Width = 400; owner.Height = 220;
        CanvasTextInput? input = null;
        InlineTextEdit? edit = null;
        try
        {
            owner.Show(); Pump(100);
            using var renderer = new FloatCanvas.Rendering.SceneRenderer();
            var session = new FloatCanvas.Core.EditorSession();
            edit = new InlineTextEdit(session, null, default, new() { FontSize = 24 }, renderer);
            input = new CanvasTextInput(owner, edit, () => default);
            check("handle-first reveal keeps inline text input initialization and native hooks working", input is not null);
        }
        finally
        {
            input?.Dispose();
            edit?.Finish(false);
            owner.Close();
        }
    }

    internal static void RunPopupOpenFailure(Action<string, bool> check)
    {
        var owner = Fixture(new Border { Background = Brushes.White });
        var target = new Button { Content = "Open", Width = 120, Height = 36 };
        owner.Width = 360;
        owner.Height = 180;
        owner.Content = target;
        var popup = new WindowPopup(new TextBlock { Text = "Popup content" }, target, "Popup failure regression", 8);
        var previousCloak = WindowRevealGate.Cloak;
        try
        {
            owner.Show();
            Pump(100);
            WindowRevealGate.Cloak = (_, cloak) => cloak
                ? throw new InvalidOperationException("Injected DWM cloak failure.")
                : true;
            bool failed = false;
            try { popup.IsOpen = true; }
            catch (InvalidOperationException) { failed = true; }
            check("a failed popup show rolls back its logical open state", failed && !popup.IsOpen);

            WindowRevealGate.Cloak = previousCloak;
            popup.IsOpen = true;
            Pump(300);
            check("the same popup can reopen after a failed show without a stale half-open window", popup.IsOpen);
            popup.IsOpen = false;
            Pump(100);
            check("a popup reopened after failure still closes normally", !popup.IsOpen);
        }
        finally
        {
            WindowRevealGate.Cloak = previousCloak;
            if (popup.IsOpen) popup.IsOpen = false;
            owner.Close();
        }
    }

    internal static void RunOverlayPopupConstructionFailure(Action<string, bool> check)
    {
        var owner = Fixture(new Border { Background = Brushes.White });
        var target = new Button { Content = "Anchor", Width = 120, Height = 36 };
        owner.Width = 360;
        owner.Height = 180;
        owner.Content = target;
        var previousPolicy = OverlayWindowScope.NativeToolPolicyForTesting;
        OverlayWindowScope? scope = null;
        OverlayPopupWindow? popup = null;
        try
        {
            owner.Show();
            Pump(100);
            scope = new OverlayWindowScope(new WindowInteropHelper(owner).Handle, () => true, 16);
            int before = OverlayPopupWindow.OpenWindowCount;
            OverlayWindowScope.NativeToolPolicyForTesting = _ =>
                throw new System.ComponentModel.Win32Exception(5, "Injected tool-band failure.");
            bool failed = false;
            try
            {
                _ = new OverlayPopupWindow(new TextBlock { Text = "Content" }, target,
                    System.Windows.Controls.Primitives.PlacementMode.Bottom, 0, 0, () => { });
            }
            catch (System.ComponentModel.Win32Exception) { failed = true; }
            OverlayWindowScope.NativeToolPolicyForTesting = previousPolicy;
            check("failed overlay-popup construction releases its native window registration",
                failed && OverlayPopupWindow.OpenWindowCount == before);

            var wrapper = new OverlayPopup { Child = new TextBlock { Text = "Wrapper content" }, PlacementTarget = target };
            OverlayWindowScope.NativeToolPolicyForTesting = _ =>
                throw new System.ComponentModel.Win32Exception(5, "Injected tool-band failure.");
            bool wrapperFailed = false;
            try { wrapper.IsOpen = true; }
            catch (System.ComponentModel.Win32Exception) { wrapperFailed = true; }
            check("a failed overlay popup clears its logical open state",
                wrapperFailed && !wrapper.IsOpen && OverlayPopupWindow.OpenWindowCount == before);

            OverlayWindowScope.NativeToolPolicyForTesting = _ => { };
            popup = new OverlayPopupWindow(new TextBlock { Text = "Reusable content" }, target,
                System.Windows.Controls.Primitives.PlacementMode.Bottom, 0, 0, () => { });
            check("overlay popup construction can retry after a native-policy failure",
                OverlayPopupWindow.OpenWindowCount == before + 1);
            wrapper.IsOpen = true; Pump(100);
            check("the same overlay popup can reopen after a tool-band failure",
                wrapper.IsOpen && OverlayPopupWindow.OpenWindowCount == before + 2);
            wrapper.IsOpen = false; Pump(100);
            popup.Dispose();
            popup = null;
            OverlayWindowScope.NativeToolPolicyForTesting = previousPolicy;
            check("a retried overlay popup releases its registration", OverlayPopupWindow.OpenWindowCount == before);
        }
        finally
        {
            OverlayWindowScope.NativeToolPolicyForTesting = previousPolicy;
            popup?.Dispose();
            scope?.Dispose();
            owner.Close();
        }
    }

    private static Window Fixture(Border border) => new()
    {
        WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
        ShowInTaskbar = false, ShowActivated = false, Left = 24, Top = 24, Width = 48, Height = 48, Content = border
    };

    /// <summary>The production toolbar/launcher handoff and canvas-visibility state across both modes.</summary>
    internal static void RunSession(Action<string, bool> check)
    {
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        new UserSettings { ShowFloatingLogo = false, ShowPassThroughLauncher = true }.Save();
        var host = (FloatCanvas.Windows.OverlayHost)Activator.CreateInstance(typeof(FloatCanvas.Windows.OverlayHost), Private, null, [FloatCanvas.Windows.OverlayMonitorScope.PrimaryScreen, false], null)!;
        using var session = new DesktopSession(app, host);
        T Field<T>(string name) => (T)typeof(DesktopSession).GetField(name, Private)!.GetValue(session)!;
        void Invoke(string name, params object[] args) => typeof(DesktopSession).GetMethod(name, Private, args.Select(a => a.GetType()).ToArray())!.Invoke(session, args);
        Field<System.Windows.Forms.NotifyIcon>("_tray").Visible = false;
        var toolbar = Field<Window>("_toolbar");
        var launcher = Field<ToolbarLauncher>("_toolbarLauncher");
        Invoke("RefreshPassThroughLauncher");
        var passive = Field<PassThroughLauncherWindow>("_passThroughLauncher");
        var busy = typeof(DesktopSession).GetProperty("DocumentOperationBusy", Private)!;
        PumpUntil(() => !(bool)busy.GetValue(session)!, 5000);
        Pump(150);
        check("pass-through starts with the launcher genuinely shown", passive.IsVisible && !WindowRevealGate.IsConcealed(passive));

        if (!host.IsVisible) { typeof(DesktopSession).GetMethod("ToggleCanvasVisibility", Private)!.Invoke(session, null); Pump(80); }
        check("fixture starts from a visible canvas", host.IsVisible);
        typeof(DesktopSession).GetMethod("ToggleCanvasVisibility", Private)!.Invoke(session, null);
        Pump(80);
        check("hiding the canvas in pass-through updates the hidden toolbar's mascot", !host.IsVisible &&
            ReferenceEquals(((Image)launcher.Content).Source, AppIcon.LauncherImage(false, true)));
        check("hiding the canvas in pass-through updates the hidden toolbar's eye button",
            ((System.Windows.Automation.AutomationProperties.GetItemStatus(Field<Button>("_canvasVisibilityButton")))) == FloatCanvas.Localization.AppText.Get("Feedback4.Canvas.Hidden"));

        toolbar.ShowActivated = false;
        int toolbarRevealed = 0;
        SessionWindowCoordinator.Show(toolbar, revealed: () => toolbarRevealed++);
        check("the launcher stays shown while the toolbar's first current frame is pending", WindowRevealGate.IsPending(toolbar) &&
            passive.IsVisible && !WindowRevealGate.IsConcealed(passive));
        PumpUntil(() => !WindowRevealGate.IsPending(toolbar), 1000);
        check("the toolbar reveal callback runs once after its current frame", toolbarRevealed == 1 && toolbar.IsVisible);
        toolbar.Hide(); passive.Show(); Pump(80);

        passive.Conceal();
        check("concealing the launcher keeps it rendering but not presented", passive.IsVisible && WindowRevealGate.IsConcealed(passive));
        typeof(DesktopSession).GetMethod("ToggleCanvasVisibility", Private)!.Invoke(session, null);
        Pump(80);
        check("canvas visibility changes reach the concealed launcher", host.IsVisible &&
            ReferenceEquals(((Image)passive.Launcher.Content).Source, AppIcon.LauncherImage(true, false)));
        Invoke("RefreshPassThroughLauncher");
        check("returning to pass-through uncloaks the current launcher", passive.IsVisible && !WindowRevealGate.IsConcealed(passive));
        passive.Conceal(); Invoke("ToggleFloatingLogo");
        check("disabling the launcher genuinely hides a concealed launcher", !passive.IsVisible && !WindowRevealGate.IsConcealed(passive));
        Invoke("ToggleFloatingLogo");
        check("re-enabling the launcher shows it normally", passive.IsVisible && !WindowRevealGate.IsConcealed(passive));
        app.Shutdown();
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
    }

    private static void PumpUntil(Func<bool> condition, int timeout)
    {
        long deadline = Environment.TickCount64 + timeout;
        while (!condition() && Environment.TickCount64 < deadline) Pump(5);
    }

    private static int Sample(int x, int y)
    {
        DwmFlush();
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, 1, 1);
        nint old = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, 1, 1, screen, x, y, 0x40CC0020); // SRCCOPY | CAPTUREBLT
            uint color = GetPixel(memory, 0, 0);
            return (int)(((color & 0xFF) << 16) | (color & 0xFF00) | ((color >> 16) & 0xFF));
        }
        finally { SelectObject(memory, old); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(0, screen); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT point);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int operation);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
