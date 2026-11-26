using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace FloatCanvas.App;

internal static class AppInputPolicy
{
    private static readonly object nativeMessageTag = new();
    [ThreadStatic] private static bool threadInitialized;
    [ThreadStatic] private static ConditionalWeakTable<InputEventArgs, NativeMessage>? inputMessages;
    private sealed record NativeMessage(MSG Value, int ProviderTime, HwndSource? Source);
    private static readonly NativeMessage absentMessage = new(default, 0, null);
    private static readonly ConditionalWeakTable<Window, FormSubmit> formSubmits = new();
    private sealed record FormSubmit(Button Button, Func<bool>? CanFinishField);
    private static readonly DependencyProperty SubmitFromTextProperty = DependencyProperty.RegisterAttached(
        "SubmitFromText", typeof(bool), typeof(AppInputPolicy), new PropertyMetadata(false));

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
        if (source.RootVisual is UIElement root)
        {
            root.KeyDown -= HandlePopupEnter;
            root.KeyDown += HandlePopupEnter;
        }
    }
    private static void HandlePopupEnter(object sender, KeyEventArgs args) => HandleFieldEnter(args);
    [DllImport("user32.dll")] private static extern int GetMessageTime();

    internal static void AttachToolbarToggle(Window window, Action toggle, Func<bool>? canRoute = null)
    {
        AttachKeyboardSource(window);
        window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Handled || e.Key != Key.Tab || canRoute?.Invoke() == false || SessionKeyboardRouter.IsModal) return;
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
        WindowFocusLifecycle.Initialize();
        TooltipLifecycle.Initialize();
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.KeyDownEvent,
            new KeyEventHandler((sender, args) => HandleWindowEnter((Window)sender, args)));
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Handled || e.Key != Key.Tab || SessionKeyboardRouter.IsModal) return;
            var source = e.OriginalSource as DependencyObject ?? Keyboard.FocusedElement as DependencyObject;
            if (EditorShortcutPolicy.OwnsLocalEditingKeys(source) || EditorShortcutPolicy.OwnsMenu(source)) return;
            if (source is not null && Window.GetWindow(source) is {} window && EditorShortcutPolicy.RecordingWindows.Contains(window)) return;
            e.Handled = true;
        }));
        InputManager.Current.PreProcessInput += CaptureNativeMessage;
    }

    internal static void AttachFormSubmit(Window window, Button submit, Func<bool>? canFinishField = null)
    {
        AttachKeyboardSource(window);
        submit.IsDefault = false;
        KeyboardNavigation.SetAcceptsReturn(submit, true);
        AutomationProperties.SetAcceleratorKey(submit, "Ctrl+Enter");
        formSubmits.Add(window, new(submit, canFinishField));
    }

    internal static void AllowPromptSubmit(TextBox input) => input.SetValue(SubmitFromTextProperty, true);

    internal static void AttachSearchEnter(TextBox input, Action search)
    {
        input.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            args.Handled = true;
            if (!IsRepeat(args)) search();
        };
    }

    private static void HandleWindowEnter(Window window, KeyEventArgs args)
    {
        if (args.Handled || args.Key != Key.Enter || EditorShortcutPolicy.RecordingWindows.Contains(window)) return;
        if (IsRepeat(args)) { args.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && formSubmits.TryGetValue(window, out var form))
        {
            args.Handled = true;
            if (form.Button is { IsEnabled: true, IsVisible: true } button)
                ((IInvokeProvider)new ButtonAutomationPeer(button)).Invoke();
            return;
        }
        HandleFieldEnter(args);
    }

    internal static void HandleFieldEnter(KeyEventArgs args, Func<bool>? confirm = null)
    {
        if (args.Handled || args.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None ||
            args.OriginalSource is not TextBox { AcceptsReturn: false, IsReadOnly: false } input ||
            (bool)input.GetValue(SubmitFromTextProperty)) return;
        if (EditorShortcutPolicy.Ancestors(input).Any(ancestor => ancestor is ComboBox)) return;
        var window = Window.GetWindow(input);
        if (window is not null && EditorShortcutPolicy.RecordingWindows.Contains(window)) return;
        args.Handled = true;
        if (IsRepeat(args) || confirm?.Invoke() == false ||
            window is not null && formSubmits.TryGetValue(window, out var form) && form.CanFinishField?.Invoke() == false)
            return;
        if (!input.IsKeyboardFocused) return;
        var scope = FocusManager.GetFocusScope(input);
        if (ReferenceEquals(FocusManager.GetFocusedElement(scope), input)) FocusManager.SetFocusedElement(scope, null);
        var source = PresentationSource.FromVisual(input);
        if (window is not null && ReferenceEquals(PresentationSource.FromVisual(window), source))
        {
            FocusManager.SetFocusedElement(window, window);
            Keyboard.Focus(window);
        }
        else if (source?.RootVisual is UIElement { Focusable: true } root && root != input)
            Keyboard.Focus(root);
        if (input.IsKeyboardFocused) Keyboard.ClearFocus();
    }
    internal static void Initialize()
    {
        InitializeThread();
    }
}
