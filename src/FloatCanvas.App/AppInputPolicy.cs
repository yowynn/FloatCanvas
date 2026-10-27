using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FloatCanvas.App;

internal static class AppInputPolicy
{
    private static bool initialized;
    private static readonly object nativeMessageTag = new();
    [ThreadStatic] private static bool threadInitialized;
    [ThreadStatic] private static ConditionalWeakTable<InputEventArgs, NativeMessage>? inputMessages;
    private sealed record NativeMessage(MSG Value, int ProviderTime, HwndSource? Source);
    private static readonly NativeMessage absentMessage = new(default, 0, null);

    internal static bool IsRepeat(KeyEventArgs args)
    {
        // Hiding an input window on KeyDown can strand WPF's previous-key cache
        // when its KeyUp is delivered elsewhere. Use this event's native transition.
        var info = InspectRepeat(args);
        bool result = info.NativeMatches ? info.NativeRepeat : args.IsRepeat;
        if (EscapeInputTrace.Enabled && (args.Key == Key.Escape || args.SystemKey == Key.Escape))
            EscapeInputTrace.RecordPolicy(args, info, result);
        return result;
    }

    internal readonly record struct RepeatInfo(bool WpfRepeat, bool NativeMatches, bool NativeRepeat,
        int Message, int MessageTime, int ProviderTime, nint Window, nint VirtualKey, nint Flags);

    internal static RepeatInfo InspectRepeat(KeyEventArgs args)
    {
        NativeMessage? native = null;
        inputMessages?.TryGetValue(args, out native);
        var message = native?.Value ?? default;
        bool matches = native is not null && Matches(native, args);
        return new(args.IsRepeat, matches, IsDown(message.message) && (message.lParam.ToInt64() & (1L << 30)) != 0,
            message.message, message.time, native?.ProviderTime ?? 0, message.hwnd, message.wParam, message.lParam);
    }

    private static void CaptureNativeMessage(object sender, PreProcessInputEventArgs args)
    {
        var input = args.StagingItem.Input;
        var events = inputMessages ??= new();
        if (!events.TryGetValue(input, out var native))
        {
            // Promotion clones staging data. Preserve it on every input object so
            // cancelling and replaying any stage retains its original provenance.
            native = args.StagingItem.GetData(nativeMessageTag) as NativeMessage;
            if (native is null)
            {
                if (input.RoutedEvent?.OwnerType != typeof(InputManager) || input.RoutedEvent.Name != "PreviewInputReport") return;
                // WPF scopes CurrentKeyboardMessage around its provider's ReportInput.
                // Capture only at the first raw report, never at a promoted KeyEvent.
                // KeyEventArgs.Timestamp may be the provider's older cached time.
                var message = ComponentDispatcher.CurrentKeyboardMessage;
                var source = message.hwnd == 0 ? null : HwndSource.FromHwnd(message.hwnd);
                bool keyboard = source is { IsDisposed: false } && (IsDown(message.message) || IsUp(message.message));
                native = keyboard ? new(message, GetMessageTime(), source) : absentMessage;
                // Preserve absence too: replay must not borrow another key's message.
                if (keyboard && EscapeInputTrace.Enabled) EscapeInputTrace.RecordNative(message, native.ProviderTime);
            }
            events.Add(input, native);
        }
        if (native.Source is not null) args.StagingItem.SetData(nativeMessageTag, native);
        else if (args.StagingItem.GetData(nativeMessageTag) is not null)
            args.StagingItem.SetData(nativeMessageTag, null);
        // Keep absence only on the original report. Mouse promotions need neither
        // staging dictionaries nor weak-table entries for keyboard provenance.
    }

    private static bool Matches(NativeMessage native, KeyEventArgs key)
    {
        var actual = key.Key == Key.System ? key.SystemKey : key.Key;
        bool down = key.RoutedEvent == Keyboard.PreviewKeyDownEvent || key.RoutedEvent == Keyboard.KeyDownEvent;
        bool up = key.RoutedEvent == Keyboard.PreviewKeyUpEvent || key.RoutedEvent == Keyboard.KeyUpEvent;
        // Native provenance already identifies the source. Current focus can legally
        // differ for a nonactivating Popup or after deferred input changes focus.
        return native.Source is { IsDisposed: false } source && source.Handle == native.Value.hwnd &&
            ReferenceEquals(HwndSource.FromHwnd(native.Value.hwnd), source) && native.Value.wParam == KeyInterop.VirtualKeyFromKey(actual) &&
            (down && IsDown(native.Value.message) || up && IsUp(native.Value.message));
    }
    private static bool IsDown(int message) => message is 0x100 or 0x104;
    private static bool IsUp(int message) => message is 0x101 or 0x105;

    internal static void AttachKeyboardSource(Window window)
    {
        window.Dispatcher.VerifyAccess();
        InitializeThread();
    }
    internal static void AttachKeyboardSource(HwndSource source)
    {
        source.Dispatcher.VerifyAccess();
        InitializeThread();
    }
    [DllImport("user32.dll")] private static extern int GetMessageTime();

    internal static void AttachToolbarToggle(Window window, Action toggle, Func<bool>? canRoute = null)
    {
        AttachKeyboardSource(window);
        window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key != Key.Tab || canRoute?.Invoke() == false) return;
            var source = e.OriginalSource as DependencyObject ?? Keyboard.FocusedElement as DependencyObject;
            if (!EditorShortcutPolicy.ShouldRoute(0x09, Keyboard.Modifiers, source)) return;
            e.Handled = true;
            if (!IsRepeat(e)) toggle();
        }), handledEventsToo: true);
    }
    private static void InitializeThread()
    {
        if (threadInitialized) return;
        threadInitialized = true;
        InputManager.Current.PreProcessInput += CaptureNativeMessage;
    }
    internal static void Initialize()
    {
        InitializeThread();
        if (initialized) return;
        initialized = true;
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler((_, e) => { if (e.Key == Key.Tab) e.Handled = true; }), true);
    }
}
