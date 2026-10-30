using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Both current-color swatches share the same clipboard commands.</summary>
internal sealed class ColorClipboardMenu
{
    private readonly Func<uint> current;
    private readonly Action<uint> apply;
    private readonly Action<string> reportError;
    private readonly Func<string?> readText;
    private readonly Action<string> writeText;
    private readonly ColorCopyFeedback feedback;
    private readonly MenuItem paste = new() { Header = L.Get("ToolbarV2.Paste") };
    private readonly List<(MenuItem Item, ColorClipboardFormat Format)> copies = [];
    internal OverlayContextMenu Menu { get; } = new();
    internal event Action? Copied;

    internal ColorClipboardMenu(FrameworkElement target, Func<uint> current, Action<uint> apply, Action<string> reportError,
        bool allowTransparent = false, Func<string?>? readText = null, Action<string>? writeText = null)
    {
        this.current = current; this.apply = apply; this.reportError = reportError;
        this.readText = readText ?? (() => Clipboard.GetText()); this.writeText = writeText ?? Clipboard.SetText;
        feedback = new ColorCopyFeedback(target);
        target.ContextMenu = Menu;
        Menu.Items.Add(paste);
        paste.Click += (_, _) => { Menu.IsOpen = false; Paste(); };
        var copy = new MenuItem { Header = L.Get("ToolbarV2.Copy") }; Menu.Items.Add(copy);
        foreach (var format in ColorClipboardText.Formats)
        {
            var item = new MenuItem { Header = format switch
            {
                ColorClipboardFormat.HexRgb => "#RRGGBB", ColorClipboardFormat.HexArgb => "#AARRGGBB",
                ColorClipboardFormat.Rgb => "rgb(R, G, B)", ColorClipboardFormat.Rgba => "rgba(R, G, B, A)",
                ColorClipboardFormat.Hsl => "hsl(H, S%, L%)", _ => "hsla(H, S%, L%, A)"
            } };
            copy.Items.Add(item); copies.Add((item, format));
            item.Click += (_, _) => { Menu.IsOpen = false; Copy(format); };
        }
        if (allowTransparent)
        {
            Menu.Items.Add(new Separator());
            var transparent = new MenuItem { Header = L.Get("Color.SetTransparent") }; Menu.Items.Add(transparent);
            transparent.Click += (_, _) => { Menu.IsOpen = false; apply(0); };
        }
        Menu.Opened += (_, _) => Refresh();
    }

    internal void Refresh()
    {
        uint color = current();
        foreach (var (item, format) in copies) item.ToolTip = ColorClipboardText.Format(color, format);
        try { paste.IsEnabled = ColorClipboardText.TryParse(readText(), color, out _); }
        catch (ExternalException) { paste.IsEnabled = false; }
    }

    internal void Copy(ColorClipboardFormat format)
    {
        try { writeText(ColorClipboardText.Format(current(), format)); }
        catch (ExternalException) { feedback.Close(); reportError(L.Get("Color.CopyFailed")); return; }
        feedback.Show(); Copied?.Invoke();
    }

    internal bool PasteText(string? text)
    {
        if (!ColorClipboardText.TryParse(text, current(), out uint color)) return false;
        apply(color); return true;
    }

    private void Paste()
    {
        try { if (!PasteText(readText())) paste.IsEnabled = false; }
        catch (ExternalException) { reportError(L.Get("Color.PasteFailed")); }
    }
}
