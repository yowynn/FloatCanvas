using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class FontInteractionAcceptanceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var picker = (FontPickerControl)ToolOptionPreviews.FontPicker(); picker.SelectFont("Arial");
        var window = new Window { Content = picker, Width = 420, Height = 100, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show(); Pump(); picker.ApplyTemplate(); HidePopup(picker);
            var text = (TextBox)picker.Template.FindName("PART_EditableTextBox", picker);
            Focus(window, text); text.Text = "uncommitted quick draft";
            var escape = Escape(window, text);
            check("font Escape before the search delay consumes and cancels the draft", escape.Handled && picker.Text == "Arial");

            var session = new EditorSession(new SceneDocument { Elements = [new CanvasElement { Kind = ElementKind.Text, Text = "Font", Width = 100, Height = 50, Style = new() { FontFamily = "Arial" } }] }); session.SelectAll();
            var inspector = new InspectorWindow(() => session) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            int dismissals = 0; inspector.Dismissed += () => dismissals++;
            try
            {
                inspector.PrepareForShow(); inspector.Show(); Pump();
                var font = (FontPickerControl)typeof(InspectorWindow).GetField("fontPicker", Private)!.GetValue(inspector)!;
                font.ApplyTemplate(); HidePopup(font);
                var input = (TextBox)font.Template.FindName("PART_EditableTextBox", font);
                Focus(inspector, input); input.Text = "quick inspector draft";
                Escape(inspector, input);
                check("inspector cancels its font draft before its window Escape action", dismissals == 0 && font.Text == "Arial" && session.UndoCount == 0);
                Focus(inspector, input); font.ApplySearch("Segoe");
                Escape(inspector, input);
                check("inspector font filtering Escape keeps the panel and model intact", dismissals == 0 && font.Text == "Arial" && session.UndoCount == 0);
            }
            finally { inspector.Close(); Pump(); }

            Focus(window, text); picker.ShowAllFonts(); picker.IsDropDownOpen = true; Pump(); int all = picker.Items.Count;
            picker.ApplySearch("Arial"); int filtered = picker.Items.Count;
            var arrow = (ToggleButton)picker.Template.FindName("DropDownToggle", picker);
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = arrow };
            arrow.RaiseEvent(press); Pump();
            check("one arrow press replaces an open search with the full read-only font list", filtered < all && picker.IsDropDownOpen && picker.Items.Count == all && picker.IsReadOnly && press.Handled);
        }
        finally { picker.IsDropDownOpen = false; window.Close(); Pump(); }
    }
    private static void HidePopup(FontPickerControl picker)
    {
        if (picker.Template.FindName("PART_Popup", picker) is Popup { Child: UIElement child }) child.Opacity = 0;
        else if (picker.Template.FindName("PART_Popup", picker) is OverlayPopup { Child: { } overlayChild }) overlayChild.Opacity = 0;
    }
    private static void Focus(Window window, TextBox input)
    {
        SetFocus(new WindowInteropHelper(window).Handle); Keyboard.Focus(input); Pump();
        if (!input.IsKeyboardFocusWithin) throw new InvalidOperationException("Font Escape fixture must focus the real editor.");
    }
    private static KeyEventArgs Escape(Window window, TextBox input)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, HwndSource.FromHwnd(new WindowInteropHelper(window).Handle), Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        input.RaiseEvent(args); Pump(); return args;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
}
