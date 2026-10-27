using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FloatCanvas.App;

/// <summary>Closing a reusable surface forgets its property editor, without changing live child-dialog focus.</summary>
internal static class WindowFocusLifecycle
{
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(WindowFocusLifecycle), new PropertyMetadata(false));
    private static readonly DependencyProperty ResetPendingProperty = DependencyProperty.RegisterAttached(
        "ResetPending", typeof(bool), typeof(WindowFocusLifecycle), new PropertyMetadata(false));

    internal static void Attach(Window window)
    {
        if ((bool)window.GetValue(AttachedProperty)) return;
        window.SetValue(AttachedProperty, true);
        window.IsVisibleChanged += (_, _) => { if (!window.IsVisible) Reset(window); };
        window.Closed += (_, _) => Reset(window);
        window.SourceInitialized += (_, _) => AttachNativeFocus(window);
        if (new WindowInteropHelper(window).Handle != 0) AttachNativeFocus(window);
    }

    private static void AttachNativeFocus(Window window)
    {
        // EnsureHandle can initialize a hidden tool HWND before its Window has
        // joined the visual source, so resolve the source by its native handle.
        if (HwndSource.FromHwnd(new WindowInteropHelper(window).Handle) is not HwndSource source) return;
        source.AddHook((nint hwnd, int message, nint wparam, nint lparam, ref bool handled) =>
        {
            if (message == 0x0007 && window.IsVisible && (bool)window.GetValue(ResetPendingProperty))
            {
                window.SetValue(ResetPendingProperty, false);
                // Native focus loss remembers an editor independently of logical
                // focus. Acquire focus through WPF first, clearing that native
                // restore cache before its WM_SETFOCUS processing can replay it.
                Keyboard.Focus(window);
            }
            return 0;
        });
    }

    private static void Reset(Window window)
    {
        window.SetValue(ResetPendingProperty, true);
        // Popup children can have their own focus scopes and native presentation
        // roots. Owned Windows are deliberately excluded: a retained modal child
        // may still be editing while its parent surface is temporarily hidden.
        var elements = Descendants(window).ToArray();
        var popupRoots = elements.Select(element => element switch
        {
            Popup popup => popup.Child,
            OverlayPopup popup => popup.Child,
            _ => null
        }).OfType<DependencyObject>().ToArray();
        bool Owns(DependencyObject value) => OverlayPopupWindow.BelongsTo(value, window) ||
            popupRoots.Any(root => OverlayPopupWindow.BelongsTo(value, root));

        if (Keyboard.FocusedElement is DependencyObject keyboard && Owns(keyboard)) Keyboard.ClearFocus();
        foreach (var scope in elements.Where(FocusManager.GetIsFocusScope))
            if (FocusManager.GetFocusedElement(scope) is DependencyObject focused && Owns(focused))
                FocusManager.SetFocusedElement(scope, null);
        // Give WPF a neutral logical destination for the next native activation.
        // This does not focus a hidden window or override an explicit later Focus().
        FocusManager.SetFocusedElement(window, window);
    }

    private static IEnumerable<DependencyObject> Descendants(Window window)
    {
        var visited = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>(); pending.Push(window);
        while (pending.TryPop(out var element))
        {
            if (!visited.Add(element) || element is Window other && !ReferenceEquals(other, window)) continue;
            yield return element;
            if (element is Visual or Visual3D)
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                    pending.Push(VisualTreeHelper.GetChild(element, index));
            foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>()) pending.Push(child);
            if (element is Popup { Child: { } nativeChild }) pending.Push(nativeChild);
            else if (element is OverlayPopup { Child: { } hostedChild }) pending.Push(hostedChild);
        }
    }
}
