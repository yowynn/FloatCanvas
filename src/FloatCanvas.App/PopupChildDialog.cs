using System.Windows;
using System.Windows.Controls.Primitives;

namespace FloatCanvas.App;

/// <summary>Keep the originating popup in its stack while an owned child dialog has focus.</summary>
internal static class PopupChildDialog
{
    private static readonly DependencyProperty OwnerProperty = DependencyProperty.RegisterAttached("Owner", typeof(object), typeof(PopupChildDialog),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));
    private static readonly DependencyProperty RetainedDialogProperty = DependencyProperty.RegisterAttached("RetainedDialog", typeof(bool), typeof(PopupChildDialog),
        new PropertyMetadata(false));

    internal static void Attach(DependencyObject root, object popup) => root.SetValue(OwnerProperty, popup);
    internal static bool HasRetainedDialog(DependencyObject source) => source.GetValue(OwnerProperty) is DependencyObject popup &&
        (bool)popup.GetValue(RetainedDialogProperty);

    internal static IDisposable? RetainFor(DependencyObject source)
    {
        var popup=source.GetValue(OwnerProperty);
        if(popup is null || !PopupHandle.IsOpenFor(popup)) return null;
        var owner=(DependencyObject)popup;
        bool wasRetained=(bool)owner.GetValue(RetainedDialogProperty);
        bool wasStaysOpen=PopupHandle.StaysOpenFor(popup);
        owner.SetValue(RetainedDialogProperty,true);
        PopupHandle.Retain(popup,true);
        return new Retention(()=>
        {
            PopupHandle.Retain(popup,wasStaysOpen);
            owner.SetValue(RetainedDialogProperty,wasRetained);
        });
    }
    private sealed class Retention(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() { var current = action; action = null; current?.Invoke(); }
    }
}
