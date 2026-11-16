using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>One owner per physical key press, shared by WPF and both canvas input backends.</summary>
internal sealed class SessionKeyboardRouter : IDisposable
{
    private enum Owner { Local, Editor, Suppressed }
    [ThreadStatic] private static SessionKeyboardRouter? current;
    private static bool registered;
    private readonly SessionKeyboardRouter? previous;
    private readonly Func<bool> active, localText;
    private readonly Func<DependencyObject?, bool> owns;
    private readonly Action<int> command;
    private readonly Action<bool> pan;
    internal Action<OverlayKeyEvent>? CommandInput { get; set; }
    internal Action<int>? KeyReleased { get; set; }
    internal Action? Cancelled { get; set; }
    private void Dispatch(OverlayKeyEvent input) { if (CommandInput is { } dispatch) dispatch(input); else command(input.VirtualKey); }
    private readonly Dictionary<int, Owner> presses = [];
    private bool disposed;
    private IInputElement? pointerText;
    internal static bool IsModal => ComponentDispatcher.IsThreadModal || SessionWindowCoordinator.HasModalInput;

    internal SessionKeyboardRouter(Func<bool> active, Func<DependencyObject?, bool> owns, Func<bool> localText,
        Action<int> command, Action<bool> pan)
    {
        this.active = active; this.owns = owns; this.localText = localText; this.command = command; this.pan = pan;
        previous = current; current = this;
        InputManager.Current.PostProcessInput += ObserveContext;
        ComponentDispatcher.EnterThreadModal += EnterModal;
        if (registered) return;
        registered = true;
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Preview), true);
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.PreviewKeyUpEvent, new KeyEventHandler(Preview), true);
    }

    private static void Preview(object sender, KeyEventArgs e)
    {
        if (current is not { disposed: false } router || e.Handled || sender is not Visual root ||
            !ReferenceEquals(PresentationSource.FromVisual(root)?.RootVisual, root)) return;
        var source = e.OriginalSource as DependencyObject ?? Keyboard.FocusedElement as DependencyObject;
        if (!router.owns(source)) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int code = KeyInterop.VirtualKeyFromKey(key);
        var modifiers = OverlayHost.CurrentModifiers;
        bool down = e.RoutedEvent == Keyboard.PreviewKeyDownEvent;
        if (code == 0x20)
        {
            if (router.Space(down, AppInputPolicy.IsRepeat(e), modifiers, source)) e.Handled = true;
            return;
        }
        if (!down)
        {
            if (router.presses.Remove(code, out var released) && released != Owner.Local) e.Handled = true;
            router.KeyReleased?.Invoke(code);
            return;
        }
        bool local = !router.active() || IsModal || OverlayContextMenu.HasOpenMenu || router.localText() ||
            !EditorShortcutPolicy.ShouldRoute(code, (ModifierKeys)modifiers, source) ||
            // These windows own Escape and their list commands (including rename,
            // group entry and permission checks), after the common policy check.
            code == 0x1B || EditorShortcutPolicy.OwnsList(source) && code != 0x09 ||
            Window.GetWindow(source) is LayersWindow && code != 0x09 ||
            Window.GetWindow(source) is StampLibraryWindow stamps && stamps.OwnsShortcut(key, (ModifierKeys)modifiers, source);
        if (router.presses.TryGetValue(code, out var owner) && AppInputPolicy.IsRepeat(e))
        {
            // Closing a menu while a key is down cannot turn its repeat into an editor command.
            if (owner == Owner.Local) { if (!local) e.Handled = true; return; }
            if (owner == Owner.Suppressed || local) { e.Handled = true; return; }
        }
        router.presses[code] = local ? Owner.Local : Owner.Editor;
        if (local) return;
        e.Handled = true;
        if (!AppInputPolicy.IsRepeat(e) || EditorShortcutPolicy.AllowsRepeat(code, (OverlayModifiers)Keyboard.Modifiers)) router.Dispatch(new(code, modifiers, AppInputPolicy.IsRepeat(e)));
    }

    // The bridge observes Space before dispatch, including native HWNDs which do
    // not raise WPF events. Repeated observation of a down is deliberately idempotent.
    internal bool Space(bool down, bool repeat, OverlayModifiers modifiers, DependencyObject? source, bool canvas = false)
    {
        if (!down)
        {
            bool consumed = presses.Remove(0x20, out var released) && released != Owner.Local;
            pan(false); return consumed;
        }
        if (presses.TryGetValue(0x20, out var owner))
        {
            if (owner == Owner.Suppressed && !repeat) presses.Remove(0x20);
            else
            {
                if (owner == Owner.Editor && (!active() || IsModal || OverlayContextMenu.HasOpenMenu)) Cancel();
                return owner != Owner.Local || !IsModal && !OverlayContextMenu.HasOpenMenu &&
                    EditorShortcutPolicy.ShouldRoute(0x20, (ModifierKeys)modifiers, canvas ? null : source);
            }
        }
        bool editor = !repeat && active() && !IsModal && !OverlayContextMenu.HasOpenMenu && !localText() &&
            (canvas || owns(source)) && EditorShortcutPolicy.ShouldRoute(0x20, (ModifierKeys)modifiers, canvas ? null : source);
        presses[0x20] = editor ? Owner.Editor : Owner.Local;
        pan(editor); return editor;
    }

    internal void CanvasKey(OverlayKeyEvent key)
    {
        if (key.VirtualKey == 0x20) { Space(true, key.IsRepeat, key.Modifiers, null, canvas: true); return; }
        if (!active() || IsModal || OverlayContextMenu.HasOpenMenu) return;
        if (presses.TryGetValue(key.VirtualKey, out var owner) && key.IsRepeat && owner != Owner.Editor) return;
        if (!EditorShortcutPolicy.IsCommand(key.VirtualKey, key.Modifiers) ||
            key.IsRepeat && !EditorShortcutPolicy.AllowsRepeat(key.VirtualKey, key.Modifiers)) return;
        presses[key.VirtualKey] = Owner.Editor;
        Dispatch(key);
    }

    internal void Release(int key)
    {
        presses.Remove(key);
        KeyReleased?.Invoke(key);
        if (key == 0x20) pan(false);
    }

    internal void Cancel()
        => CancelOwnership(notify:true);

    // Controller gesture resets also run at the start of a new stroke. They do
    // not end the lifetime of a held tool key; focus/modal loss still does.
    internal void CancelGesture() => CancelOwnership(notify:false);

    private void CancelOwnership(bool notify)
    {
        pointerText = null;
        foreach (var key in presses.Where(pair => pair.Value == Owner.Editor).Select(pair => pair.Key).ToArray()) presses[key] = Owner.Suppressed;
        pan(false); if(notify) Cancelled?.Invoke();
    }
    private void EnterModal(object? sender, EventArgs e) => Cancel();

    private void ObserveContext(object sender, ProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is MouseButtonEventArgs mouse && active() && !IsModal &&
            mouse.OriginalSource is DependencyObject source && owns(source))
        {
            if (mouse.RoutedEvent == Mouse.MouseDownEvent)
                pointerText = !EditorShortcutPolicy.OwnsLocalEditingKeys(source) && !EditorShortcutPolicy.OwnsMenu(source) &&
                    EditorShortcutPolicy.OwnsLocalEditingKeys(Keyboard.FocusedElement as DependencyObject) ? Keyboard.FocusedElement : null;
            else if (mouse.RoutedEvent == Mouse.MouseUpEvent)
            {
                var old = pointerText; pointerText = null;
                // Nonfocusable swatches and blank panel space also end text input,
                // but only after ButtonBase has completed its capture/click cycle.
                if (old is not null && ReferenceEquals(Keyboard.FocusedElement, old) && !OverlayContextMenu.HasOpenMenu &&
                    !EditorShortcutPolicy.OwnsMenu(source) && Window.GetWindow(source) is { IsVisible: true, IsEnabled: true } window)
                    Keyboard.Focus(window);
            }
        }
        if (presses.Count == 0) return;
        if (!active() || IsModal || OverlayContextMenu.HasOpenMenu ||
            EditorShortcutPolicy.OwnsMenu(Keyboard.FocusedElement as DependencyObject)) Cancel();
    }

    public void Dispose()
    {
        if (disposed) return;
        Cancel(); presses.Clear(); disposed = true;
        InputManager.Current.PostProcessInput -= ObserveContext;
        ComponentDispatcher.EnterThreadModal -= EnterModal;
        if (ReferenceEquals(current, this)) current = previous is { disposed: false } ? previous : null;
    }
}
