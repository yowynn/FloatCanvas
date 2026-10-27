using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

/// <summary>Real inspector font edits and document history, without desktop mouse input.</summary>
internal static class FontInspectorCommitTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_FONT_INSPECTOR_OUTPUT") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "artifacts",
            "FloatCanvas-global-hidden-refresh", "verification", "font-inspector");
        Directory.CreateDirectory(output);
        var findings = new List<(string Name, bool Passed)>();
        var observations = new List<object>();
        var oldCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        AppTheme.Apply("light");

        var first = new CanvasElement { Kind = ElementKind.Text, Text = "First font target", Width = 180, Height = 60,
            Style = new() { FontFamily = "Arial" } };
        var second = new CanvasElement { Kind = ElementKind.Text, Text = "Second font target", Width = 180, Height = 60,
            Style = new() { FontFamily = "Arial" } };
        var session = new EditorSession(new SceneDocument { Elements = [first, second] });
        session.SetSelection([first.Id]);
        var inspector = new InspectorWindow(() => session, colorLibrary: new ColorLibrary(Path.Combine(output, "colors.json")))
        {
            Left = -20000, Top = -20000, Width = 460, Height = 720, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false, Topmost = false
        };
        var picker = Field<FontPickerControl>(inspector, "fontPicker");
        var fields = Field<Dictionary<string, TextBox>>(inspector, "fields");
        TextBox? input = null;
        nint handle = 0;
        int commits = 0;
        picker.FontCommitted += _ => commits++;
        try
        {
            // Veto foreground activation while creating/showing the surface only.
            // Keeping this hook installed during SetFocus would also reject the local
            // native activation WPF needs to establish its real TextBox keyboard focus.
            using (new ActivationGuard())
            {
                inspector.PrepareForShow(); inspector.Show(); Pump();
            }
            handle = new WindowInteropHelper(inspector).Handle;
            picker.ApplyTemplate(); inspector.UpdateLayout(); Pump();
            input = (TextBox)picker.Template.FindName("PART_EditableTextBox", picker);
            switch (picker.Template.FindName("PART_Popup", picker))
            {
                case Popup { Child: { } child }: child.Opacity = 0; break;
                case OverlayPopup { Child: { } child }: child.Opacity = 0; break;
                default: throw new InvalidOperationException("The actual inspector font popup is missing.");
            }
            // Keep the actual inspector visible while testing local focus transitions.
            // Its production pin action suppresses dismissal of a nonactive fixture window.
            Field<Button>(inspector, "pin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Observe("inspector font regression uses a visible nonactivating native window", inspector.IsVisible && IsWindow(handle) && GetForegroundWindow() != handle);

            string before = JsonSerializer.Serialize(session.Document);
            FocusEditor();
            string draft = "";
            foreach (char letter in "Segoe")
            {
                TypeLetter(letter); draft += letter; Wait(350);
                Observe("inspector typing " + draft + " preserves its text and caret", input.Text == draft && input.SelectionStart == draft.Length && input.SelectionLength == 0);
                Observe("inspector typing " + draft + " changes no font, history or transaction",
                    JsonSerializer.Serialize(session.Document) == before && session.UndoCount == 0 && !session.IsTransactionActive && commits == 0);
            }
            var candidates = picker.Items.OfType<FontPickerControl.FontChoice>().ToArray();
            Observe("inspector search exposes installed font candidates", candidates.Length > 0);
            if (candidates.Length == 0) throw new InvalidOperationException("The installed Segoe font candidate is unavailable.");
            var expected = candidates[0];
            PressKey(Key.Down); PressKey(Key.Up);
            Observe("inspector candidate navigation changes no document or undo entry",
                JsonSerializer.Serialize(session.Document) == before && session.UndoCount == 0 && !session.IsTransactionActive && commits == 0 && input.Text == "Segoe");
            Observe("inspector navigation returns to the first candidate before blur", ReferenceEquals(picker.SelectedItem, expected));
            Snapshot("typed-and-navigated");

            Keyboard.Focus(fields["Text"]); Pump();
            Observe("inspector editor blur publishes the first canonical font once", Font(first.Id) == expected.Name && Font(second.Id) == "Arial" && commits == 1);
            Observe("inspector commit shows the friendly font name", picker.Text == expected.DisplayName && input.Text == expected.DisplayName && fields["FontFamily"].Text == expected.Name);
            Snapshot("blur-applied");
            Wait(750);
            Observe("one inspector font commit produces one completed undo entry", session.UndoCount == 1 && !session.IsTransactionActive && commits == 1);
            Snapshot("blur-history-complete");
            bool undone = session.Undo(); Pump();
            Observe("one undo restores Arial without changing the other text object", undone && Font(first.Id) == "Arial" && Font(second.Id) == "Arial" && session.UndoCount == 0 && session.RedoCount == 1);
            Observe("undo refreshes the inspector font display without republishing a commit", picker.Text == "Arial" && fields["FontFamily"].Text == "Arial" && commits == 1);
            Snapshot("undone");

            // Both objects deliberately use the same canonical family. A font-name-only
            // synchronization shortcut must not retain the old selection's search session.
            session.SetSelection([first.Id]); Pump(); commits = 0;
            FocusEditor(); TypeText("Segoe"); Wait(350);
            Observe("selection-context regression starts with an uncommitted first-object search",
                input.Text == "Segoe" && picker.IsDropDownOpen && !picker.IsReadOnly && Font(first.Id) == "Arial" && Font(second.Id) == "Arial" && commits == 0);
            int undoBeforeSwitch = session.UndoCount, redoBeforeSwitch = session.RedoCount;
            session.SetSelection([second.Id]); Pump();
            Observe("same-font selection changes replace the old search with the new object's display",
                session.Selection.SetEquals([second.Id]) && fields["Text"].Text == "Second font target" && input.Text == "Arial" && picker.IsReadOnly && !picker.IsDropDownOpen);
            Keyboard.Focus(fields["Text"]); Pump(); Wait(750);
            Observe("blur after a same-font selection switch cannot commit the old candidate",
                Font(first.Id) == "Arial" && Font(second.Id) == "Arial" && commits == 0 && session.UndoCount == undoBeforeSwitch && session.RedoCount == redoBeforeSwitch && !session.IsTransactionActive);
            Snapshot("same-font-selection-blurred");
        }
        finally
        {
            picker.IsDropDownOpen = false; Keyboard.ClearFocus(); inspector.Hide();
            if (handle != 0) HwndSource.FromHwnd(handle)?.Dispose();
            Pump(); CultureInfo.CurrentUICulture = oldCulture;
            File.WriteAllText(Path.Combine(output, "font-inspector-state.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
        }
        Observe("inspector font fixture releases its own window without foreground activation", handle != 0 && !IsWindow(handle) && GetForegroundWindow() != handle);
        foreach (var result in findings) check(result.Name, result.Passed);

        string Font(Guid id) => session.Document.Elements.Single(element => element.Id == id).Style.FontFamily;
        void Observe(string name, bool passed)
        {
            findings.Add((name, passed)); Console.WriteLine($"FONT_INSPECTOR_FINDING {passed}: {name}");
        }
        void Snapshot(string phase)
        {
            var observed = new { Phase = phase, FirstFont = Font(first.Id), SecondFont = Font(second.Id), session.UndoCount, session.RedoCount,
                session.IsTransactionActive, Commits = commits, Text = input?.Text, SelectionStart = input?.SelectionStart, SelectionLength = input?.SelectionLength,
                Selected = (picker.SelectedItem as FontPickerControl.FontChoice)?.Name, picker.IsReadOnly, picker.IsDropDownOpen,
                Selection = session.Selection.ToArray(), FontField = fields["FontFamily"].Text, Status = Field<TextBlock>(inspector, "status").Text };
            observations.Add(observed); Console.WriteLine("FONT_INSPECTOR " + JsonSerializer.Serialize(observed));
        }
        void FocusEditor()
        {
            SetFocus(handle); Keyboard.Focus(fields["Text"]); Pump(); Keyboard.Focus(input); Pump(); input!.SelectAll();
            Observe("inspector search starts in its loaded visible real TextBox", picker.IsLoaded && picker.IsVisible && input.IsLoaded && input.IsKeyboardFocusWithin && !picker.IsReadOnly && picker.IsDropDownOpen);
        }
        void TypeText(string text) { foreach (char letter in text) TypeLetter(letter); }
        void TypeLetter(char letter)
        {
            nint focus = OwnFocus(); SendMessage(focus, 0x102, letter, 1); Pump();
        }
        void PressKey(Key key)
        {
            using var modifiers = new ThreadModifierScope(ModifierKeys.None);
            uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key), scan = MapVirtualKey(virtualKey, 0);
            var original = new byte[256]; GetKeyboardState(original);
            try
            {
                foreach (bool up in new[] { false, true })
                {
                    var state = (byte[])original.Clone(); state[virtualKey] = up ? (byte)0 : (byte)128; SetKeyboardState(state);
                    uint packet = 1 | ((scan & 255) << 16) | (up ? 0xC0000000u : 0);
                    if (Keyboard.Modifiers != ModifierKeys.None) throw new InvalidOperationException("Font inspector key fixture requires a plain key with no modifiers.");
                    SendMessage(OwnFocus(), up ? 0x101u : 0x100u, (nint)virtualKey, unchecked((nint)(int)packet)); Pump();
                }
            }
            finally { SetKeyboardState(original); }
        }
        nint OwnFocus()
        {
            nint focus = GetFocus(); GetWindowThreadProcessId(focus, out uint process);
            if (focus == 0 || process != Environment.ProcessId) throw new InvalidOperationException("Font inspector input must target this fixture's own HWND.");
            return focus;
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private sealed class ActivationGuard : IDisposable
    {
        private readonly HookProcedure procedure;
        private nint hook;
        internal ActivationGuard()
        {
            procedure = (code, wparam, lparam) => code == 5 ? 1 : CallNextHookEx(hook, code, wparam, lparam);
            hook = SetWindowsHookExW(5, procedure, 0, GetCurrentThreadId());
            if (hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() { if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; } }
    }
    private delegate nint HookProcedure(int code, nint wparam, nint lparam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int kind, HookProcedure procedure, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint SendMessage(nint window, uint message, nint key, nint flags);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint key, uint type);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
}
