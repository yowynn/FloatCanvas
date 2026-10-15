using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

/// <summary>
/// An independent WPF input target. It records real keyboard/character/text events;
/// it never assigns the expected text or synthesizes a TextComposition result.
/// </summary>
internal sealed class KeyboardBridgeFixture : IDisposable
{
    private readonly HwndSource source;
    private readonly HwndSourceHook hook;
    private readonly uint notifyMessage;
    public Wpf.Window Window { get; }
    public TextBox Input { get; }
    public nint Handle { get; }
    public List<object> Events { get; } = [];
    public Action? Notification { get; set; }
    public bool Disposed { get; private set; }

    public KeyboardBridgeFixture(uint notifyMessage)
    {
        this.notifyMessage = notifyMessage;
        Input = new TextBox { AcceptsReturn = true, MinHeight = 120, Margin = new Wpf.Thickness(16), FontSize = 18 };
        // This probe tests English character messages and editing commands. Chinese IME is a
        // separate acceptance task; disabling it affects only this self-created TextBox.
        InputMethod.SetIsInputMethodEnabled(Input, false);
        Window = new Wpf.Window { Title = "浮绘 · 自建 WPF 键盘桥验证", Content = Input, Width = 460, Height = 240,
            Left = 70, Top = 90, Topmost = true, ShowActivated = false };
        Handle = new WindowInteropHelper(Window).EnsureHandle();
        source = HwndSource.FromHwnd(Handle) ?? throw new InvalidOperationException("The own WPF fixture has no HwndSource.");
        hook = OnMessage; source.AddHook(hook);
        Input.PreviewKeyDown += (_, e) => RecordKey("PreviewKeyDown", e);
        Input.PreviewKeyUp += (_, e) => RecordKey("PreviewKeyUp", e);
        Input.PreviewTextInput += (_, e) => Events.Add(new { Kind = "PreviewTextInput", At = Stopwatch.GetTimestamp(), Text = ReportText(e.Text) });
        Input.TextChanged += (_, _) => Events.Add(new { Kind = "TextChanged", At = Stopwatch.GetTimestamp(), Text = ReportText(Input.Text),
            Input.SelectionStart, Input.SelectionLength });
    }
    public void ActivateText()
    {
        Window.Show(); Window.Activate(); Input.Focus();
    }
    public bool HasTextFocus => ReferenceEquals(Keyboard.FocusedElement, Input) && Input.IsKeyboardFocused;
    public object Snapshot() => new { Text = ReportText(Input.Text), Input.SelectionStart, Input.SelectionLength,
        HasTextFocus, WindowVisible = Window.IsVisible, WindowHandle = Handle.ToInt64(),
        Ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl),
        Shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift),
        X = Keyboard.IsKeyDown(Key.X), ImeEnabled = InputMethod.GetIsInputMethodEnabled(Input) };
    public static string ReportText(string text) => text.All(c => c is 'x' or 'X' or 'a' or 'A' or 'b' or 'B' or '\n' or '\r')
        ? text : $"<unexpected text, length={text.Length}>";
    private void RecordKey(string kind, KeyEventArgs e) => Events.Add(new { Kind = kind, At = Stopwatch.GetTimestamp(),
        Key = e.Key.ToString(), SystemKey = e.SystemKey.ToString(), e.IsRepeat, e.Handled, Modifiers = Keyboard.Modifiers.ToString() });
    private nint OnMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == notifyMessage)
        {
            Notification?.Invoke(); handled = true; return 0;
        }
        if (message is 0x100 or 0x101 or 0x104 or 0x105 or 0x102 or 0x106)
            Events.Add(new { Kind = "WindowMessage", At = Stopwatch.GetTimestamp(), Message = message,
                KeyOrCharacter = wParam.ToInt64(), Flags = lParam.ToInt64() });
        return 0;
    }
    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true; Notification = null; source.RemoveHook(hook); Window.Close();
    }
}
