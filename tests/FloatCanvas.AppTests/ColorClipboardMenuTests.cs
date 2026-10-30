using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class ColorClipboardMenuTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        Commands(check);
        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-ColorClipboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FieldCommands(root, check);
            PickerCommands(root, check);
            CanvasHistory(root, check);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Commands(Action<string, bool> check)
    {
        uint current = 0x80112233;
        string? text = "not a color", written = null;
        int applied = 0, reads = 0, writes = 0, copied = 0;
        bool failRead = false, failWrite = false;
        var errors = new List<string>();
        var target = new Border();
        var commands = new ColorClipboardMenu(target, () => current, value => { current = value; applied++; }, errors.Add,
            readText: () => { reads++; if (failRead) throw new ExternalException("busy"); return text; },
            writeText: value => { writes++; if (failWrite) throw new ExternalException("busy"); written = value; });
        commands.Copied += () => copied++;
        var paste = (MenuItem)commands.Menu.Items[0];
        var copy = (MenuItem)commands.Menu.Items[1];
        check("color clipboard menus attach to their color target and offer six copy choices",
            ReferenceEquals(target.ContextMenu, commands.Menu) && commands.Menu.Items.Count == 2 && copy.Items.Count == 6);
        commands.Refresh();
        check("color clipboard paste is disabled for invalid clipboard text", !paste.IsEnabled && applied == 0);
        text = "#445566"; commands.Refresh();
        check("color clipboard paste becomes enabled when clipboard text becomes valid", paste.IsEnabled && applied == 0);
        text = null; commands.Refresh();
        check("color clipboard paste disables again when clipboard text is removed", !paste.IsEnabled);
        for (int index = 0; index < ColorClipboardText.Formats.Count; index++)
        {
            var item = (MenuItem)copy.Items[index];
            var format = ColorClipboardText.Formats[index];
            Click(item);
            check("color clipboard " + format + " menu action copies the chosen syntax without changing the color",
                written == ColorClipboardText.Format(current, format) && applied == 0);
        }
        check("each copy format reports one successful copy without a color edit", copied == 6 && applied == 0);
        current = 0x24556677; commands.Refresh();
        check("color clipboard copy tooltips refresh to the current color", copy.Items.Cast<MenuItem>().Select(item => (string)item.ToolTip)
            .SequenceEqual(ColorClipboardText.Formats.Select(format => ColorClipboardText.Format(current, format))));
        current = 0x3C778899; Click((MenuItem)copy.Items[1]);
        check("color clipboard copy reads the current color when invoked rather than its old tooltip", written == "#3C778899");
        text = "#001122"; commands.Refresh();
        text = "rgb(64, 128, 192)"; current = 0x5A112233;
        Click(paste);
        check("color clipboard paste rereads text and preserves the invocation-time alpha", current == 0x5A4080C0 && applied == 1);
        text = "invalid after opening"; Click(paste);
        check("color clipboard invalid text after opening cannot mutate color and disables paste", applied == 1 && !paste.IsEnabled);
        int errorCount = errors.Count; failRead = true; commands.Refresh();
        check("a busy clipboard safely disables paste while opening without reporting a failed user command", !paste.IsEnabled && errors.Count == errorCount);
        failRead = false; text = "#ABCDEF"; commands.Refresh(); failRead = true; Click(paste);
        check("a clipboard read failure during paste reports an error without changing the color", errors.Count == ++errorCount && applied == 1);
        failWrite = true; commands.Copy(ColorClipboardFormat.HexArgb);
        check("a clipboard write failure reports an error without changing the color", errors.Count == ++errorCount && applied == 1);
        check("a clipboard write failure cannot report a successful copy", copied == 7);
        check("color clipboard tests use injected clipboard delegates", reads > 0 && writes == 8);

        var first = new ColorClipboardMenu(new Border(), () => current, value => { current = value; applied++; }, errors.Add,
            allowTransparent: true, readText: () => throw new InvalidOperationException("transparent must not read clipboard"),
            writeText: _ => throw new InvalidOperationException("transparent must not write clipboard"));
        check("only the first color control adds a separate transparency command",
            first.Menu.Items.Count == 4 && first.Menu.Items[2] is Separator && first.Menu.Items[3] is MenuItem && commands.Menu.Items.Count == 2);
        Click((MenuItem)first.Menu.Items[3]);
        check("setting transparent clears every ARGB channel without accessing the clipboard", current == 0 && applied == 2 && errors.Count == errorCount);
    }

    private static void FieldCommands(string root, Action<string, bool> check)
    {
        var field = new ColorField(ColorRole.Foreground, new ColorLibrary(Path.Combine(root, "field.json"))) { Value = 0x80112233 };
        var commands = Field<ColorClipboardMenu>(field, "clipboard");
        int changes = 0; field.ValueChanged += _ => changes++;
        check("the property color button owns its clipboard menu", ReferenceEquals(Field<Button>(field, "button").ContextMenu, commands.Menu));
        check("pasting into the first color control applies once and keeps its picker closed",
            commands.PasteText("#445566") && field.Value == 0x80445566 && changes == 1 && !field.IsPickerOpen);
        check("invalid text at the first color control leaves the model unchanged",
            !commands.PasteText("#1122") && field.Value == 0x80445566 && changes == 1 && !field.IsPickerOpen);
        Click((MenuItem)commands.Menu.Items[3]);
        check("the first color control transparency command applies one exact zero color", field.Value == 0 && changes == 2 && !field.IsPickerOpen);
        Click((MenuItem)commands.Menu.Items[3]);
        check("repeating transparency on the same color does not repeat a model edit", field.Value == 0 && changes == 2);
    }

    private static void PickerCommands(string root, Action<string, bool> check)
    {
        var field = new ColorField(ColorRole.Foreground, new ColorLibrary(Path.Combine(root, "picker.json"))) { Value = 0x80112233 };
        var picker = Field<ColorPickerPanel>(field, "picker");
        using var surface = new ColorPickerTestSurface(picker);
        SetField(field, "pickerSessionOpen", true);
        picker.Value = field.Value;
        Field<TextBox>(picker, "hex").Text = "#40556677";
        var commands = Field<ColorClipboardMenu>(picker, "clipboard");
        int changes = 0, starts = 0, completed = 0, chosen = 0;
        field.ValueChanged += _ => changes++;
        picker.GestureStarted += () => starts++;
        picker.GestureCompleted += commit => { if (commit) completed++; };
        picker.ColorChosen += _ => chosen++;
        check("the second color preview owns a clipboard menu without the first control's transparency action",
            ReferenceEquals(Field<Border>(picker, "preview").ContextMenu, commands.Menu) && commands.Menu.Items.Count == 2);
        var preview = Field<Border>(picker, "preview");
        var hit = picker.InputHitTest(preview.TranslatePoint(new Point(preview.ActualWidth / 2, preview.ActualHeight / 2), picker)) as DependencyObject;
        check("the visible secondary swatch center receives real pointer hit testing for copy and context menu",
            hit is not null && (ReferenceEquals(hit, preview) || preview.IsAncestorOf(hit)));
        check("pasting into the second preview preserves its current draft alpha and updates the owning color once",
            commands.PasteText("rgb(64, 128, 192)") && field.Value == 0x404080C0 &&
            picker.TryGetDraft(out uint draft) && draft == field.Value && changes == 1 && starts == 1 && completed == 1 && !picker.IsInteracting && chosen == 0);
        check("invalid preview paste causes no gesture or draft change", !commands.PasteText("1, 2, 3") &&
            changes == 1 && starts == 1 && completed == 1 && field.Value == 0x404080C0);
        string? copied = null;
        SetField(commands, "writeText", (Action<string>)(value => copied = value));
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        Field<Border>(picker, "preview").RaiseEvent(click);
        check("left-clicking the second preview copies exact standard AARRGGBB without editing color",
            click.Handled && copied == "#404080C0" && field.Value == 0x404080C0 && changes == 1 && starts == 1 && completed == 1);
        var feedback = Field<ColorCopyFeedback>(commands, "feedback");
        check("a successful preview copy shows its anchored receipt", feedback.IsOpen);
        var focused = Keyboard.FocusedElement; var captured = Mouse.Captured;
        commands.Copy(ColorClipboardFormat.HexArgb);
        check("repeated copy keeps the receipt visible without taking focus or capture",
            feedback.IsOpen && ReferenceEquals(focused, Keyboard.FocusedElement) && ReferenceEquals(captured, Mouse.Captured));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.25) };
        var frame = new DispatcherFrame();
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
        check("the copy receipt automatically disappears after its short lifetime", !feedback.IsOpen);
        commands.Copy(ColorClipboardFormat.Rgb);
        preview.Visibility = Visibility.Collapsed;
        check("hiding a color anchor closes its copy receipt immediately", !feedback.IsOpen);
        preview.Visibility = Visibility.Visible;
        commands.Copy(ColorClipboardFormat.Rgb);
        SetField(commands, "writeText", (Action<string>)(_ => throw new ExternalException("busy")));
        commands.Copy(ColorClipboardFormat.Rgb);
        check("a failed repeat copy clears an earlier success receipt", !feedback.IsOpen);
        Field<TextBox>(picker, "hex").Text = "#invalid";
        check("valid preview paste recovers an invalid hex draft and retains the last valid preview alpha",
            commands.PasteText("rgb(10, 20, 30)") && field.Value == 0x400A141E && picker.TryGetDraft(out draft) && draft == field.Value &&
            changes == 2 && starts == 2 && completed == 2 && !picker.IsInteracting);
        field.CanBeginGesture = () => false;
        Field<TextBox>(picker, "hex").Text = "#invalid";
        commands.PasteText("#ABCDEF");
        check("preview paste cannot bypass a rejected owner gesture even when recovering an invalid draft",
            field.Value == 0x400A141E && changes == 2 && starts == 2 && completed == 2 && !picker.IsInteracting);
        field.ClosePicker();
        check("closing after preview paste does not publish the same color twice", changes == 2 && chosen == 0);
    }

    private static void CanvasHistory(string root, Action<string, bool> check)
    {
        var session = new EditorSession();
        session.ExecuteSettings("previous", document => document.Canvas.BackgroundColor = 0x7F112233);
        var field = new ColorField(ColorRole.Background, new ColorLibrary(Path.Combine(root, "canvas.json"))) { Value = 0x7F112233 };
        var edit = new CanvasColorEdit(() => session, () => true, () => { }, () => { });
        field.CanBeginGesture = edit.Begin; field.GestureCompleted += edit.Complete;
        field.ValueChanged += color =>
        {
            if (!edit.Preview(canvas => canvas.BackgroundColor = color))
                session.ExecuteSettings("color", document => document.Canvas.BackgroundColor = color);
        };
        var picker = Field<ColorPickerPanel>(field, "picker");
        using var surface = new ColorPickerTestSurface(picker);
        SetField(field, "pickerSessionOpen", true); picker.Value = field.Value;
        var commands = Field<ColorClipboardMenu>(picker, "clipboard");
        check("canvas preview paste commits one coherent settings history entry", commands.PasteText("#445566") &&
            session.Document.Canvas.BackgroundColor == 0x7F445566 && session.UndoCount == 2 && !session.IsTransactionActive);
        field.ClosePicker();
        check("closing the pasted canvas color does not create another undo entry", session.UndoCount == 2 && !session.IsTransactionActive);
        bool undone = session.Undo(); field.SynchronizeModel(session.Document.Canvas.BackgroundColor); field.ClosePicker();
        check("undo after preview paste restores exact prior alpha and RGB without a stale picker close overwriting it",
            undone && session.Document.Canvas.BackgroundColor == 0x7F112233 && field.Value == 0x7F112233 && session.UndoCount == 1 && session.RedoCount == 1);
        bool redone = session.Redo(); field.SynchronizeModel(session.Document.Canvas.BackgroundColor);
        check("redo restores the exact pasted canvas color", redone && field.Value == 0x7F445566 && session.Document.Canvas.BackgroundColor == field.Value);
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
