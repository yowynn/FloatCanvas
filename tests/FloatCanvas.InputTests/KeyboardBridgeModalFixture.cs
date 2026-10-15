using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

/// <summary>A real, owned ShowDialog target. Only its actual Escape key handler counts as a successful close.</summary>
internal sealed class KeyboardBridgeModalFixture : IDisposable
{
    public Wpf.Window Window { get; }
    public TextBox Input { get; } = new() { Margin = new Wpf.Thickness(16), FontSize = 18, MinHeight = 80 };
    public nint Handle { get; private set; }
    public bool Returned { get; private set; }
    public bool ClosedByEscape { get; private set; }
    public List<object> Events { get; } = [];
    public bool HasTextFocus => ReferenceEquals(Keyboard.FocusedElement, Input) && Input.IsKeyboardFocused;

    public KeyboardBridgeModalFixture(Wpf.Window owner)
    {
        InputMethod.SetIsInputMethodEnabled(Input, false);
        Window = new Wpf.Window { Owner = owner, Title = "浮绘 · 自建键盘桥模态验证", Content = Input,
            Width = 380, Height = 190, ShowActivated = true, Topmost = true,
            WindowStartupLocation = Wpf.WindowStartupLocation.CenterOwner };
        Window.SourceInitialized += (_, _) => Handle = new WindowInteropHelper(Window).Handle;
        Window.ContentRendered += (_, _) => { Input.Focus(); Events.Add(new { Kind = "ContentRendered", At = Stopwatch.GetTimestamp(), HasTextFocus }); };
        Input.PreviewKeyDown += (_, e) =>
        {
            Events.Add(new { Kind = "PreviewKeyDown", At = Stopwatch.GetTimestamp(), Key = e.Key.ToString() });
            if (e.Key != Key.Escape) return;
            ClosedByEscape = true; e.Handled = true; Window.DialogResult = false;
        };
        Input.PreviewTextInput += (_, e) => Events.Add(new { Kind = "PreviewTextInput", At = Stopwatch.GetTimestamp(), Text = KeyboardBridgeFixture.ReportText(e.Text) });
        Input.TextChanged += (_, _) => Events.Add(new { Kind = "TextChanged", At = Stopwatch.GetTimestamp(), Text = KeyboardBridgeFixture.ReportText(Input.Text) });
    }

    public void ShowFromActualKey()
    {
        Events.Add(new { Kind = "ShowDialogEnteredFromNativeKey", At = Stopwatch.GetTimestamp() });
        Window.ShowDialog(); Returned = true;
        Events.Add(new { Kind = "ShowDialogReturned", At = Stopwatch.GetTimestamp(), ClosedByEscape });
    }

    public void Dispose() { if (Window.IsVisible) Window.Close(); }
}
