using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

/// <summary>Read-only listeners on the unmodified production dialog/control.</summary>
internal sealed class ProductionImeObservation : IDisposable
{
    private readonly Func<bool> leaseActive;
    private readonly KeyEventHandler keyDown, keyUp;
    private readonly TextCompositionEventHandler start, update, commit;
    private readonly TextChangedEventHandler textChanged;
    private readonly EventHandler closed;
    private bool composing, disposed;
    public Wpf.Window Window { get; }
    public TextBox Input { get; }
    public nint Handle { get; }
    public bool IsClosed { get; private set; }
    public bool CtrlEnterSeen { get; private set; }
    public bool EscapeSeen { get; private set; }
    public bool EscapeRetainedDialog { get; private set; }
    public bool XPreeditObserved { get; private set; }
    public int CompositionStarts { get; private set; }
    public int CompositionUpdates { get; private set; }
    public int CompositionCommits { get; private set; }
    public int ShiftEvents { get; private set; }
    public bool HasTextFocus => ReferenceEquals(Keyboard.FocusedElement, Input) && Input.IsKeyboardFocused;
    public List<object> Events { get; } = [];

    public ProductionImeObservation(Wpf.Window window, TextBox input, Func<bool> leaseActive)
    {
        Window = window; Input = input; this.leaseActive = leaseActive;
        Handle = new WindowInteropHelper(window).Handle;
        start = (_, e) => RecordComposition("Start", e);
        update = (_, e) => RecordComposition("Update", e);
        commit = (_, e) => RecordComposition("Commit", e);
        keyDown = (_, e) => RecordKey("PreviewKeyDown", e);
        keyUp = (_, e) => RecordKey("PreviewKeyUp", e);
        textChanged = (_, _) => Events.Add(new { Kind = "TextChanged", At = Stopwatch.GetTimestamp(),
            Text = KeyboardBridgeImeFixture.ReportText(Input.Text), Input.SelectionStart, Input.SelectionLength, LeaseActive = leaseActive() });
        closed = (_, _) => { IsClosed = true; Events.Add(new { Kind = "ActualProductionDialogClosed", At = Stopwatch.GetTimestamp(),
            CtrlEnterObservedBeforeClosedEvent = CtrlEnterSeen, EscapeSeen,
            EventOrdering = "The production Window handler can close before this later handled-events-too observer sees the same Ctrl+Enter." }); };
        TextCompositionManager.AddPreviewTextInputStartHandler(Input, start);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(Input, update);
        Input.PreviewTextInput += commit; Input.TextChanged += textChanged;
        // Observe already-handled Ctrl+Enter too, without changing the production handler.
        Window.AddHandler(Keyboard.PreviewKeyDownEvent, keyDown, handledEventsToo: true);
        Window.AddHandler(Keyboard.PreviewKeyUpEvent, keyUp, handledEventsToo: true);
        Window.Closed += closed;
    }
    private void RecordComposition(string kind, TextCompositionEventArgs e)
    {
        if (kind == "Start") { composing = true; CompositionStarts++; }
        else if (kind == "Update") CompositionUpdates++;
        else { composing = false; CompositionCommits++; }
        XPreeditObserved |= composing && e.TextComposition.CompositionText.Contains('x', StringComparison.OrdinalIgnoreCase);
        Events.Add(new { Kind = "Composition" + kind, At = Stopwatch.GetTimestamp(),
            Text = KeyboardBridgeImeFixture.ReportText(e.Text), CompositionText = KeyboardBridgeImeFixture.ReportText(e.TextComposition.CompositionText),
            DocumentText = KeyboardBridgeImeFixture.ReportText(Input.Text), LeaseActive = leaseActive(), ImeState = KeyboardBridgeImeFixture.ReadImeState() });
    }
    private void RecordKey(string kind, KeyEventArgs e)
    {
        Key effective = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key == Key.System ? e.SystemKey : e.Key;
        if (effective is Key.LeftShift or Key.RightShift) ShiftEvents++;
        bool down = kind == "PreviewKeyDown";
        if (down && effective == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) CtrlEnterSeen = true;
        Events.Add(new { Kind = kind, At = Stopwatch.GetTimestamp(), Key = e.Key.ToString(), ImeProcessedKey = e.ImeProcessedKey.ToString(),
            SystemKey = e.SystemKey.ToString(), Modifiers = Keyboard.Modifiers.ToString(), e.Handled, Composing = composing,
            LeaseActive = leaseActive(), ImeState = KeyboardBridgeImeFixture.ReadImeState() });
        if (!down || effective != Key.Escape) return;
        EscapeSeen = true;
        Window.Dispatcher.BeginInvoke(() =>
        {
            if (disposed) return;
            bool retained = !IsClosed && Window.IsVisible;
            EscapeRetainedDialog |= retained;
            Events.Add(new { Kind = "AfterActualEscape", At = Stopwatch.GetTimestamp(), DialogRetained = retained,
                Text = KeyboardBridgeImeFixture.ReportText(Input.Text), LeaseActive = leaseActive() });
        }, DispatcherPriority.ContextIdle);
    }
    public object Snapshot() => new { WindowHandle = Handle.ToInt64(), IsClosed, WindowVisible = Window.IsVisible, HasTextFocus,
        ImeState = KeyboardBridgeImeFixture.ReadImeState(), InputLanguage = InputLanguageManager.Current.CurrentInputLanguage.Name,
        ImeEnabled = InputMethod.GetIsInputMethodEnabled(Input), Text = KeyboardBridgeImeFixture.ReportText(Input.Text),
        CtrlEnterSeen, EscapeSeen, EscapeRetainedDialog, XPreeditObserved, CompositionStarts, CompositionUpdates, CompositionCommits, ShiftEvents,
        Events = Events.ToArray() };
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        TextCompositionManager.RemovePreviewTextInputStartHandler(Input, start);
        TextCompositionManager.RemovePreviewTextInputUpdateHandler(Input, update);
        Input.PreviewTextInput -= commit; Input.TextChanged -= textChanged;
        Window.RemoveHandler(Keyboard.PreviewKeyDownEvent, keyDown); Window.RemoveHandler(Keyboard.PreviewKeyUpEvent, keyUp);
        Window.Closed -= closed;
    }
}
