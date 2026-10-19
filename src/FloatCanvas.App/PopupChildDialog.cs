using System.Windows;
using System.Windows.Controls.Primitives;

namespace FloatCanvas.App;

/// <summary>Keep the originating popup in its stack while an owned child dialog has focus.</summary>
internal static class PopupChildDialog
{
    private static readonly DependencyProperty OwnerProperty = DependencyProperty.RegisterAttached("Owner", typeof(Popup), typeof(PopupChildDialog),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    internal static void Attach(DependencyObject root, Popup popup) => root.SetValue(OwnerProperty, popup);

    internal static IDisposable? RetainFor(DependencyObject source)
    {
        if (source.GetValue(OwnerProperty) is not Popup { IsOpen: true } popup) return null;
        bool wasStaysOpen = popup.StaysOpen;
        popup.StaysOpen = true;
        return new Retention(() => popup.StaysOpen = wasStaysOpen);
    }
    private sealed class Retention(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() { var current = action; action = null; current?.Invoke(); }
    }
}
