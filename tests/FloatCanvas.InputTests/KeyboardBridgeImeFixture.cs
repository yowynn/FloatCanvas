using System.Diagnostics;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

/// <summary>Own interactive target only; no text or composition event is synthesized.</summary>
internal sealed class KeyboardBridgeImeFixture : IDisposable
{
    private readonly HwndSource source;
    private readonly HwndSourceHook hook;
    private readonly uint notifyMessage;
    private readonly TextBlock mode;
    private readonly Button toggle;
    private readonly Button imeToggle;
    private readonly TextBlock imeStatus;
    private ImeCompositionContext? composing;
    private long compositionSequence, textRevision;
    private int pendingSettledCommits;
    private bool disposed, transition;
    public Wpf.Window Window { get; }
    public TextBox Input { get; }
    public nint Handle { get; }
    public string Phase { get; private set; } = "Baseline";
    public int PhaseId { get; private set; } = 1;
    public List<object> Events { get; } = [];
    public Action? Notification { get; set; }
    public Func<Task>? ToggleRequested { get; set; }
    public Action<string>? FinishRequested { get; set; }
    public Action<ImeCompositionObservation>? CompositionObserved { get; set; }
    public Action<ImeSettledCommit>? SettledCommitObserved { get; set; }
    public Action? UiProgress { get; set; }
    public bool HasTextFocus => ReferenceEquals(Keyboard.FocusedElement, Input) && Input.IsKeyboardFocused;

    public KeyboardBridgeImeFixture(uint notifyMessage)
    {
        this.notifyMessage = notifyMessage;
        Input = new TextBox { AcceptsReturn = true, MinHeight = 150, Margin = new Wpf.Thickness(0, 12, 0, 12), FontSize = 22 };
        InputMethod.SetIsInputMethodEnabled(Input, true);
        InputMethod.SetPreferredImeState(Input, InputMethodState.On);
        mode = new TextBlock { FontSize = 18, Text = "阶段 1 · Baseline · 无键盘隔离" };
        imeStatus = new TextBlock { Margin = new Wpf.Thickness(0, 6, 0, 0), FontSize = 15 };
        toggle = new Button { Content = "切换至隔离阶段", Margin = new Wpf.Thickness(0, 0, 12, 0), Padding = new Wpf.Thickness(12, 6, 12, 6) };
        var finish = new Button { Content = "保存报告并退出（F12）", Padding = new Wpf.Thickness(12, 6, 12, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(toggle); buttons.Children.Add(finish);
        imeToggle = new Button { Content = "切换中文／英文状态", Padding = new Wpf.Thickness(12, 6, 12, 6), Margin = new Wpf.Thickness(0, 10, 0, 0), HorizontalAlignment = Wpf.HorizontalAlignment.Left };
        var panel = new StackPanel { Margin = new Wpf.Thickness(20) };
        panel.Children.Add(mode);
        panel.Children.Add(imeStatus);
        panel.Children.Add(new TextBlock { Text = "请使用你当前的输入法输入并提交“你好”。本测试不切换输入方案，也不自动填入中文。\n先确认下方 IME 状态为 On，完成无隔离基线后，再切换隔离并重复同一输入。\n每阶段可额外按 X 检查后台 X 通道。F12 保存退出；Ctrl+Alt+Esc 安全退出；300 秒自动结束。", TextWrapping = Wpf.TextWrapping.Wrap, Margin = new Wpf.Thickness(0, 8, 0, 0) });
        panel.Children.Add(Input); panel.Children.Add(buttons); panel.Children.Add(imeToggle);
        Window = new Wpf.Window { Title = "浮绘 · 自建 IME 基线与键盘桥验证", Content = panel,
            Width = 700, Height = 520, Left = 100, Top = 100, Topmost = true, ShowActivated = false };
        Handle = new WindowInteropHelper(Window).EnsureHandle();
        source = HwndSource.FromHwnd(Handle) ?? throw new InvalidOperationException("The own interactive IME fixture has no HwndSource.");
        hook = OnMessage; source.AddHook(hook);
        TextCompositionManager.AddPreviewTextInputStartHandler(Input, (_, e) => RecordComposition("Start", e));
        TextCompositionManager.AddPreviewTextInputUpdateHandler(Input, (_, e) => RecordComposition("Update", e));
        Input.PreviewTextInput += (_, e) => RecordComposition("Commit", e);
        Input.TextChanged += (_, _) =>
        {
            textRevision++;
            Events.Add(new { Kind = "TextChanged", At = Stopwatch.GetTimestamp(), PhaseId, Phase = EventPhase,
                Text = ReportText(Input.Text), Input.SelectionStart, Input.SelectionLength });
        };
        Window.PreviewKeyDown += (_, e) =>
        {
            UiProgress?.Invoke(); RefreshImeStatus();
            Events.Add(new { Kind = "PreviewKeyDown", At = Stopwatch.GetTimestamp(), PhaseId, Phase = EventPhase, Key = e.Key.ToString(),
                SystemKey = e.SystemKey.ToString(), ImeProcessedKey = e.ImeProcessedKey.ToString(), Modifiers = Keyboard.Modifiers.ToString() });
            if (e.Key == Key.F12) { e.Handled = true; FinishRequested?.Invoke("F12"); }
            else if (e.Key == Key.Escape && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt))
            { e.Handled = true; FinishRequested?.Invoke("CtrlAltEsc"); }
        };
        Window.PreviewKeyUp += (_, _) => UiProgress?.Invoke();
        Window.Closed += (_, _) => FinishRequested?.Invoke("OwnWindowClosed");
        toggle.Click += async (_, _) =>
        {
            if (ToggleRequested is null) return;
            toggle.IsEnabled = false;
            try { await ToggleRequested(); }
            finally { toggle.IsEnabled = true; }
        };
        finish.Click += (_, _) => FinishRequested?.Invoke("SaveButton");
        imeToggle.Click += async (_, _) =>
        {
            imeToggle.IsEnabled = false;
            try
            {
                // Clicking the button can temporarily select its disabled-IME context. Read
                // On/Off only after focus has returned to the actual target TextBox.
                Input.Focus();
                await Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                await RequestImeStateAsync(ReadImeState() == "On" ? InputMethodState.Off : InputMethodState.On);
            }
            catch (Exception ex) { Events.Add(new { Kind = "OwnImeStateRequestFailed", Error = ex.ToString() }); RefreshImeStatus(); }
            finally { imeToggle.IsEnabled = true; }
        };
    }

    public void ActivateText() { Window.Show(); Window.Activate(); Input.Focus(); }
    public void SetPhase(int id, string phase)
    {
        PhaseId = id; Phase = phase;
        mode.Text = phase == "Lease" ? $"阶段 {id} · Lease · 键盘隔离已启用" : $"阶段 {id} · Baseline · 无键盘隔离";
        toggle.Content = phase == "Lease" ? "返回无隔离基线" : "切换至隔离阶段";
        Events.Add(new { Kind = "PhaseMarker", At = Stopwatch.GetTimestamp(), PhaseId, Phase });
        RefreshImeStatus();
    }
    private string EventPhase => transition ? "Transition" : Phase;
    public void SetTransition(bool value)
    {
        transition = value;
        if (value) mode.Text = "正在切换阶段；此间的新组合不归入基线或隔离证据";
        else SetPhase(PhaseId, Phase);
    }
    public void RefreshImeStatus()
    {
        string state = ReadImeState();
        imeStatus.Text = $"当前 IME：{state}　语言：{InputLanguageManager.Current.CurrentInputLanguage.Name}　文本框焦点：{HasTextFocus}";
        imeToggle.Content = state == "On" ? "切换至英文状态（仅本测试文本框）" : "开启中文状态（仅本测试文本框）";
    }
    public async Task RequestImeStateAsync(InputMethodState state)
    {
        if (!Window.IsActive) throw new InvalidOperationException("The own IME window must be active before changing its local IME state.");
        Input.Focus(); InputMethod.SetPreferredImeState(Input, state);
        await Window.Dispatcher.InvokeAsync(() =>
        {
            if (!Window.IsActive || !HasTextFocus) throw new InvalidOperationException("The own IME TextBox lost focus before its local state request.");
            InputMethod.Current.ImeState = state;
            Events.Add(new { Kind = "OwnImeStateRequested", At = Stopwatch.GetTimestamp(), PhaseId, Phase = EventPhase, Requested = state.ToString(), Actual = ReadImeState() });
            RefreshImeStatus();
        }, DispatcherPriority.Input);
        await Window.Dispatcher.InvokeAsync(RefreshImeStatus, DispatcherPriority.ContextIdle);
    }
    public async Task WaitForSettledCommitsAsync()
    {
        long started = Stopwatch.GetTimestamp();
        while (pendingSettledCommits != 0)
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 2) throw new TimeoutException("The own TextBox's queued commit observation did not settle.");
            await Task.Delay(1);
        }
    }
    public void SetLanguageStatus(string message) => mode.ToolTip = message;
    public void SetLeaseEnded(string reason)
    {
        mode.Text = $"阶段 {PhaseId} · Lease · 隔离已结束：{reason}";
        toggle.Content = "重新开启隔离阶段";
        SetLanguageStatus("此阶段后续输入不能作为活动隔离证据。");
    }
    public object Snapshot() => new { PhaseId, Phase, Text = ReportText(Input.Text), HasTextFocus, WindowVisible = Window.IsVisible,
        WindowHandle = Handle.ToInt64(), CurrentInputLanguage = InputLanguageManager.Current.CurrentInputLanguage.Name,
        AttachedInputLanguage = InputLanguageManager.GetInputLanguage(Input)?.Name, ImeEnabled = InputMethod.GetIsInputMethodEnabled(Input),
        PreferredImeState = InputMethod.GetPreferredImeState(Input).ToString(), ImeState = ReadImeState() };
    public static string ReadImeState()
    {
        try { return InputMethod.Current.ImeState.ToString(); }
        catch (Exception ex) { return "Unavailable: " + ex.GetType().Name; }
    }
    public static string ReportText(string value) => value.All(c => "nNiIhHaAoOkKxX你好 \t\r\n'".Contains(c))
        ? value : $"<outside self-test alphabet, length={value.Length}>";
    private void RecordComposition(string kind, TextCompositionEventArgs e)
    {
        UiProgress?.Invoke();
        if (kind == "Start") composing = new ImeCompositionContext(++compositionSequence, Input.Text, Stopwatch.GetTimestamp());
        var context = composing;
        long at = Stopwatch.GetTimestamp();
        string text = kind == "Commit" ? e.Text : e.TextComposition.CompositionText;
        Events.Add(new { Kind = "Composition" + kind, At = at, PhaseId, Phase = EventPhase, CompositionId = context?.Id,
            Text = ReportText(e.Text), CompositionText = ReportText(e.TextComposition.CompositionText),
            SystemText = ReportText(e.TextComposition.SystemText), ControlText = ReportText(e.TextComposition.ControlText) });
        CompositionObserved?.Invoke(new ImeCompositionObservation(context?.Id ?? 0, kind, text, at));
        if (kind == "Commit" && context is not null)
        {
            composing = null;
            ScheduleSettledCommit(context, at);
        }
    }
    private void ScheduleSettledCommit(ImeCompositionContext context, long commitAt)
    {
        pendingSettledCommits++;
        void Finish(string? reason, string text)
        {
            pendingSettledCommits--;
            string addedHan = reason is null ? NewlyAddedHan(context.BeforeText, text) : "";
            Events.Add(new { Kind = "SettledCompositionCommit", CompositionId = context.Id, context.StartedAt, CommitAt = commitAt,
                SettledAt = Stopwatch.GetTimestamp(), Reason = reason, Before = ReportText(context.BeforeText), After = ReportText(text), AddedHan = ReportText(addedHan),
                Method = "Two ContextIdle observations after actual PreviewTextInput, no active/new composition, unchanged TextBox revision and value; added Han compared with the actual composition-start document." });
            SettledCommitObserved?.Invoke(new ImeSettledCommit(context.Id, commitAt, reason, addedHan));
        }
        Window.Dispatcher.BeginInvoke(() =>
        {
            if (disposed || composing is not null || compositionSequence != context.Id) { Finish("DisposedOrNewCompositionBeforeFirstObservation", ""); return; }
            string candidate = Input.Text; long revision = textRevision;
            Window.Dispatcher.BeginInvoke(() =>
            {
                if (disposed || composing is not null || compositionSequence != context.Id) { Finish("DisposedOrNewCompositionBeforeStableObservation", ""); return; }
                if (revision != textRevision || candidate != Input.Text) { Finish("DocumentChangedBetweenSettledObservations", Input.Text); return; }
                Finish(null, Input.Text);
            }, DispatcherPriority.ContextIdle);
        }, DispatcherPriority.ContextIdle);
    }
    private static string NewlyAddedHan(string before, string after)
    {
        var counts = before.Where(IsHan).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        var added = new System.Text.StringBuilder();
        foreach (char c in after.Where(IsHan))
            if (counts.TryGetValue(c, out int remaining) && remaining > 0) counts[c] = remaining - 1;
            else added.Append(c);
        return added.ToString();
    }
    private static bool IsHan(char c) => c is >= '\u3400' and <= '\u9FFF';
    private nint OnMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == notifyMessage) { Notification?.Invoke(); handled = true; return 0; }
        if (message is 0x10D or 0x10E or 0x10F or 0x281 or 0x282 or 0x286 or 0x288)
            Events.Add(new { Kind = "ImeWindowMessage", At = Stopwatch.GetTimestamp(), PhaseId, Phase, Message = message });
        return 0;
    }
    public void Dispose() { disposed = true; Notification = null; FinishRequested = null; UiProgress = null; source.RemoveHook(hook); Window.Close(); }
    private sealed record ImeCompositionContext(long Id, string BeforeText, long StartedAt);
}

internal sealed record ImeCompositionObservation(long Id, string Kind, string Text, long At);
internal sealed record ImeSettledCommit(long Id, long CommitAt, string? RejectedReason, string AddedHan);
