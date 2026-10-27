using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FloatCanvas.App;

/// <summary>Opt-in local Escape diagnostics; never records text or other key input.</summary>
internal sealed class EscapeInputTrace : IDisposable
{
    internal static bool Enabled { get; } = Environment.GetEnvironmentVariable("FLOATCANVAS_TRACE_ESCAPE") == "1";
    private static EscapeInputTrace? active;
    private readonly ApplicationDiagnostics diagnostics;
    private readonly Func<object> captureState;
    private readonly InputManager input;
    private bool disposed;

    private EscapeInputTrace(ApplicationDiagnostics diagnostics, Func<object> captureState)
    {
        this.diagnostics = diagnostics;
        this.captureState = captureState;
        input = InputManager.Current;
    }

    internal static IDisposable? Start(ApplicationDiagnostics diagnostics, Func<object> captureState)
    {
        if (!Enabled) return null;
        EscapeInputTrace? trace = null;
        try
        {
            active?.Dispose();
            trace = new(diagnostics, captureState);
            active = trace;
            trace.input.PreProcessInput += trace.BeforeInput;
            trace.input.PreNotifyInput += trace.BeforeRouting;
            trace.input.PostProcessInput += trace.AfterInput;
            trace.Write("Start", null, new { });
            return trace;
        }
        catch { trace?.Dispose(); return null; }
    }

    internal static void RecordPolicy(KeyEventArgs args, AppInputPolicy.RepeatInfo info, bool chosen)
    {
        if (!IsEscape(args)) return;
        active?.Write("RepeatPolicy", args, new { ChosenRepeat = chosen, Native = Describe(info) });
    }

    internal static void RecordNative(MSG message, int providerTime)
    {
        if (message.wParam != 0x1B || message.message is not (0x100 or 0x101 or 0x104 or 0x105)) return;
        active?.Write("NativeMessage", null, new
        {
            Message = message.message,
            message.time,
            ProviderTime = providerTime,
            Window = Handle(message.hwnd),
            Flags = Handle(message.lParam),
            NativeRepeat = message.message is 0x100 or 0x104 && (message.lParam.ToInt64() & (1L << 30)) != 0,
            KeyUp = message.message is 0x101 or 0x105
        });
    }

    private void BeforeInput(object sender, PreProcessInputEventArgs args) => TraceInput("PreProcess", args.StagingItem.Input);
    private void BeforeRouting(object sender, NotifyInputEventArgs args) => TraceInput("PreNotify", args.StagingItem.Input);
    private void AfterInput(object sender, ProcessInputEventArgs args) => TraceInput("PostProcess", args.StagingItem.Input);
    private void TraceInput(string phase, InputEventArgs inputArgs)
    {
        try
        {
            if (inputArgs is not KeyEventArgs key || !IsEscape(key)) return;
            if (key.RoutedEvent != Keyboard.PreviewKeyDownEvent && key.RoutedEvent != Keyboard.KeyDownEvent &&
                key.RoutedEvent != Keyboard.PreviewKeyUpEvent && key.RoutedEvent != Keyboard.KeyUpEvent) return;
            Write(phase, key, new { Native = Describe(AppInputPolicy.InspectRepeat(key)) });
        }
        catch { }
    }

    private static bool IsEscape(KeyEventArgs args) => (args.Key == Key.System ? args.SystemKey : args.Key) == Key.Escape;
    private static object Describe(AppInputPolicy.RepeatInfo info) => info.VirtualKey == 0x1B ? new
    {
        info.WpfRepeat, info.NativeMatches, info.NativeRepeat, info.Message, info.MessageTime, info.ProviderTime,
        Window = Handle(info.Window), Flags = Handle(info.Flags)
    } : new { info.WpfRepeat, info.NativeMatches, CurrentNativeMessageIsEscape = false };

    private void Write(string phase, KeyEventArgs? args, object detail)
    {
        if (disposed) return;
        try
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            var origin = args?.OriginalSource as DependencyObject ?? args?.KeyboardDevice.Target as DependencyObject;
            var foreground = GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(foreground, out uint foregroundProcess);
            diagnostics.Write("Input.EscapeTrace", new
            {
                Phase = phase,
                RoutedEvent = args?.RoutedEvent?.Name,
                Timestamp = args?.Timestamp,
                Handled = args?.Handled,
                WpfRepeat = args?.IsRepeat,
                Modifiers = Keyboard.Modifiers.ToString(),
                IsThreadModal = ComponentDispatcher.IsThreadModal,
                Focus = DescribeElement(focused),
                Origin = DescribeElement(origin),
                NativeFocus = Handle(GetFocus()),
                ForegroundWindow = Handle(foreground),
                ForegroundProcess = foregroundProcess,
                ForegroundThread = foregroundThread,
                State = captureState(),
                Detail = detail
            });
        }
        catch { } // Diagnostics must not change focus, input routing, or command execution.
    }

    private static object? DescribeElement(DependencyObject? element)
    {
        if (element is null) return null;
        var source = PresentationSource.FromDependencyObject(element) as HwndSource;
        return new
        {
            Type = element.GetType().FullName,
            Visible = element is UIElement visible ? (bool?)visible.IsVisible : null,
            Enabled = element is UIElement enabled ? (bool?)enabled.IsEnabled : null,
            SourceWindow = source is null || source.IsDisposed ? null : Handle(source.Handle),
            SourceDisposed = source?.IsDisposed
        };
    }

    private static string Handle(nint value) => value.ToInt64().ToString("X");
    public void Dispose()
    {
        if (disposed) return;
        Write("Stop", null, new { });
        disposed = true;
        input.PreProcessInput -= BeforeInput;
        input.PreNotifyInput -= BeforeRouting;
        input.PostProcessInput -= AfterInput;
        if (ReferenceEquals(active, this)) active = null;
    }

    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
