using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FloatCanvas.Windows;

/// <summary>Bounded registration metadata only, on a never displayed desktop. No input injection.</summary>
internal static class PresentationNativeInputTests
{
    private const uint InputSink = 0x100, DeviceNotify = 0x2000, Remove = 1;
    private static readonly nint MessageOnly = new(-3);

    internal static void Run(Action<string, bool> check)
    {
        string desktop = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
        if (!desktop.StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal) || PresentationDesktopAvailability.IsCurrentInputDesktop())
            throw new InvalidOperationException("Use --isolated-ui-desktop --presentation-native-only. This fixture must not run on the user's input desktop.");
        check("presentation native fixture runs on a separate inactive desktop", !PresentationDesktopAvailability.IsCurrentInputDesktop());
        check("presentation native fixture starts without process raw-input registrations", Registrations().Length == 0);
        using var fixture = new MessageWindow();
        check("foreign registration fixture owns an invisible message-only window", IsOwnMessageWindow(fixture.Handle) && !IsWindowVisible(fixture.Handle));
        Console.WriteLine($"PRESENTATION NATIVE desktop={desktop}; process={Environment.ProcessId}; fixture=0x{fixture.Handle:X}");
        try
        {
            fixture.Register(2, InputSink | DeviceNotify);
            var calibration = Registrations().Single(item => item.Page == 1 && item.Usage == 2);
            Console.WriteLine($"PRESENTATION NATIVE independent calibration requested=0x{InputSink | DeviceNotify:X}; reported=0x{calibration.Flags:X}; targetOwn={calibration.Target == fixture.Handle}");
            check("independent native calibration preserves INPUTSINK without adding legacy-suppression flags", calibration.Target == fixture.Handle && (calibration.Flags & InputSink) != 0 && (calibration.Flags & ~(InputSink | DeviceNotify)) == 0);
            // GetRegisteredRawInputDevices omits DEVNOTIFY on this host even for an explicit 0x2100 call.
            // Compare with the independent native call rather than claiming that API proves notification delivery.
            fixture.RemoveOwnedRegistrations();
            RejectForeign(fixture, check);
            KeyboardOnly(fixture, calibration.Flags, check);
            StopOwned(fixture, calibration.Flags, check);
            PreserveReplacement(fixture, check);
            InactiveDesktop(calibration.Flags, check);
        }
        finally { fixture.RemoveOwnedRegistrations(); }
        check("presentation native fixture leaves no raw-input registration behind", Registrations().Length == 0);
        check("presentation native fixture leaves no observer message-only window behind", WaitForNoObserverWindows());
    }

    private static void RejectForeign(MessageWindow fixture, Action<string, bool> check)
    {
        fixture.Register(2);
        var expected = Registrations();
        check("foreign mouse registration is established before observer startup", expected is [{ Page: 1, Usage: 2, Flags: 0 }] && expected[0].Target == fixture.Handle);
        PresentationInputObserver? unexpected = null; Exception? failure = null;
        try { unexpected = PresentationInputObserver.StartAsync(101, keyboard: false, pointer: true).GetAwaiter().GetResult(); }
        catch (Exception exception) { failure = exception; }
        finally
        {
            if (unexpected is not null) { unexpected.Stop(); unexpected.Completion.Wait(TimeSpan.FromSeconds(5)); unexpected.Dispose(); }
        }
        check("mouse-only observer rejects an existing foreign registration of the same class", failure is InvalidOperationException);
        check("rejected observer startup preserves the foreign mouse registration exactly", Same(expected, Registrations()));
        check("rejected observer startup destroys its own message-only carrier", WaitForNoObserverWindows());
        fixture.RemoveOwnedRegistrations();
        check("foreign fixture unregisters only its own test mouse class", Registrations().Length == 0);
    }

    private static void StopOwned(MessageWindow fixture, uint reportedPassiveFlags, Action<string, bool> check)
    {
        fixture.Register(5); // An unrelated, unattached gamepad TLC; no device input is read.
        var unrelated = Registrations();
        using var observer = PresentationInputObserver.StartAsync(202, keyboard: false, pointer: true).GetAwaiter().GetResult();
        nint carrier = 0;
        try
        {
            var registered = Registrations(); var mouse = registered.Single(item => item.Page == 1 && item.Usage == 2); carrier = mouse.Target;
            Console.WriteLine("PRESENTATION NATIVE registrations=" + string.Join("; ", registered.Select(item => $"page={item.Page},usage={item.Usage},flags=0x{item.Flags:X},target=0x{item.Target:X}")));
            check("mouse-only observer passive flags match the independently calibrated native registration", mouse.Flags == reportedPassiveFlags);
            check("mouse-only observer does not register a keyboard or suppress legacy input", registered.Length == 2 && registered.All(item => item.Page != 1 || item.Usage != 6) && (mouse.Flags & 0x30) == 0);
            check("mouse-only observer owns a private invisible message-only carrier", IsOwnMessageWindow(carrier) && !IsWindowVisible(carrier) && ClassName(carrier).StartsWith("FloatCanvas.PresentationInput.", StringComparison.Ordinal));
            check("native observer reports its requested epoch and live startup", observer.Epoch == 202 && observer.IsRunning && !observer.Completion.IsCompleted);
            Console.WriteLine($"PRESENTATION NATIVE own registration page={mouse.Page}; usage={mouse.Usage}; flags=0x{mouse.Flags:X}; target=0x{mouse.Target:X}");
            var time = Stopwatch.StartNew(); bool removed = observer.Stop(); time.Stop();
            check("native observer Stop immediately closes accepting state and unregisters its own class", removed && !observer.IsRunning && Registrations().All(item => item.Page != 1 || item.Usage != 2));
            check("native observer Stop preserves the unrelated foreign registration", Same(unrelated, Registrations()));
            check("stopped native observer reaches Completion and destroys its own carrier", observer.Completion.Wait(TimeSpan.FromSeconds(5)) && !IsWindow(carrier));
            check("native observer repeated Stop preserves foreign registration", observer.Stop() && Same(unrelated, Registrations()));
            Console.WriteLine($"PRESENTATION NATIVE Stop milliseconds={time.Elapsed.TotalMilliseconds:F3}; completion={observer.Completion.IsCompletedSuccessfully}; error={observer.LastError}");
        }
        finally
        {
            observer.Stop(); observer.Completion.Wait(TimeSpan.FromSeconds(5)); fixture.RemoveOwnedRegistrations();
        }
    }

    private static void KeyboardOnly(MessageWindow fixture, uint reportedPassiveFlags, Action<string, bool> check)
    {
        using var observer = PresentationInputObserver.StartAsync(252, keyboard: true, pointer: false).GetAwaiter().GetResult();
        nint carrier = 0;
        try
        {
            var registrations = Registrations();
            var keyboard = registrations.Single(item => item.Page == 1 && item.Usage == 6);
            carrier = keyboard.Target;
            check("keyboard-only observer registers keyboard input without any mouse class",
                registrations.Length == 1 && registrations.All(item => item.Usage == 6) && keyboard.Flags == reportedPassiveFlags);
            check("keyboard-only observer owns a private message-only carrier", IsOwnMessageWindow(carrier) && !IsWindowVisible(carrier));
            check("keyboard-only Stop removes exactly its own registration", observer.Stop() && Registrations().Length == 0);
        }
        finally
        {
            observer.Stop(); observer.Completion.Wait(TimeSpan.FromSeconds(5)); fixture.RemoveOwnedRegistrations();
        }
    }

    private static void PreserveReplacement(MessageWindow fixture, Action<string, bool> check)
    {
        using var observer = PresentationInputObserver.StartAsync(303, keyboard: false, pointer: true).GetAwaiter().GetResult();
        nint carrier = Registrations().Single(item => item.Page == 1 && item.Usage == 2).Target;
        try
        {
            fixture.Register(2); var replacement = Registrations();
            check("foreign component can replace the process mouse registration with its own target", replacement is [{ Page: 1, Usage: 2, Flags: 0 }] && replacement[0].Target == fixture.Handle && replacement[0].Target != carrier);
            check("observer Stop does not unregister a replacement owned by another window", observer.Stop() && Same(replacement, Registrations()));
            check("replaced observer reaches Completion without destroying the foreign window or registration", observer.Completion.Wait(TimeSpan.FromSeconds(5)) && !IsWindow(carrier) && IsWindow(fixture.Handle) && Same(replacement, Registrations()));
            observer.Dispose();
            check("observer Dispose after replacement remains ownership-safe", Same(replacement, Registrations()));
        }
        finally
        {
            observer.Stop(); observer.Completion.Wait(TimeSpan.FromSeconds(5)); fixture.RemoveOwnedRegistrations();
        }
    }

    private static void InactiveDesktop(uint reportedPassiveFlags, Action<string, bool> check)
    {
        using var observer = PresentationInputObserver.StartAsync(404, keyboard: false, pointer: true).GetAwaiter().GetResult();
        try
        {
            var registrations = Registrations();
            check("inactive desktop can prove mouse registration metadata before its availability watchdog", registrations is [{ Page: 1, Usage: 2 }] && registrations[0].Flags == reportedPassiveFlags);
            check("inactive-desktop watchdog stops the observer and completes its worker", observer.Completion.Wait(TimeSpan.FromSeconds(5)) && !observer.IsRunning && observer.LastError == 5);
            check("inactive-desktop watchdog removes only the observer registration", Registrations().Length == 0 && WaitForNoObserverWindows());
        }
        finally { observer.Stop(); observer.Completion.Wait(TimeSpan.FromSeconds(5)); }
    }

    private sealed class MessageWindow : IDisposable
    {
        internal nint Handle { get; }
        internal MessageWindow()
        {
            Handle = CreateWindowExW(0, "STATIC", "FloatCanvas.Presentation.Native.Fixture." + Guid.NewGuid().ToString("N"), 0, 0, 0, 0, 0, MessageOnly, 0, 0, 0);
            if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        internal void Register(ushort usage, uint flags = 0)
        { if (!RegisterRawInputDevices([new(1, usage, flags, Handle)], 1, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        internal void RemoveOwnedRegistrations()
        {
            var own = Registrations().Where(item => item.Target == Handle).Select(item => new RawDevice(item.Page, item.Usage, Remove, 0)).ToArray();
            if (own.Length != 0 && !RegisterRawInputDevices(own, (uint)own.Length, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() { RemoveOwnedRegistrations(); if (IsWindow(Handle) && !DestroyWindow(Handle)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }

    private static bool Same(RawDevice[] left, RawDevice[] right) => left.OrderBy(item => item.Page).ThenBy(item => item.Usage).SequenceEqual(right.OrderBy(item => item.Page).ThenBy(item => item.Usage));
    private static RawDevice[] Registrations()
    {
        uint count = 0, size = (uint)Marshal.SizeOf<RawDevice>(); uint result = GetRegisteredRawInputDevices(null, ref count, size);
        if (result == uint.MaxValue && Marshal.GetLastWin32Error() != 122) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (count == 0) return [];
        if (count > 4096) throw new InvalidOperationException("Unexpected raw-input registration count.");
        var devices = new RawDevice[count]; result = GetRegisteredRawInputDevices(devices, ref count, size);
        if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        return devices.Take((int)result).ToArray();
    }
    private static bool IsOwnMessageWindow(nint window)
    {
        uint thread = GetWindowThreadProcessId(window, out uint process);
        if (thread == 0 || process != Environment.ProcessId ||
            DesktopName(GetThreadDesktop(thread)) != DesktopName(GetThreadDesktop(GetCurrentThreadId()))) return false;
        nint current = 0;
        while ((current = FindWindowExW(MessageOnly, current, null, null)) != 0) if (current == window) return true;
        return false;
    }
    private static string ClassName(nint window)
    { var text = new StringBuilder(256); return GetClassNameW(window, text, text.Capacity) == 0 ? "" : text.ToString(); }
    private static string DesktopName(nint desktop)
    {
        var text = new StringBuilder(256);
        if (!GetUserObjectInformationW(desktop, 2, text, (uint)text.Capacity * 2, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return text.ToString();
    }
    private static bool WaitForNoObserverWindows()
    {
        var time = Stopwatch.StartNew();
        do
        {
            bool found = false; nint current = 0;
            while ((current = FindWindowExW(MessageOnly, current, null, null)) != 0)
            {
                GetWindowThreadProcessId(current, out uint process);
                if (process == Environment.ProcessId && ClassName(current).StartsWith("FloatCanvas.PresentationInput.", StringComparison.Ordinal)) { found = true; break; }
            }
            if (!found) return true;
            Thread.Sleep(5);
        } while (time.Elapsed < TimeSpan.FromSeconds(5));
        return false;
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct RawDevice(ushort Page, ushort Usage, uint Flags, nint Target);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterRawInputDevices([In] RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRegisteredRawInputDevices([Out] RawDevice[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string? className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder name, uint bytes, out uint needed);
}
