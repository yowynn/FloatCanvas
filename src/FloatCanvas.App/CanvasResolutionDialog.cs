using System.Windows;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal enum CanvasResolutionChoice { Preserve, Convert, Cancel }

internal static class CanvasResolutionDialog
{
    internal static (Window Window, Func<CanvasResolutionChoice> Result) Create(Window owner, CanvasAuthoringReference reference, RectD target)
    {
        var screen = reference.ReferenceScreenBounds;
        var dialog = AppDialog.Create(owner, L.Format("Canvas.ResolutionChoice", screen.Width, screen.Height, target.Width, target.Height),
            L.Get("Canvas.ResolutionTitle"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
            "Canvas.PreserveResolution", "Canvas.ConvertResolution");
        return (dialog.Window, () => dialog.Result() switch
        {
            MessageBoxResult.Yes => CanvasResolutionChoice.Preserve,
            MessageBoxResult.No => CanvasResolutionChoice.Convert,
            _ => CanvasResolutionChoice.Cancel
        });
    }
}
