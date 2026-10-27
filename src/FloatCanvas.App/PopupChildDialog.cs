using System.Windows;
using System.Windows.Controls.Primitives;

namespace FloatCanvas.App;

/// <summary>Keep the originating popup in its stack while an owned child dialog has focus.</summary>
internal static class PopupChildDialog
{
    private static readonly DependencyProperty OwnerProperty = DependencyProperty.RegisterAttached("Owner", typeof(object), typeof(PopupChildDialog),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));
    private static readonly DependencyProperty RetainedDialogProperty = DependencyProperty.RegisterAttached("RetainedDialog", typeof(RetainedChildren), typeof(PopupChildDialog),
        new PropertyMetadata(null));

    internal static void Attach(DependencyObject root, object popup) => root.SetValue(OwnerProperty, popup);
    internal static bool HasRetainedDialog(DependencyObject source) => source.GetValue(OwnerProperty) is DependencyObject popup &&
        popup.GetValue(RetainedDialogProperty) is RetainedChildren { Count: > 0 };

    internal static bool StaysOpenForOutsidePress(DependencyObject popup, bool current) =>
        popup.GetValue(RetainedDialogProperty) is RetainedChildren { ModalCount: 0 } children ? children.OriginalStaysOpen : current;

    internal static IDisposable? RetainFor(DependencyObject source, bool transientMenu = false)
    {
        var popup=source.GetValue(OwnerProperty);
        if(popup is null || !PopupHandle.IsOpenFor(popup)) return null;
        var owner=(DependencyObject)popup;
        var children = owner.GetValue(RetainedDialogProperty) as RetainedChildren;
        if (children is null) { children = new(PopupHandle.StaysOpenFor(popup)); owner.SetValue(RetainedDialogProperty, children); }
        children.Count++;
        if(!transientMenu)children.ModalCount++;
        PopupHandle.Retain(popup,true);
        return new Retention(()=>
        {
            if(!transientMenu)children.ModalCount--;
            if (--children.Count != 0) return;
            PopupHandle.Retain(popup,children.OriginalStaysOpen);
            owner.ClearValue(RetainedDialogProperty);
        });
    }
    private sealed class RetainedChildren(bool originalStaysOpen)
    {
        internal bool OriginalStaysOpen { get; } = originalStaysOpen;
        internal int Count;
        internal int ModalCount;
    }
    private sealed class Retention(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() { var current = action; action = null; current?.Invoke(); }
    }
}
